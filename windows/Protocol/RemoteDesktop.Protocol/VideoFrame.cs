using System.Buffers.Binary;

namespace RemoteDesktop.Protocol;

public enum VideoCodec : byte
{
    Jpeg = 1,
    H264 = 2
}

/// <summary>
/// 영상 프레임 바이너리 헤더 (21바이트, big-endian)
/// [frame_id u32][capture_time_ms i64][width i32][height i32][codec u8]
/// </summary>
public readonly record struct VideoFrameHeader(
    uint FrameId,
    long CaptureTimeMs,
    int Width,
    int Height,
    VideoCodec Codec)
{
    public const int Size = 21;

    public void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination, FrameId);
        BinaryPrimitives.WriteInt64BigEndian(destination[4..], CaptureTimeMs);
        BinaryPrimitives.WriteInt32BigEndian(destination[12..], Width);
        BinaryPrimitives.WriteInt32BigEndian(destination[16..], Height);
        destination[20] = (byte)Codec;
    }

    public static VideoFrameHeader Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
        {
            throw new ProtocolException("영상 프레임 헤더가 너무 짧습니다.");
        }

        return new VideoFrameHeader(
            BinaryPrimitives.ReadUInt32BigEndian(source),
            BinaryPrimitives.ReadInt64BigEndian(source[4..]),
            BinaryPrimitives.ReadInt32BigEndian(source[12..]),
            BinaryPrimitives.ReadInt32BigEndian(source[16..]),
            (VideoCodec)source[20]);
    }
}

public sealed record VideoFrame(VideoFrameHeader Header, ReadOnlyMemory<byte> Data);

public static class VideoCodecNames
{
    public const string Jpeg = "jpeg";
    public const string H264 = "h264";
}
