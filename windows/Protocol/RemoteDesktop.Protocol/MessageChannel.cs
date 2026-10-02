using System.Buffers.Binary;

namespace RemoteDesktop.Protocol;

public enum MessageKind : byte
{
    Control = 1,
    VideoFrame = 2,

    /// <summary>파일 조각: [transfer_id u32][offset u64][data]</summary>
    FileChunk = 3,

    /// <summary>오디오: [sequence u32][PCM 16bit little-endian]</summary>
    Audio = 4
}

/// <summary>
/// 수신한 메시지. Control, Video, Binary(파일 조각/오디오) 중 하나만 값이 있습니다.
/// 모두 null이면 알 수 없는 메시지입니다(무시).
/// </summary>
public sealed record ReceivedMessage(ControlMessage? Control, VideoFrame? Video, BinaryMessage? Binary = null);

public sealed record BinaryMessage(MessageKind Kind, ReadOnlyMemory<byte> Data);

/// <summary>
/// TLS 스트림 위의 메시지 framing입니다.
/// [length u32 big-endian][kind u8][payload (length - 1 바이트)]
/// 쓰기는 여러 스레드에서 호출해도 안전하고, 읽기는 한 스레드에서만 호출해야 합니다.
/// </summary>
public sealed class MessageChannel(Stream stream) : IDisposable
{
    private const int FrameHeaderBytes = 5;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _readHeader = new byte[FrameHeaderBytes];

    public Task SendControlAsync(ControlMessage message, CancellationToken cancellationToken)
    {
        byte[] json = ProtocolJson.Serialize(message);
        return WriteMessageAsync(MessageKind.Control, json, ReadOnlyMemory<byte>.Empty, cancellationToken);
    }

    /// <summary>파일 조각, 오디오 등 바이너리 메시지</summary>
    public Task SendBinaryAsync(MessageKind kind, ReadOnlyMemory<byte> head, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        WriteMessageAsync(kind, head, data, cancellationToken);

    public Task SendVideoFrameAsync(VideoFrameHeader header, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        byte[] headerBytes = new byte[VideoFrameHeader.Size];
        header.Write(headerBytes);
        return WriteMessageAsync(MessageKind.VideoFrame, headerBytes, data, cancellationToken);
    }

    public async Task<ReceivedMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        await stream.ReadExactlyAsync(_readHeader, cancellationToken).ConfigureAwait(false);

        int length = BinaryPrimitives.ReadInt32BigEndian(_readHeader);
        if (length < 1 || length > ProtocolConstants.MaxMessageBytes)
        {
            throw new ProtocolException($"허용되지 않는 메시지 길이입니다: {length}");
        }

        var kind = (MessageKind)_readHeader[4];
        int payloadLength = length - 1;

        if (kind == MessageKind.Control && payloadLength > ProtocolConstants.MaxControlMessageBytes)
        {
            throw new ProtocolException($"제어 메시지가 너무 큽니다: {payloadLength}바이트");
        }

        byte[] payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

        return kind switch
        {
            MessageKind.Control => new ReceivedMessage(ProtocolJson.Deserialize(payload), null),
            MessageKind.VideoFrame => new ReceivedMessage(null, new VideoFrame(
                VideoFrameHeader.Read(payload),
                payload.AsMemory(VideoFrameHeader.Size))),
            MessageKind.FileChunk or MessageKind.Audio => new ReceivedMessage(null, null, new BinaryMessage(kind, payload)),
            _ => new ReceivedMessage(null, null)
        };
    }

    private async Task WriteMessageAsync(
        MessageKind kind,
        ReadOnlyMemory<byte> head,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        int length = 1 + head.Length + body.Length;
        if (length > ProtocolConstants.MaxMessageBytes)
        {
            throw new ProtocolException($"보내려는 메시지가 너무 큽니다: {length}바이트");
        }

        // 길이·종류·head를 한 번에 써서 작은 TLS 레코드가 여러 개 생기지 않게 합니다.
        byte[] prefix = new byte[FrameHeaderBytes + head.Length];
        BinaryPrimitives.WriteInt32BigEndian(prefix, length);
        prefix[4] = (byte)kind;
        head.CopyTo(prefix.AsMemory(FrameHeaderBytes));

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
            if (!body.IsEmpty)
            {
                await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        stream.Dispose();
    }
}
