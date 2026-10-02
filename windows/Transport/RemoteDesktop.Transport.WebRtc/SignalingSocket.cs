using System.Net.WebSockets;
using System.Text;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Transport.WebRtc;

/// <summary>시그널링 서버 WebSocket 연결 (JSON 메시지 송수신)</summary>
public sealed class SignalingSocket : IDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private SignalingSocket()
    {
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    public bool IsOpen => _socket.State == WebSocketState.Open;

    public static async Task<SignalingSocket> ConnectAsync(Uri server, CancellationToken cancellationToken)
    {
        var socket = new SignalingSocket();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await socket._socket.ConnectAsync(server, timeout.Token).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task SendAsync(SignalMessage message, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(SignalingJson.Serialize(message));
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>다음 메시지. 연결이 닫히면 null. 모르는 메시지는 건너뜁니다.</summary>
    public async Task<SignalMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                message.Write(buffer, 0, result.Count);
                if (message.Length > SignalingJson.MaxMessageBytes)
                {
                    throw new ProtocolException("시그널링 메시지가 너무 큽니다.");
                }
            }
            while (!result.EndOfMessage);

            if (SignalingJson.Deserialize(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length)) is { } parsed)
            {
                return parsed;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).Wait(500);
            }
        }
        catch
        {
        }

        _socket.Dispose();
    }
}
