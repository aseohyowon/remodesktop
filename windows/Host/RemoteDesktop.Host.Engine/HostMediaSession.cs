using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport.WebRtc;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// STEP 14: 세션 하나의 WebRTC 미디어 트랙 협상과 전환.
///
///   Client ──media_offer──► Host: MediaTrackSender 생성, answer
///   Client ◄─media_answer── Host
///   양방향 media_ice (ICE 후보)
///   연결되면 Host ──media_state(active)──► Client, 영상·소리를 트랙으로 전환
///   실패/끊김이면 media_state(inactive, error) → 기존 채널(JPEG 프레임)로 계속
///
/// 연결이 안 되어도 세션은 계속되므로, 집 Wi-Fi·모바일 데이터 어디서든 최소한 JPEG 화면은 보입니다.
/// </summary>
internal sealed class HostMediaSession : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly MessageChannel _channel;
    private readonly HostTransport _transport;
    private readonly VideoStreamer _video;
    private readonly AudioStreamer? _audio;
    private readonly CancellationToken _cancellationToken;
    private MediaTrackSender? _sender;
    private int _deactivated;

    public HostMediaSession(MessageChannel channel, HostTransport transport, VideoStreamer video, AudioStreamer? audio, CancellationToken cancellationToken)
    {
        _channel = channel;
        _transport = transport;
        _video = video;
        _audio = audio;
        _cancellationToken = cancellationToken;
        video.TrackFailed += Deactivate;
    }

    public bool Active => _video.UsingTrack;

    public async Task HandleOfferAsync(MediaOfferMessage offer)
    {
        if (_sender is not null)
        {
            await SendAsync(new MediaStateMessage(false, "미디어 연결은 세션마다 한 번만 만들 수 있습니다."));
            return;
        }

        var sender = new MediaTrackSender(_transport.IceServers, ice => SendAsync(ice));
        _sender = sender;
        try
        {
            string answer = await sender.AcceptOfferAsync(offer.Sdp);
            await SendAsync(new MediaAnswerMessage(answer));
        }
        catch (ProtocolException exception)
        {
            Log.Warn($"Media track rejected: {exception.Message}");
            Deactivate(exception.Message);
            return;
        }

        _ = ActivateWhenConnectedAsync(sender);
    }

    public void HandleIce(MediaIceMessage ice) => _sender?.AddRemoteCandidate(ice);

    private async Task ActivateWhenConnectedAsync(MediaTrackSender sender)
    {
        try
        {
            await sender.WaitConnectedAsync(ConnectTimeout, _cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            if (!_cancellationToken.IsCancellationRequested)
            {
                Log.Warn($"Media track not connected: {exception.Message}");
                Deactivate(exception is TimeoutException ? "미디어 연결 시간 초과 (방화벽/NAT)" : exception.Message);
            }

            return;
        }

        if (Volatile.Read(ref _deactivated) == 1)
        {
            return;
        }

        sender.Closed += reason => Deactivate($"미디어 연결 끊김 ({reason})");
        _video.AttachTrack(sender);
        _audio?.AttachTrack(sender);
        Log.Info("Media track active: H.264 video + Opus audio over WebRTC");
        await SendAsync(new MediaStateMessage(true));
    }

    /// <summary>트랙을 끊고 기존 채널로 되돌립니다 (한 번만).</summary>
    private void Deactivate(string reason)
    {
        if (Interlocked.Exchange(ref _deactivated, 1) == 1)
        {
            return;
        }

        _video.DetachTrack();
        _audio?.DetachTrack();
        _ = SendAsync(new MediaStateMessage(false, reason));
        if (_sender is { } sender)
        {
            _ = Task.Run(sender.Dispose); // RTCPeerConnection 이벤트 스레드 안에서 바로 닫지 않도록
        }
    }

    private async Task SendAsync(ControlMessage message)
    {
        try
        {
            await _channel.SendControlAsync(message, _cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _deactivated, 1);
        _video.TrackFailed -= Deactivate;
        _video.DetachTrack();
        _audio?.DetachTrack();
        _sender?.Dispose();
    }
}
