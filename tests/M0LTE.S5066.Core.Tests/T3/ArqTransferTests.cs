using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests.T3;

/// <summary>
/// T3: an ARQ transfer between two real nodes over a lossy half duplex channel (P4-2 review). Both
/// clients send several multi-segment U_PDUs to each other, with a small transmit window so that
/// window blocking, DATA-ACK merging, bitmap acknowledgements, the half duplex failed rule and
/// <c>dts.ack_wait</c> retransmission all take part. Every U_PDU must arrive exactly once and in
/// order, every C_PDU must be confirmed, and at the end the two ends' windows must agree: each
/// sender's TX LWE and TX UWE + 1 equal the receiver's RX LWE, with nothing outstanding. The Ed3
/// variant (P4-5) has each node record the other Ed3 first, so a node calls with Ed4 clear and the link
/// uses the 8-bit family (B-0122, B-0232), with segments small enough that the FSNs wrap modulo 256.
/// </summary>
/// <remarks>
/// <para>
/// The long-transfer variants (P4-7) send 40 U_PDUs one way and 10 the other through a channel that
/// also duplicates data D_PDUs within a transmission, reorders them, and adds late DATA-ONLY copies of
/// earlier transmissions' data (see <see cref="ChannelModel"/>), on both families: copies behind RX
/// LWE are acknowledged and not delivered again (C-0777, C-1126), copies already held do not move RX
/// UWE back (C-0778), FSNs arrive out of order and wrap modulo 256 on the 8-bit family, and the
/// synchronisation tests never fire (C-0790): no RESET/WIN-RESYNC is ever sent. A sweep of 400 seeds
/// of each family passed (P4-7).
/// </para>
/// <para>
/// A review sweep of 300 seeds at 30% block loss, 10% missed bursts and fades passed wherever the
/// link was made (12 seeds ended half open after the Caller gave up, which is link setup, not this
/// test), and 100 seeds with the full window and 60 s transmissions passed with acknowledgements
/// of up to 144 bytes (the long form).
/// </para>
/// </remarks>
public class ArqTransferTests(ITestOutputHelper output)
{
    private const byte Sap = 5;

    public static TheoryData<ulong, bool, bool, bool> Cases()
    {
        var data = new TheoryData<ulong, bool, bool, bool>();
        for (ulong seed = 1; seed <= 8; seed++)
        {
            data.Add(seed, false, false, false);   // dts.ack_wait_ms set
            data.Add(seed, true, false, false);    // dts.ack_wait_ms unset: the derived wait (C-1120)
            data.Add(seed, false, true, false);    // the 8-bit family (P4-5)
            data.Add(seed, false, false, true);    // a long transfer with duplication and reordering (P4-7)
            data.Add(seed, false, true, true);     // the same on the 8-bit family
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_transfer_under_loss_completes_in_order_with_the_windows_in_step(ulong seed, bool derivedAckWait, bool ed3, bool mangle)
    {
        var parts = new List<DtsSublayer>();
        CoreDriver Factory(NodeConfig config, ulong s, IModemPort modem)
        {
            var context = new CoreContext(config, modem);
            if (ed3)
            {
                // Each node has the other recorded Ed3, so whichever calls (and in a crossed call both
                // do, B-0405) calls with Ed4 clear and the link uses the 8-bit family.
                context.Peers.SetEdition(parts.Count == 0 ? 0x0102u : 0x0101u, KnownEdition.Ed3);
            }

            var mac = new MacSublayer(context, new SplitMix64(s));
            var dts = new DtsSublayer(context, mac);
            mac.Transmitter = dts.Transmitter;
            var dispatcher = new Dispatcher(context, new SisSublayer(context), new CasSublayer(context), dts, mac);
            parts.Add(dts);
            return new CoreDriver(dispatcher.Handle, context.TakeOutputs);
        }

        var channel = new ChannelModel(BlockErrorPpm: 150_000, BurstMissPpm: 50_000, HostLatencyMs: 50, TurnaroundMs: 100, EndOfBurstDelayMs: 50,
            DuplicatePpm: mangle ? 100_000 : 0, ReorderPpm: mangle ? 300_000 : 0, StaleCopyPpm: mangle ? 300_000 : 0);
        var sim = new Simulation(seed, channel, [Node("0.0.1.1", derivedAckWait, ed3), Node("0.0.1.2", derivedAckWait, ed3)], Factory);
        uint a = sim.AddressOf(0);
        uint b = sim.AddressOf(1);
        sim.BindArq(0, 0, Sap);
        sim.BindArq(1, 0, Sap);

        // The long transfer: 40 U_PDUs one way and 10 the other, about 11 kB, so on the 8-bit family
        // the FSNs wrap modulo 256 several times.
        var sent = new List<byte[]>[] { [], [] };
        for (int i = 0; i < (mangle ? 40 : 10); i++)
        {
            byte[] u = Payload(0, i, mangle ? 150 + (i * 37 % 200) : 150 + (i * 37));
            sent[0].Add(u);
            sim.SendArq(0, 1_000 + (i * 20_000), b, Sap, u);
        }

        for (int i = 0; i < (mangle ? 10 : 4); i++)
        {
            byte[] u = Payload(1, i, 90 + (i * 53 % 160));
            sent[1].Add(u);
            sim.SendArq(1, 150_000 + (i * 60_000), a, Sap, u);
        }

        sim.Run(mangle ? 200_000_000 : 40_000_000);

        sim.Violations.Should().BeEmpty();
        // Retransmissions: data D_PDUs whose (sender, FSN) went before. DATA-ACKs: merged acknowledgements.
        var seen = new HashSet<(int, int)>();
        int retransmitted = 0;
        int dataAcks = 0;
        foreach (SimBurst burst in sim.Bursts)
        {
            foreach (SimDpdu d in burst.Dpdus)
            {
                int? fsn = d.Pdu switch { DataOnly x => x.TxFsn, DataAck x => x.TxFsn, Ed3DataOnly x => x.TxFsn, Ed3DataAck x => x.TxFsn, _ => null };
                dataAcks += d.Pdu is DataAck or Ed3DataAck ? 1 : 0;
                if (d.Pdu is DataOnly or DataAck or AckOnly or Ed3DataOnly or Ed3DataAck or Ed3AckOnly)
                {
                    (d.Pdu.Type <= 3).Should().Be(ed3, "one family on the link (C-0763)");
                }
                if (fsn is int f && !seen.Add((burst.Sender, f)))
                {
                    retransmitted++;
                }
            }
        }

        int expiries = sim.Log.Count(l => l.Contains("timer_expired dts.ack_wait", StringComparison.Ordinal));
        int resets = sim.Bursts.Sum(x => x.Dpdus.Count(d => d.Pdu is ResetWinResync or Ed3ResetWinResync));
        output.WriteLine($"seed {seed} derived {derivedAckWait} ed3 {ed3} mangle {mangle}: {sim.Bursts.Count} bursts, {sim.Collisions} collisions, {retransmitted} retransmissions, {dataAcks} DATA-ACKs, {expiries} ack_wait expiries, "
            + $"{sim.Duplicated} duplicated, {sim.Reordered} reordered, {sim.StaleCopies} late copies, last delivery at {sim.Deliveries.Max(d => d.T)}");
        retransmitted.Should().BePositive("the loss makes the nodes retransmit");
        resets.Should().Be(0, "copies and reordering never put the windows out of step (C-0790)");
        if (mangle)
        {
            sim.Duplicated.Should().BePositive();
            sim.Reordered.Should().BePositive();
            sim.StaleCopies.Should().BePositive();
        }

        for (int receiver = 0; receiver < 2; receiver++)
        {
            int sender = 1 - receiver;
            var got = sim.Deliveries.Where(d => d.Receiver == receiver && d.Src == sim.AddressOf(sender)).Select(d => d.UPdu).ToList();
            got.Should().HaveCount(sent[sender].Count, $"node {receiver} gets each U_PDU of node {sender} once");
            for (int i = 0; i < got.Count; i++)
            {
                got[i].Should().Equal(sent[sender][i], $"U_PDU {i} from node {sender}, in order");
            }
        }

        sim.Rejections.Should().Be(0);
        for (int n = 0; n < 2; n++)
        {
            parts[n].HeldArq.Should().BeEmpty($"node {n} has every ARQ C_PDU confirmed");
            var tx = (TransmitQueue)parts[n].Transmitter;
            var rx = (TransmitQueue)parts[1 - n].Transmitter;
            ArqLink mine = tx.LinkOf(sim.AddressOf(1 - n))!;
            ArqLink theirs = rx.LinkOf(sim.AddressOf(n))!;
            mine.Should().NotBeNull();
            theirs.Should().NotBeNull();
            mine.ShortFamily.Should().Be(ed3);
            mine.Outstanding.Should().BeEmpty();
            mine.TxLwe.Should().Be(theirs.RxLwe, "TX LWE meets the peer's RX LWE");
            mine.Seq.Add(mine.TxUwe, 1).Should().Be(theirs.RxLwe, "every FSN sent has been received");
            mine.Seq.Add(theirs.RxUwe, 1).Should().Be(theirs.RxLwe, "nothing held beyond RX LWE");
        }
    }

    private static byte[] Payload(int node, int index, int length)
    {
        var u = new byte[length];
        for (int i = 0; i < length; i++)
        {
            u[i] = (byte)((node * 101) + (index * 13) + i);
        }

        return u;
    }

    private static JsonObject Node(string address, bool derivedAckWait, bool ed3) => new()
    {
        ["local_address"] = address,
        // With dts.ack_wait_ms set it must exceed the peer's LBT wait: one shorter than lbt_wait_dcd_ms
        // lets a sender that hears nothing retransmit before a peer that heard only part of its
        // transmission (and so waits lbt_wait_dcd_ms) may answer, again and again. The derived wait
        // covers the longer LBT wait, so that variant keeps the default 30000.
        ["csma"] = derivedAckWait
            ? new JsonObject { ["mode"] = "jitter", ["num_cont_slots"] = 4, ["cont_slot_width_ms"] = 1000, ["lbt_wait_vdcd_ms"] = 1000 }
            : new JsonObject { ["mode"] = "jitter", ["num_cont_slots"] = 4, ["cont_slot_width_ms"] = 1000, ["lbt_wait_vdcd_ms"] = 1000, ["lbt_wait_dcd_ms"] = 5000 },
        ["modem"] = new JsonObject
        {
            ["preamble_ms"] = 600,
            ["flush_ms"] = 150,
            ["waveforms"] = new JsonArray(new JsonObject
            {
                ["wn"] = 2, ["interleaver"] = "long", ["block_bytes"] = 64, ["block_ms"] = 1200,
            }),
        },
        ["cas"] = new JsonObject
        {
            ["max_links"] = 4,
            ["link_request_timeout_ms"] = 30_000,
            ["link_request_retries"] = 10,
            ["first_data_timeout_ms"] = 10_000_000,
        },
        ["dts"] = derivedAckWait
            ? new JsonObject { ["max_segment_size"] = 40, ["max_window"] = 6, ["max_transmission_ms"] = 12_000 }
            : new JsonObject { ["max_segment_size"] = ed3 ? 8 : 40, ["max_window"] = 6, ["max_transmission_ms"] = 12_000, ["ack_wait_ms"] = 20_000 },
    };
}
