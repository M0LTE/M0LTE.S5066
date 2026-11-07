using System.Text;
using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests;

public class ConfigReaderTests
{
    private static JsonObject Config(JsonObject? overlay = null) =>
        ConfigMerge.Merge(Corpus.T2Defaults, overlay ?? new JsonObject { ["local_address"] = "0.0.1.2" });

    [Fact]
    public void Reads_the_defaults_plus_the_per_node_settings()
    {
        NodeConfig c = ConfigReader.Read(Config());
        c.LocalAddress.Should().Be(0x0102u);
        c.Csma.Should().Be(new CsmaConfig(null, 3000, 16, 30000, 3000, null));
        c.Modem.PreambleMs.Should().BeNull();
        c.Modem.Waveforms.Should().BeEmpty();
        ConfigReader.ReadSeed(Config()).Should().Be(0UL);
        ConfigReader.UnknownKey(Config()).Should().BeNull();
    }

    [Theory]
    [InlineData("jitter", CsmaMode.Jitter)]
    [InlineData("slotted", CsmaMode.Slotted)]
    [InlineData("ed3", CsmaMode.Ed3)]
    public void Reads_each_csma_mode(string text, CsmaMode mode)
    {
        var o = new JsonObject { ["local_address"] = "0.0.1.2", ["csma"] = new JsonObject { ["mode"] = text } };
        ConfigReader.Read(Config(o)).Csma.Mode.Should().Be(mode);
    }

    [Fact]
    public void Missing_and_bad_settings_are_reported_by_key()
    {
        FluentActions.Invoking(() => ConfigReader.Read(ConfigMerge.Merge(Corpus.T2Defaults, new JsonObject())))
            .Should().Throw<MissingSettingException>().Which.Setting.Should().Be("local_address");
        FluentActions.Invoking(() => ConfigReader.Read(Config(new JsonObject
        {
            ["local_address"] = "0.0.1.2", ["csma"] = new JsonObject { ["mode"] = "aloha" },
        }))).Should().Throw<ConfigException>().Which.Setting.Should().Be("csma.mode");
        FluentActions.Invoking(() => ConfigReader.Read(Config(new JsonObject
        {
            ["local_address"] = "0.0.1.2", ["csma"] = new JsonObject { ["num_cont_slots"] = 0 },
        }))).Should().Throw<ConfigException>().Which.Setting.Should().Be("csma.num_cont_slots");
    }

    [Fact]
    public void Reads_the_modem_fixture()
    {
        var o = JsonNode.Parse("""
            {"local_address": "0.0.1.2", 
             "modem": {"preamble_ms": 720, "flush_ms": 0,
                       "waveforms": [{"wn": 6, "interleaver": "short", "block_bytes": 192, "block_ms": 1000}]}}
            """)!.AsObject();
        ModemFixture m = ConfigReader.Read(Config(o)).Modem;
        m.PreambleMs.Should().Be(720);
        m.Waveforms.Should().Equal(new ModemWaveform(6, Interleaver.Short, 192, 1000));
    }

    [Fact]
    public void An_unknown_key_is_named()
    {
        var c = Config();
        c["csma"]!.AsObject()["future_knob"] = 1;
        ConfigReader.UnknownKey(c).Should().Be("csma.future_knob");
    }

    [Fact]
    public void Merge_rejects_unknown_keys_and_shape_changes()
    {
        FluentActions.Invoking(() => ConfigMerge.Merge(Corpus.T2Defaults, new JsonObject { ["nope"] = 1 })).Should().Throw<InvalidDataException>();
        FluentActions.Invoking(() => ConfigMerge.Merge(Corpus.T2Defaults, new JsonObject { ["csma"] = 1 })).Should().Throw<InvalidDataException>();
        ConfigMerge.Merge(Corpus.T2Defaults, new JsonObject { ["modem"] = new JsonObject { ["waveforms"] = null } })["modem"]!["waveforms"]
            .Should().BeNull();
    }
}

public class FixtureModemTests
{
    private static readonly FixtureModem Modem = new(new ModemFixture(720, 40, [new ModemWaveform(6, Interleaver.Short, 192, 1000)]));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(192, 1)]
    [InlineData(193, 2)]
    [InlineData(1920, 10)]
    public void Plans_by_the_corpus_format_arithmetic(int bytes, int blocks)
    {
        Modem.PlanBurst(6, Interleaver.Short, bytes).Should().Be(new BurstPlan(720, 1000, 40, 192, blocks, 720 + (blocks * 1000) + 40));
    }

    [Fact]
    public void An_unplanned_waveform_and_a_null_preamble_are_reported()
    {
        FluentActions.Invoking(() => Modem.PlanBurst(6, Interleaver.Long, 1)).Should().Throw<UnplannedWaveformException>();
        var noPreamble = new FixtureModem(new ModemFixture(null, 0, [new ModemWaveform(6, Interleaver.Short, 192, 1000)]));
        FluentActions.Invoking(() => noPreamble.PlanBurst(6, Interleaver.Short, 1)).Should().Throw<MissingSettingException>();
    }
}

public class T2RunnerTests
{
    private static JsonObject NodeOverlay() => new() { ["local_address"] = "0.0.1.2" };

    private static ScenarioBuilder Builder(JsonObject? config = null) =>
        new("test-scenario", ["K-0065"], "derived", "a test", config ?? NodeOverlay());

    private static T2Result Run(ScenarioBuilder b) => T2Runner.Run("mem.jsonl", b.Lines(), Corpus.T2Defaults);

    [Fact]
    public void A_scenario_with_matching_outputs_passes_and_records_a_transcript()
    {
        T2Result r = Run(Builder().BurstStarted(0).BurstEnded(500, snrTenthDb: -12).ChannelBusy(600, true));
        r.Unsupported.Should().BeNull();
        r.Id.Should().Be("test-scenario");
        r.Transcript.Should().HaveCount(3);
        r.Transcript[1].Should().Contain("\"snr_tenth_db\":-12");
    }

    [Fact]
    public void A_missing_output_is_an_output_mismatch()
    {
        var ex = FluentActions.Invoking(() => Run(Builder().BurstStarted(0).ExpectTimerStart("csma.lbt_wait", 3000)))
            .Should().Throw<T2FailureException>().Which;
        ex.Reason.Should().Be("output_mismatch");
        ex.Message.Should().Contain("mem.jsonl:3");
    }

    [Fact]
    public void An_expiry_for_a_timer_not_running_is_a_bad_expiry()
    {
        FluentActions.Invoking(() => Run(Builder().TimerExpired(100, "csma.lbt_wait")))
            .Should().Throw<T2FailureException>().Which.Reason.Should().Be("bad_expiry");
    }

    [Fact]
    public void Time_going_backwards_is_a_missed_timer()
    {
        FluentActions.Invoking(() => Run(Builder().BurstStarted(100).BurstEnded(50)))
            .Should().Throw<T2FailureException>().Which.Reason.Should().Be("missed_timer");
    }

    [Fact]
    public void A_null_setting_the_core_needs_is_a_missing_setting()
    {
        FluentActions.Invoking(() => Run(Builder(new JsonObject()).BurstStarted(0)))
            .Should().Throw<T2FailureException>().Which.Reason.Should().Be("missing_setting");
        FluentActions.Invoking(() => Run(Builder().Config(10, new JsonObject { ["local_address"] = null })))
            .Should().Throw<T2FailureException>().Which.Reason.Should().Be("missing_setting");
    }

    [Fact]
    public void Unknown_inputs_outputs_and_keys_are_unsupported_not_passed()
    {
        Run(Builder().In(0, "station_hook")).Unsupported.Should().Contain("station_hook");
        Run(Builder().In(0, "burst_started", new JsonObject { ["extra"] = 1 })).Unsupported.Should().Contain("extra");
        Run(Builder().BurstStarted(0).Out("hook_granted")).Unsupported.Should().Contain("hook_granted");
    }

    [Fact]
    public void An_unknown_kind_late_in_the_file_makes_the_scenario_unsupported_even_after_a_mismatch()
    {
        Run(Builder().BurstStarted(0).ExpectTimerStart("csma.lbt_wait", 3000).In(10, "station_hook"))
            .Unsupported.Should().Contain("station_hook");
    }

    [Fact]
    public void An_expiry_for_a_name_the_core_does_not_know_is_a_bad_expiry()
    {
        FluentActions.Invoking(() => Run(Builder().TimerExpired(100, "station.id_hook")))
            .Should().Throw<T2FailureException>().Which.Reason.Should().Be("bad_expiry");
    }

    [Fact]
    public void Byte_fields_compare_as_bytes_whatever_the_hex_case()
    {
        JsonNode.DeepEquals(
            TraceJson.Normalise(new JsonObject { ["out"] = "sis_bytes", ["conn"] = 1, ["bytes"] = "90EB" }),
            TraceJson.Normalise(new JsonObject { ["conn"] = 1, ["out"] = "sis_bytes", ["bytes"] = "90eb" })).Should().BeTrue();
    }

    [Fact]
    public void Outputs_convert_to_the_corpus_shapes()
    {
        TraceJson.ToJson(new TimerStart(TimerName.ForPeer(TimerKind.DtsAckWait, 0x0102), 2500)).ToJsonString()
            .Should().Be("""{"out":"timer_start","name":"dts.ack_wait/0.0.1.2","ms":2500}""");
        TraceJson.ToJson(new Transmit(new byte[] { 0x90, 0xEB }, 6, Interleaver.VeryShort, 7, true)).ToJsonString()
            .Should().Be("""{"out":"transmit","bytes":"90eb","wn":6,"interleaver":"very_short","k":7,"eom":true}""");
        TraceJson.ToJson(new LinkState(0x0F0000FF, LinkStateName.Linked)).ToJsonString()
            .Should().Be("""{"out":"link_state","peer":"15.0.0.255","state":"linked"}""");
        TraceJson.ToJson(new SisClose(3)).ToJsonString().Should().Be("""{"out":"sis_close","conn":3}""");
    }
}

/// <summary>
/// Determinism (core-architecture.md section 6): the same scenario run twice gives byte-identical
/// transcripts, and two cores interleaved in one process behave as if run alone.
/// </summary>
public class DeterminismTests
{
    public static IEnumerable<TheoryDataRow<string>> Scenarios() =>
        Corpus.T2Files.Select(f => new TheoryDataRow<string>(Path.GetRelativePath(Corpus.Root, f).Replace('\\', '/')));

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void A_corpus_scenario_run_twice_gives_byte_identical_transcripts(string file)
    {
        string[] lines = File.ReadAllLines(Path.Combine(Corpus.Root, file));
        byte[] first = Transcript(T2Runner.Run(file, lines, Corpus.T2Defaults));
        byte[] second = Transcript(T2Runner.Run(file, lines, Corpus.T2Defaults));
        first.Should().NotBeEmpty();
        second.Should().Equal(first);
    }

    // Outputs from a run through the full runtime with timers, primitives, a PRNG draw and a
    // transmit (probe sublayers, since the P3-0 sublayers are stubs): twice, compared byte for byte.
    [Fact]
    public void A_scripted_run_with_timers_and_draws_is_byte_identical_twice()
    {
        ScriptedRun(0x0123456789abcdef).Should().Equal(ScriptedRun(0x0123456789abcdef));
        ScriptedRun(0x0123456789abcdef).Should().NotEqual(ScriptedRun(1), "a different seed draws different slots");
    }

    [Fact]
    public void Two_nodes_interleaved_match_each_run_alone()
    {
        CoreInput[] inputs =
        [
            new BurstStarted(0), new BytesReceived(10, new byte[] { 0x90, 0xEB }),
            new BurstEnded(20, BurstEndReason.SignalLost, 2, Interleaver.Long, 7, null, 0), new ChannelBusy(30, true),
            new ConfigInput(40, TestConfig.Basic(CsmaMode.Ed3)), new SisConnect(50, 1),
        ];
        string Alone(ulong seed)
        {
            var n = new Node(TestConfig.Basic(), seed, new NullModem());
            var sb = new StringBuilder();
            foreach (CoreInput i in inputs)
            {
                n.Handle(i);
                n.DrainOutputs().ForEach(o => sb.AppendLine(TraceJson.ToJson(o).ToJsonString()));
            }

            return sb.ToString();
        }

        var a = new Node(TestConfig.Basic(), 7, new NullModem());
        var b = new Node(TestConfig.Basic(), 8, new NullModem());
        var sa = new StringBuilder();
        var sb2 = new StringBuilder();
        foreach (CoreInput i in inputs)
        {
            a.Handle(i);
            b.Handle(i);
            a.DrainOutputs().ForEach(o => sa.AppendLine(TraceJson.ToJson(o).ToJsonString()));
            b.DrainOutputs().ForEach(o => sb2.AppendLine(TraceJson.ToJson(o).ToJsonString()));
        }

        sa.ToString().Should().Be(Alone(7));
        sb2.ToString().Should().Be(Alone(8));
    }

    private static byte[] Transcript(T2Result r) => Encoding.UTF8.GetBytes(string.Join('\n', r.Transcript) + "\n");

    private static byte[] ScriptedRun(ulong seed)
    {
        var rig = new ProbeRig();
        var prng = new SplitMix64(seed);
        TimerName cont = TimerName.Of(TimerKind.CsmaContWait);
        rig.Sis.Reactions["OnBytes"] = () => rig.Context.Enqueue(new ProbeToCas("req"));
        rig.Cas.Reactions["On:req"] = () => rig.Context.Enqueue(new ProbeToDts("queue"));
        rig.Dts.Reactions["On:queue"] = () =>
        {
            rig.Context.Timers.Start(TimerName.ForPeer(TimerKind.DtsAckWait, 0x0203), 9000);
            rig.Context.Timers.Start(TimerName.ForPeer(TimerKind.DtsAckWait, 0x0102), 9000);
            rig.Context.Emit(new SisBytesOut(1, new byte[] { 0x90, 0xEB, 0x00, (byte)rig.Context.AllocateHandle() }));
        };
        bool contExpired = false;
        rig.Mac.Reactions["AfterInput"] = () =>
        {
            if (rig.Context.Now == 100 && !rig.Context.Timers.StartUnlessZero(cont, (long)prng.Uniform(16) * 3000))
            {
                rig.Context.Emit(new Transmit(new byte[] { 1, 2, 3 }, 2, Interleaver.Long, 7, true));
            }

            if (contExpired)
            {
                contExpired = false;
                rig.Context.Emit(new Transmit(new byte[] { 4 }, 2, Interleaver.Long, 7, true));
            }
        };

        // The MAC's OnTimer only records the fact; AfterInput acts on it (core-architecture.md 3.5 rule 4).
        rig.Mac.Reactions["OnTimer:csma.cont_wait"] = () => contExpired = true;

        var sb = new StringBuilder();
        rig.Dispatcher.Handle(new SisBytes(0, 1, new byte[] { 0 }));
        rig.Dispatcher.Handle(new SisBytes(100, 1, new byte[] { 0 }));
        long? due = rig.Context.Timers.Deadline(cont);
        if (due is long d)
        {
            rig.Dispatcher.Handle(new TimerExpired(d, cont));
        }

        rig.Outputs().ForEach(o => sb.AppendLine(TraceJson.ToJson(o).ToJsonString()));
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
