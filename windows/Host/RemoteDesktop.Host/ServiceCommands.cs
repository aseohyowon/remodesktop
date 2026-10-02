using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using RemoteDesktop.Core;
using RemoteDesktop.Host.Engine;

namespace RemoteDesktop.Host;

/// <summary>
/// STEP 13: Windows 서비스 모드
///
///   [서비스 RemoteDesktopHost, 세션 0, SYSTEM]
///        │ 콘솔 세션 감시, 에이전트 실행/재시작, Ctrl+Alt+Del(SendSAS)
///        ▼
///   [에이전트, 사용자 세션, SYSTEM]  = RemoteDesktop.Host.exe agent ...
///        캡처·입력·네트워크 (입력 데스크톱을 따라가 로그인/잠금/UAC 화면도 처리)
///
/// 설정은 C:\ProgramData\RemoteDesktop (SYSTEM과 Administrators만 접근)에 저장합니다.
/// </summary>
internal static class ServiceCommands
{
    public const string ServiceName = "RemoteDesktopHost";

    /// <summary>service run: 서비스 관리자(SCM)가 실행</summary>
    public static int Run(string[] hostArgs)
    {
        HostOptions.Parse(hostArgs); // 잘못된 옵션이면 바로 알림
        if (Environment.UserInteractive)
        {
            Console.Error.WriteLine("'service run'은 Windows 서비스 관리자가 실행합니다. 설치는 scripts\\install-service.ps1, 테스트는 'service console'을 사용하세요.");
            return 1;
        }

        ServiceBase.Run(new RemoteDesktopService(hostArgs));
        return 0;
    }

    /// <summary>service console: 서비스 없이 같은 구조를 현재 사용자 권한으로 실행 (개발/테스트용)</summary>
    public static async Task<int> ConsoleAsync(string[] hostArgs)
    {
        HostOptions.Parse(hostArgs);
        AppPaths.UseServiceData();
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        Log.Info($"Service console mode (data: {AppPaths.DataDirectory}). 로그인/UAC 화면과 Ctrl+Alt+Del은 서비스로 설치해야 동작합니다.");
        await RunSupervisorAsync(hostArgs, asService: false, onSupervisor: null, shutdown.Token);
        return 0;
    }

    /// <summary>서비스 본체: 에이전트 감시 + Ctrl+Alt+Del 파이프</summary>
    public static async Task RunSupervisorAsync(string[] hostArgs, bool asService, Action<AgentSupervisor>? onSupervisor, CancellationToken cancellationToken)
    {
        string pipeName = $"RemoteDesktopHost-sas-{Guid.NewGuid():N}";
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 경로를 알 수 없습니다.");
        string[] agentArgs = ["agent", "--pipe", pipeName, .. hostArgs];

        using var launcher = new SessionAgentLauncher(executable, agentArgs, asSystemInSession: asService);
        uint mySession = (uint)Process.GetCurrentProcess().SessionId;
        var supervisor = new AgentSupervisor(launcher, asService ? SessionAgentLauncher.ActiveConsoleSessionId : () => mySession);
        onSupervisor?.Invoke(supervisor);

        var sas = new SasPipeServer(pipeName, () => supervisor.AgentProcessId,
            asService ? SecureAttentionSequence.Send : () => "콘솔 테스트 모드에서는 Ctrl+Alt+Del을 보낼 수 없습니다. 서비스로 설치하세요.");

        await Task.WhenAll(supervisor.RunAsync(cancellationToken), sas.RunAsync(cancellationToken));
    }

    /// <summary>agent --pipe 이름 [Host 옵션]: 서비스가 사용자 세션에 띄우는 실제 Host</summary>
    public static async Task<int> AgentAsync(string[] args)
    {
        if (args is not ["--pipe", var pipeName, .. var hostArgs])
        {
            Console.Error.WriteLine("agent는 서비스가 실행합니다.");
            return 1;
        }

        AppPaths.UseServiceData();
        Log.SetFile(AppPaths.GetFile("agent.log"));

        HostOptions options = HostOptions.Parse(hostArgs) with
        {
            FollowInputDesktop = true,
            AccessCodeEnabled = false // 화면 앞에 사람이 없을 수 있으므로 비밀번호/신뢰된 장치로만
        };

        if (!hostArgs.Contains("--shared-folder"))
        {
            // SYSTEM의 사용자 폴더 대신 공용 문서 폴더 (C:\Users\Public\Documents\RemoteDesktop)
            options = options with
            {
                SharedFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "RemoteDesktop")
            };
        }

        Log.DebugEnabled = options.Verbose;
        using var shutdown = new CancellationTokenSource();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

        try
        {
            var server = new HostServer(options, HostSettings.Load(), new AgentCallbacks())
            {
                SecureAttention = cancellationToken => SasPipeClient.RequestAsync(pipeName, cancellationToken),
                PowerController = new WindowsPowerController(sessionAgent: true)
            };
            Log.Info($"Agent started: session {Process.GetCurrentProcess().SessionId}, user {Environment.UserName}, Host ID {server.Settings.HostId}");
            await server.RunAsync(shutdown.Token);
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Log.Error($"Agent 실행 실패: {exception.Message}");
            return 1;
        }
    }

    /// <summary>service password|devices|totp|log …: 서비스용 설정(C:\ProgramData\RemoteDesktop)을 관리 (관리자 권한 필요)</summary>
    public static int Settings(string[] args, Func<string[], int> command)
    {
        AppPaths.UseServiceData();
        try
        {
            SecureDataDirectory();
            return command(args);
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("서비스 설정은 관리자 권한으로 실행한 PowerShell/명령 프롬프트에서만 바꿀 수 있습니다.");
            return 1;
        }
    }

    /// <summary>
    /// 서비스 데이터 폴더를 SYSTEM과 Administrators만 접근할 수 있게 합니다 (상속 끊기).
    /// 비밀번호 해시, 신뢰된 장치 키, TLS 개인 키가 PC 범위 DPAPI로 저장되므로 폴더 권한이 보호선입니다.
    /// REMODESK_DATA_DIR로 다른 폴더를 쓰는 경우(테스트)는 건드리지 않습니다.
    /// </summary>
    public static void SecureDataDirectory()
    {
        string path = AppPaths.DataDirectory;
        if (!string.Equals(Path.GetFullPath(path).TrimEnd('\\'), Path.GetFullPath(AppPaths.ServiceDataDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = new DirectoryInfo(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (WellKnownSidType sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        directory.SetAccessControl(security);
    }

    /// <summary>서비스 모드에는 승인 창을 띄울 콘솔이 없으므로 승인 요청은 거부합니다.</summary>
    private sealed class AgentCallbacks : NullHostCallbacks
    {
        public override Task<bool> ApproveAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Log.Warn($"서비스 모드에서는 접속 승인을 받을 수 없어 거부합니다: {request.ClientName}");
            return Task.FromResult(false);
        }

        public override void OnSessionStarted(SessionInfo session) =>
            Log.Info($"Session started: {session.ClientName} ({session.Platform}) via {session.Transport}");

        public override void OnSessionEnded(SessionInfo session, string reason) =>
            Log.Info($"Session ended: {session.ClientName} - {reason}");
    }
}

/// <summary>Windows 서비스 RemoteDesktopHost</summary>
internal sealed class RemoteDesktopService : ServiceBase
{
    private readonly string[] _hostArgs;
    private CancellationTokenSource? _stop;
    private Task? _run;
    private AgentSupervisor? _supervisor;

    public RemoteDesktopService(string[] hostArgs)
    {
        _hostArgs = hostArgs;
        ServiceName = ServiceCommands.ServiceName;
        CanHandleSessionChangeEvent = true;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        AppPaths.UseServiceData();
        ServiceCommands.SecureDataDirectory();
        Log.SetFile(AppPaths.GetFile("service.log"));
        Log.Info("Service starting");

        _stop = new CancellationTokenSource();
        _run = Task.Run(async () =>
        {
            try
            {
                await ServiceCommands.RunSupervisorAsync(_hostArgs, asService: true, s => _supervisor = s, _stop.Token);
            }
            catch (Exception exception)
            {
                Log.Error($"Service failed: {exception}");
                Stop();
            }
        });
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        Log.Info($"Session change: {changeDescription.Reason} (session {changeDescription.SessionId})");
        _supervisor?.Notify();
    }

    protected override void OnStop() => Shutdown();

    protected override void OnShutdown() => Shutdown();

    private void Shutdown()
    {
        _stop?.Cancel();
        _run?.Wait(TimeSpan.FromSeconds(10));
        Log.Info("Service stopped");
    }
}
