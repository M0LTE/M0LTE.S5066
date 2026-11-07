using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests.T3;

/// <summary>
/// T3: two real nodes that call each other at the same moment (crossed calls, B-0405). Each node's
/// client sends an ARQ U_PDU to the other, so both SISs ask for a physical link at once. Whatever
/// the channel does, the two nodes must end with one link that both agree on (the same Ed4 and
/// FSN family in D_CONNECTION_MADE), or with no link at all, and neither may be left calling.
/// A sweep of 600 seeds per case passed when this was written (P4-1 review); the seeds kept here
/// cover single and double crossings, collisions, and half-open links under loss.
/// </summary>
public class CrossedCallTests(ITestOutputHelper output)
{
    private const byte Sap = 5;

    private static readonly ChannelModel Ideal = new();

    private static readonly ChannelModel Realistic = new(HostLatencyMs: 50, TurnaroundMs: 100, EndOfBurstDelayMs: 50);

    public static TheoryData<ulong, int, int> Cases()
    {
        var data = new TheoryData<ulong, int, int>();
        for (ulong seed = 1; seed <= 12; seed++)
        {
            foreach (int stagger in new[] { 0, 700, 2500 })
            {
                data.Add(seed, stagger, 0);
                data.Add(seed, stagger, 1);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Crossed_calls_end_with_one_agreed_link(ulong seed, int staggerMs, int channel)
    {
        Run(seed, staggerMs, channel == 0 ? Ideal : Realistic, staleEd3: false, expectLink: true);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Crossed_calls_agree_even_when_one_node_holds_a_stale_ed3_record(ulong seed, int staggerMs, int channel)
    {
        // Node 0 recorded node 1 as Ed3 earlier (B-0408), node 1 knows nothing of node 0, so the
        // two requests differ in their Ed4 bit. The link both sides use must still be the same.
        Run(seed, staggerMs, channel == 0 ? Ideal : Realistic, staleEd3: true, expectLink: true);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    [InlineData(4UL)]
    [InlineData(5UL)]
    [InlineData(6UL)]
    [InlineData(67UL)]    // half open: the Caller gives up, the Called end waits for first data
    [InlineData(124UL)]   // the same, with a stale Ed3 record
    public void Crossed_calls_with_loss_never_leave_a_disagreeing_link_or_a_node_calling(ulong seed)
    {
        Run(seed, 0, Realistic with { BlockErrorPpm = 150_000, BurstMissPpm = 50_000 }, staleEd3: seed % 2 == 0, expectLink: false);
    }

    private void Run(ulong seed, int staggerMs, ChannelModel channel, bool staleEd3, bool expectLink)
    {
        var parts = new List<(CoreContext Context, DtsSublayer Dts)>();
        CoreDriver Factory(NodeConfig config, ulong s, IModemPort modem)
        {
            var context = new CoreContext(config, modem);
            var mac = new MacSublayer(context, new SplitMix64(s));
            var dts = new DtsSublayer(context, mac);
            mac.Transmitter = dts.Transmitter;
            var dispatcher = new Dispatcher(context, new SisSublayer(context), new CasSublayer(context), dts, mac);
            parts.Add((context, dts));
            return new CoreDriver(dispatcher.Handle, context.TakeOutputs);
        }

        var sim = new Simulation(seed, channel, [Node("0.0.1.1"), Node("0.0.1.2")], Factory);
        uint a = sim.AddressOf(0);
        uint b = sim.AddressOf(1);
        if (staleEd3)
        {
            parts[0].Context.Peers.SetEdition(b, KnownEdition.Ed3);
        }

        sim.BindArq(0, 0, Sap);
        sim.BindArq(1, 0, Sap);
        sim.SendArq(0, 1_000, b, Sap, [0xA0]);
        sim.SendArq(1, 1_000 + staggerMs, a, Sap, [0xB0]);

        // Long enough for every retry of a call, and short of the first-data abort: ARQ data is not
        // sent before P4-2, so the Called end would abort its link at that time.
        sim.Run(1_500_000);

        sim.Violations.Should().BeEmpty();
        string trace = string.Join("\n", sim.LinkStates.Select(l => $"{l.T} n{l.Node} {l.Peer} {l.State}"));
        output.WriteLine($"seed {seed} stagger {staggerMs}: {sim.Bursts.Count} bursts, {sim.Collisions} collisions\n{trace}");

        LinkStateName? last0 = sim.LinkStates.LastOrDefault(l => l.Node == 0).State;
        LinkStateName? last1 = sim.LinkStates.LastOrDefault(l => l.Node == 1).State;
        last0.Should().NotBe(LinkStateName.Calling, trace);
        last1.Should().NotBe(LinkStateName.Calling, trace);

        var c0 = parts[0].Dts.Peers.ConnectionOf(b);
        var c1 = parts[1].Dts.Peers.ConnectionOf(a);
        if (c0 is not null && c1 is not null)
        {
            c0.Should().Be(c1, "the two ends of the link use the same edition and family\n" + trace);
        }
        else if (c0 is not null || c1 is not null)
        {
            // Half open: the Caller gave up after every accept was lost. The end that has the link
            // must be the Called end, waiting for its first DATA C_PDU, which aborts it (B-0215).
            int linked = c0 is not null ? 0 : 1;
            uint peer = linked == 0 ? b : a;
            sim.Log.Should().ContainMatch($"* n{linked} out timer_start cas.first_data/{DpduAddress.ToDotted(peer)} 2000000", "a half-open link is the Called end's\n" + trace);
        }

        if (expectLink)
        {
            c0.Should().NotBeNull(trace);
            sim.LinkStates.Count(l => l.Node == 0 && l.State == LinkStateName.Linked).Should().Be(1, trace);
            sim.LinkStates.Count(l => l.Node == 1 && l.State == LinkStateName.Linked).Should().Be(1, trace);
        }
    }

    private static JsonObject Node(string address) => new()
    {
        ["local_address"] = address,
        ["csma"] = new JsonObject { ["mode"] = "jitter", ["num_cont_slots"] = 16 },
        ["modem"] = new JsonObject
        {
            ["preamble_ms"] = 600,
            ["flush_ms"] = 150,
            ["waveforms"] = new JsonArray(new JsonObject
            {
                ["wn"] = 2, ["interleaver"] = "long", ["block_bytes"] = 256, ["block_ms"] = 4800,
            }),
        },
        ["cas"] = new JsonObject
        {
            ["max_links"] = 4,
            ["link_request_timeout_ms"] = 90_000,
            ["link_request_retries"] = 3,
            ["first_data_timeout_ms"] = 2_000_000,
        },
        // The link must stay up for the whole run: no idle close or maximum session (A-0321 derives them when unset).
        ["sis"] = new JsonObject { ["soft_link_idle_ms"] = 2_000_000, ["soft_link_max_ms"] = 2_000_000 },
        ["dts"] = new JsonObject { ["ack_wait_ms"] = 120_000 },
    };
}
