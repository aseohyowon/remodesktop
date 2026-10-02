namespace RemoteDesktop.Protocol;

/// <summary>상대방이 프로토콜 규칙을 어겼을 때 발생합니다. 이 경우 연결을 끊습니다.</summary>
public sealed class ProtocolException(string message) : Exception(message);
