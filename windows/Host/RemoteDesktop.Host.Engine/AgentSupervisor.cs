using System.ComponentModel;
using RemoteDesktop.Core;

namespace RemoteDesktop.Host.Engine;

/// <summary>서비스가 띄운 에이전트 프로세스 하나</summary>
public interface IAgentProcess : IDisposable
{
    int Id { get; }

    /// <summary>에이전트가 실행 중인 Windows 세션 번호</summary>
    uint SessionId { get; }

    bool HasExited { get; }

    void Kill();
}

public interface IAgentLauncher
{
    IAgentProcess Launch(uint sessionId);
}

/// <summary>
/// Windows 서비스(STEP 13)의 핵심: 지금 화면 앞에 있는 세션(콘솔 세션)에 에이전트가 항상 하나 떠 있게 합니다.
///
/// 서비스는 세션 0에서 실행되어 화면이 없습니다. 실제 캡처·입력은 사용자의 세션에서 해야 하므로
/// 서비스는 "콘솔 세션에 SYSTEM 권한 에이전트를 띄우는 일"만 합니다.
/// - 로그인 전/로그아웃 후: 로그인 화면 세션에 에이전트 → 로그인 화면을 원격으로 볼 수 있음
/// - 사용자 전환(빠른 사용자 전환)이나 로그아웃으로 콘솔 세션이 바뀌면: 옛 에이전트를 끄고 새 세션에 다시 띄움
/// - 에이전트가 죽으면: 1, 2, 4 … 최대 30초 간격으로 다시 띄움 (계속 죽는 경우 CPU 낭비 방지)
/// </summary>
public sealed class AgentSupervisor
{
    /// <summary>WTSGetActiveConsoleSessionId: 콘솔에 연결된 세션이 없을 때(전환 중)</summary>
    public const uint NoSession = 0xFFFFFFFF;

    private static readonly TimeSpan HealthyRunTime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly IAgentLauncher _launcher;
    private readonly Func<uint> _activeSession;
    private readonly Func<DateTime> _now;
    private readonly TimeSpan _pollInterval;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly object _sync = new();
    private IAgentProcess? _agent;
    private DateTime _startedAt;
    private DateTime _nextLaunch = DateTime.MinValue;
    private int _failures;

    public AgentSupervisor(IAgentLauncher launcher, Func<uint> activeSession, Func<DateTime>? now = null, TimeSpan? pollInterval = null)
    {
        _launcher = launcher;
        _activeSession = activeSession;
        _now = now ?? (() => DateTime.UtcNow);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>지금 실행 중인 에이전트의 프로세스 ID (SAS 파이프에서 요청자 확인용)</summary>
    public int? AgentProcessId
    {
        get
        {
            lock (_sync)
            {
                return _agent is { HasExited: false } agent ? agent.Id : null;
            }
        }
    }

    public int Launches { get; private set; }

    /// <summary>세션 변경 알림(로그인, 로그아웃, 사용자 전환 등)을 받으면 바로 확인합니다.</summary>
    public void Notify() => _wake.Release();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Check();
                try
                {
                    await _wake.WaitAsync(_pollInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            Stop();
        }
    }

    internal void Check()
    {
        lock (_sync)
        {
            DateTime now = _now();
            uint session = _activeSession();

            if (_agent is { } agent)
            {
                if (agent.HasExited)
                {
                    if (now - _startedAt >= HealthyRunTime)
                    {
                        _failures = 0; // 한동안 잘 돌다가 끝난 경우는 바로 다시 띄움
                    }

                    _failures++;
                    _nextLaunch = now + Backoff(_failures);
                    Log.Warn($"Agent (pid {agent.Id}) exited; restarting in {Backoff(_failures).TotalSeconds:F0}s");
                    agent.Dispose();
                    _agent = null;
                }
                else if (agent.SessionId != session)
                {
                    Log.Info($"Console session changed {agent.SessionId} -> {session}; restarting agent");
                    StopAgent();
                    _failures = 0;
                    _nextLaunch = now;
                }
                else
                {
                    return;
                }
            }

            // 세션 0은 서비스 전용(화면 없음), NoSession은 전환 중
            if (session is NoSession or 0 || now < _nextLaunch)
            {
                return;
            }

            try
            {
                _agent = _launcher.Launch(session);
                _startedAt = now;
                Launches++;
                Log.Info($"Agent started in session {session} (pid {_agent.Id})");
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
            {
                _failures++;
                _nextLaunch = now + Backoff(_failures);
                Log.Error($"Agent start failed in session {session}: {exception.Message}");
            }
        }
    }

    internal static TimeSpan Backoff(int failures) =>
        TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Max(0, failures - 1))));

    public void Stop()
    {
        lock (_sync)
        {
            StopAgent();
        }
    }

    private void StopAgent()
    {
        if (_agent is null)
        {
            return;
        }

        try
        {
            if (!_agent.HasExited)
            {
                _agent.Kill();
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
        }

        _agent.Dispose();
        _agent = null;
    }
}
