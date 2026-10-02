using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteDesktop.Core;

namespace RemoteDesktop.Client;

/// <summary>
/// 앱 설정: %LOCALAPPDATA%\RemoteDesktop\client.json
/// 비밀번호와 접속 코드는 저장하지 않습니다.
/// </summary>
public sealed class ClientSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>시그널링 서버 (예: wss://signal.example.com/ws)</summary>
    public string? SignalingServer { get; set; }

    public List<SavedHost> Hosts { get; set; } = new();

    /// <summary>"내 PC 원격 허용" 설정</summary>
    public HostPreferences HostPrefs { get; set; } = new();

    public static string FilePath => AppPaths.GetFile("client.json");

    public static ClientSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new ClientSettings()
                : new ClientSettings();
        }
        catch (JsonException)
        {
            return new ClientSettings();
        }
    }

    public void Save() => AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));

    public Uri? SignalingUri =>
        Uri.TryCreate(SignalingServer, UriKind.Absolute, out Uri? uri) && uri.Scheme is "ws" or "wss" ? uri : null;
}

/// <summary>등록된 PC. Address가 있으면 LAN, HostId만 있으면 인터넷(시그널링)으로 연결합니다.</summary>
public sealed class SavedHost
{
    public string Name { get; set; } = "";

    public string? Address { get; set; }

    public int Port { get; set; } = Protocol.ProtocolConstants.DefaultPort;

    public string? HostId { get; set; }

    [JsonIgnore]
    public bool IsInternet => string.IsNullOrWhiteSpace(Address) && !string.IsNullOrWhiteSpace(HostId);
}

public sealed class HostPreferences
{
    /// <summary>[내 PC를 원격으로 허용] 켜짐</summary>
    public bool AllowRemote { get; set; }

    public bool RequireApproval { get; set; } = true;

    public bool ViewOnly { get; set; }

    /// <summary>시그널링 서버로 인터넷 연결도 받기</summary>
    public bool AllowInternet { get; set; }

    public bool AllowPower { get; set; }

    public bool AllowClipboard { get; set; } = true;

    public bool AllowFileTransfer { get; set; } = true;

    public bool AllowAudio { get; set; } = true;

    public string? SharedFolder { get; set; }

    public bool StartWithWindows { get; set; }

    public bool MinimizeToTray { get; set; } = true;
}
