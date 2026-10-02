using System.Diagnostics;
using RemoteDesktop.Client;
using RemoteDesktop.Host.Engine;
using RemoteDesktop.Protocol;
using Xunit.Abstractions;

namespace RemoteDesktop.Tests;

public class AdaptiveQualityTests
{
    private static readonly DateTime T0 = new(2026, 1, 1);

    [Fact]
    public void DegradesAfterTwoBadSecondsAndRecoversAfterFiveGood()
    {
        var q = new AdaptiveQuality(maxFps: 30, maxJpegQuality: 70, maxBitrateKbps: 8000);
        Assert.Equal(0, q.Current.Index);

        Assert.False(q.Update(400, 0.1, T0));                // 나쁨 1초: 아직 유지
        Assert.True(q.Update(400, 0.1, T0.AddSeconds(1)));   // 나쁨 2초: 한 단계 낮춤
        Assert.Equal(1, q.Current.Index);

        Assert.False(q.Update(50, 0.9, T0.AddSeconds(2)));   // credit 대기도 "나쁨"
        Assert.False(q.Update(50, 0.9, T0.AddSeconds(2.5))); // 바꾼 지 2초가 안 됨
        Assert.True(q.Update(50, 0.9, T0.AddSeconds(3.5)));
        Assert.Equal(2, q.Current.Index);
        Assert.Equal(0.75, q.Current.Scale);

        for (int i = 0; i < 4; i++)
        {
            Assert.False(q.Update(30, 0.05, T0.AddSeconds(4 + i)));
        }

        Assert.True(q.Update(30, 0.05, T0.AddSeconds(9)));   // 좋음 5초: 한 단계 올림
        Assert.Equal(1, q.Current.Index);
    }

    [Fact]
    public void RespectsMaximumsAndCanBeDisabled()
    {
        var q = new AdaptiveQuality(maxFps: 15, maxJpegQuality: 50, maxBitrateKbps: 2000);
        Assert.Equal(15, q.Current.Fps);
        Assert.Equal(50, q.Current.JpegQuality);
        Assert.Equal(2000, q.Current.BitrateKbps);

        var fixedQuality = new AdaptiveQuality(30, 70, 8000, enabled: false);
        for (int i = 0; i < 10; i++)
        {
            Assert.False(fixedQuality.Update(1000, 1, T0.AddSeconds(i * 5)));
        }
    }

    [Fact]
    public void NeverGoesBeyondLowestLevel()
    {
        var q = new AdaptiveQuality(30, 70, 8000);
        for (int i = 0; i < 40; i++)
        {
            q.Update(1000, 1, T0.AddSeconds(i * 3));
        }

        Assert.Equal(4, q.Current.Index);
    }
}

/// <summary>STEP 8 통합: 실제 Host 엔진(Desktop Duplication + H.264) → Windows Client(H.264 디코딩)</summary>
public class StreamingIntegrationTests(ITestOutputHelper output)
{
    private static LoginRequest Code(TestHost host) => new(AuthMethods.AccessCode, host.Server.AccessCode, false);

    private async Task<(int Frames, Size Size, StreamStatsMessage? Stats)> ReceiveAsync(RemoteHostConnection connection, TimeSpan duration)
    {
        int frames = 0;
        Size size = default;
        StreamStatsMessage? stats = null;
        connection.StatsReceived += s => stats = s;
        connection.FrameReceived += (bitmap, header) =>
        {
            Interlocked.Increment(ref frames);
            size = bitmap.Size;
            bitmap.Dispose();
            _ = connection.SendAckAsync(header.FrameId);
        };
        connection.StartReceiving();
        await Task.Delay(duration);
        return (frames, size, stats);
    }

    [Fact]
    public async Task H264IsNegotiatedAndDecoded()
    {
        await using var host = new TestHost();
        using var connection = await host.ConnectAsync(Code(host));
        Assert.Equal(VideoCodecNames.H264, connection.VideoCodecName);
        Assert.NotEmpty(connection.Monitors);
        Assert.Contains(HostFeatures.MonitorSelect, connection.Features);

        var (frames, size, stats) = await ReceiveAsync(connection, TimeSpan.FromSeconds(4));
        output.WriteLine($"H.264: {frames} frames, {size}, stats: {stats}");
        Assert.True(frames >= 1, "최소 한 프레임은 디코딩되어야 합니다");
        Assert.Equal(connection.ScreenWidth, size.Width);
        Assert.NotNull(stats);
        Assert.Equal(VideoCodecNames.H264, stats!.Codec);
    }

    [Fact]
    public async Task JpegCanBeForced()
    {
        await using var host = new TestHost(new HostOptions { Codec = "jpeg" });
        using var connection = await host.ConnectAsync(Code(host));
        Assert.Equal(VideoCodecNames.Jpeg, connection.VideoCodecName);

        var (frames, size, _) = await ReceiveAsync(connection, TimeSpan.FromSeconds(3));
        Assert.True(frames >= 1);
        Assert.Equal(connection.ScreenWidth, size.Width);
    }

    [Fact]
    public async Task MonitorSelectionAndKeyframeRequestAreHandled()
    {
        await using var host = new TestHost();
        using var connection = await host.ConnectAsync(Code(host));
        var task = ReceiveAsync(connection, TimeSpan.FromSeconds(4));
        await Task.Delay(1000);

        // -1 = 모든 모니터를 합친 가상 화면
        Assert.True(await connection.SendAsync(new SelectMonitorMessage(-1)));
        Assert.True(await connection.SendAsync(new KeyframeRequestMessage()));
        var (frames, size, _) = await task;

        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        output.WriteLine($"{frames} frames, last {size}, virtual screen {virtualScreen.Size}");
        Assert.Equal(virtualScreen.Width & ~1, size.Width);
    }
}
