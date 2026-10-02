using System.Threading.Channels;
using SIPSorcery.Net;

namespace RemoteDesktop.Transport.WebRtc;

/// <summary>
/// WebRTC Data Channel(순서 보장, 신뢰성 있음)을 일반 Stream처럼 쓸 수 있게 합니다.
/// 덕분에 LAN(TLS 스트림)과 같은 MessageChannel/인증/화면 코드를 그대로 사용합니다.
/// - 쓰기: 16 KB 조각으로 나눠 보냄 (브라우저·모바일 WebRTC와 호환되는 크기)
/// - 읽기: 받은 조각을 이어서 바이트 스트림으로 제공
/// - 송신 버퍼가 4 MB를 넘으면 잠깐 기다림 (메모리 폭주 방지)
/// </summary>
public sealed class DataChannelStream : Stream
{
    public const int ChunkSize = 16 * 1024;
    private const ulong MaxBuffered = 4 * 1024 * 1024;

    private readonly RTCDataChannel _channel;
    private readonly Action? _onDispose;
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private byte[]? _current;
    private int _offset;
    private int _disposed;

    public DataChannelStream(RTCDataChannel channel, Action? onDispose = null)
    {
        _channel = channel;
        _onDispose = onDispose;
        _channel.onmessage += (_, _, data) => _incoming.Writer.TryWrite(data);
        _channel.onclose += () => _incoming.Writer.TryComplete();
    }

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_current is null || _offset >= _current.Length)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0; // 닫힘 = 스트림 끝
            }

            _incoming.Reader.TryRead(out _current);
            _offset = 0;
        }

        int count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (!buffer.IsEmpty)
            {
                if (_channel.readyState != RTCDataChannelState.open)
                {
                    throw new IOException("WebRTC 데이터 채널이 닫혔습니다.");
                }

                while (_channel.bufferedAmount > MaxBuffered)
                {
                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                }

                int count = Math.Min(ChunkSize, buffer.Length);
                byte[] chunk = buffer[..count].ToArray();
                _channel.send(chunk, 0, chunk.Length);
                buffer = buffer[count..];
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _incoming.Writer.TryComplete();
            try
            {
                _channel.close();
            }
            catch
            {
            }

            _onDispose?.Invoke();
        }

        base.Dispose(disposing);
    }
}
