namespace RemoteDesktop.Protocol;

public static class ProtocolConstants
{
    /// <summary>호환되지 않는 변경이 생기면 올립니다.</summary>
    public const int Version = 1;

    public const int DefaultPort = 50505;

    /// <summary>프레임 헤더(길이 4바이트 + 종류 1바이트) 다음에 오는 전체 크기 상한입니다.</summary>
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    /// <summary>JSON 제어 메시지는 작아야 합니다. 큰 JSON은 공격 또는 버그로 봅니다.</summary>
    public const int MaxControlMessageBytes = 64 * 1024;

    public const int NonceBytes = 32;
}
