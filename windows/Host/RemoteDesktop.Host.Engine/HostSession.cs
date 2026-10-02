using RemoteDesktop.Core;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// Client 한 명과의 세션: 인증 → 화면 전송 + 입력 처리.
/// 전송 방식(LAN TLS / WebRTC)과 관계없이 같은 코드로 동작합니다.
/// </summary>
internal sealed class HostSession
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    private readonly HostServer _server;
    private readonly HostTransport _transport;
    private readonly Action _onAuthPhaseDone;

    public HostSession(HostServer server, HostTransport transport, Action onAuthPhaseDone)
    {
        _server = server;
        _transport = transport;
        _onAuthPhaseDone = onAuthPhaseDone;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var channel = new MessageChannel(_transport.Stream);

        AuthOutcome? auth;
        try
        {
            auth = await new HostAuthenticator(_server, _transport, channel)
                .AuthenticateAsync(HandshakeTimeout, cancellationToken);
        }
        finally
        {
            _onAuthPhaseDone(); // 인증 전 연결 수 제한 해제
        }

        if (auth is null)
        {
            return;
        }

        var info = new SessionInfo(auth.SessionId, auth.ClientName, auth.Platform, _transport.RemoteDescription, auth.Method, _transport.Kind);
        string reason = "연결 종료";
        _server.Callbacks.OnSessionStarted(info);

        try
        {
            reason = await StreamAsync(channel, auth, cancellationToken);
        }
        finally
        {
            _server.EndStreaming();
            AccessLog.Write("logout", _transport.RemoteDescription, auth.ClientName, auth.Method, reason);
            _server.Callbacks.OnSessionEnded(info, reason);
        }
    }

    /// <summary>세션이 끝날 때까지 실행하고 종료 이유를 반환합니다.</summary>
    private async Task<string> StreamAsync(MessageChannel channel, AuthOutcome auth, CancellationToken cancellationToken)
    {
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var hostUserDisconnect = new CancellationTokenSource();
        _server.SetCurrentSession(hostUserDisconnect);
        CancellationToken token = sessionCancellation.Token;

        var input = _server.Options.ViewOnly ? null : new InputInjector(_server.CaptureBounds);
        using var video = new VideoStreamer(channel, _server.Options, _server.CaptureBounds, auth.Codec);
        using var features = new SessionFeatures(_server, channel, _transport.RemoteDescription, auth.ClientName, token);

        Task<string> receiveTask = ReceiveLoopAsync(channel, video, input, features, token);
        Task sendTask = Task.Run(() => video.RunAsync(token), token);
        Task expiryTask = Task.Delay(_server.Options.MaxSessionDuration, token);
        Task kickTask = Task.Delay(Timeout.Infinite, hostUserDisconnect.Token);

        Log.Info($"Video stream started ({auth.Codec})");
        Task finished = await Task.WhenAny(receiveTask, sendTask, expiryTask, kickTask);
        _server.SetCurrentSession(null);

        string reason;
        if (finished == kickTask)
        {
            reason = "Host 사용자가 연결을 끊었습니다.";
            await SayByeAsync(channel, reason, receiveTask);
        }
        else if (finished == expiryTask && !token.IsCancellationRequested)
        {
            reason = "세션 시간이 만료되었습니다. 다시 연결하세요.";
            Log.Info("Session expired");
            await SayByeAsync(channel, reason, receiveTask);
        }
        else if (cancellationToken.IsCancellationRequested)
        {
            reason = "Host가 종료되었습니다.";
            await SayByeAsync(channel, reason, receiveTask);
        }
        else
        {
            try
            {
                reason = finished == receiveTask ? await receiveTask : "전송 종료";
                await finished;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or ProtocolException or OperationCanceledException)
            {
                reason = $"연결 끊김: {exception.Message}";
            }
        }

        // 남은 작업을 멈추고, 스트림을 닫아 대기 중인 읽기/쓰기를 깨웁니다.
        sessionCancellation.Cancel();
        channel.Dispose();

        try
        {
            await Task.WhenAll(receiveTask, sendTask);
        }
        catch
        {
            // 종료 과정의 예외는 무시합니다.
        }

        input?.ReleaseAll();
        Log.Info($"Video stream ended: {reason} ({video.Stats.TotalFrames} frames sent, {input?.EventCount ?? 0} input events)");
        return reason;
    }

    private async Task<string> ReceiveLoopAsync(MessageChannel channel, VideoStreamer video, InputInjector? input, SessionFeatures features, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ReceivedMessage message = await channel.ReceiveAsync(cancellationToken);

            if (await features.TryHandleAsync(message))
            {
                continue;
            }

            switch (message.Control)
            {
                case FrameAckMessage ack:
                    video.OnAck(ack.FrameId);
                    break;

                case KeyframeRequestMessage:
                    video.RequestKeyframe();
                    break;

                case SelectMonitorMessage select when _server.Options.AllowMonitorSelect:
                    if (HostServer.MonitorBounds(select.Index) is { } bounds)
                    {
                        video.SwitchBounds(bounds);
                        input?.SetBounds(bounds); // 입력 좌표도 새 모니터 기준으로
                    }

                    break;

                case ByeMessage bye:
                    Log.Info($"Client said bye: {HostAuthenticator.Sanitize(bye.Reason)}");
                    return "Client가 연결을 종료했습니다.";

                case { } control when input is not null && input.TryHandle(control):
                    break;

                default:
                    Log.Debug($"Ignored message from {_transport.RemoteDescription}: {message.Control?.GetType().Name ?? "unknown"}");
                    break;
            }
        }

        return "연결 종료";
    }

    /// <summary>
    /// bye를 보내고 Client가 먼저 끊을 때까지 잠깐 기다립니다.
    /// 바로 닫으면 TCP RST 때문에 Client가 bye를 읽기 전에 연결이 끊길 수 있습니다.
    /// </summary>
    private static async Task SayByeAsync(MessageChannel channel, string reason, Task receiveTask)
    {
        await TrySendAsync(channel, new ByeMessage(reason));
        await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(1)));
    }

    private static async Task TrySendAsync(MessageChannel channel, ControlMessage message)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await channel.SendControlAsync(message, timeout.Token);
        }
        catch
        {
        }
    }
}
