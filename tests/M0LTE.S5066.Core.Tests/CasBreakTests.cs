using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// Breaking links, clean close and link failure in the CAS (P5-1) on paths the cas corpus scenarios
/// do not reach: a break the SIS asks for (no SIS item sends one before P5-2), with an Ed4 peer clean
/// or not and with an Ed3 peer; the clean close timeout; crossed breaks; CAS-1 C_PDUs met while
/// closing or breaking; the half-open rule and its first-data exemption; D_CONNECTION_LOST; and the
/// derived timer defaults. The rig is <see cref="CasLinkTests.Rig"/>: real SIS, CAS and DTS, a probe MAC.
/// </summary>
public class CasBreakTests
{
    private const uint X = 0x0103;
    private const uint Us = 0x0102;

    // TestConfig.Basic: 127500 + max(30000, 3000) + 16 x 3000, the derived answer wait (C-1120).
    private const long AnswerWait = 205500;

    private static TimerName Break(uint p) => TimerName.ForPeer(TimerKind.CasBreak, p);

    private static TimerName CleanClose(uint p) => TimerName.ForPeer(TimerKind.CasCleanClose, p);

    private static TimerName First(uint p) => TimerName.ForPeer(TimerKind.CasFirstData, p);

    private static DExpeditedUnidataIndication Link(uint src, CPdu c) => new(src, Us, false, false, CpduCodec.Encode(c));

    private static DUnidataIndication Arq(uint src, CPdu c) => new(src, Us, false, true, CpduCodec.Encode(c));

    /// <summary>A Called-node link with X, Ed4 or Ed3 by the request, made and awaiting its first DATA C_PDU.</summary>
    private static CasLinkTests.Rig Linked(bool ed4 = true, Func<NodeConfig, NodeConfig>? configure = null)
    {
        var rig = new CasLinkTests.Rig(configure ?? (c => c with { Cas = c.Cas with { BreakRetries = 1 } }));
        rig.Deliver(Link(X, new PhysicalLinkRequest(false, ed4, false)));
        rig.Sent(X);
        rig.Cas.PhaseOf(X).Should().Be("Linked");
        return rig;
    }

    /// <summary>The peer's DATA-ONLY with a one-byte DATA C_PDU: delivered, so an acknowledgement is owed and the link is not clean (B-0236 (a)).</summary>
    private static void PeerData(CasLinkTests.Rig rig) =>
        rig.Dts.Peers.OnDpdu(new DataOnly(new DpduHeader(0, 10, 3, Us, X), new DataFlags(true, true, false, false, true, true), 0,
            new Octets(CpduCodec.Encode(new CpduData(new byte[] { 0x20, 0x59, 0x00, 0x41 }))), true));

    [Fact]
    public void A_break_the_sis_asks_for_on_a_clean_ed4_link_goes_straight_to_physical_link_break()
    {
        // B-0278, B-0317, B-0421: clean, so no type 6; Step 1 with the B-0302 reason (Idle: 4).
        var rig = Linked();
        rig.Deliver(new CPhysicalLinkBreak(X, SisBreakReason.Idle));
        rig.Outputs.Should().Equal(new TimerCancel(First(X)), new LinkState(X, LinkStateName.Breaking));
        rig.Cas.PhaseOf(X).Should().Be("Breaking");
        rig.Dts.HeldArq.Should().BeEmpty("no PHYSICAL LINK BREAK REQUEST on a clean link (B-0317)");
        rig.Sent(X);
        rig.Outputs.Should().Equal(new TimerStart(Break(X), AnswerWait));   // derived (B-0420)
    }

    [Fact]
    public void A_break_the_sis_asks_for_on_a_busy_ed4_link_sends_the_request_and_waits_for_clean_close()
    {
        // B-0239, B-0241, B-0242, B-0312, B-0322, B-0421.
        var rig = Linked();
        PeerData(rig);
        rig.Deliver(new CPhysicalLinkBreak(X, SisBreakReason.HigherPriorityLink, 22));
        rig.Outputs.Should().Equal(new TimerCancel(First(X)), new LinkState(X, LinkStateName.Closing), new TimerStart(CleanClose(X), 3 * AnswerWait));
        DUnidataRequest q = rig.Dts.HeldArq.Should().ContainSingle().Which;
        q.CPdu.ToArray().Should().Equal(0x61, 15);   // Higher Priority, the priority clamped to 15 (B-0172, B-0311)
        (q.Arq, q.Priority, q.Ttd).Should().Be((true, 0, TransmitQueue.NoTtd));

        // B-0322: the clean close took too long: Step 1 anyway, with reason 3 (B-0302).
        rig.Expire(CleanClose(X));
        rig.Outputs.Should().Equal(new LinkState(X, LinkStateName.Breaking));
        rig.Cas.PhaseOf(X).Should().Be("Breaking");
    }

    [Fact]
    public void A_break_the_sis_asks_for_on_an_ed3_link_skips_clean_close()
    {
        // B-0240, B-0321.
        var rig = Linked(ed4: false);
        PeerData(rig);
        rig.Deliver(new CPhysicalLinkBreak(X, SisBreakReason.Operator));
        rig.Outputs.Should().Equal(new TimerCancel(First(X)), new LinkState(X, LinkStateName.Breaking));
        rig.Dts.HeldArq.Should().BeEmpty();
    }

    [Fact]
    public void A_break_request_from_the_peer_while_breaking_changes_nothing_and_one_by_expedited_delivery_is_discarded()
    {
        // B-0312, B-0423.
        var rig = Linked();
        rig.Deliver(Link(X, new PhysicalLinkBreakRequest(3, 0)));
        rig.Outputs.Should().BeEmpty();
        rig.Cas.PhaseOf(X).Should().Be("Linked");
        rig.Deliver(new CPhysicalLinkBreak(X, SisBreakReason.Idle));
        rig.Outputs.Clear();
        rig.Deliver(Arq(X, new PhysicalLinkBreakRequest(3, 0)));
        rig.Outputs.Should().BeEmpty();
        rig.Cas.PhaseOf(X).Should().Be("Breaking");
    }

    [Fact]
    public void Crossed_breaks_make_the_receiver_of_a_break_the_responder()
    {
        // B-0316: our break is under way; the peer's arrives: confirm, broken, our timer stops.
        var rig = Linked();
        rig.Deliver(new CPhysicalLinkBreak(X, SisBreakReason.Idle));
        rig.Sent(X);
        rig.Outputs.Should().Equal(new TimerStart(Break(X), AnswerWait));
        rig.Deliver(Link(X, new PhysicalLinkBreak(0)));
        rig.Outputs.Should().Equal(new TimerCancel(Break(X)), new LinkState(X, LinkStateName.Unlinked));
        rig.Cas.PhaseOf(X).Should().BeNull();
        rig.Cas.PendingHandleOf(X).Should().BeNull();
        rig.Dts.Peers.IsConnected(X).Should().BeFalse("D_CONNECTION_TERMINATED (B-0285)");

        // A late confirm of ours is stale (B-0316), and so is one never asked for (B-0425).
        rig.Deliver(Link(X, new PhysicalLinkBreakConfirm()));
        rig.Outputs.Should().BeEmpty();
    }

    [Fact]
    public void A_confirm_while_not_breaking_and_a_request_while_breaking_are_ignored_and_a_request_while_closing_is_answered()
    {
        // B-0425, B-0214.
        var rig = Linked();
        rig.Deliver(Link(X, new PhysicalLinkBreakConfirm()));
        rig.Outputs.Should().BeEmpty();
        rig.Cas.PhaseOf(X).Should().Be("Linked");

        PeerData(rig);
        rig.Deliver(Arq(X, new PhysicalLinkBreakRequest(0, 0)));
        rig.Cas.PhaseOf(X).Should().Be("Closing");
        rig.Deliver(Link(X, new PhysicalLinkRequest(false, true, false)));
        rig.Cas.PendingHandleOf(X).Should().NotBeNull("answered again on a made link (B-0214)");
        rig.Sent(X);

        var breaking = Linked();
        breaking.Deliver(new CPhysicalLinkBreak(X, SisBreakReason.Idle));
        breaking.Sent(X);
        breaking.Deliver(Link(X, new PhysicalLinkRequest(false, true, false)));
        breaking.Outputs.Should().BeEmpty();
        breaking.Cas.PendingHandleOf(X).Should().BeNull("not answered while breaking (B-0425)");
    }

    [Fact]
    public void A_break_from_a_node_whose_link_is_being_made_is_confirmed_and_the_call_goes_on()
    {
        // B-0323, B-0425.
        var rig = new CasLinkTests.Rig();
        rig.ArqRequest(X);
        rig.Outputs.Should().Equal(new LinkState(X, LinkStateName.Calling));
        int? request = rig.Cas.PendingHandleOf(X);
        rig.Deliver(Link(X, new PhysicalLinkBreak(0)));
        rig.Outputs.Should().BeEmpty();
        rig.Cas.PhaseOf(X).Should().Be("Calling");
        rig.Cas.PendingHandleOf(X).Should().Be(request);
    }

    [Fact]
    public void Two_warnings_reason_1_break_a_half_open_link_unless_the_first_data_cpdu_is_awaited()
    {
        // B-0426.
        var rig = Linked();
        Warning(rig, 1);
        Warning(rig, 1);
        rig.Cas.PhaseOf(X).Should().Be("Linked", "the Caller may still be calling: the first-data wait covers it (B-0215)");

        rig.Deliver(Arq(X, new CpduData(new byte[] { 1 })));   // the first DATA C_PDU
        rig.Outputs.Should().Equal(new TimerCancel(First(X)));
        Warning(rig, 1);
        Warning(rig, 3);   // another reason does not count
        rig.Deliver(Arq(X, new CpduData(new byte[] { 2 })));   // an ARQ C_PDU from the peer starts the count again
        Warning(rig, 1);
        rig.Cas.PhaseOf(X).Should().Be("Linked");
        Warning(rig, 1);
        rig.Outputs.Should().Equal(new LinkState(X, LinkStateName.Unlinked));
        rig.Cas.PhaseOf(X).Should().BeNull();
        rig.Dts.Peers.IsConnected(X).Should().BeFalse("D_CONNECTION_TERMINATED, as for D_CONNECTION_LOST (B-0324)");
        rig.Sis.SoftLinkOf(X).Should().BeNull("C_PHYSICAL_LINK_BROKEN ends the soft link (A-0199)");

        static void Warning(CasLinkTests.Rig rig, int reason) => rig.Deliver(new DWarningReceived(X, Us, 9, reason));
    }

    [Fact]
    public void Connection_lost_breaks_the_link_locally_with_no_cpdu_to_the_peer()
    {
        // B-0277, B-0324.
        var rig = Linked();
        rig.Deliver(new DConnectionLost(X));
        rig.Outputs.Should().Equal(new TimerCancel(First(X)), new LinkState(X, LinkStateName.Unlinked));
        rig.Cas.PendingHandleOf(X).Should().BeNull();
        rig.Sis.SoftLinkOf(X).Should().BeNull();
        rig.Deliver(new DConnectionLost(X));   // no link: nothing
        rig.Outputs.Should().BeEmpty();
    }

    [Fact]
    public void Our_break_request_confirm_and_reject_do_not_reach_the_sis()
    {
        // B-0312: the type 6 is the CAS's own ARQ C_PDU.
        var rig = Linked();
        PeerData(rig);
        rig.Deliver(new CPhysicalLinkBreak(X, SisBreakReason.ShareChannel));
        int handle = rig.Dts.HeldArq.Single().Handle;
        rig.Dts.HeldArq.Single().CPdu.ToArray().Should().Equal(0x62, 0);   // Channel Share, priority 0 (B-0173, B-0302)
        rig.Deliver(new DUnidataRequestConfirm(handle));
        rig.Deliver(new DUnidataRequestRejected(handle, DReason.NoConnection));
        rig.Outputs.Should().BeEmpty();
    }

    [Fact]
    public void Unset_cas_timers_are_derived_from_the_answer_wait_and_counts_stay_required()
    {
        // B-0420, issue #49 (a).
        JsonObject defaults = Corpus.T2Defaults.DeepClone().AsObject();
        defaults["local_address"] = "0.0.1.2";
        NodeConfig c = ConfigReader.Read(defaults);
        long wait = 127500 + 30000 + (16 * 3000);
        (c.LinkRequestTimeoutMs(), c.BreakTimeoutMs(), c.FirstDataTimeoutMs(), c.CleanCloseTimeoutMs()).Should().Be((wait, wait, 3 * wait, 3 * wait));
        defaults["dts"]!["ack_wait_ms"] = 40000;
        c = ConfigReader.Read(defaults);
        (c.LinkRequestTimeoutMs(), c.BreakTimeoutMs(), c.FirstDataTimeoutMs(), c.CleanCloseTimeoutMs()).Should().Be((40000L, 40000L, 120000L, 120000L));
        defaults["cas"] = JsonNode.Parse("""{"max_links":1,"link_request_timeout_ms":7,"link_request_retries":0,"first_data_timeout_ms":8,"break_timeout_ms":9,"break_retries":0,"clean_close_timeout_ms":10}""");
        c = ConfigReader.Read(defaults);
        (c.LinkRequestTimeoutMs(), c.FirstDataTimeoutMs(), c.BreakTimeoutMs(), c.CleanCloseTimeoutMs(), c.Cas.RequireBreakRetries()).Should().Be((7L, 8L, 9L, 10L, 0));
        foreach (string key in new[] { "break_timeout_ms", "clean_close_timeout_ms" })
        {
            JsonObject bad = defaults.DeepClone().AsObject();
            bad["cas"]![key] = 0;
            var read = () => ConfigReader.Read(bad);
            read.Should().Throw<ConfigException>().Which.Setting.Should().Be("cas." + key);
        }

        var retries = () => CasConfig.Unset.RequireBreakRetries();
        retries.Should().Throw<MissingSettingException>().Which.Setting.Should().Be("cas.break_retries");
    }

    [Fact]
    public void An_ack_wait_shorter_than_the_dcd_lbt_wait_is_refused_at_load()
    {
        // C-1252, issue #49 (b).
        JsonObject defaults = Corpus.T2Defaults.DeepClone().AsObject();
        defaults["local_address"] = "0.0.1.2";
        defaults["dts"]!["ack_wait_ms"] = 29999;
        var read = () => ConfigReader.Read(defaults);
        ConfigException ex = read.Should().Throw<ConfigException>().Which;
        ex.Setting.Should().Be("dts.ack_wait_ms");
        ex.Message.Should().NotContain("\n").And.Contain("csma.lbt_wait_dcd_ms");
        defaults["dts"]!["ack_wait_ms"] = 30000;
        ConfigReader.Read(defaults).Dts.AckWaitMs.Should().Be(30000);
    }
}
