using System.Buffers.Binary;
using System.Net;
using M0LTE.S5066.Ip;

namespace M0LTE.S5066.Ip.Tests;

/// <summary>The ICMP too-big answers for path MTU discovery (U-0031, U-0032, interp U-0037).</summary>
public class IcmpTests
{
    [Fact]
    public void An_ipv4_datagram_with_df_draws_fragmentation_needed_with_the_mtu()
    {
        // U-0031, U-0032, interp U-0037; RFC 1191 4, RFC 1812 4.3.2.3.
        byte[] big = Packets.Udp4("10.1.0.1", "10.66.0.9", 1000, 2000, bodyLength: 3000, df: true);
        byte[] a = Icmp.TooBig(big, Packets.Parse(big), 2048)!;

        IpDatagram d = Packets.Parse(a);
        d.Protocol.Should().Be(IpDatagram.Icmp);
        d.Source.Should().Be(IPAddress.Parse("10.66.0.9"), "from the original destination");
        d.Destination.Should().Be(IPAddress.Parse("10.1.0.1"));
        a.Length.Should().Be(Icmp.MaxV4);
        a[20].Should().Be(3);
        a[21].Should().Be(4);
        BinaryPrimitives.ReadUInt16BigEndian(a.AsSpan(26)).Should().Be(2048);
        Checksum.Compute(a.AsSpan(20)).Should().Be(0, "the ICMP checksum is right");
        a.AsSpan(28, 28).ToArray().Should().Equal(big.AsSpan(0, 28).ToArray(), "it quotes the original header and more");
    }

    [Fact]
    public void An_ipv6_datagram_draws_packet_too_big_with_the_mtu()
    {
        // U-0031, U-0032, interp U-0037; RFC 4443 3.2, RFC 8201.
        byte[] big = Packets.V6(IpDatagram.Udp, "fd01::1", "fd66::9", Packets.Udp(1000, 2000, 3000));
        byte[] a = Icmp.TooBig(big, Packets.Parse(big), 2048)!;

        IpDatagram d = Packets.Parse(a);
        d.Protocol.Should().Be(IpDatagram.Icmpv6);
        d.IcmpType.Should().Be(2);
        d.Source.Should().Be(IPAddress.Parse("fd66::9"));
        d.Destination.Should().Be(IPAddress.Parse("fd01::1"));
        a.Length.Should().Be(Icmp.MaxV6);
        BinaryPrimitives.ReadUInt32BigEndian(a.AsSpan(44)).Should().Be(2048u);
        Checksum.Compute(a.AsSpan(40), Icmp.PseudoHeaderSum(a, a.Length - 40)).Should().Be(0);
    }

    [Fact]
    public void No_answer_to_an_icmp_error_a_later_fragment_or_a_multicast_source_or_destination()
    {
        // RFC 1122 3.2.2, RFC 4443 2.4(e).
        byte[] err = Packets.V4(IpDatagram.Icmp, "10.1.0.1", "10.66.0.9", [3, 1, 0, 0, 0, 0, 0, 0, .. new byte[3000]], df: true);
        Icmp.TooBig(err, Packets.Parse(err), 2048).Should().BeNull();
        byte[] frag = Packets.V4(IpDatagram.Udp, "10.1.0.1", "10.66.0.9", new byte[3000], df: true, fragOffset: 100);
        Icmp.TooBig(frag, Packets.Parse(frag), 2048).Should().BeNull();
        byte[] mc = Packets.V6(IpDatagram.Udp, "fd01::1", "ff02::fb", Packets.Udp(1, 2, 3000));
        Icmp.TooBig(mc, Packets.Parse(mc), 2048).Should().BeNull();
        byte[] echo = Packets.V6(IpDatagram.Icmpv6, "fd01::1", "fd66::9", Packets.Echo(128, 3000));
        Icmp.TooBig(echo, Packets.Parse(echo), 2048).Should().NotBeNull("an echo request is not an error");
    }

    [Fact]
    public void Icmp_errors_are_rate_limited()
    {
        // RFC 4443 2.4(f), RFC 1812 4.3.2.8.
        var clock = new ManualClock();
        var limit = new IcmpRateLimit(clock);
        Enumerable.Range(0, IcmpRateLimit.Burst).Select(_ => limit.TryTake()).ToArray().Should().AllBeEquivalentTo(true);
        limit.TryTake().Should().BeFalse();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        limit.TryTake().Should().BeTrue();
        limit.TryTake().Should().BeFalse();
    }
}
