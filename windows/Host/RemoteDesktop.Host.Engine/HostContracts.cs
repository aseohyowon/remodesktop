using System.Net;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// 인증된 암호화 연결 하나.
/// - LAN: TLS 스트림, channel_binding = Host 인증서 SHA-256
/// - 인터넷(STEP 7): WebRTC Data Channel, channel_binding = DTLS 지문 해시
/// 이 위의 인증·화면·입력 처리는 전송 방식과 관계없이 같습니다.
/// </summary>
public sealed record HostTransport(
    Stream Stream,
    byte[] ChannelBinding,
    IPAddress? RemoteAddress,
    string RemoteDescription,
    string Kind);

public sealed record ApprovalRequest(string ClientName, string Platform, string Remote, string Method);

public sealed record SessionInfo(string SessionId, string ClientName, string Platform, string Remote, string Method, string Transport);

/// <summary>
/// Host 화면(콘솔 또는 STEP 10의 Windows 앱)과 엔진 사이의 연결점.
/// </summary>
public interface IHostCallbacks
{
    /// <summary>접속 승인 요청. 30초 안에 답하지 않으면 거부로 처리합니다.</summary>
    Task<bool> ApproveAsync(ApprovalRequest request, CancellationToken cancellationToken);

    void OnSessionStarted(SessionInfo session);

    void OnSessionEnded(SessionInfo session, string reason);
}

/// <summary>아무것도 하지 않는 기본 구현 (승인 요청은 거부)</summary>
public class NullHostCallbacks : IHostCallbacks
{
    public virtual Task<bool> ApproveAsync(ApprovalRequest request, CancellationToken cancellationToken) => Task.FromResult(false);

    public virtual void OnSessionStarted(SessionInfo session)
    {
    }

    public virtual void OnSessionEnded(SessionInfo session, string reason)
    {
    }
}
