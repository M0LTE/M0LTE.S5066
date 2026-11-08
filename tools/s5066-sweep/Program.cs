using System.Globalization;
using M0LTE.S5066.Sweep;

// s5066-sweep: the stack sweep harness (core-architecture.md 9, P7-8). Runbook: docs/stack-sweep.md.
return Cli.Execute(args);

namespace M0LTE.S5066.Sweep
{
    internal static class Cli
    {
        private const string Usage = """
            usage: s5066-sweep <command> [options]

              run       one run on the virtual clock; prints a summary, --json writes the result
                        --channel awgn|poor|moderate[-ssb]  --snr <dB>  --config fixed:wn6-long|adaptive:<table>[@wnN-il]
                        [--seed 1] [--duration-s 600] [--warmup-s 60] [--node-config file] [--json file] [--timeline 1]
              plan      print a sweep's runs and size     [--profile smoke|baseline|adaptive] [grid options]
              sweep     run a sweep (resumable, shardable) --out <dir> [--workers N] [--shard i/n] [--profile ...] [grid options]
              merge     merge a sweep's runs into aggregate/ --out <dir> [--profile ...] [grid options]
              realtime  the real-time cross-check: two s5066d over UDP audio through the same channel
                        --channel ... --snr ... --config fixed:wnN-il [--seed 1] [--duration-s 600] [--warmup-s 60] [--json file]

            grid options (replace the profile's): --channels a,b  --snrs 0:30:3 or 6,12  --configs c1,c2
              --seeds 1,2,3  --duration-s N  --warmup-s N  --node-config file  --table <name or path> (for adaptive configs)
            """;

        public static int Execute(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help")
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            try
            {
                var o = Options.Parse(args[1..]);
                return args[0] switch
                {
                    "run" => RunOne(o),
                    "plan" => Plan(o),
                    "sweep" => SweepRunner.Run(Grid(o), o.Required("out"), o.Int("workers", Environment.ProcessorCount), o.Shard(), Log),
                    "merge" => Merge.Run(Grid(o), o.Required("out"), Log),
                    "realtime" => Realtime.Run(Single(o), o.Get("json"), Log),
                    _ => throw new ArgumentException($"unknown command '{args[0]}'"),
                };
            }
            catch (Exception e) when (e is ArgumentException or FormatException or FileNotFoundException or InvalidOperationException)
            {
                Console.Error.WriteLine($"s5066-sweep: {e.Message}");
                return 2;
            }
        }

        private static void Log(string line) => Console.Error.WriteLine(line);

        private static RunSettings Single(Options o)
        {
            var grid = new SweepSettings { NodeConfigPath = o.Get("node-config") ?? new SweepSettings().NodeConfigPath, TablesDir = o.Get("tables-dir") };
            return new RunSettings(
                ChannelSpec.Parse(o.Required("channel")),
                (int)Math.Round(double.Parse(o.Required("snr"), CultureInfo.InvariantCulture) * 10),
                SweepConfig.Parse(o.Required("config"), grid.TablesDir),
                o.Int("seed", 1),
                o.Int("duration-s", 600) * 1000L,
                o.Int("warmup-s", 60) * 1000L,
                grid.NodeOverlay());
        }

        private static int RunOne(Options o)
        {
            RunSettings s = Single(o);
            RunResult r = new Simulation(s) { Timeline = o.Get("timeline") == "1" ? Console.WriteLine : null }.Run();
            if (o.Get("json") is { } path)
            {
                File.WriteAllText(path, r.ToJson());
            }

            Print(r);
            return r.Error is null ? 0 : 1;
        }

        internal static void Print(RunResult r)
        {
            var inv = CultureInfo.InvariantCulture;
            bool real = r.Mode == "realtime";   // the bridge sees only audio: bursts and their timing, no D_PDUs or links
            Console.WriteLine(string.Create(inv, $"{r.Id}: goodput {r.GoodputBps:0} bit/s ({r.DeliveredBytesWindow} bytes in the window, {r.DeliveredBytes} in all){(real ? "" : $", retransmissions {r.RetransmissionRate:P1}, links lost {r.LinksLost}")}"));
            Console.WriteLine(string.Create(inv, $"  chat {r.Chat.Delivered}/{r.Chat.Sent} delivered, latency {r.Chat.LatencyMs.Summary()}"));
            foreach ((string node, NodeStats n) in real ? [] : r.Nodes)
            {
                Console.WriteLine(string.Create(inv, $"  node {node}: {n.Transmissions} transmissions ({n.AckOnlyTransmissions} ACK-only), links made {n.LinksMade}, lost {n.LinksLost}, closed {n.LinksClosed}"));
                foreach ((string rung, RungStats s) in n.Rungs)
                {
                    string snr = s.SnrCount > 0 ? string.Create(inv, $", mean estimate {s.SnrSumTenths / 10.0 / s.SnrCount:0.0} dB") : "";
                    Console.WriteLine(string.Create(inv, $"    {rung}: {s.Count} tx, {s.AirtimeMs / 1000.0:0.0} s on air, D_PDUs {s.DpdusOk}/{s.Dpdus} right, {s.RetransmittedDpdus}/{s.DataDpdus} data retransmitted, {string.Join(" ", s.Outcomes.Select(kv => $"{kv.Key} {kv.Value}"))}{snr}"));
                }
            }

            foreach ((string node, NodeStats n) in real ? r.Nodes : [])
            {
                Console.WriteLine(string.Create(inv, $"  node {node}: {n.Transmissions} bursts, {n.Rungs.Values.Sum(x => x.AirtimeMs) / 1000.0:0.0} s on air"));
            }

            foreach ((string rung, Dist d) in r.SameSenderGapMs)
            {
                Console.WriteLine($"  gap to the same sender's next burst after {rung}: {d.Summary()}");
            }

            Console.WriteLine($"  gap to the other node's next burst: {r.ExchangeGapMs.Summary()}");
            foreach ((string rung, Dist d) in r.EotOvershootMs)
            {
                Console.WriteLine($"  EOT overshoot on {rung}: {d.Summary()}");
            }

            Console.WriteLine(string.Create(inv, $"  {r.WallSeconds:0.0} s wall{(real ? "" : $", {r.CoreSecondsPerSignalSecond:0.000} core-s per channel-s")}{(r.Error is null ? "" : ", ERROR " + r.Error)}"));
        }

        private static SweepSettings Grid(Options o)
        {
            SweepSettings s = SweepSettings.ForProfile(o.Get("profile") ?? "smoke");
            if (o.Get("channels") is { } ch)
            {
                s = s with { Channels = [.. ch.Split(',', StringSplitOptions.RemoveEmptyEntries)] };
            }

            if (o.Get("snrs") is { } snrs)
            {
                s = s with { SnrTenths = ParseSnrs(snrs) };
            }

            if (o.Get("configs") is { } cf)
            {
                s = s with { Configs = [.. cf.Split(',', StringSplitOptions.RemoveEmptyEntries)] };
            }

            if (o.Get("table") is { } table)
            {
                s = s with { Configs = [.. s.Configs.Select(c => c.StartsWith("adaptive:", StringComparison.Ordinal) ? "adaptive:" + table + (c.Contains('@') ? c[c.LastIndexOf('@')..] : "") : c)] };
            }

            if (o.Get("seeds") is { } seeds)
            {
                s = s with { Seeds = [.. seeds.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture))] };
            }

            if (o.Get("duration-s") is not null)
            {
                s = s with { DurationMs = o.Int("duration-s", 0) * 1000L };
            }

            if (o.Get("warmup-s") is not null)
            {
                s = s with { WarmupMs = o.Int("warmup-s", 0) * 1000L };
            }

            if (o.Get("node-config") is { } nc)
            {
                s = s with { NodeConfigPath = Path.GetFullPath(nc) };
            }

            return s with { TablesDir = o.Get("tables-dir") };
        }

        internal static List<int> ParseSnrs(string text)
        {
            static int T(string x) => (int)Math.Round(double.Parse(x, CultureInfo.InvariantCulture) * 10);
            string[] r = text.Split(':');
            if (r.Length == 3)
            {
                int from = T(r[0]), to = T(r[1]), step = T(r[2]);
                var list = new List<int>();
                for (int v = from; v <= to; v += step)
                {
                    list.Add(v);
                }

                return list;
            }

            return [.. text.Split(',').Select(T)];
        }

        private static int Plan(Options o)
        {
            SweepSettings s = Grid(o);
            List<RunSettings> jobs = s.Jobs();
            foreach (IGrouping<string, RunSettings> g in jobs.GroupBy(j => j.Channel.Name))
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{g.Key}: {g.Count()} runs, SNR {string.Join(",", s.SnrTenths.Select(x => x / 10.0))} dB"));
            }

            Console.WriteLine($"configs: {string.Join(", ", s.Configs)}; seeds {string.Join(",", s.Seeds)}");
            double hours = jobs.Count * s.DurationMs / 3_600_000.0;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"profile {s.Profile}: {jobs.Count} runs of {s.DurationMs / 60000.0:0.#} min, {hours:F1} channel-hours ({2 * hours:F1} hours of demodulator time at most, two nodes)"));
            return 0;
        }
    }

    /// <summary>--key value options.</summary>
    internal sealed class Options
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                {
                    throw new ArgumentException($"expected --option value, got '{args[i]}'");
                }

                o._values[args[i][2..]] = args[++i];
            }

            return o;
        }

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public string Required(string key) => Get(key) ?? throw new ArgumentException($"--{key} is required");

        public int Int(string key, int fallback) => Get(key) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

        public (int Index, int Count) Shard()
        {
            if (Get("shard") is not { } s)
            {
                return (0, 1);
            }

            string[] p = s.Split('/');
            int i = int.Parse(p[0], CultureInfo.InvariantCulture), n = int.Parse(p[1], CultureInfo.InvariantCulture);
            if (n < 1 || i < 0 || i >= n)
            {
                throw new ArgumentException("--shard must be i/n with 0 <= i < n");
            }

            return (i, n);
        }
    }
}
