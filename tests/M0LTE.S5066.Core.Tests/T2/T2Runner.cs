using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Core.Tests.T2;

/// <summary>A T2 scenario failed, with one of the five reason words of harness-contract.md section 2.</summary>
internal sealed class T2FailureException(string reason, string where, string detail)
    : Exception($"{reason} at {where}: {detail}")
{
    public string Reason { get; } = reason;
}

/// <summary>The outcome of one scenario: its id, unsupported reason if any, and the transcript of what the core did.</summary>
/// <param name="Id">The scenario id from the header.</param>
/// <param name="Unsupported">Why the scenario was not run, or null.</param>
/// <param name="Transcript">Each input as fed (normalised JSON) followed by the core's actual outputs, one JSON text per line.</param>
internal sealed record T2Result(string Id, string? Unsupported, IReadOnlyList<string> Transcript);

/// <summary>
/// Runs one T2 scenario as harness-contract.md section 2 says (core-architecture.md section 8):
/// merge the header config over defaults.json, build the node with a fixture modem, keep the
/// harness's own timer table from the outputs, and for each input check time and expiry, feed it,
/// and compare the outputs with the lines that follow. Stops at the first failure.
/// </summary>
internal static class T2Runner
{
    private static readonly string[] HeaderKeys = ["scenario", "rows", "source", "description", "config", "note"];

    /// <summary>Runs a scenario file's lines. Throws <see cref="T2FailureException"/> on a failure.</summary>
    /// <param name="file">The file name, for failure reports.</param>
    /// <param name="lines">The JSON lines of the file.</param>
    /// <param name="defaults">The parsed defaults.json.</param>
    public static T2Result Run(string file, IReadOnlyList<string> lines, JsonObject defaults)
    {
        var transcript = new List<string>();
        JsonObject header = JsonNode.Parse(lines[0])!.AsObject();
        string id = TraceJson.Str(header, "scenario");
        foreach (KeyValuePair<string, JsonNode?> kv in header)
        {
            if (!HeaderKeys.Contains(kv.Key, StringComparer.Ordinal))
            {
                return new T2Result(id, $"unknown header key '{kv.Key}'", transcript);
            }
        }

        // Unsupported is decided from the whole file before anything runs, so a scenario with an
        // unknown input, output or key anywhere is unsupported, never a failure at an earlier line,
        // whatever the core does (harness-contract.md 1).
        for (int k = 1; k < lines.Count; k++)
        {
            JsonObject line = JsonNode.Parse(lines[k])!.AsObject();
            try
            {
                if (line.ContainsKey("in"))
                {
                    TraceJson.InputClass(line);
                }
                else
                {
                    TraceJson.CheckOutput(line);
                }
            }
            catch (UnsupportedTraceException ex)
            {
                return new T2Result(id, ex.Message, transcript);
            }
        }

        // Step 1: configuration.
        JsonObject config = ConfigMerge.Merge(defaults, header["config"]!.AsObject());
        if (ConfigReader.UnknownKey(config) is { } unknown)
        {
            return new T2Result(id, $"unknown setting '{unknown}'", transcript);
        }

        string where = $"{file}:1";
        NodeConfig nodeConfig;
        ulong seed;
        try
        {
            nodeConfig = ConfigReader.Read(config);
            seed = ConfigReader.ReadSeed(config);
        }
        catch (MissingSettingException ex)
        {
            throw new T2FailureException("missing_setting", where, ex.Message);
        }

        // Step 2: the node and its fake modem.
        var node = new Node(nodeConfig, seed, new FixtureModem(nodeConfig.Modem));

        // Step 3: the harness's own timer table, from the outputs.
        var timers = new SortedDictionary<string, long>(StringComparer.Ordinal);
        long lastT = -1;

        // Step 4: inputs in order.
        int i = 1;
        while (i < lines.Count)
        {
            int lineNo = i + 1;
            where = $"{file}:{lineNo}";
            JsonObject step = JsonNode.Parse(lines[i])!.AsObject();
            if (!step.ContainsKey("in"))
            {
                throw new InvalidDataException($"{where}: an output line with no input before it");
            }

            int cls = TraceJson.InputClass(step);

            // Expected outputs: the out lines up to the next input.
            var expected = new List<(int Line, JsonObject Out)>();
            int j = i + 1;
            for (; j < lines.Count; j++)
            {
                JsonObject o = JsonNode.Parse(lines[j])!.AsObject();
                if (o.ContainsKey("in"))
                {
                    break;
                }

                expected.Add((j + 1, o));
            }

            long t = TraceJson.Long(step, "t");
            CheckTime(where, t, lastT, cls, timers);
            lastT = t;
            string kind = TraceJson.Str(step, "in");

            CoreInput input;
            if (kind == "config")
            {
                config = ConfigMerge.Merge(config, step["set"]!.AsObject());
                try
                {
                    input = new ConfigInput(t, ConfigReader.Read(config));
                }
                catch (MissingSettingException ex)
                {
                    throw new T2FailureException("missing_setting", where, ex.Message);
                }
            }
            else
            {
                // The expiry is checked against the harness's table before the name is parsed, so
                // a name the core never started is a bad_expiry, not a parse error.
                if (kind == "timer_expired")
                {
                    string name = TraceJson.Str(step, "name");
                    if (!timers.TryGetValue(name, out long deadline) || deadline != t)
                    {
                        throw new T2FailureException("bad_expiry", where,
                            $"timer '{name}' is not running with deadline {t}" + (timers.ContainsKey(name) ? $" (deadline {deadline})" : ""));
                    }

                    timers.Remove(name);
                }

                input = TraceJson.ToInput(step);
            }

            transcript.Add(TraceJson.Normalise(step).ToJsonString());
            List<CoreOutput> actual;
            try
            {
                node.Handle(input);
                actual = node.DrainOutputs();
            }
            catch (MissingSettingException ex)
            {
                throw new T2FailureException("missing_setting", where, ex.Message);
            }
            catch (UnplannedWaveformException ex)
            {
                throw new T2FailureException("unplanned_waveform", where, ex.Message);
            }

            var actualJson = actual.Select(TraceJson.ToJson).ToList();
            foreach (JsonObject a in actualJson)
            {
                transcript.Add(a.ToJsonString());
                string kindOut = TraceJson.Str(a, "out");
                if (kindOut == "timer_start")
                {
                    timers[TraceJson.Str(a, "name")] = t + TraceJson.Long(a, "ms");
                }
                else if (kindOut == "timer_cancel")
                {
                    timers.Remove(TraceJson.Str(a, "name"));
                }
            }

            Compare(file, where, expected, actualJson);
            i = j;
        }

        return new T2Result(id, null, transcript);
    }

    /// <summary>
    /// Time checks of harness-contract.md 2 step 4: t does not go back; no running timer has a
    /// deadline before t; for a modem or SIS input, none has a deadline equal to t.
    /// </summary>
    private static void CheckTime(string where, long t, long lastT, int cls, SortedDictionary<string, long> timers)
    {
        if (t < lastT)
        {
            throw new T2FailureException("missed_timer", where, $"t {t} is earlier than the previous input's {lastT}");
        }

        foreach (KeyValuePair<string, long> kv in timers)
        {
            if (kv.Value < t || (kv.Value == t && cls >= 2))
            {
                throw new T2FailureException("missed_timer", where, $"timer '{kv.Key}' was due at {kv.Value}, before this input at {t}");
            }
        }
    }

    private static void Compare(string file, string where, List<(int Line, JsonObject Out)> expected, List<JsonObject> actual)
    {
        int n = Math.Min(expected.Count, actual.Count);
        for (int k = 0; k < n; k++)
        {
            JsonNode e = TraceJson.Normalise(expected[k].Out);
            JsonNode a = TraceJson.Normalise(actual[k]);
            if (!JsonNode.DeepEquals(e, a))
            {
                throw new T2FailureException("output_mismatch", $"{file}:{expected[k].Line}",
                    $"expected {e.ToJsonString()} but the core produced {a.ToJsonString()}");
            }
        }

        if (expected.Count > n)
        {
            throw new T2FailureException("output_mismatch", $"{file}:{expected[n].Line}",
                $"expected {TraceJson.Normalise(expected[n].Out).ToJsonString()} but the core produced no more outputs for the input at {where}");
        }

        if (actual.Count > n)
        {
            throw new T2FailureException("output_mismatch", where,
                $"the core produced an unexpected output {TraceJson.Normalise(actual[n]).ToJsonString()} (output {n + 1} for this input)");
        }
    }
}
