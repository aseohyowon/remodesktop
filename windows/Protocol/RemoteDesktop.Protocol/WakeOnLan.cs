using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteDesktop.Protocol;

/// <summary>
/// Wake-on-LAN (STEP 12): 꺼져 있거나 절전 중인 PC를 같은 네트워크에서 깨웁니다.
/// 매직 패킷 = FF×6 + MAC×16 (102바이트)을 UDP 9번으로 브로드캐스트합니다.
///
/// 동작하려면 Host PC에서 미리 설정해야 합니다:
///   BIOS/UEFI의 Wake on LAN 켜기, 장치 관리자 → 네트워크 어댑터 → 전원 관리 "이 장치로 컴퓨터의 대기 모드를 해제할 수 있음",
///   고급 탭의 "Wake on Magic Packet" 사용. (유선 LAN 권장, Wi-Fi는 대부분 지원하지 않음)
/// 인터넷 너머의 PC는 공유기가 매직 패킷을 전달하지 않으므로 같은 네트워크의 다른 기기가 보내야 합니다.
/// </summary>
public static class WakeOnLan
{
    public const int Port = 9;

    public static bool IsValidMac(string? mac) => TryParseMac(mac, out _);

    public static bool TryParseMac(string? mac, out byte[] bytes)
    {
        bytes = new byte[6];
        if (mac is null)
        {
            return false;
        }

        string hex = new(mac.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12 || mac.Length > 17)
        {
            return false;
        }

        for (int i = 0; i < 6; i++)
        {
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return !bytes.All(b => b == 0) && !bytes.All(b => b == 0xFF);
    }

    public static byte[] CreateMagicPacket(byte[] mac)
    {
        if (mac.Length != 6)
        {
            throw new ArgumentException("MAC 주소는 6바이트입니다.", nameof(mac));
        }

        byte[] packet = new byte[102];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (int i = 0; i < 16; i++)
        {
            mac.CopyTo(packet, 6 + i * 6);
        }

        return packet;
    }

    /// <summary>모든 네트워크의 브로드캐스트 주소로 매직 패킷을 보냅니다. 보낸 MAC 수를 반환합니다.</summary>
    public static async Task<int> WakeAsync(IEnumerable<string> macs, IEnumerable<IPAddress>? extraTargets = null, int port = Port)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (IPAddress broadcast in LanDiscovery.SubnetBroadcastAddresses())
        {
            targets.Add(broadcast);
        }

        foreach (IPAddress extra in extraTargets ?? [])
        {
            targets.Add(extra);
        }

        int sent = 0;
        foreach (string mac in macs)
        {
            if (!TryParseMac(mac, out byte[] bytes))
            {
                continue;
            }

            byte[] packet = CreateMagicPacket(bytes);
            foreach (IPAddress target in targets)
            {
                try
                {
                    // 잃어버리기 쉬운 UDP라 세 번 보냅니다.
                    for (int i = 0; i < 3; i++)
                    {
                        await socket.SendAsync(packet, new IPEndPoint(target, port));
                    }
                }
                catch (SocketException)
                {
                }
            }

            sent++;
        }

        return sent;
    }

    /// <summary>이 PC의 유선/무선 네트워크 어댑터 MAC 주소 (가상 어댑터 제외)</summary>
    public static string[] LocalMacAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
                && !nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                && !nic.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                && !nic.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) // 유선 우선
            .Select(nic => nic.GetPhysicalAddress().GetAddressBytes())
            .Where(bytes => bytes.Length == 6 && !bytes.All(b => b == 0))
            .Select(bytes => string.Join("-", bytes.Select(b => b.ToString("X2"))))
            .Distinct()
            .Take(4)
            .ToArray();
}
