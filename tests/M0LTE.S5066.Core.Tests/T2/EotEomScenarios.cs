using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;
using System.Text.Json.Nodes;

namespace M0LTE.S5066.Core.Tests.T2;

/// <summary>
/// Authoring aid for the issue #66 and #97 scenarios (corpus 0.31.0; interp C-1289, C-1290). Every
/// expected value is worked out by hand from the rows, written as field values and encoded with the
/// phase 1 codecs; nothing is read back from the implementation. Set S5066_WRITE_EOTEOM to a
/// directory to write the files; otherwise the test does nothing.
/// </summary>
public class EotEomScenarios
{
    private const uint Us = 0x0102;
    private const uint P3 = 0x0103;
    private const uint P9 = 0x0109;

    [Fact]
    public void Write()
    {
        string? dir = Environment.GetEnvironmentVariable("S5066_WRITE_EOTEOM");
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

        Save("dts-rx", "eot-anchor-by-block", AnchorByBlock());
        Save("dts-rx", "eot-anchor-split-block", AnchorSplitBlock());
        Save("dts-tx", "eom-in-length-limit", EomInLengthLimit());
        Save("dts-tx", "eom-oversize-dpdu", EomOversizeDpdu());
        Save("dts-tx", "eom-align-stretched", EomAlignStretched());
    }

    private static DpduHeader H(uint dst, uint src, int eot) => new(0, eot, DpduAddress.MinimumSize(dst, src), dst, src);

    private static string Hex(byte[] b) => Convert.ToHexStringLower(b);

    private static byte[] Enc(Dpdu p) => DpduCodec.Encode(p);

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>The C_PDU of the dts-rx traces: a DATA C_PDU holding a DATA S_PDU SAP 9 to SAP 9 with U_PDU "hi", taken from dts-rx-vdcd-end-after-burst-end.</summary>
    private static readonly byte[] RxCpdu = ((NonArqData)DpduCodec.DecodeFirst(
        Convert.FromHexString("90eb7000108f01090103000601000600000014dee7000099006869bf44d33b"), PeerEdition.Ed4).Value!).Segment.Span.ToArray();

    /// <summary>A received single-segment NON-ARQ-DATA from 0.0.1.3 to 0.0.1.9, reception window 20, as the dts-rx traces.</summary>
    private static byte[] RxType7(int id, int eot) =>
        Enc(new NonArqData(H(P9, P3, eot), new NonArqFields(id, false, false, RxCpdu.Length, 0, 20), new Octets(RxCpdu), true));

    private static JsonObject RxConfig() => new()
    {
        ["local_address"] = "0.0.1.2",
        ["csma"] = new JsonObject { ["mode"] = "jitter" },
    };

    private const string RxCommon =
        "Conn 1 binds SAP 5 for non-ARQ and gets S_BIND_ACCEPTED (A-0041, A-0047). Every received D_PDU is a single-segment NON-ARQ-DATA (type 7, address size 4, C_PDU ID as stated, reception window 20) from 0.0.1.3 to 0.0.1.9 whose segment is a DATA C_PDU holding a DATA S_PDU from SAP 9 to SAP 9 with U_PDU \"hi\" (31 bytes, as in dts-rx-vdcd-end-after-burst-end). burst_started gives block_ms (corpus 0.31.0), so each EOT counts from the latest instant its D_PDU can have started on air: the arrival time of the bytes_received holding its first byte, less block_ms, plus ceil(offset x block_ms / bytes in that bytes_received) (interp C-1289); the burst's estimate is the earliest end time (C-0843). After the burst, conn 1's S_UNIDATA_REQUEST (priority 0 to SAP 5 at 0.0.1.3, U_PDU \"hello\", no confirmation) is queued: no SIS output (A-0095, K-0096), dts.ttd starts at its TTD (C-1050), and with the channel busy until the estimate csma.vdcd_end runs to it (K-0073, K-0111). At its expiry VDCD clears last and the D_PDUs heard were addressed to another node, so rule 3 gives lbt_wait_vdcd_ms 3000 (K-0071, K-0073). ";

    private static ScenarioBuilder RxOpening(ScenarioBuilder b)
    {
        b.SisConnect(0, 1);
        b.SisBytes(0, 1, "90eb00000401502000").ExpectSisBytes(1, "90eb00000403500800");
        return b;
    }

    private const string Hello = "90eb0000111405e000010300000000000568656c6c6f";

    /// <summary>
    /// Two blocks of 4800 ms. Air: preamble ends at 1000, block 0 is on air 1000 to 5800 and block 1
    /// 5800 to 10600, flush to 10750; each block is delivered 200 ms after it ends. The sender's EOTs
    /// count from the start of each D_PDU's block (C-1043): 20 in block 0, 10 in block 1.
    /// </summary>
    private static ScenarioBuilder AnchorByBlock()
    {
        var b = new ScenarioBuilder(
            "dts-rx-eot-anchor-by-block",
            ["C-1289", "C-1024", "C-0842", "C-0843", "C-0844", "K-0021", "K-0071", "K-0073", "K-0111", "C-1043", "C-1050"],
            "Ed4 C.7.5.1.1, p.C-110; C.4.2.3, p.C-13; K.2.2.5, p.K-3",
            "With the block duration from the modem, a received EOT counts from the start of the D_PDU's block (the block's arrival less one block), plus the D_PDU's share of the block, not from the arrival of its header.",
            RxConfig(),
            RxCommon + "burst_started at 1000 gives block_ms 4800. The block at 6000 holds 62 bytes: D_PDU A (C_PDU ID 1, EOT 20) at offset 0, anchor 6000 - 4800 + 0 = 1200, end 11200; D_PDU B (C_PDU ID 2, EOT 20) at offset 31, anchor 1200 + ceil(31 x 4800 / 62) = 3600, end 13600. The block at 10800 holds D_PDU C (C_PDU ID 3, EOT 10) at offset 0 of 31 bytes: anchor 6000, end 11000. The estimate is 11000 (C-0843), against 15800 under C-1024 alone. The burst ends at 10800 (eom, 2 blocks) with nothing queued, so no output. The request at 10900 starts dts.ttd and csma.vdcd_end 100 ms; at 11000 rule 3 gives csma.lbt_wait 3000.");
        RxOpening(b);
        b.BurstStarted(1000, 4800);
        b.BytesReceived(6000, Hex(Cat(RxType7(1, 20), RxType7(2, 20))));
        b.BytesReceived(10800, Hex(RxType7(3, 10)));
        b.BurstEnded(10800, blocks: 2);
        b.SisBytes(10900, 1, Hello).ExpectTimerStart("dts.ttd", 1296000000).ExpectTimerStart("csma.vdcd_end", 100);
        b.TimerExpired(11000, "csma.vdcd_end").ExpectTimerStart("csma.lbt_wait", 3000);
        return b;
    }

    /// <summary>
    /// Two blocks of 1000 ms and 21 bytes. Air: block 0 on air 1000 to 2000, block 1 2000 to 3000,
    /// flush to 3100; each block delivered 100 ms after it ends. D_PDU A starts at offset 11 of block 0,
    /// so its sender counts EOT from 1000: ceil(2100 / 500) = 5 (C-1043).
    /// </summary>
    private static ScenarioBuilder AnchorSplitBlock()
    {
        byte[] a = RxType7(1, 5);
        byte[] block0 = Cat(new byte[11], a[..10]);
        byte[] block1 = a[10..];
        var b = new ScenarioBuilder(
            "dts-rx-eot-anchor-split-block",
            ["C-1289", "C-1024", "C-0842", "C-0843", "C-0844", "C-0973", "D-0007", "K-0021", "K-0071", "K-0073", "K-0111", "C-1050"],
            "Ed4 C.7.5.1.1, p.C-110; C.4.2.3, p.C-13; C.7.7.3.2, p.C-124",
            "A D_PDU whose header completes in the block after the one holding its first byte counts its EOT from that first block, plus the byte's share of it rounded up.",
            RxConfig(),
            RxCommon + "burst_started at 1000 gives block_ms 1000. The block at 2100 holds 11 zero bytes and the first 10 bytes of D_PDU A (C_PDU ID 1, EOT 5); the block at 3100 holds its other 21 bytes, completing its header and data (C-0973, D-0007). Its first byte is at offset 11 of the 21-byte block that arrived at 2100: anchor 2100 - 1000 + ceil(11 x 1000 / 21) = 1100 + 524 = 1624, end 1624 + 2500 = 4124 (C-1289), against 3100 + 2500 = 5600 under C-1024 alone. The burst ends at 3200 (eom, 2 blocks) with nothing queued, so no output. The request at 3300 starts dts.ttd and csma.vdcd_end 824 ms; at 4124 rule 3 gives csma.lbt_wait 3000.");
        RxOpening(b);
        b.BurstStarted(1000, 1000);
        b.BytesReceived(2100, Hex(block0));
        b.BytesReceived(3100, Hex(block1));
        b.BurstEnded(3200, blocks: 2);
        b.SisBytes(3300, 1, Hello).ExpectTimerStart("dts.ttd", 1296000000).ExpectTimerStart("csma.vdcd_end", 824);
        b.TimerExpired(4124, "csma.vdcd_end").ExpectTimerStart("csma.lbt_wait", 3000);
        return b;
    }

    /// <summary>The DATA C_PDU holding the DATA S_PDU SAP 5 to SAP 5 of a U_PDU requested at 0, TTD 1296000000 ms (A-0091, A-0158 to A-0163).</summary>
    private static byte[] TxCpdu(byte[] updu)
    {
        var time = new TimeConfig(2026, 1, 0);
        byte[] spdu = SpduCodec.Encode(new SpduData(0, 5, 5, false, TtdCalendar.Encode(time, 1296000000L), updu));
        return CpduCodec.Encode(new CpduData(spdu));
    }

    private static string Request(byte[] updu) =>
        ScenarioBuilder.SisHex(new SisUnidataRequest(0, 5, new SisAddress(P3), ServiceType.Default, 0, updu));

    private static byte[] TxType7(int eot, byte[] cpdu, int offset, int length, int window) =>
        Enc(new NonArqData(H(P3, Us, eot), new NonArqFields(0, false, false, cpdu.Length, offset, window), new Octets(cpdu.AsSpan(offset, length).ToArray()), true));

    private static JsonObject TxConfig(int blockBytes, int maxSegment) => new()
    {
        ["local_address"] = "0.0.1.2",
        ["csma"] = new JsonObject { ["mode"] = "jitter", ["num_cont_slots"] = 1 },
        ["modem"] = new JsonObject
        {
            ["preamble_ms"] = 600,
            ["flush_ms"] = 150,
            ["eom_bytes"] = 4,
            ["waveforms"] = new JsonArray(new JsonObject { ["wn"] = 2, ["interleaver"] = "long", ["block_bytes"] = blockBytes, ["block_ms"] = 4800 }),
        },
        ["dts"] = new JsonObject { ["max_segment_size"] = maxSegment, ["max_transmission_ms"] = 5550 },
    };

    private const string TxCommon =
        "Conn 1 binds SAP 5 for non-ARQ and gets S_BIND_ACCEPTED (A-0041, A-0047); at 0 it sends S_UNIDATA_REQUEST (priority 0 to SAP 5 at 0.0.1.3, TTL 0 so TTD 1296000000 ms, no retransmissions, no confirmation): no SIS output (A-0095), dts.ttd starts (C-1050), and csma.lbt_wait starts with lbt_wait_dcd_ms 30000 (K-0077). The DATA C_PDU holds the DATA S_PDU SAP 5 to SAP 5 with its TTD; each type 7 has address size 3 and C_PDU ID 0, and its EOT and reception window are the half seconds from the start of its block to the end of the burst, rounded up (C-0104, C-0350, C-1043). Fixture (test data): preamble 600, flush 150, WN 2 Long blocks of 4800 ms, eom_bytes 4, so n blocks carry n x block_bytes - 4 bytes and a burst of m bytes takes ceil((m + 4) / block_bytes) blocks (corpus-format.md 5; interp C-1290). dts.max_transmission_ms 5550 allows one block (600 + 4800 + 150). 0.0.1.3 is of unknown edition, so there is no PADDING fill (C-1154), and csma.num_cont_slots 1 makes the contention wait 0 (K-0080). ";

    /// <summary>
    /// U_PDU "0123456789": a 16-byte C_PDU in two 8-byte segments, two 32-byte D_PDUs. One 64-byte
    /// block holds both only if the EOM is ignored; with it the block carries 60 bytes.
    /// </summary>
    private static ScenarioBuilder EomInLengthLimit()
    {
        byte[] updu = "0123456789"u8.ToArray();
        byte[] cpdu = TxCpdu(updu);
        if (cpdu.Length != 16 || TxType7(0, cpdu, 0, 8, 0).Length != 32)
        {
            throw new InvalidOperationException("the scenario's sizes are not as described");
        }

        var b = new ScenarioBuilder(
            "dts-tx-eom-in-length-limit",
            ["C-1290", "K-0094", "K-0095", "C-0886", "C-1154", "C-0880", "C-0542", "C-0549", "C-0527", "C-0529", "C-0350", "C-1051", "C-1052", "D-0017", "K-0074", "C-1050"],
            "Ed4 C.7.5.4, pp.C-113 to C-114; Ed4 D, p.D-1; K.3.2.5, p.K-12",
            "The modem's end-of-message marker counts against the configured transmission length: two D_PDUs that fill one block exactly go in two one-block transmissions, not one two-block transmission over the limit.",
            TxConfig(64, 8),
            TxCommon + "U_PDU \"0123456789\": the C_PDU is 16 bytes, cut by dts.max_segment_size 8 into two 8-byte segments (C-0527, C-0529, C-0549), D_PDUs of 32 bytes. The one block within the limit carries 64 - 4 = 60 bytes, so the first transmission at 30000 takes segment 0 and segment 1 does not fit (C-0880, C-0542): 32 + 4 bytes is one block, 5550 ms, EOT ceil(4950 / 500) = 10. Without the marker both would fit the 64-byte block and the burst would need two blocks, 10350 ms, over the limit. The unsent segment (32 bytes) needs one more transmission, so the reception window is ceil((4950 + 11550) / 500) = 33, where 11550 is 5550 + 3000 (K-0074) + 3000 (one contention slot), committing to 30000 + 600 + 16500 = 47100 (C-1051, C-1052). The second transmission at 38550 (lbt_wait_vdcd_ms after the own transmission, K-0074) ends 44100, inside 47100: segment 1, offset 8, same ID and cut, EOT and window 10 (C-0350). It empties the queue, so dts.ttd is cancelled first (C-1050).");
        b.SisConnect(0, 1);
        b.SisBytes(0, 1, "90eb00000401502000").ExpectSisBytes(1, "90eb00000403500800");
        b.SisBytes(0, 1, Request(updu)).ExpectTimerStart("dts.ttd", 1296000000).ExpectTimerStart("csma.lbt_wait", 30000);
        b.TimerExpired(30000, "csma.lbt_wait").ExpectTransmit(Hex(TxType7(10, cpdu, 0, 8, 33)), 2, Interleaver.Long);
        b.TransmitDone(35550).ExpectTimerStart("csma.lbt_wait", 3000);
        b.TimerExpired(38550, "csma.lbt_wait").ExpectTimerCancel("dts.ttd").ExpectTransmit(Hex(TxType7(10, cpdu, 8, 8, 10)), 2, Interleaver.Long);
        b.TransmitDone(44100);
        return b;
    }

    /// <summary>
    /// U_PDU "0123456789": a 16-byte C_PDU in one segment, a 40-byte D_PDU, with 40-byte blocks. It does
    /// not fit the 36 bytes one block carries, so it goes alone in the blocks it needs, the marker
    /// included: ceil((40 + 4) / 40) = 2.
    /// </summary>
    private static ScenarioBuilder EomOversizeDpdu()
    {
        byte[] updu = "0123456789"u8.ToArray();
        byte[] cpdu = TxCpdu(updu);
        if (TxType7(0, cpdu, 0, cpdu.Length, 0).Length != 40)
        {
            throw new InvalidOperationException("the scenario's sizes are not as described");
        }

        var b = new ScenarioBuilder(
            "dts-tx-eom-oversize-dpdu",
            ["C-1290", "C-1042", "C-0551", "C-0878", "K-0094", "K-0095", "C-0104", "C-0891", "C-1043", "D-0005", "D-0017", "C-1050"],
            "Ed4 C.7.5.4 (item 5), p.C-113; C.5.3, p.C-65; Ed4 D, p.D-1",
            "A D_PDU too large for the configured transmission length goes in exactly the blocks it needs, counting the modem's end-of-message marker, and its EOT covers them.",
            TxConfig(40, 1023),
            TxCommon + "Here the blocks are 40 bytes. U_PDU \"0123456789\": the C_PDU is 16 bytes in one segment (dts.max_segment_size 1023), a 40-byte D_PDU. The one block within the limit carries 40 - 4 = 36 bytes, so the D_PDU does not fit; it is not blocked (C-0551, C-0878): the transmission is made the whole blocks it needs, ceil((40 + 4) / 40) = 2 (C-1042, C-1290), 600 + 9600 + 150 = 10350 ms. Its EOT counts from block 0 to the end, flush and marker included: ceil(9750 / 500) = 20 (C-0104, C-0891, D-0005, D-0017), and so does its reception window, 20, since nothing is left for later (C-0350). It empties the queue, so dts.ttd is cancelled first (C-1050).");
        b.SisConnect(0, 1);
        b.SisBytes(0, 1, "90eb00000401502000").ExpectSisBytes(1, "90eb00000403500800");
        b.SisBytes(0, 1, Request(updu)).ExpectTimerStart("dts.ttd", 1296000000).ExpectTimerStart("csma.lbt_wait", 30000);
        b.TimerExpired(30000, "csma.lbt_wait").ExpectTimerCancel("dts.ttd").ExpectTransmit(Hex(TxType7(20, cpdu, 0, cpdu.Length, 20)), 2, Interleaver.Long);
        b.TransmitDone(40350);
        return b;
    }

    /// <summary>
    /// dts-tx-block-align-ed4 up to its data transmission, with eom_bytes 4 and a one-block limit. The
    /// first segment's D_PDU (49 bytes at the maximum segment size) does not fit the 36 bytes one block
    /// carries, so the transmission is stretched for it (C-1042); cut at the block boundary at 40 it
    /// would still need two blocks, the EOM taking the second, so it is cut to end at 36 (C-1290).
    /// </summary>
    private static ScenarioBuilder EomAlignStretched()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "conformance", "corpus", "t2", "dts-tx", "block-align-ed4.jsonl");
        if (!File.Exists(path))
        {
            path = Path.Combine(FindRepoRoot(), "conformance", "corpus", "t2", "dts-tx", "block-align-ed4.jsonl");
        }

        List<JsonObject> lines = File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => (JsonObject)JsonNode.Parse(l)!).ToList();

        // The template's data transmission at 108000: two DATA-ONLY D_PDUs, then PADDING. Its C_PDU is
        // the two segments together (C_PCI, DATA S_PDU header and the 44-byte U_PDU, 50 bytes).
        int at = lines.FindIndex(l => l["out"]?.GetValue<string>() == "transmit" && lines.IndexOf(l) > lines.FindIndex(x => x["t"]?.GetValue<long>() == 108000));
        byte[] old = Convert.FromHexString(lines[at]["bytes"]!.GetValue<string>());
        var d0 = (DataOnly)DpduCodec.DecodeFirst(old, PeerEdition.Ed4).Value!;
        int n0 = Enc(d0).Length;
        var d1 = (DataOnly)DpduCodec.DecodeFirst(old.AsSpan(n0), PeerEdition.Ed4).Value!;
        byte[] cpdu = Cat(d0.Segment.Span.ToArray(), d1.Segment.Span.ToArray());
        if (cpdu.Length != 50)
        {
            throw new InvalidOperationException("the template is not as described");
        }

        var first = new DataOnly(d0.Header with { Eot = 10 }, d0.Flags with { TxWinUwe = true }, 0, new Octets(cpdu.AsSpan(0, 17)), true);
        if (Enc(first).Length != 36)
        {
            throw new InvalidOperationException("the D_PDU is not 36 bytes");
        }

        JsonObject header = lines[0];
        var config = (JsonObject)header["config"]!.DeepClone();
        config["modem"]!["eom_bytes"] = 4;
        config["dts"]!["max_transmission_ms"] = 5550;
        var b = new ScenarioBuilder(
            "dts-tx-eom-align-stretched",
            ["C-1290", "C-1155", "C-1042", "C-0551", "C-0553", "C-0871", "C-0872", "C-0176", "C-0185", "C-0186", "C-1122", "C-0104", "C-1043", "C-1154", "K-0094", "K-0095", "K-0074"],
            "Ed4 C.5.4, p.C-65; C.7.5.3.1, p.C-112; C.7.5.4, p.C-113",
            "A first ARQ segment that stretches the transmission past the configured length is cut so its D_PDU leaves room in its last block for the modem's end-of-message marker; cut at the block boundary, the marker would add a block.",
            config,
            "As dts-tx-block-align-ed4 up to its data transmission, with modem.eom_bytes 4 and dts.max_transmission_ms 5550, one block: 40-byte blocks carry 36 bytes once the marker has its room, and a burst of m bytes takes ceil((m + 4) / 40) blocks (corpus-format.md 5; interp C-1290). Everything before 108000 is unchanged: the 25-byte request needs one block. At 108000 the first segment at the maximum segment size, 30 bytes, is a 49-byte DATA-ONLY, which does not fit the 36 bytes the limit allows, so the transmission is stretched for it to ceil((49 + 4) / 40) = 2 blocks (C-0551, C-1042). It crosses the block boundary at 40 and is not the last segment, so towards the Ed4 peer it is cut (C-0553, C-1155); ending at 40 it would still need two blocks, since the marker would take a second, so it is cut to end where one block's room ends, 36 bytes, a 17-byte segment (interp C-1290). It now needs one block, so the transmission is one block, the configured length, and nothing else fits (C-1042, C-1155). FSN 0, C_PDU START, TX WIN UWE and TX WIN LWE, since it is the only FSN allocated (C-0176, C-0185, C-0186, C-1122), DELIVER IN ORDER as the SIS asked, EOW CAPABILITY (C-1260), EOT ceil(4950 / 500) = 10 (C-0104, C-1043). No room is left for fill (C-1154). The transmission lasts 5550 ms and ends at 113550, when dts.ack_wait/0.0.1.3 starts with dts.ack_wait_ms 60000; the other 33 bytes of the C_PDU are still queued, so csma.lbt_wait starts with lbt_wait_vdcd_ms 3000 after the own transmission (K-0074). Without the marker's room it would have been the 40-byte D_PDU and two blocks, 10350 ms, past the limit.");
        foreach (JsonObject l in lines.Skip(1).Take(at - 1))
        {
            if (l["in"] is not null)
            {
                b.In(l["t"]!.GetValue<long>(), l["in"]!.GetValue<string>(), Keys(l, "t", "in"));
            }
            else
            {
                b.Out(l["out"]!.GetValue<string>(), Keys(l, "out"));
            }
        }

        b.ExpectTransmit(Hex(Enc(first)), 2, Interleaver.Long);
        b.TransmitDone(113550).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000).ExpectTimerStart("csma.lbt_wait", 3000);
        return b;
    }

    private static JsonObject Keys(JsonObject line, params string[] skip)
    {
        var o = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> kv in line)
        {
            if (!skip.Contains(kv.Key))
            {
                o[kv.Key] = kv.Value?.DeepClone();
            }
        }

        return o;
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "conformance", "corpus")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("conformance submodule not found");
    }
}
