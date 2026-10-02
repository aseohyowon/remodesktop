using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteDesktop.Protocol;

/// <summary>
/// LAN 자동 검색 (STEP 11, UDP 브로드캐스트)
///
/// Client → 255.255.255.255:50506 (및 각 네트워크의 브로드캐스트 주소)
///   {"type":"rd_discover","version":1,"nonce":"..."}
/// Host → Client (유니캐스트 응답)
///   {"type":"rd_host","version":1,"nonce":"...","host_id":"HOST-...","host_name":"PC","port":50505}
///
/// 검색은 "편의 기능"일 뿐 인증이 아닙니다. 찾은 PC에 연결할 때 평소처럼 TLS + 인증을 거칩니다.
/// Client는 자기가 보낸 nonce가 들어 있는 응답만 받습니다(엉뚱한 응답 무시).
/// </summary>
public static class LanDiscovery
{
    public const int DefaultPort = 50506;
    public const int MaxDatagram = 1024;

    public sealed record Probe(string Type, int Version, string Nonce);

    public sealed record HostReply(string Type, int Version, string Nonce, string HostId, string HostName, int Port);

    /// <summary>찾은 PC</summary>
    public sealed record FoundHost(string HostId, string HostName, IPAddress Address, int Port);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static byte[] CreateProbe(string nonce) =>
        JsonSerializer.SerializeToUtf8Bytes(new Probe("rd_discover", 1, nonce), JsonOptions);

    public static Probe? ParseProbe(ReadOnlySpan<byte> data)
    {
        try
        {
            var probe = data.Length <= MaxDatagram ? JsonSerializer.Deserialize<Probe>(data, JsonOptions) : null;
            return probe is { Type: "rd_discover", Version: 1 } && probe.Nonce is { Length: > 0 and <= 64 } ? probe : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static byte[] CreateReply(string nonce, string hostId, string hostName, int port) =>
        JsonSerializer.SerializeToUtf8Bytes(new HostReply("rd_host", 1, nonce, hostId, hostName, port), JsonOptions);

    public static HostReply? ParseReply(ReadOnlySpan<byte> data, string expectedNonce)
    {
        try
        {
            var reply = data.Length <= MaxDatagram ? JsonSerializer.Deserialize<HostReply>(data, JsonOptions) : null;
            return reply is { Type: "rd_host", Version: 1 } && reply.Nonce == expectedNonce
                   && reply.Port is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(reply.HostId)
                ? reply
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 같은 네트워크의 Host를 찾습니다. extraTargets: 브로드캐스트 외에 직접 물어볼 주소(테스트 등).
    /// </summary>
    public static async Task<IReadOnlyList<FoundHost>> DiscoverAsync(
        TimeSpan timeout,
        int port = DefaultPort,
        IEnumerable<IPAddress>? extraTargets = null,
        CancellationToken cancellationToken = default)
    {
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        byte[] probe = CreateProbe(nonce);

        using var socket = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        IgnoreConnectionReset(socket);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (IPAddress broadcast in SubnetBroadcastAddresses())
        {
            targets.Add(broadcast);
        }

        foreach (IPAddress extra in extraTargets ?? [])
        {
            targets.Add(extra);
        }

        foreach (IPAddress target in targets)
        {
            try
            {
                await socket.SendAsync(probe, new IPEndPoint(target, port), cancellationToken);
            }
            catch (SocketException)
            {
                // 일부 어댑터는 브로드캐스트를 보낼 수 없음
            }
        }

        var found = new Dictionary<string, FoundHost>();
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        try
        {
            while (true)
            {
                UdpReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(timer.Token);
                }
                catch (SocketException)
                {
                    continue; // 응답 없는 주소에서 온 ICMP 오류 등
                }

                if (ParseReply(result.Buffer, nonce) is { } reply)
                {
                    // 같은 Host가 여러 주소로 답하면 처음 것만 (보통 가장 가까운 경로)
                    found.TryAdd(reply.HostId, new FoundHost(reply.HostId, Clean(reply.HostName), result.RemoteEndPoint.Address, reply.Port));
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return found.Values.OrderBy(h => h.HostName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Windows는 UDP로 보낸 곳에 받는 프로그램이 없으면(ICMP port unreachable) 다음 수신에서 연결 재설정 오류를 냅니다.
    /// 검색에서는 의미 없는 오류이므로 끕니다 (SIO_UDP_CONNRESET).
    /// </summary>
    public static void IgnoreConnectionReset(UdpClient socket)
    {
        if (OperatingSystem.IsWindows())
        {
            const int SioUdpConnReset = unchecked((int)0x9800000C);
            socket.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
    }

    /// <summary>각 네트워크 어댑터의 브로드캐스트 주소 (예: 192.168.0.255)</summary>
    public static IEnumerable<IPAddress> SubnetBroadcastAddresses()
    {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily != AddressFamily.InterNetwork || info.IPv4Mask is null)
                {
                    continue;
                }

                byte[] address = info.Address.GetAddressBytes();
                byte[] mask = info.IPv4Mask.GetAddressBytes();
                for (int i = 0; i < 4; i++)
                {
                    address[i] = (byte)(address[i] | ~mask[i]);
                }

                yield return new IPAddress(address);
            }
        }
    }

    private static string Clean(string value)
    {
        string cleaned = new(value.Where(c => !char.IsControl(c)).Take(64).ToArray());
        return cleaned.Length == 0 ? "-" : cleaned;
    }
}
