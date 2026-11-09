using M0LTE.S5066.Core.Dts;
using System.Text.Json.Nodes;

namespace M0LTE.S5066.Core.Tests.T2;

/// <summary>
/// Authoring aid for the P7-9c scenarios (corpus 0.33.0): the MAX-SPEED probe of interp C-1291 and the
/// derived first-data wait of interp B-0420 as revised. Each scenario is an existing trace with its
/// bursts, configuration and the EOWs or timer values the rows give changed by hand; the EOW rotation
/// is rebuilt by the rule of interp C-1260 and C-1286 and checked first against the template's own
/// transmission, so nothing is read back from the implementation. Set S5066_WRITE_P79C to a directory to
/// write the files; otherwise the test does nothing.
/// </summary>
public class P79cScenarios
{
    private const string Padding = "90ebe000006610210331f2";   // PADDING from 0.0.1.3 to 0.0.1.2, EOT 0, EOW 0
    private const string Errored = "90eb7000006f102103000b00000b0000000ae42b00005540000168656c6c6f5398c0d5";   // a type 7 to us whose data CRC fails
    private const int Approach = 0xB04;
    private const int Segment = 0xAFF;

    [Fact]
    public void Write()
    {
        string? dir = Environment.GetEnvironmentVariable("S5066_WRITE_P79C");
        if (dir is null)
        {
            return;
        }

        void Save(string area, string name, List<JsonObject> lines)
        {
            string path = Path.Combine(dir, area, name + ".jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Join("\n", lines.Select(l => l.ToJsonString())) + "\n");
        }

        SelfCheck();
        Save("drc", "rx-probe-after-clean-run", ProbeAfterCleanRun());
        Save("drc", "rx-clean-burst-holds-against-estimate", CleanBurstHolds());
        Save("drc", "rx-errored-burst-estimate-moves-down", ErroredBurstMovesDown());
        Save("drc", "rx-cap-bounds-probe", CapBoundsProbe());
        Save("cas", "first-data-wait-from-slowest-rung", FirstDataFromSlowestRung());
        Save("cas", "first-data-wait-floor-three", FirstDataFloorThree());
    }

    private static readonly string[] ProbeRows = ["C-1291", "C-1270", "C-1271", "C-1281", "C-0459", "C-0460", "C-0472", "C-0473", "C-0833", "C-0836", "C-0935", "C-0939", "C-0940", "C-1260", "C-1286"];

    private const string ProbeSource = "Ed4 C.6.5 and C.6.6, pp.C-71 to C-73; C.7.6.3, pp.C-120 to C-122; corpus-format.md 6.2 step 6";

    private const string FlowNote = " Flow as drc-rx-trial-after-clean-run, whose table this is with probe_run 3 added: conn 1 binds SAP 5; at 1000 to 3000 a burst holding a PADDING from 0.0.1.3 to us with CAPABILITY (Ed4 1), EOT 10, records 0.0.1.3 Ed4 (C-1261); the request at 1500 queues \"hello\" for 0.0.1.3; at 52000 one transmission (WN 2 Long) holds the type 7 and 20 PADDING D_PDUs, all to 0.0.1.3, whose 21 EOWs are the top priority of Table C-25 (MAX-SPEED, LOW-SPEED), then SENDER-APPROACH 0xB04, then the rotation MAX-SPEED, LOW-SPEED, MAX-SEGMENT 0xAFF from its start (interp C-1260, C-1286). Earlier bursts (burst_started, bytes_received 30 ms later, burst_ended 60 ms later) hold a PADDING with EOT 0 (C-0105), or a type 7 to us whose data CRC fails (a D_PDU error, C-0836). Codes: MAX-SPEED 0x8A0 + WN and LOW-SPEED 0x9A0 + WN at Long (C-0462, C-0469). Table positions: WN 2, 4, 6, 7 Long are 0 to 3; bulk thresholds on WN 4 Long -100, 0, 100, 150, on WN 6 Long -100, 0, 300, 500, on WN 7 Long -100, -50, 100, 400; latency on WN 4 Long -100, 100, 300, 400, on WN 6 Long -100, 200, 400, 600, on WN 7 Long -100, 0, 300, 600; smoothing weight 8, clean run 2, error limit 500 ppt, caps 2 doubling to 8. The probe (corpus-format.md 6.2 step 6, interp C-1291) is for MAX-SPEED only.";

    /// <summary>Burst inputs: (rung WN, estimate, bytes) at 100, 200, ...</summary>
    private static List<JsonObject> ProbeScenario(string id, string description, string note, (int Wn, int? Snr, string Bytes)[] bursts, (int Wn, int Snr) capability, int maxWn, int lowWn)
    {
        List<JsonObject> lines = Template("drc", "rx-trial-after-clean-run.jsonl");
        var header = (JsonObject)lines[0].DeepClone();
        header["scenario"] = id;
        header["rows"] = new JsonArray(ProbeRows.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        header["source"] = ProbeSource;
        header["description"] = description;
        header["config"]!["drc"]!["table"]!["probe_run"] = 3;
        header["note"] = "Table (corpus-format.md 6.1) as drc-rx-trial-after-clean-run with probe_run 3." + FlowNote + " " + note;

        var output = new List<JsonObject> { header };
        int first = lines.FindIndex(l => l["t"]?.GetValue<long>() == 100);
        int cap = lines.FindIndex(l => l["t"]?.GetValue<long>() == 1000);
        output.AddRange(lines.Skip(1).Take(first - 1).Select(l => (JsonObject)l.DeepClone()));
        for (int i = 0; i < bursts.Length; i++)
        {
            long t = 100L * (i + 1);
            output.Add(new JsonObject { ["t"] = t, ["in"] = "burst_started" });
            output.Add(new JsonObject { ["t"] = t + 30, ["in"] = "bytes_received", ["bytes"] = bursts[i].Bytes });
            output.Add(new JsonObject
            {
                ["t"] = t + 60, ["in"] = "burst_ended", ["reason"] = "eom", ["wn"] = bursts[i].Wn, ["interleaver"] = "long", ["k"] = 7,
                ["snr_tenth_db"] = bursts[i].Snr, ["blocks"] = 1,
            });
        }

        foreach (JsonObject l in lines.Skip(cap))
        {
            var c = (JsonObject)l.DeepClone();
            if (c["t"]?.GetValue<long>() == 3000 && (string?)c["in"] == "burst_ended")
            {
                c["wn"] = capability.Wn;
                c["snr_tenth_db"] = capability.Snr;
            }

            if ((string?)c["out"] == "transmit")
            {
                c["bytes"] = WithEows(c["bytes"]!.GetValue<string>(), 0x8A0 + maxWn, 0x9A0 + lowWn);
            }

            output.Add(c);
        }

        return output;
    }

    /// <summary>
    /// The transmission's D_PDUs to 0.0.1.3 with their EOWs set by rule: MAX-SPEED, LOW-SPEED (top
    /// priority, mixed), SENDER-APPROACH, then the rotation MAX-SPEED, LOW-SPEED, MAX-SEGMENT.
    /// </summary>
    private static string WithEows(string hex, int max, int low)
    {
        byte[] bytes = Convert.FromHexString(hex);
        var pdus = new List<Dpdu>();
        int off = 0;
        while (off < bytes.Length)
        {
            var r = DpduCodec.DecodeFirst(bytes.AsSpan(off), PeerEdition.Ed4);
            if (!r.IsSuccess)
            {
                break;
            }

            pdus.Add(r.Value!);
            off += r.Consumed;
        }

        var eows = new List<int> { max, low, Approach };
        int[] rotation = [max, low, Segment];
        for (int i = 0; eows.Count < pdus.Count; i++)
        {
            eows.Add(rotation[i % 3]);
        }

        byte[] rebuilt = pdus.Select((p, i) => p with { Header = p.Header with { Eow = eows[i] } }).SelectMany(p => DpduCodec.Encode(p)).Concat(bytes.Skip(off)).ToArray();
        if (rebuilt.Length != bytes.Length)
        {
            throw new InvalidOperationException("length changed");
        }

        return Convert.ToHexStringLower(rebuilt);
    }

    /// <summary>The rotation rule reproduces drc-rx-trial-after-clean-run's own transmission (0x8A6, 0x9A4).</summary>
    private static void SelfCheck()
    {
        string t = Template("drc", "rx-trial-after-clean-run.jsonl").Single(l => (string?)l["out"] == "transmit")["bytes"]!.GetValue<string>();
        if (WithEows(t, 0x8A6, 0x9A4) != t)
        {
            throw new InvalidOperationException("the EOW rule does not reproduce the template");
        }
    }

    private static List<JsonObject> ProbeAfterCleanRun() => ProbeScenario(
        "drc-rx-probe-after-clean-run",
        "With probe_run set, MAX-SPEED moves up one rung after that many clean bursts on its rung even when the estimate does not call for it; LOW-SPEED does not.",
        "At 100 a clean burst on WN 4 Long, estimate 50: S = 50; bulk target 1 (0; 100 is above), latency target 0 (100 is above); the first recommendation is the target, not above WN 4 Long's own position 1: MAX-SPEED WN 4 Long, LOW-SPEED WN 2 Long. WN 4 Long's clean run is 1. At 200 the same: S stays 50, the targets are unchanged and equal to the recommendations, so no trial; the clean run is 2, which is clean_run but short of probe_run 3, so MAX-SPEED holds. The CAPABILITY burst, clean, on WN 4 Long, estimate 50: the clean run of the MAX-SPEED rung is 3, the target (1) is not above MAX-SPEED (1) and position 1 is not the top, so MAX-SPEED moves up one rung, to WN 6 Long (interp C-1291); LOW-SPEED stays WN 2 Long, since the probe is for MAX-SPEED only. EOWs: 0x8A6, 0x9A2, 0xB04, then 0x8A6, 0x9A2, 0xAFF repeated, 21 in all. Without probe_run (drc-rx-trial-after-clean-run's table) MAX-SPEED would have stayed WN 4 Long.",
        [(4, 50, Padding), (4, 50, Padding)],
        (4, 50),
        6,
        2);

    private static List<JsonObject> CleanBurstHolds() => ProbeScenario(
        "drc-rx-clean-burst-holds-against-estimate",
        "With probe_run set, a clean burst whose estimate calls for a lower rung does not move MAX-SPEED down; LOW-SPEED still moves down at once.",
        "As drc-rx-estimate-drop-moves-down with probe_run 3. At 100 a clean burst on WN 7 Long, estimate 450: S = 450 on WN 7 Long; bulk target 3 (400), latency target 2 (300; 600 is above); the first recommendation is the target, not above position 3: MAX-SPEED WN 7 Long, LOW-SPEED WN 6 Long. The CAPABILITY burst, clean, on WN 7 Long, estimate -300: d = -750, q = (750 x 8) / 16 = 375 taken negative, S = 75; bulk target 1 (-50; 100 is above) and latency target 1 (0; 300 is above). The burst is clean (e = 0), so the estimate alone does not move MAX-SPEED down and it stays WN 7 Long (interp C-1291); it is already the top position, so no probe. LOW-SPEED keeps the P7-6 walk and moves down at once to WN 4 Long (interp C-1270). EOWs: 0x8A7, 0x9A4, 0xB04, then 0x8A7, 0x9A4, 0xAFF repeated, 21 in all. Without probe_run (drc-rx-estimate-drop-moves-down) MAX-SPEED moved down to WN 4 Long.",
        [(7, 450, Padding)],
        (7, -300),
        7,
        4);

    private static List<JsonObject> ErroredBurstMovesDown() => ProbeScenario(
        "drc-rx-errored-burst-estimate-moves-down",
        "With probe_run set, a burst with a D_PDU error below the error limit still lets its estimate move MAX-SPEED down at once.",
        "At 100 a clean burst on WN 7 Long, estimate 450: MAX-SPEED WN 7 Long, LOW-SPEED WN 6 Long (as drc-rx-clean-burst-holds-against-estimate). At 200 a burst on WN 7 Long, estimate -300, holds a PADDING and a type 7 whose data CRC fails: h = 2, e = 1, and 1 x 1000 is not above 500 x 2, so no cap, but the burst is not clean: WN 7 Long's clean run goes to 0. S = 75 (d = -750, q = 375), bulk target 1 and latency target 1: after an errored burst the estimate moves MAX-SPEED down at once (interp C-1291), to WN 4 Long, and LOW-SPEED to WN 4 Long (interp C-1270). The CAPABILITY burst, clean, on WN 4 Long, estimate 100: S = 100 on WN 4 Long; bulk target 2 (100) is above MAX-SPEED, but WN 4 Long's clean run is 1, short of clean_run 2, so no trial, and the probe needs a target not above the recommendation; MAX-SPEED holds. Latency target 1 (100; 300 is above) equals LOW-SPEED, which holds. EOWs: 0x8A4, 0x9A4, 0xB04, then 0x8A4, 0x9A4, 0xAFF repeated, 21 in all.",
        [(7, 450, Padding), (7, -300, Padding + Errored)],
        (4, 100),
        4,
        4);

    private static List<JsonObject> CapBoundsProbe() => ProbeScenario(
        "drc-rx-cap-bounds-probe",
        "A probed rung whose burst fails the error limit is capped, which brings MAX-SPEED back down and keeps a renewed probe below the cap until it lifts.",
        "At 100, 200 and 300 clean bursts on WN 4 Long, estimate 50: as drc-rx-probe-after-clean-run, MAX-SPEED WN 4 Long and LOW-SPEED WN 2 Long after the first; at 300 WN 4 Long's clean run is 3, the target 1 is not above MAX-SPEED, so MAX-SPEED probes up to WN 6 Long (interp C-1291). At 400 a burst on WN 6 Long, estimate 400, holds only a type 7 whose data CRC fails: h = 1, e = 1, 1000 is above 500, so WN 6 Long (position 2) fails for the first time and is capped for 2 bursts; its clean run is 0. S = 400 on WN 6 Long; bulk target 2 (300; 500 is above) equals MAX-SPEED, so no move by the estimate, but the ceiling is position 1 and MAX-SPEED comes down to WN 4 Long. Latency target 2 (200; 400 is above) is above LOW-SPEED (0), but WN 2 Long's clean run is 0: LOW-SPEED holds. The CAPABILITY burst, clean, on WN 4 Long, estimate 50: the cap counts down to 1 and stays; WN 4 Long's clean run is 4 (the failed burst was on WN 6 Long), the target 1 equals MAX-SPEED, so the probe moves it to position 2, and the ceiling brings it back to 1: MAX-SPEED stays WN 4 Long. LOW-SPEED target 0, holds. EOWs: 0x8A4, 0x9A2, 0xB04, then 0x8A4, 0x9A2, 0xAFF repeated, 21 in all.",
        [(4, 50, Padding), (4, 50, Padding), (4, 50, Padding), (6, 400, Errored)],
        (4, 50),
        4,
        2);

    private static readonly string[] FirstDataRows = ["B-0420", "B-0215", "B-0216", "B-0213", "B-0400", "A-0047", "A-0160", "B-0111", "C-1120", "C-0551", "C-1042", "C-1290", "K-0094", "A-0321", "C-1271", "C-1272", "C-1286", "C-1280", "C-1154"];

    private const string FirstDataSource = "Ed4 B.7.2.1 Step 2 (d) and (e), p.B-28; C.7.5.6, p.C-117";

    private const string PaddingOut = "90ebeaff0a66103102ad67";   // PADDING from 0.0.1.2 to 0.0.1.3, EOT 10, EOW MAX-SEGMENT 0xAFF (as in drc-rx-trial-after-clean-run)

    private static List<JsonObject> FirstData(string id, string description, string note, Action<JsonObject> configure, long firstData, long answerWait, int? fillWn = null)
    {
        List<JsonObject> lines = Template("cas", "make-called-first-data-timeout.jsonl");
        var header = (JsonObject)lines[0].DeepClone();
        header["scenario"] = id;
        header["rows"] = new JsonArray(FirstDataRows.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        header["source"] = FirstDataSource;
        header["description"] = description;
        var config = (JsonObject)header["config"]!;
        ((JsonObject)config["cas"]!).Remove("first_data_timeout_ms");
        config["drc"] = JsonNode.Parse("""
            {"table": {"ladder": [], "thresholds": [], "smoothing_weight": 8, "clean_run": 2, "rx_error_limit_ppt": 500,
                       "rx_ceiling_initial": 2, "rx_ceiling_cap": 8, "tx_step_down_waits": 2, "tx_ceiling_initial": 2, "tx_ceiling_cap": 8}}
            """);
        configure(config);
        header["note"] = note;

        var output = new List<JsonObject> { header };
        foreach (JsonObject l in lines.Skip(1))
        {
            var c = (JsonObject)l.DeepClone();
            if ((string?)c["out"] == "transmit")
            {
                byte[] bytes = Convert.FromHexString(c["bytes"]!.GetValue<string>());
                var r = DpduCodec.DecodeFirst(bytes, PeerEdition.Ed4);
                Dpdu p = r.Value!;
                if (r.Consumed != bytes.Length || p.Header.Eow != 0xF01)
                {
                    throw new InvalidOperationException("unexpected template transmission");
                }

                string accept = Convert.ToHexStringLower(DpduCodec.Encode(p with { Header = p.Header with { Eow = Approach } }));
                if (fillWn is int wn)
                {
                    accept += string.Concat(Enumerable.Repeat(PaddingOut, 21));   // 25 + 21 x 11 = 256 bytes, one block
                    c["wn"] = wn;
                }

                c["bytes"] = accept;
            }

            if ((string?)c["out"] == "timer_start")
            {
                string name = c["name"]!.GetValue<string>();
                c["ms"] = name.StartsWith("cas.first_data/", StringComparison.Ordinal) ? firstData
                    : name.StartsWith("sis.soft_link_max/", StringComparison.Ordinal) ? 6 * answerWait
                    : name.StartsWith("sis.soft_link_", StringComparison.Ordinal) ? 3 * answerWait
                    : c["ms"]!.GetValue<long>();
            }

            output.Add(c);
            if ((string?)c["out"] == "timer_start" && ((string)c["name"]!).StartsWith("sis.soft_link_idle/", StringComparison.Ordinal))
            {
                break;   // the run ends with the timers started: the value is the point, the expiry is drc-free (cas-make-called-first-data-timeout)
            }
        }

        return output;
    }

    private const string FirstDataFlow = "Flow as cas-make-called-first-data-timeout up to the link being made, with cas.first_data_timeout_ms unset and a drc.table, and the run ends once the link's timers have started. At 2000, from 0.0.1.3 (EOT 10), a type 8 D_PDU carrying a PHYSICAL LINK REQUEST with the Ed4 bit set, which records 0.0.1.3 Ed4 (B-0408); the CAS answers PHYSICAL LINK ACCEPTED, sent at 52000 after the contention wait of 45000 (seed 0, slot 15), at drc.default_speed WN 2 Long since 0.0.1.3 has sent no recommendation. The table's thresholds list is empty, so the burst at 1000 to 3000 gives no estimate and 0.0.1.3, heard and recorded Ed4, is owed MAX-SEGMENT alone, 0xAFF (value 255 to a sender that has sent no SENDER-APPROACH; interp C-1271, C-1272), and no top priority. With a calibrated table this node sends its SENDER-APPROACH, 0xB04, first in a transmission that carries data to the peer, here the type 8 D_PDU (interp C-1286), so the accept carries 0xB04 in place of the template's CAPABILITY 0xF01; that D_PDU differs only in its EOW and header CRC. The link is made at 57550 (B-0213, B-0400) and cas.first_data/0.0.1.3 starts (B-0215, B-0216), then the SIS soft link timers, unset and so derived from the answer wait: minimum hold and idle three answer waits, maximum six (A-0321). The derived first-data wait (interp B-0420, revised corpus 0.33.0; corpus-format.md section 6): n answer waits, n the larger of 3 and ceil(2054 / c), where 2054 bytes is the largest DATA C_PDU (1-octet C_PCI, B-0111; 5-octet S_PCI with a TTD, A-0160; a 2048-byte U_PDU at the MTU, A-0047) and c the bytes one transmission of the configured length carries on the ladder's slowest rung, ladder[0]: b = floor((min(127500, dts.max_transmission_ms) - preamble_ms - flush_ms) / block_ms) blocks, at least 1, so c = b x block_bytes - modem.eom_bytes (C-0551, C-1042, C-1290). The answer wait is the derived dts.ack_wait (C-1120): min(127500, dts.max_transmission_ms) + max(lbt_wait_dcd_ms, lbt_wait_vdcd_ms) + num_cont_slots x cont_slot_width_ms.";

    private static List<JsonObject> FirstDataFromSlowestRung() => FirstData(
        "cas-first-data-wait-from-slowest-rung",
        "With cas.first_data_timeout_ms unset, the Called node's first-data wait covers the transmissions one largest DATA C_PDU needs on the slowest ladder rung, one answer wait each, when that is more than three.",
        FirstDataFlow + " Here dts.max_transmission_ms is 30000 and the ladder is WN 1 Long then WN 2 Long, with a fixture entry for WN 1 Long of 50 bytes and 9600 ms a block. The answer wait is 30000 + 30000 + 16 x 3000 = 108000. On WN 1 Long b = floor((30000 - 600 - 150) / 9600) = 3, c = 3 x 50 - 0 = 150, and ceil(2054 / 150) = 14 (13 x 150 = 1950 is short), so cas.first_data/0.0.1.3 starts with 14 x 108000 = 1512000 (before corpus 0.33.0, three answer waits, 324000). The soft link minimum hold and idle start with 324000 and the maximum with 648000. The accept transmission lasts 5550 ms, inside the 30000 limit, so nothing before the link is made changes.",
        config =>
        {
            config["dts"] = new JsonObject { ["max_transmission_ms"] = 30000 };
            config["modem"]!["waveforms"]!.AsArray().Add(JsonNode.Parse("""{"wn":1,"interleaver":"long","block_bytes":50,"block_ms":9600}"""));
            config["drc"]!["table"]!["ladder"] = JsonNode.Parse("""[{"wn":1,"interleaver":"long"},{"wn":2,"interleaver":"long"}]""");
        },
        1512000,
        108000);

    private static List<JsonObject> FirstDataFloorThree() => FirstData(
        "cas-first-data-wait-floor-three",
        "With cas.first_data_timeout_ms unset, the Called node's first-data wait stays three answer waits when the slowest ladder rung delivers the largest DATA C_PDU in fewer transmissions.",
        FirstDataFlow + " Here dts.max_transmission_ms is the default 127500 and the ladder is WN 4 Long then WN 6 Long, with a fixture entry for WN 4 Long of 256 bytes and 4800 ms a block. The answer wait is 127500 + 30000 + 16 x 3000 = 205500. On WN 4 Long b = floor((127500 - 600 - 150) / 4800) = 26, c = 26 x 256 = 6656, and ceil(2054 / 6656) = 1, so n = 3 and cas.first_data/0.0.1.3 starts with 3 x 205500 = 616500, as with an empty table. The soft link minimum hold and idle start with 616500 and the maximum with 1233000, as in the template. drc.default_speed, WN 2 Long, is not on this ladder, so the accept goes at the lowest ladder rung, WN 4 Long (interp C-1280: no ladder rung has a data rate not above 600 bit/s; the link is made at that rung, interp C-1281), still one block, 5550 ms and EOT 10 (C-0104, C-1043). Its 256-byte block takes the 25-byte accept and is filled with 21 PADDING D_PDUs of 11 bytes to the peer recorded Ed4, EOT 10, each carrying the next entry of the repeating set, MAX-SEGMENT 0xAFF (C-1154, interp C-1260).",
        config =>
        {
            config["modem"]!["waveforms"]!.AsArray().Add(JsonNode.Parse("""{"wn":4,"interleaver":"long","block_bytes":256,"block_ms":4800}"""));
            config["drc"]!["table"]!["ladder"] = JsonNode.Parse("""[{"wn":4,"interleaver":"long"},{"wn":6,"interleaver":"long"}]""");
        },
        616500,
        205500,
        4);

    private static List<JsonObject> Template(string area, string name)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "conformance", "corpus")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        string path = Path.Combine(dir ?? throw new InvalidOperationException("conformance submodule not found"), "conformance", "corpus", "t2", area, name);
        return File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => (JsonObject)JsonNode.Parse(l)!).ToList();
    }
}
