using System.Net;
using System.Net.Sockets;

namespace Switchknife.SafeFetch;

/// <summary>Which addresses a fetch may connect to: global unicast only. Embedded IPv4 (mapped, compatible, NAT64, 6to4) is judged as IPv4.</summary>
public static class IpGuard
{
    /// <summary>True for a global unicast address.</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        var b = ip.GetAddressBytes();
        return ip.AddressFamily switch
        {
            AddressFamily.InterNetwork => PublicV4(b),
            AddressFamily.InterNetworkV6 => PublicV6(b),
            _ => false,
        };
    }

    private static bool PublicV4(byte[] b) => !(
        b[0] == 0                                           // this network
        || b[0] == 10                                       // private
        || b[0] == 127                                      // loopback
        || (b[0] == 100 && (b[1] & 0xC0) == 64)             // carrier-grade NAT, also used by VPN overlays
        || (b[0] == 169 && b[1] == 254)                     // link-local, cloud metadata
        || (b[0] == 172 && (b[1] & 0xF0) == 16)             // private
        || (b[0] == 192 && b[1] == 168)                     // private
        || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)     // IETF protocol, documentation
        || (b[0] == 198 && (b[1] & 0xFE) == 18)             // benchmarking
        || (b[0] == 198 && b[1] == 51 && b[2] == 100)       // documentation
        || (b[0] == 203 && b[1] == 0 && b[2] == 113)        // documentation
        || b[0] >= 224);                                    // multicast, reserved, broadcast

    private static bool PublicV6(byte[] b)
    {
        if (b[..12].All(x => x == 0))                       // :: , ::1 and IPv4-compatible ::a.b.c.d
        {
            return PublicV4(b[12..]);
        }

        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4..12].All(x => x == 0))   // NAT64 64:ff9b::/96
        {
            return PublicV4(b[12..]);
        }

        if (b[0] == 0x20 && b[1] == 0x02)                   // 6to4 2002::/16 embeds the IPv4 address
        {
            return PublicV4(b[2..6]);
        }

        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)   // Teredo
        {
            return false;
        }

        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)   // documentation
        {
            return false;
        }

        return (b[0] & 0xE0) == 0x20;                       // global unicast 2000::/3 only; the rest (fe80, fc00, ff00, ...) is refused
    }
}
