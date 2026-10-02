using System.Drawing;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Forms;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// Host 엔진. LAN(TLS 직접 연결)과 인터넷(시그널링 + WebRTC, STEP 7) 연결을 받아 세션을 실행합니다.
/// 한 번에 한 Client만 화면을 볼 수 있습니다.
/// </summary>
public sealed class HostServer
{
    // 헷갈리는 문자(0/O, 1/I/L)를 뺀 31자. 10자리 ≈ 49비트.
    private const string AccessCodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int AccessCodeLength = 10;
    private const int MaxPendingHandshakes = 16;
    private static readonly TimeSpan TlsHandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _pendingHandshakes = new(MaxPendingHandshakes, MaxPendingHandshakes);
    private readonly object _totpSync = new();
    private Totp? _totp;
    private byte[]? _totpSecret;
    private int _streamingSessions;

    /// <param name="certificate">테스트용. 생략하면 Windows 인증서 저장소의 Host 인증서를 사용합니다.</param>
    public HostServer(HostOptions options, HostSettings settings, IHostCallbacks? callbacks = null, X509Certificate2? certificate = null)
    {
        Options = options;
        Settings = settings;
        Callbacks = callbacks ?? new NullHostCallbacks();
        Certificate = certificate ?? HostCertificate.LoadOrCreate(settings.HostId);
        TlsChannelBinding = AuthProof.TlsChannelBinding(Certificate.RawData);
        AccessCode = CreateAccessCode();
        CaptureBounds = SelectMonitor(options.Monitor);
    }

    public HostOptions Options { get; }

    public HostSettings Settings { get; }

    public IHostCallbacks Callbacks { get; }

    public X509Certificate2 Certificate { get; }

    public byte[] TlsChannelBinding { get; }

    /// <summary>Host를 실행할 때마다 새로 만듭니다. 파일이나 로그에 저장하지 않습니다.</summary>
    public string AccessCode { get; }

    public string FormattedAccessCode => $"{AccessCode[..5]}-{AccessCode[5..]}";

    public Rectangle CaptureBounds { get; }

    public AuthThrottle Throttle { get; } = new();

    /// <summary>원격 잠금/로그아웃/재부팅/종료 실행기 (테스트에서 교체)</summary>
    public IPowerController PowerController { get; set; } = new WindowsPowerController();

    /// <summary>클립보드 접근 (테스트에서 교체)</summary>
    public Func<IClipboardAccess> ClipboardFactory { get; set; } = () => new WindowsClipboard();

    public bool HasActiveSession => Volatile.Read(ref _streamingSessions) == 1;

    public static IEnumerable<IPAddress> GetLanAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Log.Info("Host started");
        Log.Info($"Host ID: {Settings.HostId}");
        Log.Info($"Capture: {CaptureBounds.Width}x{CaptureBounds.Height} at ({CaptureBounds.X},{CaptureBounds.Y}), max {Options.MaxFps} fps");
        Log.Info(Options.ViewOnly ? "Input: disabled (--view-only)" : "Input: enabled (mouse, keyboard)");
        Log.Info($"Auth: access code {(Options.AccessCodeEnabled ? "on" : "off")}, password {(Settings.HasPassword ? "on" : "off")}, " +
                 $"2FA {(Settings.TotpEnabled ? "on" : "off")}, approval {(Options.RequireApproval ? "on" : "off")}, trusted devices {Settings.ListDevices().Count}");

        if (!Options.AccessCodeEnabled && !Settings.HasPassword && !Settings.HasDevices)
        {
            throw new InvalidOperationException("사용할 수 있는 인증 방식이 없습니다. 접속 코드를 켜거나 'password set'으로 비밀번호를 설정하세요.");
        }

        var tasks = new List<Task>();
        if (Options.EnableLan)
        {
            tasks.Add(RunLanListenerAsync(cancellationToken));
            if (Options.Discoverable)
            {
                tasks.Add(new DiscoveryResponder(this).RunAsync(cancellationToken));
            }
        }

        if (Options.SignalingServer is not null)
        {
            tasks.Add(RunSignalingAsync(cancellationToken));
        }

        if (tasks.Count == 0)
        {
            throw new InvalidOperationException("LAN과 시그널링이 모두 꺼져 있습니다.");
        }

        await Task.WhenAll(tasks);
    }

    // ---------------- LAN (TCP + TLS) ----------------

    private async Task RunLanListenerAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Any, Options.Port);
        listener.Start();
        Log.Info($"Listening on TCP {Options.Port} ({(Options.AllowPublicAddresses ? "모든 주소 허용" : "사설망/LAN 주소만 허용")})");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _ = HandleTcpClientAsync(client, cancellationToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleTcpClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
            IPAddress address = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;

            if (!Options.AllowPublicAddresses && !NetworkAddress.IsPrivate(address))
            {
                Log.Warn($"Rejected {remote}: 공인 IP의 직접 연결은 허용하지 않습니다 (--allow-public 또는 인터넷 연결 사용)");
                return;
            }

            if (Throttle.IsLocked(address, out TimeSpan remaining))
            {
                Log.Warn($"Rejected {remote}: 인증 실패가 많아 {Math.Ceiling(remaining.TotalSeconds)}초 동안 차단됨");
                return;
            }

            // 인증 전 연결 수를 제한해 연결 폭주로 자원이 고갈되지 않게 합니다.
            if (!await _pendingHandshakes.WaitAsync(0, cancellationToken))
            {
                Log.Warn($"Rejected {remote}: 동시 연결 시도가 너무 많습니다");
                return;
            }

            Stream? stream = null;
            try
            {
                client.NoDelay = true;
                var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                stream = tls;

                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TlsHandshakeTimeout);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = Certificate,
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    }, timeout.Token);
                }
            }
            catch (Exception exception) when (exception is AuthenticationException or IOException or OperationCanceledException)
            {
                // 앱의 온라인 상태 확인(연결 후 바로 끊기)도 여기로 옵니다.
                Log.Debug($"{remote} TLS handshake not completed: {exception.Message}");
                stream?.Dispose();
                _pendingHandshakes.Release();
                return;
            }

            Log.Info($"Client connected: {remote} (LAN)");
            var transport = new HostTransport(stream, TlsChannelBinding, address, remote.ToString(), "lan");
            await RunSessionAsync(transport, () => _pendingHandshakes.Release(), cancellationToken);
            Log.Info($"Client disconnected: {remote}");
        }
    }

    /// <summary>
    /// 인증과 세션을 실행합니다. onAuthenticated는 인증 단계가 끝나면(성공/실패 무관) 한 번 호출됩니다.
    /// </summary>
    internal async Task RunSessionAsync(HostTransport transport, Action? onAuthPhaseDone, CancellationToken cancellationToken)
    {
        int released = 0;
        void ReleaseOnce()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                onAuthPhaseDone?.Invoke();
            }
        }

        try
        {
            var session = new HostSession(this, transport, ReleaseOnce);
            await session.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Warn($"Session {transport.RemoteDescription} ended with error: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            ReleaseOnce();
            transport.Stream.Dispose();
        }
    }

    // ---------------- 인터넷 (STEP 7) ----------------

    private Task RunSignalingAsync(CancellationToken cancellationToken) =>
        new SignalingHost(this).RunAsync(cancellationToken);

    // ---------------- 세션 슬롯 / TOTP ----------------

    /// <summary>
    /// 스트리밍 슬롯을 얻습니다. 직전 세션이 정리 중일 수 있으므로(빠른 재연결) 최대 2초 기다립니다.
    /// </summary>
    internal async Task<bool> TryBeginStreamingAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (Interlocked.CompareExchange(ref _streamingSessions, 1, 0) == 0)
            {
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        return false;
    }

    internal void EndStreaming() => Interlocked.Exchange(ref _streamingSessions, 0);

    private CancellationTokenSource? _currentSession;

    internal void SetCurrentSession(CancellationTokenSource? session) => Interlocked.Exchange(ref _currentSession, session);

    /// <summary>Host 사용자가 현재 연결을 끊습니다 (Windows 앱의 [연결 끊기] 버튼).</summary>
    public void DisconnectCurrentSession()
    {
        try
        {
            Volatile.Read(ref _currentSession)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>TOTP가 켜져 있으면 검증기를 반환합니다. (한 번 쓴 코드는 재사용 불가 상태를 유지)</summary>
    internal Totp? GetTotp()
    {
        byte[]? secret = Settings.GetTotpSecret();
        lock (_totpSync)
        {
            if (secret is null)
            {
                _totp = null;
                _totpSecret = null;
            }
            else if (_totpSecret is null || !CryptographicOperations.FixedTimeEquals(_totpSecret, secret))
            {
                _totp = new Totp(secret);
                _totpSecret = secret;
            }

            return _totp;
        }
    }

    /// <summary>Client에 알려 줄 모니터 목록 (index는 Screen.AllScreens 순서)</summary>
    public static MonitorInfo[] ListMonitors() =>
        Screen.AllScreens
            .Select((screen, index) => new MonitorInfo(index, screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height, screen.Primary))
            .ToArray();

    /// <summary>모니터 번호(0부터) → 화면 영역. -1이면 모든 모니터를 합친 가상 화면</summary>
    public static Rectangle? MonitorBounds(int index)
    {
        Screen[] screens = Screen.AllScreens;
        if (index == -1)
        {
            return SystemInformation.VirtualScreen;
        }

        return index >= 0 && index < screens.Length ? screens[index].Bounds : null;
    }

    internal string[] EnabledFeatures()
    {
        var features = new List<string>();
        if (!Options.ViewOnly) features.Add(HostFeatures.Input);
        if (Options.AllowClipboard && !Options.ViewOnly) features.Add(HostFeatures.Clipboard);
        if (Options.AllowFileTransfer && !Options.ViewOnly) features.Add(HostFeatures.FileTransfer);
        if (Options.AllowAudio) features.Add(HostFeatures.Audio);
        if (!Options.ViewOnly) features.Add(HostFeatures.Power);
        if (Options.AllowMonitorSelect) features.Add(HostFeatures.MonitorSelect);
        return features.ToArray();
    }

    private static string CreateAccessCode() =>
        string.Create(AccessCodeLength, 0, static (span, _) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = AccessCodeAlphabet[RandomNumberGenerator.GetInt32(AccessCodeAlphabet.Length)];
            }
        });

    private static Rectangle SelectMonitor(int? monitorNumber)
    {
        Screen[] screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            Rectangle bounds = screens[i].Bounds;
            Log.Info($"Monitor {i + 1}: {bounds.Width}x{bounds.Height} at ({bounds.X},{bounds.Y}){(screens[i].Primary ? " [주 모니터]" : "")}");
        }

        if (monitorNumber is null)
        {
            return (Screen.PrimaryScreen ?? screens[0]).Bounds;
        }

        if (monitorNumber > screens.Length)
        {
            throw new ArgumentException($"모니터 {monitorNumber}번이 없습니다. (모니터 수: {screens.Length})");
        }

        return screens[monitorNumber.Value - 1].Bounds;
    }
}

public static class NetworkAddress
{
    /// <summary>사설망, 링크 로컬, 루프백, CGNAT(100.64/10, Tailscale 등) 주소인지</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte first = address.GetAddressBytes()[0];
            return address.IsIPv6LinkLocal || (first & 0xFE) == 0xFC; // fe80::/10, fc00::/7
        }

        return false;
    }
}
