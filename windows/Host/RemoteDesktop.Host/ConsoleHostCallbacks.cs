using System.Threading.Channels;
using RemoteDesktop.Core;
using RemoteDesktop.Host.Engine;

namespace RemoteDesktop.Host;

/// <summary>콘솔에서 접속 승인(y/N)을 받습니다.</summary>
internal sealed class ConsoleHostCallbacks : NullHostCallbacks
{
    private readonly SemaphoreSlim _promptLock = new(1, 1);
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private int _readerStarted;

    public override async Task<bool> ApproveAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected)
        {
            Log.Warn("콘솔 입력이 없어 접속 요청을 거부합니다.");
            return false;
        }

        StartReader();
        await _promptLock.WaitAsync(cancellationToken);
        try
        {
            while (_lines.Reader.TryRead(out _))
            {
                // 이전에 입력된 줄은 버립니다.
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine();
            Console.WriteLine($"[접속 요청] {request.ClientName} ({request.Platform}) - {request.Remote}, 인증: {request.Method}");
            Console.Write("허용할까요? (y/N, 30초 후 자동 거부): ");
            Console.ResetColor();

            string answer = await _lines.Reader.ReadAsync(cancellationToken);
            return answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _promptLock.Release();
        }
    }

    public override void OnSessionStarted(SessionInfo session) =>
        Log.Info($"Session started: {session.ClientName} ({session.Platform}) via {session.Transport}");

    public override void OnSessionEnded(SessionInfo session, string reason) =>
        Log.Info($"Session ended: {session.ClientName} - {reason}");

    private void StartReader()
    {
        if (Interlocked.Exchange(ref _readerStarted, 1) == 1)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            while (Console.ReadLine() is { } line)
            {
                _lines.Writer.TryWrite(line);
            }
        })
        {
            IsBackground = true,
            Name = "console-approval-reader"
        };
        thread.Start();
    }
}
