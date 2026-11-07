using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Core.Tests;

public class TimerNameTests
{
    [Fact]
    public void Every_planned_kind_has_its_trace_name_and_owner()
    {
        var expected = new (TimerKind Kind, string Name, string Owner)[]
        {
            (TimerKind.CsmaLbtWait, "csma.lbt_wait", "Mac"), (TimerKind.CsmaContWait, "csma.cont_wait", "Mac"),
            (TimerKind.CsmaVdcdEnd, "csma.vdcd_end", "Mac"), (TimerKind.DtsAckWait, "dts.ack_wait", "Dts"),
            (TimerKind.DtsManagementWait, "dts.management_wait", "Dts"), (TimerKind.DtsTtd, "dts.ttd", "Dts"),
            (TimerKind.DtsReceptionWindow, "dts.reception_window", "Dts"), (TimerKind.CasLinkRequest, "cas.link_request", "Cas"),
            (TimerKind.CasFirstData, "cas.first_data", "Cas"), (TimerKind.CasBreak, "cas.break", "Cas"),
            (TimerKind.CasCleanClose, "cas.clean_close", "Cas"), (TimerKind.SisConfirmWait, "sis.confirm_wait", "Sis"),
            (TimerKind.SisTtd, "sis.ttd", "Sis"), (TimerKind.SisSoftLinkIdle, "sis.soft_link_idle", "Sis"),
            (TimerKind.SisSoftLinkMinHold, "sis.soft_link_min_hold", "Sis"), (TimerKind.SisSoftLinkMax, "sis.soft_link_max", "Sis"),
        };
        expected.Should().HaveCount(Enum.GetValues<TimerKind>().Length);
        foreach (var (kind, name, owner) in expected)
        {
            TimerName.BaseName(kind).Should().Be(name);
            new TimerName(kind, TimerName.Qualifier(kind) == TimerQualifier.Peer ? 0x0102u : null).Owner.ToString().Should().Be(owner);
        }
    }

    [Theory]
    [InlineData("csma.cont_wait")]
    [InlineData("dts.ack_wait/0.0.1.2")]
    [InlineData("cas.break/15.255.255.255")]
    [InlineData("sis.soft_link_max/0.0.0.0")]
    public void Trace_names_round_trip(string text) => TimerName.Parse(text).ToTraceString().Should().Be(text);

    [Theory]
    [InlineData("csma.nope")]
    [InlineData("dts.ack_wait")]
    [InlineData("csma.cont_wait/0.0.1.2")]
    [InlineData("dts.ack_wait/16.0.0.0")]
    [InlineData("dts.ack_wait/3")]
    [InlineData("dts.ttd/0")]
    public void Malformed_names_are_rejected(string text) => FluentActions.Invoking(() => TimerName.Parse(text)).Should().Throw<FormatException>();

    [Fact]
    public void Peer_address_is_formatted_dotted()
    {
        TimerName.ForPeer(TimerKind.DtsAckWait, 0x0A010203).ToTraceString().Should().Be("dts.ack_wait/10.1.2.3");
    }

    [Fact]
    public void Order_is_kind_then_peer_then_conn_with_absent_first()
    {
        var names = new List<TimerName>
        {
            TimerName.ForPeer(TimerKind.DtsAckWait, 5), TimerName.Of(TimerKind.CsmaVdcdEnd),
            TimerName.ForPeer(TimerKind.DtsAckWait, 2), TimerName.Of(TimerKind.CsmaLbtWait),
            new(TimerKind.DtsAckWait), TimerName.Of(TimerKind.DtsTtd),
        };
        names.Sort();
        names.Should().Equal(
            TimerName.Of(TimerKind.CsmaLbtWait), TimerName.Of(TimerKind.CsmaVdcdEnd), new TimerName(TimerKind.DtsAckWait),
            TimerName.ForPeer(TimerKind.DtsAckWait, 2), TimerName.ForPeer(TimerKind.DtsAckWait, 5), TimerName.Of(TimerKind.DtsTtd));
    }
}

public class TimerTableTests
{
    private static readonly TimerName Lbt = TimerName.Of(TimerKind.CsmaLbtWait);

    private static (CoreContext, TimerTable) Make()
    {
        var ctx = new CoreContext(TestConfig.Basic(), new NullModem());
        return (ctx, ctx.Timers);
    }

    [Fact]
    public void Start_and_restart_each_emit_one_timer_start_with_a_new_deadline()
    {
        var (ctx, timers) = Make();
        ctx.Now = 100;
        timers.Start(Lbt, 3000);
        ctx.Now = 200;
        timers.Start(Lbt, 3000);
        ctx.TakeOutputs().Should().Equal(new TimerStart(Lbt, 3000), new TimerStart(Lbt, 3000));
        timers.Deadline(Lbt).Should().Be(3200);
    }

    [Fact]
    public void Start_rejects_a_zero_or_negative_duration()
    {
        var (_, timers) = Make();
        FluentActions.Invoking(() => timers.Start(Lbt, 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => timers.StartUnlessZero(Lbt, -1)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Zero_duration_starts_nothing_and_emits_nothing()
    {
        var (ctx, timers) = Make();
        timers.StartUnlessZero(Lbt, 0).Should().BeFalse();
        timers.IsRunning(Lbt).Should().BeFalse();
        ctx.TakeOutputs().Should().BeEmpty();
        timers.StartUnlessZero(Lbt, 1).Should().BeTrue();
        ctx.TakeOutputs().Should().Equal(new TimerStart(Lbt, 1));
    }

    [Fact]
    public void Zero_duration_on_a_running_timer_is_a_bug()
    {
        var (_, timers) = Make();
        timers.Start(Lbt, 10);
        FluentActions.Invoking(() => timers.StartUnlessZero(Lbt, 0)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancel_emits_only_for_a_running_timer()
    {
        var (ctx, timers) = Make();
        timers.Cancel(Lbt);
        ctx.TakeOutputs().Should().BeEmpty();
        timers.Start(Lbt, 10);
        timers.Cancel(Lbt);
        timers.Cancel(Lbt);
        ctx.TakeOutputs().Should().Equal(new TimerStart(Lbt, 10), new TimerCancel(Lbt));
    }

    [Fact]
    public void Expire_checks_the_deadline_and_removes_the_timer()
    {
        var (ctx, timers) = Make();
        timers.Start(Lbt, 10);
        FluentActions.Invoking(() => timers.Expire(Lbt, 9)).Should().Throw<InvalidOperationException>();
        timers.Expire(Lbt, 10);
        timers.IsRunning(Lbt).Should().BeFalse();
        FluentActions.Invoking(() => timers.Expire(Lbt, 10)).Should().Throw<InvalidOperationException>();
        ctx.TakeOutputs().Should().Equal(new TimerStart(Lbt, 10));
    }

    [Fact]
    public void A_name_with_the_wrong_qualifier_cannot_be_started()
    {
        var (_, timers) = Make();
        FluentActions.Invoking(() => timers.Start(new TimerName(TimerKind.DtsAckWait), 5)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => timers.Start(new TimerName(TimerKind.CsmaLbtWait, 7), 5)).Should().Throw<InvalidOperationException>();
    }
}

public class DispatcherTests
{
    [Fact]
    public void Entry_order_follows_the_table_and_the_mac_acts_last_once_per_input()
    {
        var rig = new ProbeRig();
        rig.Dispatcher.Handle(new ConfigInput(0, TestConfig.Basic(CsmaMode.Jitter)));
        rig.Dispatcher.Handle(new BurstStarted(1));
        rig.Dispatcher.Handle(new BytesReceived(2, new byte[] { 1 }));
        rig.Dispatcher.Handle(new BurstEnded(3, BurstEndReason.Eom, 2, Interleaver.Long, 7, null, 1));
        rig.Dispatcher.Handle(new TransmitDone(4));
        rig.Dispatcher.Handle(new ChannelBusy(5, true));
        rig.Dispatcher.Handle(new SisConnect(6, 1));
        rig.Dispatcher.Handle(new SisBytes(6, 1, new byte[] { 2 }));
        rig.Dispatcher.Handle(new SisDisconnect(7, 1));
        rig.Log.Should().Equal(
            "sis.OnConfigChanged", "cas.OnConfigChanged", "dts.OnConfigChanged", "mac.OnConfigChanged", "mac.AfterInput",
            "dts.OnBurstStarted", "mac.OnBurstStarted", "mac.AfterInput",
            "dts.OnBytesReceived", "mac.AfterInput",
            "dts.OnBurstEnded", "mac.OnBurstEnded", "mac.AfterInput",
            "dts.OnTransmitDone", "mac.OnTransmitDone", "mac.AfterInput",
            "mac.OnChannelBusy", "mac.AfterInput",
            "sis.OnConnect", "mac.AfterInput",
            "sis.OnBytes", "mac.AfterInput",
            "sis.OnDisconnect", "mac.AfterInput");
        rig.Context.Config.Csma.Mode.Should().Be(CsmaMode.Jitter);
    }

    [Fact]
    public void Primitives_are_delivered_in_fifo_order_after_the_entry_calls()
    {
        var rig = new ProbeRig();
        rig.Sis.Reactions["OnBytes"] = () =>
        {
            rig.Context.Enqueue(new ProbeToCas("a"));
            rig.Context.Enqueue(new ProbeToCas("b"));
        };
        rig.Cas.Reactions["On:a"] = () => rig.Context.Enqueue(new ProbeToDts("a1"));
        rig.Cas.Reactions["On:b"] = () => rig.Context.Enqueue(new ProbeToSis("b1"));
        rig.Dts.Reactions["On:a1"] = () => rig.Context.Enqueue(new ProbeToCas("a2"));
        rig.Dispatcher.Handle(new SisBytes(0, 1, new byte[] { 1 }));
        rig.Log.Should().Equal("sis.OnBytes", "cas.On:a", "cas.On:b", "dts.On:a1", "sis.On:b1", "cas.On:a2", "mac.AfterInput");
    }

    [Fact]
    public void The_mac_may_drain_the_fifo_during_after_input_and_only_then()
    {
        var rig = new ProbeRig();
        rig.Mac.Reactions["AfterInput"] = () =>
        {
            rig.Context.Enqueue(new ProbeToSis("rejected"));
            rig.Context.DrainPrimitives();
            rig.Log.Add("mac.rereads-queue");
        };
        rig.Dispatcher.Handle(new BurstStarted(0));
        rig.Log.Should().Equal("dts.OnBurstStarted", "mac.OnBurstStarted", "mac.AfterInput", "sis.On:rejected", "mac.rereads-queue");

        var rig2 = new ProbeRig();
        rig2.Sis.Reactions["OnConnect"] = () => rig2.Context.DrainPrimitives();
        FluentActions.Invoking(() => rig2.Dispatcher.Handle(new SisConnect(0, 1))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_primitive_left_after_the_mac_acted_is_a_bug()
    {
        var rig = new ProbeRig();
        rig.Mac.Reactions["AfterInput"] = () => rig.Context.Enqueue(new ProbeToSis("late"));
        FluentActions.Invoking(() => rig.Dispatcher.Handle(new BurstStarted(0))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Calls_that_only_record_facts_may_not_emit_or_enqueue()
    {
        var rig = new ProbeRig();
        rig.Mac.Reactions["OnBurstStarted"] = () => rig.Context.Timers.Start(TimerName.Of(TimerKind.CsmaVdcdEnd), 10);
        FluentActions.Invoking(() => rig.Dispatcher.Handle(new BurstStarted(0))).Should().Throw<InvalidOperationException>();

        var rig2 = new ProbeRig();
        rig2.Dts.Reactions["OnConfigChanged"] = () => rig2.Context.Enqueue(new ProbeToCas("x"));
        FluentActions.Invoking(() => rig2.Dispatcher.Handle(new ConfigInput(0, TestConfig.Basic())))
            .Should().Throw<InvalidOperationException>();

        var rig3 = new ProbeRig();
        TimerName lbt = TimerName.Of(TimerKind.CsmaLbtWait);
        rig3.Context.Timers.Start(lbt, 5);
        rig3.Mac.Reactions["OnTimer:csma.lbt_wait"] = () => rig3.Context.Emit(new TerminateReceive());
        FluentActions.Invoking(() => rig3.Dispatcher.Handle(new TimerExpired(5, lbt))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Time_may_not_go_backwards()
    {
        var rig = new ProbeRig();
        rig.Dispatcher.Handle(new BurstStarted(10));
        rig.Context.Now.Should().Be(10);
        FluentActions.Invoking(() => rig.Dispatcher.Handle(new BurstEnded(9, BurstEndReason.Eom, 2, Interleaver.Long, 7, null, 1)))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_timer_expiry_removes_the_timer_then_goes_to_its_owner()
    {
        var rig = new ProbeRig();
        TimerName ack = TimerName.ForPeer(TimerKind.DtsAckWait, 0x0102);
        TimerName lbt = TimerName.Of(TimerKind.CsmaLbtWait);
        TimerName confirm = TimerName.Of(TimerKind.SisConfirmWait);
        TimerName link = TimerName.ForPeer(TimerKind.CasLinkRequest, 9);
        rig.Context.Timers.Start(ack, 100);
        rig.Context.Timers.Start(lbt, 100);
        rig.Context.Timers.Start(confirm, 100);
        rig.Context.Timers.Start(link, 100);
        rig.Dts.Reactions["OnTimer:dts.ack_wait/0.0.1.2"] = () => rig.Context.Timers.IsRunning(ack).Should().BeFalse();
        foreach (TimerName n in new[] { ack, lbt, confirm, link })
        {
            rig.Dispatcher.Handle(new TimerExpired(100, n));
        }

        rig.Log.Should().Equal(
            "dts.OnTimer:dts.ack_wait/0.0.1.2", "mac.AfterInput", "mac.OnTimer:csma.lbt_wait", "mac.AfterInput",
            "sis.OnTimer:sis.confirm_wait", "mac.AfterInput", "cas.OnTimer:cas.link_request/0.0.0.9", "mac.AfterInput");
        FluentActions.Invoking(() => rig.Dispatcher.Handle(new TimerExpired(100, ack))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Handles_are_allocated_from_one_counter()
    {
        var rig = new ProbeRig();
        rig.Context.AllocateHandle().Should().Be(1);
        rig.Context.AllocateHandle().Should().Be(2);
    }

    // The zero-duration rule (core-architecture.md 3.3): SENSE -> LBT_WAIT (0) -> CONT_WAIT (0) ->
    // transmit inside one input, with the jitter draw made on the way. The probe MAC is a sketch
    // of the shape P3-2 fills in, not Annex K itself.
    [Fact]
    public void Zero_waits_chain_within_one_input_and_a_non_zero_draw_starts_a_timer()
    {
        ulong zeroSeed = Enumerable.Range(0, 1000).Select(s => (ulong)s).First(s => new SplitMix64(s).Uniform(16) == 0);
        ZeroChain(zeroSeed).Should().Equal(new Transmit(Array.Empty<byte>(), 2, Interleaver.Long, 7, true));

        // Seed 0: the first uniform(16) is 15 (prng.md 5), so CONT_WAIT runs 15 slots.
        ZeroChain(0).Should().Equal(new TimerStart(TimerName.Of(TimerKind.CsmaContWait), 45000));
    }

    private static List<CoreOutput> ZeroChain(ulong seed)
    {
        var rig = new ProbeRig();
        var prng = new SplitMix64(seed);
        rig.Mac.Reactions["AfterInput"] = () =>
        {
            TimerTable t = rig.Context.Timers;
            if (!t.StartUnlessZero(TimerName.Of(TimerKind.CsmaLbtWait), 0))
            {
                long slot = (long)prng.Uniform(16);
                if (!t.StartUnlessZero(TimerName.Of(TimerKind.CsmaContWait), slot * 3000))
                {
                    rig.Context.Emit(new Transmit(Array.Empty<byte>(), 2, Interleaver.Long, 7, true));
                }
            }
        };
        rig.Dispatcher.Handle(new BurstEnded(0, BurstEndReason.Eom, 2, Interleaver.Long, 7, null, 1));
        return rig.Outputs();
    }
}

public class NodeTests
{
    [Fact]
    public void A_new_node_is_in_sense_with_no_outputs_and_ignores_traffic_while_nothing_is_queued()
    {
        var node = new Node(TestConfig.Basic(), 0, new NullModem());
        node.DrainOutputs().Should().BeEmpty();
        node.Handle(new BurstStarted(0));
        node.Handle(new BytesReceived(10, new byte[] { 0x90, 0xEB, 0x00 }));
        node.Handle(new BurstEnded(4000, BurstEndReason.Eom, 2, Interleaver.Long, 7, 120, 1));
        node.Handle(new ChannelBusy(5000, true));
        node.Now.Should().Be(5000);
        node.DrainOutputs().Should().BeEmpty();
    }

    [Fact]
    public void A_null_csma_mode_is_not_needed_by_an_idle_node()
    {
        var config = TestConfig.Basic(mode: null);
        config.Csma.Mode.Should().BeNull();
        FluentActions.Invoking(() => config.Csma.RequireMode()).Should().Throw<MissingSettingException>().Which.Setting.Should().Be("csma.mode");
        var node = new Node(config, 0, new NullModem());
        node.Handle(new BurstStarted(0));
        node.DrainOutputs().Should().BeEmpty();
    }

    [Fact]
    public void A_config_change_replaces_the_configuration_and_emits_nothing()
    {
        var node = new Node(TestConfig.Basic(), 0, new NullModem());
        node.Handle(new ConfigInput(0, TestConfig.Basic(CsmaMode.Slotted) with { LocalAddress = 0x0103 }));
        node.DrainOutputs().Should().BeEmpty();
    }

    [Fact]
    public void Two_nodes_share_no_state()
    {
        var a = new Node(TestConfig.Basic(), 1, new NullModem());
        var b = new Node(TestConfig.Basic(), 2, new NullModem());
        a.Handle(new BurstStarted(100));
        b.Now.Should().Be(0);
        b.Handle(new BurstStarted(50));
        a.Now.Should().Be(100);
    }
}

public class SeqSpaceTests
{
    [Fact]
    public void Add_wraps_both_ways()
    {
        SeqSpace.Mod256.Add(250, 10).Should().Be(4);
        SeqSpace.Mod256.Add(3, -5).Should().Be(254);
        SeqSpace.Mod256.Add(3, -256).Should().Be(3);
        SeqSpace.Mod256.Add(3, -512 - 4).Should().Be(255);
        SeqSpace.Mod65536.Add(65535, 1).Should().Be(0);
        SeqSpace.Mod65536.Add(0, int.MinValue).Should().Be(0);
    }

    [Fact]
    public void Distance_is_forward_and_non_negative()
    {
        SeqSpace.Mod256.Distance(250, 4).Should().Be(10);
        SeqSpace.Mod256.Distance(4, 250).Should().Be(246);
        SeqSpace.Mod256.Distance(7, 7).Should().Be(0);
        SeqSpace.Mod65536.Distance(65535, 0).Should().Be(1);
    }

    [Fact]
    public void Out_of_range_values_are_bugs()
    {
        FluentActions.Invoking(() => SeqSpace.Mod256.Add(256, 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => SeqSpace.Mod256.Distance(-1, 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => default(SeqSpace).Distance(0, 0)).Should().Throw<InvalidOperationException>();
    }

    // C-0786: arcs measured forward from the lower bound, including across the wrap.
    [Fact]
    public void Arc_test_follows_the_c0786_reading()
    {
        SeqSpace s = SeqSpace.Mod256;
        s.InArc(5, 3, 10).Should().BeTrue();
        s.InArc(3, 3, 10).Should().BeTrue();
        s.InArc(10, 3, 10).Should().BeTrue();
        s.InArc(11, 3, 10).Should().BeFalse();
        s.InArc(2, 3, 10).Should().BeFalse();
        s.InArc(1, 250, 5).Should().BeTrue();
        s.InArc(249, 250, 5).Should().BeFalse();
        s.InArc(3, 3, 10, lowerExclusive: true).Should().BeFalse();
        s.InArc(4, 3, 10, lowerExclusive: true).Should().BeTrue();
    }

    // C-0786's example: Equation 1 at start-up accepts TX UWE from RX UWE = M - 1 forward to
    // RX LWE + MAX WIN SIZE - 1. With M = 65536, RX LWE = 0 and a window of 32768 the arc is 65535 to 32767.
    [Fact]
    public void Equation_1_at_start_up_uses_its_arc_as_written()
    {
        SeqSpace s = SeqSpace.Mod65536;
        int upper = s.Add(0, 32768 - 1);
        s.InArc(65535, 65535, upper).Should().BeTrue();
        s.InArc(0, 65535, upper).Should().BeTrue();
        s.InArc(32767, 65535, upper).Should().BeTrue();
        s.InArc(32768, 65535, upper).Should().BeFalse();
        s.InArc(65534, 65535, upper).Should().BeFalse();
    }
}
