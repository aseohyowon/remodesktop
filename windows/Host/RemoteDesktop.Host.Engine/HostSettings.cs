using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// Host 설정 파일: %LOCALAPPDATA%\RemoteDesktop\host.json
/// 비밀값(비밀번호 키, 장치 비밀키, TOTP 비밀키, 시그널링 키)은 모두 DPAPI로 암호화해서 저장합니다.
/// 비밀번호 자체는 어디에도 저장하지 않습니다(PBKDF2 결과만 저장).
/// </summary>
public sealed class HostSettings
{
    public const int DefaultPasswordIterations = 200_000;
    public const int MinPasswordLength = 8;
    public static readonly TimeSpan DeviceExpiry = TimeSpan.FromDays(90);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _sync = new();
    private readonly string _path;
    private Data _data;
    private DateTime _loadedWriteTime;

    private HostSettings(string path, Data data)
    {
        _path = path;
        _data = data;
    }

    public string HostId => _data.HostId;

    public bool HasPassword
    {
        get
        {
            Refresh();
            return _data.Password is not null;
        }
    }

    public bool TotpEnabled
    {
        get
        {
            Refresh();
            return _data.Totp is not null;
        }
    }

    /// <summary>
    /// 다른 프로세스(예: 실행 중인 Host 옆에서 쓴 "devices revoke" 명령)나 다른 인스턴스가 파일을 바꿨으면 다시 읽습니다.
    /// 덕분에 장치 등록 해제·비밀번호 변경이 Host를 다시 시작하지 않아도 바로 적용됩니다.
    /// </summary>
    private void Refresh()
    {
        lock (_sync)
        {
            try
            {
                DateTime writeTime = File.GetLastWriteTimeUtc(_path);
                if (writeTime == _loadedWriteTime || !File.Exists(_path))
                {
                    return;
                }

                Data? fresh = JsonSerializer.Deserialize<Data>(File.ReadAllText(_path), JsonOptions);
                if (fresh is not null && fresh.HostId == _data.HostId)
                {
                    _data = fresh;
                }

                _loadedWriteTime = writeTime;
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                // 다른 쪽이 쓰는 중이면 다음에 다시 읽음
            }
        }
    }

    public static HostSettings Load(string? path = null)
    {
        path ??= AppPaths.GetFile("host.json");
        Data? data = null;

        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                data = JsonSerializer.Deserialize<Data>(json, JsonOptions);

                // STEP 2~5 형식({"HostId": "..."}) 호환
                if (data?.HostId is null or "")
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.TryGetProperty("HostId", out JsonElement legacy))
                    {
                        data = new Data { HostId = legacy.GetString() ?? "" };
                    }
                }
            }
            catch (JsonException)
            {
                Log.Warn($"{path} 파일이 손상되어 새로 만듭니다.");
            }
        }

        data ??= new Data();
        if (string.IsNullOrEmpty(data.HostId))
        {
            data.HostId = "HOST-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));
        }

        var settings = new HostSettings(path, data);
        settings.Save();
        return settings;
    }

    // ---------------- 비밀번호 ----------------

    public void SetPassword(string password)
    {
        if (password.Length < MinPasswordLength)
        {
            throw new ArgumentException($"비밀번호는 {MinPasswordLength}자 이상이어야 합니다.");
        }

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] key = AuthProof.DerivePasswordKey(password, salt, DefaultPasswordIterations);

        Refresh();
        lock (_sync)
        {
            _data.Password = new PasswordData
            {
                Salt = Convert.ToBase64String(salt),
                Iterations = DefaultPasswordIterations,
                KeyProtected = SecretProtector.Protect(key)
            };
            Save();
        }

        CryptographicOperations.ZeroMemory(key);
    }

    public void ClearPassword()
    {
        Refresh();
        lock (_sync)
        {
            _data.Password = null;
            Save();
        }
    }

    /// <summary>(salt, iterations, key). 비밀번호가 없으면 null.</summary>
    public (byte[] Salt, int Iterations, byte[] Key)? GetPasswordKey()
    {
        Refresh();
        PasswordData? password;
        lock (_sync)
        {
            password = _data.Password;
        }

        if (password is null)
        {
            return null;
        }

        return (Convert.FromBase64String(password.Salt), password.Iterations, SecretProtector.Unprotect(password.KeyProtected));
    }

    // ---------------- 신뢰된 장치 ----------------

    public (string DeviceId, byte[] Secret) RegisterDevice(string name)
    {
        string id = "DEV-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        byte[] secret = RandomNumberGenerator.GetBytes(AuthProof.KeyBytes);

        Refresh();
        lock (_sync)
        {
            _data.Devices.Add(new DeviceData
            {
                Id = id,
                Name = Truncate(name, 64),
                SecretProtected = SecretProtector.Protect(secret),
                Created = DateTimeOffset.UtcNow,
                LastUsed = DateTimeOffset.UtcNow
            });
            Save();
        }

        return (id, secret);
    }

    /// <summary>유효한(만료되지 않은) 장치의 비밀키. 없으면 null.</summary>
    public byte[]? GetDeviceSecret(string? deviceId)
    {
        if (deviceId is null)
        {
            return null;
        }

        Refresh();
        lock (_sync)
        {
            DeviceData? device = _data.Devices.FirstOrDefault(d => d.Id == deviceId);
            if (device is null || DateTimeOffset.UtcNow - device.LastUsed > DeviceExpiry)
            {
                return null;
            }

            return SecretProtector.Unprotect(device.SecretProtected);
        }
    }

    public void TouchDevice(string deviceId)
    {
        Refresh();
        lock (_sync)
        {
            DeviceData? device = _data.Devices.FirstOrDefault(d => d.Id == deviceId);
            if (device is not null)
            {
                device.LastUsed = DateTimeOffset.UtcNow;
                Save();
            }
        }
    }

    public bool HasDevices
    {
        get
        {
            Refresh();
            lock (_sync)
            {
                return _data.Devices.Count > 0;
            }
        }
    }

    public IReadOnlyList<DeviceInfo> ListDevices()
    {
        Refresh();
        lock (_sync)
        {
            return _data.Devices
                .Select(d => new DeviceInfo(d.Id, d.Name, d.Created, d.LastUsed, DateTimeOffset.UtcNow - d.LastUsed > DeviceExpiry))
                .ToList();
        }
    }

    /// <summary>"all"이면 전체 해제. 해제한 개수를 반환합니다.</summary>
    public int RevokeDevice(string deviceIdOrAll)
    {
        Refresh();
        lock (_sync)
        {
            int removed = deviceIdOrAll.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? _data.Devices.RemoveAll(_ => true)
                : _data.Devices.RemoveAll(d => d.Id.Equals(deviceIdOrAll, StringComparison.OrdinalIgnoreCase));
            Save();
            return removed;
        }
    }

    // ---------------- TOTP (2단계 인증) ----------------

    public void SetTotpSecret(byte[] secret)
    {
        Refresh();
        lock (_sync)
        {
            _data.Totp = new TotpData { SecretProtected = SecretProtector.Protect(secret) };
            Save();
        }
    }

    public void ClearTotp()
    {
        Refresh();
        lock (_sync)
        {
            _data.Totp = null;
            Save();
        }
    }

    public byte[]? GetTotpSecret()
    {
        Refresh();
        lock (_sync)
        {
            return _data.Totp is null ? null : SecretProtector.Unprotect(_data.Totp.SecretProtected);
        }
    }

    // ---------------- 시그널링 서버 등록 키 (STEP 7) ----------------

    /// <summary>시그널링 서버에 Host ID 소유를 증명하는 ECDSA P-256 키 (PKCS#8). 없으면 새로 만듭니다.</summary>
    public ECDsa GetOrCreateSignalingKey()
    {
        Refresh();
        lock (_sync)
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            if (_data.SignalingKeyProtected is { } existing)
            {
                key.ImportPkcs8PrivateKey(SecretProtector.Unprotect(existing), out _);
                return key;
            }

            _data.SignalingKeyProtected = SecretProtector.Protect(key.ExportPkcs8PrivateKey());
            Save();
            return key;
        }
    }

    private void Save()
    {
        lock (_sync)
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
            _loadedWriteTime = File.GetLastWriteTimeUtc(_path);
        }
    }

    private static string Truncate(string value, int max)
    {
        string cleaned = new(value.Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Length <= max ? cleaned : cleaned[..max];
    }

    public sealed record DeviceInfo(string Id, string Name, DateTimeOffset Created, DateTimeOffset LastUsed, bool Expired);

    private sealed class Data
    {
        public string HostId { get; set; } = "";
        public PasswordData? Password { get; set; }
        public TotpData? Totp { get; set; }
        public string? SignalingKeyProtected { get; set; }
        public List<DeviceData> Devices { get; set; } = new();
    }

    private sealed class PasswordData
    {
        public string Salt { get; set; } = "";
        public int Iterations { get; set; }
        public string KeyProtected { get; set; } = "";
    }

    private sealed class TotpData
    {
        public string SecretProtected { get; set; } = "";
    }

    private sealed class DeviceData
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string SecretProtected { get; set; } = "";
        public DateTimeOffset Created { get; set; }
        public DateTimeOffset LastUsed { get; set; }
    }
}
