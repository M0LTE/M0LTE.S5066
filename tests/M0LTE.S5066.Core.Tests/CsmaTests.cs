using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// A stand-in for the thin SIS of P3-1, used only until that item lands, so the Annex K scenarios
/// under <c>t2/csma</c> can run against the real MAC now. It answers S_BIND_REQUEST with
/// S_BIND_ACCEPTED (MTU 2048, A-0047) and treats S_UNIDATA_REQUEST as data queued for
/// transmission (K-0096), which in the real node the DTS reports through
/// <see cref="IMacObserver.QueueState"/>. Nothing else.
/// </summary>
internal sealed class StandInSis(CoreContext context, IMacObserver mac) : ISisSublayer
{
    private readonly SortedDictionary<int, SisFrameDecoder> _decoders = new();

    public void OnConfigChanged(NodeConfig oldConfig, NodeConfig newConfig)
    {
    }

    public void OnTimer(TimerName name) => throw new InvalidOperationException($"stand-in SIS has no timer {name}");

    public void OnConnect(int conn) => _decoders[conn] = new SisFrameDecoder();

    public void OnBytes(int conn, ReadOnlyMemory<byte> bytes)
    {
        SisFrameDecoder decoder = _decoders[conn];
        decoder.Push(bytes.Span);
        while (decoder.TryRead(out SisDecodeResult r))
        {
            switch (r.Primitive)
            {
                case SisBindRequest b:
                    context.Emit(new SisBytesOut(conn, SisCodec.Encode(new SisBindAccepted(b.Sap, 2048))));
                    break;
                case SisUnidataRequest:
                    mac.QueueState(true);
                    break;
                default:
                    throw new InvalidOperationException($"stand-in SIS does not handle {r.Primitive?.GetType().Name ?? r.Error.ToString()}");
            }
        }
    }

    public void OnDisconnect(int conn) => _decoders.Remove(conn);

    public void On(ToSis primitive) => throw new InvalidOperationException($"stand-in SIS got {primitive.GetType().Name}");
}

/// <summary>Which corpus scenarios need the P3-1 SIS, and the stand-in core that runs them until then.</summary>
internal static class AwaitingSis
{
    /// <summary>
    /// True for the Annex K scenarios, which queue data through S_BIND_REQUEST and
    /// S_UNIDATA_REQUEST. Remove this (and its callers' branches) when P3-1 is merged: the real
    /// node must then pass them unchanged.
    /// </summary>
    public static bool Applies(string relativePath) => relativePath.StartsWith("t2/csma/", StringComparison.Ordinal);

    /// <summary>The real CAS, DTS and MAC with the stand-in SIS.</summary>
    public static CoreDriver Core(NodeConfig config, ulong seed, IModemPort modem)
    {
        var context = new CoreContext(config, modem);
        var mac = new MacSublayer(context, new SplitMix64(seed));
        var dts = new DtsSublayer(context, mac);
        var dispatcher = new Dispatcher(context, new StandInSis(context, mac), new CasSublayer(context), dts, mac);
        return new CoreDriver(dispatcher.Handle, context.TakeOutputs);
    }
}

/// <summary>The <c>t2/csma</c> corpus scenarios against the real MAC, with the stand-in SIS (P3-2).</summary>
public class CsmaScenarioTests
{
    public static IEnumerable<TheoryDataRow<string>> Scenarios() =>
        Corpus.T2Files.Select(f => Path.GetRelativePath(Corpus.Root, f).Replace('\\', '/'))
            .Where(AwaitingSis.Applies)
            .Select(f => new TheoryDataRow<string>(f));

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Scenario_passes_with_the_stand_in_sis(string file)
    {
        string[] lines = File.ReadAllLines(Path.Combine(Corpus.Root, file));
        T2Result result = T2Runner.Run(file, lines, Corpus.T2Defaults, AwaitingSis.Core);
        result.Unsupported.Should().BeNull();
    }
}

/// <summary>
/// Annex K behaviour a phase 3 trace cannot reach yet: LINKING and own transmissions need the
/// planner (P3-4), and an emptied queue needs TTL expiry (P3-5). The real MAC with probe SIS, CAS
/// and DTS; data is queued by calling the observer as the DTS would.
/// </summary>
public class MacTests
{
    private static readonly TimerName Lbt = TimerName.Of(TimerKind.CsmaLbtWait);
    private static readonly TimerName Cont = TimerName.Of(TimerKind.CsmaContWait);

    [Fact]
    public void Nothing_queued_stays_in_sense_whatever_the_channel_does()
    {
        var rig = new MacRig(CsmaMode.Jitter);
        rig.Handle(new BurstStarted(0)).Should().BeEmpty();
        rig.Handle(new BurstEnded(1000, BurstEndReason.Eom, 2, Interleaver.Long, 7, null, 1)).Should().BeEmpty();
        rig.Mac.State.Should().Be(MacState.Sense);
    }

    [Fact]
    public void Jitter_slot_0_chains_from_lbt_expiry_to_linking_in_one_input()
    {
        // K-0080 with a draw of 0, K-0090: the zero contention wait starts no timer
        // (core-architecture.md 3.3). Seed 0 draws 15, 4, 15, 12, 11, 10, 1, ... so pick a seed
        // whose first uniform(16) is 0.
        ulong seed = SeedWithFirstDraw(16, 0);
        var rig = new MacRig(CsmaMode.Jitter, seed: seed);
        rig.Queue(0).Should().Equal(new TimerStart(Lbt, 30000));
        rig.Handle(new TimerExpired(30000, Lbt)).Should().BeEmpty();
        rig.Mac.State.Should().Be(MacState.Linking);
        rig.Linked.Should().Be(1);
        rig.Mac.PrngDraws.Should().Be(1);
    }

    [Fact]
    public void Slotted_position_1_after_another_nodes_transmission_waits_zero_slots()
    {
        // K-0081: (1 - 1) * width = 0, straight to LINKING; slotted never draws.
        var rig = new MacRig(CsmaMode.Slotted, position: 1);
        rig.Handle(new BurstStarted(0));
        rig.Queue(1000).Should().BeEmpty();
        rig.Handle(new BurstEnded(5000, BurstEndReason.Eom, 2, Interleaver.Long, 7, null, 1)).Should().Equal(new TimerStart(Lbt, 30000));
        rig.Handle(new TimerExpired(35000, Lbt)).Should().BeEmpty();
        rig.Mac.State.Should().Be(MacState.Linking);
        rig.Mac.PrngDraws.Should().Be(0);
    }

    [Fact]
    public void Slotted_after_own_transmission_waits_position_slots_and_vdcd_lbt()
    {
        // K-0074: our own end is VDCD-determined, so lbt_wait_vdcd_ms. K-0082, K-0086: the last
        // transmission was ours, so position * width rather than (position - 1) * width.
        var rig = new MacRig(CsmaMode.Slotted, position: 1);
        rig.Queue(0).Should().Equal(new TimerStart(Lbt, 30000));
        rig.Handle(new TimerExpired(30000, Lbt)).Should().BeEmpty();
        rig.Linked.Should().Be(1);
        rig.Handle(new TransmitDone(40000)).Should().Equal(new TimerStart(Lbt, 3000));
        rig.Handle(new TimerExpired(43000, Lbt)).Should().Equal(new TimerStart(Cont, 3000));
        rig.Handle(new TimerExpired(46000, Cont)).Should().BeEmpty();
        rig.Linked.Should().Be(2);
    }

    [Fact]
    public void A_burst_heard_after_own_transmission_makes_the_last_transmission_someone_elses()
    {
        // K-0086: any busy period since our own transmission ended.
        var rig = new MacRig(CsmaMode.Slotted, position: 2);
        rig.Queue(0).Should().Equal(new TimerStart(Lbt, 30000));
        rig.Handle(new TimerExpired(30000, Lbt)).Should().Equal(new TimerStart(Cont, 3000));
        rig.Handle(new TimerExpired(33000, Cont)).Should().BeEmpty();
        rig.Handle(new TransmitDone(40000)).Should().Equal(new TimerStart(Lbt, 3000));
        rig.Handle(new BurstStarted(41000)).Should().Equal(new TimerCancel(Lbt));
        rig.Handle(new BurstEnded(42000, BurstEndReason.Eom, 2, Interleaver.Long, 7, null, 1)).Should().Equal(new TimerStart(Lbt, 30000));
        rig.Handle(new TimerExpired(72000, Lbt)).Should().Equal(new TimerStart(Cont, 3000));
    }

    [Fact]
    public void Ed3_uses_the_dcd_value_after_own_transmission_and_jitter_contention()
    {
        // K-0107: one common LBT value, lbt_wait_dcd_ms; jitter contention, no own-last rule (K-0085).
        var rig = new MacRig(CsmaMode.Ed3, seed: SeedWithFirstDraw(16, 0));
        rig.Queue(0).Should().Equal(new TimerStart(Lbt, 30000));
        rig.Handle(new TimerExpired(30000, Lbt)).Should().BeEmpty();
        rig.Handle(new TransmitDone(40000)).Should().Equal(new TimerStart(Lbt, 30000));
    }

    [Fact]
    public void Jitter_after_own_transmission_uses_the_vdcd_value_and_the_jitter_rule()
    {
        // K-0074, K-0085: jitter has no own-last-transmission rule.
        var rig = new MacRig(CsmaMode.Jitter, seed: SeedWithFirstDraw(16, 0));
        rig.Queue(0);
        rig.Handle(new TimerExpired(30000, Lbt));
        rig.Handle(new TransmitDone(40000)).Should().Equal(new TimerStart(Lbt, 3000));
        var prng = new SplitMix64(SeedWithFirstDraw(16, 0));
        prng.Uniform(16);
        long draw = (long)prng.Uniform(16);
        CoreOutput[] expected = draw == 0 ? Array.Empty<CoreOutput>() : [new TimerStart(Cont, draw * 3000)];
        rig.Handle(new TimerExpired(43000, Lbt)).Should().Equal(expected);
    }

    [Fact]
    public void An_empty_queue_in_lbt_wait_forces_sense()
    {
        // K-0078.
        var rig = new MacRig(CsmaMode.Jitter);
        rig.Queue(0).Should().Equal(new TimerStart(Lbt, 30000));
        rig.Mac.QueueState(false);
        rig.Handle(new ChannelBusy(1000, false)).Should().Equal(new TimerCancel(Lbt));
        rig.Mac.State.Should().Be(MacState.Sense);
    }

    [Fact]
    public void An_empty_queue_in_lbt_wait_held_by_busy_forces_sense_with_no_output()
    {
        // K-0078 while the timer is held (K-0087): nothing is running to cancel.
        var rig = new MacRig(CsmaMode.Jitter);
        rig.Queue(0);
        rig.Handle(new BurstStarted(1000)).Should().Equal(new TimerCancel(Lbt));
        rig.Mac.QueueState(false);
        rig.Handle(new BurstEnded(2000, BurstEndReason.Eom, 2, Interleaver.Long, 7, null, 1)).Should().BeEmpty();
        rig.Mac.State.Should().Be(MacState.Sense);
    }

    [Fact]
    public void An_empty_queue_in_cont_wait_forces_sense()
    {
        // K-0091.
        var rig = new MacRig(CsmaMode.Jitter);
        rig.Queue(0);
        rig.Handle(new TimerExpired(30000, Lbt)).Should().Equal(new TimerStart(Cont, 45000));
        rig.Mac.QueueState(false);
        rig.Handle(new ChannelBusy(31000, true)).Should().Equal(new TimerCancel(Cont));
        rig.Mac.State.Should().Be(MacState.Sense);
    }

    [Fact]
    public void The_external_busy_input_is_ignored_until_p5_4()
    {
        var rig = new MacRig(CsmaMode.Jitter);
        rig.Handle(new ChannelBusy(0, true)).Should().BeEmpty();
        rig.Queue(1000).Should().Equal(new TimerStart(Lbt, 30000));
    }

    [Fact]
    public void A_zero_lbt_value_chains_into_contention()
    {
        // Zero-duration rule (core-architecture.md 3.3) on the LBT wait, here from configuration.
        var rig = new MacRig(CsmaMode.Jitter, lbtDcdMs: 0);
        rig.Queue(0).Should().Equal(new TimerStart(Cont, 45000));
        rig.Mac.PrngDraws.Should().Be(1);
    }

    [Fact]
    public void Contending_without_a_mode_is_a_missing_setting()
    {
        var rig = new MacRig(null);
        Action act = () => rig.Queue(0);
        act.Should().Throw<MissingSettingException>().Which.Setting.Should().Be("csma.mode");
    }

    [Fact]
    public void Slotted_contention_without_a_position_is_a_missing_setting()
    {
        // K-0104: no default.
        var rig = new MacRig(CsmaMode.Slotted);
        rig.Queue(0);
        Action act = () => rig.Handle(new TimerExpired(30000, Lbt));
        act.Should().Throw<MissingSettingException>().Which.Setting.Should().Be("csma.node_slot_position");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void A_slot_position_outside_1_to_num_cont_slots_is_rejected(int position)
    {
        // K-0037, K-0039, K-0040, K-0104 as configuration validation, at construction and on change.
        Action build = () => _ = new MacRig(CsmaMode.Slotted, position: position);
        build.Should().Throw<ConfigException>().Which.Setting.Should().Be("csma.node_slot_position");

        var rig = new MacRig(CsmaMode.Jitter);
        NodeConfig bad = rig.Context.Config with
        {
            Csma = rig.Context.Config.Csma with { Mode = CsmaMode.Slotted, NodeSlotPosition = position },
        };
        Action change = () => rig.Handle(new ConfigInput(0, bad));
        change.Should().Throw<ConfigException>();
    }

    [Fact]
    public void Linking_without_the_planner_is_not_supported_yet()
    {
        var rig = new MacRig(CsmaMode.Slotted, position: 1, linkingHook: false);
        rig.Queue(0);
        Action act = () => rig.Handle(new TimerExpired(30000, Lbt));
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Transmit_done_outside_linking_is_a_bug()
    {
        var rig = new MacRig(CsmaMode.Jitter);
        Action act = () => rig.Handle(new TransmitDone(0));
        act.Should().Throw<InvalidOperationException>();
    }

    internal static ulong SeedWithFirstDraw(ulong n, ulong value)
    {
        for (ulong seed = 0; ; seed++)
        {
            if (new SplitMix64(seed).Uniform(n) == value)
            {
                return seed;
            }
        }
    }

    private sealed class MacRig
    {
        private readonly Dispatcher _dispatcher;

        public MacRig(CsmaMode? mode, ulong seed = 0, int? position = null, int lbtDcdMs = 30000, bool linkingHook = true)
        {
            var log = new List<string>();
            NodeConfig config = TestConfig.Basic(mode) with
            {
                Csma = new CsmaConfig(mode, 3000, 16, lbtDcdMs, 3000, position),
            };
            Context = new CoreContext(config, new NullModem());
            Mac = new MacSublayer(Context, new SplitMix64(seed));
            if (linkingHook)
            {
                Mac.LinkingEntered = () => Linked++;
            }

            _dispatcher = new Dispatcher(Context, new Probe("sis", log), new Probe("cas", log), new Probe("dts", log), Mac);
        }

        public CoreContext Context { get; }

        public MacSublayer Mac { get; }

        public int Linked { get; private set; }

        /// <summary>Reports data queued (K-0096) and hands the node an input at t, as a SIS request would.</summary>
        public List<CoreOutput> Queue(long t)
        {
            Mac.QueueState(true);
            return Handle(new SisBytes(t, 1, new byte[] { 0 }));
        }

        public List<CoreOutput> Handle(CoreInput input)
        {
            _dispatcher.Handle(input);
            return Context.TakeOutputs();
        }
    }
}
