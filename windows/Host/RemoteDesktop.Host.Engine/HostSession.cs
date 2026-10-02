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

        // 서비스 모드(STEP 13): 캡처와 입력을 각각 전용 스레드에서, 로그인/잠금/UAC 화면까지 따라가며 처리
        using DesktopThread? captureDesktop = _server.Options.FollowInputDesktop ? new DesktopThread("desktop-capture") : null;
        using DesktopThread? inputDesktop = _server.Options.FollowInputDesktop && input is not null ? new DesktopThread("desktop-input") : null;

        using var video = new VideoStreamer(channel, _server.Options, _server.CaptureBounds, auth.Codec, captureDesktop);
        using var features = new SessionFeatures(_server, channel, _transport.RemoteDescription, auth.ClientName, token);
        using var display = new DisplaySession(_server.DisplayController); // 세션이 끝나면 바꾼 해상도를 되돌림
        using var sleepBlocker = new SleepBlocker();                        // 연결 중 절전 방지
        var view = new ViewState(_server.CaptureBounds);

        Task<string> receiveTask = ReceiveLoopAsync(channel, video, input, inputDesktop, features, display, view, token);
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

        if (input is not null)
        {
            if (inputDesktop is not null)
            {
                await inputDesktop.InvokeAsync(input.ReleaseAll);
            }
            else
            {
                input.ReleaseAll();
            }
        }

        Log.Info($"Video stream ended: {reason} ({video.Stats.TotalFrames} frames sent, {input?.EventCount ?? 0} input events)");
        return reason;
    }

    /// <summary>지금 보고 있는 모니터 영역 (모니터 선택·해상도 변경으로 바뀜)</summary>
    private sealed class ViewState(System.Drawing.Rectangle bounds)
    {
        public System.Drawing.Rectangle Bounds { get; set; } = bounds;

        public Dictionary<string, System.Drawing.Rectangle> Originals { get; } = new();
    }

    private async Task<string> ReceiveLoopAsync(
        MessageChannel channel,
        VideoStreamer video,
        InputInjector? input,
        DesktopThread? inputDesktop,
        SessionFeatures features,
        DisplaySession display,
        ViewState view,
        CancellationToken cancellationToken)
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
                        view.Bounds = bounds;
                        video.SwitchBounds(bounds);
                        input?.SetBounds(bounds); // 입력 좌표도 새 모니터 기준으로
                    }

                    break;

                case DisplayModesRequestMessage when _server.CanChangeResolution:
                    await SendDisplayAsync(channel, () =>
                    {
                        string device = DeviceFor(view.Bounds);
                        System.Drawing.Rectangle original = view.Originals.TryGetValue(device, out var saved) ? saved : view.Bounds;
                        return display.Describe(device, original);
                    }, cancellationToken);
                    break;

                case SetResolutionMessage set when _server.CanChangeResolution:
                    await SendDisplayAsync(channel, () =>
                    {
                        string device = DeviceFor(view.Bounds);
                        view.Originals.TryAdd(device, view.Bounds);
                        var (bounds, error) = display.Change(device, new DisplayMode(set.Width, set.Height));
                        if (bounds is not { } changed)
                        {
                            return new DisplayResultMessage(false, null, error);
                        }

                        view.Bounds = changed;
                        video.SwitchBounds(changed);
                        input?.SetBounds(changed);
                        Log.Info($"Resolution changed to {changed.Width}x{changed.Height} by client");
                        return new DisplayResultMessage(true, new DisplayMode(changed.Width, changed.Height));
                    }, cancellationToken);
                    break;

                case ByeMessage bye:
                    Log.Info($"Client said bye: {HostAuthenticator.Sanitize(bye.Reason)}");
                    return "Client가 연결을 종료했습니다.";

                case { } control when input is not null && InputInjector.IsInputMessage(control):
                    if (inputDesktop is not null)
                    {
                        await inputDesktop.InvokeAsync(() => input.TryHandle(control));
                    }
                    else
                    {
                        input.TryHandle(control);
                    }

                    break;

                default:
                    Log.Debug($"Ignored message from {_transport.RemoteDescription}: {message.Control?.GetType().Name ?? "unknown"}");
                    break;
            }
        }

        return "연결 종료";
    }

    /// <summary>모니터 영역에 해당하는 장치 이름. 여러 모니터를 합친 화면이면 해상도를 바꿀 수 없습니다.</summary>
    private string DeviceFor(System.Drawing.Rectangle bounds) =>
        _server.MonitorDeviceName(bounds) ?? throw new InvalidOperationException("모든 모니터 보기에서는 해상도를 바꿀 수 없습니다. 모니터 하나를 선택하세요.");

    private static async Task SendDisplayAsync(MessageChannel channel, Func<ControlMessage> action, CancellationToken cancellationToken)
    {
        ControlMessage reply;
        try
        {
            reply = action();
        }
        catch (InvalidOperationException exception)
        {
            reply = new DisplayResultMessage(false, null, exception.Message);
        }

        await channel.SendControlAsync(reply, cancellationToken);
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
