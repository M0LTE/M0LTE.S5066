using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Core.Tests.T2;

/// <summary>
/// Authoring aid for P7-6: turns a skeleton (a header and the modem and SIS inputs only) into a full
/// trace, inserting each timer expiry at its deadline and recording the outputs of the existing core.
/// Used only for the timing that earlier, already conformance-tested items fix (MAC, CAS, SIS); the
/// rate selection content of every recorded D_PDU (its EOW) is checked against values derived by
/// hand from the rules, by a separate script, before a recorded trace goes into the corpus. A
/// skeleton line <c>{"t": T, "in": "end"}</c> fires the timers due before T and stops.
/// </summary>
internal static class TraceRecorder
{
    public static List<string> Record(IReadOnlyList<string> skeleton, JsonObject defaults)
    {
        var lines = new List<string> { skeleton[0] };
        JsonObject header = JsonNode.Parse(skeleton[0])!.AsObject();
        JsonObject config = ConfigMerge.Merge(defaults, header["config"]!.AsObject());
        NodeConfig nc = ConfigReader.Read(config);
        var node = new Node(nc, ConfigReader.ReadSeed(config), new FixtureModem(nc.Modem));
        var timers = new List<(string Name, long Deadline, long Seq)>();
        long seq = 0;
        long? transmitDone = null;

        void Feed(JsonObject step, CoreInput input)
        {
            lines.Add(step.ToJsonString());
            node.Handle(input);
            foreach (CoreOutput o in node.DrainOutputs())
            {
                JsonObject j = TraceJson.ToJson(o);
                lines.Add(j.ToJsonString());
                string kind = TraceJson.Str(j, "out");
                if (kind == "transmit")
                {
                    ModemWaveform w = nc.Modem.Waveforms.Single(x => x.Wn == (int)TraceJson.Long(j, "wn")
                        && InterleaverNames.ToName(x.Interleaver) == TraceJson.Str(j, "interleaver"));
                    int n = TraceJson.Str(j, "bytes").Length / 2;
                    long blocks = Math.Max(1, (n + w.BlockBytes - 1) / w.BlockBytes);
                    transmitDone = input.T + nc.Modem.PreambleMs!.Value + (blocks * w.BlockMs) + nc.Modem.FlushMs!.Value;
                }

                if (kind is "timer_start" or "timer_cancel")
                {
                    string name = TraceJson.Str(j, "name");
                    timers.RemoveAll(x => x.Name == name);
                    if (kind == "timer_start")
                    {
                        timers.Add((name, input.T + TraceJson.Long(j, "ms"), seq++));
                    }
                }
            }
        }

        void FireBefore(long t, bool inclusive)
        {
            while (true)
            {
                var due = timers.Where(x => x.Deadline < t || (inclusive && x.Deadline == t))
                    .OrderBy(x => x.Deadline).ThenBy(x => x.Seq).ToList();
                if (due.Count == 0 || (transmitDone is long td && td < due[0].Deadline))
                {
                    if (transmitDone is long d && (d < t || (inclusive && d == t)) && (due.Count == 0 || d < due[0].Deadline))
                    {
                        transmitDone = null;
                        var done = new JsonObject { ["t"] = d, ["in"] = "transmit_done" };
                        Feed(done, TraceJson.ToInput(done));
                        continue;
                    }

                    if (due.Count == 0)
                    {
                        return;
                    }
                }

                (string name, long deadline, _) = due[0];
                timers.RemoveAll(x => x.Name == name);
                var step = new JsonObject { ["t"] = deadline, ["in"] = "timer_expired", ["name"] = name };
                Feed(step, TraceJson.ToInput(step));
            }
        }

        for (int i = 1; i < skeleton.Count; i++)
        {
            JsonObject step = JsonNode.Parse(skeleton[i])!.AsObject();
            long t = TraceJson.Long(step, "t");
            if (TraceJson.Str(step, "in") == "end")
            {
                FireBefore(t, false);
                break;
            }

            FireBefore(t, true);
            Feed(step, TraceJson.ToInput(step));
        }

        return lines;
    }
}

/// <summary>Runs <see cref="TraceRecorder"/> over a directory of skeletons when S5066_RECORD_DIR is set.</summary>
public sealed class TraceRecorderRun
{
    [Fact]
    public void Record_skeletons()
    {
        string? dir = Environment.GetEnvironmentVariable("S5066_RECORD_DIR");
        if (dir is null)
        {
            return;
        }

        foreach (string f in Directory.GetFiles(dir, "*.skel.jsonl").Order(StringComparer.Ordinal))
        {
            List<string> lines = TraceRecorder.Record(File.ReadAllLines(f).Where(l => l.Length > 0).ToList(), Corpus.T2Defaults);
            File.WriteAllLines(f.Replace(".skel.jsonl", ".jsonl", StringComparison.Ordinal), lines);
        }
    }
}
