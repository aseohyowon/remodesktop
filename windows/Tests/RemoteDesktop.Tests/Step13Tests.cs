using System.ComponentModel;
using RemoteDesktop.Client;
using RemoteDesktop.Host.Engine;
using RemoteDesktop.Protocol;
using Xunit.Abstractions;

namespace RemoteDesktop.Tests;

/// <summary>STEP 13: Windows 서비스 + 세션 에이전트 (권한 상승 없이 시험할 수 있는 부분)</summary>
public class Step13Tests(ITestOutputHelper output)
{
    // ---------------- 입력 데스크톱 스레드 ----------------

    [Fact]
    public async Task DesktopThreadRunsWorkInOrderOnOneThread()
    {
        using var desktop = new DesktopThread("test-desktop");
        var threads = new HashSet<int>();
        var order = new List<int>();
        var tasks = Enumerable.Range(0, 50).Select(i => desktop.InvokeAsync(() =>
        {
            threads.Add(Environment.CurrentManagedThreadId);
            order.Add(i);
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.Single(threads);
        Assert.Equal(Enumerable.Range(0, 50), order);
        Assert.Equal(42, await desktop.InvokeAsync(() => 42));
    }

    [Fact]
    public async Task DesktopThreadPropagatesExceptionsAndFollowsInputDesktop()
    {
        using var desktop = new DesktopThread("test-desktop");
        await Assert.ThrowsAsync<Win32Exception>(() => desktop.InvokeAsync<int>(() => throw new Win32Exception("blocked")));

        // 일반 권한에서는 입력 데스크톱 = 평소 바탕 화면(Default). 잠금 상태면 Winlogon을 열 수 없어 그대로 머무름
        await desktop.InvokeAsync(() => { });
        output.WriteLine($"desktop: {desktop.DesktopName}, generation {desktop.Generation}");
        Assert.Equal("Default", desktop.DesktopName);
    }

    [Fact]
    public async Task DisposedDesktopThreadRejectsWork()
    {
        var desktop = new DesktopThread("test-desktop");
        desktop.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => desktop.InvokeAsync(() => 1));
    }

    // ---------------- 에이전트 감시 ----------------

    private sealed class FakeAgent(int id, uint session) : IAgentProcess
    {
        public int Id => id;

        public uint SessionId => session;

        public bool HasExited { get; set; }

        public bool Killed { get; private set; }

        public void Kill()
        {
            Killed = true;
            HasExited = true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeLauncher : IAgentLauncher
    {
        public List<FakeAgent> Started { get; } = new();

        public bool Fail { get; set; }

        public IAgentProcess Launch(uint sessionId)
        {
            if (Fail)
            {
                throw new Win32Exception(5, "access denied");
            }

            var agent = new FakeAgent(1000 + Started.Count, sessionId);
            Started.Add(agent);
            return agent;
        }
    }

    [Fact]
    public void SupervisorKeepsOneAgentInConsoleSession()
    {
        var launcher = new FakeLauncher();
        uint session = 1;
        DateTime now = new(2026, 1, 1);
        var supervisor = new AgentSupervisor(launcher, () => session, () => now);

        supervisor.Check();
        supervisor.Check();
        Assert.Single(launcher.Started);
        Assert.Equal(1000, supervisor.AgentProcessId);
        Assert.Equal(1u, launcher.Started[0].SessionId);

        // 사용자 전환/로그아웃: 콘솔 세션이 2로 바뀜 → 옛 에이전트 종료, 새 세션에 실행
        session = 2;
        supervisor.Check();
        Assert.True(launcher.Started[0].Killed);
        Assert.Equal(2, launcher.Started.Count);
        Assert.Equal(2u, launcher.Started[1].SessionId);

        // 콘솔 세션이 없음(전환 중) → 에이전트 종료 후 기다림
        session = AgentSupervisor.NoSession;
        supervisor.Check();
        Assert.True(launcher.Started[1].Killed);
        Assert.Null(supervisor.AgentProcessId);
        supervisor.Check();
        Assert.Equal(2, launcher.Started.Count);

        supervisor.Stop();
    }

    [Fact]
    public void SupervisorRestartsCrashedAgentWithBackoff()
    {
        var launcher = new FakeLauncher();
        DateTime now = new(2026, 1, 1);
        var supervisor = new AgentSupervisor(launcher, () => 1, () => now);

        supervisor.Check();
        launcher.Started[^1].HasExited = true;  // 바로 죽음
        supervisor.Check();                     // 1초 뒤로 예약
        Assert.Single(launcher.Started);
        now = now.AddSeconds(1);
        supervisor.Check();
        Assert.Equal(2, launcher.Started.Count);

        launcher.Started[^1].HasExited = true;  // 또 죽음 → 2초
        supervisor.Check();
        now = now.AddSeconds(1);
        supervisor.Check();
        Assert.Equal(2, launcher.Started.Count);
        now = now.AddSeconds(1);
        supervisor.Check();
        Assert.Equal(3, launcher.Started.Count);

        // 1분 넘게 잘 돌다가 끝나면 대기 시간이 처음(1초)으로
        now = now.AddMinutes(5);
        launcher.Started[^1].HasExited = true;
        supervisor.Check();
        now = now.AddSeconds(1);
        supervisor.Check();
        Assert.Equal(4, launcher.Started.Count);

        Assert.Equal(TimeSpan.FromSeconds(30), AgentSupervisor.Backoff(20));
    }

    [Fact]
    public void SupervisorRetriesWhenLaunchFails()
    {
        var launcher = new FakeLauncher { Fail = true };
        DateTime now = new(2026, 1, 1);
        var supervisor = new AgentSupervisor(launcher, () => 3, () => now);
        supervisor.Check();
        Assert.Empty(launcher.Started);

        launcher.Fail = false;
        supervisor.Check(); // 아직 대기 시간
        Assert.Empty(launcher.Started);
        now = now.AddSeconds(1);
        supervisor.Check();
        Assert.Single(launcher.Started);
    }

    [Fact]
    public void SessionZeroIsNeverUsed()
    {
        var launcher = new FakeLauncher();
        var supervisor = new AgentSupervisor(launcher, () => 0);
        supervisor.Check();
        Assert.Empty(launcher.Started);
    }

    [Theory]
    [InlineData(new[] { "agent", "--pipe", "p1" }, "\"C:\\a b\\x.exe\" agent --pipe p1")]
    [InlineData(new[] { "--shared-folder", "C:\\My Files\\" }, "\"C:\\a b\\x.exe\" --shared-folder \"C:\\My Files\\\\\"")]
    [InlineData(new[] { "say \"hi\"", "" }, "\"C:\\a b\\x.exe\" \"say \\\"hi\\\"\" \"\"")]
    public void CommandLineQuoting(string[] args, string expected) =>
        Assert.Equal(expected, SessionAgentLauncher.BuildCommandLine("C:\\a b\\x.exe", args));

    // ---------------- 실제 에이전트 프로세스 실행 (현재 사용자, Job Object) ----------------

    [Fact]
    public void LauncherStartsProcessAndJobKillsItOnDispose()
    {
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        IAgentProcess agent;
        using (var launcher = new SessionAgentLauncher(cmd, ["/c", "ping -n 30 127.0.0.1 >nul"], asSystemInSession: false))
        {
            agent = launcher.Launch(1);
            Assert.False(agent.HasExited);
        }

        // Job을 닫으면(서비스 종료) 에이전트도 끝남
        for (int i = 0; i < 50 && !agent.HasExited; i++)
        {
            Thread.Sleep(100);
        }

        Assert.True(agent.HasExited);
        agent.Dispose();
    }

    [Fact]
    public void LaunchAsSystemFailsClearlyWithoutPrivilege()
    {
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using var launcher = new SessionAgentLauncher(cmd, ["/c", "exit"], asSystemInSession: true);
        var error = Assert.Throws<Win32Exception>(() => launcher.Launch(1));
        output.WriteLine(error.Message);
    }

    // ---------------- Ctrl+Alt+Del 파이프 ----------------

    [Fact]
    public async Task SasPipeAcceptsOnlyTheAgentProcess()
    {
        string pipe = $"rd-test-sas-{Guid.NewGuid():N}";
        int? allowed = Environment.ProcessId;
        int sent = 0;
        var server = new SasPipeServer(pipe, () => allowed, () =>
        {
            sent++;
            return null;
        });
        using var stop = new CancellationTokenSource();
        Task run = server.RunAsync(stop.Token);

        Assert.Null(await SasPipeClient.RequestAsync(pipe, CancellationToken.None));
        Assert.Equal(1, sent);

        // 2초 안에 다시 요청하면 거부
        Assert.NotNull(await SasPipeClient.RequestAsync(pipe, CancellationToken.None));
        Assert.Equal(1, sent);

        // 에이전트가 아닌 프로세스(여기서는 허용 ID를 바꿔서 흉내)
        allowed = 12345;
        string? error = await SasPipeClient.RequestAsync(pipe, CancellationToken.None);
        Assert.Contains("허용되지 않은", error);
        Assert.Equal(1, sent);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void SasPolicyCheckDoesNotThrow() =>
        output.WriteLine($"SoftwareSASGeneration allows services: {SecureAttentionSequence.PolicyAllowsServices()}");

    // ---------------- 세션 통합: 데스크톱 스레드 + Ctrl+Alt+Del 기능 ----------------

    [Fact]
    public async Task AgentModeSessionStreamsAndForwardsSas()
    {
        int sasRequests = 0;
        await using var host = new TestHost(new HostOptions { Codec = "jpeg", FollowInputDesktop = true }, viewOnly: false);
        host.Server.SecureAttention = _ =>
        {
            sasRequests++;
            return Task.FromResult<string?>(null);
        };

        using var connection = await host.ConnectAsync(new LoginRequest(AuthMethods.AccessCode, host.Server.AccessCode, false));
        Assert.Contains(HostFeatures.SecureAttention, connection.Features);

        int frames = 0;
        connection.FrameReceived += (bitmap, header) =>
        {
            Interlocked.Increment(ref frames);
            bitmap.Dispose();
            _ = connection.SendAckAsync(header.FrameId);
        };
        using var features = new ClientFeatures(connection);
        connection.StartReceiving();

        for (int i = 0; i < 50 && Volatile.Read(ref frames) == 0; i++)
        {
            await Task.Delay(100);
        }

        Assert.True(frames > 0, "데스크톱 스레드에서 캡처한 프레임이 와야 함");

        SasResultMessage result = await features.SendSasAsync(CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.Equal(1, sasRequests);
    }

    /// <summary>
    /// 실제 'service console' → 에이전트 프로세스에 연결 (환경 변수가 있을 때만):
    ///   REMODESK_E2E_PORT=50515, REMODESK_E2E_PASSWORD=비밀번호
    /// </summary>
    [Fact]
    public async Task ExternalServiceConsoleAgent()
    {
        if (Environment.GetEnvironmentVariable("REMODESK_E2E_PORT") is not { } port
            || Environment.GetEnvironmentVariable("REMODESK_E2E_PASSWORD") is not { } password)
        {
            output.WriteLine("REMODESK_E2E_PORT / REMODESK_E2E_PASSWORD 미설정 - 건너뜀");
            return;
        }

        string folder = Directory.CreateTempSubdirectory("rd-e2e-").FullName;
        KnownHostsStore.PathOverride = Path.Combine(folder, "known.json");
        ClientTransport transport = await RemoteHostConnection.ConnectLanAsync("127.0.0.1", int.Parse(port), CancellationToken.None);
        using var connection = await RemoteHostConnection.AuthenticateAsync(transport, new LoginRequest(AuthMethods.Password, password, false),
            new TestPrompts(), new DeviceCredentialStore(Path.Combine(folder, "devices.json")), CancellationToken.None);
        output.WriteLine($"features: {string.Join(",", connection.Features)}");
        Assert.Contains(HostFeatures.SecureAttention, connection.Features);

        int frames = 0;
        connection.FrameReceived += (bitmap, header) =>
        {
            Interlocked.Increment(ref frames);
            output.WriteLine($"frame {bitmap.Width}x{bitmap.Height}");
            bitmap.Dispose();
            _ = connection.SendAckAsync(header.FrameId);
        };
        using var features = new ClientFeatures(connection);
        connection.StartReceiving();
        for (int i = 0; i < 100 && Volatile.Read(ref frames) == 0; i++)
        {
            await Task.Delay(100);
        }

        Assert.True(frames > 0);

        // 에이전트 → 파이프 → 서비스(콘솔 모드)까지 왕복. 콘솔 모드라 SendSAS는 하지 않고 안내 문구가 와야 함
        SasResultMessage result = await features.SendSasAsync(CancellationToken.None);
        output.WriteLine($"sas: {result.Success} {result.Error}");
        Assert.False(result.Success);
        Assert.Contains("콘솔 테스트 모드", result.Error);
    }

    [Fact]
    public async Task NormalHostDoesNotOfferSas()
    {
        await using var host = new TestHost(new HostOptions { Codec = "jpeg" }, viewOnly: false);
        using var connection = await host.ConnectAsync(new LoginRequest(AuthMethods.AccessCode, host.Server.AccessCode, false));
        Assert.DoesNotContain(HostFeatures.SecureAttention, connection.Features);

        using var features = new ClientFeatures(connection);
        connection.StartReceiving();
        SasResultMessage result = await features.SendSasAsync(CancellationToken.None);
        Assert.False(result.Success);
    }
}
