using System.Text.Json;
using RemoteDesktop.Core;

namespace RemoteDesktop.Client;

internal enum HostTrust
{
    Unknown,
    Trusted,
    Mismatch
}

/// <summary>
/// 처음 연결한 Host의 인증서 지문을 저장합니다(TOFU: Trust On First Use).
/// 같은 Host ID인데 지문이 바뀌면 중간자 공격일 수 있으므로 연결을 거부합니다.
/// 파일: %LOCALAPPDATA%\RemoteDesktop\known_hosts.json
/// </summary>
internal static class KnownHostsStore
{
    private const string FileName = "known_hosts.json";
    private static readonly object Sync = new();

    /// <summary>테스트에서 실제 사용자 파일 대신 임시 파일을 쓰기 위한 경로</summary>
    internal static string? PathOverride { get; set; }

    private static string FilePath => PathOverride ?? AppPaths.GetFile(FileName);

    public static HostTrust Check(string hostId, string fingerprintHex)
    {
        Dictionary<string, string> hosts;
        lock (Sync)
        {
            hosts = Load();
        }

        if (!hosts.TryGetValue(hostId, out string? saved))
        {
            return HostTrust.Unknown;
        }

        return string.Equals(saved, fingerprintHex, StringComparison.OrdinalIgnoreCase)
            ? HostTrust.Trusted
            : HostTrust.Mismatch;
    }

    public static void Save(string hostId, string fingerprintHex)
    {
        lock (Sync)
        {
            Dictionary<string, string> hosts = Load();
            hosts[hostId] = fingerprintHex;
            AtomicFile.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(hosts, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static Dictionary<string, string> Load()
    {
        string path = FilePath;
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            Log.Warn($"{path} 파일이 손상되어 무시합니다.");
            return new Dictionary<string, string>();
        }
    }
}
