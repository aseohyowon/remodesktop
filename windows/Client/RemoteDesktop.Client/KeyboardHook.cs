using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemoteDesktop.Client;

/// <summary>
/// 저수준 키보드 훅(WH_KEYBOARD_LL).
/// 일반 KeyDown 이벤트로는 Windows 키, Alt+Tab, Alt+F4 등이 내 PC에서 먼저 처리되어 버립니다.
/// 원격 창이 활성화된 동안만 훅을 설치해 이런 키를 가로채 원격 PC로 보냅니다.
/// Ctrl+Alt+Del은 Windows가 보호하므로 가로챌 수 없습니다.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    /// <summary>true를 반환하면 내 PC에는 전달하지 않습니다.</summary>
    public delegate bool KeyHandler(int virtualKey, bool extended, bool up);

    private const int WhKeyboardLowLevel = 13;
    private const int LowLevelExtended = 0x01;
    private const int LowLevelInjected = 0x10;
    private const int LowLevelUp = 0x80;

    private readonly KeyHandler _handler;
    private readonly HookProc _callback; // GC가 대리자를 수거하지 않도록 필드로 보관
    private IntPtr _hook;

    public KeyboardHook(KeyHandler handler)
    {
        _handler = handler;
        _callback = Callback;
        _hook = SetWindowsHookEx(WhKeyboardLowLevel, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private IntPtr Callback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KeyboardLowLevelData>(data);

            // 프로그램이 만든 입력(같은 PC에서 Host가 넣은 입력 등)은 다시 보내지 않습니다.
            if ((info.Flags & LowLevelInjected) == 0
                && _handler(info.VirtualKey, (info.Flags & LowLevelExtended) != 0, (info.Flags & LowLevelUp) != 0))
            {
                return 1;
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardLowLevelData
    {
        public int VirtualKey;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
