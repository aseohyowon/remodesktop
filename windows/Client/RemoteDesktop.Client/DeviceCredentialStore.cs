using System.Text.Json;
using RemoteDesktop.Core;

namespace RemoteDesktop.Client;

/// <summary>
/// "이 PC 기억하기"로 받은 신뢰된 장치 자격 증명.
/// 파일: %LOCALAPPDATA%\RemoteDesktop\devices.json — 비밀키는 DPAPI로 암호화되어 이 Windows 사용자만 읽을 수 있습니다.
/// </summary>
public sealed class DeviceCredentialStore
{
    private readonly string _path;
    private readonly object _sync = new();

    public DeviceCredentialStore(string? path = null)
    {
        _path = path ?? AppPaths.GetFile("devices.json");
    }

    public (string DeviceId, byte[] Secret)? Get(string hostId)
    {
        lock (_sync)
        {
            if (!Load().TryGetValue(hostId, out Entry? entry))
            {
                return null;
            }

            try
            {
                return (entry.DeviceId, SecretProtector.Unprotect(entry.SecretProtected));
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return null; // 다른 Windows 사용자/PC에서 복사된 파일
            }
        }
    }

    public bool Has(string hostId)
    {
        lock (_sync)
        {
            return Load().ContainsKey(hostId);
        }
    }

    public void Save(string hostId, string deviceId, byte[] secret)
    {
        lock (_sync)
        {
            var all = Load();
            all[hostId] = new Entry(deviceId, SecretProtector.Protect(secret));
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public void Remove(string hostId)
    {
        lock (_sync)
        {
            var all = Load();
            if (all.Remove(hostId))
            {
                AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }

    private Dictionary<string, Entry> Load()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, Entry>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path)) ?? new Dictionary<string, Entry>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, Entry>();
        }
    }

    private sealed record Entry(string DeviceId, string SecretProtected);
}
