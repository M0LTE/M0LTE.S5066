using System.Text.Json.Nodes;

namespace M0LTE.S5066.Core.Tests.T2;

/// <summary>
/// Authoring aid for the P7-9 round 2 receiver scenario (corpus 0.35.0): the burst error rate of corpus-format.md 6.2
/// counts only D_PDUs whose data part a CRC-32 checks (interp C-1270 as revised), so PADDING and other header-only
/// D_PDUs no longer dilute it. Built as the P7-9c scenarios. Set S5066_WRITE_P79E to a directory to write the file.
/// </summary>
public partial class P79cScenarios
{
    [Fact]
    public void WriteRound2()
    {
        string? dir = Environment.GetEnvironmentVariable("S5066_WRITE_P79E");
        if (dir is null)
        {
            return;
        }

        SelfCheck();
        List<JsonObject> lines = PaddingDoesNotDilute();
        string path = Path.Combine(dir, "drc", "rx-padding-does-not-dilute-errors.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines.Select(l => l.ToJsonString())) + "\n");
    }

    private static List<JsonObject> PaddingDoesNotDilute() => ProbeScenario(
        "drc-rx-padding-does-not-dilute-errors",
        "The burst error rate that caps a rung counts only D_PDUs with a data part checked by a CRC-32: PADDING in the burst does not dilute a failed segment.",
        "At 100, 200 and 300 clean bursts on WN 4 Long, estimate 50: as drc-rx-cap-bounds-probe, MAX-SPEED WN 4 Long and LOW-SPEED WN 2 Long after the first, and at 300 MAX-SPEED probes up to WN 6 Long (interp C-1291). At 400 a burst on WN 6 Long, estimate 400, holds a PADDING and a type 7 whose data CRC fails. The PADDING has a valid header but no data part, so it does not count: h = 1, e = 1, and 1000 is above 500, so WN 6 Long (position 2) fails for the first time and is capped for 2 bursts (corpus-format.md 6.2 step 2, interp C-1270 as revised in corpus 0.35.0); its clean run is 0. S = 400 on WN 6 Long; bulk target 2 (300; 500 is above) equals MAX-SPEED, but the ceiling is position 1, so MAX-SPEED comes down to WN 4 Long. LOW-SPEED holds at WN 2 Long (WN 2 Long's clean run is 0). The CAPABILITY burst, clean, on WN 4 Long, estimate 50: the cap counts down to 1 and stays; the probe would take MAX-SPEED to position 2 and the ceiling brings it back to 1: MAX-SPEED WN 4 Long. EOWs: 0x8A4, 0x9A2, 0xB04, then 0x8A4, 0x9A2, 0xAFF repeated, 21 in all. Before corpus 0.35.0 every valid header counted (h = 2, and 1000 is not above 1000): no cap, the probe held MAX-SPEED at WN 6 Long after the errored burst (target 2), and the transmission carried 0x8A6.",
        [(4, 50, Padding), (4, 50, Padding), (4, 50, Padding), (6, 400, Padding + Errored)],
        (4, 50),
        4,
        2);
}
