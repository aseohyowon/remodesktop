using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;
using RemoteDesktop.Client;
using RemoteDesktop.Host.Engine;
using RemoteDesktop.Protocol;
using Xunit.Abstractions;

namespace RemoteDesktop.Tests;

/// <summary>실제 화면을 바꾸지 않는 가짜 해상도 제어기</summary>
internal sealed class FakeDisplay : IDisplayController
{
    private readonly Size _original;
    private Size _size;

    public FakeDisplay(Size size)
    {
        _original = size;
        _size = size;
    }

    public List<string> Calls { get; } = new();

    public Rectangle Current(string deviceName) => new(Point.Empty, _size);

    public DisplayMode[] Modes(string deviceName) =>
        [new(_original.Width, _original.Height), new(1280, 720), new(1024, 768)];

    public string? Apply(string deviceName, DisplayMode mode)
    {
        Calls.Add($"apply {mode.Width}x{mode.Height}");
        _size = new Size(mode.Width, mode.Height);
        return null;
    }

    public void Restore(string deviceName)
    {
        Calls.Add("restore");
        _size = _original;
    }
}

/// <summary>STEP 12: 해상도 변경, 절전 방지, Wake-on-LAN</summary>
public class Step12Tests(ITestOutputHelper output)
{
    // ---------------- Wake-on-LAN ----------------

    [Theory]
    [InlineData("AA-BB-CC-DD-EE-FF", true)]
    [InlineData("aa:bb:cc:dd:ee:ff", true)]
    [InlineData("AABBCCDDEEFF", true)]
    [InlineData("00-00-00-00-00-00", false)]
    [InlineData("FF-FF-FF-FF-FF-FF", false)]
    [InlineData("AA-BB-CC", false)]
    [InlineData("GG-BB-CC-DD-EE-FF", false)]
    [InlineData(null, false)]
    public void ParsesMacAddresses(string? mac, bool valid) => Assert.Equal(valid, WakeOnLan.IsValidMac(mac));

    [Fact]
    public void MagicPacketFormat()
    {
        WakeOnLan.TryParseMac("01-23-45-67-89-AB", out byte[] mac);
        byte[] packet = WakeOnLan.CreateMagicPacket(mac);
        Assert.Equal(102, packet.Length);
        Assert.All(packet[..6], b => Assert.Equal(0xFF, b));
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(mac, packet[(6 + i * 6)..(12 + i * 6)]);
        }
    }

    [Fact]
    public async Task WakeSendsMagicPacketOverUdp()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        int sent = await WakeOnLan.WakeAsync(["01-23-45-67-89-AB"], [IPAddress.Loopback], port);
        Assert.Equal(1, sent);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        UdpReceiveResult received = await listener.ReceiveAsync(timeout.Token);
        Assert.Equal(102, received.Buffer.Length);
        Assert.Equal(0x01, received.Buffer[6]);
    }

    [Fact]
    public void LocalMacAddressesLookValid()
    {
        string[] macs = WakeOnLan.LocalMacAddresses();
        output.WriteLine(string.Join(", ", macs));
        Assert.All(macs, mac => Assert.True(WakeOnLan.IsValidMac(mac)));
    }

    // ---------------- 해상도 (실제 드라이버, 읽기 + CDS_TEST만) ----------------

    [Fact]
    public void RealDisplayDriverModesAndValidation()
    {
        NativeMethods.EnablePerMonitorDpiAwareness();
        string device = Screen.PrimaryScreen!.DeviceName;
        var controller = new WindowsDisplayController();

        Rectangle current = controller.Current(device);
        DisplayMode[] modes = controller.Modes(device);
        output.WriteLine($"{device}: current {current.Width}x{current.Height}, modes: {string.Join(", ", modes.Select(m => $"{m.Width}x{m.Height}"))}");

        Assert.Contains(new DisplayMode(current.Width, current.Height), modes);

        // CDS_TEST: 바꿀 수 있는지 확인만 하고 실제로는 바꾸지 않습니다.
        Assert.Null(controller.Validate(device, new DisplayMode(current.Width, current.Height)));
        foreach (DisplayMode mode in modes.Take(3))
        {
            Assert.Null(controller.Validate(device, mode));
        }

        Assert.NotNull(controller.Validate(device, new DisplayMode(12345, 777)));
        Assert.Equal(current, controller.Current(device)); // 그대로인지
    }

    // ---------------- 절전 방지 ----------------

    [Fact]
    public void SleepBlockerActivatesAndReleases()
    {
        using var blocker = new SleepBlocker();
        Assert.True(blocker.Active);
    }

    // ---------------- 세션 통합 (가짜 해상도 제어기) ----------------

    [Fact]
    public async Task ClientChangesResolutionAndHostRestoresOnDisconnect()
    {
        Rectangle screen = Screen.PrimaryScreen!.Bounds;
        var fake = new FakeDisplay(screen.Size);
        await using var host = new TestHost(new HostOptions { Codec = "jpeg" }, viewOnly: false);
        host.Server.DisplayController = fake;
        host.Server.MonitorDeviceName = _ => "FAKE";

        var connection = await host.ConnectAsync(new LoginRequest(AuthMethods.AccessCode, host.Server.AccessCode, false));
        Assert.Contains(HostFeatures.Display, connection.Features);
        Assert.All(connection.MacAddresses, mac => Assert.True(WakeOnLan.IsValidMac(mac)));

        var features = new ClientFeatures(connection);
        Size lastFrame = default;
        connection.FrameReceived += (bitmap, header) =>
        {
            lastFrame = bitmap.Size;
            bitmap.Dispose();
            _ = connection.SendAckAsync(header.FrameId);
        };
        connection.StartReceiving();

        DisplayModesMessage modes = await features.GetDisplayModesAsync(CancellationToken.None);
        Assert.Contains(new DisplayMode(1280, 720), modes.Modes);
        Assert.Equal(new DisplayMode(screen.Width, screen.Height), modes.Original);

        // 지원 목록에 없는 해상도 → 거부
        DisplayResultMessage bad = await features.SetResolutionAsync(800, 600, CancellationToken.None);
        Assert.False(bad.Success);

        // 1280x720으로 → 다음 프레임 크기도 1280x720
        DisplayResultMessage ok = await features.SetResolutionAsync(1280, 720, CancellationToken.None);
        Assert.True(ok.Success, ok.Error);
        for (int i = 0; i < 30 && lastFrame.Width != 1280; i++)
        {
            await connection.SendAsync(new KeyframeRequestMessage());
            await Task.Delay(200);
        }

        Assert.Equal(new Size(1280, 720), lastFrame);

        // 연결을 끊으면 원래대로
        features.Dispose();
        connection.Dispose();
        for (int i = 0; i < 30 && !fake.Calls.Contains("restore"); i++)
        {
            await Task.Delay(100);
        }

        Assert.Equal(["apply 1280x720", "restore"], fake.Calls);
    }

    [Fact]
    public async Task ResolutionChangeIsOffInViewOnly()
    {
        await using var host = new TestHost(new HostOptions { Codec = "jpeg" }, viewOnly: true);
        host.Server.DisplayController = new FakeDisplay(new Size(1920, 1080));
        using var connection = await host.ConnectAsync(new LoginRequest(AuthMethods.AccessCode, host.Server.AccessCode, false));
        Assert.DoesNotContain(HostFeatures.Display, connection.Features);
    }
}
