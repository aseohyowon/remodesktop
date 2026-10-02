using System.Runtime.InteropServices;
using RemoteDesktop.Core;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RemoteDesktop.Media;

internal static class MediaFoundationRuntime
{
    private static readonly Lazy<bool> Started = new(() =>
    {
        MediaFactory.MFStartup(useLightVersion: true).CheckError();
        return true;
    });

    public static void EnsureStarted() => _ = Started.Value;
}

/// <summary>
/// H.264 인코더 (Windows Media Foundation)
/// 1) GPU 하드웨어 인코더(NVIDIA NVENC, Intel Quick Sync, AMD AMF)를 먼저 시도
/// 2) 없거나 실패하면 Microsoft 소프트웨어 인코더(CPU)
/// 저지연 설정: B-프레임 없음, 저지연 모드, CBR. 키프레임은 요청 시 + 일정 간격.
/// 출력은 Annex-B 형식(00 00 00 01 + NAL)으로 디코더에 그대로 넣을 수 있습니다.
/// </summary>
public sealed class H264Encoder : IDisposable
{
    private const int MftOutputStreamProvidesSamples = 0x100;
    private const int MftOutputStreamCanProvideSamples = 0x200;
    private static readonly Result NoEventsAvailable = new(unchecked((int)0xC00D3E80));
    private static readonly Result NeedMoreInput = ResultCode.TransformNeedMoreInput;

    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator? _events;
    private readonly ICodecAPI? _codecApi;
    private readonly bool _providesSamples;
    private readonly int _outputSize;
    private readonly long _frameDuration;
    private int _pendingNeedInput;
    private long _frameIndex;

    private H264Encoder(IMFTransform transform, bool isAsync, bool isHardware, string name, int width, int height, int fps, int bitrate)
    {
        _transform = transform;
        Width = width;
        Height = height;
        IsHardware = isHardware;
        Name = name;
        _frameDuration = 10_000_000L / fps;

        if (isAsync)
        {
            _transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
            _events = _transform.QueryInterface<IMFMediaEventGenerator>();
        }

        try
        {
            _codecApi = (ICodecAPI)Marshal.GetObjectForIUnknown(_transform.NativePointer);
        }
        catch (InvalidCastException)
        {
            _codecApi = null;
        }

        // 형식 설정 전에 적용해야 하는 값들
        _codecApi?.TrySet(CodecApiKeys.LowLatencyMode, true);
        _codecApi?.TrySet(CodecApiKeys.RateControlMode, CodecApiKeys.RateControlCbr);
        _codecApi?.TrySet(CodecApiKeys.MeanBitRate, (uint)bitrate);
        _codecApi?.TrySet(CodecApiKeys.BPictureCount, 0u);
        _codecApi?.TrySet(CodecApiKeys.GopSize, (uint)(fps * 4));
        _transform.Attributes.Set(CodecApiKeys.LowLatencyMode, 1u);

        // 출력(H.264) 형식을 먼저, 입력(NV12) 형식을 나중에 설정해야 합니다.
        using (IMFMediaType output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
            output.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            output.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            output.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            output.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, 66u); // Baseline: 모든 디코더와 호환, B-프레임 없음
            _transform.SetOutputType(0, output, 0);
        }

        using (IMFMediaType input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            input.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            input.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            input.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            input.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            _transform.SetInputType(0, input, 0);
        }

        OutputStreamInfo info = _transform.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & (MftOutputStreamProvidesSamples | MftOutputStreamCanProvideSamples)) != 0;
        _outputSize = Math.Max(info.Size, width * height);

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    public int Width { get; }

    public int Height { get; }

    public bool IsHardware { get; }

    public string Name { get; }

    /// <summary>
    /// 인코더를 만듭니다. preferHardware가 true면 GPU 인코더를 먼저 시도합니다.
    /// 가로·세로는 짝수여야 합니다.
    /// </summary>
    public static H264Encoder Create(int width, int height, int fps, int bitrate, bool preferHardware = true)
    {
        MediaFoundationRuntime.EnsureStarted();
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };

        var attempts = new List<(uint Flags, bool Hardware)>();
        if (preferHardware)
        {
            attempts.Add(((uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter), true));
        }

        attempts.Add(((uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagLocalmft | EnumFlag.EnumFlagSortandfilter), false));

        var errors = new List<string>();
        foreach (var (flags, hardware) in attempts)
        {
            using IMFActivateCollection activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, input, output);
            foreach (IMFActivate activate in activates)
            {
                string name = TryGetName(activate);
                IMFTransform? transform = null;
                try
                {
                    transform = activate.ActivateObject<IMFTransform>();
                    bool isAsync = transform.Attributes.GetUInt32(TransformAttributeKeys.TransformAsync, out uint asyncValue).Success
                        && asyncValue != 0;
                    var encoder = new H264Encoder(transform, isAsync, hardware, name, width, height, fps, bitrate);
                    Log.Info($"H.264 encoder: {name} ({(hardware ? "GPU" : "CPU")}{(isAsync ? ", async" : "")}) {width}x{height}@{fps} {bitrate / 1000} kbps");
                    return encoder;
                }
                catch (Exception exception) when (exception is SharpGenException or COMException or InvalidCastException)
                {
                    errors.Add($"{name}: {exception.Message}");
                    transform?.Dispose();
                }
            }
        }

        throw new NotSupportedException("사용할 수 있는 H.264 인코더가 없습니다. " + string.Join(" / ", errors));
    }

    /// <summary>비트레이트를 실행 중에 바꿉니다 (적응형 화질). 지원하지 않는 인코더면 false.</summary>
    public bool SetBitrate(int bitrate) => _codecApi?.TrySet(CodecApiKeys.MeanBitRate, (uint)bitrate) ?? false;

    /// <summary>
    /// NV12 프레임 하나를 인코딩합니다. 결과는 0개 이상의 Annex-B 조각을 이어 붙인 바이트입니다.
    /// (저지연 모드에서는 보통 입력 1개 → 출력 1개)
    /// </summary>
    public byte[] Encode(byte[] nv12, bool forceKeyFrame)
    {
        if (forceKeyFrame)
        {
            _codecApi?.TrySet(CodecApiKeys.ForceKeyFrame, 1u);
        }

        using IMFSample sample = CreateSample(nv12, nv12.Length);
        sample.SampleTime = _frameIndex * _frameDuration;
        sample.SampleDuration = _frameDuration;
        _frameIndex++;

        using var result = new MemoryStream();
        if (_events is null)
        {
            _transform.ProcessInput(0, sample, 0);
            DrainOutput(result);
        }
        else
        {
            // 비동기(하드웨어) MFT: NeedInput 이벤트를 받은 만큼만 입력할 수 있습니다.
            var waitInput = System.Diagnostics.Stopwatch.StartNew();
            while (_pendingNeedInput == 0)
            {
                PumpEvent(result, blocking: waitInput.ElapsedMilliseconds > 200);
            }

            _transform.ProcessInput(0, sample, 0);
            _pendingNeedInput--;

            // 이번 프레임의 출력을 최대 100 ms 기다립니다.
            var wait = System.Diagnostics.Stopwatch.StartNew();
            long before = result.Length;
            while (result.Length == before && wait.ElapsedMilliseconds < 100)
            {
                if (!PumpEvent(result, blocking: false))
                {
                    Thread.Sleep(1);
                }
            }

            while (PumpEvent(result, blocking: false))
            {
            }
        }

        return result.ToArray();
    }

    /// <summary>이벤트 하나를 처리했으면 true</summary>
    private bool PumpEvent(MemoryStream result, bool blocking)
    {
        IMFMediaEvent mediaEvent;
        try
        {
            mediaEvent = _events!.GetEvent(blocking ? 0 : 1); // 1 = MF_EVENT_FLAG_NO_WAIT
        }
        catch (SharpGenException exception) when (exception.ResultCode == NoEventsAvailable)
        {
            return false;
        }

        using (mediaEvent)
        {
            if (mediaEvent.EventType == MediaEventTypes.TransformNeedInput)
            {
                _pendingNeedInput++;
            }
            else if (mediaEvent.EventType == MediaEventTypes.TransformHaveOutput)
            {
                ReadOneOutput(result);
            }
        }

        return true;
    }

    private void DrainOutput(MemoryStream result)
    {
        while (ReadOneOutput(result))
        {
        }
    }

    /// <summary>출력 하나를 읽으면 true, 더 필요한 입력이 있으면 false</summary>
    private bool ReadOneOutput(MemoryStream result)
    {
        IMFSample? provided = _providesSamples ? null : CreateSample(null, _outputSize);
        var buffer = new OutputDataBuffer { StreamID = 0, Sample = provided! };
        try
        {
            Result hr = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);
            if (hr == NeedMoreInput)
            {
                return false;
            }

            if (hr == ResultCode.TransformStreamChange)
            {
                using IMFMediaType type = _transform.GetOutputAvailableType(0, 0);
                _transform.SetOutputType(0, type, 0);
                return true;
            }

            hr.CheckError();
            if ((provided ?? buffer.Sample) is { } output)
            {
                AppendSample(output, result);
            }

            return true;
        }
        finally
        {
            ReleaseOutput(provided, buffer);
        }
    }

    /// <summary>
    /// ProcessOutput 뒤 샘플 해제. 우리가 넣은 샘플이면 Vortice가 같은 COM 객체의 래퍼를 하나 더 만들어 돌려주므로
    /// 그 래퍼는 해제하지 않아야 합니다(두 번 Release하면 메모리 손상). MFT가 만든 샘플이면 한 번 해제합니다.
    /// </summary>
    internal static void ReleaseOutput(IMFSample? provided, OutputDataBuffer buffer)
    {
        if (provided is not null)
        {
            if (buffer.Sample is { } duplicate && !ReferenceEquals(duplicate, provided))
            {
                GC.SuppressFinalize(duplicate);
            }

            provided.Dispose();
        }
        else
        {
            buffer.Sample?.Dispose();
        }

        buffer.Events?.Dispose();
    }

    private static void AppendSample(IMFSample sample, MemoryStream destination)
    {
        using IMFMediaBuffer contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out IntPtr data, out _, out int length);
        try
        {
            unsafe
            {
                destination.Write(new ReadOnlySpan<byte>((void*)data, length));
            }
        }
        finally
        {
            contiguous.Unlock();
        }
    }

    internal static IMFSample CreateSample(byte[]? data, int size)
    {
        IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(size);
        if (data is not null)
        {
            buffer.Lock(out IntPtr pointer, out _, out _);
            try
            {
                Marshal.Copy(data, 0, pointer, data.Length);
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.CurrentLength = data.Length;
        }

        IMFSample sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        buffer.Dispose();
        return sample;
    }

    internal static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    private static string TryGetName(IMFActivate activate)
    {
        try
        {
            // MFT_FRIENDLY_NAME_Attribute
            return activate.GetString(new Guid("314ffbae-5b41-4c95-9c19-4e7d586face3"));
        }
        catch (SharpGenException)
        {
            return "H.264 Encoder";
        }
    }

    public void Dispose()
    {
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        }
        catch
        {
        }

        if (_codecApi is not null)
        {
            Marshal.ReleaseComObject(_codecApi);
        }

        _events?.Dispose();
        _transform.Dispose();
    }
}
