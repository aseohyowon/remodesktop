using System.Net;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace RemoteDesktop.Transport.WebRtc;

/// <summary>RTCP 수신 보고서 요약 (적응형 화질용)</summary>
public readonly record struct MediaReport(double RoundTripMs, double LossFraction);

/// <summary>
/// STEP 14: WebRTC 미디어 트랙 송신 (Host).
/// 모바일·맥 앱은 Data Channel의 H.264를 디코딩할 방법이 없으므로(네이티브 디코더가 Dart에 없음),
/// 화면은 H.264 영상 트랙, 소리는 Opus 오디오 트랙으로 보내 flutter_webrtc(=libwebrtc)의
/// 하드웨어 디코더(Android MediaCodec, iOS/macOS VideoToolbox)와 오디오 재생을 그대로 씁니다.
///
/// - SDP/ICE는 이미 인증된 채널 안의 media_offer/media_answer/media_ice로 주고받습니다.
/// - 영상은 RTP(90 kHz), 소리는 Opus 48 kHz 스테레오 20 ms. 전송 암호화는 DTLS-SRTP.
/// - 흐름 제어는 RTCP 수신 보고서(RTT, 손실률)와 PLI(키프레임 요청)로 합니다.
/// </summary>
public sealed class MediaTrackSender : IDisposable
{
    public const int VideoClockRate = 90000;
    public const int AudioClockRate = 48000;

    private readonly RTCPeerConnection _pc;
    private readonly Func<MediaIceMessage, Task> _sendIce;
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _closed;
    private bool _h264Negotiated;

    public MediaTrackSender(IceServerInfo[]? iceServers, Func<MediaIceMessage, Task> sendIce)
    {
        _sendIce = sendIce;
        var configuration = new RTCConfiguration
        {
            iceServers = (iceServers ?? [])
                .SelectMany(server => server.Urls.Select(url => new RTCIceServer { urls = url, username = server.Username, credential = server.Credential }))
                .ToList()
        };

        _pc = new RTCPeerConnection(configuration);

        // H.264 Constrained Baseline/packetization-mode=1: 모든 WebRTC 구현이 지원하는 조합
        var video = new VideoFormat(VideoCodecsEnum.H264, 102, VideoClockRate, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f");
        _pc.addTrack(new MediaStreamTrack(video, MediaStreamStatusEnum.SendOnly));
        var audio = new AudioFormat(AudioCodecsEnum.OPUS, 111, AudioClockRate, 2, "minptime=10;useinbandfec=1");
        _pc.addTrack(new MediaStreamTrack(audio, MediaStreamStatusEnum.SendOnly));

        _pc.onicecandidate += candidate =>
        {
            if (candidate is not null && !_closed)
            {
                _ = _sendIce(new MediaIceMessage(candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex));
            }
        };
        _pc.onconnectionstatechange += state =>
        {
            Log.Debug($"Media track: {state}");
            switch (state)
            {
                case RTCPeerConnectionState.connected:
                    _connected.TrySetResult();
                    break;
                case RTCPeerConnectionState.failed or RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected:
                    _connected.TrySetException(new IOException($"미디어 연결 실패 ({state})"));
                    if (!_closed)
                    {
                        Closed?.Invoke(state.ToString());
                    }

                    break;
            }
        };
        _pc.OnReceiveReport += OnReceiveReport;
        _pc.OnVideoFormatsNegotiated += formats => _h264Negotiated = formats.Any(f => f.Codec == VideoCodecsEnum.H264);
    }

    /// <summary>연결된 뒤 끊기거나 실패했을 때</summary>
    public event Action<string>? Closed;

    /// <summary>Client 디코더가 키프레임을 요청 (RTCP PLI/FIR)</summary>
    public event Action? KeyframeRequested;

    /// <summary>RTCP 수신 보고서 (영상 기준, 보통 1초 정도 간격)</summary>
    public event Action<MediaReport>? Report;

    public bool IsConnected => _pc.connectionState == RTCPeerConnectionState.connected;

    /// <summary>Client의 offer를 적용하고 answer SDP를 돌려줍니다.</summary>
    public async Task<string> AcceptOfferAsync(string offerSdp)
    {
        if (offerSdp.Length > 64 * 1024)
        {
            throw new ProtocolException("SDP가 너무 깁니다.");
        }

        SetDescriptionResultEnum result = _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp });
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new ProtocolException($"미디어 offer를 적용할 수 없습니다: {result}");
        }

        if (!_h264Negotiated)
        {
            throw new ProtocolException("Client가 H.264 영상 수신을 지원하지 않습니다.");
        }

        RTCSessionDescriptionInit answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer).ConfigureAwait(false);
        return AddKeyframeFeedback(answer.sdp);
    }

    /// <summary>
    /// answer의 H.264 payload마다 "nack pli", "ccm fir"을 선언합니다.
    /// SIPSorcery의 answer에는 transport-cc만 들어가는데, 그러면 libwebrtc(앱)는 패킷 손실 뒤 키프레임을 요청할 방법이 없어
    /// 화면이 멈춘 채로 남을 수 있습니다. 이 선언은 "상대가 PLI/FIR을 보내도 된다"는 뜻이고, 받으면 KeyframeRequested로 처리합니다.
    /// </summary>
    public static string AddKeyframeFeedback(string sdp)
    {
        string newline = sdp.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = sdp.Split(newline).ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith("a=rtpmap:", StringComparison.Ordinal) || !lines[i].Contains(" H264/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string payload = lines[i]["a=rtpmap:".Length..].Split(' ')[0];
            foreach (string feedback in new[] { "nack pli", "ccm fir" })
            {
                string line = $"a=rtcp-fb:{payload} {feedback}";
                if (!lines.Contains(line))
                {
                    lines.Insert(++i, line);
                }
            }
        }

        return string.Join(newline, lines);
    }

    public void AddRemoteCandidate(MediaIceMessage ice)
    {
        if (string.IsNullOrEmpty(ice.Candidate) || ice.Candidate.Length > 1024)
        {
            return;
        }

        _pc.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = ice.Candidate,
            sdpMid = ice.SdpMid ?? "0",
            sdpMLineIndex = (ushort)Math.Clamp(ice.SdpMlineIndex ?? 0, 0, 16)
        });
    }

    public Task WaitConnectedAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _connected.Task.WaitAsync(timeout, cancellationToken);

    /// <summary>H.264 Annex-B 액세스 유닛(프레임 하나) 전송. duration은 90 kHz 단위</summary>
    public void SendVideo(uint durationRtpUnits, byte[] annexB)
    {
        if (!_closed && IsConnected)
        {
            _pc.SendVideo(durationRtpUnits, annexB);
        }
    }

    /// <summary>Opus 패킷 하나 전송. duration은 48 kHz 단위 (20 ms = 960)</summary>
    public void SendAudio(uint durationRtpUnits, byte[] opus)
    {
        if (!_closed && IsConnected)
        {
            _pc.SendAudio(durationRtpUnits, opus);
        }
    }

    private void OnReceiveReport(IPEndPoint remote, SDPMediaTypesEnum media, RTCPCompoundPacket packet)
    {
        if (media != SDPMediaTypesEnum.video)
        {
            return;
        }

        if (packet.Feedback is { } feedback
            && feedback.Header.PayloadFeedbackMessageType is PSFBFeedbackTypesEnum.PLI or PSFBFeedbackTypesEnum.FIR)
        {
            KeyframeRequested?.Invoke();
        }

        ReceptionReportSample? sample = packet.ReceiverReport?.ReceptionReports?.FirstOrDefault()
            ?? packet.SenderReport?.ReceptionReports?.FirstOrDefault();
        if (sample is not null)
        {
            Report?.Invoke(new MediaReport(RoundTripMs(sample.LastSenderReportTimestamp, sample.DelaySinceLastSenderReport), sample.FractionLost / 256.0));
        }
    }

    /// <summary>RFC 3550 6.4.1: RTT = 지금(NTP 가운데 32비트) - LSR - DLSR (1/65536초 단위). 아직 SR을 받지 못했으면 0</summary>
    public static double RoundTripMs(uint lastSenderReport, uint delaySinceLastSenderReport, DateTime? now = null)
    {
        if (lastSenderReport == 0)
        {
            return 0;
        }

        DateTime time = now ?? DateTime.UtcNow;
        double ntpSeconds = (time - new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        uint middle = (uint)((ulong)(ntpSeconds * 65536) & 0xFFFFFFFF);
        uint rtt = unchecked(middle - lastSenderReport - delaySinceLastSenderReport);
        double ms = rtt / 65536.0 * 1000;
        return ms is >= 0 and < 10000 ? ms : 0;
    }

    public void Dispose()
    {
        _closed = true;
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
