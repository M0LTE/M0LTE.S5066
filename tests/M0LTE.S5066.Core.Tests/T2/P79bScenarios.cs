using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using System.Text.Json.Nodes;

namespace M0LTE.S5066.Core.Tests.T2;

/// <summary>
/// Authoring aid for the P7-9b scenarios (corpus 0.32.0; interp C-1042, C-1155, C-1290 as revised): a
/// first ARQ segment to a peer recorded Ed4 is cut to the configured transmission length instead of
/// stretching the transmission past it, while towards an Ed3 peer the stretch stays. Every expected value
/// is worked out by hand from the rows, written as field values and encoded with the phase 1 codecs;
/// nothing is read back from the implementation. Set S5066_WRITE_P79B to a directory to write the files;
/// otherwise the test does nothing.
/// </summary>
public class P79bScenarios
{
    [Fact]
    public void Write()
    {
        string? dir = Environment.GetEnvironmentVariable("S5066_WRITE_P79B");
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

        Save("dts-tx", "ed4-first-segment-cut-to-limit", Ed4FirstSegmentCutToLimit());
        Save("dts-tx", "ed4-last-segment-cut-to-limit", Ed4LastSegmentCutToLimit());
        Save("dts-tx", "ed3-first-segment-stretches", Ed3FirstSegmentStretches());
    }

    private static string Hex(byte[] b) => Convert.ToHexStringLower(b);

    private static byte[] Enc(Dpdu p) => DpduCodec.Encode(p);

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static readonly string[] CommonRows = ["C-1042", "C-1155", "C-1290", "C-0551", "C-0553", "C-0878", "C-0887", "K-0094", "K-0095", "C-0176", "C-0185", "C-0186", "C-1122", "C-0104", "C-1043", "C-1154", "K-0074"];

    private const string Source = "Ed4 C.5.3 and C.5.4, p.C-65; C.7.5.4.1, p.C-113";

    /// <summary>
    /// dts-tx-block-align-ed4 with modem.eom_bytes 4, a one-block limit, dts.max_segment_size 100 and a
    /// 100-byte U_PDU: the first segment at the maximum size is a 119-byte DATA-ONLY that would need four
    /// blocks. It is cut to the 36 bytes one block carries after the marker's room.
    /// </summary>
    private static ScenarioBuilder Ed4FirstSegmentCutToLimit()
    {
        List<JsonObject> lines = Template("block-align-ed4.jsonl");
        (int at, byte[] prefix, DataOnly d0) = Ed4Template(lines);

        // The U_PDU: the template's 44 letters, then 56 more, 100 bytes in all.
        byte[] text = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQR"u8.ToArray();
        byte[] more = "STUVWXYZ0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKL"u8.ToArray();
        byte[] updu = Cat(text, more);
        if (updu.Length != 100)
        {
            throw new InvalidOperationException("the U_PDU is not 100 bytes");
        }

        // S_UNIDATA_REQUEST as the template's (priority 0, SAP 9 to SAP 9 at 0.0.1.3, ARQ, in order, TTL 0),
        // with the length 12 + 100 = 112 and the U_PDU length 100.
        string request = "90eb0000701409e000010300000000" + "0064" + Hex(updu);
        int sis = lines.FindIndex(l => l["in"]?.GetValue<string>() == "sis_bytes" && l["bytes"]!.GetValue<string>().StartsWith("90eb000038", StringComparison.Ordinal));
        lines[sis]["bytes"] = request;

        byte[] cpdu = Cat(prefix, updu);   // C_PCI and DATA S_PDU header (6 bytes), then the U_PDU: 106 bytes
        var first = new DataOnly(d0.Header with { Eot = 10 }, d0.Flags with { TxWinUwe = true }, 0, new Octets(cpdu.AsSpan(0, 17)), true);
        if (Enc(first).Length != 36)
        {
            throw new InvalidOperationException("the D_PDU is not 36 bytes");
        }

        var config = (JsonObject)lines[0]["config"]!.DeepClone();
        config["modem"]!["eom_bytes"] = 4;
        config["dts"]!["max_segment_size"] = 100;
        config["dts"]!["max_transmission_ms"] = 5550;
        var b = new ScenarioBuilder(
            "dts-tx-ed4-first-segment-cut-to-limit",
            CommonRows,
            Source,
            "Towards a peer recorded Ed4, a first ARQ segment too long for the configured transmission length is cut to end where the length's room ends, marker included, rather than stretching the transmission for it.",
            config,
            "As dts-tx-block-align-ed4 up to its data transmission, with modem.eom_bytes 4, dts.max_segment_size 100 (C-0503), dts.max_transmission_ms 5550 (one block: 600 + 4800 + 150) and a 100-byte U_PDU, the template's 44 letters followed by 'STUVWXYZ0123456789' and the letters again to 'L' (S_UNIDATA_REQUEST length 112, U_PDU length 100). 40-byte blocks carry 36 bytes once the marker has its room, and a burst of m bytes takes ceil((m + 4) / 40) blocks (corpus-format.md 5; interp C-1290). Everything before 108000 is unchanged: the U_PDU's length changes no earlier output, and the 25-byte request needs one block. The C_PDU is 106 bytes (6 of C_PCI and DATA S_PDU header, then the U_PDU). At 108000 the first segment at the maximum segment size, 100 bytes, is a 119-byte DATA-ONLY, which would need ceil(123 / 40) = 4 blocks. Towards a peer recorded Ed4 any segment may be shorter than the maximum (C-0553), so the limit blocks nothing (C-0551, C-0878) and the transmission is not stretched for it: it is cut to end where the configured length's room ends, 36 bytes, a 17-byte segment (interp C-1042, C-1155, C-1290; the space-filling use of short segments, C-0887). FSN 0, C_PDU START, TX WIN UWE and TX WIN LWE, since it is the only FSN allocated (C-0176, C-0185, C-0186, C-1122), DELIVER IN ORDER as the SIS asked, EOW CAPABILITY (C-1260), EOT ceil(4950 / 500) = 10 (C-0104, C-1043). No room is left for fill (C-1154). The transmission lasts 5550 ms, the limit, and ends at 113550, when dts.ack_wait/0.0.1.3 starts with dts.ack_wait_ms 60000; the other 89 bytes of the C_PDU are still queued, so csma.lbt_wait starts with lbt_wait_vdcd_ms 3000 after the own transmission (K-0074). Before corpus 0.32.0 the transmission was stretched to the 4 blocks and the segment cut to the last room end it passed, 116 bytes (a 97-byte segment), so 3 blocks and 15150 ms against a 5550 ms limit.");
        Copy(b, lines, at);
        b.ExpectTransmit(Hex(Enc(first)), 2, Interleaver.Long);
        b.TransmitDone(113550).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000).ExpectTimerStart("csma.lbt_wait", 3000);
        return b;
    }

    /// <summary>
    /// dts-tx-block-align-ed4 with a one-block limit, no end-of-message marker and the default maximum
    /// segment size: the whole 50-byte C_PDU is one segment, its last, a 69-byte DATA-ONLY that would need
    /// two blocks. It is cut to the 40 bytes of the one block.
    /// </summary>
    private static ScenarioBuilder Ed4LastSegmentCutToLimit()
    {
        List<JsonObject> lines = Template("block-align-ed4.jsonl");
        (int at, byte[] prefix, DataOnly d0) = Ed4Template(lines);
        byte[] cpdu = Cat(prefix, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQR"u8.ToArray());
        var first = new DataOnly(d0.Header with { Eot = 10 }, d0.Flags with { TxWinUwe = true }, 0, new Octets(cpdu.AsSpan(0, 21)), true);
        if (Enc(first).Length != 40)
        {
            throw new InvalidOperationException("the D_PDU is not 40 bytes");
        }

        var config = (JsonObject)lines[0]["config"]!.DeepClone();
        ((JsonObject)config["dts"]!).Remove("max_segment_size");
        config["dts"]!["max_transmission_ms"] = 5550;
        var b = new ScenarioBuilder(
            "dts-tx-ed4-last-segment-cut-to-limit",
            CommonRows,
            Source,
            "Towards a peer recorded Ed4, a C_PDU's last segment too long for the configured transmission length is cut to fill the length, rather than stretching the transmission for it.",
            config,
            "As dts-tx-block-align-ed4 up to its data transmission, with dts.max_segment_size back at its default 1023 and dts.max_transmission_ms 5550 (one block: 600 + 4800 + 150); no end-of-message marker (modem.eom_bytes 0), so one 40-byte block carries 40 bytes. Everything before 108000 is unchanged: the 25-byte request needs one block. At 108000 the whole 50-byte C_PDU (C_PCI, DATA S_PDU header and the 44-byte U_PDU) would be one segment, its last, a 69-byte DATA-ONLY needing 2 blocks. Towards a peer recorded Ed4 any segment may be shorter than the maximum (C-0553), and the cut to the capacity applies to a last segment too (C-1155), so the transmission is not stretched for it (interp C-1042): it is cut to end at the one block's end, 40 bytes, a 21-byte segment, C_PDU START but not END. FSN 0, TX WIN UWE and TX WIN LWE, since it is the only FSN allocated (C-0176, C-0185, C-0186, C-1122), DELIVER IN ORDER as the SIS asked, EOW CAPABILITY (C-1260), EOT ceil(4950 / 500) = 10 (C-0104, C-1043). No room is left for fill (C-1154). The transmission lasts 5550 ms and ends at 113550, when dts.ack_wait/0.0.1.3 starts with dts.ack_wait_ms 60000; the other 29 bytes of the C_PDU are still queued, so csma.lbt_wait starts with lbt_wait_vdcd_ms 3000 after the own transmission (K-0074). Before corpus 0.32.0 the transmission was stretched to 2 blocks for the whole 69-byte D_PDU, 10350 ms against a 5550 ms limit.");
        Copy(b, lines, at);
        b.ExpectTransmit(Hex(Enc(first)), 2, Interleaver.Long);
        b.TransmitDone(113550).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000).ExpectTimerStart("csma.lbt_wait", 3000);
        return b;
    }

    /// <summary>
    /// dts-tx-no-align-ed3-peer-arq with a one-block limit and dts.max_segment_size 100: the whole 50-byte
    /// C_PDU is one ED3-DATA-ONLY of 68 bytes, more than the 64 one block carries. Towards an Ed3 peer the
    /// segment may not be shortened, so the transmission is stretched to the 2 blocks it needs.
    /// </summary>
    private static ScenarioBuilder Ed3FirstSegmentStretches()
    {
        List<JsonObject> lines = Template("no-align-ed3-peer-arq.jsonl");
        int t = lines.FindIndex(l => l["t"]?.GetValue<long>() == 76500);
        int at = lines.FindIndex(t, l => l["out"]?.GetValue<string>() == "transmit");
        byte[] old = Convert.FromHexString(lines[at]["bytes"]!.GetValue<string>());
        var segs = new List<byte[]>();
        Ed3DataOnly? d0 = null;
        int pos = 0;
        while (pos < old.Length)
        {
            var d = (Ed3DataOnly)DpduCodec.DecodeFirst(old.AsSpan(pos), PeerEdition.Ed3).Value!;
            d0 ??= d;
            segs.Add(d.Segment.Span.ToArray());
            pos += Enc(d).Length;
        }

        byte[] cpdu = Cat([.. segs]);
        if (cpdu.Length != 50 || d0!.Header.Eot != 20)
        {
            throw new InvalidOperationException("the template is not as described");
        }

        var only = new Ed3DataOnly(d0.Header, d0.Flags with { CPduEnd = true, TxWinUwe = true }, 0, new Octets(cpdu), true);
        if (Enc(only).Length != 68)
        {
            throw new InvalidOperationException("the D_PDU is not 68 bytes");
        }

        var config = (JsonObject)lines[0]["config"]!.DeepClone();
        config["dts"]!["max_segment_size"] = 100;
        config["dts"]!["max_transmission_ms"] = 5550;
        var b = new ScenarioBuilder(
            "dts-tx-ed3-first-segment-stretches",
            ["C-1042", "C-1155", "C-0551", "C-0554", "C-0870", "C-0878", "K-0094", "K-0095", "C-0190", "C-0730", "C-0104", "C-1043", "C-0367", "C-0055", "C-1154"],
            Source,
            "Towards a peer recorded Ed3 a segment is never shortened, so a first ARQ D_PDU too long for the configured transmission length still stretches the transmission to the whole blocks it needs.",
            config,
            "As dts-tx-no-align-ed3-peer-arq up to its data transmission, with dts.max_segment_size 100 (C-0503) and dts.max_transmission_ms 5550 (one block: 600 + 4800 + 150), so one 64-byte block carries 64 bytes. Everything before 76500 is unchanged: our accept needs one block. At 76500 the whole 50-byte C_PDU is one segment, an 18 + 50 = 68-byte ED3-DATA-ONLY (type 0, C-0190, C-0730), FSN 0, C_PDU START and END, TX WIN LWE and UWE, DELIVER IN ORDER. It does not fit the one block the limit allows. Towards a peer recorded Ed3 the segment may not be shortened (C-0554, C-0870; interp C-1155), and the limit may not block it (C-0551, C-0878), so the transmission is made the whole blocks it needs, 2 (interp C-1042), 600 + 9600 + 150 = 10350 ms; EOT ceil(9750 / 500) = 20 (C-0104, C-1043). The 60 spare bytes take no PADDING towards an Ed3 peer (C-0367, C-0055, C-1154) and there is no acknowledgement to repeat, so they are zero filled. dts.ack_wait/0.0.1.3 starts when the transmission ends at 86850; nothing else is queued. Towards a peer recorded Ed4 the segment would instead be cut to the one block (as in dts-tx-ed4-first-segment-cut-to-limit).");
        Copy(b, lines, at);
        b.ExpectTransmit(Hex(Enc(only)), 2, Interleaver.Long);
        b.TransmitDone(86850).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000);
        return b;
    }

    /// <summary>
    /// The block-align-ed4 template's data transmission at 108000: its first DATA-ONLY, and the 6 bytes of
    /// C_PCI and DATA S_PDU header that precede the U_PDU in its segment.
    /// </summary>
    private static (int At, byte[] Prefix, DataOnly D0) Ed4Template(List<JsonObject> lines)
    {
        int t = lines.FindIndex(l => l["t"]?.GetValue<long>() == 108000);
        int at = lines.FindIndex(t, l => l["out"]?.GetValue<string>() == "transmit");
        byte[] old = Convert.FromHexString(lines[at]["bytes"]!.GetValue<string>());
        var d0 = (DataOnly)DpduCodec.DecodeFirst(old, PeerEdition.Ed4).Value!;
        byte[] seg = d0.Segment.Span.ToArray();
        byte[] prefix = seg[..6];
        if (!seg.AsSpan(6).SequenceEqual("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQR"u8[..(seg.Length - 6)]))
        {
            throw new InvalidOperationException("the template is not as described");
        }

        return (at, prefix, d0);
    }

    private static void Copy(ScenarioBuilder b, List<JsonObject> lines, int at)
    {
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
    }

    private static List<JsonObject> Template(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "conformance", "corpus", "t2", "dts-tx", name);
        if (!File.Exists(path))
        {
            path = Path.Combine(FindRepoRoot(), "conformance", "corpus", "t2", "dts-tx", name);
        }

        return File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => (JsonObject)JsonNode.Parse(l)!).ToList();
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
