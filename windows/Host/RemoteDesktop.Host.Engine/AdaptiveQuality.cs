namespace RemoteDesktop.Host.Engine;

/// <summary>화질 단계. Scale = 원래 해상도 대비 비율</summary>
public sealed record QualityLevel(int Index, double Scale, int Fps, int JpegQuality, int BitrateKbps);

/// <summary>
/// 네트워크 상태에 따른 자동 화질 조절 (adaptive bitrate / FPS / 해상도)
///
/// 매초 측정값을 받습니다.
/// - RTT: 프레임을 보낸 뒤 Client가 표시하고 ack하기까지 걸린 시간 (전송 시간 포함 → 대역폭이 부족하면 커짐)
/// - credit 대기 비율: 다음 프레임을 보내려고 ack를 기다린 시간의 비율 (Client/네트워크가 못 따라오면 커짐)
///
/// 나쁨(RTT &gt; 250ms 또는 대기 &gt; 50%)이 2초 연속이면 한 단계 낮추고,
/// 좋음(RTT &lt; 100ms 그리고 대기 &lt; 15%)이 5초 연속이면 한 단계 올립니다.
/// 너무 자주 바뀌지 않도록 바꾼 뒤 일정 시간은 그대로 둡니다.
/// </summary>
public sealed class AdaptiveQuality
{
    private static readonly QualityLevel[] BaseLevels =
    [
        new(0, 1.00, 30, 75, 8000),
        new(1, 1.00, 24, 65, 5000),
        new(2, 0.75, 20, 55, 3000),
        new(3, 0.50, 15, 45, 1500),
        new(4, 0.50, 10, 35, 800)
    ];

    private static readonly TimeSpan DegradeCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan UpgradeCooldown = TimeSpan.FromSeconds(5);

    private readonly QualityLevel[] _levels;
    private readonly bool _enabled;
    private int _badWindows;
    private int _goodWindows;
    private DateTime _lastChange = DateTime.MinValue;

    public AdaptiveQuality(int maxFps, int maxJpegQuality, int maxBitrateKbps, bool enabled = true)
    {
        _enabled = enabled;
        _levels = BaseLevels
            .Select(l => l with
            {
                Fps = Math.Min(l.Fps, maxFps),
                JpegQuality = Math.Min(l.JpegQuality, maxJpegQuality),
                BitrateKbps = Math.Min(l.BitrateKbps, maxBitrateKbps)
            })
            .ToArray();
        Current = _levels[0];
    }

    public QualityLevel Current { get; private set; }

    /// <summary>1초 측정값을 반영합니다. 단계가 바뀌었으면 true</summary>
    public bool Update(double averageRttMs, double creditWaitRatio, DateTime now)
    {
        if (!_enabled)
        {
            return false;
        }

        bool bad = averageRttMs > 250 || creditWaitRatio > 0.5;
        bool good = averageRttMs < 100 && creditWaitRatio < 0.15;

        _badWindows = bad ? _badWindows + 1 : 0;
        _goodWindows = good ? _goodWindows + 1 : 0;

        if (_badWindows >= 2 && Current.Index < _levels.Length - 1 && now - _lastChange >= DegradeCooldown)
        {
            return Change(Current.Index + 1, now);
        }

        if (_goodWindows >= 5 && Current.Index > 0 && now - _lastChange >= UpgradeCooldown)
        {
            return Change(Current.Index - 1, now);
        }

        return false;
    }

    private bool Change(int index, DateTime now)
    {
        Current = _levels[index];
        _lastChange = now;
        _badWindows = 0;
        _goodWindows = 0;
        return true;
    }
}
