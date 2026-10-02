using System.Drawing;
using System.Runtime.InteropServices;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// Client가 보낸 마우스/키보드 메시지를 Windows SendInput으로 실행합니다.
///
/// 제한 사항 (Windows 보안 정책):
/// - Host를 일반 권한으로 실행하면 관리자 권한 창(작업 관리자 등)은 조작할 수 없습니다(UIPI).
/// - 잠금 화면, 로그인 화면, UAC 확인 창(보안 데스크톱)은 일반 앱이 조작할 수 없습니다.
/// - Ctrl+Alt+Del은 키 입력으로 만들 수 없습니다. SYSTEM 서비스의 SendSAS가 필요합니다.
///
/// 보안: 비밀번호가 입력될 수 있으므로 키 이름이나 문자열은 절대 로그에 남기지 않습니다.
/// </summary>
internal sealed class InputInjector
{
    private const int MaxTextLength = 256;
    private const int MaxWheelDelta = 120 * 20;

    private Rectangle _bounds;
    private readonly Dictionary<ushort, bool> _keysDown = new(); // VK → extended
    private readonly HashSet<MouseButton> _buttonsDown = new();
    private bool _failureLogged;

    public InputInjector(Rectangle bounds)
    {
        _bounds = bounds;
    }

    public long EventCount { get; private set; }

    /// <summary>보고 있는 모니터가 바뀌면 0~1 좌표의 기준 영역도 바꿉니다.</summary>
    public void SetBounds(Rectangle bounds) => _bounds = bounds;

    public static bool IsInputMessage(ControlMessage message) =>
        message is MouseMoveMessage or MouseButtonMessage or MouseWheelMessage or KeyDownMessage or KeyUpMessage or TextInputMessage;

    /// <summary>입력 메시지면 처리하고 true, 입력 메시지가 아니면 false.</summary>
    public bool TryHandle(ControlMessage message)
    {
        switch (message)
        {
            case MouseMoveMessage move:
                MoveTo(move.X, move.Y);
                break;
            case MouseButtonMessage button:
                SetButton(button.Button, button.Action == ButtonAction.Down);
                break;
            case MouseWheelMessage wheel:
                Wheel(wheel.DeltaX, wheel.DeltaY);
                break;
            case KeyDownMessage keyDown:
                SetKey(keyDown.Code, down: true);
                break;
            case KeyUpMessage keyUp:
                SetKey(keyUp.Code, down: false);
                break;
            case TextInputMessage text:
                TypeText(text.Text);
                break;
            default:
                return false;
        }

        EventCount++;
        return true;
    }

    /// <summary>연결이 끊길 때 눌린 채로 남은 키/버튼을 모두 뗍니다. (Ctrl이 계속 눌려 있는 사고 방지)</summary>
    public void ReleaseAll()
    {
        foreach ((ushort virtualKey, bool extended) in _keysDown.ToArray())
        {
            SendKey(virtualKey, extended, down: false);
        }

        foreach (MouseButton button in _buttonsDown.ToArray())
        {
            SetButton(button, down: false);
        }

        _keysDown.Clear();
        _buttonsDown.Clear();
    }

    private void MoveTo(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ProtocolException("마우스 좌표가 숫자가 아닙니다.");
        }

        // 0~1 정규화 좌표 → 캡처 중인 모니터의 실제 픽셀
        int pixelX = _bounds.Left + Math.Clamp((int)(Math.Clamp(x, 0, 1) * _bounds.Width), 0, _bounds.Width - 1);
        int pixelY = _bounds.Top + Math.Clamp((int)(Math.Clamp(y, 0, 1) * _bounds.Height), 0, _bounds.Height - 1);

        // SendInput 절대 좌표는 전체 가상 화면(모든 모니터)을 0~65535로 나타냅니다.
        int virtualLeft = GetSystemMetrics(SmXVirtualScreen);
        int virtualTop = GetSystemMetrics(SmYVirtualScreen);
        int virtualWidth = Math.Max(GetSystemMetrics(SmCxVirtualScreen), 1);
        int virtualHeight = Math.Max(GetSystemMetrics(SmCyVirtualScreen), 1);

        int absoluteX = (int)(((long)(pixelX - virtualLeft) * 65536 + virtualWidth - 1) / virtualWidth);
        int absoluteY = (int)(((long)(pixelY - virtualTop) * 65536 + virtualHeight - 1) / virtualHeight);

        SendMouse(MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk, absoluteX, absoluteY, 0);
    }

    private void SetButton(MouseButton button, bool down)
    {
        (uint downFlag, uint upFlag, uint data) = button switch
        {
            MouseButton.Left => (MouseEventLeftDown, MouseEventLeftUp, 0u),
            MouseButton.Right => (MouseEventRightDown, MouseEventRightUp, 0u),
            MouseButton.Middle => (MouseEventMiddleDown, MouseEventMiddleUp, 0u),
            MouseButton.X1 => (MouseEventXDown, MouseEventXUp, XButton1),
            MouseButton.X2 => (MouseEventXDown, MouseEventXUp, XButton2),
            _ => throw new ProtocolException("알 수 없는 마우스 버튼입니다.")
        };

        SendMouse(down ? downFlag : upFlag, 0, 0, data);

        if (down)
        {
            _buttonsDown.Add(button);
        }
        else
        {
            _buttonsDown.Remove(button);
        }
    }

    private void Wheel(int deltaX, int deltaY)
    {
        deltaX = Math.Clamp(deltaX, -MaxWheelDelta, MaxWheelDelta);
        deltaY = Math.Clamp(deltaY, -MaxWheelDelta, MaxWheelDelta);

        if (deltaY != 0)
        {
            SendMouse(MouseEventWheel, 0, 0, unchecked((uint)deltaY));
        }

        if (deltaX != 0)
        {
            SendMouse(MouseEventHorizontalWheel, 0, 0, unchecked((uint)deltaX));
        }
    }

    private void SetKey(string code, bool down)
    {
        if (!KeyCodes.TryGetVirtualKey(code, out ushort virtualKey, out bool extended))
        {
            Log.Debug("Ignored unknown key code");
            return;
        }

        SendKey(virtualKey, extended, down);

        if (down)
        {
            _keysDown[virtualKey] = extended;
        }
        else
        {
            _keysDown.Remove(virtualKey);
        }
    }

    private void TypeText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (text.Length > MaxTextLength)
        {
            throw new ProtocolException($"text_input은 최대 {MaxTextLength}자입니다.");
        }

        // KEYEVENTF_UNICODE: 키보드 배열/IME와 관계없이 문자 그대로 입력합니다. (한글, 이모지 포함)
        var inputs = new List<Input>(text.Length * 2);
        foreach (char character in text)
        {
            if (char.IsControl(character) && character is not '\n' and not '\t')
            {
                continue;
            }

            ushort unit = character == '\n' ? (ushort)'\r' : character;
            inputs.Add(KeyboardInput(0, unit, KeyEventUnicode));
            inputs.Add(KeyboardInput(0, unit, KeyEventUnicode | KeyEventKeyUp));
        }

        Send(inputs.ToArray());
    }

    private void SendKey(ushort virtualKey, bool extended, bool down)
    {
        ushort scanCode = (ushort)MapVirtualKey(virtualKey, MapVkToVsc);
        uint flags = (extended ? KeyEventExtendedKey : 0) | (down ? 0 : KeyEventKeyUp);
        Send([KeyboardInput(virtualKey, scanCode, flags)]);
    }

    private void SendMouse(uint flags, int x, int y, uint data)
    {
        Send([new Input
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInputData { Dx = x, Dy = y, MouseData = data, Flags = flags }
            }
        }]);
    }

    private static Input KeyboardInput(ushort virtualKey, ushort scanCode, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInputData { VirtualKey = virtualKey, ScanCode = scanCode, Flags = flags }
        }
    };

    private void Send(Input[] inputs)
    {
        if (inputs.Length == 0)
        {
            return;
        }

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length)
        {
            _failureLogged = false;
            return;
        }

        if (!_failureLogged)
        {
            // 보통 관리자 권한 창이 앞에 있거나 잠금/UAC 화면일 때입니다.
            Log.Warn($"입력 전달 실패 (Win32 오류 {Marshal.GetLastWin32Error()}). 관리자 권한 창, 잠금 화면, UAC 창은 조작할 수 없습니다.");
            _failureLogged = true;
        }
    }

    // ---- Win32 ----

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventXDown = 0x0080;
    private const uint MouseEventXUp = 0x0100;
    private const uint MouseEventWheel = 0x0800;
    private const uint MouseEventHorizontalWheel = 0x1000;
    private const uint MouseEventVirtualDesk = 0x4000;
    private const uint MouseEventAbsolute = 0x8000;
    private const uint XButton1 = 0x0001;
    private const uint XButton2 = 0x0002;

    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const uint MapVkToVsc = 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInputData Mouse;

        [FieldOffset(0)]
        public KeyboardInputData Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}
