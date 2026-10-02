using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteDesktop.Protocol;

/// <summary>
/// 시그널링 서버(WebSocket)와 주고받는 메시지 (docs/protocol.md 7장)
/// 서버는 연결 협상(SDP/ICE)만 전달하고, 화면·입력 데이터는 보지 못합니다(WebRTC로 직접 전달).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HostHelloSignal), "host_hello")]
[JsonDerivedType(typeof(HostChallengeSignal), "host_challenge")]
[JsonDerivedType(typeof(HostAuthSignal), "host_auth")]
[JsonDerivedType(typeof(HostRegisteredSignal), "host_registered")]
[JsonDerivedType(typeof(ConnectSignal), "connect")]
[JsonDerivedType(typeof(ConnectRequestSignal), "connect_request")]
[JsonDerivedType(typeof(ConnectingSignal), "connecting")]
[JsonDerivedType(typeof(SdpSignal), "sdp")]
[JsonDerivedType(typeof(IceSignal), "ice")]
[JsonDerivedType(typeof(SessionEndSignal), "session_end")]
[JsonDerivedType(typeof(StatusSignal), "status")]
[JsonDerivedType(typeof(StatusResultSignal), "status_result")]
[JsonDerivedType(typeof(ErrorSignal), "error")]
public abstract record SignalMessage;

/// <summary>Host → 서버. public_key: ECDSA P-256 공개 키 (SubjectPublicKeyInfo, Base64)</summary>
public sealed record HostHelloSignal(string HostId, string PublicKey) : SignalMessage;

public sealed record HostChallengeSignal(string Nonce) : SignalMessage;

/// <summary>Host → 서버. signature = ECDSA-SHA256("remodesktop-signal-v1:" + host_id + ":" + nonce)</summary>
public sealed record HostAuthSignal(string Signature) : SignalMessage;

public sealed record HostRegisteredSignal(string HostId) : SignalMessage;

/// <summary>Client → 서버: 이 Host에 연결하고 싶음</summary>
public sealed record ConnectSignal(string HostId) : SignalMessage;

/// <summary>서버 → Host: 새 연결 요청. Host가 offer를 만듭니다.</summary>
public sealed record ConnectRequestSignal(string SessionId, string? ClientAddress, IceServerInfo[] IceServers) : SignalMessage;

/// <summary>서버 → Client: Host에 요청을 전달했음. 곧 offer가 옵니다.</summary>
public sealed record ConnectingSignal(string SessionId, IceServerInfo[] IceServers) : SignalMessage;

/// <summary>sdp_type: "offer"(Host → Client) | "answer"(Client → Host)</summary>
public sealed record SdpSignal(string SessionId, string SdpType, string Sdp) : SignalMessage;

public sealed record IceSignal(string SessionId, string Candidate, string? SdpMid, int? SdpMlineIndex) : SignalMessage;

public sealed record SessionEndSignal(string SessionId, string? Reason = null) : SignalMessage;

public sealed record StatusSignal(string[] HostIds) : SignalMessage;

public sealed record StatusResultSignal(Dictionary<string, bool> Online) : SignalMessage;

/// <summary>code: offline | rate_limited | bad_request | unauthorized | session_not_found | host_id_taken</summary>
public sealed record ErrorSignal(string Code, string Message, string? SessionId = null) : SignalMessage;

/// <summary>WebRTC ICE 서버 (STUN/TURN). TURN은 서버가 발급한 임시 자격 증명</summary>
public sealed record IceServerInfo(string[] Urls, string? Username = null, string? Credential = null);

public static class SignalingJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public const int MaxMessageBytes = 64 * 1024;

    public static string Serialize(SignalMessage message) => JsonSerializer.Serialize(message, Options);

    /// <summary>모르는 type이면 null</summary>
    public static SignalMessage? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SignalMessage>(json, Options);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public static byte[] HostAuthPayload(string hostId, string nonce) =>
        Encoding.UTF8.GetBytes($"remodesktop-signal-v1:{hostId}:{nonce}");
}

/// <summary>
/// WebRTC 연결의 channel binding.
/// DTLS 인증서 지문(SDP의 a=fingerprint)을 Host(offer)·Client(answer) 순서로 섞은 해시입니다.
/// 시그널링 서버가 SDP를 바꿔치기하면(중간자) 양쪽이 계산한 값이 달라져 인증이 실패합니다.
/// </summary>
public static class WebRtcBinding
{
    public static string? ExtractFingerprint(string sdp)
    {
        foreach (string raw in sdp.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("a=fingerprint:sha-256 ", StringComparison.OrdinalIgnoreCase))
            {
                return line["a=fingerprint:sha-256 ".Length..].Trim().ToUpperInvariant();
            }
        }

        return null;
    }

    public static byte[] Compute(string hostOfferSdp, string clientAnswerSdp)
    {
        string host = ExtractFingerprint(hostOfferSdp) ?? throw new ProtocolException("offer에 DTLS 지문이 없습니다.");
        string client = ExtractFingerprint(clientAnswerSdp) ?? throw new ProtocolException("answer에 DTLS 지문이 없습니다.");
        return SHA256.HashData(Encoding.ASCII.GetBytes($"remodesktop-webrtc-v1|{host}|{client}"));
    }
}
