using System.Buffers.Binary;
using System.Net;
using M0LTE.S5066.Ip;

namespace M0LTE.S5066.Ip.Tests;

/// <summary>Builds IPv4 and IPv6 datagrams for the tests.</summary>
internal static class Packets
{
    public static byte[] V4(int protocol, string src, string dst, byte[] payload, bool df = false, ushort fragOffset = 0)
    {
        byte[] p = new byte[20 + payload.Length];
        p[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)p.Length);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), 0x1234);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(6), (ushort)((df ? 0x4000 : 0) | fragOffset));
        p[8] = 64;
        p[9] = (byte)protocol;
        IPAddress.Parse(src).TryWriteBytes(p.AsSpan(12, 4), out _);
        IPAddress.Parse(dst).TryWriteBytes(p.AsSpan(16, 4), out _);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(10), Checksum.Compute(p.AsSpan(0, 20)));
        payload.CopyTo(p, 20);
        return p;
    }

    public static byte[] V6(int next, string src, string dst, byte[] payload)
    {
        byte[] p = new byte[40 + payload.Length];
        p[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), (ushort)payload.Length);
        p[6] = (byte)next;
        p[7] = 64;
        IPAddress.Parse(src).TryWriteBytes(p.AsSpan(8, 16), out _);
        IPAddress.Parse(dst).TryWriteBytes(p.AsSpan(24, 16), out _);
        payload.CopyTo(p, 40);
        return p;
    }

    /// <summary>A UDP header and body (checksum left zero, which IPv4 allows; the client does not check it).</summary>
    public static byte[] Udp(int sport, int dport, int bodyLength = 8)
    {
        byte[] u = new byte[8 + bodyLength];
        BinaryPrimitives.WriteUInt16BigEndian(u, (ushort)sport);
        BinaryPrimitives.WriteUInt16BigEndian(u.AsSpan(2), (ushort)dport);
        BinaryPrimitives.WriteUInt16BigEndian(u.AsSpan(4), (ushort)u.Length);
        for (int i = 8; i < u.Length; i++)
        {
            u[i] = (byte)i;
        }

        return u;
    }

    /// <summary>An ICMP (type 8) or ICMPv6 (type 128) echo request with a correct ICMPv4 checksum.</summary>
    public static byte[] Echo(byte type = 8, int bodyLength = 16)
    {
        byte[] e = new byte[8 + bodyLength];
        e[0] = type;
        BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(4), 0x0042);
        BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(6), 1);
        for (int i = 8; i < e.Length; i++)
        {
            e[i] = (byte)(0x30 + i);
        }

        BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(2), Checksum.Compute(e));
        return e;
    }

    public static byte[] Udp4(string src, string dst, int sport, int dport, int bodyLength = 8, bool df = false) =>
        V4(IpDatagram.Udp, src, dst, Udp(sport, dport, bodyLength), df);

    public static IpDatagram Parse(byte[] p) => IpDatagram.TryParse(p) ?? throw new InvalidOperationException("not IP");
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualClock : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}
