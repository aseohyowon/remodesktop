using System.Security.Cryptography;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Host.Engine;

public sealed record AuthOutcome(string SessionId, string ClientName, string Platform, string Method, string? DeviceId, string Codec);

/// <summary>
/// 연결 하나의 인증 절차 (hello → auth_challenge → auth_response → auth_result)
///
/// 인증 방식: 접속 코드 / 비밀번호 / 신뢰된 장치
/// 추가 확인: 2단계 인증(TOTP), Host 사용자 승인
/// 실패 시: 1초 지연, IP별·전체 실패 횟수 제한, 접속 기록
/// </summary>
internal sealed class HostAuthenticator
{
    private static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromSeconds(30);

    private readonly HostServer _server;
    private readonly HostTransport _transport;
    private readonly MessageChannel _channel;

    public HostAuthenticator(HostServer server, HostTransport transport, MessageChannel channel)
    {
        _server = server;
        _transport = transport;
        _channel = channel;
    }

    /// <summary>성공하면 결과, 실패하면(이미 실패 응답을 보낸 뒤) null. 스트리밍 슬롯은 성공 시에만 잡혀 있습니다.</summary>
    public async Task<AuthOutcome?> AuthenticateAsync(TimeSpan handshakeTimeout, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(handshakeTimeout);
        CancellationToken token = timeout.Token;

        // 1) hello
        HelloMessage hello = await ReceiveAsync<HelloMessage>(token);
        string clientName = Sanitize(hello.ClientName);
        string platform = Sanitize(hello.Platform);

        if (hello.ProtocolVersion != ProtocolConstants.Version)
        {
            await FailAsync($"프로토콜 버전이 다릅니다. Host={ProtocolConstants.Version}, Client={hello.ProtocolVersion}. 앱을 업데이트하세요.",
                AuthErrorCodes.Unsupported, token);
            return null;
        }

        if (!AuthProof.TryDecodeNonce(hello.ClientNonce, out byte[] clientNonce))
        {
            throw new ProtocolException("client_nonce 형식이 잘못되었습니다.");
        }

        Log.Info($"Hello from {_transport.RemoteDescription}: {clientName} ({platform}) via {_transport.Kind}");

        // 2) challenge
        HostSettings settings = _server.Settings;
        var password = settings.GetPasswordKey();
        var methods = new List<string>();
        if (_server.Options.AccessCodeEnabled) methods.Add(AuthMethods.AccessCode);
        if (password is not null) methods.Add(AuthMethods.Password);
        if (settings.HasDevices) methods.Add(AuthMethods.Device);

        byte[] serverNonce = AuthProof.CreateNonce();
        await _channel.SendControlAsync(new AuthChallengeMessage(
            ProtocolConstants.Version,
            settings.HostId,
            Environment.MachineName,
            Convert.ToBase64String(serverNonce),
            methods.ToArray(),
            password is { } p ? Convert.ToBase64String(p.Salt) : null,
            password?.Iterations ?? 0,
            settings.TotpEnabled), token);

        // 3) response
        AuthResponseMessage response = await ReceiveAsync<AuthResponseMessage>(token);
        string method = response.Method;

        byte[]? key = method switch
        {
            AuthMethods.AccessCode when _server.Options.AccessCodeEnabled => AuthProof.DeriveAccessCodeKey(_server.AccessCode),
            AuthMethods.Password => password?.Key,
            AuthMethods.Device => settings.GetDeviceSecret(response.DeviceId),
            _ => null
        };

        if (key is null)
        {
            // 등록이 해제된 장치는 Client가 저장된 자격 증명을 지우도록 알려 줍니다.
            bool revokedDevice = method == AuthMethods.Device;
            return await RejectAsync(clientName, method,
                revokedDevice ? "등록이 해제되었거나 만료된 장치입니다. 다시 로그인하세요." : "사용할 수 없는 인증 방식입니다.",
                revokedDevice ? AuthErrorCodes.DeviceRevoked : AuthErrorCodes.InvalidCredentials, token);
        }

        bool proofValid = AuthProof.Verify(AuthRole.Client, key, clientNonce, serverNonce, _transport.ChannelBinding, response.Proof);
        if (!proofValid)
        {
            string message = method switch
            {
                AuthMethods.Password => "비밀번호가 올바르지 않습니다.",
                AuthMethods.Device => "장치 인증에 실패했습니다. 다시 로그인하세요.",
                _ => "접속 코드가 올바르지 않습니다."
            };
            return await RejectAsync(clientName, method, message,
                method == AuthMethods.Device ? AuthErrorCodes.DeviceRevoked : AuthErrorCodes.InvalidCredentials, token);
        }

        // 4) 2단계 인증 (신뢰된 장치는 등록할 때 이미 통과했으므로 생략)
        if (method != AuthMethods.Device && _server.GetTotp() is { } totp && !totp.Verify(response.Totp, DateTimeOffset.UtcNow))
        {
            return await RejectAsync(clientName, method,
                string.IsNullOrEmpty(response.Totp) ? "2단계 인증 코드가 필요합니다." : "2단계 인증 코드가 올바르지 않습니다.",
                AuthErrorCodes.TotpRequired, token);
        }

        // 5) Host 사용자 승인 (신뢰된 장치는 생략)
        if (method != AuthMethods.Device && _server.Options.RequireApproval)
        {
            Log.Info($"Waiting for approval: {clientName} ({_transport.RemoteDescription})");
            bool approved;
            using (var approvalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                approvalTimeout.CancelAfter(ApprovalTimeout);
                try
                {
                    approved = await _server.Callbacks.ApproveAsync(
                        new ApprovalRequest(clientName, platform, _transport.RemoteDescription, method), approvalTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    approved = false;
                }
            }

            if (!approved)
            {
                AccessLog.Write("denied", _transport.RemoteDescription, clientName, method);
                Log.Warn($"Connection denied by host user: {clientName}");
                await FailAsync("Host 사용자가 접속을 거부했습니다.", AuthErrorCodes.Denied, cancellationToken);
                return null;
            }
        }

        _server.Throttle.RecordSuccess(_transport.RemoteAddress);

        // 6) 동시 접속 확인
        if (!await _server.TryBeginStreamingAsync(cancellationToken))
        {
            Log.Warn($"Rejected {_transport.RemoteDescription}: 다른 Client가 이미 연결되어 있음");
            await FailAsync("다른 Client가 이미 연결되어 있습니다.", AuthErrorCodes.Busy, cancellationToken);
            return null;
        }

        try
        {
            // 7) 장치 등록 (요청한 경우, 장치 인증이 아닌 경우)
            string? deviceId = method == AuthMethods.Device ? response.DeviceId : null;
            byte[]? newDeviceSecret = null;
            if (response.RegisterDevice && method != AuthMethods.Device)
            {
                (deviceId, newDeviceSecret) = settings.RegisterDevice(Sanitize(response.DeviceName ?? clientName));
                Log.Info($"Registered trusted device {deviceId} ({clientName})");
                AccessLog.Write("device_registered", _transport.RemoteDescription, clientName, method, deviceId);
            }
            else if (method == AuthMethods.Device && deviceId is not null)
            {
                settings.TouchDevice(deviceId);
            }

            string sessionId = Guid.NewGuid().ToString("N");
            string codec = ChooseCodec(hello.Codecs);
            byte[] hostProof = AuthProof.Compute(AuthRole.Host, key, clientNonce, serverNonce, _transport.ChannelBinding);

            await _channel.SendControlAsync(new AuthResultMessage(
                Success: true,
                Error: null,
                SessionId: sessionId,
                HostProof: Convert.ToBase64String(hostProof),
                ScreenWidth: _server.CaptureBounds.Width,
                ScreenHeight: _server.CaptureBounds.Height,
                DeviceId: newDeviceSecret is null ? null : deviceId,
                DeviceSecret: newDeviceSecret is null ? null : Convert.ToBase64String(newDeviceSecret),
                Monitors: _server.Options.AllowMonitorSelect ? HostServer.ListMonitors() : null,
                VideoCodec: codec,
                Features: _server.EnabledFeatures(),
                MacAddresses: WakeOnLan.LocalMacAddresses()), cancellationToken);

            if (newDeviceSecret is not null)
            {
                CryptographicOperations.ZeroMemory(newDeviceSecret);
            }

            Log.Info($"Authentication successful: {clientName} ({_transport.RemoteDescription}) method={method}");
            AccessLog.Write("login", _transport.RemoteDescription, clientName, method, deviceId);
            return new AuthOutcome(sessionId, clientName, platform, method, deviceId, codec);
        }
        catch
        {
            _server.EndStreaming();
            throw;
        }
    }

    /// <summary>Client가 H.264를 디코딩할 수 있고 Host 설정이 허용하면 H.264, 아니면 JPEG</summary>
    private string ChooseCodec(string[]? clientCodecs)
    {
        bool clientH264 = clientCodecs?.Contains(VideoCodecNames.H264) ?? false;
        return _server.Options.Codec switch
        {
            "jpeg" => VideoCodecNames.Jpeg,
            _ when clientH264 => VideoCodecNames.H264,
            _ => VideoCodecNames.Jpeg
        };
    }

    private async Task<AuthOutcome?> RejectAsync(string clientName, string method, string message, string errorCode, CancellationToken token)
    {
        bool locked = _server.Throttle.RecordFailure(_transport.RemoteAddress);
        Log.Warn($"Authentication failed from {_transport.RemoteDescription} method={method} ({errorCode}){(locked ? " - 차단됨" : "")}");
        AccessLog.Write("login_failed", _transport.RemoteDescription, clientName, method, errorCode);

        // 실패 응답을 늦게 보내 대입 공격 속도를 줄입니다.
        await Task.Delay(FailureDelay, token);
        await FailAsync(message, errorCode, token);
        return null;
    }

    private Task FailAsync(string error, string errorCode, CancellationToken token) =>
        _channel.SendControlAsync(new AuthResultMessage(false, error, null, null, 0, 0, ErrorCode: errorCode), token);

    private async Task<T> ReceiveAsync<T>(CancellationToken token) where T : ControlMessage
    {
        ReceivedMessage message = await _channel.ReceiveAsync(token);
        return message.Control as T
            ?? throw new ProtocolException($"{typeof(T).Name}를 기대했지만 다른 메시지를 받았습니다.");
    }

    /// <summary>상대가 보낸 문자열을 로그/화면에 쓰기 전에 길이와 제어 문자를 제한합니다.</summary>
    internal static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "-";
        }

        string cleaned = new(value.Where(character => !char.IsControl(character)).Take(64).ToArray());
        return cleaned.Length == 0 ? "-" : cleaned;
    }
}
