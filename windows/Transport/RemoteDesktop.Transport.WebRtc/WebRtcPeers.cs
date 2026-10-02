using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using SIPSorcery.Net;

namespace RemoteDesktop.Transport.WebRtc;

/// <summary>WebRTC로 연결된 암호화 스트림 + channel binding</summary>
public sealed record WebRtcConnection(Stream Stream, byte[] ChannelBinding);

/// <summary>
/// WebRTC 연결 한쪽. Host는 offer를 만들고(Data Channel 생성), Client는 answer를 만듭니다.
/// 서로의 SDP/ICE는 시그널링 서버로 주고받습니다.
/// </summary>
public sealed class WebRtcPeer : IDisposable
{
    public const string DataChannelLabel = "remodesktop";
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);

    private readonly RTCPeerConnection _pc;
    private readonly bool _isHost;
    private readonly Func<SignalMessage, Task> _send;
    private readonly TaskCompletionSource<DataChannelStream> _channelOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _localSdp;
    private string? _remoteSdp;

    public WebRtcPeer(string sessionId, bool isHost, IceServerInfo[] iceServers, Func<SignalMessage, Task> send)
    {
        SessionId = sessionId;
        _isHost = isHost;
        _send = send;

        var configuration = new RTCConfiguration
        {
            iceServers = iceServers
                .SelectMany(server => server.Urls.Select(url => new RTCIceServer
                {
                    urls = url,
                    username = server.Username,
                    credential = server.Credential
                }))
                .ToList()
        };

        _pc = new RTCPeerConnection(configuration);
        _pc.onicecandidate += candidate =>
        {
            if (candidate is not null)
            {
                _ = _send(new IceSignal(SessionId, candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex));
            }
        };
        _pc.onconnectionstatechange += state =>
        {
            Log.Debug($"WebRTC {SessionId}: {state}");
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
            {
                _channelOpen.TrySetException(new IOException($"WebRTC 연결 실패 ({state}). 방화벽/NAT 환경이면 TURN 서버가 필요할 수 있습니다."));
            }
        };

        if (!isHost)
        {
            _pc.ondatachannel += channel =>
            {
                if (channel.label != DataChannelLabel)
                {
                    return;
                }

                Attach(channel);
            };
        }
    }

    public string SessionId { get; }

    /// <summary>Host: Data Channel과 offer를 만들어 보냅니다.</summary>
    public async Task StartAsHostAsync()
    {
        RTCDataChannel channel = await _pc.createDataChannel(DataChannelLabel, new RTCDataChannelInit { ordered = true }).ConfigureAwait(false);
        Attach(channel);

        RTCSessionDescriptionInit offer = _pc.createOffer(null);
        await _pc.setLocalDescription(offer).ConfigureAwait(false);
        _localSdp = offer.sdp;
        await _send(new SdpSignal(SessionId, "offer", offer.sdp)).ConfigureAwait(false);
    }

    /// <summary>상대의 SDP/ICE 처리. Client는 offer를 받으면 answer를 보냅니다.</summary>
    public async Task HandleAsync(SignalMessage message)
    {
        switch (message)
        {
            case SdpSignal { SdpType: "offer" } offer when !_isHost:
                _remoteSdp = offer.Sdp;
                var result = _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offer.Sdp });
                if (result != SetDescriptionResultEnum.OK)
                {
                    throw new ProtocolException($"offer를 적용할 수 없습니다: {result}");
                }

                RTCSessionDescriptionInit answer = _pc.createAnswer(null);
                await _pc.setLocalDescription(answer).ConfigureAwait(false);
                _localSdp = answer.sdp;
                await _send(new SdpSignal(SessionId, "answer", answer.sdp)).ConfigureAwait(false);
                break;

            case SdpSignal { SdpType: "answer" } answerSignal when _isHost:
                _remoteSdp = answerSignal.Sdp;
                var applied = _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answerSignal.Sdp });
                if (applied != SetDescriptionResultEnum.OK)
                {
                    throw new ProtocolException($"answer를 적용할 수 없습니다: {applied}");
                }

                break;

            case IceSignal ice when !string.IsNullOrEmpty(ice.Candidate):
                _pc.addIceCandidate(new RTCIceCandidateInit
                {
                    candidate = ice.Candidate,
                    sdpMid = ice.SdpMid ?? "0",
                    sdpMLineIndex = (ushort)(ice.SdpMlineIndex ?? 0)
                });
                break;

            case SessionEndSignal end:
                // 상대가 연결 후 시그널링 연결을 닫은 경우에도 session_end가 옵니다. 이미 P2P로 연결됐으면 무시합니다.
                if (_pc.connectionState is not (RTCPeerConnectionState.connected or RTCPeerConnectionState.connecting))
                {
                    _channelOpen.TrySetException(new IOException($"상대방이 연결을 취소했습니다: {end.Reason}"));
                }

                break;
        }
    }

    /// <summary>Data Channel이 열릴 때까지 기다린 뒤 스트림을 반환합니다.</summary>
    public async Task<WebRtcConnection> WaitOpenAsync(CancellationToken cancellationToken)
    {
        DataChannelStream stream = await _channelOpen.Task.WaitAsync(OpenTimeout, cancellationToken).ConfigureAwait(false);
        string hostSdp = (_isHost ? _localSdp : _remoteSdp) ?? throw new ProtocolException("offer가 없습니다.");
        string clientSdp = (_isHost ? _remoteSdp : _localSdp) ?? throw new ProtocolException("answer가 없습니다.");
        byte[] binding = WebRtcBinding.Compute(hostSdp, clientSdp);
        return new WebRtcConnection(stream, binding);
    }

    /// <summary>
    /// 채널 객체를 받자마자 스트림을 연결합니다. (열린 직후 도착하는 첫 메시지를 놓치지 않도록)
    /// </summary>
    private void Attach(RTCDataChannel channel)
    {
        var stream = new DataChannelStream(channel, Dispose);
        if (channel.readyState == RTCDataChannelState.open)
        {
            _channelOpen.TrySetResult(stream);
        }
        else
        {
            channel.onopen += () => _channelOpen.TrySetResult(stream);
        }
    }

    public void Dispose()
    {
        try
        {
            _pc.close();
        }
        catch
        {
        }

        _pc.Dispose();
    }
}

/// <summary>Client: Host ID로 시그널링 서버를 거쳐 WebRTC 연결을 맺습니다.</summary>
public static class WebRtcClientConnector
{
    public static async Task<WebRtcConnection> ConnectAsync(Uri signalingServer, string hostId, CancellationToken cancellationToken)
    {
        var signaling = await SignalingSocket.ConnectAsync(signalingServer, cancellationToken).ConfigureAwait(false);
        WebRtcPeer? peer = null;
        try
        {
            await signaling.SendAsync(new ConnectSignal(hostId), cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            // 협상 메시지를 처리하는 동안 Data Channel이 열리기를 기다립니다.
            Task<WebRtcConnection>? open = null;
            while (open is null || !open.IsCompleted)
            {
                Task<SignalMessage?> receive = signaling.ReceiveAsync(timeout.Token);
                Task finished = open is null ? receive : await Task.WhenAny(receive, open).ConfigureAwait(false);
                if (finished == open)
                {
                    break;
                }

                SignalMessage? message = await receive.ConfigureAwait(false)
                    ?? throw new IOException("시그널링 서버 연결이 끊겼습니다.");

                switch (message)
                {
                    case ErrorSignal error:
                        throw new IOException(error.Message);

                    case ConnectingSignal connecting:
                        peer = new WebRtcPeer(connecting.SessionId, isHost: false, connecting.IceServers,
                            m => signaling.SendAsync(m, CancellationToken.None));
                        open = peer.WaitOpenAsync(timeout.Token);
                        break;

                    case SdpSignal or IceSignal or SessionEndSignal when peer is not null:
                        await peer.HandleAsync(message).ConfigureAwait(false);
                        break;
                }
            }

            WebRtcConnection connection = await open!.ConfigureAwait(false);

            // 연결이 끝난 뒤 늦게 오는 ICE 후보는 무시하고 시그널링 연결을 닫습니다. (화면 데이터는 P2P)
            signaling.Dispose();
            return connection;
        }
        catch
        {
            peer?.Dispose();
            signaling.Dispose();
            throw;
        }
    }
}
