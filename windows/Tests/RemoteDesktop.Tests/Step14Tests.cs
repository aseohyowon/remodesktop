using Concentus;
using RemoteDesktop.Client;
using RemoteDesktop.Host.Engine;
using RemoteDesktop.Media;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport.WebRtc;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Xunit.Abstractions;

namespace RemoteDesktop.Tests;

/// <summary>STEP 14: 모바일·맥 앱용 WebRTC 미디어 트랙 (H.264 영상 + Opus 소리)</summary>
public class Step14Tests(ITestOutputHelper output)
{
    // ---------------- Opus ----------------

    [Fact]
    public void OpusPacketizerEncodes48kStereo()
    {
        var packetizer = new OpusPacketizer(48000, 2);
        float[] input = Sine(48000, 2, 440, seconds: 1);
        var packets = new List<byte[]>();
        for (int offset = 0; offset < input.Length; offset += 480 * 2) // 10 ms씩 (WASAPI처럼 조각내서)
        {
            packets.AddRange(packetizer.Add(input.AsSpan(offset, Math.Min(480 * 2, input.Length - offset))));
        }

        Assert.Equal(50, packets.Count); // 20 ms × 50 = 1초
        Assert.All(packets, p => Assert.InRange(p.Length, 10, 400));
        output.WriteLine($"Opus: {packets.Sum(p => p.Length) * 8 / 1000} kbps");

        short[] decoded = Decode(packets);
        double frequency = ZeroCrossingFrequency(decoded[(48000 / 2 * 2)..], 48000); // 앞 0.5초는 인코더 지연 무시
        output.WriteLine($"decoded frequency ≈ {frequency:F0} Hz");
        Assert.InRange(frequency, 420, 460);
    }

    [Fact]
    public void OpusPacketizerResamples44kMono()
    {
        var packetizer = new OpusPacketizer(44100, 1);
        float[] input = Sine(44100, 1, 1000, seconds: 2);
        var packets = new List<byte[]>();
        for (int offset = 0; offset < input.Length; offset += 441)
        {
            packets.AddRange(packetizer.Add(input.AsSpan(offset, Math.Min(441, input.Length - offset))));
        }

        Assert.InRange(packets.Count, 99, 100); // 2초 = 20 ms × 100
        double frequency = ZeroCrossingFrequency(Decode(packets)[(48000 * 2)..], 48000);
        output.WriteLine($"44.1 kHz 1000 Hz → 48 kHz, decoded ≈ {frequency:F0} Hz");
        Assert.InRange(frequency, 970, 1030);
    }

    [Fact]
    public void RtcpRoundTripCalculation()
    {
        DateTime now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        double ntp = (now - new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        uint Middle(double seconds) => (uint)((ulong)(seconds * 65536) & 0xFFFFFFFF);

        // SR을 0.5초 전에 보냈고, 상대가 0.42초 붙잡고 있었다 → RTT 80 ms
        uint lsr = Middle(ntp - 0.5);
        uint dlsr = (uint)(0.42 * 65536);
        Assert.InRange(MediaTrackSender.RoundTripMs(lsr, dlsr, now), 79, 81);
        Assert.Equal(0, MediaTrackSender.RoundTripMs(0, 0, now));
    }

    // ---------------- Host 세션 통합: C# WebRTC 수신기를 모바일 앱 대신 사용 ----------------

    /// <summary>flutter_webrtc가 하는 일을 SIPSorcery로 흉내: recvonly 영상/소리 offer → answer → ICE → 영상 수신</summary>
    private sealed class FakeMobileReceiver : IDisposable
    {
        private readonly RemoteHostConnection _connection;

        public FakeMobileReceiver(RemoteHostConnection connection, bool supportH264 = true)
        {
            _connection = connection;
            Pc = new RTCPeerConnection(null);
            VideoFormat video = supportH264
                ? new VideoFormat(VideoCodecsEnum.H264, 96, 90000, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f")
                : new VideoFormat(VideoCodecsEnum.VP8, 97, 90000);
            Pc.addTrack(new MediaStreamTrack(video, MediaStreamStatusEnum.RecvOnly));
            Pc.addTrack(new MediaStreamTrack(new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 2, "minptime=10;useinbandfec=1"), MediaStreamStatusEnum.RecvOnly));
            Pc.onicecandidate += c =>
            {
                if (c is not null)
                {
                    _ = connection.SendAsync(new MediaIceMessage(c.candidate, c.sdpMid, c.sdpMLineIndex));
                }
            };
            Pc.OnVideoFrameReceived += (_, _, frame, _) =>
            {
                lock (Frames)
                {
                    Frames.Add(frame);
                }
            };
            Pc.OnRtpPacketReceived += (_, media, packet) =>
            {
                if (media == SDPMediaTypesEnum.video)
                {
                    Interlocked.Increment(ref VideoRtp);
                    LastVideoPt = packet.Header.PayloadType;
                }

                if (media == SDPMediaTypesEnum.audio)
                {
                    lock (AudioPackets)
                    {
                        AudioPackets.Add(packet.Payload);
                    }
                }
            };
            connection.ControlReceived += OnControl;
        }

        public RTCPeerConnection Pc { get; }

        public int VideoRtp;

        public int LastVideoPt;

        public List<byte[]> Frames { get; } = new();

        public List<byte[]> AudioPackets { get; } = new();

        public TaskCompletionSource<MediaStateMessage> State { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task OfferAsync()
        {
            RTCSessionDescriptionInit offer = Pc.createOffer(null);
            await Pc.setLocalDescription(offer);
            await _connection.SendAsync(new MediaOfferMessage(offer.sdp));
        }

        public void RequestKeyframe()
        {
            uint mediaSsrc = Pc.VideoStream.RemoteTrack?.Ssrc ?? 0;
            Pc.SendRtcpFeedback(SDPMediaTypesEnum.video, new RTCPFeedback(Pc.VideoStream.LocalTrack?.Ssrc ?? 1, mediaSsrc, PSFBFeedbackTypesEnum.PLI));
        }

        public int KeyframeCount()
        {
            lock (Frames)
            {
                return Frames.Count(IsKeyframe);
            }
        }

        private void OnControl(ControlMessage message)
        {
            switch (message)
            {
                case MediaAnswerMessage answer:
                    Pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answer.Sdp });
                    break;
                case MediaIceMessage ice when !string.IsNullOrEmpty(ice.Candidate):
                    Pc.addIceCandidate(new RTCIceCandidateInit { candidate = ice.Candidate, sdpMid = ice.SdpMid, sdpMLineIndex = (ushort)(ice.SdpMlineIndex ?? 0) });
                    break;
                case MediaStateMessage state:
                    State.TrySetResult(state);
                    break;
            }
        }

        public void Dispose()
        {
            _connection.ControlReceived -= OnControl;
            Pc.close();
            Pc.Dispose();
        }
    }

    [Fact]
    public async Task MobileReceivesH264OverMediaTrackAndChannelFramesStop()
    {
        await using var host = new TestHost(new HostOptions());
        using var connection = await host.ConnectAsync(new LoginRequest(AuthMethods.AccessCode, host.Server.AccessCode, false));
        Assert.Contains(HostFeatures.MediaTrack, connection.Features);

        int channelFrames = 0;
        string? statsCodec = null;
        connection.FrameReceived += (bitmap, header) =>
        {
            Interlocked.Increment(ref channelFrames);
            bitmap.Dispose();
            _ = connection.SendAckAsync(header.FrameId);
        };
        connection.StatsReceived += stats => statsCodec = stats.Codec;
        connection.StartReceiving();

        using var receiver = new FakeMobileReceiver(connection);
        await receiver.OfferAsync();
        MediaStateMessage state = await receiver.State.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(state.Active, state.Error);

        // 트랙으로 영상이 옴
        for (int i = 0; i < 50 && receiver.KeyframeCount() == 0; i++)
        {
            await Task.Delay(100);
        }

        output.WriteLine($"video rtp {receiver.VideoRtp} pt {receiver.LastVideoPt}, frames {receiver.Frames.Count}");
        Assert.True(receiver.KeyframeCount() > 0, "키프레임(SPS/PPS/IDR)이 트랙으로 와야 함");
        int channelAtSwitch = Volatile.Read(ref channelFrames);

        // 받은 Annex-B를 실제 H.264 디코더로 풀어 화면 크기의 그림이 나오는지
        byte[] keyframe;
        lock (receiver.Frames)
        {
            keyframe = receiver.Frames.First(IsKeyframe);
        }

        using (var decoder = new H264Decoder())
        {
            byte[]? bgra = decoder.Decode(keyframe, connection.ScreenWidth, connection.ScreenHeight)
                           ?? decoder.Decode(keyframe, connection.ScreenWidth, connection.ScreenHeight);
            output.WriteLine($"keyframe {keyframe.Length} bytes → decoded {decoder.LastWidth}x{decoder.LastHeight}");
            Assert.NotNull(bgra);
        }

        // PLI(키프레임 요청) → 새 키프레임
        await Task.Delay(1500);
        int keyframesBefore = receiver.KeyframeCount();
        receiver.RequestKeyframe();
        for (int i = 0; i < 30 && receiver.KeyframeCount() == keyframesBefore; i++)
        {
            await Task.Delay(100);
        }

        Assert.True(receiver.KeyframeCount() > keyframesBefore, "PLI 후 키프레임이 와야 함");

        // 전환 뒤에는 채널 프레임이 더 오지 않음 (전환 순간 이미 보낸 1~2개만 허용)
        await Task.Delay(2500);
        int channelAfter = Volatile.Read(ref channelFrames) - channelAtSwitch;
        output.WriteLine($"track frames {receiver.Frames.Count}, channel frames after switch {channelAfter}, stats codec {statsCodec}");
        Assert.InRange(channelAfter, 0, 2);
        Assert.Equal(VideoCodecNames.H264, statsCodec);
    }

    /// <summary>
    /// 소리 트랙: 실제로 스피커에 작은 440 Hz 톤을 1.5초 재생해 루프백 캡처 → Opus → RTP를 확인합니다.
    /// 소리가 나므로 환경 변수 REMODESK_AUDIO_PROBE=1 일 때만 실행합니다.
    /// </summary>
    [Fact]
    public async Task AudioArrivesAsOpusOverMediaTrack()
    {
        if (Environment.GetEnvironmentVariable("REMODESK_AUDIO_PROBE") != "1")
        {
            output.WriteLine("REMODESK_AUDIO_PROBE=1 이 아니라 건너뜀 (스피커로 톤을 재생함)");
            return;
        }

        await using var host = new TestHost(new HostOptions());
        using var connection = await host.ConnectAsync(new LoginRequest(AuthMethods.AccessCode, host.Server.AccessCode, false));
        connection.FrameReceived += (bitmap, header) =>
        {
            bitmap.Dispose();
            _ = connection.SendAckAsync(header.FrameId);
        };
        connection.StartReceiving();
        using var receiver = new FakeMobileReceiver(connection);
        await receiver.OfferAsync();
        Assert.True((await receiver.State.Task.WaitAsync(TimeSpan.FromSeconds(20))).Active);

        await connection.SendAsync(new AudioControlMessage("start"));
        await Task.Delay(500);
        var tone = new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2) { Frequency = 440, Gain = 0.05 };
        using (var player = new NAudio.Wave.WaveOutEvent())
        {
            player.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(new NAudio.Wave.SampleProviders.OffsetSampleProvider(tone) { TakeSamples = 48000 * 2 * 3 / 2 }));
            player.Play();
            await Task.Delay(2000);
        }

        await connection.SendAsync(new AudioControlMessage("stop"));
        List<byte[]> packets;
        lock (receiver.AudioPackets)
        {
            packets = receiver.AudioPackets.ToList();
        }

        short[] pcm = Decode(packets);
        // 톤 앞뒤의 무음은 빼고 소리가 있는 구간만 측정
        int first = Array.FindIndex(pcm, v => Math.Abs((int)v) > 300) & ~1;
        int last = Array.FindLastIndex(pcm, v => Math.Abs((int)v) > 300) & ~1;
        Assert.True(first >= 0 && last > first, "소리가 있어야 함");
        double frequency = ZeroCrossingFrequency(pcm[first..last], 48000);
        output.WriteLine($"Opus packets {packets.Count} ({packets.Count * 20} ms), {packets.Sum(p => p.Length) * 8 / Math.Max(1, packets.Count * 20)} kbps, decoded ≈ {frequency:F0} Hz");
        Assert.InRange(packets.Count, 50, 150); // 1.5초 톤 ≈ 75개
        Assert.InRange(frequency, 400, 480);
    }

    /// <summary>libwebrtc(Chrome/Android/iOS)가 실제로 만드는 형태의 offer: VP8, VP9, H.264 여러 개(packetization-mode 0/1), AV1</summary>
    internal const string LibWebRtcOffer = """
v=0
o=- 4611731400430051336 2 IN IP4 127.0.0.1
s=-
t=0 0
a=group:BUNDLE 0 1
a=extmap-allow-mixed
a=msid-semantic: WMS
m=video 9 UDP/TLS/RTP/SAVPF 96 97 98 99 104 105 106 107 108 109 127 125 39 40 45 46
c=IN IP4 0.0.0.0
a=rtcp:9 IN IP4 0.0.0.0
a=ice-ufrag:Abcd
a=ice-pwd:abcdefghijklmnopqrstuvwx
a=ice-options:trickle
a=fingerprint:sha-256 0A:1B:2C:3D:4E:5F:60:71:82:93:A4:B5:C6:D7:E8:F9:0A:1B:2C:3D:4E:5F:60:71:82:93:A4:B5:C6:D7:E8:F9
a=setup:actpass
a=mid:0
a=extmap:1 urn:ietf:params:rtp-hdrext:toffset
a=extmap:3 http://www.webrtc.org/experiments/rtp-hdrext/abs-send-time
a=recvonly
a=rtcp-mux
a=rtcp-rsize
a=rtpmap:96 VP8/90000
a=rtcp-fb:96 goog-remb
a=rtcp-fb:96 transport-cc
a=rtcp-fb:96 ccm fir
a=rtcp-fb:96 nack
a=rtcp-fb:96 nack pli
a=rtpmap:97 rtx/90000
a=fmtp:97 apt=96
a=rtpmap:98 VP9/90000
a=fmtp:98 profile-id=0
a=rtpmap:99 rtx/90000
a=fmtp:99 apt=98
a=rtpmap:104 H264/90000
a=rtcp-fb:104 nack pli
a=fmtp:104 level-asymmetry-allowed=1;packetization-mode=0;profile-level-id=42001f
a=rtpmap:105 rtx/90000
a=fmtp:105 apt=104
a=rtpmap:106 H264/90000
a=rtcp-fb:106 ccm fir
a=rtcp-fb:106 nack
a=rtcp-fb:106 nack pli
a=fmtp:106 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f
a=rtpmap:107 rtx/90000
a=fmtp:107 apt=106
a=rtpmap:108 H264/90000
a=fmtp:108 level-asymmetry-allowed=1;packetization-mode=0;profile-level-id=42e01f
a=rtpmap:109 rtx/90000
a=fmtp:109 apt=108
a=rtpmap:127 H264/90000
a=fmtp:127 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=4d001f
a=rtpmap:125 rtx/90000
a=fmtp:125 apt=127
a=rtpmap:39 H264/90000
a=fmtp:39 level-asymmetry-allowed=1;packetization-mode=0;profile-level-id=4d001f
a=rtpmap:40 rtx/90000
a=fmtp:40 apt=39
a=rtpmap:45 AV1/90000
a=rtpmap:46 rtx/90000
a=fmtp:46 apt=45
m=audio 9 UDP/TLS/RTP/SAVPF 111 63 9 0 8 13 110 126
c=IN IP4 0.0.0.0
a=rtcp:9 IN IP4 0.0.0.0
a=ice-ufrag:Abcd
a=ice-pwd:abcdefghijklmnopqrstuvwx
a=ice-options:trickle
a=fingerprint:sha-256 0A:1B:2C:3D:4E:5F:60:71:82:93:A4:B5:C6:D7:E8:F9:0A:1B:2C:3D:4E:5F:60:71:82:93:A4:B5:C6:D7:E8:F9
a=setup:actpass
a=mid:1
a=extmap:14 urn:ietf:params:rtp-hdrext:ssrc-audio-level
a=recvonly
a=rtcp-mux
a=rtpmap:111 opus/48000/2
a=rtcp-fb:111 transport-cc
a=fmtp:111 minptime=10;useinbandfec=1
a=rtpmap:63 red/48000/2
a=fmtp:63 111/111
a=rtpmap:9 G722/8000
a=rtpmap:0 PCMU/8000
a=rtpmap:8 PCMA/8000
a=rtpmap:13 CN/8000
a=rtpmap:110 telephone-event/48000
a=rtpmap:126 telephone-event/8000

""";

    [Fact]
    public async Task AnswersLibWebRtcOfferWithPacketizationMode1()
    {
        using var sender = new MediaTrackSender(null, _ => Task.CompletedTask);
        string answer = await sender.AcceptOfferAsync(LibWebRtcOffer.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        output.WriteLine(answer);

        string videoSection = answer[answer.IndexOf("m=video", StringComparison.Ordinal)..answer.IndexOf("m=audio", StringComparison.Ordinal)];
        string[] payloads = videoSection.Split("\r\n")[0].Split(' ')[3..];
        string fmtp = videoSection.Split("\r\n").First(l => l.StartsWith($"a=fmtp:{payloads[0]} ", StringComparison.Ordinal));
        Assert.Contains("H264", videoSection);
        Assert.Contains("packetization-mode=1", fmtp);
        Assert.Contains("a=sendonly", videoSection);
        Assert.Contains($"a=rtcp-fb:{payloads[0]} nack pli", videoSection); // 앱이 키프레임을 요청할 수 있게
        Assert.Contains($"a=rtcp-fb:{payloads[0]} ccm fir", videoSection);
        Assert.Contains("opus/48000/2", answer);
    }

    [Fact]
    public async Task FallsBackToChannelWhenClientCannotDecodeH264()
    {
        await using var host = new TestHost(new HostOptions { Codec = "auto" });
        using var connection = await host.ConnectAsync(new LoginRequest(AuthMethods.AccessCode, host.Server.AccessCode, false));
        int channelFrames = 0;
        connection.FrameReceived += (bitmap, header) =>
        {
            Interlocked.Increment(ref channelFrames);
            bitmap.Dispose();
            _ = connection.SendAckAsync(header.FrameId);
        };
        connection.StartReceiving();

        using var receiver = new FakeMobileReceiver(connection, supportH264: false);
        await receiver.OfferAsync();
        MediaStateMessage state = await receiver.State.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(state.Active);
        output.WriteLine($"fallback reason: {state.Error}");

        int before = Volatile.Read(ref channelFrames);
        for (int i = 0; i < 30 && Volatile.Read(ref channelFrames) <= before; i++)
        {
            await Task.Delay(100);
        }

        Assert.True(channelFrames > before, "기존 채널로 화면이 계속 와야 함");
    }

    [Fact]
    public async Task MediaTrackOffWhenJpegOnlyOrDisabled()
    {
        await using (var host = new TestHost(new HostOptions { Codec = "jpeg" }))
        {
            using var connection = await host.ConnectAsync(new LoginRequest(AuthMethods.AccessCode, host.Server.AccessCode, false));
            Assert.DoesNotContain(HostFeatures.MediaTrack, connection.Features);
        }

        await using (var host = new TestHost(HostOptions.Parse(["--no-media-track"])))
        {
            using var connection = await host.ConnectAsync(new LoginRequest(AuthMethods.AccessCode, host.Server.AccessCode, false));
            Assert.DoesNotContain(HostFeatures.MediaTrack, connection.Features);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NewEncoderStartsWithKeyframe(bool hardware)
    {
        using var encoder = H264Encoder.Create(1280, 720, 30, 4_000_000, hardware);
        byte[] nv12 = new byte[Nv12Converter.Nv12Size(1280, 720)];
        new Random(1).NextBytes(nv12);
        var outputs = new List<byte[]>();
        for (int i = 0; i < 4; i++)
        {
            outputs.Add(encoder.Encode(nv12, i == 0));
        }

        foreach (byte[] o in outputs)
        {
            output.WriteLine($"{encoder.Name}: {o.Length} bytes {Convert.ToHexString(o.AsSpan(0, Math.Min(40, o.Length)))}");
        }

        Assert.True(IsKeyframe(outputs.First(o => o.Length > 0)), "첫 출력은 SPS/PPS/IDR이어야 함");
    }

    // ---------------- 도움 함수 ----------------

    /// <summary>Annex-B 안에 IDR(5) 또는 SPS(7) NAL이 있으면 키프레임</summary>
    private static bool IsKeyframe(byte[] annexB)
    {
        for (int i = 0; i + 3 < annexB.Length; i++)
        {
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
            {
                int type = annexB[i + 3] & 0x1F;
                if (type is 5 or 7)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static float[] Sine(int rate, int channels, double frequency, double seconds)
    {
        int frames = (int)(rate * seconds);
        float[] data = new float[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            float value = (float)(0.3 * Math.Sin(2 * Math.PI * frequency * i / rate));
            for (int c = 0; c < channels; c++)
            {
                data[i * channels + c] = value;
            }
        }

        return data;
    }

    private static short[] Decode(List<byte[]> packets)
    {
        IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(48000, 2);
        var pcm = new List<short>();
        short[] frame = new short[960 * 2];
        foreach (byte[] packet in packets)
        {
            int samples = decoder.Decode(packet, frame, 960, false);
            pcm.AddRange(frame.AsSpan(0, samples * 2).ToArray());
        }

        return pcm.ToArray();
    }

    /// <summary>왼쪽 채널의 0 교차 횟수로 주파수 추정</summary>
    private static double ZeroCrossingFrequency(short[] stereo, int rate)
    {
        int crossings = 0;
        int frames = stereo.Length / 2;
        for (int i = 1; i < frames; i++)
        {
            if ((stereo[(i - 1) * 2] < 0) != (stereo[i * 2] < 0))
            {
                crossings++;
            }
        }

        return crossings / 2.0 / ((double)frames / rate);
    }
}
