using System.Buffers.Binary;
using System.Net;

namespace M0LTE.S5066.Ip;

/// <summary>
/// What the IP client needs to know about one IPv4 or IPv6 datagram: version, addresses, upper
/// protocol and ports, and the IPv4 Don't Fragment flag. The version comes from the first four
/// bits of the first byte [Ed4 U.5.1, p.U-3] (U-0011).
/// </summary>
internal sealed record IpDatagram(
    int Version,
    IPAddress Source,
    IPAddress Destination,
    int Protocol,
    int? SourcePort,
    int? DestinationPort,
    bool DontFragment,
    bool IsLaterFragment,
    int? IcmpType)
{
    /// <summary>IP protocol numbers used by the policy (IANA).</summary>
    public const int Icmp = 1, Tcp = 6, Udp = 17, Icmpv6 = 58;

    /// <summary>True for an IPv4 multicast or limited broadcast destination, or an IPv6 multicast one.</summary>
    public bool IsMulticast => Version == 4
        ? (Destination.GetAddressBytes()[0] & 0xF0) == 0xE0 || Destination.Equals(IPAddress.Broadcast)
        : Destination.IsIPv6Multicast;

    /// <summary>True for an ICMP or ICMPv6 error message, which never draws an ICMP error in answer (RFC 1122 3.2.2, RFC 4443 2.4(e)).</summary>
    public bool IsIcmpError => IcmpType is int t && (Version == 4 ? t is 3 or 4 or 5 or 11 or 12 : t < 128);

    /// <summary>
    /// Parses a whole datagram, or returns null when the bytes are not one: wrong version, short
    /// header, a length field that does not match the bytes (one datagram, nothing more, U-0029), or
    /// for IPv4 a bad header checksum.
    /// </summary>
    public static IpDatagram? TryParse(ReadOnlySpan<byte> p)
    {
        if (p.IsEmpty)
        {
            return null;
        }

        return (p[0] >> 4) switch
        {
            4 => ParseV4(p),
            6 => ParseV6(p),
            _ => null,
        };
    }

    private static IpDatagram? ParseV4(ReadOnlySpan<byte> p)
    {
        if (p.Length < 20)
        {
            return null;
        }

        int ihl = (p[0] & 0x0F) * 4;
        int total = BinaryPrimitives.ReadUInt16BigEndian(p[2..]);
        if (ihl < 20 || total != p.Length || ihl > total || Checksum.Compute(p[..ihl]) != 0)
        {
            return null;
        }

        ushort flagsFrag = BinaryPrimitives.ReadUInt16BigEndian(p[6..]);
        bool df = (flagsFrag & 0x4000) != 0;
        bool later = (flagsFrag & 0x1FFF) != 0;
        int proto = p[9];
        var src = new IPAddress(p.Slice(12, 4));
        var dst = new IPAddress(p.Slice(16, 4));
        (int? sp, int? dp, int? icmp) = later ? (null, null, null) : Upper(proto, p[ihl..]);
        return new IpDatagram(4, src, dst, proto, sp, dp, df, later, proto == Icmp ? icmp : null);
    }

    private static IpDatagram? ParseV6(ReadOnlySpan<byte> p)
    {
        if (p.Length < 40 || BinaryPrimitives.ReadUInt16BigEndian(p[4..]) + 40 != p.Length)
        {
            return null;
        }

        var src = new IPAddress(p.Slice(8, 16));
        var dst = new IPAddress(p.Slice(24, 16));
        int next = p[6];
        int at = 40;
        bool later = false;

        // Walk the extension headers to the upper protocol (RFC 8200 4).
        while (true)
        {
            if (next is 0 or 43 or 60)
            {
                if (at + 2 > p.Length)
                {
                    return null;
                }

                int len = (p[at + 1] + 1) * 8;
                next = p[at];
                at += len;
            }
            else if (next == 44)
            {
                if (at + 8 > p.Length)
                {
                    return null;
                }

                later = (BinaryPrimitives.ReadUInt16BigEndian(p[(at + 2)..]) & 0xFFF8) != 0;
                next = p[at];
                at += 8;
            }
            else if (next == 51)
            {
                if (at + 2 > p.Length)
                {
                    return null;
                }

                int len = (p[at + 1] + 2) * 4;
                next = p[at];
                at += len;
            }
            else
            {
                break;
            }

            if (at > p.Length)
            {
                return null;
            }
        }

        (int? sp, int? dp, int? icmp) = later ? (null, null, null) : Upper(next, p[at..]);
        return new IpDatagram(6, src, dst, next, sp, dp, false, later, next == Icmpv6 ? icmp : null);
    }

    private static (int? Src, int? Dst, int? Icmp) Upper(int proto, ReadOnlySpan<byte> payload) => proto switch
    {
        Tcp or Udp when payload.Length >= 4 => (BinaryPrimitives.ReadUInt16BigEndian(payload), BinaryPrimitives.ReadUInt16BigEndian(payload[2..]), null),
        Icmp or Icmpv6 when payload.Length >= 1 => (null, null, payload[0]),
        _ => (null, null, null),
    };
}

/// <summary>The Internet checksum (RFC 1071).</summary>
internal static class Checksum
{
    /// <summary>The ones' complement of the ones' complement sum of 16-bit words; 0 over a block that holds a correct checksum.</summary>
    public static ushort Compute(ReadOnlySpan<byte> data, uint initial = 0) => (ushort)~Fold(Sum(data, initial));

    /// <summary>The running 32-bit sum of 16-bit big-endian words, an odd byte padded with zero.</summary>
    public static uint Sum(ReadOnlySpan<byte> data, uint sum = 0)
    {
        int i = 0;
        for (; i + 1 < data.Length; i += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        if (i < data.Length)
        {
            sum += (uint)(data[i] << 8);
        }

        return sum;
    }

    private static uint Fold(uint sum)
    {
        while (sum >> 16 != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return sum;
    }
}
