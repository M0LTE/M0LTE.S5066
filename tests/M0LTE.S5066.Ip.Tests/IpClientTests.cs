using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Host;
using M0LTE.S5066.Host.Modem;
using M0LTE.S5066.Host.Tests;
using M0LTE.S5066.Ip;

namespace M0LTE.S5066.Ip.Tests;

/// <summary>Two IP clients with in-memory devices over two loopback hosts: the whole path but the tun.</summary>
public class IpClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static IpConfig ConfigFor(string peerNode, string peerV4, string peerV6, string extra = "") => IpConfig.Parse($$"""
        {
          "routes": [ { "prefix": "{{peerV4}}", "node": "{{peerNode}}" }, { "prefix": "{{peerV6}}", "node": "{{peerNode}}" } ]
          {{extra}}
        }
        """);

    private sealed class Side : IAsyncDisposable
    {
        public required NodeHost Host { get; init; }
        public required MemoryPacketDevice Device { get; init; }
        public required IpClient Client { get; init; }
        public required Task Run { get; init; }
        public required CancellationTokenSource Stop { get; init; }
        public List<string> Log { get; } = [];

        public async ValueTask DisposeAsync()
        {
            await Stop.CancelAsync();
            try
            {
                await Run.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
            }

            await Client.DisposeAsync();
            Device.Dispose();
            await Host.DisposeAsync();
            Stop.Dispose();
        }
    }

    private static Side Start(LoopbackMedium medium, string address, string seed, IpConfig config, Task? gate = null)
    {
        NodeHost host = TestNodes.StartLoopbackHost(medium, address, seed);
        var device = new MemoryPacketDevice();
        var log = new List<string>();
        var client = new IpClient(config, device, l => { lock (log) { log.Add(l); } });
        var stop = new CancellationTokenSource();
        Task run = client.RunAsync(async ct =>
        {
            if (gate is not null)
            {
                await gate.WaitAsync(ct);
            }

            return await SisClient.ConnectAsync(TestNodes.Endpoint(host), ct);
        }, stop.Token);
        return new Side { Host = host, Device = device, Client = client, Run = run, Stop = stop };
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        using var cts = new CancellationTokenSource(Timeout);
        while (!condition())
        {
            await Task.Delay(20, cts.Token).ContinueWith(t => t.IsCanceled ? throw new TimeoutException(what) : t, TaskScheduler.Default);
        }
    }

    [Fact]
    public async Task A_ping_crosses_arq_as_one_u_pdu_byte_for_byte_and_the_reply_comes_back()
    {
        // U-0004, U-0006, U-0008, U-0009, U-0010, U-0012, U-0018, U-0029; the device MTU follows the bind.
        var medium = new LoopbackMedium();
        await using Side a = Start(medium, "0.0.1.1", "0000000000000001", ConfigFor("0.0.1.2", "10.66.2.0/24", "fd66:2::/64"));
        await using Side b = Start(medium, "0.0.1.2", "0000000000000002", ConfigFor("0.0.1.1", "10.66.1.0/24", "fd66:1::/64"));
        await Until(() => a.Client.Mtu > 0 && b.Client.Mtu > 0, "both bound");
        a.Device.Mtu.Should().Be(a.Client.Mtu);
        a.Client.Mtu.Should().Be(2048);

        byte[] request = Packets.V4(IpDatagram.Icmp, "10.66.1.1", "10.66.2.1", Packets.Echo(8, 56), df: true);
        a.Device.Inject(request);
        b.Device.ToHost.TryTake(out byte[]? got, Timeout).Should().BeTrue();
        got.Should().Equal(request);

        byte[] reply = Packets.V4(IpDatagram.Icmp, "10.66.2.1", "10.66.1.1", Packets.Echo(0, 56), df: true);
        b.Device.Inject(reply);
        a.Device.ToHost.TryTake(out byte[]? back, Timeout).Should().BeTrue();
        back.Should().Equal(reply);

        await Until(() => a.Client.Counters.Confirmed == 1 && b.Client.Counters.Confirmed == 1, "node delivery confirmations");
        a.Client.Counters.SentArq.Should().Be(1);
        b.Client.Counters.Delivered.Should().Be(1);
    }

    [Fact]
    public async Task Ipv6_goes_non_arq_when_a_rule_says_so()
    {
        // U-0004, U-0016, U-0020, U-0021.
        var medium = new LoopbackMedium();
        await using Side a = Start(medium, "0.0.1.1", "0000000000000001",
            ConfigFor("0.0.1.2", "10.66.2.0/24", "fd66:2::/64", """, "rules": [ { "protocol": "udp", "port": 5001, "mode": "non_arq" } ]"""));
        await using Side b = Start(medium, "0.0.1.2", "0000000000000002", ConfigFor("0.0.1.1", "10.66.1.0/24", "fd66:1::/64"));
        await Until(() => a.Client.Mtu > 0 && b.Client.Mtu > 0, "both bound");

        byte[] d = Packets.V6(IpDatagram.Udp, "fd66:1::1", "fd66:2::1", Packets.Udp(40000, 5001, 1200));
        a.Device.Inject(d);
        b.Device.ToHost.TryTake(out byte[]? got, Timeout).Should().BeTrue();
        got.Should().Equal(d);
        a.Client.Counters.SentNonArq.Should().Be(1);
        a.Client.Counters.SentArq.Should().Be(0);
    }

    [Fact]
    public async Task Over_the_mtu_ipv4_with_df_and_ipv6_draw_icmp_and_ipv4_without_df_is_dropped()
    {
        // U-0029, U-0030, U-0031, U-0032, interp U-0037.
        var medium = new LoopbackMedium();
        await using Side a = Start(medium, "0.0.1.1", "0000000000000001", ConfigFor("0.0.1.2", "10.66.2.0/24", "fd66:2::/64"));
        await Until(() => a.Client.Mtu > 0, "bound");

        a.Device.Inject(Packets.Udp4("10.66.1.1", "10.66.2.1", 1000, 2000, bodyLength: 2100, df: true));
        a.Device.ToHost.TryTake(out byte[]? icmp4, Timeout).Should().BeTrue();
        Packets.Parse(icmp4!).IcmpType.Should().Be(3);

        a.Device.Inject(Packets.V6(IpDatagram.Udp, "fd66:1::1", "fd66:2::1", Packets.Udp(1000, 2000, 2100)));
        a.Device.ToHost.TryTake(out byte[]? icmp6, Timeout).Should().BeTrue();
        Packets.Parse(icmp6!).IcmpType.Should().Be(2);

        a.Device.Inject(Packets.Udp4("10.66.1.1", "10.66.2.1", 1000, 2000, bodyLength: 2100, df: false));
        await Until(() => a.Client.Counters.Dropped(DropReason.TooBig) == 1, "dropped");
        a.Client.Counters.IcmpTooBig.Should().Be(2);
        a.Client.Counters.SentArq.Should().Be(0, "nothing over the MTU reaches the node, and nothing is split");
    }

    [Fact]
    public async Task While_not_bound_datagrams_wait_in_a_bounded_queue_that_drops_the_newest()
    {
        // U-0028, interp U-0040.
        var medium = new LoopbackMedium();
        var open = new TaskCompletionSource();
        await using Side a = Start(medium, "0.0.1.1", "0000000000000001",
            ConfigFor("0.0.1.2", "10.66.2.0/24", "fd66:2::/64", """, "queue_limit": 3"""), open.Task);
        await using Side b = Start(medium, "0.0.1.2", "0000000000000002", ConfigFor("0.0.1.1", "10.66.1.0/24", "fd66:1::/64"));
        await Until(() => b.Client.Mtu > 0, "b bound");

        for (int i = 0; i < 5; i++)
        {
            a.Device.Inject(Packets.Udp4("10.66.1.1", "10.66.2.1", 1000, 2000 + i));
        }

        await Until(() => a.Client.Counters.FromHost == 5, "read");
        a.Client.Counters.Dropped(DropReason.QueueFull).Should().Be(2);
        a.Client.Queued.Should().Be(3);

        open.SetResult();
        var ports = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            b.Device.ToHost.TryTake(out byte[]? got, Timeout).Should().BeTrue();
            ports.Add(Packets.Parse(got!).DestinationPort!.Value);
        }

        ports.Should().Equal(2000, 2001, 2002);
    }

    [Fact]
    public async Task When_the_node_turns_flow_off_the_queue_fills_and_drops()
    {
        // U-0028, interp U-0040, S-0020. No peer answers, so ARQ data piles up in the node until it says flow off.
        var medium = new LoopbackMedium();
        await using Side a = Start(medium, "0.0.1.1", "0000000000000001",
            ConfigFor("0.0.1.2", "10.66.2.0/24", "fd66:2::/64", """, "queue_limit": 4"""));
        await Until(() => a.Client.Mtu > 0, "bound");

        for (int i = 0; i < 40; i++)
        {
            a.Device.Inject(Packets.Udp4("10.66.1.1", "10.66.2.1", 1000, 2000, bodyLength: 1900));
        }

        await Until(() => a.Client.Counters.FromHost == 40, "read");
        await Until(() => a.Client.Counters.Dropped(DropReason.QueueFull) > 0, "drops");
        a.Client.Queued.Should().BeLessThanOrEqualTo(4);
        a.Client.Counters.SentArq.Should().BeLessThan(40);
    }

    [Fact]
    public async Task A_received_u_pdu_that_is_not_ip_is_dropped_and_ip_is_delivered()
    {
        // U-0006, interp U-0041.
        var device = new MemoryPacketDevice();
        await using var c = new IpClient(IpConfig.Parse("{}"), device, _ => { });
        SisAddress from = SisAddress.FromParts(0, 0, 1, 2);
        c.Deliver(new SisUnidataIndication(4, 9, SisAddress.FromParts(0, 0, 1, 1), 1, 9, from, new byte[] { 1, 2, 3 }));
        c.Counters.Dropped(DropReason.ReceivedNotIp).Should().Be(1);
        device.ToHost.Count.Should().Be(0);
        byte[] ok = Packets.Udp4("10.66.2.1", "10.66.1.1", 1, 2);
        c.Deliver(new SisUnidataIndication(4, 9, SisAddress.FromParts(0, 0, 1, 1), 1, 9, from, ok));
        device.ToHost.Take().Should().Equal(ok);
    }
}
