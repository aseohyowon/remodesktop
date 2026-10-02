using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// LAN 자동 검색 응답 (STEP 11). UDP 50506에서 검색 요청을 받으면 Host ID·이름·포트를 알려 줍니다.
/// - 사설망/루프백 주소에서 온 요청에만 응답합니다 (LAN 리스너와 같은 규칙).
/// - 같은 주소에는 1초에 5번까지만 응답합니다 (반사 공격·폭주 방지). 응답은 요청보다 크지 않습니다.
/// - 비밀 정보는 보내지 않습니다. 연결하려면 평소처럼 인증해야 합니다.
/// </summary>
internal sealed class DiscoveryResponder(HostServer server)
{
    private const int MaxRepliesPerSecond = 5;
    private readonly ConcurrentDictionary<IPAddress, (long Second, int Count)> _rates = new();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        UdpClient socket;
        try
        {
            socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            LanDiscovery.IgnoreConnectionReset(socket);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, server.Options.DiscoveryPort));
        }
        catch (SocketException exception)
        {
            Log.Warn($"LAN 검색 응답을 시작할 수 없습니다 (UDP {server.Options.DiscoveryPort}): {exception.Message}");
            return;
        }

        using (socket)
        {
            Log.Info($"LAN discovery on UDP {server.Options.DiscoveryPort}");
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult request;
                try
                {
                    request = await socket.ReceiveAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    continue; // ICMP port unreachable 등
                }

                IPAddress from = request.RemoteEndPoint.Address;
                if ((!server.Options.AllowPublicAddresses && !NetworkAddress.IsPrivate(from)) || !Allow(from))
                {
                    continue;
                }

                if (LanDiscovery.ParseProbe(request.Buffer) is not { } probe)
                {
                    continue;
                }

                byte[] reply = LanDiscovery.CreateReply(probe.Nonce, server.Settings.HostId, Environment.MachineName, server.Options.Port);
                try
                {
                    await socket.SendAsync(reply, request.RemoteEndPoint, cancellationToken);
                    Log.Debug($"Discovery reply to {request.RemoteEndPoint}");
                }
                catch (SocketException)
                {
                }
            }
        }
    }

    private bool Allow(IPAddress address)
    {
        long second = Environment.TickCount64 / 1000;
        var (_, count) = _rates.AddOrUpdate(address, (second, 1), (_, value) => value.Second == second ? (second, value.Count + 1) : (second, 1));
        if (_rates.Count > 1000)
        {
            _rates.Clear();
        }

        return count <= MaxRepliesPerSecond;
    }
}
