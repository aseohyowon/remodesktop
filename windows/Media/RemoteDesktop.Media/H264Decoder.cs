using RemoteDesktop.Core;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RemoteDesktop.Media;

/// <summary>
/// H.264 디코더 (Windows Media Foundation, Microsoft H.264 Video Decoder)
/// Annex-B 프레임을 넣으면 BGRA 이미지를 돌려줍니다.
/// 저지연 모드로 설정해 프레임을 모아 두지 않고 바로 출력합니다.
/// </summary>
public sealed class H264Decoder : IDisposable
{
    private static readonly Result NeedMoreInput = ResultCode.TransformNeedMoreInput;
    private const int MftOutputStreamProvidesSamples = 0x100;

    private readonly IMFTransform _transform;
    private bool _outputTypeSet;
    private bool _providesSamples;
    private int _outputSize;
    private int _planeWidth;
    private int _planeHeight;
    private int _stride;
    private long _sampleTime;

    public H264Decoder()
    {
        MediaFoundationRuntime.EnsureStarted();
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };

        using IMFActivateCollection activates = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoDecoder,
            (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagLocalmft | EnumFlag.EnumFlagSortandfilter),
            input,
            output);

        IMFActivate activate = activates.FirstOrDefault() ?? throw new NotSupportedException("H.264 디코더가 없습니다.");
        _transform = activate.ActivateObject<IMFTransform>();
        _transform.Attributes.Set(CodecApiKeys.LowLatencyMode, 1u);

        using IMFMediaType type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        _transform.SetInputType(0, type, 0);

        TrySetOutputType();
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    /// <summary>
    /// H.264 데이터 하나를 디코딩합니다. 출력할 프레임이 있으면 width×height BGRA(stride = width*4)를 반환합니다.
    /// width/height는 원래 화면 크기(프레임 헤더 값)로, 디코더의 16 정렬 여백을 잘라 냅니다.
    /// </summary>
    public unsafe byte[]? Decode(ReadOnlySpan<byte> annexB, int width, int height)
    {
        using IMFSample sample = H264Encoder.CreateSample(annexB.ToArray(), annexB.Length);
        sample.SampleTime = _sampleTime;
        sample.SampleDuration = 333_333;
        _sampleTime += 333_333;

        _transform.ProcessInput(0, sample, 0);

        byte[]? latest = null;
        while (true)
        {
            if (!_outputTypeSet && !TrySetOutputType())
            {
                return latest;
            }

            IMFSample? provided = _providesSamples ? null : H264Encoder.CreateSample(null, _outputSize);
            var buffer = new OutputDataBuffer { StreamID = 0, Sample = provided! };
            try
            {
                Result hr = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);
                if (hr == NeedMoreInput)
                {
                    return latest;
                }

                if (hr == ResultCode.TransformStreamChange)
                {
                    _outputTypeSet = false; // 해상도 정보가 확정됨 → 출력 형식 다시 설정
                    continue;
                }

                hr.CheckError();
                if ((provided ?? buffer.Sample) is not { } output)
                {
                    continue;
                }

                using IMFMediaBuffer contiguous = output.ConvertToContiguousBuffer();
                contiguous.Lock(out IntPtr data, out _, out int length);
                try
                {
                    int w = Math.Min(width, _planeWidth);
                    int h = Math.Min(height, _planeHeight);
                    if (length < _stride * _planeHeight * 3 / 2)
                    {
                        continue;
                    }

                    latest = new byte[w * h * 4];
                    fixed (byte* dst = latest)
                    {
                        Nv12Converter.Nv12ToBgra((byte*)data, _stride, _planeHeight, w, h, dst, w * 4);
                    }

                    LastWidth = w;
                    LastHeight = h;
                }
                finally
                {
                    contiguous.Unlock();
                }
            }
            finally
            {
                H264Encoder.ReleaseOutput(provided, buffer);
            }
        }
    }

    public int LastWidth { get; private set; }

    public int LastHeight { get; private set; }

    private bool TrySetOutputType()
    {
        for (int index = 0; ; index++)
        {
            IMFMediaType type;
            try
            {
                type = _transform.GetOutputAvailableType(0, index);
            }
            catch (SharpGenException)
            {
                return false; // 아직 SPS를 받지 못함
            }

            using (type)
            {
                if (type.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12)
                {
                    continue;
                }

                _transform.SetOutputType(0, type, 0);
                ulong size = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                _planeWidth = (int)(size >> 32);
                _planeHeight = (int)(size & 0xFFFFFFFF);
                _stride = type.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out uint stride).Success && stride > 0
                    ? (int)stride
                    : _planeWidth;

                OutputStreamInfo info = _transform.GetOutputStreamInfo(0);
                _providesSamples = (info.Flags & MftOutputStreamProvidesSamples) != 0;
                _outputSize = Math.Max(info.Size, _stride * _planeHeight * 3 / 2);
                _outputTypeSet = true;
                Log.Debug($"H.264 decoder output: NV12 {_planeWidth}x{_planeHeight} stride {_stride}");
                return true;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        }
        catch
        {
        }

        _transform.Dispose();
    }
}
