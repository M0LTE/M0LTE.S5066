using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Core.Tests.T2;

/// <summary>
/// Authoring aid for the P7-7 scenarios (core-architecture.md 8, authoring; 9, phase 7, P7-7). Every
/// expected value here is worked out from the rows and the rulings by hand, written as field values,
/// and encoded with the phase 1 codecs: the waveform each transmission must use, the EOW each header
/// must carry, the EOT and reception window of each D_PDU from its block (C-0104, C-0350, C-1043),
/// the fill (C-1154). Nothing is read back from the implementation. Set S5066_WRITE_P77 to a
/// directory to write the files; otherwise the test does nothing.
/// </summary>
public class P77Scenarios
{
    private const uint Us = 0x0102;
    private const uint P3 = 0x0103;
    private const uint P4 = 0x0104;
    private const uint Broadcast = 0x0FFFFFFF;
    private const int Preamble = 600;
    private const int Flush = 150;

    /// <summary>EOW CAPABILITY, Ed4 1, Duplex 0 (C-0426, C-0488 to C-0491).</summary>
    private const int Capability = 0xF01;

    /// <summary>EOW SENDER-APPROACH: Needed 0, strategy mixed 10, Fixed 0 (open question 8; C-0478, C-1018).</summary>
    private const int Approach = 0xB04;

    private static readonly Wf W2L = new(2, Interleaver.Long, 256, 4800);
    private static readonly Wf W4L = new(4, Interleaver.Long, 256, 4800);
    private static readonly Wf W4S = new(4, Interleaver.Short, 128, 1200);
    private static readonly Wf W6L = new(6, Interleaver.Long, 256, 4800);

    private readonly record struct Wf(int Wn, Interleaver Il, int BlockBytes, int BlockMs);

    [Fact]
    public void Write()
    {
        string? dir = Environment.GetEnvironmentVariable("S5066_WRITE_P77");
        if (dir is null)
        {
            return;
        }

        void Save(string area, string name, ScenarioBuilder b)
        {
            string path = Path.Combine(dir, area, name + ".jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Join("\n", b.Lines()) + "\n");
        }

        Save("drc", "tx-sender-approach", SenderApproach());
        Save("drc", "tx-default-until-recommendation", DefaultUntilRecommendation());
        Save("drc", "tx-follows-max-speed", FollowsMaxSpeed());
        Save("drc", "tx-low-speed-for-short-transmission", LowSpeedForShort());
        Save("drc", "tx-min-over-peers", MinOverPeers());
        Save("drc", "tx-group-at-default", GroupAtDefault());
        Save("drc", "tx-max-segment-caps-segments", MaxSegmentCaps());
        Save("drc", "tx-reserved-speed-ignored", ReservedSpeedIgnored());
        Save("drc", "tx-off-ladder-recommendation", OffLadder());
        Save("drc", "tx-empty-table-fixed", EmptyTableFixed());
        Save("sis", "management-node-info-speed", NodeInfoSpeed());
    }

    // ---- the synthetic table and fixture -------------------------------------------------------

    /// <summary>
    /// The synthetic table of the P7-7 scenarios: ladder WN 2 Long, WN 4 Long, WN 4 Short, WN 6 Long
    /// (positions 0 to 3); no thresholds, so the receiver half computes no recommendation of its own
    /// (corpus-format.md 6.2 step 6); the sender constants as each scenario says.
    /// </summary>
    private static JsonObject Table(int stepDownWaits = 2, int ceilingInitial = 2, int ceilingCap = 4) => new()
    {
        ["ladder"] = new JsonArray(Rung(W2L), Rung(W4L), Rung(W4S), Rung(W6L)),
        ["thresholds"] = new JsonArray(),
        ["smoothing_weight"] = 8,
        ["clean_run"] = 2,
        ["rx_error_limit_ppt"] = 500,
        ["rx_ceiling_initial"] = 2,
        ["rx_ceiling_cap"] = 8,
        ["tx_step_down_waits"] = stepDownWaits,
        ["tx_ceiling_initial"] = ceilingInitial,
        ["tx_ceiling_cap"] = ceilingCap,
    };

    private static JsonObject Rung(Wf w) => new() { ["wn"] = w.Wn, ["interleaver"] = InterleaverNames.ToName(w.Il) };

    private static JsonObject Fixture(params Wf[] w) => new()
    {
        ["preamble_ms"] = Preamble,
        ["flush_ms"] = Flush,
        ["waveforms"] = new JsonArray(w.Select(x => (JsonNode)new JsonObject
        {
            ["wn"] = x.Wn, ["interleaver"] = InterleaverNames.ToName(x.Il), ["block_bytes"] = x.BlockBytes, ["block_ms"] = x.BlockMs,
        }).ToArray()),
    };

    private static JsonObject NonArqConfig(bool table, params Wf[] w)
    {
        var c = new JsonObject
        {
            ["local_address"] = "0.0.1.2",
            ["csma"] = new JsonObject { ["mode"] = "jitter" },
            ["modem"] = Fixture(w),
        };
        if (table)
        {
            c["drc"] = new JsonObject { ["table"] = Table() };
        }

        return c;
    }

    // ---- encoding helpers ----------------------------------------------------------------------

    private static DpduHeader H(uint dst, uint src, int eow, int eot) => new(eow, eot, DpduAddress.MinimumSize(dst, src), dst, src);

    private static string Hex(byte[] b) => Convert.ToHexStringLower(b);

    private static byte[] Enc(Dpdu p) => DpduCodec.Encode(p);

    /// <summary>A PADDING D_PDU from a peer to us carrying an EOW, EOT 10 (a one-block burst, as dts-rx traces).</summary>
    private static byte[] PeerPad(uint src, int eow) => Enc(new Padding(H(Us, src, eow, 10)));

    private static int Eow(Eow e) => EowCodec.Encode(e);

    private static int MaxSpeed(int wn, Interleaver il) => Eow(new MaxSpeedEow(new SpeedInterleaver((int)il + 1, wn)));

    private static int LowSpeed(int wn, Interleaver il) => Eow(new LowSpeedEow(new SpeedInterleaver((int)il + 1, wn)));

    /// <summary>The S_UNIDATA_REQUEST of the non-ARQ scenarios: priority 0, SAP 5, DELIVERY MODE default, TTL 0 (A-0089, A-0091).</summary>
    private static string Request(SisAddress dst, byte[] updu) =>
        ScenarioBuilder.SisHex(new SisUnidataRequest(0, 5, dst, ServiceType.Default, 0, updu));

    /// <summary>The DATA C_PDU holding the DATA S_PDU SAP 5 to SAP 5 of a U_PDU requested at <paramref name="t"/>, TTD t + 1296000000 ms (A-0091, A-0158 to A-0163; B-0297).</summary>
    private static byte[] Cpdu(long t, byte[] updu)
    {
        var time = new TimeConfig(2026, 1, 0);
        byte[] spdu = SpduCodec.Encode(new SpduData(0, 5, 5, false, TtdCalendar.Encode(time, t + 1296000000L), updu));
        return CpduCodec.Encode(new CpduData(spdu));
    }

    private static Dpdu Type7(uint dst, int eow, int eot, int id, byte[] cpdu, int offset, int length, int window) =>
        new NonArqData(
            H(dst, Us, eow, eot),
            new NonArqFields(id, false, dst == Broadcast, cpdu.Length, offset, window),
            new Octets(cpdu.AsSpan(offset, length).ToArray()),
            true);

    /// <summary>One D_PDU of a transmission, built once its EOT is known.</summary>
    private delegate Dpdu Item(int eot);

    /// <summary>
    /// A transmission: the items back to back from offset 0, then the fill (PADDING to <paramref name="padTo"/>
    /// with the EOWs <paramref name="padEow"/> gives, as many as fit in the last block, C-1154), each
    /// D_PDU's EOT the half seconds from the start of its block to the end of the burst, rounded up
    /// (C-0104, C-1043). Returns the hex and the duration.
    /// </summary>
    private static (string Hex, long Duration) Transmission(Wf w, IReadOnlyList<Item> items, uint? padTo = null, Func<int, int>? padEow = null)
    {
        var sizes = items.Select(i => Enc(i(0)).Length).ToList();
        long total = sizes.Sum();
        long blocks = Math.Max(1, (total + w.BlockBytes - 1) / w.BlockBytes);
        long duration = Preamble + (blocks * w.BlockMs) + Flush;
        int EotAt(long offset) => (int)((duration - (Preamble + (offset / w.BlockBytes * w.BlockMs)) + 499) / 500);
        var bytes = new List<byte>();
        long at = 0;
        for (int i = 0; i < items.Count; i++)
        {
            bytes.AddRange(Enc(items[i](EotAt(at))));
            at += sizes[i];
        }

        if (padTo is uint pad)
        {
            int padSize = Enc(new Padding(H(pad, Us, 0, 0))).Length;
            for (int n = 0; at + padSize <= blocks * w.BlockBytes; n++)
            {
                bytes.AddRange(Enc(new Padding(H(pad, Us, padEow!(n), EotAt(at)))));
                at += padSize;
            }
        }

        return (Hex(bytes.ToArray()), duration);
    }

    private static ScenarioBuilder Transmit(ScenarioBuilder b, (string Hex, long Duration) t, Wf w) => b.ExpectTransmit(t.Hex, w.Wn, w.Il);

    /// <summary>
    /// The opening shared by the non-ARQ scenarios, after drc/rx-capability-records-ed4: conn 1 binds
    /// SAP 5 for non-ARQ at 0; at 1000 a burst starts; at 1500 conn 1's requests (dts.ttd starts at the
    /// first); at 2000 the burst's one block from the peers; at 3000 it ends, and the EOT prediction
    /// (EOT 10 at 2000, so 7000) starts csma.vdcd_end; at 7000 LBT rule 1, since the burst was
    /// addressed only to us (K-0067, K-0068), and contention, seed 0 slot 15, 45000 ms (K-0080);
    /// at 52000 the transmission.
    /// </summary>
    private static ScenarioBuilder Opening(ScenarioBuilder b, IEnumerable<string> requests, byte[] burst, int conns = 1)
    {
        for (int c = 1; c <= conns; c++)
        {
            b.SisConnect(0, c);
        }

        b.SisBytes(0, 1, "90eb00000401502000").ExpectSisBytes(1, "90eb00000403500800");
        b.BurstStarted(1000);
        bool first = true;
        foreach (string r in requests)
        {
            b.SisBytes(1500, 1, r);
            if (first)
            {
                b.ExpectTimerStart("dts.ttd", 1296000000);
                first = false;
            }
        }

        b.BytesReceived(2000, Hex(burst));
        b.BurstEnded(3000, snrTenthDb: 60).ExpectTimerStart("csma.vdcd_end", 4000);
        b.TimerExpired(7000, "csma.vdcd_end").ExpectTimerStart("csma.cont_wait", 45000);
        b.TimerExpired(52000, "csma.cont_wait").ExpectTimerCancel("dts.ttd");
        return b;
    }

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static readonly byte[] Hello = "hello"u8.ToArray();

    private const string Common =
        "Conn 1 binds SAP 5 for non-ARQ and gets S_BIND_ACCEPTED (A-0041, A-0047). At 1500, during a burst, it sends S_UNIDATA_REQUEST (priority 0, SAP 5, DELIVERY MODE default, TTL 0, so TTD 1500 + 1296000000 ms, no confirmation): no SIS output (A-0095), dts.ttd starts (C-1050). At 2000 the burst's one block holds PADDING D_PDUs (type 14) to 0.0.1.2, EOT 10, each carrying one EOW; the PADDING itself is ignored (C-0588). A CAPABILITY with the Ed4 bit records its sender Ed4 (interp C-1261). Burst end 3000: csma.vdcd_end 4000; at 7000 LBT rule 1 (K-0067, K-0068), seed 0 slot 15, CONT_WAIT 45000 (K-0080). The transmit queue empties, so dts.ttd is cancelled (C-1050). Fixture: preamble 600, flush 150; per rung the block bytes and ms given in the config (test data). The DATA C_PDU holds the DATA S_PDU SAP 5 to SAP 5 with its TTD; each type 7 has address size 3, and its EOT and reception window are the half seconds from the start of its block to the end of the burst, rounded up (C-0104, C-0350, C-1043). ";

    private const string TableNote =
        "drc.table (corpus-format.md 6.1): ladder WN 2 Long, WN 4 Long, WN 4 Short, WN 6 Long (positions 0 to 3); no thresholds, so this node makes no recommendation of its own (corpus-format.md 6.2); sender constants tx_step_down_waits 2, tx_ceiling_initial 2, tx_ceiling_cap 4. ";

    // ---- scenarios -----------------------------------------------------------------------------

    private static ScenarioBuilder SenderApproach()
    {
        var b = new ScenarioBuilder(
            "drc-tx-sender-approach",
            ["C-0477", "C-0478", "C-0479", "C-0480", "C-0481", "C-0482", "C-0483", "C-0484", "C-1018", "C-0948", "C-1286", "C-1260", "C-0426", "C-1261", "C-1154"],
            "Ed4 C.6.8, pp.C-74 to C-75; C.7.6.3.1, p.C-121",
            "With a calibrated table, the first header of a transmission carrying data to an Ed4 peer holds our SENDER-APPROACH: variable speed, mixed strategy, EOW 12 not needed.",
            NonArqConfig(true, W2L),
            Common + TableNote + "0.0.1.3 sends only CAPABILITY, so no recommendation has come and the transmission goes at drc.default_speed, WN 2 Long. The type 7 to 0.0.1.3 carries data to it, so its header takes SENDER-APPROACH, EOW 0xB04: bits 7-4 zero (C-0483), EOW 12 Needed 0 since our modem reports the received waveform (C-0481, C-0482), strategy 10 mixed, Fixed 0 (C-1018; open question 8, interp C-1286), placed after any owed EOW and the top-priority recommendations, of which there are none (interp C-1260). The rotation set is empty, so the 20 PADDING D_PDUs that fill the 256-byte block (11 bytes each) carry CAPABILITY (C-0426, C-1154).");
        byte[] cpdu = Cpdu(1500, Hello);
        Opening(b, [Request(new SisAddress(P3), Hello)], PeerPad(P3, Capability));
        var t = Transmission(W2L, [eot => Type7(P3, Approach, eot, 0, cpdu, 0, cpdu.Length, eot)], P3, _ => Capability);
        Transmit(b, t, W2L);
        b.TransmitDone(52000 + t.Duration);
        return b;
    }

    private static ScenarioBuilder DefaultUntilRecommendation()
    {
        byte[] hello2 = "again"u8.ToArray();
        var b = new ScenarioBuilder(
            "drc-tx-default-until-recommendation",
            ["C-0929", "C-0932", "C-0459", "C-0461", "C-0894", "C-1280", "C-1284", "C-1287", "C-1286", "C-0426"],
            "Ed4 C.7.6.2, pp.C-119 to C-120; C.6.5, p.C-72",
            "A peer that has sent no recommendation gets drc.default_speed; once its MAX-SPEED has come, the next transmission to it goes at that rung.",
            NonArqConfig(true, W2L, W6L),
            Common + TableNote + "0.0.1.3 sends only CAPABILITY at 2000, so the first transmission, at 52000, goes at drc.default_speed, WN 2 Long (plan 6.2; interp C-1284): the type 7 with SENDER-APPROACH (0xB04, interp C-1286) and 20 PADDING with CAPABILITY, 5550 ms. At 60000 a second burst from 0.0.1.3 holds one PADDING to us, EOT 10 (end 66000), EOW MAX-SPEED WN 6 Long (0x8A6: interleaver code 101, speed 6; C-0459, C-0462). Nothing is queued, so no VDCD timer runs (K-0111). At 63000 conn 1 sends 'again' to 0.0.1.3 (TTD 63000 + 1296000000 ms): dts.ttd starts, and with the prediction ahead, csma.vdcd_end runs to 66000; then LBT rule 1 (K-0067) and contention, the second draw of seed 0, slot 4, 12000 ms (K-0080). At 78000 the type 7 (C_PDU ID 1, C-1040) goes at WN 6 Long, the rung MAX-SPEED names, which is on the ladder (open question 5, interp C-1280). Its D_PDUs are within one 256-byte block, so this is not a bulk transmission and LOW-SPEED would be used, but none has come, so MAX-SPEED stands in (interp C-1284). Fixture WN 6 Long: 256 bytes and 4800 ms a block (test data).");
        byte[] cpdu1 = Cpdu(1500, Hello);
        Opening(b, [Request(new SisAddress(P3), Hello)], PeerPad(P3, Capability));
        var t1 = Transmission(W2L, [eot => Type7(P3, Approach, eot, 0, cpdu1, 0, cpdu1.Length, eot)], P3, _ => Capability);
        Transmit(b, t1, W2L);
        b.TransmitDone(52000 + t1.Duration);
        b.BurstStarted(60000).BytesReceived(61000, Hex(PeerPad(P3, MaxSpeed(6, Interleaver.Long)))).BurstEnded(62000, snrTenthDb: 60);
        b.SisBytes(63000, 1, Request(new SisAddress(P3), hello2)).ExpectTimerStart("dts.ttd", 1296000000).ExpectTimerStart("csma.vdcd_end", 3000);
        b.TimerExpired(66000, "csma.vdcd_end").ExpectTimerStart("csma.cont_wait", 12000);
        b.TimerExpired(78000, "csma.cont_wait").ExpectTimerCancel("dts.ttd");
        byte[] cpdu2 = Cpdu(63000, hello2);
        var t2 = Transmission(W6L, [eot => Type7(P3, Approach, eot, 1, cpdu2, 0, cpdu2.Length, eot)], P3, _ => Capability);
        Transmit(b, t2, W6L);
        b.TransmitDone(78000 + t2.Duration);
        return b;
    }

    private static ScenarioBuilder FollowsMaxSpeed()
    {
        byte[] big = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var b = new ScenarioBuilder(
            "drc-tx-follows-max-speed",
            ["C-0459", "C-0460", "C-0461", "C-0462", "C-0929", "C-0931", "C-0932", "C-0894", "C-1280", "C-1285", "C-1286"],
            "Ed4 C.6.5, p.C-72; C.7.6.2, pp.C-119 to C-120",
            "A transmission whose data to the peer needs more than one block at the peer's MAX-SPEED rung is a bulk transmission and goes at that rung.",
            NonArqConfig(true, W2L, W4S, W6L),
            Common + TableNote + "The burst from 0.0.1.3 holds CAPABILITY, then MAX-SPEED WN 6 Long (0x8A6), then LOW-SPEED WN 4 Short (0x964: interleaver code 011, speed 4; C-0472). The U_PDU is 300 bytes (0x00 to 0xff, then 0x00 to 0x2b), so its type 7 is more than one 256-byte block of the MAX-SPEED rung: a bulk transmission (open question 7, interp C-1285), which goes at the MAX-SPEED rung WN 6 Long, on the ladder (open question 5, interp C-1280), not at LOW-SPEED. The type 7 (C_PDU ID 0, the whole C_PDU as one segment, dts.max_segment_size 1023) carries SENDER-APPROACH (interp C-1286) and spans two blocks; the rest of the second block is filled with PADDING with CAPABILITY (C-1154), EOT 10, while the type 7, starting in block 0, has EOT and window 20 (C-1043).");
        byte[] cpdu = Cpdu(1500, big);
        Opening(b, [Request(new SisAddress(P3), big)], Cat(PeerPad(P3, Capability), PeerPad(P3, MaxSpeed(6, Interleaver.Long)), PeerPad(P3, LowSpeed(4, Interleaver.Short))));
        var t = Transmission(W6L, [eot => Type7(P3, Approach, eot, 0, cpdu, 0, cpdu.Length, eot)], P3, _ => Capability);
        Transmit(b, t, W6L);
        b.TransmitDone(52000 + t.Duration);
        return b;
    }

    private static ScenarioBuilder LowSpeedForShort()
    {
        var b = new ScenarioBuilder(
            "drc-tx-low-speed-for-short-transmission",
            ["C-0472", "C-0473", "C-0462", "C-0929", "C-0931", "C-0894", "C-1280", "C-1285", "C-1286"],
            "Ed4 C.6.6, p.C-73; C.7.6.2, pp.C-119 to C-120",
            "A transmission whose data to the peer fits one block at its MAX-SPEED rung is a latency transmission and goes at the peer's LOW-SPEED rung.",
            NonArqConfig(true, W2L, W4S, W6L),
            Common + TableNote + "The burst from 0.0.1.3 holds CAPABILITY, MAX-SPEED WN 6 Long (0x8A6) and LOW-SPEED WN 4 Short (0x964). The U_PDU is 'hello': its 35-byte type 7 fits one 256-byte block of the MAX-SPEED rung, so the transmission is for latency (open question 7, interp C-1285) and goes at the LOW-SPEED rung, WN 4 Short, on the ladder with exactly that interleaver (open question 5) and not above MAX-SPEED (C-0894). Fixture WN 4 Short: 128 bytes and 1200 ms a block (test data), so one block, 1950 ms, EOT and window ceil(1350 / 500) = 3; the type 7 carries SENDER-APPROACH (interp C-1286), then 8 PADDING with CAPABILITY fill the block (C-1154).");
        byte[] cpdu = Cpdu(1500, Hello);
        Opening(b, [Request(new SisAddress(P3), Hello)], Cat(PeerPad(P3, Capability), PeerPad(P3, MaxSpeed(6, Interleaver.Long)), PeerPad(P3, LowSpeed(4, Interleaver.Short))));
        var t = Transmission(W4S, [eot => Type7(P3, Approach, eot, 0, cpdu, 0, cpdu.Length, eot)], P3, _ => Capability);
        Transmit(b, t, W4S);
        b.TransmitDone(52000 + t.Duration);
        return b;
    }

    private static ScenarioBuilder MinOverPeers()
    {
        byte[] hi = "hi"u8.ToArray();
        var b = new ScenarioBuilder(
            "drc-tx-min-over-peers",
            ["C-0894", "C-0930", "C-1283", "C-0459", "C-0936", "C-1280", "C-1286", "C-1154"],
            "Ed4 C.7.5.4.4, p.C-115; C.7.6.2, p.C-120",
            "A transmission carrying data to two peers goes at the lower of their rungs, by ladder order, so it never exceeds either peer's MAX-SPEED.",
            NonArqConfig(true, W4L, W6L),
            Common + TableNote + "The burst holds CAPABILITY and MAX-SPEED WN 6 Long (0x8A6) from 0.0.1.3, then CAPABILITY and MAX-SPEED WN 4 Long (0x885) from 0.0.1.4, all to 0.0.1.2 and recorded per sender (interp C-1262). Conn 1 sends 'hello' to 0.0.1.3 and then 'hi' to 0.0.1.4, both at 1500, so both C_PDUs share a TTD. One transmission carries both (C_PDU IDs 0 and 1, by age, C-0853); its rung is the lowest over the addressees by ladder order (C-0894, C-0930; open question 6, interp C-1283): WN 4 Long (position 1), below WN 6 Long (position 3). Each type 7 is the first header to its peer in the transmission and carries data to it, so each takes SENDER-APPROACH (interp C-1260, C-1286). Both addressees are recorded Ed4, so the block is filled with PADDING to the lowest, 0.0.1.3, with CAPABILITY (C-1154). Fixture WN 4 Long: 256 bytes and 4800 ms a block (test data).");
        byte[] c1 = Cpdu(1500, Hello);
        byte[] c2 = Cpdu(1500, hi);
        Opening(b, [Request(new SisAddress(P3), Hello), Request(new SisAddress(P4), hi)],
            Cat(PeerPad(P3, Capability), PeerPad(P3, MaxSpeed(6, Interleaver.Long)), PeerPad(P4, Capability), PeerPad(P4, MaxSpeed(4, Interleaver.Long))));
        var t = Transmission(W4L,
            [eot => Type7(P3, Approach, eot, 0, c1, 0, c1.Length, eot), eot => Type7(P4, Approach, eot, 1, c2, 0, c2.Length, eot)],
            P3, _ => Capability);
        Transmit(b, t, W4L);
        b.TransmitDone(52000 + t.Duration);
        return b;
    }

    private static ScenarioBuilder GroupAtDefault()
    {
        byte[] all = "all"u8.ToArray();
        var b = new ScenarioBuilder(
            "drc-tx-group-at-default",
            ["C-0894", "C-0930", "C-0937", "C-1283", "C-0122", "C-0123", "C-0398", "C-1286", "C-1154"],
            "Ed4 C.7.5.4.4, p.C-115; C.7.6.3, p.C-120",
            "A broadcast destination counts as drc.default_speed, so a transmission carrying data to it and to a peer that recommended a faster rung goes at the default.",
            NonArqConfig(true, W2L, W6L),
            Common + TableNote + "The burst from 0.0.1.3 holds CAPABILITY and MAX-SPEED WN 6 Long (0x8A6). Conn 1 sends 'hello' to 0.0.1.3 and then 'all' to the broadcast address 31.255.255.255 (C-0123), both at 1500. No recommendation can exist for a group or broadcast destination (C-0937), so it counts as drc.default_speed, WN 2 Long (plan 6.2 non-ARQ; interp C-1283), and the transmission takes the lowest rung over its addressees (C-0894, C-0930): WN 2 Long. The type 7 to 0.0.1.3 (C_PDU ID 0) carries SENDER-APPROACH (interp C-1286); the type 7 to broadcast (C_PDU ID 1, GROUP ADDRESS 1, address size 7, C-0122, C-0133) carries EMPTY (C-0398, C-0937). Not every addressee is recorded Ed4, so no PADDING: the rest of the block is the modem's zero fill (C-1154).");
        byte[] c1 = Cpdu(1500, Hello);
        byte[] c2 = Cpdu(1500, all);
        Opening(b, [Request(new SisAddress(P3), Hello), Request(new SisAddress(SisAddress.MaxValue, true), all)],
            Cat(PeerPad(P3, Capability), PeerPad(P3, MaxSpeed(6, Interleaver.Long))));
        var t = Transmission(W2L,
            [eot => Type7(P3, Approach, eot, 0, c1, 0, c1.Length, eot), eot => Type7(Broadcast, 0, eot, 1, c2, 0, c2.Length, eot)]);
        Transmit(b, t, W2L);
        b.TransmitDone(52000 + t.Duration);
        return b;
    }

    private static ScenarioBuilder MaxSegmentCaps()
    {
        var b = new ScenarioBuilder(
            "drc-tx-max-segment-caps-segments",
            ["C-0474", "C-0475", "C-0476", "C-0549", "C-0523", "C-0525", "C-0526", "C-0527", "C-0398", "C-0419"],
            "Ed4 C.6.7, p.C-74; C.5.2, pp.C-61 to C-62",
            "A received MAX-SEGMENT caps the segments of new C_PDUs to that peer at its value x 4 bytes.",
            NonArqConfig(false, W2L),
            Common + "drc.table is empty: the speed stays drc.default_speed, WN 2 Long. The burst from 0.0.1.3, a peer of unknown edition, holds one PADDING with MAX-SEGMENT 2 (EOW 0xA02; C-0475), recorded (interp C-1262). Conn 1 sends 'hello' to 0.0.1.3: its 11-byte DATA C_PDU is cut into segments of at most 2 x 4 = 8 bytes (C-0476), dts.max_segment_size being 1023 (C-0523): offsets 0 (8 bytes) and 8 (3 bytes), both C_PDU ID 0 and C_PDU size 11 (C-0525 to C-0527), one size for the whole transmission (C-0549). The peer's edition is unknown, so both carry EMPTY (C-0398, C-0419) and there is no PADDING (C-1154); both start in block 0, so EOT and window 10.");
        byte[] cpdu = Cpdu(1500, Hello);
        Opening(b, [Request(new SisAddress(P3), Hello)], PeerPad(P3, Eow(new MaxSegmentEow(2))));
        var t = Transmission(W2L,
            [eot => Type7(P3, 0, eot, 0, cpdu, 0, 8, eot), eot => Type7(P3, 0, eot, 0, cpdu, 8, cpdu.Length - 8, eot)]);
        Transmit(b, t, W2L);
        b.TransmitDone(52000 + t.Duration);
        return b;
    }

    private static ScenarioBuilder ReservedSpeedIgnored()
    {
        var b = new ScenarioBuilder(
            "drc-tx-reserved-speed-ignored",
            ["C-0466", "C-0469", "C-0471", "C-0459", "C-1262", "C-1280", "C-1286"],
            "Ed4 C.6.5.1, p.C-72",
            "A MAX-SPEED with a reserved speed is ignored and the previous MAX-SPEED stands, so the transmission goes at the earlier recommended rung.",
            NonArqConfig(true, W2L, W6L),
            Common + TableNote + "The burst from 0.0.1.3 holds CAPABILITY, MAX-SPEED WN 6 Long (0x8A6), then MAX-SPEED with the reserved speed 14 and interleaver Long (0x8AE; C-0466) and MAX-SPEED with interleaver 111 and speed 2 (0x8E2; C-0469): both ignored, the earlier one kept (interp C-0471, C-1262). The transmission goes at WN 6 Long (open question 5, interp C-1280); 'hello' fits one block, a latency transmission with no LOW-SPEED, so MAX-SPEED stands in (interp C-1284). The type 7 carries SENDER-APPROACH (interp C-1286), then 20 PADDING with CAPABILITY.");
        byte[] cpdu = Cpdu(1500, Hello);
        Opening(b, [Request(new SisAddress(P3), Hello)],
            Cat(PeerPad(P3, Capability), PeerPad(P3, MaxSpeed(6, Interleaver.Long)), PeerPad(P3, 0x8AE), PeerPad(P3, 0x8E2)));
        var t = Transmission(W6L, [eot => Type7(P3, Approach, eot, 0, cpdu, 0, cpdu.Length, eot)], P3, _ => Capability);
        Transmit(b, t, W6L);
        b.TransmitDone(52000 + t.Duration);
        return b;
    }

    private static ScenarioBuilder OffLadder()
    {
        var b = new ScenarioBuilder(
            "drc-tx-off-ladder-recommendation",
            ["C-0461", "C-0473", "C-0459", "C-0894", "C-1280", "C-1286"],
            "Ed4 C.6.5, p.C-72; C.7.5.4.4, p.C-115",
            "A recommended rung not on our ladder maps to the highest ladder rung whose data rate is not above it and whose interleaver is not shorter.",
            NonArqConfig(true, W2L, W4S, W6L),
            Common + TableNote + "The burst from 0.0.1.3 holds CAPABILITY and MAX-SPEED WN 5 Short (0x865: interleaver 011, speed 5), which is not on the ladder. WN 5 is 1600 bit/s (M0LTE.Ms110d docs/waveforms.md, from MIL-STD-188-110D Table D-I). Ladder rungs not faster and with an interleaver not shorter than Short: WN 2 Long (300), WN 4 Long (1200) and WN 4 Short (1200); WN 6 Long is 3200 and too fast. The highest by ladder order is WN 4 Short (open question 5, interp C-1280); speeds compare by data rate, not WN number. 'hello' fits one block: latency, no LOW-SPEED, so MAX-SPEED stands in (interp C-1284). Fixture WN 4 Short: 128 bytes and 1200 ms (test data); the type 7 carries SENDER-APPROACH (interp C-1286), 8 PADDING with CAPABILITY fill the block.");
        byte[] cpdu = Cpdu(1500, Hello);
        Opening(b, [Request(new SisAddress(P3), Hello)], Cat(PeerPad(P3, Capability), PeerPad(P3, MaxSpeed(5, Interleaver.Short))));
        var t = Transmission(W4S, [eot => Type7(P3, Approach, eot, 0, cpdu, 0, cpdu.Length, eot)], P3, _ => Capability);
        Transmit(b, t, W4S);
        b.TransmitDone(52000 + t.Duration);
        return b;
    }

    private static ScenarioBuilder EmptyTableFixed()
    {
        var b = new ScenarioBuilder(
            "drc-tx-empty-table-fixed",
            ["C-0926", "C-1018", "C-0479", "C-0929", "C-0426", "C-1260", "C-1154"],
            "Ed4 C.7.6.2, p.C-119; C.6.8, p.C-74",
            "With an empty drc.table a received MAX-SPEED changes nothing: the transmission stays at drc.default_speed and carries no SENDER-APPROACH.",
            NonArqConfig(false, W2L),
            Common + "drc.table is empty (defaults.json): no calibration, so the sender varies nothing (core-architecture.md 9, phase 7, design shape item 6; open question 13). The burst from 0.0.1.3 holds CAPABILITY, MAX-SPEED WN 6 Long (0x8A6) and LOW-SPEED WN 4 Short (0x964), recorded (interp C-1262) but not used: 'hello' goes at drc.default_speed, WN 2 Long, as the fixed-rate baseline, and no SENDER-APPROACH is sent (interp C-1286), so the type 7 and the 20 PADDING carry CAPABILITY (C-0426, C-1154). With an empty table no step down or failed-trial ceiling runs either (interp C-1281, C-1282); dts-arq/ack-wait-expiry-retransmit shows a retransmission after silence staying at the default.");
        byte[] cpdu = Cpdu(1500, Hello);
        Opening(b, [Request(new SisAddress(P3), Hello)],
            Cat(PeerPad(P3, Capability), PeerPad(P3, MaxSpeed(6, Interleaver.Long)), PeerPad(P3, LowSpeed(4, Interleaver.Short))));
        var t = Transmission(W2L, [eot => Type7(P3, Capability, eot, 0, cpdu, 0, cpdu.Length, eot)], P3, _ => Capability);
        Transmit(b, t, W2L);
        b.TransmitDone(52000 + t.Duration);
        return b;
    }

    private static ScenarioBuilder NodeInfoSpeed()
    {
        var b = new ScenarioBuilder(
            "sis-management-node-info-speed",
            ["S-0164", "S-0038", "S-0034", "S-0037", "C-0833", "C-0459", "C-1280", "C-1284"],
            "Ed4 S.4.2, S.4.3, pp.S-5 to S-6; S.5.11, p.S-15",
            "The node information message shows the waveform of the last transmission and, per peer, the waveform last used to it.",
            NonArqConfig(true, W2L, W6L),
            Common + TableNote + "Conn 2 connects at 0 and never binds. The burst from 0.0.1.3 holds CAPABILITY and MAX-SPEED WN 6 Long (0x8A6), so 'hello' goes at WN 6 Long (interp C-1280, C-1284) with SENDER-APPROACH and 20 PADDING with CAPABILITY. At 60000 conn 2 asks for node information (MSG TYPE 2, interp S-0164) and is answered at once: this node's address field e0 00 01 02, the waveform of the last transmission, 6 and interleaver code 4 (long), one peer (0.0.1.3 has a receive quality record from its burst, C-0833), and its record: address field e0 00 01 03; soft link state 0; the waveform last used to it, 6 and 4; the last burst heard from it, wn 2 code 4; SNR 60 tenths (003c); 57 whole seconds since that burst ended at 3000 (0039). Before any transmission the speeds shown are drc.default_speed, as sis/management-node-info shows.");
        byte[] cpdu = Cpdu(1500, Hello);
        Opening(b, [Request(new SisAddress(P3), Hello)], Cat(PeerPad(P3, Capability), PeerPad(P3, MaxSpeed(6, Interleaver.Long))), conns: 2);
        var t = Transmission(W6L, [eot => Type7(P3, Approach, eot, 0, cpdu, 0, cpdu.Length, eot)], P3, _ => Capability);
        Transmit(b, t, W6L);
        b.TransmitDone(52000 + t.Duration);
        byte[] body = [0xe0, 0x00, 0x01, 0x02, 6, 4, 1, 0xe0, 0x00, 0x01, 0x03, 0, 6, 4, 2, 4, 0x00, 0x3c, 0x00, 0x39];
        b.SisBytes(60000, 2, "90eb0000021202").ExpectSisBytes(2, ScenarioBuilder.SisHex(new SisManagementMsgIndication(2, body)));
        return b;
    }
}
