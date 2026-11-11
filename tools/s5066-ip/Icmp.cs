using System.Buffers.Binary;
using System.Net;

namespace M0LTE.S5066.Ip;

/// <summary>
/// The ICMP messages for path MTU discovery the IP client writes back into the tun device
/// (U-0031, U-0032): IPv4 Destination Unreachable, Fragmentation Needed and DF Set with the next
/// hop MTU (RFC 1191 4), and ICMPv6 Packet Too Big (RFC 4443 3.2, RFC 8201).
/// </summary>
internal static class Icmp
{
    /// <summary>Most an IPv4 ICMP error may be (RFC 1812 4.3.2.3).</summary>
    public const int MaxV4 = 576;

    /// <summary>Most an ICMPv6 error may be, the IPv6 minimum MTU (RFC 4443 2.4(c)).</summary>
    public const int MaxV6 = 1280;

    /// <summary>
    /// Builds the too-big answer to <paramref name="original"/>, or null when none may be sent: the
    /// original is an ICMP error or a later fragment, or its source is unspecified or multicast
    /// (RFC 1122 3.2.2, RFC 4443 2.4(e)), or its destination is multicast, which cannot be the
    /// answer's source. The answer comes from the original's destination (interp U-0037), so the
    /// host's reverse path check passes, since that address is routed into this tun.
    /// </summary>
    public static byte[]? TooBig(ReadOnlySpan<byte> original, IpDatagram d, int mtu)
    {
        if (d.IsIcmpError || d.IsLaterFragment || d.IsMulticast
            || d.Source.Equals(IPAddress.Any) || d.Source.Equals(IPAddress.IPv6None)
            || (d.Version == 4 ? (d.Source.GetAddressBytes()[0] & 0xF0) == 0xE0 || d.Source.Equals(IPAddress.Broadcast) : d.Source.IsIPv6Multicast))
        {
            return null;
        }

        return d.Version == 4 ? V4(original, d, mtu) : V6(original, d, mtu);
    }

    private static byte[] V4(ReadOnlySpan<byte> original, IpDatagram d, int mtu)
    {
        int quote = Math.Min(original.Length, MaxV4 - 28);
        byte[] p = new byte[28 + quote];
        p[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)p.Length);
        p[8] = 64;
        p[9] = IpDatagram.Icmp;
        d.Destination.TryWriteBytes(p.AsSpan(12, 4), out _);
        d.Source.TryWriteBytes(p.AsSpan(16, 4), out _);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(10), Checksum.Compute(p.AsSpan(0, 20)));
        Span<byte> icmp = p.AsSpan(20);
        icmp[0] = 3; // Destination Unreachable
        icmp[1] = 4; // Fragmentation Needed and DF Set
        BinaryPrimitives.WriteUInt16BigEndian(icmp[6..], (ushort)mtu);
        original[..quote].CopyTo(icmp[8..]);
        BinaryPrimitives.WriteUInt16BigEndian(icmp[2..], Checksum.Compute(icmp));
        return p;
    }

    private static byte[] V6(ReadOnlySpan<byte> original, IpDatagram d, int mtu)
    {
        int quote = Math.Min(original.Length, MaxV6 - 48);
        byte[] p = new byte[48 + quote];
        p[0] = 0x60;
        int upper = 8 + quote;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), (ushort)upper);
        p[6] = IpDatagram.Icmpv6;
        p[7] = 64;
        d.Destination.TryWriteBytes(p.AsSpan(8, 16), out _);
        d.Source.TryWriteBytes(p.AsSpan(24, 16), out _);
        Span<byte> icmp = p.AsSpan(40);
        icmp[0] = 2; // Packet Too Big
        BinaryPrimitives.WriteUInt32BigEndian(icmp[4..], (uint)mtu);
        original[..quote].CopyTo(icmp[8..]);
        BinaryPrimitives.WriteUInt16BigEndian(icmp[2..], Checksum.Compute(icmp, PseudoHeaderSum(p, upper)));
        return p;
    }

    /// <summary>The sum of the IPv6 pseudo-header for an ICMPv6 message (RFC 8200 8.1).</summary>
    internal static uint PseudoHeaderSum(ReadOnlySpan<byte> v6Packet, int upperLength)
    {
        uint sum = Checksum.Sum(v6Packet.Slice(8, 32));
        Span<byte> tail = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(tail, (uint)upperLength);
        tail[7] = IpDatagram.Icmpv6;
        return Checksum.Sum(tail, sum);
    }
}

/// <summary>
/// A token bucket for ICMP errors, which a node must rate-limit (RFC 4443 2.4(f); RFC 1812
/// 4.3.2.8): at most <see cref="Burst"/> at once and <see cref="PerSecond"/> a second after that.
/// </summary>
internal sealed class IcmpRateLimit(TimeProvider clock)
{
    public const int Burst = 10;
    public const int PerSecond = 10;

    private readonly object _gate = new();
    private double _tokens = Burst;
    private long _last = clock.GetTimestamp();

    /// <summary>Takes a token; false when none is left.</summary>
    public bool TryTake()
    {
        lock (_gate)
        {
            long now = clock.GetTimestamp();
            _tokens = Math.Min(Burst, _tokens + (clock.GetElapsedTime(_last, now).TotalSeconds * PerSecond));
            _last = now;
            if (_tokens < 1)
            {
                return false;
            }

            _tokens -= 1;
            return true;
        }
    }
}
