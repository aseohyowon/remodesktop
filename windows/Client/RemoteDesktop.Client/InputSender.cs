using RemoteDesktop.Protocol;

namespace RemoteDesktop.Client;

/// <summary>
/// UI 스레드를 막지 않고 입력 메시지를 순서대로 보냅니다.
/// 아직 보내지 못한 mouse_move가 연속되면 마지막 위치만 보냅니다. (클릭/키 순서는 그대로 유지)
/// </summary>
internal sealed class InputSender : IDisposable
{
    private readonly RemoteHostConnection _connection;
    private readonly List<ControlMessage> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cancellation = new();

    public InputSender(RemoteHostConnection connection)
    {
        _connection = connection;
        _ = Task.Run(SendLoopAsync);
    }

    public void Enqueue(ControlMessage message)
    {
        lock (_queue)
        {
            if (message is MouseMoveMessage && _queue.Count > 0 && _queue[^1] is MouseMoveMessage)
            {
                _queue[^1] = message;
                return;
            }

            _queue.Add(message);
        }

        _signal.Release();
    }

    private async Task SendLoopAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_cancellation.Token).ConfigureAwait(false);

                ControlMessage[] batch;
                lock (_queue)
                {
                    batch = _queue.ToArray();
                    _queue.Clear();
                }

                foreach (ControlMessage message in batch)
                {
                    if (!await _connection.SendAsync(message).ConfigureAwait(false))
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
    }
}
