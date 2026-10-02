using System.Drawing;
using System.Runtime.InteropServices;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Host.Engine;

/// <summary>해상도 변경 (테스트에서 실제 화면을 바꾸지 않도록 분리)</summary>
public interface IDisplayController
{
    /// <summary>모니터 장치 이름(예: \\.\DISPLAY1)의 현재 해상도와 위치</summary>
    Rectangle Current(string deviceName);

    DisplayMode[] Modes(string deviceName);

    /// <summary>해상도를 바꿉니다(재부팅하면 원래대로). 실패하면 오류 문구</summary>
    string? Apply(string deviceName, DisplayMode mode);

    /// <summary>레지스트리에 저장된 원래 해상도로 되돌립니다.</summary>
    void Restore(string deviceName);
}

/// <summary>
/// Windows 해상도 변경 (STEP 12)
/// - ChangeDisplaySettingsEx(flags 0): 지금만 바꾸고 레지스트리에는 저장하지 않으므로 재부팅하거나 Restore하면 원래대로 돌아옵니다.
/// - 바꾸기 전에 CDS_TEST로 그 해상도를 쓸 수 있는지 먼저 확인합니다.
/// </summary>
public sealed class WindowsDisplayController : IDisplayController
{
    private const int EnumCurrentSettings = -1;
    private const int DmPelsWidth = 0x80000;
    private const int DmPelsHeight = 0x100000;
    private const int DmDisplayFrequency = 0x400000;
    private const uint CdsTest = 0x2;
    private const int DispChangeSuccessful = 0;

    public Rectangle Current(string deviceName)
    {
        DevMode mode = NewDevMode();
        if (!EnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode))
        {
            throw new InvalidOperationException($"{deviceName}의 현재 해상도를 읽을 수 없습니다.");
        }

        return new Rectangle(mode.dmPositionX, mode.dmPositionY, mode.dmPelsWidth, mode.dmPelsHeight);
    }

    public DisplayMode[] Modes(string deviceName)
    {
        var modes = new HashSet<(int, int)>();
        DevMode mode = NewDevMode();
        for (int i = 0; EnumDisplaySettings(deviceName, i, ref mode); i++)
        {
            if (mode.dmBitsPerPel >= 32 && mode.dmPelsWidth >= 800 && mode.dmPelsHeight >= 600)
            {
                modes.Add((mode.dmPelsWidth, mode.dmPelsHeight));
            }
        }

        return modes
            .OrderByDescending(m => m.Item1 * m.Item2)
            .ThenByDescending(m => m.Item1)
            .Select(m => new DisplayMode(m.Item1, m.Item2))
            .ToArray();
    }

    /// <summary>실제로 바꾸지 않고 가능한지 확인만 합니다 (CDS_TEST).</summary>
    public string? Validate(string deviceName, DisplayMode mode) => Change(deviceName, mode, CdsTest);

    public string? Apply(string deviceName, DisplayMode mode)
    {
        string? error = Change(deviceName, mode, CdsTest);
        return error ?? Change(deviceName, mode, 0);
    }

    public void Restore(string deviceName)
    {
        int result = ChangeDisplaySettingsEx(deviceName, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (result != DispChangeSuccessful)
        {
            Log.Warn($"원래 해상도로 되돌리지 못했습니다 (코드 {result}).");
        }
    }

    private static string? Change(string deviceName, DisplayMode target, uint flags)
    {
        DevMode current = NewDevMode();
        EnumDisplaySettings(deviceName, EnumCurrentSettings, ref current);

        // 같은 해상도에서 지금 주사율을 쓸 수 있으면 유지, 아니면 가장 높은 주사율
        int frequency = 0;
        DevMode mode = NewDevMode();
        for (int i = 0; EnumDisplaySettings(deviceName, i, ref mode); i++)
        {
            if (mode.dmPelsWidth == target.Width && mode.dmPelsHeight == target.Height && mode.dmBitsPerPel >= 32)
            {
                if (mode.dmDisplayFrequency == current.dmDisplayFrequency)
                {
                    frequency = mode.dmDisplayFrequency;
                    break;
                }

                frequency = Math.Max(frequency, mode.dmDisplayFrequency);
            }
        }

        if (frequency == 0)
        {
            return $"{target.Width}x{target.Height}는 이 모니터에서 지원하지 않는 해상도입니다.";
        }

        DevMode request = current;
        request.dmPelsWidth = target.Width;
        request.dmPelsHeight = target.Height;
        request.dmDisplayFrequency = frequency;
        request.dmFields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency;

        IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<DevMode>());
        try
        {
            Marshal.StructureToPtr(request, buffer, false);
            int result = ChangeDisplaySettingsEx(deviceName, buffer, IntPtr.Zero, flags, IntPtr.Zero);
            return result == DispChangeSuccessful ? null : $"해상도를 바꿀 수 없습니다 (코드 {result}).";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static DevMode NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DevMode>() };

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DevMode devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, IntPtr devMode, IntPtr hwnd, uint flags, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
}

/// <summary>
/// 원격 연결 중에는 Host PC가 절전 모드로 들어가거나 화면이 꺼지지 않게 합니다 (STEP 12).
/// SetThreadExecutionState는 호출한 스레드에 묶이므로 전용 스레드에서 설정하고, 세션이 끝나면 해제합니다.
/// </summary>
public sealed class SleepBlocker : IDisposable
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();

    public SleepBlocker()
    {
        _thread = new Thread(() =>
        {
            Active = SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired) != 0;
            _started.Set();
            _stop.Wait();
            SetThreadExecutionState(EsContinuous);
        })
        {
            IsBackground = true,
            Name = "sleep-blocker"
        };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(2));
    }

    public bool Active { get; private set; }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}

/// <summary>세션 중 해상도 변경 관리: 바꾼 모니터는 세션이 끝날 때 원래대로 되돌립니다.</summary>
internal sealed class DisplaySession : IDisposable
{
    private readonly IDisplayController _controller;
    private readonly HashSet<string> _changed = new();

    public DisplaySession(IDisplayController controller)
    {
        _controller = controller;
    }

    public DisplayModesMessage Describe(string deviceName, Rectangle originalBounds)
    {
        Rectangle current = _controller.Current(deviceName);
        return new DisplayModesMessage(
            new DisplayMode(current.Width, current.Height),
            new DisplayMode(originalBounds.Width, originalBounds.Height),
            _controller.Modes(deviceName));
    }

    /// <summary>해상도 변경. 성공하면 바뀐 모니터 영역</summary>
    public (Rectangle? Bounds, string? Error) Change(string deviceName, DisplayMode mode)
    {
        if (mode.Width == 0 && mode.Height == 0)
        {
            _controller.Restore(deviceName);
            _changed.Remove(deviceName);
            return (_controller.Current(deviceName), null);
        }

        if (!_controller.Modes(deviceName).Contains(mode))
        {
            return (null, $"{mode.Width}x{mode.Height}는 이 모니터에서 쓸 수 없는 해상도입니다.");
        }

        string? error = _controller.Apply(deviceName, mode);
        if (error is not null)
        {
            return (null, error);
        }

        _changed.Add(deviceName);
        return (_controller.Current(deviceName), null);
    }

    public void Dispose()
    {
        foreach (string device in _changed)
        {
            _controller.Restore(device);
            Log.Info($"Display {device} restored to original resolution");
        }

        _changed.Clear();
    }
}
