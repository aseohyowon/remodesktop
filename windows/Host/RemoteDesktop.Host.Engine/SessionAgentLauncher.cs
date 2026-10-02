using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// 에이전트 프로세스를 띄웁니다 (STEP 13).
/// - 서비스 모드(asSystemInSession): 서비스의 SYSTEM 토큰을 복제해 세션 번호만 바꾼 뒤 CreateProcessAsUser.
///   → 사용자의 세션에서 SYSTEM 권한으로 실행되어 로그인/잠금/UAC 화면(Winlogon 데스크톱)에 접근할 수 있습니다.
/// - 콘솔 테스트 모드: 현재 사용자로 같은 세션에 실행 (권한 상승 없음)
/// 두 경우 모두 Job Object에 넣어 서비스가 끝나면(비정상 종료 포함) 에이전트도 함께 끝나게 합니다.
/// </summary>
public sealed class SessionAgentLauncher : IAgentLauncher, IDisposable
{
    private readonly string _executable;
    private readonly IReadOnlyList<string> _arguments;
    private readonly bool _asSystemInSession;
    private readonly IntPtr _job;

    public SessionAgentLauncher(string executable, IReadOnlyList<string> arguments, bool asSystemInSession)
    {
        _executable = executable;
        _arguments = arguments;
        _asSystemInSession = asSystemInSession;
        _job = CreateKillOnCloseJob();
    }

    public IAgentProcess Launch(uint sessionId)
    {
        Process process = _asSystemInSession ? LaunchAsSystem(sessionId) : LaunchAsCurrentUser();
        return new AgentProcess(process, sessionId);
    }

    private Process LaunchAsCurrentUser()
    {
        var start = new ProcessStartInfo(_executable) { UseShellExecute = false };
        foreach (string argument in _arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process = Process.Start(start) ?? throw new InvalidOperationException("에이전트를 시작하지 못했습니다.");
        AssignProcessToJobObject(_job, process.Handle);
        return process;
    }

    private Process LaunchAsSystem(uint sessionId)
    {
        IntPtr processToken = IntPtr.Zero;
        IntPtr token = IntPtr.Zero;
        var info = default(ProcessInformation);
        try
        {
            Check(OpenProcessToken(Process.GetCurrentProcess().Handle,
                TokenAssignPrimary | TokenDuplicate | TokenQuery | TokenAdjustDefault | TokenAdjustSessionId, out processToken), "OpenProcessToken");
            Check(DuplicateTokenEx(processToken, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out token), "DuplicateTokenEx");
            Check(SetTokenInformation(token, TokenSessionIdClass, ref sessionId, sizeof(uint)), "SetTokenInformation(TokenSessionId)");

            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
            var commandLine = new StringBuilder(BuildCommandLine(_executable, _arguments));
            Check(CreateProcessAsUser(token, _executable, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment, IntPtr.Zero,
                Path.GetDirectoryName(_executable), ref startup, out info), "CreateProcessAsUser");

            Process process = Process.GetProcessById(info.ProcessId);
            AssignProcessToJobObject(_job, info.Process);
            ResumeThread(info.Thread);
            return process;
        }
        finally
        {
            Close(info.Thread);
            Close(info.Process);
            Close(token);
            Close(processToken);
        }
    }

    /// <summary>Windows 명령줄 규칙(CommandLineToArgvW)에 맞게 인자를 따옴표로 묶습니다.</summary>
    public static string BuildCommandLine(string executable, IEnumerable<string> arguments)
    {
        var builder = new StringBuilder();
        builder.Append('"').Append(executable).Append('"');
        foreach (string argument in arguments)
        {
            builder.Append(' ');
            if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
            {
                builder.Append(argument);
                continue;
            }

            builder.Append('"');
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                builder.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
                backslashes = 0;
                builder.Append(c);
            }

            builder.Append('\\', backslashes * 2).Append('"');
        }

        return builder.ToString();
    }

    public void Dispose() => Close(_job); // KILL_ON_JOB_CLOSE: 에이전트도 끝남

    private static IntPtr CreateKillOnCloseJob()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject");
        }

        var limits = new JobExtendedLimitInformation();
        limits.Basic.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, Marshal.SizeOf<JobExtendedLimitInformation>()))
        {
            int error = Marshal.GetLastWin32Error();
            CloseHandle(job);
            throw new Win32Exception(error, "SetInformationJobObject");
        }

        return job;
    }

    private static void Check(bool ok, string what)
    {
        if (!ok)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"{what} 실패 (서비스가 LocalSystem으로 실행 중인지 확인)");
        }
    }

    private static void Close(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
        }
    }

    /// <summary>지금 화면 앞(콘솔)에 있는 세션 번호. 없으면 0xFFFFFFFF</summary>
    public static uint ActiveConsoleSessionId() => WTSGetActiveConsoleSessionId();

    private sealed class AgentProcess(Process process, uint sessionId) : IAgentProcess
    {
        public int Id => process.Id;

        public uint SessionId => sessionId;

        public bool HasExited => process.HasExited;

        public void Kill() => process.Kill();

        public void Dispose() => process.Dispose();
    }

    // ---- Win32 ----

    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint TokenAdjustSessionId = 0x0100;
    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenSessionIdClass = 12;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr token, int informationClass, ref uint information, int length);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr token, string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref JobExtendedLimitInformation information, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}
