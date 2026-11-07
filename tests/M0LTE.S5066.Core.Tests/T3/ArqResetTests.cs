using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests.T3;

/// <summary>
/// T3 (P4-4): two real nodes, one of which loses its ARQ state part way through a transfer, as a
/// node does after a power failure (C.7.3.1, C-0789). The synchronisation tests must notice, a FULL
/// RESET must bring both ends back to the initial window edges (C-0791, C-0792, C-0812, C-0822),
/// and the transfer must carry on: no U_PDU is delivered twice or out of order, every U_PDU sent
/// after the reset arrives, and the windows end in step. A variant (P4-7) duplicates and reorders data
/// D_PDUs within transmissions as well; a sweep of 300 seeds of both passed.
/// </summary>
public class ArqResetTests(ITestOutputHelper output)
{
    private const byte Sap = 5;

    public static TheoryData<ulong, bool> Cases()
    {
        var data = new TheoryData<ulong, bool>();
        for (ulong seed = 1; seed <= 6; seed++)
        {
            data.Add(seed, false);
            data.Add(seed, true);   // with data D_PDUs duplicated and reordered within transmissions (P4-7)
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_node_that_loses_its_arq_state_is_resynchronised_by_a_full_reset(ulong seed, bool mangle)
    {
        var parts = new List<DtsSublayer>();
        CoreDriver Factory(NodeConfig config, ulong s, IModemPort modem)
        {
            var context = new CoreContext(config, modem);
            var mac = new MacSublayer(context, new SplitMix64(s));
            var dts = new DtsSublayer(context, mac);
            mac.Transmitter = dts.Transmitter;
            var dispatcher = new Dispatcher(context, new SisSublayer(context), new CasSublayer(context), dts, mac);
            parts.Add(dts);
            return new CoreDriver(dispatcher.Handle, context.TakeOutputs);
        }

        // Late copies across transmissions are left out here: after the state loss node 1 cannot tell a
        // late copy of an old FSN, flags cleared, from new data, as no test can (C-0796).
        var channel = new ChannelModel(BlockErrorPpm: 50_000, BurstMissPpm: 20_000, HostLatencyMs: 50, TurnaroundMs: 100, EndOfBurstDelayMs: 50,
            DuplicatePpm: mangle ? 100_000 : 0, ReorderPpm: mangle ? 300_000 : 0);
        var sim = new Simulation(seed, channel, [Node("0.0.1.1"), Node("0.0.1.2")], Factory);
        uint a = sim.AddressOf(0);
        uint b = sim.AddressOf(1);
        sim.BindArq(0, 0, Sap);
        sim.BindArq(1, 0, Sap);

        var early = new List<byte[]>();
        for (int i = 0; i < 6; i++)
        {
            byte[] u = Payload(i, 120 + (i * 31));
            early.Add(u);
            sim.SendArq(0, 1_000 + (i * 10_000), b, Sap, u);
        }

        // Once the first transfer is over, node 1 loses its ARQ state (its windows go back to the
        // initial edges with no exchange): node 0 knows nothing of it. A loss while low FSNs are
        // still in flight can pass every test (C-0796: some losses go undetected), so the state is
        // lost at a quiet moment, when node 0's next FSN is well past the initial edges.
        long t = 0;
        do
        {
            t += 10_000;
            sim.Run(t);
        }
        while (t < 2_900_000 && (sim.Deliveries.Count(d => d.Receiver == 1) < early.Count || parts[0].HeldArq.Count > 0));

        ArqLink lost = ((TransmitQueue)parts[1].Transmitter).LinkOf(a)!;
        ((TransmitQueue)parts[0].Transmitter).LinkOf(b)!.Outstanding.Should().BeEmpty();
        lost.RxLwe.Should().BePositive();
        lost.Reset();

        var late = new List<byte[]>();
        for (int i = 0; i < 4; i++)
        {
            byte[] u = Payload(10 + i, 90 + (i * 23));
            late.Add(u);
            // The first, partly sent when the lost state is found, is discarded by the reset (C-0826);
            // the rest are sent once the reset is long over.
            sim.SendArq(0, i == 0 ? 3_000_000 : 6_000_000 + (i * 10_000), b, Sap, u);
        }

        sim.Run(40_000_000);
        sim.Violations.Should().BeEmpty();

        int resets = sim.Bursts.Sum(x => x.Dpdus.Count(d => d.Pdu is ResetWinResync { Flags.FullResetCmnd: true }));
        output.WriteLine($"seed {seed} mangle {mangle}: {sim.Bursts.Count} bursts, {resets} FULL RESET CMDs, {sim.Duplicated} duplicated, {sim.Reordered} reordered");
        resets.Should().BePositive("the lost state is found by the synchronisation tests");

        // Delivered: an in-order subsequence of everything sent, with no repeats; every late U_PDU arrives.
        List<byte[]> sent = [.. early, .. late];
        var got = sim.Deliveries.Where(d => d.Receiver == 1 && d.Src == a).Select(d => d.UPdu).ToList();
        int next = 0;
        foreach (byte[] u in got)
        {
            int at = sent.FindIndex(next, s => s.AsSpan().SequenceEqual(u));
            at.Should().BeGreaterThanOrEqualTo(next, "each U_PDU is delivered once and in order");
            next = at + 1;
        }

        foreach (byte[] u in late.Skip(1))
        {
            got.Count(g => g.SequenceEqual(u)).Should().Be(1, "a U_PDU sent after the reset arrives");
        }

        parts[0].HeldArq.Should().BeEmpty("every C_PDU is confirmed or was rejected by the reset");
        ArqLink mine = ((TransmitQueue)parts[0].Transmitter).LinkOf(b)!;
        ArqLink theirs = ((TransmitQueue)parts[1].Transmitter).LinkOf(a)!;
        mine.Suspended.Should().BeFalse();
        theirs.Suspended.Should().BeFalse();
        mine.Outstanding.Should().BeEmpty();
        mine.TxLwe.Should().Be(theirs.RxLwe);
        mine.Seq.Add(mine.TxUwe, 1).Should().Be(theirs.RxLwe);
    }

    private static byte[] Payload(int index, int length)
    {
        var u = new byte[length];
        for (int i = 0; i < length; i++)
        {
            u[i] = (byte)((index * 13) + i);
        }

        return u;
    }

    private static JsonObject Node(string address) => new()
    {
        ["local_address"] = address,
        ["csma"] = new JsonObject { ["mode"] = "jitter", ["num_cont_slots"] = 4, ["cont_slot_width_ms"] = 1000, ["lbt_wait_vdcd_ms"] = 1000, ["lbt_wait_dcd_ms"] = 5000 },
        ["modem"] = new JsonObject
        {
            ["preamble_ms"] = 600,
            ["flush_ms"] = 150,
            ["waveforms"] = new JsonArray(new JsonObject { ["wn"] = 2, ["interleaver"] = "long", ["block_bytes"] = 64, ["block_ms"] = 1200 }),
        },
        ["cas"] = new JsonObject
        {
            ["max_links"] = 4,
            ["link_request_timeout_ms"] = 30_000,
            ["link_request_retries"] = 10,
            ["first_data_timeout_ms"] = 10_000_000,
        },
        // The link must stay up for the whole run: no idle close or maximum session (A-0321 derives them when unset).
        ["sis"] = new JsonObject { ["soft_link_idle_ms"] = 1_000_000_000, ["soft_link_max_ms"] = 1_000_000_000 },
        ["dts"] = new JsonObject { ["max_segment_size"] = 40, ["max_window"] = 6, ["max_transmission_ms"] = 12_000, ["ack_wait_ms"] = 20_000 },
    };
}
