using Concentus;
using Concentus.Enums;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// STEP 14: 시스템 소리(WASAPI float, 장치 샘플레이트/채널) → Opus 48 kHz 스테레오 20 ms 패킷.
/// WebRTC 오디오 트랙은 Opus 48 kHz가 표준이라 44.1 kHz 장치면 선형 보간으로 바꿉니다.
/// 약 64 kbps로 PCM(약 1.5 Mbps)보다 20배 이상 작아 인터넷에서도 쓸 수 있습니다.
/// </summary>
public sealed class OpusPacketizer
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int FrameSamples = 960; // 20 ms

    private readonly IOpusEncoder _encoder;
    private readonly int _sourceRate;
    private readonly int _sourceChannels;
    private readonly short[] _frame = new short[FrameSamples * Channels];
    private readonly byte[] _packet = new byte[1500];
    private int _frameFill; // 채운 샘플 수 (채널당)
    private double _position; // 원본 기준 다음 출력 샘플 위치 (보간용)
    private float _lastLeft;
    private float _lastRight;

    public OpusPacketizer(int sourceRate, int sourceChannels, int bitrate = 64000)
    {
        if (sourceRate is < 8000 or > 192000 || sourceChannels < 1)
        {
            throw new NotSupportedException($"지원하지 않는 오디오 형식입니다: {sourceRate} Hz, {sourceChannels} ch");
        }

        _sourceRate = sourceRate;
        _sourceChannels = sourceChannels;
        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _encoder.Bitrate = bitrate;
    }

    /// <summary>interleaved float 샘플을 넣고, 완성된 Opus 패킷(20 ms씩)을 돌려줍니다.</summary>
    public List<byte[]> Add(ReadOnlySpan<float> interleaved)
    {
        var packets = new List<byte[]>();
        int frames = interleaved.Length / _sourceChannels;
        double step = (double)_sourceRate / SampleRate;

        // 출력 샘플 t는 원본 위치 _position (직전 블록 마지막 샘플을 -1로 보는 좌표)에서 보간
        while (_position < frames - 1 + 1e-9)
        {
            int index = (int)Math.Floor(_position);
            double fraction = _position - index;
            (float l0, float r0) = index < 0 ? (_lastLeft, _lastRight) : Read(interleaved, index);
            (float l1, float r1) = Read(interleaved, Math.Min(index + 1, frames - 1));
            float left = (float)(l0 + (l1 - l0) * fraction);
            float right = (float)(r0 + (r1 - r0) * fraction);

            _frame[_frameFill * 2] = ToShort(left);
            _frame[_frameFill * 2 + 1] = ToShort(right);
            _frameFill++;
            if (_frameFill == FrameSamples)
            {
                int length = _encoder.Encode(_frame, FrameSamples, _packet, _packet.Length);
                packets.Add(_packet.AsSpan(0, length).ToArray());
                _frameFill = 0;
            }

            _position += step;
        }

        if (frames > 0)
        {
            (_lastLeft, _lastRight) = Read(interleaved, frames - 1);
            _position -= frames;
        }

        return packets;
    }

    private (float Left, float Right) Read(ReadOnlySpan<float> interleaved, int frame)
    {
        int offset = frame * _sourceChannels;
        float left = interleaved[offset];
        float right = _sourceChannels > 1 ? interleaved[offset + 1] : left;
        return (left, right);
    }

    private static short ToShort(float value) => (short)Math.Clamp(value * 32767f, short.MinValue, short.MaxValue);
}
