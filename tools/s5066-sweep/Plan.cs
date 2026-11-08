using System.Globalization;
using System.Text.Json.Nodes;
using M0LTE.S5066.Host;

namespace M0LTE.S5066.Sweep;

/// <summary>
/// What the nodes run: <c>fixed:wn6-long</c> (an empty drc.table on both nodes plus
/// drc.default_speed, the fixed-rate baseline of design shape item 6 and open question 13) or
/// <c>adaptive:&lt;table&gt;[@wnN-il]</c> (a drc.table file, and optionally the default speed).
/// </summary>
internal sealed record SweepConfig(string Id, Rung? DefaultSpeed, JsonObject? Table, string Spec)
{
    public static SweepConfig Parse(string spec, string? tablesDir = null)
    {
        string s = spec.Trim();
        if (s.StartsWith("fixed:", StringComparison.Ordinal))
        {
            Rung r = Rung.Parse(s[6..]);
            JsonObject empty = HostConfig.Defaults()["drc"]!["table"]!.AsObject();
            return new SweepConfig("fixed-" + r.Name, r, (JsonObject)empty.DeepClone(), s);
        }

        if (s.StartsWith("adaptive:", StringComparison.Ordinal))
        {
            string rest = s[9..];
            Rung? speed = null;
            int at = rest.LastIndexOf('@');
            if (at >= 0)
            {
                speed = Rung.Parse(rest[(at + 1)..]);
                rest = rest[..at];
            }

            string path = ResolveTable(rest, tablesDir);
            JsonNode root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })
                ?? throw new FormatException($"{path} is empty");
            JsonObject table = root["drc"]?["table"]?.AsObject() ?? root.AsObject();
            if (table["ladder"] is null)
            {
                throw new FormatException($"{path} holds no drc.table (no ladder key)");
            }

            string name = Path.GetFileNameWithoutExtension(path);
            string id = "adaptive-" + name + (speed is { } sp ? "-from-" + sp.Name : "");
            return new SweepConfig(id, speed, (JsonObject)table.DeepClone(), s);
        }

        throw new FormatException($"config '{spec}' is not fixed:wnN-il or adaptive:<table>[@wnN-il]");
    }

    private static string ResolveTable(string name, string? tablesDir)
    {
        if (File.Exists(name))
        {
            return Path.GetFullPath(name);
        }

        foreach (string? dir in new[] { tablesDir, Path.Combine(AppContext.BaseDirectory, "tables") })
        {
            if (dir is null)
            {
                continue;
            }

            string p = Path.Combine(dir, name.EndsWith(".json", StringComparison.Ordinal) ? name : name + ".json");
            if (File.Exists(p))
            {
                return p;
            }
        }

        throw new FileNotFoundException($"drc.table '{name}' not found (a path, or a name in tables/)");
    }
}

/// <summary>The grid of a sweep and its options.</summary>
internal sealed record SweepSettings
{
    public string Profile { get; init; } = "smoke";

    public List<string> Channels { get; init; } = [];

    public List<int> SnrTenths { get; init; } = [];

    public List<string> Configs { get; init; } = [];

    public List<int> Seeds { get; init; } = [];

    public long DurationMs { get; init; }

    public long WarmupMs { get; init; }

    public string NodeConfigPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "sweep-node.json");

    public string? TablesDir { get; init; }

    /// <summary>The named profiles; options given on the command line replace their parts.</summary>
    public static SweepSettings ForProfile(string profile) => profile switch
    {
        // A couple of minutes on one machine: two channels, one fixed rung and the synthetic adaptive table, one point.
        "smoke" => new SweepSettings
        {
            Profile = "smoke",
            Channels = ["awgn", "poor"],
            SnrTenths = [180],
            Configs = ["fixed:wn4-long", "adaptive:synthetic-test"],
            Seeds = [1],
            DurationMs = 240_000,
            WarmupMs = 30_000,
        },
        // The stack baselines of the calibration plan: fixed WN 1 to 8 and 13 at the Long interleaver.
        "baseline" => new SweepSettings
        {
            Profile = "baseline",
            Channels = ["awgn", "poor", "poor-ssb"],
            SnrTenths = [.. Enumerable.Range(0, 11).Select(i => i * 30)],
            Configs = [.. Rung.LadderWns.Select(w => $"fixed:wn{w}-long")],
            Seeds = [1, 2, 3],
            DurationMs = 1_800_000,
            WarmupMs = 60_000,
        },
        // One tuning round of the adaptive node (P7-9): the same grid, the table given with --table.
        "adaptive" => new SweepSettings
        {
            Profile = "adaptive",
            Channels = ["awgn", "poor", "poor-ssb"],
            SnrTenths = [.. Enumerable.Range(0, 11).Select(i => i * 30)],
            Configs = ["adaptive:synthetic-test"],
            Seeds = [1, 2, 3],
            DurationMs = 1_800_000,
            WarmupMs = 60_000,
        },
        _ => throw new ArgumentException($"profile '{profile}' is not smoke, baseline or adaptive"),
    };

    public JsonObject NodeOverlay()
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(NodeConfigPath), documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })
            ?? throw new FormatException($"{NodeConfigPath} is empty");
        return root.AsObject();
    }

    /// <summary>Every run of the sweep, in a fixed order (the order shards split on).</summary>
    public List<RunSettings> Jobs()
    {
        JsonObject overlay = NodeOverlay();
        var configs = Configs.Select(c => SweepConfig.Parse(c, TablesDir)).ToList();
        var jobs = new List<RunSettings>();
        foreach (string ch in Channels)
        {
            ChannelSpec spec = ChannelSpec.Parse(ch);
            foreach (int snr in SnrTenths)
            {
                foreach (SweepConfig c in configs)
                {
                    foreach (int seed in Seeds)
                    {
                        jobs.Add(new RunSettings(spec, snr, c, seed, DurationMs, WarmupMs, overlay));
                    }
                }
            }
        }

        return jobs;
    }

    /// <summary>What fixes the results: a resumed run or another shard must match it.</summary>
    public JsonObject Describe()
    {
        var configs = Configs.Select(c => SweepConfig.Parse(c, TablesDir)).ToList();
        return new JsonObject
        {
            ["profile"] = Profile,
            ["channels"] = new JsonArray([.. Channels.Select(c => (JsonNode)c)]),
            ["snr_tenth_db"] = new JsonArray([.. SnrTenths.Select(s => (JsonNode)s)]),
            ["configs"] = new JsonArray([.. configs.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["spec"] = c.Spec,
                ["table_fnv1a64"] = c.Table is null ? null : Sweep.Seeds.Mix(c.Table.ToJsonString()).ToString("x16", CultureInfo.InvariantCulture),
            })]),
            ["seeds"] = new JsonArray([.. Seeds.Select(s => (JsonNode)s)]),
            ["duration_ms"] = DurationMs,
            ["warmup_ms"] = WarmupMs,
            ["node_overlay"] = NodeOverlay(),
            ["versions"] = new JsonObject([.. Simulation.VersionInfo().Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))]),
        };
    }
}
