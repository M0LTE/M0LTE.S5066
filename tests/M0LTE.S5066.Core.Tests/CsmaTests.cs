using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// Annex K behaviour checked against the real MAC with probe SIS, CAS and DTS and a stand-in
/// transmission source: data is queued by calling the observer as the DTS would, and an emptied
/// queue (TTL expiry, P3-5) is set directly. LINKING, the zero-wait chains and own transmissions
/// are covered end to end by the corpus scenarios of P3-4 (csma/jitter-slot-0-chain,
/// csma/slotted-position-1-zero-wait, csma/slotted-own-last-tx, csma/ed3-own-transmission).
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
    public void A_burst_heard_after_own_transmission_makes_the_last_transmission_someone_elses()
    {
        // K-0086: any busy period since our own transmission ended.
        var rig = new MacRig(CsmaMode.Slotted, position: 2);
        rig.Queue(0).Should().Equal(new TimerStart(Lbt, 30000));
        rig.Handle(new TimerExpired(30000, Lbt)).Should().Equal(new TimerStart(Cont, 3000));
        rig.Handle(new TimerExpired(33000, Cont)).Should().Equal(FakeTransmitter.Tx);
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
        rig.Handle(new TimerExpired(30000, Lbt)).Should().Equal(FakeTransmitter.Tx);
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
        CoreOutput[] expected = draw == 0 ? [FakeTransmitter.Tx] : [new TimerStart(Cont, draw * 3000)];
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
    public void The_external_busy_input_keeps_a_queued_node_in_sense_until_it_clears()
    {
        // K-0006, K-0066: ORed into DCD; its clear is a DCD-determined idle, so rule 2 (K-0070).
        var rig = new MacRig(CsmaMode.Jitter);
        rig.Handle(new ChannelBusy(0, true)).Should().BeEmpty();
        rig.Queue(1000).Should().BeEmpty();
        rig.Mac.State.Should().Be(MacState.Sense);
        rig.Handle(new ChannelBusy(2000, false)).Should().Equal(new TimerStart(Lbt, 30000));
    }

    [Fact]
    public void A_repeated_external_busy_value_changes_nothing()
    {
        // K-0006: only a change of the input is a busy indication or an idle.
        var rig = new MacRig(CsmaMode.Jitter);
        rig.Queue(0).Should().Equal(new TimerStart(Lbt, 30000));
        rig.Handle(new ChannelBusy(1000, false)).Should().BeEmpty();
        rig.Handle(new ChannelBusy(2000, true)).Should().Equal(new TimerCancel(Lbt));
        rig.Handle(new ChannelBusy(3000, true)).Should().BeEmpty();
        rig.Handle(new ChannelBusy(4000, false)).Should().Equal(new TimerStart(Lbt, 30000));
        rig.Handle(new ChannelBusy(5000, false)).Should().BeEmpty();
    }

    [Fact]
    public void An_external_busy_period_still_set_when_our_transmission_ends_makes_it_someone_elses()
    {
        // K-0006, K-0086: the busy period goes on after our transmission ended, so the idle it delays
        // is DCD-determined (rule 2, not K-0074's VDCD value) and slotted contention uses position - 1.
        var rig = new MacRig(CsmaMode.Slotted, position: 2);
        rig.Queue(0);
        rig.Handle(new TimerExpired(30000, Lbt)).Should().Equal(new TimerStart(Cont, 3000));
        rig.Handle(new TimerExpired(33000, Cont)).Should().Equal(FakeTransmitter.Tx);
        rig.Handle(new ChannelBusy(36000, true)).Should().BeEmpty();
        rig.Handle(new TransmitDone(38000)).Should().BeEmpty();
        rig.Mac.LastTransmissionOwn.Should().BeFalse();
        rig.Handle(new ChannelBusy(39000, false)).Should().Equal(new TimerStart(Lbt, 30000));
        rig.Handle(new TimerExpired(69000, Lbt)).Should().Equal(new TimerStart(Cont, 3000));
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
    public void A_queue_emptied_by_expiry_on_entry_to_linking_goes_back_to_sense()
    {
        // core-architecture.md 3.5 rule 4, K-0091: ExpireDue runs before the build, and an empty
        // queue then means SENSE with no transmission.
        var rig = new MacRig(CsmaMode.Slotted, position: 1);
        rig.Transmitter.EmptyOnExpire = true;
        rig.Queue(0).Should().Equal(new TimerStart(Lbt, 30000));
        rig.Handle(new TimerExpired(30000, Lbt)).Should().BeEmpty();
        rig.Mac.State.Should().Be(MacState.Sense);
        rig.Transmitter.Builds.Should().Be(0);
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

        public MacRig(CsmaMode? mode, ulong seed = 0, int? position = null, int lbtDcdMs = 30000)
        {
            var log = new List<string>();
            NodeConfig config = TestConfig.Basic(mode) with
            {
                Csma = new CsmaConfig(mode, 3000, 16, lbtDcdMs, 3000, position),
            };
            Context = new CoreContext(config, new NullModem());
            Mac = new MacSublayer(Context, new SplitMix64(seed));
            Transmitter = new FakeTransmitter(Mac);
            Mac.Transmitter = Transmitter;

            _dispatcher = new Dispatcher(Context, new Probe("sis", log), new Probe("cas", log), new Probe("dts", log), Mac);
        }

        public CoreContext Context { get; }

        public MacSublayer Mac { get; }

        public FakeTransmitter Transmitter { get; }

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

    /// <summary>Stands in for the DTS transmit queue: every build returns the same transmission.</summary>
    private sealed class FakeTransmitter(MacSublayer mac) : ITransmissionSource
    {
        public static readonly Transmit Tx = new(new byte[] { 0x90, 0xeb }, 2, Interleaver.Long, 7, true);

        public bool EmptyOnExpire { get; set; }

        public int Builds { get; private set; }

        public void ExpireDue()
        {
            if (EmptyOnExpire)
            {
                mac.QueueState(false);
            }
        }

        public Transmit Build()
        {
            Builds++;
            return Tx;
        }
    }
}
