using System.Drawing;
using System.Drawing.Imaging;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// 프레임마다 독립된 JPEG 이미지를 만듭니다.
/// H.264 디코더가 없는 Client(현재 모바일 앱)와의 호환용이며, 품질은 적응형 화질 단계에 따라 바뀝니다.
/// </summary>
internal sealed class JpegFrameEncoder : IDisposable
{
    private readonly ImageCodecInfo _codec;
    private readonly EncoderParameters _parameters = new(1);
    private readonly MemoryStream _buffer = new();
    private int _quality = -1;

    public JpegFrameEncoder()
    {
        _codec = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
    }

    /// <summary>
    /// 반환된 메모리는 다음 Encode 호출 전까지만 유효합니다.
    /// (전송이 끝난 뒤 다음 프레임을 인코딩하므로 버퍼를 재사용할 수 있습니다.)
    /// </summary>
    public ReadOnlyMemory<byte> Encode(Bitmap frame, int quality)
    {
        if (quality != _quality)
        {
            _parameters.Param[0]?.Dispose();
            _parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
            _quality = quality;
        }

        _buffer.SetLength(0);
        frame.Save(_buffer, _codec, _parameters);
        return _buffer.GetBuffer().AsMemory(0, (int)_buffer.Length);
    }

    public void Dispose()
    {
        _parameters.Dispose();
        _buffer.Dispose();
    }
}
