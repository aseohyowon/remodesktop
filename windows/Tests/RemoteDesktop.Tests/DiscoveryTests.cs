using System.Net;
using System.Net.Sockets;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Tests;

/// <summary>STEP 11: LAN 자동 검색 (UDP)</summary>
public class DiscoveryTests
{
    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    [Fact]
    public void MessagesRoundTrip()
    {
        Assert.Equal("{\"type\":\"rd_discover\",\"version\":1,\"nonce\":\"ABC\"}", System.Text.Encoding.UTF8.GetString(LanDiscovery.CreateProbe("ABC")));
        Assert.Equal("ABC", LanDiscovery.ParseProbe(LanDiscovery.CreateProbe("ABC"))!.Nonce);

        byte[] reply = LanDiscovery.CreateReply("N1", "HOST-ABCDEF", "PC-Office", 50505);
        Assert.Equal("PC-Office", LanDiscovery.ParseReply(reply, "N1")!.HostName);
        Assert.Null(LanDiscovery.ParseReply(reply, "OTHER"));                // 다른 검색의 응답 무시
        Assert.Null(LanDiscovery.ParseProbe("not json"u8));
        Assert.Null(LanDiscovery.ParseProbe(new byte[2000]));                 // 너무 큼
        Assert.Null(LanDiscovery.ParseReply(LanDiscovery.CreateReply("N1", "HOST-A", "x", 0), "N1")); // 잘못된 포트
    }

    [Fact]
    public async Task FindsRunningHost()
    {
        int udp = FreeUdpPort();
        await using var host = new TestHost(discoveryPort: udp);
        await Task.Delay(500);

        var found = await LanDiscovery.DiscoverAsync(TimeSpan.FromSeconds(1.5), udp, [IPAddress.Loopback]);
        var me = Assert.Single(found, f => f.HostId == host.Settings.HostId);
        Assert.Equal(host.Port, me.Port);
        Assert.Equal(Environment.MachineName, me.HostName);
    }

    [Fact]
    public async Task HostWithDiscoveryOffDoesNotAnswer()
    {
        int udp = FreeUdpPort();
        await using var host = new TestHost(); // 검색 끔
        var found = await LanDiscovery.DiscoverAsync(TimeSpan.FromSeconds(1), udp, [IPAddress.Loopback]);
        Assert.DoesNotContain(found, f => f.HostId == host.Settings.HostId);
    }

    [Fact]
    public async Task RepliesAreRateLimited()
    {
        int udp = FreeUdpPort();
        await using var host = new TestHost(discoveryPort: udp);
        await Task.Delay(500);

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        for (int i = 0; i < 20; i++)
        {
            await client.SendAsync(LanDiscovery.CreateProbe($"N{i}"), new IPEndPoint(IPAddress.Loopback, udp));
        }

        int replies = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            while (true)
            {
                await client.ReceiveAsync(timeout.Token);
                replies++;
            }
        }
        catch (OperationCanceledException)
        {
        }

        Assert.InRange(replies, 1, 10); // 초당 5개 제한 (경계에 걸치면 최대 두 구간)
    }
}
