using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using RemoteDesktop.Core;
using RemoteDesktop.Media;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport.WebRtc;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// 화면 캡처 → (축소) → 압축(H.264 또는 JPEG) → 전송 루프와 흐름 제어.
///
/// 흐름 제어: Client가 ack하지 않은 프레임은 최대 2개까지만 보냅니다.
///   네트워크가 느리면 캡처 자체를 늦춰 오래된 화면이 쌓이지 않습니다.
///   (H.264는 프레임끼리 의존하므로 인코딩한 프레임은 버리지 않고, 캡처 단계에서 건너뜁니다)
/// 적응형 화질: AdaptiveQuality가 1초마다 RTT와 대기 비율을 보고 해상도/FPS/비트레이트를 조절합니다.
///
/// 미디어 트랙 모드(STEP 14, 모바일·맥 앱): AttachTrack 이후에는 H.264를 WebRTC 영상 트랙(RTP)으로 보냅니다.
///   ack 대신 RTCP 수신 보고서(RTT, 손실률)로 화질을 조절하고, PLI를 받으면 키프레임을 만듭니다.
/// </summary>
internal sealed class VideoStreamer : IDisposable
{
    private const int MaxFramesInFlight = 2;
    private static readonly TimeSpan IdleKeepAliveInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(2);

    private readonly MessageChannel _channel;
    private readonly HostOptions _options;
    private readonly SemaphoreSlim _credits = new(MaxFramesInFlight, MaxFramesInFlight);
    private readonly ConcurrentDictionary<uint, long> _sentAt = new();
    private readonly AdaptiveQuality _adaptive;
    private readonly DesktopThread? _desktop;
    private Rectangle _bounds;
    private Rectangle? _pendingBounds;
    private volatile bool _keyframeRequested = true;
    private string _codec;
    private volatile MediaTrackSender? _track;
    private readonly object _reportSync = new();
    private MediaReport _lastReport;

    /// <param name="desktop">서비스 모드(STEP 13): 캡처를 입력 데스크톱을 따라가는 스레드에서 실행</param>
    public VideoStreamer(MessageChannel channel, HostOptions options, Rectangle bounds, string codec, DesktopThread? desktop = null)
    {
        _desktop = desktop;
        _channel = channel;
        _options = options;
        _bounds = bounds;
        _codec = codec;
        _adaptive = new AdaptiveQuality(options.MaxFps, options.JpegQuality, options.MaxBitrateKbps, options.AdaptiveQuality);
    }

    public FrameStats Stats { get; } = new();

    public string Codec => _codec;

    /// <summary>ack 처리. 보내지 않은 프레임 번호면 무시합니다(credit이 부당하게 늘지 않도록).</summary>
    public void OnAck(uint frameId)
    {
        if (_sentAt.TryRemove(frameId, out long sentTicks))
        {
            Stats.AddRoundTrip(Stopwatch.GetElapsedTime(sentTicks));
            _credits.Release();
        }
    }

    public void RequestKeyframe() => _keyframeRequested = true;

    /// <summary>미디어 트랙에서 H.264 인코더를 만들 수 없는 등 트랙으로 보낼 수 없게 되었을 때</summary>
    public event Action<string>? TrackFailed;

    public bool UsingTrack => _track is not null;

    /// <summary>이제부터 영상을 WebRTC 영상 트랙으로 보냅니다 (STEP 14).</summary>
    public void AttachTrack(MediaTrackSender track)
    {
        track.KeyframeRequested += RequestKeyframe;
        track.Report += OnReport;
        _keyframeRequested = true;
        _track = track;
    }

    /// <summary>다시 기존 채널(ack 흐름 제어)로 보냅니다.</summary>
    public void DetachTrack()
    {
        if (Interlocked.Exchange(ref _track, null) is not { } track)
        {
            return;
        }

        track.KeyframeRequested -= RequestKeyframe;
        track.Report -= OnReport;
        _sentAt.Clear(); // 트랙으로 바꾸기 전에 보낸 프레임의 ack는 더 기다리지 않음
        while (_credits.CurrentCount < MaxFramesInFlight)
        {
            try
            {
                _credits.Release();
            }
            catch (SemaphoreFullException)
            {
                break;
            }
        }

        _keyframeRequested = true;
    }

    private void OnReport(MediaReport report)
    {
        lock (_reportSync)
        {
            _lastReport = report;
        }
    }

    private MediaReport LastReport
    {
        get
        {
            lock (_reportSync)
            {
                return _lastReport;
            }
        }
    }

    private void ReleaseCredit(bool took)
    {
        if (took)
        {
            _credits.Release();
        }
    }

    /// <summary>다른 모니터로 바꿉니다 (다음 프레임부터 적용)</summary>
    public void SwitchBounds(Rectangle bounds)
    {
        lock (this)
        {
            _pendingBounds = bounds;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        int desktopGeneration = 0;
        IScreenCapturer capturer = await OnDesktopAsync(() =>
        {
            desktopGeneration = _desktop?.Generation ?? 0;
            return ScreenCapturerFactory.Create(_bounds);
        });
        using var jpeg = new JpegFrameEncoder();
        H264Encoder? h264 = null;
        Bitmap? scaled = null;
        byte[] nv12 = [];
        int encoderBitrate = 0;

        TimeSpan nextFrameAt = TimeSpan.Zero;
        TimeSpan lastSentAt = TimeSpan.MinValue;
        TimeSpan lastAdapt = TimeSpan.Zero;
        TimeSpan lastStats = TimeSpan.Zero;
        TimeSpan lastEncoderReset = TimeSpan.FromSeconds(-10);
        MediaTrackSender? previousTrack = null;
        var clock = Stopwatch.StartNew();
        uint frameId = 0;
        bool captureBlockedLogged = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // 1) Client가 이전 프레임을 표시할 때까지 기다립니다 (흐름 제어). 미디어 트랙은 RTCP로 조절하므로 기다리지 않음
                MediaTrackSender? track = _track;
                if (!ReferenceEquals(track, previousTrack))
                {
                    // 채널 ↔ 트랙 전환: 새 경로의 첫 프레임은 키프레임이어야 상대가 디코딩을 시작할 수 있음
                    previousTrack = track;
                    _keyframeRequested = true;
                }

                bool tookCredit = track is null;
                if (tookCredit)
                {
                    long waitStart = Stopwatch.GetTimestamp();
                    await _credits.WaitAsync(cancellationToken);
                    Stats.AddCreditWait(Stopwatch.GetElapsedTime(waitStart));
                }

                string codecName = track is not null ? VideoCodecNames.H264 : _codec;

                // 2) 1초마다 화질 조절, 2초마다 상태 전송
                if (clock.Elapsed - lastAdapt >= TimeSpan.FromSeconds(1))
                {
                    var window = Stats.TakeWindow();
                    if (track is not null)
                    {
                        // 트랙 모드: ack가 없으므로 RTCP의 RTT와 손실률(10% 손실 = 대기 50%로 간주)
                        MediaReport report = LastReport;
                        window = window with { AverageRttMs = report.RoundTripMs, CreditWaitRatio = Math.Min(1, report.LossFraction * 5) };
                    }

                    if (_adaptive.Update(window.AverageRttMs, window.CreditWaitRatio, DateTime.UtcNow))
                    {
                        QualityLevel level = _adaptive.Current;
                        Log.Info($"Quality level {level.Index}: {level.Scale * 100:F0}% {level.Fps} fps " +
                                 (codecName == VideoCodecNames.H264 ? $"{level.BitrateKbps} kbps" : $"JPEG {level.JpegQuality}") +
                                 $" (RTT {window.AverageRttMs:F0} ms, wait {window.CreditWaitRatio:P0})");
                    }

                    lastAdapt = clock.Elapsed;
                    if (clock.Elapsed - lastStats >= StatsInterval)
                    {
                        lastStats = clock.Elapsed;
                        await SendStatsAsync(window, scaled?.Size ?? capturer.Frame.Size, h264, codecName, cancellationToken);
                    }
                }

                // 3) 최대 FPS를 넘지 않도록 기다립니다.
                QualityLevel quality = _adaptive.Current;
                TimeSpan wait = nextFrameAt - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken);
                }

                nextFrameAt = Max(nextFrameAt + TimeSpan.FromSeconds(1.0 / quality.Fps), clock.Elapsed);

                // 4) 모니터 전환
                Rectangle? newBounds;
                lock (this)
                {
                    newBounds = _pendingBounds;
                    _pendingBounds = null;
                }

                if (newBounds is { } nb && nb != _bounds)
                {
                    capturer.Dispose();
                    _bounds = nb;
                    capturer = await OnDesktopAsync(() => ScreenCapturerFactory.Create(_bounds));
                    _keyframeRequested = true;
                    Log.Info($"Switched capture to {nb.Width}x{nb.Height} at ({nb.X},{nb.Y})");
                }

                // 5) 화면 캡처
                bool changed;
                try
                {
                    changed = await OnDesktopAsync(() =>
                    {
                        // 로그인/잠금/UAC 화면으로 바뀌었으면 새 데스크톱에서 캡처 장치를 다시 만듭니다.
                        if (_desktop is not null && _desktop.Generation != desktopGeneration)
                        {
                            desktopGeneration = _desktop.Generation;
                            capturer.Dispose();
                            capturer = ScreenCapturerFactory.Create(_bounds);
                            _keyframeRequested = true;
                        }

                        return capturer.Capture();
                    });
                    captureBlockedLogged = false;
                }
                catch (Win32Exception exception)
                {
                    if (!captureBlockedLogged)
                    {
                        Log.Warn($"화면 캡처 불가 (잠금 화면/UAC 화면일 수 있음): {exception.Message}");
                        captureBlockedLogged = true;
                    }

                    ReleaseCredit(tookCredit);
                    await Task.Delay(500, cancellationToken);
                    continue;
                }

                // 6) 화면이 그대로면 보내지 않습니다. 단, 1초마다 한 번은 보내 연결 상태를 확인합니다.
                if (!changed && !_keyframeRequested && clock.Elapsed - lastSentAt < IdleKeepAliveInterval)
                {
                    ReleaseCredit(tookCredit);
                    continue;
                }

                // 7) 해상도 조절 (짝수 크기)
                Bitmap source = capturer.Frame;
                int width = Math.Max(2, (int)(capturer.Width * quality.Scale) & ~1);
                int height = Math.Max(2, (int)(capturer.Height * quality.Scale) & ~1);
                if (width != source.Width || height != source.Height)
                {
                    if (scaled is null || scaled.Width != width || scaled.Height != height)
                    {
                        scaled?.Dispose();
                        scaled = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                    }

                    using (Graphics graphics = Graphics.FromImage(scaled))
                    {
                        graphics.InterpolationMode = InterpolationMode.Bilinear;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                        graphics.DrawImage(source, 0, 0, width, height);
                    }

                    source = scaled;
                }
                else if (scaled is not null)
                {
                    scaled.Dispose();
                    scaled = null;
                }

                // 8) 압축
                ReadOnlyMemory<byte> payload;
                VideoCodec codec;
                if (codecName == VideoCodecNames.H264)
                {
                    bool newEncoder = false;
                    bool keyframeDeferred = false;

                    // 키프레임 요청: 하드웨어 인코더(AMD 등)는 ForceKeyFrame을 무시하기도 하므로 인코더를 새로 만들어
                    // 확실히 IDR(SPS/PPS 포함)을 만듭니다. 요청이 몰려도(PLI 반복) 1초에 한 번만.
                    if (h264 is not null && _keyframeRequested)
                    {
                        if (clock.Elapsed - lastEncoderReset >= TimeSpan.FromSeconds(1))
                        {
                            h264.Dispose();
                            h264 = null;
                            lastEncoderReset = clock.Elapsed;
                        }
                        else
                        {
                            keyframeDeferred = true; // 1초 뒤 다시 시도 (그동안은 ForceKeyFrame만)
                        }
                    }

                    if (h264 is null || h264.Width != width || h264.Height != height)
                    {
                        h264?.Dispose();
                        h264 = null;
                        try
                        {
                            h264 = H264Encoder.Create(width, height, _options.MaxFps, quality.BitrateKbps * 1000, _options.PreferHardwareEncoder);
                            encoderBitrate = quality.BitrateKbps;
                            newEncoder = true;
                        }
                        catch (Exception exception) when (exception is NotSupportedException or SharpGen.Runtime.SharpGenException)
                        {
                            Log.Warn($"H.264 인코더를 만들 수 없어 JPEG로 전환합니다: {exception.Message}");
                            _codec = VideoCodecNames.Jpeg;
                            if (track is not null)
                            {
                                DetachTrack();
                                TrackFailed?.Invoke($"H.264 인코더를 만들 수 없습니다: {exception.Message}");
                            }

                            ReleaseCredit(tookCredit);
                            continue;
                        }
                    }
                    else if (encoderBitrate != quality.BitrateKbps)
                    {
                        h264.SetBitrate(quality.BitrateKbps * 1000);
                        encoderBitrate = quality.BitrateKbps;
                    }

                    if (nv12.Length != Nv12Converter.Nv12Size(width, height))
                    {
                        nv12 = new byte[Nv12Converter.Nv12Size(width, height)];
                    }

                    ConvertToNv12(source, nv12);
                    bool keyframe = newEncoder || _keyframeRequested;
                    _keyframeRequested = keyframeDeferred;
                    byte[] encoded = h264.Encode(nv12, keyframe);
                    if (encoded.Length == 0)
                    {
                        ReleaseCredit(tookCredit); // 인코더가 아직 출력하지 않음 (비동기 하드웨어 인코더)
                        continue;
                    }

                    payload = encoded;
                    codec = VideoCodec.H264;
                }
                else
                {
                    _keyframeRequested = false;
                    payload = jpeg.Encode(source, quality.JpegQuality);
                    codec = VideoCodec.Jpeg;
                }

                // 9) 전송
                if (track is not null)
                {
                    // RTP 타임스탬프는 90 kHz: 직전 프레임과의 실제 간격
                    uint duration = lastSentAt == TimeSpan.MinValue
                        ? 3000
                        : (uint)Math.Clamp((clock.Elapsed - lastSentAt).TotalSeconds * MediaTrackSender.VideoClockRate, 1, MediaTrackSender.VideoClockRate * 5);
                    track.SendVideo(duration, payload.ToArray());
                    lastSentAt = clock.Elapsed;
                    Stats.AddFrame(payload.Length);
                    Stats.LogIfDue(codecName + " (track)", quality);
                    continue;
                }

                frameId++;
                var header = new VideoFrameHeader(frameId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), width, height, codec);
                _sentAt[frameId] = Stopwatch.GetTimestamp();
                await _channel.SendVideoFrameAsync(header, payload, cancellationToken);
                lastSentAt = clock.Elapsed;

                Stats.AddFrame(payload.Length);
                Stats.LogIfDue(_codec, quality);
            }
        }
        finally
        {
            capturer.Dispose();
            h264?.Dispose();
            scaled?.Dispose();
        }
    }

    private Task<T> OnDesktopAsync<T>(Func<T> function) =>
        _desktop is null ? Task.FromResult(function()) : _desktop.InvokeAsync(function);

    private async Task SendStatsAsync(FrameStats.Window window, Size size, H264Encoder? encoder, string codec, CancellationToken cancellationToken)
    {
        string? encoderName = codec == VideoCodecNames.H264 && encoder is not null
            ? $"{encoder.Name} ({(encoder.IsHardware ? "GPU" : "CPU")})"
            : null;
        await _channel.SendControlAsync(new StreamStatsMessage(
            Math.Round(window.Fps, 1),
            (int)window.Kbps,
            (int)window.AverageRttMs,
            _adaptive.Current.Index,
            codec,
            size.Width,
            size.Height,
            encoderName), cancellationToken);
    }

    private static unsafe void ConvertToNv12(Bitmap bitmap, byte[] nv12)
    {
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            Nv12Converter.BgraToNv12((byte*)data.Scan0, data.Stride, bitmap.Width, bitmap.Height, nv12);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    public void Dispose() => _credits.Dispose();
}

/// <summary>전송 통계: 5초마다 로그, 1초 창(window) 단위로 적응형 화질에 제공</summary>
internal sealed class FrameStats
{
    private readonly object _sync = new();
    private readonly Stopwatch _logWindow = Stopwatch.StartNew();
    private readonly Stopwatch _window = Stopwatch.StartNew();
    private int _logFrames;
    private long _logBytes;
    private double _logRtt;
    private int _logRtts;
    private int _frames;
    private long _bytes;
    private double _rttSum;
    private int _rtts;
    private double _waitMs;

    public readonly record struct Window(double Fps, double Kbps, double AverageRttMs, double CreditWaitRatio);

    public long TotalFrames { get; private set; }

    public void AddFrame(int bytes)
    {
        lock (_sync)
        {
            _frames++;
            _bytes += bytes;
            _logFrames++;
            _logBytes += bytes;
            TotalFrames++;
        }
    }

    public void AddRoundTrip(TimeSpan elapsed)
    {
        lock (_sync)
        {
            _rttSum += elapsed.TotalMilliseconds;
            _rtts++;
            _logRtt += elapsed.TotalMilliseconds;
            _logRtts++;
        }
    }

    public void AddCreditWait(TimeSpan elapsed)
    {
        lock (_sync)
        {
            _waitMs += elapsed.TotalMilliseconds;
        }
    }

    public Window TakeWindow()
    {
        lock (_sync)
        {
            double seconds = Math.Max(_window.Elapsed.TotalSeconds, 0.001);
            var window = new Window(
                _frames / seconds,
                _bytes * 8 / seconds / 1000,
                _rtts == 0 ? 0 : _rttSum / _rtts,
                Math.Clamp(_waitMs / (seconds * 1000), 0, 1));
            _frames = 0;
            _bytes = 0;
            _rttSum = 0;
            _rtts = 0;
            _waitMs = 0;
            _window.Restart();
            return window;
        }
    }

    public void LogIfDue(string codec, QualityLevel level)
    {
        lock (_sync)
        {
            double seconds = _logWindow.Elapsed.TotalSeconds;
            if (seconds < 5)
            {
                return;
            }

            double rtt = _logRtts == 0 ? 0 : _logRtt / _logRtts;
            Log.Info($"Stream: {codec} {_logFrames / seconds:F1} fps, {_logBytes * 8 / seconds / 1000:F0} kbps, " +
                     $"avg frame {(_logFrames == 0 ? 0 : _logBytes / _logFrames / 1024)} KB, RTT {rtt:F0} ms, level {level.Index}");
            _logFrames = 0;
            _logBytes = 0;
            _logRtt = 0;
            _logRtts = 0;
            _logWindow.Restart();
        }
    }
}
