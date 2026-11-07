using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// SIS soft link sessions (P5-2) on paths the sis-session corpus scenarios do not reach: the
/// caller's idle wait and outstanding requests, a zero idle time, the maximum session time with and
/// without a peer waiting, the minimum hold against a higher priority close, an idle link that is
/// not displaced, U_PDUs that arrive while the SIS closes a link, the reasons carried into
/// rejections, a C_PHYSICAL_LINK_BREAK during a close the peer asked for (B-0450), and the settings.
/// The rig is <see cref="CasLinkTests.Rig"/>: real SIS, CAS and DTS, a probe MAC.
/// </summary>
public class SoftLinkSessionTests
{
    private const uint X = 0x0103;
    private const uint Y = 0x0104;
    private const uint Us = 0x0102;

    private static TimerName Idle(uint p) => TimerName.ForPeer(TimerKind.SisSoftLinkIdle, p);

    private static TimerName MinHold(uint p) => TimerName.ForPeer(TimerKind.SisSoftLinkMinHold, p);

    private static TimerName Max(uint p) => TimerName.ForPeer(TimerKind.SisSoftLinkMax, p);

    private static TimerName First(uint p) => TimerName.ForPeer(TimerKind.CasFirstData, p);

    private static TimerName CleanClose(uint p) => TimerName.ForPeer(TimerKind.CasCleanClose, p);

    private static DExpeditedUnidataIndication Link(uint src, CPdu c) => new(src, Us, false, false, CpduCodec.Encode(c));

    /// <summary>A soft link time a test does not exercise: long enough never to fall due in it.</summary>
    private const int Long = 100_000_000;

    /// <summary>The rig, with the soft link times a test leaves out set out of its way: no minimum hold, idle and maximum <see cref="Long"/>. Their timer outputs are shown.</summary>
    private static CasLinkTests.Rig Rig(int idle = Long, int minHold = 0, int max = Long, int maxLinks = 4) =>
        new(c => c with
        {
            Sis = c.Sis with { SoftLinkIdleMs = idle, SoftLinkMinHoldMs = minHold, SoftLinkMaxMs = max },
            Cas = new CasConfig(maxLinks, 60000, 2, 120000, BreakRetries: 1),
        }, showSisTimers: true);

    /// <summary>X calls us and the link is made: this node is the called node (A-0325).</summary>
    private static void CalledLink(CasLinkTests.Rig rig, uint peer = X)
    {
        rig.Deliver(Link(peer, new PhysicalLinkRequest(false, true, false)));
        rig.Sent(peer);
    }

    /// <summary>We call X for a U_PDU and X accepts: this node is the caller.</summary>
    private static void CallerLink(CasLinkTests.Rig rig, byte confirm = 0, byte priority = 0)
    {
        rig.ArqRequest(X, confirm: confirm, priority: priority);
        rig.Sent(X);
        rig.Deliver(Link(X, new PhysicalLinkAccepted(false, true, false)));
    }

    [Fact]
    public void The_callers_idle_wait_runs_only_while_nothing_is_outstanding()
    {
        // A-0011, A-0320: the released U_PDU is outstanding, so no idle wait and no minimum hold (caller).
        var rig = Rig(idle: 5000, minHold: 60000);
        CallerLink(rig);
        rig.Outputs.Should().NotContain(o => o is TimerStart && ((TimerStart)o).Name.Kind != TimerKind.DtsTtd && ((TimerStart)o).Name.Kind != TimerKind.SisSoftLinkMax);
        CUnidataRequest sent = rig.Sis.Outstanding.Should().ContainSingle().Subject;

        // Confirmed: nothing outstanding, the idle wait starts.
        rig.Deliver(new DUnidataRequestConfirm(sent.Handle));
        rig.Outputs.Should().Contain(new TimerStart(Idle(X), 5000));
        rig.Sis.Outstanding.Should().BeEmpty();

        // A new U_PDU for X goes straight down and stops the idle wait.
        rig.ArqRequest(X);
        rig.Outputs.Should().Contain(new TimerCancel(Idle(X)));

        // Its confirmation starts the wait again, and at expiry the caller closes the link (no minimum hold).
        rig.Deliver(new DUnidataRequestConfirm(rig.Sis.Outstanding.Single().Handle));
        rig.Expire(Idle(X));
        rig.Sis.SoftLinkOf(X).Should().Be("Closing");
        rig.Cas.PhaseOf(X).Should().Be("Breaking");
    }

    [Fact]
    public void A_zero_idle_time_closes_an_idle_link_at_once()
    {
        // A-0013: the zero-duration rule, no timer output.
        var rig = Rig(idle: 0);
        CalledLink(rig);
        rig.Outputs.Should().Equal(
            new LinkState(X, LinkStateName.Linked), new TimerStart(First(X), 120000), new TimerStart(Max(X), Long),
            new TimerCancel(Max(X)), new TimerCancel(First(X)), new LinkState(X, LinkStateName.Breaking));
        rig.Sis.SoftLinkOf(X).Should().Be("Closing");
    }

    [Fact]
    public void Received_arq_data_restarts_the_idle_wait()
    {
        // A-0320.
        var rig = Rig(idle: 5000);
        CalledLink(rig);
        rig.Outputs.Should().Contain(new TimerStart(Idle(X), 5000));
        rig.Context.Timers.Deadline(Idle(X)).Should().Be(5000);
        rig.Deliver(new DUnidataIndication(X, Us, false, true, CpduCodec.Encode(new CpduData(new byte[] { 0x01 }))));
        rig.Outputs.Should().Equal(new TimerCancel(First(X)), new TimerStart(Idle(X), 5000));
    }

    [Fact]
    public void The_maximum_session_time_closes_a_link_only_once_another_peer_waits()
    {
        // A-0014, A-0322: expiry with nobody waiting changes nothing.
        var rig = Rig(max: 30000, maxLinks: 1);
        CalledLink(rig);
        rig.Outputs.Should().Contain(new TimerStart(Max(X), 30000));
        rig.Expire(Max(X));
        rig.Outputs.Should().BeEmpty();
        rig.Sis.SoftLinkOf(X).Should().Be("Established");

        // A U_PDU for Y is held off (B-0028) and the overdue link closes to share the channel: type 4 reason 0 (B-0302).
        rig.ArqRequest(Y);
        rig.Sis.SoftLinkOf(Y).Should().Be("HeldOff");
        rig.Sis.SoftLinkOf(X).Should().Be("Closing");
        rig.Outputs.Should().Equal(new TimerCancel(Idle(X)), new TimerCancel(First(X)), new LinkState(X, LinkStateName.Breaking));

        // Once broken, Y is called (B-0412).
        rig.Sent(X);
        rig.Deliver(Link(X, new PhysicalLinkBreakConfirm()));
        rig.Cas.PhaseOf(Y).Should().Be("Calling");
        rig.Sis.SoftLinkOf(X).Should().BeNull();
    }

    [Fact]
    public void A_link_within_its_maximum_time_is_not_closed_for_a_waiting_peer_of_equal_priority()
    {
        // A-0322, A-0323: priority 0 outstanding, priority 0 waiting: no close until the maximum time.
        var rig = Rig(max: 30000, maxLinks: 1);
        CalledLink(rig);
        rig.ArqRequest(X);
        rig.ArqRequest(Y);
        rig.Sis.SoftLinkOf(X).Should().Be("Established");
        rig.Expire(Max(X));
        rig.Sis.SoftLinkOf(X).Should().Be("Closing");
    }

    [Fact]
    public void A_called_node_within_its_minimum_hold_is_not_closed_for_higher_priority_traffic()
    {
        // A-0197, A-0326, then A-0323 when the hold ends.
        var rig = Rig(minHold: 10000, maxLinks: 1);
        CalledLink(rig);
        rig.ArqRequest(X, priority: 2);
        rig.ArqRequest(Y, priority: 9);
        rig.Sis.SoftLinkOf(Y).Should().Be("HeldOff");
        rig.Sis.SoftLinkOf(X).Should().Be("Established");
        rig.Expire(MinHold(X));
        rig.Sis.SoftLinkOf(X).Should().Be("Closing");
        rig.Cas.PhaseOf(X).Should().Be("Breaking", "the outstanding C_PDU had not started, so the link was clean (B-0317)");
    }

    [Fact]
    public void An_idle_link_is_left_to_its_idle_wait_and_a_caller_is_closed_at_once_for_higher_priority()
    {
        // A-0323: nothing outstanding, nothing to displace.
        var rig = Rig(maxLinks: 1);
        CalledLink(rig);
        rig.ArqRequest(Y, priority: 15);
        rig.Sis.SoftLinkOf(X).Should().Be("Established");

        // A caller has no minimum hold: lower priority outstanding is displaced at once.
        var caller = Rig(maxLinks: 1, minHold: 60000);
        CallerLink(caller, priority: 3);
        caller.ArqRequest(Y, priority: 4);
        caller.Sis.SoftLinkOf(X).Should().Be("Closing");
    }

    [Fact]
    public void A_u_pdu_for_a_link_being_closed_is_held_and_gets_a_new_link_once_the_old_is_broken()
    {
        // A-0327.
        var rig = Rig(idle: 1000);
        CalledLink(rig);
        rig.Expire(Idle(X));
        rig.Sis.SoftLinkOf(X).Should().Be("Closing");
        rig.ArqRequest(X);
        rig.Sis.AwaitingSoftLink.Should().ContainSingle();
        rig.Sis.Outstanding.Should().BeEmpty();
        rig.Cas.PhaseOf(X).Should().Be("Breaking");

        rig.Sent(X);
        rig.Deliver(Link(X, new PhysicalLinkBreakConfirm()));
        rig.Outputs.Should().Equal(
            new TimerCancel(TimerName.ForPeer(TimerKind.CasBreak, X)), new LinkState(X, LinkStateName.Unlinked), new LinkState(X, LinkStateName.Calling));
        rig.Sis.SoftLinkOf(X).Should().Be("Making");
    }

    [Theory]
    [InlineData((int)SisBreakReason.HigherPriorityLink, 9)]
    [InlineData((int)SisBreakReason.ShareChannel, 10)]
    [InlineData((int)SisBreakReason.Idle, 11)]
    [InlineData((int)SisBreakReason.Operator, 3)]
    public void Rejections_caused_by_a_close_of_ours_carry_its_reason(int reason, int sisReason)
    {
        // A-0207, A-0324: the C_PDU held by the clean close is rejected when the connection ends; the
        // client asked for confirmation, so reasons 9 to 11 reach it (A-0105, A-0107). An operator
        // break has no SIS reason and keeps the no-connection reason, 3 (interp A-0340).
        var rig = Rig();
        CallerLink(rig, confirm: 1);
        rig.Deliver(new CPhysicalLinkBreak(X, (SisBreakReason)reason));
        rig.Cas.PhaseOf(X).Should().Be("Breaking");
        rig.Sent(X);
        rig.Deliver(Link(X, new PhysicalLinkBreakConfirm()));
        List<SisBytesOut> sis = rig.Outputs.OfType<SisBytesOut>().ToList();
        if (sisReason < 0)
        {
            sis.Should().BeEmpty();
        }
        else
        {
            var expected = new SisUnidataRequestRejected((byte)sisReason, 9, new SisAddress(X), new byte[] { 1 });
            sis.Should().ContainSingle().Which.Bytes.ToArray().Should().Equal(SisCodec.Encode(expected));
        }

        rig.Sis.Outstanding.Should().BeEmpty();
    }

    [Fact]
    public void A_rejection_after_a_close_without_confirmation_or_after_a_disconnect_reaches_no_client()
    {
        // A-0105: no confirmation asked, nothing sent.
        var rig = Rig();
        CallerLink(rig);
        rig.Deliver(new CPhysicalLinkBreak(X, SisBreakReason.Idle));
        rig.Sent(X);
        rig.Deliver(Link(X, new PhysicalLinkBreakConfirm()));
        rig.Outputs.OfType<SisBytesOut>().Should().BeEmpty();

        // A-0260: the client went away; its outstanding request is reported to nobody.
        var gone = Rig();
        CallerLink(gone, confirm: 1);
        gone.Disconnect();
        gone.Deliver(new CPhysicalLinkBreak(X, SisBreakReason.HigherPriorityLink, 15));
        gone.Sent(X);
        gone.Deliver(Link(X, new PhysicalLinkBreakConfirm()));
        gone.Outputs.OfType<SisBytesOut>().Should().BeEmpty();
    }

    [Fact]
    public void A_ttl_rejection_during_our_close_keeps_its_own_reason()
    {
        // A-0324: only the rejections the connection's end causes take the close reason.
        var rig = Rig();
        CallerLink(rig, confirm: 1);
        int handle = rig.Sis.Outstanding.Single().Handle;
        rig.Deliver(new CPhysicalLinkBreak(X, SisBreakReason.Idle));
        rig.Deliver(new DUnidataRequestRejected(handle, DReason.TtlExpired));
        var expected = new SisUnidataRequestRejected(1, 9, new SisAddress(X), new byte[] { 1 });
        rig.Outputs.OfType<SisBytesOut>().Should().ContainSingle("TTL Expired, reason 1 (A-0229; interp A-0340)")
            .Which.Bytes.ToArray().Should().Equal(SisCodec.Encode(expected));
        rig.Sis.Outstanding.Should().BeEmpty();
    }

    [Fact]
    public void A_break_the_sis_asks_for_during_a_close_the_peer_asked_for_joins_it()
    {
        // B-0450: the peer's No Data request holds nothing back (B-0249); our break stops new C_PDUs,
        // starts cas.clean_close because the link is not clean, and sends no request of our own.
        var rig = new CasLinkTests.Rig(c => c with { Cas = new CasConfig(4, 60000, 2, 120000, BreakRetries: 1) });
        CalledLink(rig);
        rig.Dts.Peers.OnDpdu(new DataOnly(new DpduHeader(0, 10, 3, Us, X), new DataFlags(true, true, false, false, true, true), 0,
            new Octets(CpduCodec.Encode(new CpduData(new byte[] { 0x20, 0x59, 0x00, 0x41 }))), true));
        rig.Deliver(new DUnidataIndication(X, Us, false, true, CpduCodec.Encode(new PhysicalLinkBreakRequest(0, 0))));
        rig.Outputs.Should().Equal(new TimerCancel(First(X)), new LinkState(X, LinkStateName.Closing));
        rig.Dts.HeldArq.Should().BeEmpty();

        rig.Deliver(new CPhysicalLinkBreak(X, SisBreakReason.Idle));
        rig.Outputs.Should().Equal(new TimerStart(CleanClose(X), 3 * 205500));
        rig.Dts.HeldArq.Should().BeEmpty("the peer asked for the close; no PHYSICAL LINK BREAK REQUEST of ours");

        // A second break changes nothing.
        rig.Deliver(new CPhysicalLinkBreak(X, SisBreakReason.ShareChannel));
        rig.Outputs.Should().BeEmpty();
    }

    [Theory]
    [InlineData("soft_link_idle_ms", -1)]
    [InlineData("soft_link_min_hold_ms", -1)]
    [InlineData("soft_link_max_ms", 0)]
    public void Soft_link_settings_below_their_minimum_are_refused(string key, int value)
    {
        JsonObject config = ConfigMerge.Merge(Corpus.T2Defaults, JsonNode.Parse(
            "{\"local_address\":\"0.0.1.2\",\"sis\":{\"" + key + "\":" + value + "}}")!.AsObject());
        FluentActions.Invoking(() => ConfigReader.Read(config)).Should().Throw<ConfigException>().Which.Message.Should().Contain("sis." + key);
    }

    [Fact]
    public void Soft_link_settings_are_read_and_derived_when_null()
    {
        // A-0321: unset, each is derived from the answer wait, here the derived 205500 ms (C-1120).
        JsonObject config = ConfigMerge.Merge(Corpus.T2Defaults, JsonNode.Parse("""{"local_address":"0.0.1.2"}""")!.AsObject());
        NodeConfig unset = ConfigReader.Read(config);
        unset.Sis.Should().Be(new SisConfig(SisConfig.MaxTtl));
        unset.SoftLinkIdleMs().Should().Be(3 * 205500);
        unset.SoftLinkMinHoldMs().Should().Be(3 * 205500);
        unset.SoftLinkMaxMs().Should().Be(6 * 205500);
        ConfigReader.Read(ConfigMerge.Merge(config, JsonNode.Parse("""{"dts":{"ack_wait_ms":40000}}""")!.AsObject()))
            .SoftLinkMaxMs().Should().Be(240000);
        config = ConfigMerge.Merge(config, JsonNode.Parse("""{"sis":{"soft_link_idle_ms":0,"soft_link_min_hold_ms":0,"soft_link_max_ms":1}}""")!.AsObject());
        ConfigReader.Read(config).Sis.Should().Be(new SisConfig(SisConfig.MaxTtl, 0, 0, 1));
    }

    [Fact]
    public void Unset_soft_link_times_start_derived_timers_when_the_link_is_made()
    {
        // A-0321, A-0326: a called node starts the minimum hold, the maximum and the idle wait, in that order.
        var rig = new CasLinkTests.Rig(c => c with { Cas = new CasConfig(4, 60000, 2, 120000, BreakRetries: 1) }, showSisTimers: true);
        CalledLink(rig);
        rig.Outputs.Should().Equal(
            new LinkState(X, LinkStateName.Linked), new TimerStart(First(X), 120000),
            new TimerStart(MinHold(X), 3 * 205500), new TimerStart(Max(X), 6 * 205500), new TimerStart(Idle(X), 3 * 205500));
    }
}
