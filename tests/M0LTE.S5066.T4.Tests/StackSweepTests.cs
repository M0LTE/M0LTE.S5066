using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Sweep;

namespace M0LTE.S5066.T4.Tests;

/// <summary>
/// The stack sweep harness (P7-8, tools/s5066-sweep). One short virtual-clock run, twice, in CI: it
/// links, delivers bulk data and repeats exactly for a seed. Sharding, resume and the merge run on
/// stand-in results. The smoke profile through the real simulation runs only when S5066_LONG_TESTS=1.
/// </summary>
public class StackSweepTests
{
    private readonly ITestOutputHelper _output;

    public StackSweepTests(ITestOutputHelper output) => _output = output;

    private static RunSettings Short(string config, int seconds) => new(
        ChannelSpec.Parse("awgn"), 300, SweepConfig.Parse(config), 1, seconds * 1000L, 0, new SweepSettings().NodeOverlay());

    [Fact]
    public void A_short_run_links_delivers_and_repeats_exactly_for_a_seed()
    {
        // 90 s of channel at 30 dB on WN 6 Short: a few seconds of demodulator time per run.
        RunSettings s = Short("fixed:wn6-short", 90);
        RunResult first = new Simulation(s).Run();
        RunResult second = new Simulation(s).Run();
        _output.WriteLine(first.ToJson());

        first.Error.Should().BeNull();
        first.DeliveredBytes.Should().BeGreaterThan(0);
        first.BulkCorrupt.Should().Be(0);
        first.LinksLost.Should().Be(0);
        first.Nodes["a"].LinksMade.Should().Be(1);
        first.Nodes["a"].Rungs.Keys.Should().Equal("wn6-short");
        first.Nodes["b"].Rungs.Keys.Should().Equal("wn6-short");
        first.SameSenderGapMs.Should().ContainKey("wn6-short");
        first.Nodes["a"].FailedTrials.Should().Be(0, "a fixed rung has an empty table, so no trials");
        first.Nodes["a"].FirstCeilingMs.Should().BeNull();
        first.Nodes["a"].TransmissionsAfterFirstCeiling.Should().Be(0);
        first.Nodes["a"].Rungs["wn6-short"].CountAfterFirstCeiling.Should().Be(0);

        Strip(first).Should().Be(Strip(second), "a run repeats exactly for a seed");
    }

    private static string Strip(RunResult r)
    {
        JsonObject o = JsonNode.Parse(r.ToJson())!.AsObject();
        o.Remove("wall_seconds");
        o.Remove("core_seconds_per_signal_second");
        return o.ToJsonString();
    }

    [Fact]
    public void The_fixed_baseline_is_an_empty_table_plus_the_default_speed()
    {
        // Design shape item 6 and open question 13: no fixed-speed setting.
        RunSettings s = Short("fixed:wn3-medium", 1);
        JsonObject node = s.NodeJson(a: true);
        node["drc"]!["default_speed"]!.ToJsonString().Should().Be("""{"wn":3,"interleaver":"medium"}""");
        node["drc"]!["table"]!["ladder"]!.AsArray().Should().BeEmpty();
        node["local_address"]!.GetValue<string>().Should().Be("0.0.1.1");
        s.NodeJson(a: false)["local_address"]!.GetValue<string>().Should().Be("0.0.1.2");
        s.NodeJson(a: false)["prng_seed"]!.GetValue<string>().Should().NotBe(node["prng_seed"]!.GetValue<string>());
        ConfigReader.Read(node).Drc.DefaultSpeed.Should().Be(new Speed(3, Interleaver.Medium));
    }

    [Fact]
    public void An_adaptive_config_reads_a_table_file_and_an_optional_start_rung()
    {
        SweepConfig c = SweepConfig.Parse("adaptive:synthetic-test@wn4-long");
        c.Id.Should().Be("adaptive-synthetic-test-from-wn4-long");
        c.DefaultSpeed.Should().Be(new Rung(4, Interleaver.Long));
        c.Table!["ladder"]!.AsArray().Should().HaveCount(5);
        SweepConfig.Parse("adaptive:synthetic-test").DefaultSpeed.Should().BeNull();
    }

    [Fact]
    public void An_adaptive_config_reads_the_calibrated_source_and_table_wrapper()
    {
        // A calibrated drc-table.json is {"source": ..., "table": ...} (corpus-format.md 6.1); round 1 had to unwrap it by hand.
        JsonObject bare = SweepConfig.Parse("adaptive:synthetic-test").Table!;
        string dir = Directory.CreateTempSubdirectory("s5066-sweep-").FullName;
        try
        {
            string path = Path.Combine(dir, "drc-table.json");
            File.WriteAllText(path, new JsonObject { ["source"] = new JsonObject { ["calibration"] = "test" }, ["table"] = bare.DeepClone() }.ToJsonString());
            SweepConfig c = SweepConfig.Parse("adaptive:" + path);
            c.Id.Should().Be("adaptive-drc-table");
            c.Table!.ContainsKey("source").Should().BeFalse();
            c.Table.ToJsonString().Should().Be(bare.ToJsonString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Rungs_snr_lists_and_channels_parse()
    {
        Rung.Parse("wn13-ultrashort").Should().Be(new Rung(13, Interleaver.UltraShort));
        Rung.Parse("WN2:long").Name.Should().Be("wn2-long");
        Rung.All36.Should().HaveCount(36);
        Cli.ParseSnrs("0:30:3").Should().Equal(0, 30, 60, 90, 120, 150, 180, 210, 240, 270, 300);
        Cli.ParseSnrs("1.5,6").Should().Equal(15, 60);
        ChannelSpec.Parse("poor-ssb").Ssb.Should().BeTrue();
        ChannelSpec.Parse("awgn").Paths.Should().BeEmpty();
        FluentActions.Invoking(() => ChannelSpec.Parse("good")).Should().Throw<FormatException>();
    }

    [Fact]
    public void A_distribution_merges_and_gives_quantiles_by_100_ms_bin()
    {
        var a = new Dist();
        foreach (long v in new long[] { 50, 150, 250, 350 })
        {
            a.Add(v);
        }

        var b = new Dist();
        b.Add(-120);
        a.Merge(b);
        (a.N, a.Min, a.Max).Should().Be((5L, -120L, 350L));
        a.Mean.Should().Be(136);
        a.Quantile(0.5).Should().Be(150);
        a.Quantile(0).Should().Be(-120, "a quantile stays within the extremes");
        a.Quantile(1).Should().Be(350);
    }

    [Fact]
    public void Shards_cover_the_plan_once_a_resume_does_only_what_is_left_and_the_merge_aggregates()
    {
        string dir = Directory.CreateTempSubdirectory("s5066-sweep-test-").FullName;
        try
        {
            SweepSettings grid = SweepSettings.ForProfile("smoke") with
            {
                Channels = ["awgn", "poor"],
                SnrTenths = [60, 120],
                Configs = ["fixed:wn2-long", "fixed:wn6-long"],
                Seeds = [1, 2],
                DurationMs = 60_000,
                WarmupMs = 0,
            };
            var ran = new List<string>();
            RunResult Fake(RunSettings j)
            {
                lock (ran)
                {
                    ran.Add(j.Id);
                }

                // WN 6 wins at 12 dB, WN 2 at 6 dB; WN 2 has the higher sum on both channels.
                double bps = j.Config.Id == "fixed-wn6-long" ? (j.SnrTenthDb == 120 ? 900 : 0) : 500 + j.Seed;
                var stats = new NodeStats { Transmissions = 2, DataDpdus = 10, RetransmittedDpdus = 1 };
                stats.Rungs[j.Config.DefaultSpeed!.Value.Name] = new RungStats { Count = 2 };
                var r = new RunResult { Id = j.Id, Channel = j.Channel.Name, SnrTenthDb = j.SnrTenthDb, Config = j.Config.Id, Seed = j.Seed, DurationMs = j.DurationMs, GoodputBps = bps };
                r.Nodes["a"] = stats;
                r.Nodes["b"] = new NodeStats();
                r.SameSenderGapMs[j.Config.DefaultSpeed!.Value.Name] = new Dist();
                r.SameSenderGapMs[j.Config.DefaultSpeed!.Value.Name].Add(8000);
                r.ExchangeGapMs.Add(3000);
                return r;
            }

            int total = grid.Jobs().Count;
            total.Should().Be(16);
            SweepRunner.Run(grid, dir, 2, (0, 3), _output.WriteLine, Fake).Should().Be(0);
            SweepRunner.Run(grid, dir, 2, (1, 3), _output.WriteLine, Fake).Should().Be(0);
            Merge.Run(grid, dir, _output.WriteLine).Should().Be(1, "one shard is still to run");
            SweepRunner.Run(grid, dir, 2, (2, 3), _output.WriteLine, Fake).Should().Be(0);
            ran.Should().OnlyHaveUniqueItems().And.HaveCount(total);

            // A resume runs nothing that is done; a run whose file went missing runs again.
            File.Delete(Path.Combine(dir, "runs", grid.Jobs()[5].Id + ".json"));
            ran.Clear();
            SweepRunner.Run(grid, dir, 2, (0, 1), _output.WriteLine, Fake).Should().Be(0);
            ran.Should().Equal(grid.Jobs()[5].Id);

            // Another grid in the same directory is refused.
            SweepRunner.Run(grid with { Seeds = [1] }, dir, 2, (0, 1), _output.WriteLine, Fake).Should().Be(1);

            Merge.Run(grid, dir, _output.WriteLine).Should().Be(0);
            string agg = Path.Combine(dir, "aggregate");
            string[] curve = File.ReadAllLines(Path.Combine(agg, "curve-awgn.tsv"));
            curve.Should().HaveCount(5);
            curve[1].Split('\t')[..7].Should().Equal("awgn", "60", "fixed-wn2-long", "1,2", "501.5", "501.0", "502.0");
            curve[1].Split('\t')[7].Should().Be("10.00");
            string[] baseline = File.ReadAllLines(Path.Combine(agg, "baseline.tsv"));
            baseline.Should().Contain("awgn\tbaseline\t-\tfixed-wn2-long\t501.5");
            baseline.Should().Contain("poor\tbest_at_point\t120\tfixed-wn6-long\t900.0");
            File.ReadAllText(Path.Combine(agg, "turnaround.tsv")).Should().Contain("awgn\tfixed\twn6-long\tsame_sender\t4\t8000");
            File.Exists(Path.Combine(agg, "plan.json")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_gate_passes_strictly_above_the_baseline_or_within_2_percent_of_its_ceiling()
    {
        RunResult R(string config, int snr, double bps) => new() { Id = $"{config}-{snr}", Channel = "awgn", SnrTenthDb = snr, Config = config, Seed = 1, GoodputBps = bps };

        // fixed-wn8-long is the baseline (highest sum); its ceiling is 2000, reached at 180 and 300 (1990 is within 2%).
        RunResult[] results =
        [
            R("fixed-wn8-long", 0, 0), R("fixed-wn2-long", 0, 0), R("adaptive-t", 0, 0),
            R("fixed-wn8-long", 90, 0), R("fixed-wn2-long", 90, 300), R("adaptive-t", 90, 800),
            R("fixed-wn8-long", 120, 1500), R("fixed-wn2-long", 120, 300), R("adaptive-t", 120, 1500),
            R("fixed-wn8-long", 180, 2000), R("fixed-wn2-long", 180, 300), R("adaptive-t", 180, 1961),
            R("fixed-wn8-long", 300, 1990), R("fixed-wn2-long", 300, 300), R("adaptive-t", 300, 1959),
        ];
        string[] gate = Merge.Gate(results).TrimEnd('\n').Split('\n');
        gate.Should().HaveCount(6);
        gate[1].Should().Be("awgn\t0\tadaptive-t\t0.0\tfixed-wn8-long\t0.0\t2000.0\tno\tgoodput\t0\t0\t0\t0.0\t0.0\tnot gated");
        gate[2].Should().EndWith("\tno\tgoodput\t0\t0\t0\t0.0\t0.0\tpass", "800 is above the baseline's 0 where another rung delivers");
        gate[3].Should().EndWith("\tno\tgoodput\t0\t0\t0\t0.0\t0.0\tfail", "equal is not strictly above, and 1500 is not at the ceiling");
        gate[4].Should().EndWith("\tyes\tgoodput\t0\t0\t0\t0.0\t0.0\tpass", "1961 is within 2% of the 2000 ceiling");
        gate[5].Should().EndWith("\tyes\tgoodput\t0\t0\t0\t0.0\t0.0\tfail", "1959 is more than 2% below the ceiling");
    }

    [Fact]
    public void On_an_ssb_channel_the_gate_is_no_link_lost_and_under_5_percent_on_rungs_that_never_decode()
    {
        // Ruling (d) of 2026-11-09 (open question 14): on poor-ssb, not goodput. These stand-in results carry no
        // transmissions after the first ceiling (results written before ruling (f)), so every transmission
        // counts (rule ssb-all-tx). wn8-long never decodes on the channel (its fixed run delivers no D_PDU, and neither do adaptive's transmissions on it); wn7-long does.
        static NodeStats Node(params (string Rung, long Count, long Ok)[] rungs)
        {
            var n = new NodeStats();
            foreach ((string rung, long count, long ok) in rungs)
            {
                n.Rungs[rung] = new RungStats { Count = count, DpdusOk = ok };
            }

            return n;
        }

        RunResult R(string config, int snr, double bps, long lost, params (string, long, long)[] rungs) => new()
        {
            Id = $"{config}-{snr}", Channel = "poor-ssb", SnrTenthDb = snr, Config = config, Seed = 1, GoodputBps = bps, LinksLost = lost,
            Nodes = new(StringComparer.Ordinal) { ["a"] = Node(rungs) },
        };

        RunResult[] results =
        [
            R("fixed-wn8-long", 180, 0, 2, ("wn8-long", 40, 0)), R("fixed-wn7-long", 180, 1133, 0, ("wn7-long", 40, 900)),
            R("adaptive-t", 180, 983, 0, ("wn7-long", 97, 2000), ("wn8-long", 3, 0)),
            R("fixed-wn8-long", 300, 0, 2, ("wn8-long", 40, 0)), R("fixed-wn7-long", 300, 1133, 0, ("wn7-long", 40, 900)),
            R("adaptive-t", 300, 1500, 0, ("wn7-long", 90, 2000), ("wn8-long", 10, 0)),
            R("fixed-wn8-long", 120, 0, 2, ("wn8-long", 40, 0)), R("fixed-wn7-long", 120, 900, 0, ("wn7-long", 40, 900)),
            R("adaptive-t", 120, 1000, 1, ("wn7-long", 100, 2000)),
        ];
        string[] gate = Merge.Gate(results).TrimEnd('\n').Split('\n');
        gate.Should().HaveCount(4);
        gate[1].Should().Be("poor-ssb\t120\tadaptive-t\t1000.0\tfixed-wn7-long\t900.0\t1133.0\tno\tssb-all-tx\t1\t-\t-\t0.0\t0.0\tfail", "a link was lost, whatever the goodput");
        gate[2].Should().EndWith("\tssb-all-tx\t0\t-\t-\t3.0\t3.0\tpass", "below the baseline's goodput, but no link lost and 3 of 100 transmissions on wn8-long");
        gate[3].Should().EndWith("\tssb-all-tx\t0\t-\t-\t10.0\t10.0\tfail", "10 of 100 transmissions on a rung that never decodes");

        // Adaptive runs alone (a targeted re-run): no baseline, still gated on an SSB channel, and not on AWGN.
        string[] alone = Merge.Gate([results[2], new RunResult { Id = "x", Channel = "awgn", SnrTenthDb = 180, Config = "adaptive-t", Seed = 1, GoodputBps = 2000 }]).TrimEnd('\n').Split('\n');
        alone.Should().HaveCount(2);
        alone[1].Should().Be("poor-ssb\t180\tadaptive-t\t983.0\t-\t-\t-\tno\tssb-all-tx\t0\t-\t-\t3.0\t3.0\tpass");
    }

    [Fact]
    public void On_an_ssb_channel_the_gate_counts_only_the_transmissions_after_the_first_ceiling()
    {
        // Ruling (f) of 2026-11-09 (open question 14 as written): the learning before a node's first failed-trial
        // ceiling is not counted. wn8-long never decodes on poor-ssb. Each node: (rung, all, after the first ceiling, ok).
        static NodeStats Node(long after, params (string Rung, long Count, long After, long Ok)[] rungs)
        {
            var n = new NodeStats { TransmissionsAfterFirstCeiling = after };
            foreach ((string rung, long count, long a, long ok) in rungs)
            {
                n.Rungs[rung] = new RungStats { Count = count, CountAfterFirstCeiling = a, DpdusOk = ok };
            }

            return n;
        }

        RunResult R(string config, int snr, int seed, NodeStats a, NodeStats b) => new()
        {
            Id = $"{config}-{snr}-{seed}", Channel = "poor-ssb", SnrTenthDb = snr, Config = config, Seed = seed, GoodputBps = 1000,
            Nodes = new(StringComparer.Ordinal) { ["a"] = a, ["b"] = b },
        };

        RunResult[] results =
        [
            R("fixed-wn8-long", 180, 1, Node(0, ("wn8-long", 40, 0, 0)), Node(0, ("wn8-long", 40, 0, 0))),
            // 8 of 100 on wn8-long, but 6 of them before node a's first ceiling: 2 of the 90 after it.
            R("adaptive-t", 180, 1, Node(80, ("wn7-long", 82, 78, 900), ("wn8-long", 8, 2, 0)), Node(10, ("wn7-long", 10, 10, 100))),
            // Seed 2: no ceiling at all on either node, so nothing is counted after one.
            R("adaptive-t", 180, 2, Node(0, ("wn7-long", 100, 0, 900)), Node(0, ("wn7-long", 10, 0, 100))),
            // At 300: 3 of 20 after the ceiling are on wn8-long (15%), a fail though only 3 of 130 overall (2.3%).
            R("adaptive-t", 300, 1, Node(20, ("wn7-long", 117, 17, 900), ("wn8-long", 3, 3, 0)), Node(0, ("wn7-long", 10, 0, 100))),
        ];
        string[] gate = Merge.Gate(results).TrimEnd('\n').Split('\n');
        gate.Should().HaveCount(3);
        gate[1].Should().EndWith("\tssb\t0\t2\t90\t2.2\t3.8\tpass", "2 of the 90 transmissions after the first ceiling over both seeds, though 8 of 210 overall");
        gate[2].Should().EndWith("\tssb\t0\t3\t20\t15.0\t2.3\tfail", "3 of 20 after the ceiling, though 3 of 130 overall");
    }

    [Fact]
    public void The_final_profile_is_the_baseline_rungs_and_the_calibrated_table_with_one_first_data_wait()
    {
        // P7-10 (calibration plan "Final"): AWGN, Poor, Poor-SSB; 0 to 30 dB in 3 dB steps; fixed WN 1 to 8 and 13
        // Long plus the calibrated table; 5 seeds of 30 minutes; both arms with the wait the table derives.
        SweepSettings f = SweepSettings.ForProfile("final");
        f.Configs.Should().HaveCount(10).And.Contain("adaptive:ms110d-cs-0.6.1").And.Contain("fixed:wn13-long");
        f.Jobs().Should().HaveCount(3 * 11 * 10 * 5);
        SweepConfig adaptive = SweepConfig.Parse("adaptive:ms110d-cs-0.6.1");
        adaptive.Id.Should().Be("adaptive-ms110d-cs-0.6.1");
        long derived = SweepSettings.DerivedFirstDataMs(adaptive, f.NodeOverlay());
        derived.Should().BeGreaterThan(3 * 57_000L, "the ladder's slowest rung, WN 1 Long, needs more than three answer waits for a 2054-byte C_PDU");
        f.Jobs().Should().OnlyContain(j => ConfigReader.Read(j.NodeJson(true)).Cas.FirstDataTimeoutMs == derived, "both arms wait alike");
        f.Describe()["node_overlay"]!["cas"]!["first_data_timeout_ms"]!.GetValue<long>().Should().Be(derived, "the plan records it, so a resume with another wait is refused");

        // The other profiles leave it unset, and a number or unset can be given.
        SweepSettings.ForProfile("baseline").EffectiveOverlay()["cas"]!["first_data_timeout_ms"].Should().BeNull();
        (f with { FirstData = "600000" }).EffectiveOverlay()["cas"]!["first_data_timeout_ms"]!.GetValue<long>().Should().Be(600_000);
        FluentActions.Invoking(() => (f with { Configs = ["fixed:wn8-long"] }).EffectiveOverlay()).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_points_file_puts_adaptive_beside_the_baseline_and_the_best_fixed_rung()
    {
        RunResult R(string config, int snr, int seed, double bps) => new() { Id = $"{config}-{snr}-{seed}", Channel = "awgn", SnrTenthDb = snr, Config = config, Seed = seed, GoodputBps = bps };
        RunResult[] results =
        [
            R("fixed-wn8-long", 0, 1, 0), R("fixed-wn2-long", 0, 1, 100), R("adaptive-t", 0, 1, 150), R("adaptive-t", 0, 2, 50),
            R("fixed-wn8-long", 300, 1, 2000), R("fixed-wn2-long", 300, 1, 300), R("adaptive-t", 300, 1, 2400), R("adaptive-t", 300, 2, 2000),
        ];
        string[] lines = Merge.Points(results, Merge.Gate(results)).TrimEnd('\n').Split('\n');
        lines.Should().HaveCount(3);
        lines[1].Should().Be("awgn\t0.0\tadaptive-t\t100.0\t50.0\t150.0\tfixed-wn8-long\t0.0\t0.0\t0.0\tfixed-wn2-long\t100.0\t-\t0.0\t0\tgoodput\tpass");
        lines[2].Should().Be("awgn\t30.0\tadaptive-t\t2200.0\t2000.0\t2400.0\tfixed-wn8-long\t2000.0\t2000.0\t2000.0\tfixed-wn8-long\t2000.0\t10.0\t10.0\t0\tgoodput\tpass");
    }

    [Fact]
    public void Long_the_smoke_profile_runs_sharded_and_merges()
    {
        // A few minutes of demodulator time: four runs of 240 s. Run with S5066_LONG_TESTS=1, never in CI.
        Assert.SkipUnless(Environment.GetEnvironmentVariable("S5066_LONG_TESTS") == "1", "long stack sweep test: set S5066_LONG_TESTS=1 to run");
        string dir = Directory.CreateTempSubdirectory("s5066-sweep-smoke-").FullName;
        try
        {
            SweepSettings smoke = SweepSettings.ForProfile("smoke");
            SweepRunner.Run(smoke, dir, 2, (0, 2), _output.WriteLine).Should().Be(0);
            SweepRunner.Run(smoke, dir, 2, (1, 2), _output.WriteLine).Should().Be(0);
            Merge.Run(smoke, dir, _output.WriteLine).Should().Be(0);
            foreach (string f in Directory.GetFiles(Path.Combine(dir, "aggregate")))
            {
                _output.WriteLine($"== {Path.GetFileName(f)}");
                _output.WriteLine(File.ReadAllText(f));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
