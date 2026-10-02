using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteDesktop.Core;
using RemoteDesktop.Media;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Client;

/// <summary>처음 보는 Host에 접속할 때 사용자에게 보여 줄 정보입니다.</summary>
public sealed record NewHostPrompt(string HostId, string HostName, string Fingerprint);

/// <summary>사용자에게 그대로 보여 줘도 되는 연결 실패 사유입니다.</summary>
public sealed class ConnectionFailedException(string message, string? errorCode = null) : Exception(message)
{
    public string? ErrorCode { get; } = errorCode;
}

/// <summary>로그인 정보. Secret은 메모리에만 두고 저장하지 않습니다.</summary>
public sealed record LoginRequest(string Method, string Secret, bool RememberDevice);

/// <summary>연결 중 사용자에게 물어볼 것들 (UI 스레드 처리는 구현 쪽 책임)</summary>
public interface IConnectPrompts
{
    /// <summary>처음 보는 Host(LAN)의 인증서 지문 확인</summary>
    bool ConfirmNewHost(NewHostPrompt prompt);

    /// <summary>2단계 인증 코드 입력. 취소하면 null</summary>
    string? AskTotp(string hostName);
}

/// <summary>
/// 암호화된 연결 하나 (LAN: TLS, 인터넷: WebRTC).
/// PinnedFingerprint가 있으면 TOFU(처음 연결 시 지문 확인)를 적용합니다.
/// </summary>
public sealed record ClientTransport(Stream Stream, byte[] ChannelBinding, string Kind, byte[]? PinnedFingerprint, IDisposable? Owner);

/// <summary>
/// Host 연결 하나. ConnectLanAsync/AuthenticateAsync로 연결과 인증을 끝낸 뒤 StartReceiving으로 화면을 받습니다.
/// </summary>
public sealed class RemoteHostConnection : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AuthResultTimeout = TimeSpan.FromSeconds(60); // Host 승인 대기 포함

    /// <summary>이 PC에서 H.264 디코더를 쓸 수 있는지 (한 번만 확인)</summary>
    private static readonly Lazy<bool> H264Available = new(() =>
    {
        try
        {
            using var probe = new H264Decoder();
            return true;
        }
        catch (Exception exception)
        {
            Log.Warn($"H.264 디코더를 사용할 수 없어 JPEG로 받습니다: {exception.Message}");
            return false;
        }
    });

    private readonly ClientTransport _transport;
    private readonly MessageChannel _channel;
    private readonly CancellationTokenSource _cancellation = new();
    private H264Decoder? _decoder;
    private readonly object _decoderSync = new(); // 수신 스레드의 디코딩과 Dispose가 겹치지 않게 (COM 객체 이중 해제 방지)
    private bool _decoderClosed;
    private int _disposed;

    private RemoteHostConnection(ClientTransport transport, MessageChannel channel, string hostId, string hostName, AuthResultMessage result)
    {
        _transport = transport;
        _channel = channel;
        HostId = hostId;
        HostName = hostName;
        ScreenWidth = result.ScreenWidth;
        ScreenHeight = result.ScreenHeight;
        Monitors = result.Monitors ?? [];
        VideoCodecName = result.VideoCodec ?? VideoCodecNames.Jpeg;
        Features = result.Features ?? [];
        MacAddresses = (result.MacAddresses ?? []).Where(WakeOnLan.IsValidMac).Take(8).ToArray();
    }

    /// <summary>Host의 MAC 주소 (Wake-on-LAN으로 켤 때 사용)</summary>
    public string[] MacAddresses { get; }

    /// <summary>Host 모니터 목록 (모니터 선택 기능이 꺼져 있으면 비어 있음)</summary>
    public MonitorInfo[] Monitors { get; }

    /// <summary>협상된 영상 코덱 (h264 / jpeg)</summary>
    public string VideoCodecName { get; }

    /// <summary>Host가 허용한 부가 기능 (HostFeatures)</summary>
    public string[] Features { get; }

    public bool HasFeature(string feature) => Features.Contains(feature);

    /// <summary>Host가 2초마다 보내는 연결 품질 (백그라운드 스레드)</summary>
    public event Action<StreamStatsMessage>? StatsReceived;

    /// <summary>그 밖의 제어 메시지: 클립보드, 파일, 전원 결과 등 (백그라운드 스레드)</summary>
    public event Action<ControlMessage>? ControlReceived;

    /// <summary>파일 조각·오디오 같은 바이너리 메시지 (백그라운드 스레드)</summary>
    public event Action<BinaryMessage>? BinaryReceived;

    /// <summary>백그라운드 스레드에서 호출됩니다. 받은 Bitmap은 구독자가 Dispose해야 합니다.</summary>
    public event Action<Bitmap, VideoFrameHeader>? FrameReceived;

    /// <summary>연결이 끊기면 한 번 호출됩니다. (사용자가 직접 닫은 경우는 호출되지 않음)</summary>
    public event Action<string>? Disconnected;

    public string HostId { get; }

    public string HostName { get; }

    public int ScreenWidth { get; }

    public int ScreenHeight { get; }

    public string TransportKind => _transport.Kind;

    // ---------------- LAN 연결 (TCP + TLS) ----------------

    public static async Task<ClientTransport> ConnectLanAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            Log.Info($"Connecting to {host}:{port}");
            try
            {
                await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            }
            catch (SocketException exception)
            {
                throw new ConnectionFailedException($"{host}:{port}에 연결할 수 없습니다. ({exception.SocketErrorCode})\nHost 실행 여부, IP, 방화벽을 확인하세요.");
            }

            // Host는 자체 서명 인증서를 쓰므로 여기서는 받아 두고,
            // (1) 인증서 지문 저장소(TOFU) (2) 인증 증명값에 섞인 인증서 해시로 검증합니다.
            var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => certificate is not null);
            try
            {
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "RemoteDesktop Host",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, timeout.Token).ConfigureAwait(false);
            }
            catch (AuthenticationException exception)
            {
                throw new ConnectionFailedException($"TLS 연결 실패: {exception.Message}");
            }

            using var remoteCertificate = new X509Certificate2(tls.RemoteCertificate!);
            byte[] binding = AuthProof.TlsChannelBinding(remoteCertificate.RawData);
            Log.Info($"TLS established ({tls.SslProtocol})");
            return new ClientTransport(tls, binding, "lan", binding, tcp);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new ConnectionFailedException("연결 시간이 초과되었습니다.");
        }
        catch (IOException exception)
        {
            tcp.Dispose();
            throw new ConnectionFailedException($"연결이 끊겼습니다: {exception.Message}");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    // ---------------- 인터넷 연결 (시그널링 + WebRTC) ----------------

    /// <summary>
    /// Host ID로 시그널링 서버를 거쳐 WebRTC Data Channel 연결을 맺습니다.
    /// Host 인증서 지문(TOFU) 대신 DTLS 지문이 증명값에 섞여 중간자를 막습니다.
    /// </summary>
    public static async Task<ClientTransport> ConnectInternetAsync(Uri signalingServer, string hostId, CancellationToken cancellationToken)
    {
        try
        {
            Log.Info($"Connecting to {hostId} via {signalingServer.Host}");
            var connection = await RemoteDesktop.Transport.WebRtc.WebRtcClientConnector.ConnectAsync(signalingServer, hostId.Trim().ToUpperInvariant(), cancellationToken).ConfigureAwait(false);
            Log.Info("WebRTC data channel open");
            return new ClientTransport(connection.Stream, connection.ChannelBinding, "webrtc", null, connection.Stream);
        }
        catch (TimeoutException)
        {
            throw new ConnectionFailedException("연결 시간이 초과되었습니다. 방화벽/NAT 환경이면 TURN 서버 설정이 필요할 수 있습니다.");
        }
        catch (Exception exception) when (exception is IOException or System.Net.WebSockets.WebSocketException or ProtocolException or HttpRequestException)
        {
            throw new ConnectionFailedException($"인터넷 연결 실패: {exception.Message}");
        }
    }

    // ---------------- 인증 (전송 방식 공통) ----------------

    public static async Task<RemoteHostConnection> AuthenticateAsync(
        ClientTransport transport,
        LoginRequest login,
        IConnectPrompts prompts,
        DeviceCredentialStore devices,
        CancellationToken cancellationToken)
    {
        var channel = new MessageChannel(transport.Stream);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            CancellationToken token = timeout.Token;

            // 1) hello
            byte[] clientNonce = AuthProof.CreateNonce();
            await channel.SendControlAsync(new HelloMessage(
                ProtocolConstants.Version,
                Environment.MachineName,
                "windows",
                Convert.ToBase64String(clientNonce),
                H264Available.Value ? [VideoCodecNames.H264, VideoCodecNames.Jpeg] : [VideoCodecNames.Jpeg]), token).ConfigureAwait(false);

            // 2) challenge
            var challenge = await ReceiveControlAsync<AuthChallengeMessage>(channel, token).ConfigureAwait(false);
            if (!AuthProof.TryDecodeNonce(challenge.ServerNonce, out byte[] serverNonce))
            {
                throw new ProtocolException("server_nonce 형식이 잘못되었습니다.");
            }

            string[] methods = challenge.AuthMethods ?? [AuthMethods.AccessCode];

            // 3) LAN이면 인증서 지문 확인 (비밀 증명값을 보내기 전에)
            if (transport.PinnedFingerprint is { } fingerprint)
            {
                string fingerprintHex = Convert.ToHexString(fingerprint);
                switch (KnownHostsStore.Check(challenge.HostId, fingerprintHex))
                {
                    case HostTrust.Mismatch:
                        throw new ConnectionFailedException(
                            $"경고: {challenge.HostId}의 인증서가 이전과 다릅니다.\n" +
                            "중간자 공격일 수 있어 연결을 중단했습니다.\n" +
                            "Host를 다시 설치했거나 인증서를 지운 것이 확실하면\n" +
                            "%LOCALAPPDATA%\\RemoteDesktop\\known_hosts.json 에서 해당 항목을 지우세요.");

                    case HostTrust.Unknown:
                        if (!prompts.ConfirmNewHost(new NewHostPrompt(challenge.HostId, challenge.HostName, AuthProof.FormatFingerprint(fingerprint))))
                        {
                            throw new ConnectionFailedException("사용자가 연결을 취소했습니다.");
                        }

                        break;
                }
            }

            // 4) 인증 방식 결정: 신뢰된 장치로 등록되어 있으면 장치 인증을 우선 사용
            string method;
            byte[] key;
            string? deviceId = null;
            string? totp = null;
            var saved = devices.Get(challenge.HostId);

            if (saved is not null && methods.Contains(AuthMethods.Device))
            {
                method = AuthMethods.Device;
                key = saved.Value.Secret;
                deviceId = saved.Value.DeviceId;
                Log.Info($"Using trusted device credential {deviceId}");
            }
            else
            {
                if (saved is not null)
                {
                    // 저장된 장치 자격 증명이 있는데 Host가 장치 인증을 받지 않음 = 등록이 해제됨
                    devices.Remove(challenge.HostId);
                    Log.Info("Stored device credential is no longer accepted; removed");
                    if (string.IsNullOrEmpty(login.Secret))
                    {
                        throw new ConnectionFailedException("이 PC의 신뢰된 장치 등록이 해제되었습니다. 비밀번호나 접속 코드로 다시 로그인하세요.",
                            AuthErrorCodes.DeviceRevoked);
                    }
                }

                method = login.Method;
                if (!methods.Contains(method))
                {
                    throw new ConnectionFailedException(method == AuthMethods.Password
                        ? "이 Host에는 비밀번호가 설정되어 있지 않습니다. 접속 코드로 로그인하세요."
                        : "이 Host는 접속 코드 로그인을 사용하지 않습니다. 비밀번호로 로그인하세요.");
                }

                if (string.IsNullOrEmpty(login.Secret))
                {
                    throw new ConnectionFailedException(method == AuthMethods.Password ? "비밀번호를 입력하세요." : "접속 코드를 입력하세요.");
                }

                if (method == AuthMethods.Password)
                {
                    if (!AuthProof.TryDecodeFixed(challenge.PasswordSalt, 16, out byte[] salt) || challenge.PasswordIterations is < 10_000 or > 5_000_000)
                    {
                        throw new ProtocolException("비밀번호 매개변수가 잘못되었습니다.");
                    }

                    key = AuthProof.DerivePasswordKey(login.Secret, salt, challenge.PasswordIterations);
                }
                else
                {
                    key = AuthProof.DeriveAccessCodeKey(login.Secret);
                }

                if (challenge.TotpRequired)
                {
                    totp = prompts.AskTotp(challenge.HostName)?.Trim()
                        ?? throw new ConnectionFailedException("2단계 인증을 취소했습니다.");
                }
            }

            // 5) 증명값 전송
            byte[] proof = AuthProof.Compute(AuthRole.Client, key, clientNonce, serverNonce, transport.ChannelBinding);
            await channel.SendControlAsync(new AuthResponseMessage(
                Convert.ToBase64String(proof),
                method,
                deviceId,
                totp,
                RegisterDevice: login.RememberDevice && method != AuthMethods.Device,
                DeviceName: Environment.MachineName), token).ConfigureAwait(false);

            // 6) 결과 (Host 승인 대기 시간 포함) + Host 증명 확인
            using var resultTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            resultTimeout.CancelAfter(AuthResultTimeout);
            var result = await ReceiveControlAsync<AuthResultMessage>(channel, resultTimeout.Token).ConfigureAwait(false);
            if (!result.Success)
            {
                if (result.ErrorCode == AuthErrorCodes.DeviceRevoked)
                {
                    devices.Remove(challenge.HostId); // 다음에는 비밀번호/접속 코드로 로그인
                }

                throw new ConnectionFailedException(result.Error ?? "인증에 실패했습니다.", result.ErrorCode);
            }

            if (!AuthProof.Verify(AuthRole.Host, key, clientNonce, serverNonce, transport.ChannelBinding, result.HostProof))
            {
                throw new ConnectionFailedException("Host가 올바른 증명을 보내지 않았습니다. 연결을 중단합니다.");
            }

            // 7) 저장: 인증서 지문(TOFU), 새로 받은 장치 자격 증명
            if (transport.PinnedFingerprint is { } pinned)
            {
                KnownHostsStore.Save(challenge.HostId, Convert.ToHexString(pinned));
            }

            if (result.DeviceId is { } newDeviceId && AuthProof.TryDecodeFixed(result.DeviceSecret, AuthProof.KeyBytes, out byte[] newSecret))
            {
                devices.Save(challenge.HostId, newDeviceId, newSecret);
                Log.Info($"Registered as trusted device {newDeviceId}");
            }

            Log.Info($"Authentication successful: {challenge.HostId} ({challenge.HostName}) method={method}, screen {result.ScreenWidth}x{result.ScreenHeight}");
            return new RemoteHostConnection(transport, channel, challenge.HostId, challenge.HostName, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Close(transport, channel);
            throw new ConnectionFailedException("응답 시간이 초과되었습니다.");
        }
        catch (Exception exception) when (exception is IOException or ProtocolException or EndOfStreamException)
        {
            Close(transport, channel);
            throw new ConnectionFailedException($"연결이 끊겼습니다: {exception.Message}");
        }
        catch
        {
            Close(transport, channel);
            throw;
        }
    }

    private static void Close(ClientTransport transport, MessageChannel channel)
    {
        channel.Dispose();
        transport.Owner?.Dispose();
    }

    // ---------------- 세션 ----------------

    public void StartReceiving()
    {
        _ = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>파일 하나를 Host로 보냅니다 (file_begin → 조각 → file_end). 결과는 ClientFeatures가 기다립니다.</summary>
    public Task SendFileAsync(uint transferId, string path, IProgress<long>? progress, CancellationToken cancellationToken) =>
        RemoteDesktop.Host.Engine.FileTransferProtocol.SendFileAsync(_channel, transferId, path, "upload", progress, cancellationToken);

    /// <summary>프레임을 화면에 표시한 뒤 호출합니다. Host는 이 신호를 받아야 다음 프레임을 보냅니다.</summary>
    public Task SendAckAsync(uint frameId) => SendAsync(new FrameAckMessage(frameId));

    /// <summary>입력 등 제어 메시지 전송. 연결이 끊긴 경우 false.</summary>
    public async Task<bool> SendAsync(ControlMessage message)
    {
        try
        {
            await _channel.SendControlAsync(message, _cancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task ReceiveLoopAsync()
    {
        string reason = "연결이 종료되었습니다.";
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                ReceivedMessage message = await _channel.ReceiveAsync(_cancellation.Token).ConfigureAwait(false);

                if (message.Video is { } video)
                {
                    HandleVideoFrame(video);
                }
                else if (message.Control is ByeMessage bye)
                {
                    reason = $"Host가 연결을 종료했습니다: {bye.Reason}";
                    break;
                }
                else if (message.Control is StreamStatsMessage stats)
                {
                    StatsReceived?.Invoke(stats);
                }
                else if (message.Control is { } control)
                {
                    ControlReceived?.Invoke(control);
                }
                else if (message.Binary is { } binary)
                {
                    BinaryReceived?.Invoke(binary);
                }
            }
        }
        catch (Exception exception)
        {
            // 어떤 오류든 연결 종료로 처리해야 창이 멈춘 채로 남지 않습니다.
            reason = $"연결이 끊겼습니다: {exception.Message}";
            if (exception is not (IOException or ProtocolException or ObjectDisposedException or OperationCanceledException or EndOfStreamException))
            {
                Log.Error($"Receive loop failed: {exception}");
            }
        }

        if (!_cancellation.IsCancellationRequested)
        {
            Log.Warn(reason);
            Disconnected?.Invoke(reason);
        }
    }

    private void HandleVideoFrame(VideoFrame video)
    {
        if (video.Header.Codec == VideoCodec.H264)
        {
            HandleH264Frame(video);
            return;
        }

        if (video.Header.Codec != VideoCodec.Jpeg)
        {
            Log.Warn($"지원하지 않는 코덱입니다: {video.Header.Codec}");
            _ = SendAckAsync(video.Header.FrameId);
            return;
        }

        if (!MemoryMarshal.TryGetArray(video.Data, out ArraySegment<byte> segment))
        {
            throw new InvalidOperationException("프레임 버퍼를 읽을 수 없습니다.");
        }

        Bitmap frame;
        try
        {
            using var stream = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
            using var decoded = Image.FromStream(stream);

            // Image.FromStream 결과는 스트림이 살아 있어야 하므로 복사본을 만듭니다.
            // PArgb 형식이 화면에 그릴 때 가장 빠릅니다.
            frame = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppPArgb);
            using Graphics graphics = Graphics.FromImage(frame);
            graphics.DrawImageUnscaled(decoded, 0, 0);
        }
        catch (ArgumentException)
        {
            Log.Warn($"프레임 {video.Header.FrameId} 디코딩 실패");
            _ = SendAckAsync(video.Header.FrameId);
            return;
        }

        FrameReceived?.Invoke(frame, video.Header);
    }

    /// <summary>
    /// H.264 프레임 디코딩. 프레임끼리 의존하므로 모든 프레임을 순서대로 디코딩합니다.
    /// 오류가 나면 디코더를 새로 만들고 Host에 키프레임을 요청합니다.
    /// </summary>
    private unsafe void HandleH264Frame(VideoFrame video)
    {
        byte[]? bgra;
        int width = video.Header.Width;
        int height = video.Header.Height;
        lock (_decoderSync)
        {
            if (_decoderClosed)
            {
                return; // 연결을 닫는 중
            }

            try
            {
                _decoder ??= new H264Decoder();
                bgra = _decoder.Decode(video.Data.Span, width, height);
                width = _decoder.LastWidth;
                height = _decoder.LastHeight;
            }
            catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or COMException or NotSupportedException)
            {
                Log.Warn($"H.264 디코딩 오류: {exception.Message}. 키프레임을 요청합니다.");
                _decoder?.Dispose();
                _decoder = null;
                _ = SendAsync(new KeyframeRequestMessage());
                _ = SendAckAsync(video.Header.FrameId);
                return;
            }
        }

        if (bgra is null || width <= 0 || height <= 0)
        {
            _ = SendAckAsync(video.Header.FrameId); // 디코더가 아직 출력하지 않음
            return;
        }

        var frame = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        BitmapData data = frame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            fixed (byte* source = bgra)
            {
                for (int y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(source + y * width * 4, (byte*)data.Scan0 + y * data.Stride, data.Stride, width * 4);
                }
            }
        }
        finally
        {
            frame.UnlockBits(data);
        }

        FrameReceived?.Invoke(frame, video.Header);
    }

    private static async Task<T> ReceiveControlAsync<T>(MessageChannel channel, CancellationToken cancellationToken)
        where T : ControlMessage
    {
        ReceivedMessage message = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        return message.Control as T
            ?? throw new ProtocolException($"{typeof(T).Name}를 기대했지만 다른 메시지를 받았습니다.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            // 정상 종료 알림. 오래 기다리지 않습니다.
            _channel.SendControlAsync(new ByeMessage("client closed"), CancellationToken.None).Wait(TimeSpan.FromMilliseconds(300));
        }
        catch
        {
        }

        _cancellation.Cancel();
        _channel.Dispose();
        _transport.Owner?.Dispose();
        _cancellation.Dispose();
        lock (_decoderSync)
        {
            _decoderClosed = true;
            _decoder?.Dispose();
            _decoder = null;
        }

        Log.Info("Disconnected");
    }
}
