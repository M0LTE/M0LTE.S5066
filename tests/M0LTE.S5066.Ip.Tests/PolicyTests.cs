using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Ip;

namespace M0LTE.S5066.Ip.Tests;

/// <summary>Parsing, address mapping and the per-datagram choices of U.8, without a node or a tun.</summary>
public class PolicyTests
{
    private static readonly SisAddress NodeB = SisAddress.FromParts(0, 0, 1, 2);
    private static readonly SisAddress NodeC = SisAddress.FromParts(0, 0, 1, 3);

    private static Policy Make(string json) => new(IpConfig.Parse(json));

    private const string TwoRoutes = """
        {
          "routes": [
            { "prefix": "10.66.0.0/16", "node": "0.0.1.2" },
            { "prefix": "10.66.3.0/24", "node": "0.0.1.3" },
            { "prefix": "fd66::/48", "node": "0.0.1.2" }
          ]
        }
        """;

    [Fact]
    public void The_version_comes_from_the_first_four_bits_and_both_versions_parse()
    {
        // U-0011.
        Packets.Parse(Packets.Udp4("10.1.0.1", "10.66.0.9", 1000, 2000)).Version.Should().Be(4);
        IpDatagram v6 = Packets.Parse(Packets.V6(IpDatagram.Udp, "fd01::1", "fd66::9", Packets.Udp(1000, 2000)));
        v6.Version.Should().Be(6);
        v6.DestinationPort.Should().Be(2000);
    }

    [Fact]
    public void Bytes_that_are_not_one_whole_datagram_do_not_parse()
    {
        // U-0029, interp U-0041.
        byte[] good = Packets.Udp4("10.1.0.1", "10.66.0.9", 1000, 2000);
        IpDatagram.TryParse(good).Should().NotBeNull();
        IpDatagram.TryParse(good.AsSpan(0, good.Length - 1)).Should().BeNull("the length field says more");
        IpDatagram.TryParse([.. good, 0]).Should().BeNull("a byte after the datagram");
        byte[] badSum = (byte[])good.Clone();
        badSum[8] ^= 1;
        IpDatagram.TryParse(badSum).Should().BeNull("the IPv4 header checksum is wrong");
        byte[] v5 = (byte[])good.Clone();
        v5[0] = 0x55;
        IpDatagram.TryParse(v5).Should().BeNull("version 5");
        IpDatagram.TryParse([]).Should().BeNull();
        IpDatagram.TryParse(Packets.V6(IpDatagram.Udp, "fd01::1", "fd66::9", Packets.Udp(1, 2))[..39]).Should().BeNull();
    }

    [Fact]
    public void Unicast_goes_arq_with_node_delivery_confirmation_in_order_at_the_default_priority()
    {
        // U-0004, U-0013, U-0018, interp U-0038, interp U-0039.
        (SendDecision? s, DropReason? drop) = Make(TwoRoutes).Decide(Packets.Parse(Packets.Udp4("10.1.0.1", "10.66.0.9", 1000, 2000)));
        drop.Should().BeNull();
        s!.Destination.Should().Be(NodeB);
        s.Mode.Should().Be(new ServiceType(1, Confirm: 1, Order: 0));
        s.Priority.Should().Be(6, "the IP client default of Annex F Table F-2");
        Policy.BindServiceType.Should().Be(s.Mode, "the bind's default is the most likely traffic (U-0015)");
    }

    [Fact]
    public void The_longest_matching_prefix_picks_the_node_then_the_default()
    {
        // U-0024, interp U-0043.
        Policy p = Make(TwoRoutes);
        p.Route(System.Net.IPAddress.Parse("10.66.3.7")).Should().Be(NodeC);
        p.Route(System.Net.IPAddress.Parse("10.66.4.7")).Should().Be(NodeB);
        p.Route(System.Net.IPAddress.Parse("fd66::5")).Should().Be(NodeB);
        p.Route(System.Net.IPAddress.Parse("10.67.0.1")).Should().BeNull();
        p.Decide(Packets.Parse(Packets.Udp4("10.1.0.1", "10.67.0.1", 1, 2))).Drop.Should().Be(DropReason.NoRoute);

        Policy withDefault = Make("""{ "routes": [ { "prefix": "10.66.0.0/16", "node": "0.0.1.2" } ], "default_node": "0.0.1.3" }""");
        withDefault.Route(System.Net.IPAddress.Parse("192.0.2.1")).Should().Be(NodeC);
        withDefault.Route(System.Net.IPAddress.Parse("2001:db8::1")).Should().Be(NodeC);
    }

    [Fact]
    public void Icmp_goes_at_a_higher_priority()
    {
        // U-0035 item 4, interp U-0038.
        Policy p = Make(TwoRoutes);
        p.Decide(Packets.Parse(Packets.V4(IpDatagram.Icmp, "10.1.0.1", "10.66.0.9", Packets.Echo()))).Send!.Priority.Should().Be(8);
        p.Decide(Packets.Parse(Packets.V6(IpDatagram.Icmpv6, "fd01::1", "fd66::9", Packets.Echo(128)))).Send!.Priority.Should().Be(8);
    }

    [Fact]
    public void Rules_choose_non_arq_and_priority_per_protocol_and_port()
    {
        // U-0013, U-0014, U-0016, U-0020, U-0021, U-0034.
        Policy p = Make("""
            {
              "routes": [ { "prefix": "10.66.0.0/16", "node": "0.0.1.2" } ],
              "rules": [
                { "protocol": "udp", "port": 53, "mode": "non_arq", "priority": 2 },
                { "protocol": "tcp", "priority": 1 }
              ]
            }
            """);
        SendDecision dns = p.Decide(Packets.Parse(Packets.Udp4("10.1.0.1", "10.66.0.9", 40000, 53))).Send!;
        dns.Mode.Should().Be(new ServiceType(2, Confirm: 0, Order: 1));
        dns.Priority.Should().Be(2);
        p.Decide(Packets.Parse(Packets.Udp4("10.66.0.9", "10.66.0.1", 53, 40000))).Send!.Mode.TxMode.Should().Be(2, "a source port matches too");
        SendDecision tcp = p.Decide(Packets.Parse(Packets.V4(IpDatagram.Tcp, "10.1.0.1", "10.66.0.9", Packets.Udp(1, 80)))).Send!;
        tcp.Mode.TxMode.Should().Be(1, "a rule without a mode keeps the default");
        tcp.Priority.Should().Be(1);
        p.Decide(Packets.Parse(Packets.Udp4("10.1.0.1", "10.66.0.9", 40000, 54))).Send!.Mode.TxMode.Should().Be(1);

        Make("""{ "default_node": "0.0.1.2", "mode": "non_arq" }""")
            .Decide(Packets.Parse(Packets.Udp4("10.1.0.1", "10.66.0.9", 1, 2))).Send!.Mode.TxMode.Should().Be(2, "the whole client may be non-ARQ (U-0013)");
    }

    [Fact]
    public void Multicast_is_dropped_unless_a_group_is_configured_and_then_goes_non_arq_with_repeats()
    {
        // U-0005, U-0019, U-0021, U-0022, interp U-0042.
        IpDatagram mc4 = Packets.Parse(Packets.Udp4("10.1.0.1", "224.0.0.251", 5353, 5353));
        IpDatagram mc6 = Packets.Parse(Packets.V6(IpDatagram.Udp, "fd01::1", "ff02::fb", Packets.Udp(5353, 5353)));
        IpDatagram bcast = Packets.Parse(Packets.Udp4("10.1.0.1", "255.255.255.255", 67, 68));
        Policy none = Make(TwoRoutes);
        none.Decide(mc4).Drop.Should().Be(DropReason.Multicast);
        none.Decide(mc6).Drop.Should().Be(DropReason.Multicast);
        none.Decide(bcast).Drop.Should().Be(DropReason.Multicast);

        Policy with = Make("""{ "multicast": { "group": "16.0.0.9", "repeats": 2 }, "rules": [ { "protocol": "udp", "mode": "arq" } ] }""");
        SendDecision s = with.Decide(mc4).Send!;
        s.Destination.Should().Be(SisAddress.FromParts(0, 0, 0, 9, group: true));
        s.Mode.Should().Be(new ServiceType(2, Confirm: 0, Order: 1, MinRetx: 2), "multicast is always non-ARQ, whatever a rule says");
        with.Decide(mc6).Send!.Destination.Group.Should().BeTrue();
    }

    [Fact]
    public void Everything_is_allowed_unless_an_allow_list_is_given()
    {
        // U-0035 item 3, interp U-0044.
        IpDatagram udp = Packets.Parse(Packets.Udp4("10.1.0.1", "10.66.0.9", 1000, 2000));
        IpDatagram ping = Packets.Parse(Packets.V4(IpDatagram.Icmp, "10.1.0.1", "10.66.0.9", Packets.Echo()));
        Make(TwoRoutes).Decide(udp).Send.Should().NotBeNull();
        Policy p = Make("""
            {
              "default_node": "0.0.1.2",
              "allow": [ { "protocol": "icmp" }, { "protocol": "udp", "port": 2000, "prefix": "10.66.0.0/24" } ]
            }
            """);
        p.Decide(ping).Send.Should().NotBeNull();
        p.Decide(udp).Send.Should().NotBeNull();
        p.Decide(Packets.Parse(Packets.Udp4("10.1.0.1", "10.66.1.9", 1000, 2000))).Drop.Should().Be(DropReason.NotAllowed);
        p.Decide(Packets.Parse(Packets.Udp4("10.1.0.1", "10.66.0.9", 1000, 2001))).Drop.Should().Be(DropReason.NotAllowed);
    }

    [Fact]
    public void A_bad_configuration_is_refused_naming_the_key()
    {
        FluentActions.Invoking(() => IpConfig.Parse("""{ "routes": [ { "prefix": "10.66.0.0/33", "node": "0.0.1.2" } ] }"""))
            .Should().Throw<IpConfigException>().WithMessage("routes.prefix*");
        FluentActions.Invoking(() => IpConfig.Parse("""{ "routes": [ { "prefix": "10.66.0.0/16", "node": "16.0.0.1" } ] }"""))
            .Should().Throw<IpConfigException>().WithMessage("routes.node*group*");
        FluentActions.Invoking(() => IpConfig.Parse("""{ "priority": 16 }""")).Should().Throw<IpConfigException>().WithMessage("priority*");
        FluentActions.Invoking(() => IpConfig.Parse("""{ "rules": [ { "protocol": "sctp", "mode": "arq" } ] }""")).Should().Throw<IpConfigException>().WithMessage("rules.protocol*");
        FluentActions.Invoking(() => IpConfig.Parse("""{ "mtu": 1500 }""")).Should().Throw<IpConfigException>().WithMessage("unknown key mtu");
        IpConfig c = IpConfig.Parse("""
            // comments are allowed
            { "sis": "192.0.2.5:5070", "device": "hf0", "queue_limit": 8 }
            """);
        (c.SisHost, c.SisPort, c.Device, c.QueueLimit).Should().Be(("192.0.2.5", 5070, "hf0", 8));
    }

    [Fact]
    public void The_packaged_example_configuration_parses()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "tools", "s5066-ip", "example.json")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new FileNotFoundException("tools/s5066-ip/example.json");
        }

        IpConfig c = IpConfig.Load(Path.Combine(dir, "tools", "s5066-ip", "example.json"));
        c.Routes.Should().HaveCount(2);
        c.Device.Should().Be("s5066");
        c.Allow.Should().BeNull();
        c.MulticastGroup.Should().BeNull();
    }
}
