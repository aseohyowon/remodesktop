using System.Net;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// 무차별 대입(Brute Force) 방지
/// - 같은 IP에서 5번 실패하면 5분 차단
/// - 모든 IP 합계로 10분 동안 30번 실패하면 2분 동안 전체 차단 (여러 IP로 나눠 공격하는 경우)
/// - 오래된 기록은 자동으로 정리
/// </summary>
public sealed class AuthThrottle
{
    private const int MaxFailuresPerAddress = 5;
    private const int MaxFailuresGlobal = 30;
    private static readonly TimeSpan AddressLock = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan GlobalWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan GlobalLock = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(30);

    private readonly object _sync = new();
    private readonly Dictionary<IPAddress, Entry> _entries = new();
    private readonly Queue<DateTime> _recentFailures = new();
    private DateTime _globalLockedUntil = DateTime.MinValue;
    private readonly Func<DateTime> _now;

    public AuthThrottle(Func<DateTime>? clock = null)
    {
        _now = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>address가 null이면(주소를 모르는 연결) 전체 차단만 확인합니다.</summary>
    public bool IsLocked(IPAddress? address, out TimeSpan remaining)
    {
        lock (_sync)
        {
            DateTime now = _now();
            remaining = _globalLockedUntil - now;
            if (remaining > TimeSpan.Zero)
            {
                return true;
            }

            remaining = TimeSpan.Zero;
            if (address is null || !_entries.TryGetValue(address, out Entry? entry) || entry.LockedUntil is not { } until)
            {
                return false;
            }

            remaining = until - now;
            if (remaining > TimeSpan.Zero)
            {
                return true;
            }

            _entries.Remove(address);
            return false;
        }
    }

    /// <summary>실패를 기록하고, 이번 실패로 잠겼으면 true를 반환합니다.</summary>
    public bool RecordFailure(IPAddress? address)
    {
        lock (_sync)
        {
            DateTime now = _now();
            Prune(now);

            _recentFailures.Enqueue(now);
            bool locked = false;
            if (_recentFailures.Count >= MaxFailuresGlobal)
            {
                _globalLockedUntil = now + GlobalLock;
                _recentFailures.Clear();
                locked = true;
            }

            if (address is not null)
            {
                if (!_entries.TryGetValue(address, out Entry? entry))
                {
                    entry = new Entry();
                    _entries[address] = entry;
                }

                entry.Failures++;
                entry.LastFailure = now;
                if (entry.Failures >= MaxFailuresPerAddress)
                {
                    entry.LockedUntil = now + AddressLock;
                    locked = true;
                }
            }

            return locked;
        }
    }

    public void RecordSuccess(IPAddress? address)
    {
        if (address is null)
        {
            return;
        }

        lock (_sync)
        {
            _entries.Remove(address);
        }
    }

    private void Prune(DateTime now)
    {
        while (_recentFailures.Count > 0 && now - _recentFailures.Peek() > GlobalWindow)
        {
            _recentFailures.Dequeue();
        }

        foreach (var (address, entry) in _entries.ToArray())
        {
            if (entry.LockedUntil is null && now - entry.LastFailure > ForgetAfter)
            {
                _entries.Remove(address);
            }
        }
    }

    private sealed class Entry
    {
        public int Failures { get; set; }

        public DateTime LastFailure { get; set; }

        public DateTime? LockedUntil { get; set; }
    }
}
