using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using RemoteDesktop.Core;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// 입력 데스크톱을 따라가는 전용 스레드 (STEP 13).
///
/// Windows 화면은 "데스크톱" 단위로 나뉩니다.
///   Default  : 평소 바탕 화면
///   Winlogon : 로그인 화면, 잠금 화면, Ctrl+Alt+Del 화면, UAC 확인 창(보안 데스크톱)
/// 캡처(GDI/Desktop Duplication)와 SendInput은 "호출한 스레드가 붙어 있는 데스크톱"에만 동작하므로,
/// 지금 사용자가 보고 있는 데스크톱(입력 데스크톱)이 바뀌면 스레드도 SetThreadDesktop으로 옮겨야 합니다.
/// SetThreadDesktop은 창이나 훅이 없는 스레드에서만 성공하므로 이 일만 하는 스레드를 따로 둡니다.
///
/// Winlogon 데스크톱은 SYSTEM 권한(서비스가 띄운 에이전트)만 열 수 있습니다.
/// 일반 권한이면 OpenInputDesktop이 실패하고, 지금 데스크톱에 그대로 머뭅니다(기존 동작과 같음).
/// </summary>
public sealed class DesktopThread : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(100);

    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private readonly bool _follow;
    private readonly Stopwatch _sinceCheck = new();
    private IntPtr _openedDesktop;
    private volatile string _desktopName = "";
    private int _generation;

    /// <param name="followInputDesktop">false면 데스크톱을 바꾸지 않고 전용 스레드에서 실행만 합니다.</param>
    public DesktopThread(string name, bool followInputDesktop = true)
    {
        _follow = followInputDesktop;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>스레드가 지금 붙어 있는 데스크톱 이름 (Default, Winlogon 등)</summary>
    public string DesktopName => _desktopName;

    /// <summary>데스크톱이 바뀔 때마다 1씩 늘어납니다. 캡처 장치를 다시 만들 때 씁니다.</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>입력 데스크톱으로 옮긴 뒤 전용 스레드에서 실행합니다. 넣은 순서대로 실행됩니다.</summary>
    public Task<T> InvokeAsync<T>(Func<T> function)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _work.Add(() =>
            {
                try
                {
                    SyncToInputDesktop();
                    completion.SetResult(function());
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            });
        }
        catch (InvalidOperationException)
        {
            completion.SetException(new ObjectDisposedException(nameof(DesktopThread)));
        }

        return completion.Task;
    }

    public Task InvokeAsync(Action action) => InvokeAsync(() =>
    {
        action();
        return true;
    });

    private void Run()
    {
        _desktopName = NameOf(GetThreadDesktop(GetCurrentThreadId())) ?? "";
        foreach (Action item in _work.GetConsumingEnumerable())
        {
            item();
        }

        if (_openedDesktop != IntPtr.Zero)
        {
            CloseDesktop(_openedDesktop);
        }
    }

    /// <summary>입력 데스크톱이 바뀌었으면 이 스레드를 그쪽으로 옮깁니다 (100ms에 한 번만 확인).</summary>
    private void SyncToInputDesktop()
    {
        if (!_follow || (_sinceCheck.IsRunning && _sinceCheck.Elapsed < CheckInterval))
        {
            return;
        }

        _sinceCheck.Restart();
        IntPtr input = OpenInputDesktop(0, false, GenericAll);
        if (input == IntPtr.Zero)
        {
            return; // 권한 없음 (일반 사용자 + 보안 데스크톱): 지금 데스크톱에 머무름
        }

        string? name = NameOf(input);
        if (name is null || name == _desktopName)
        {
            CloseDesktop(input);
            return;
        }

        if (!SetThreadDesktop(input))
        {
            Log.Warn($"데스크톱 전환 실패 ({name}): Win32 오류 {Marshal.GetLastWin32Error()}");
            CloseDesktop(input);
            return;
        }

        if (_openedDesktop != IntPtr.Zero)
        {
            CloseDesktop(_openedDesktop);
        }

        _openedDesktop = input;
        Log.Info($"Input desktop: {_desktopName} -> {name}");
        _desktopName = name;
        Interlocked.Increment(ref _generation);
    }

    private static string? NameOf(IntPtr desktop)
    {
        var buffer = new byte[256];
        return GetUserObjectInformation(desktop, UoiName, buffer, buffer.Length, out int needed) && needed >= 2
            ? Encoding.Unicode.GetString(buffer, 0, needed - 2)
            : null;
    }

    public void Dispose()
    {
        _work.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private const uint GenericAll = 0x10000000;
    private const int UoiName = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetUserObjectInformationW")]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, byte[] info, int length, out int needed);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
