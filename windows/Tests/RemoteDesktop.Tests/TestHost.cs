using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteDesktop.Client;
using RemoteDesktop.Host.Engine;

namespace RemoteDesktop.Tests;

/// <summary>
/// 테스트용 Host: 임시 폴더의 설정 파일, 임시 인증서, 빈 포트를 사용합니다.
/// 실제 사용자 설정(%LOCALAPPDATA%)과 Windows 인증서 저장소는 건드리지 않습니다.
/// 화면 캡처는 실제로 하지만 입력은 받지 않습니다(ViewOnly).
/// </summary>
internal sealed class TestHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;

    static TestHost()
    {
        KnownHostsStore.PathOverride = Path.Combine(Path.GetTempPath(), $"rd-test-known-{Environment.ProcessId}.json");
        AccessLog.PathOverride = Path.Combine(Path.GetTempPath(), $"rd-test-access-{Environment.ProcessId}.jsonl");
    }

    public TestHost(HostOptions? options = null, IHostCallbacks? callbacks = null, Action<HostSettings>? configure = null, string? hostId = null, bool viewOnly = true)
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("rd-test-").FullName;
        if (hostId is not null)
        {
            File.WriteAllText(Path.Combine(Directory, "host.json"), $"{{\"host_id\": \"{hostId}\"}}");
        }

        Settings = HostSettings.Load(Path.Combine(Directory, "host.json"));
        configure?.Invoke(Settings);

        Port = GetFreePort();
        Options = (options ?? new HostOptions()) with { Port = Port, ViewOnly = viewOnly };
        Server = new HostServer(Options, Settings, callbacks, CreateCertificate());
        _run = Task.Run(() => Server.RunAsync(_stop.Token));
    }

    public string Directory { get; }

    public HostSettings Settings { get; }

    public HostOptions Options { get; }

    public HostServer Server { get; }

    public int Port { get; }

    public DeviceCredentialStore NewDeviceStore() => new(Path.Combine(Directory, $"devices-{Guid.NewGuid():N}.json"));

    public async Task<RemoteHostConnection> ConnectAsync(LoginRequest login, IConnectPrompts? prompts = null, DeviceCredentialStore? devices = null)
    {
        await WaitUntilListeningAsync();
        ClientTransport transport = await RemoteHostConnection.ConnectLanAsync("127.0.0.1", Port, CancellationToken.None);
        return await RemoteHostConnection.AuthenticateAsync(transport, login, prompts ?? new TestPrompts(), devices ?? NewDeviceStore(), CancellationToken.None);
    }

    public async Task<RemoteHostConnection> ConnectInternetAsync(Uri signaling, LoginRequest login, DeviceCredentialStore? devices = null)
    {
        ClientTransport transport = await RemoteHostConnection.ConnectInternetAsync(signaling, Settings.HostId, CancellationToken.None);
        return await RemoteHostConnection.AuthenticateAsync(transport, login, new TestPrompts(), devices ?? NewDeviceStore(), CancellationToken.None);
    }

    private async Task WaitUntilListeningAsync()
    {
        for (int i = 0; i < 50; i++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, Port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(50);
            }
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static X509Certificate2 CreateCertificate()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=RemoteDesktop Test Host", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        return new X509Certificate2(created.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }

        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch
        {
        }
    }
}

internal sealed class TestPrompts(Func<string?>? totp = null) : IConnectPrompts
{
    public int TotpAsked { get; private set; }

    public bool ConfirmNewHost(NewHostPrompt prompt) => true;

    public string? AskTotp(string hostName)
    {
        TotpAsked++;
        return totp?.Invoke();
    }
}

internal sealed class TestCallbacks(bool approve) : NullHostCallbacks
{
    public int ApprovalRequests { get; private set; }

    public List<string> Ended { get; } = new();

    public override Task<bool> ApproveAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        ApprovalRequests++;
        return Task.FromResult(approve);
    }

    public override void OnSessionEnded(SessionInfo session, string reason)
    {
        lock (Ended)
        {
            Ended.Add(reason);
        }
    }
}
