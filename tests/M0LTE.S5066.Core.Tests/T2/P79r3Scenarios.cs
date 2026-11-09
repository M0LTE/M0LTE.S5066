using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;

namespace M0LTE.S5066.Core.Tests.T2;

/// <summary>
/// Authoring aid for the P7-9 round 3 receiver scenarios (corpus 0.36.0): the estimate walk goes straight to
/// its target after the clean run (ruling 1), each recommendation walks only its own ladder positions,
/// bulk_rungs or latency_rungs (ruling 2, open question 15), and a burst with a lost block is not clean
/// (ruling 6). Built as the P7-9c scenarios: the template's bursts and table changed by hand and the EOWs of
/// its one transmission rebuilt by the rule of interp C-1260 and C-1286, checked first against the template's
/// own transmission. Set S5066_WRITE_P79R3 to a directory to write the files; otherwise the test does nothing.
/// </summary>
public partial class P79cScenarios
{
    private const string Garbage = "0102030405060708090a0b0c0d0e0f10";   // a block with no sync and not all zero: D_PDUs whose headers were lost

    [Fact]
    public void WriteRound3()
    {
        string? dir = Environment.GetEnvironmentVariable("S5066_WRITE_P79R3");
        if (dir is null)
        {
            return;
        }

        void Save(string name, List<JsonObject> lines)
        {
            string path = Path.Combine(dir, "drc", name + ".jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Join("\n", lines.Select(l => l.ToJsonString())) + "\n");
        }

        SelfCheck();
        Save("rx-trial-after-clean-run", TrialStraightToTarget());
        Save("rx-probe-skips-rungs-off-the-walk", ProbeSkipsOffWalk());
        Save("rx-latency-walk-rounds-down", LatencyWalkRoundsDown());
        Save("rx-lost-block-not-clean", LostBlockNotClean());
    }

    private static readonly string[] Round3Rows = ["C-1270", "C-1291", "C-1271", "C-1281", "C-0459", "C-0460", "C-0472", "C-0473", "C-0833", "C-0836", "C-0935", "C-0939", "C-0940", "C-1260", "C-1286"];

    private const string Round3Flow = " Flow as drc-rx-capability-records-ed4: conn 1 binds SAP 5; at 1000 to 3000 a burst holding a PADDING from 0.0.1.3 to us with CAPABILITY (Ed4 1), EOT 10, records 0.0.1.3 Ed4 (C-1261); the request at 1500 queues \"hello\" for 0.0.1.3; at 52000 one transmission (WN 2 Long, block 256 bytes) holds the type 7 and 20 PADDING D_PDUs, all to 0.0.1.3, whose 21 EOWs are the top priority of Table C-25 (MAX-SPEED, LOW-SPEED), then SENDER-APPROACH 0xB04, then the rotation MAX-SPEED, LOW-SPEED, MAX-SEGMENT 0xAFF from its start (interp C-1260, C-1286). Earlier bursts (burst_started, bytes_received 30 ms later and, for a second block, 40 ms later, burst_ended 60 ms later) hold a PADDING with EOT 0 (C-0105), or a type 7 to us whose data CRC fails (a D_PDU error, C-0836), or a block of 16 bytes with no sync that are not all zero. Codes: MAX-SPEED 0x8A0 + WN and LOW-SPEED 0x9A0 + WN at Long (C-0462, C-0469). Table positions: WN 2, 4, 6, 7 Long are 0 to 3; thresholds on WN 2 Long bulk -100, 50, 150, 300 and latency -100, 100, 200, 400; on WN 4 Long bulk -100, 0, 100, 150 and latency -100, 100, 300, 400; on WN 6 Long bulk -100, 0, 300, 500 and latency -100, 200, 400, 600; on WN 7 Long bulk -100, -50, 100, 400 and latency -100, 0, 300, 600; smoothing weight 8, clean run 2, error limit 500 ppt, caps 2 doubling to 8. Since corpus 0.36.0 (P7-9 round 3) a move up by the estimate goes straight to its target after the clean run, not one rung (interp C-1270), each recommendation walks only the ladder positions bulk_rungs or latency_rungs give (all when null), and a burst with a lost block is not clean (corpus-format.md 6.2).";

    /// <summary>A receiver scenario on drc-rx-trial-after-clean-run's flow: bursts at 100, 200, ..., each a list of blocks.</summary>
    private static List<JsonObject> Round3Scenario(string id, string description, string note, Action<JsonObject> table, (int Wn, int? Snr, string[] Blocks)[] bursts, (int Wn, int Snr) capability, int maxWn, int lowWn)
    {
        List<JsonObject> lines = Template("drc", "rx-trial-after-clean-run.jsonl");
        var header = (JsonObject)lines[0].DeepClone();
        header["scenario"] = id;
        header["rows"] = new JsonArray(Round3Rows.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        header["source"] = ProbeSource;
        header["description"] = description;
        table((JsonObject)header["config"]!["drc"]!["table"]!);
        header["note"] = note;

        var output = new List<JsonObject> { header };
        int first = lines.FindIndex(l => l["t"]?.GetValue<long>() == 100);
        int cap = lines.FindIndex(l => l["t"]?.GetValue<long>() == 1000);
        output.AddRange(lines.Skip(1).Take(first - 1).Select(l => (JsonObject)l.DeepClone()));
        for (int i = 0; i < bursts.Length; i++)
        {
            long t = 100L * (i + 1);
            output.Add(new JsonObject { ["t"] = t, ["in"] = "burst_started" });
            for (int k = 0; k < bursts[i].Blocks.Length; k++)
            {
                output.Add(new JsonObject { ["t"] = t + 30 + (10 * k), ["in"] = "bytes_received", ["bytes"] = bursts[i].Blocks[k] });
            }

            output.Add(new JsonObject
            {
                ["t"] = t + 60, ["in"] = "burst_ended", ["reason"] = "eom", ["wn"] = bursts[i].Wn, ["interleaver"] = "long", ["k"] = 7,
                ["snr_tenth_db"] = bursts[i].Snr, ["blocks"] = bursts[i].Blocks.Length,
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

    private static readonly Action<JsonObject> NoChange = _ => { };

    private static Action<JsonObject> Walks(int[] bulk, int[] latency) => t =>
    {
        t["bulk_rungs"] = new JsonArray(bulk.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
        t["latency_rungs"] = new JsonArray(latency.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
    };

    private static List<JsonObject> TrialStraightToTarget()
    {
        List<JsonObject> lines = Template("drc", "rx-trial-after-clean-run.jsonl");
        var header = lines[0];
        header["description"] = "A recommendation moves up only after the table's run of clean bursts on the current rung, and then straight to the target the estimate gives.";
        string note = header["note"]!.GetValue<string>();
        const string Old = "The CAPABILITY burst, clean, on WN 4 Long, estimate 200: S stays 200, the clean run is 2, so MAX-SPEED moves up one rung to WN 6 Long (position 2), not to the target 3 (interp C-1270). Mixed: 0x8A6, 0x9A4, then 0x8A6, 0x9A4, 0xAFF repeated.";
        if (!note.Contains(Old, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("template note changed");
        }

        header["note"] = note.Replace(Old, "The CAPABILITY burst, clean, on WN 4 Long, estimate 200: S stays 200, the clean run is 2, so MAX-SPEED moves up straight to the target, WN 7 Long (position 3; interp C-1270 as revised in corpus 0.36.0, P7-9 round 3: before, one rung, to WN 6 Long). Mixed: 0x8A7, 0x9A4, then 0x8A7, 0x9A4, 0xAFF repeated.", StringComparison.Ordinal)
            + " Corpus 0.36.0 re-derived the EOWs of the transmission by rule for the move straight to the target (M0LTE.S5066 tests T2/P79r3Scenarios.cs): 0x8A6 became 0x8A7 in each MAX-SPEED header, with its header CRC.";
        foreach (JsonObject l in lines.Where(l => (string?)l["out"] == "transmit"))
        {
            l["bytes"] = WithEows(l["bytes"]!.GetValue<string>(), 0x8A7, 0x9A4);
        }

        return lines;
    }

    private static List<JsonObject> ProbeSkipsOffWalk() => Round3Scenario(
        "drc-rx-probe-skips-rungs-off-the-walk",
        "The MAX-SPEED probe moves to the next position of the bulk walk, skipping a ladder rung that is only on the latency walk.",
        "Table (corpus-format.md 6.1) as drc-rx-trial-after-clean-run with probe_run 3, bulk_rungs [0, 1, 3] and latency_rungs [0, 2, 3]: WN 6 Long (position 2) is on the ladder for latency only." + Round3Flow + " At 100 a clean burst on WN 4 Long, estimate 50: S = 50; on the bulk walk the target is 1 (0; 150 at position 3 is above), on the latency walk 0 (300 at position 2 is above); the first recommendation is the target, not above WN 4 Long's position 1: MAX-SPEED WN 4 Long, LOW-SPEED WN 2 Long. At 200 the same; WN 4 Long's clean run is 2, the targets equal the recommendations, and 2 is short of probe_run 3. The CAPABILITY burst, clean, on WN 4 Long, estimate 50: the clean run is 3, the bulk target (1) is not above MAX-SPEED (1), so the probe moves MAX-SPEED to the next position of the bulk walk, 3, WN 7 Long (interp C-1291 as revised in corpus 0.36.0; with every position on the walk it went to WN 6 Long, drc-rx-probe-after-clean-run). LOW-SPEED holds at WN 2 Long. EOWs: 0x8A7, 0x9A2, 0xB04, then 0x8A7, 0x9A2, 0xAFF repeated, 21 in all.",
        t =>
        {
            t["probe_run"] = 3;
            Walks([0, 1, 3], [0, 2, 3])(t);
        },
        [(4, 50, [Padding]), (4, 50, [Padding])],
        (4, 50),
        7,
        2);

    private static List<JsonObject> LatencyWalkRoundsDown() => Round3Scenario(
        "drc-rx-latency-walk-rounds-down",
        "Each recommendation takes only positions of its own walk: the first LOW-SPEED is the highest latency rung not above the burst's rung, and the estimate's target is the highest walk rung it reaches.",
        "Table (corpus-format.md 6.1) as drc-rx-trial-after-clean-run with bulk_rungs [0, 1, 3] and latency_rungs [0, 2, 3]: WN 4 Long (position 1) is on the ladder for bulk only and WN 6 Long (position 2) for latency only; probe_run null." + Round3Flow + " At 100 a burst on WN 6 Long, estimate 250: S = 250 on WN 6 Long. Bulk: of positions 0, 1 and 3, the highest whose threshold (-100, 0, 500) is not above 250 is 1, which is below WN 6 Long's position 2: MAX-SPEED WN 4 Long. Latency: of positions 0, 2 and 3 (-100, 400, 600) only 0: LOW-SPEED WN 2 Long (with every position on the walk the target was 1, WN 4 Long, as in drc-rx-trial-after-clean-run). At 200 a clean burst on WN 4 Long, estimate 200: S = 200 on WN 4 Long; bulk target 3 (150), but WN 4 Long's clean run is 1, short of clean_run 2: MAX-SPEED holds; latency target 0 (300 at position 2 is above): LOW-SPEED holds. The CAPABILITY burst, clean, on WN 4 Long, estimate 200: the clean run is 2, so MAX-SPEED moves straight to the target 3, WN 7 Long (interp C-1270 as revised in corpus 0.36.0); LOW-SPEED holds at WN 2 Long. EOWs: 0x8A7, 0x9A2, 0xB04, then 0x8A7, 0x9A2, 0xAFF repeated, 21 in all.",
        Walks([0, 1, 3], [0, 2, 3]),
        [(6, 250, [Padding]), (4, 200, [Padding])],
        (4, 200),
        7,
        2);

    private static List<JsonObject> LostBlockNotClean() => Round3Scenario(
        "drc-rx-lost-block-not-clean",
        "A burst with a block that holds no byte of a valid D_PDU and is not all zero is not clean, though no data CRC failed: it ends the clean run, so no probe follows.",
        "Table (corpus-format.md 6.1) as drc-rx-trial-after-clean-run with probe_run 3." + Round3Flow + " At 100 a clean burst on WN 4 Long, estimate 50: MAX-SPEED WN 4 Long, LOW-SPEED WN 2 Long (as drc-rx-probe-after-clean-run); WN 4 Long's clean run is 1. At 200 a burst on WN 4 Long, estimate 50, of two blocks: 16 bytes with no sync that are not all zero (the D_PDUs in it lost their headers), then a PADDING. No D_PDU has a data part checked by a CRC-32, so h = 0 and e = 0: no cap; but the first block holds no byte of a D_PDU with a valid header, a lost block, so the burst is not clean (corpus-format.md 6.2 as revised in corpus 0.36.0, P7-9 round 3): WN 4 Long's clean run goes to 0 and its failure count is not reset. The targets equal the recommendations. The CAPABILITY burst, clean, on WN 4 Long, estimate 50: the clean run is 1, short of probe_run 3, so MAX-SPEED holds at WN 4 Long; LOW-SPEED holds at WN 2 Long. EOWs: 0x8A4, 0x9A2, 0xB04, then 0x8A4, 0x9A2, 0xAFF repeated, 21 in all. Before corpus 0.36.0 the burst at 200 was clean, the clean run reached 3 at the CAPABILITY burst and MAX-SPEED probed up to WN 6 Long (0x8A6), as in drc-rx-probe-after-clean-run.",
        t => t["probe_run"] = 3,
        [(4, 50, [Padding]), (4, 50, [Garbage, Padding])],
        (4, 50),
        4,
        2);
}
