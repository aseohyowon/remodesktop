using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RemoteDesktop.Protocol;

namespace Signaling.Server;

public sealed class SignalingOptions
{
    public string DataDirectory { get; set; } = "data";

    public string[] StunUrls { get; set; } = ["stun:stun.l.google.com:19302"];

    /// <summary>예: turn:turn.example.com:3478?transport=udp, turns:turn.example.com:5349</summary>
    public string[] TurnUrls { get; set; } = [];

    /// <summary>coturn의 static-auth-secret과 같은 값 (TURN REST API 방식 임시 자격 증명)</summary>
    public string TurnSecret { get; set; } = "";

    public int TurnCredentialSeconds { get; set; } = 3600;

    public int ConnectRequestsPerMinutePerAddress { get; set; } = 20;

    public int MessagesPerTenSecondsPerConnection { get; set; } = 200;
}

/// <summary>WebSocket 연결 하나 (Host 또는 Client)</summary>
public sealed class SignalConnection(WebSocket socket, IPAddress? address)
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public IPAddress? Address { get; } = address;

    /// <summary>인증된 Host이면 Host ID</summary>
    public string? HostId { get; set; }

    public async Task SendAsync(SignalMessage message, CancellationToken cancellationToken = default)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(SignalingJson.Serialize(message));
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task CloseAsync() =>
        socket.State == WebSocketState.Open
            ? socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None)
            : Task.CompletedTask;
}

/// <summary>
/// Host ID ↔ 공개 키 (처음 등록한 Host가 그 ID를 소유) + 현재 온라인인 Host 연결.
/// 파일: {DataDirectory}/hosts.json — 공개 키만 저장하므로 유출되어도 Host를 사칭할 수 없습니다.
/// </summary>
public sealed class HostRegistry
{
    private readonly object _sync = new();
    private readonly string _path;
    private readonly Dictionary<string, string> _publicKeys;
    private readonly ConcurrentDictionary<string, SignalConnection> _online = new();

    public HostRegistry(IOptions<SignalingOptions> options)
    {
        Directory.CreateDirectory(options.Value.DataDirectory);
        _path = Path.Combine(options.Value.DataDirectory, "hosts.json");
        _publicKeys = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new()
            : new();
    }

    /// <summary>등록된 공개 키와 같은지 확인. 처음 보는 Host ID면 등록합니다.</summary>
    public bool TryClaim(string hostId, string publicKey)
    {
        lock (_sync)
        {
            if (_publicKeys.TryGetValue(hostId, out string? existing))
            {
                return existing == publicKey;
            }

            _publicKeys[hostId] = publicKey;
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_publicKeys));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
    }

    public void SetOnline(string hostId, SignalConnection connection)
    {
        if (_online.TryGetValue(hostId, out var previous) && previous != connection)
        {
            _ = previous.CloseAsync(); // 같은 Host가 다시 접속하면 이전 연결을 닫음
        }

        _online[hostId] = connection;
    }

    public void SetOffline(SignalConnection connection)
    {
        if (connection.HostId is { } hostId)
        {
            _online.TryRemove(new KeyValuePair<string, SignalConnection>(hostId, connection));
        }
    }

    public bool IsOnline(string hostId) => _online.ContainsKey(hostId);

    public SignalConnection? Get(string hostId) => _online.TryGetValue(hostId, out var connection) ? connection : null;
}

/// <summary>진행 중인 연결 협상 (session_id → Host 연결, Client 연결)</summary>
public sealed class SessionBroker
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public sealed record Session(string Id, SignalConnection Host, SignalConnection Client, DateTime Created);

    public Session Create(SignalConnection host, SignalConnection client)
    {
        Prune();
        string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var session = new Session(id, host, client, DateTime.UtcNow);
        _sessions[id] = session;
        return session;
    }

    public Session? Get(string? id) => id is not null && _sessions.TryGetValue(id, out var session) ? session : null;

    public void Remove(string id) => _sessions.TryRemove(id, out _);

    public IEnumerable<Session> For(SignalConnection connection) =>
        _sessions.Values.Where(s => s.Host == connection || s.Client == connection).ToList();

    private void Prune()
    {
        DateTime now = DateTime.UtcNow;
        foreach (var session in _sessions.Values)
        {
            if (now - session.Created > SessionLifetime)
            {
                _sessions.TryRemove(session.Id, out _);
            }
        }
    }
}

/// <summary>간단한 고정 창(fixed window) 속도 제한</summary>
public sealed class RateLimiter(IOptions<SignalingOptions> options)
{
    private readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count)> _counters = new();

    public bool AllowConnect(IPAddress? address) =>
        Allow($"connect:{address}", options.Value.ConnectRequestsPerMinutePerAddress, TimeSpan.FromMinutes(1));

    public bool AllowStatus(IPAddress? address) => Allow($"status:{address}", 60, TimeSpan.FromMinutes(1));

    public bool AllowMessage(string connectionId) =>
        Allow($"msg:{connectionId}", options.Value.MessagesPerTenSecondsPerConnection, TimeSpan.FromSeconds(10));

    public void Forget(string connectionId) => _counters.TryRemove($"msg:{connectionId}", out _);

    private bool Allow(string key, int limit, TimeSpan window)
    {
        DateTime now = DateTime.UtcNow;
        if (_counters.Count > 10_000)
        {
            // 오래된 항목 정리 (주소가 많아져도 메모리가 계속 늘지 않게)
            foreach (var (oldKey, value) in _counters)
            {
                if (now - value.WindowStart > TimeSpan.FromMinutes(10))
                {
                    _counters.TryRemove(oldKey, out _);
                }
            }
        }

        var updated = _counters.AddOrUpdate(
            key,
            _ => (now, 1),
            (_, current) => now - current.WindowStart > window ? (now, 1) : (current.WindowStart, current.Count + 1));
        return updated.Count <= limit;
    }
}

/// <summary>
/// STUN 주소와 TURN 임시 자격 증명 (coturn "use-auth-secret" 방식)
/// username = 만료시각:세션ID, credential = Base64(HMAC-SHA1(secret, username))
/// </summary>
public sealed class IceServerProvider(IOptions<SignalingOptions> options)
{
    public IceServerInfo[] Create(string sessionId)
    {
        var o = options.Value;
        var servers = new List<IceServerInfo>();
        string[] stun = o.StunUrls.Where(url => !string.IsNullOrWhiteSpace(url)).ToArray();
        if (stun.Length > 0)
        {
            servers.Add(new IceServerInfo(stun));
        }

        if (o.TurnUrls.Length > 0 && o.TurnSecret.Length > 0)
        {
            long expiry = DateTimeOffset.UtcNow.AddSeconds(o.TurnCredentialSeconds).ToUnixTimeSeconds();
            string username = $"{expiry}:{sessionId}";
            string credential = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(o.TurnSecret), Encoding.UTF8.GetBytes(username)));
            servers.Add(new IceServerInfo(o.TurnUrls, username, credential));
        }

        return servers.ToArray();
    }
}
