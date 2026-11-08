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
