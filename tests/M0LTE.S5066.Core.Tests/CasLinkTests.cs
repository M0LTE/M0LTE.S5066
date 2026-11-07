using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// CAS-1 link establishment and the SIS soft link (P4-1) on paths the cas corpus scenarios do not
/// reach: crossed calls, the first DATA C_PDU, local and peer link limits with the SIS holding off,
/// discarded link C_PDUs, the DTS reject primitives, the edition record and the DTS connected
/// states. The rig wires the real SIS, CAS and DTS with a probe MAC; "sent" confirms (C-1100) are
/// delivered by hand.
/// </summary>
public class CasLinkTests
{
    private const uint X = 0x0103;
    private const uint Y = 0x0104;
    private const uint Us = 0x0102;

    private static TimerName Req(uint p) => TimerName.ForPeer(TimerKind.CasLinkRequest, p);

    private static TimerName First(uint p) => TimerName.ForPeer(TimerKind.CasFirstData, p);

    private static DExpeditedUnidataIndication Link(uint src, CPdu c, uint dst = Us, bool group = false) =>
        new(src, dst, group, false, CpduCodec.Encode(c));

    [Fact]
    public void Crossed_calls_take_the_called_role_and_make_one_link()
    {
        // B-0405: we call X, X's request arrives; our timer stops and we accept.
        var rig = new Rig();
        rig.ArqRequest(X);
        rig.Outputs.Should().Equal(new LinkState(X, LinkStateName.Calling));
        rig.Sent(X);
        rig.Outputs.Should().Equal(new TimerStart(Req(X), 60000));
        rig.Deliver(Link(X, new PhysicalLinkRequest(false, true, false)));
        rig.Outputs.Should().Equal(new TimerCancel(Req(X)));
        rig.Cas.PhaseOf(X).Should().Be("Accepting");
        rig.Sent(X);
        rig.Outputs.Should().Equal(
            new LinkState(X, LinkStateName.Linked), new TimerStart(First(X), 120000),
            new TimerStart(TimerName.Of(TimerKind.DtsTtd), 648000L * 2000));   // the released ARQ C_PDU's TTD (C-1050)
        rig.Sis.SoftLinkOf(X).Should().Be("Established");
        rig.Dts.HeldArq.Should().ContainSingle().Which.Dst.Should().Be(X);   // released at once (A-0194)
        rig.Dts.Peers.StateOf(X).Should().Be(DtsPeerState.IdleConnected);   // C-0571
    }

    [Fact]
    public void An_accept_while_our_accept_is_unsent_makes_the_link_at_once_without_a_first_data_wait()
    {
        var rig = new Rig();
        rig.Deliver(Link(X, new PhysicalLinkRequest(false, true, false)));
        rig.Deliver(Link(X, new PhysicalLinkAccepted(false, true, false)));
        rig.Outputs.Should().Equal(new LinkState(X, LinkStateName.Linked));
        rig.Sent(X);   // nothing pending: the late confirm of our accept changes nothing
        rig.Outputs.Should().BeEmpty();
    }

    [Fact]
    public void The_first_arq_data_cpdu_stops_the_first_data_timer_and_non_arq_does_not()
    {
        // B-0215, B-0403.
        var rig = new Rig();
        rig.Deliver(Link(X, new PhysicalLinkRequest(false, true, false)));
        rig.Sent(X);
        rig.Outputs.Should().HaveCount(2);
        byte[] data = CpduCodec.Encode(new CpduData(new byte[] { 0x01 }));
        rig.Deliver(new DUnidataIndication(X, Us, false, false, data));
        rig.Outputs.Should().BeEmpty();
        rig.Deliver(new DUnidataIndication(X, Us, false, true, data));
        rig.Outputs.Should().Equal(new TimerCancel(First(X)));
    }

    [Fact]
    public void Local_link_limit_holds_off_and_the_sis_asks_again_when_another_call_ends()
    {
        // B-0025, B-0028, B-0402, B-0412.
        var rig = new Rig(c => c with { Cas = new CasConfig(1, 60000, 2, 120000) });
        rig.ArqRequest(X);
        rig.Outputs.Should().Equal(new LinkState(X, LinkStateName.Calling));
        rig.ArqRequest(Y);
        rig.Outputs.Should().BeEmpty();
        rig.Sis.SoftLinkOf(Y).Should().Be("HeldOff");
        rig.Cas.PhaseOf(Y).Should().BeNull();

        rig.Deliver(Link(X, new PhysicalLinkRejected(0)));
        rig.Outputs.Should().Equal(new LinkState(X, LinkStateName.Unlinked), new LinkState(Y, LinkStateName.Calling));
        rig.Sis.SoftLinkOf(Y).Should().Be("Making");
        rig.Sis.AwaitingSoftLink.Should().ContainSingle().Which.Dst.Should().Be(Y);
    }

    [Fact]
    public void Peer_reason_4_holds_off_without_rejecting_to_the_client()
    {
        // B-0303, A-0300.
        var rig = new Rig();
        rig.ArqRequest(X, confirm: 1);
        rig.Deliver(Link(X, new PhysicalLinkRejected(4)));
        rig.Outputs.Should().Equal(new LinkState(X, LinkStateName.Unlinked));
        rig.Sis.SoftLinkOf(X).Should().Be("HeldOff");
        rig.Sis.AwaitingSoftLink.Should().ContainSingle();
    }

    [Fact]
    public void Link_cpdus_to_a_group_and_unsolicited_answers_are_discarded()
    {
        // B-0410.
        var rig = new Rig();
        rig.Deliver(Link(X, new PhysicalLinkRequest(false, true, false), dst: 0x09, group: true));
        rig.Deliver(Link(X, new PhysicalLinkAccepted(false, true, false)));
        rig.Deliver(Link(X, new PhysicalLinkRejected(0)));
        rig.Outputs.Should().BeEmpty();
        rig.Cas.PhaseOf(X).Should().BeNull();
        rig.Dts.PendingNonArq.Should().BeEmpty();
        rig.MacLog.Should().BeEmpty();
    }

    [Fact]
    public void A_warning_records_ed3_and_later_calls_and_decoding_use_it()
    {
        // B-0229, B-0230, B-0231, B-0408, B-0411; core-architecture.md 10 item 8.
        var rig = new Rig();
        rig.ArqRequest(X);
        rig.Sent(X);
        rig.Outputs.Should().Equal(new TimerStart(Req(X), 60000));
        rig.Deliver(new DWarningReceived(X, Us, 8, 2));
        rig.Outputs.Should().Equal(new TimerCancel(Req(X)));
        rig.Context.Peers.EditionOf(X).Should().Be(KnownEdition.Ed3);
        rig.Context.Peers.DecodeEditionOf(X).Should().Be(PeerEdition.Ed3);
        rig.Deliver(new DWarningReceived(X, Us, 8, 2));   // the Ed4 bit is no longer set: ignored
        rig.Outputs.Should().BeEmpty();
        rig.Deliver(Link(X, new PhysicalLinkAccepted(false, false, false)));
        rig.Outputs[0].Should().Be(new LinkState(X, LinkStateName.Linked));
    }

    [Fact]
    public void Repeated_request_on_a_made_link_keeps_the_edition_and_is_answered_once_while_unsent()
    {
        // B-0314, B-0401.
        var rig = new Rig();
        rig.Deliver(Link(X, new PhysicalLinkRequest(false, true, false)));
        rig.Sent(X);
        rig.Outputs.Clear();
        rig.Deliver(Link(X, new PhysicalLinkRequest(false, false, false)));
        int? h = rig.Cas.PendingHandleOf(X);
        h.Should().NotBeNull();
        rig.Deliver(Link(X, new PhysicalLinkRequest(false, false, false)));
        rig.Cas.PendingHandleOf(X).Should().Be(h);
        rig.Context.Peers.EditionOf(X).Should().Be(KnownEdition.Ed4);
    }

    [Fact]
    public void A_make_for_a_linked_peer_is_answered_at_once_and_new_arq_data_goes_straight_down()
    {
        // B-0224, A-0194, A-0200.
        var rig = new Rig();
        rig.ArqRequest(X);
        rig.Deliver(Link(X, new PhysicalLinkAccepted(false, true, false)));
        rig.Dts.HeldArq.Should().HaveCount(1);
        rig.ArqRequest(X);
        rig.Dts.HeldArq.Should().HaveCount(2);
        rig.Sis.AwaitingSoftLink.Should().BeEmpty();
    }

    [Fact]
    public void A_dts_refusal_of_our_link_request_fails_the_call()
    {
        // B-0022.
        var rig = new Rig();
        rig.ArqRequest(X, confirm: 1);
        rig.Deliver(new DExpeditedUnidataRequestRejected(rig.Cas.PendingHandleOf(X)!.Value, DReason.NoConnection));
        rig.Outputs.Should().HaveCount(2);
        rig.Outputs[0].Should().Be(new LinkState(X, LinkStateName.Unlinked));
        rig.Outputs[1].Should().BeOfType<SisBytesOut>().Which.Bytes.Span[5].Should().Be((byte)SisPrimitiveType.UnidataRequestRejected);
    }

    [Fact]
    public void A_dts_refusal_of_our_accept_drops_the_entry_so_the_next_request_is_answered()
    {
        // B-0405 review: an accept that is never sent must not leave the peer stuck accepting.
        var rig = new Rig();
        rig.Deliver(Link(X, new PhysicalLinkRequest(false, true, false)));
        rig.Deliver(new DExpeditedUnidataRequestRejected(rig.Cas.PendingHandleOf(X)!.Value, DReason.NoConnection));
        rig.Outputs.Should().BeEmpty();
        rig.Cas.PhaseOf(X).Should().BeNull();
        rig.Deliver(Link(X, new PhysicalLinkRequest(false, true, false)));
        rig.Cas.PhaseOf(X).Should().Be("Accepting");
        rig.Cas.PendingHandleOf(X).Should().NotBeNull();
    }

    [Fact]
    public void A_dts_refusal_of_our_accept_in_crossed_calls_fails_the_call()
    {
        var rig = new Rig();
        rig.ArqRequest(X, confirm: 1);
        rig.Sent(X);
        rig.Deliver(Link(X, new PhysicalLinkRequest(false, true, false)));
        rig.Deliver(new DExpeditedUnidataRequestRejected(rig.Cas.PendingHandleOf(X)!.Value, DReason.NoConnection));
        rig.Outputs[0].Should().Be(new LinkState(X, LinkStateName.Unlinked));
        rig.Cas.PhaseOf(X).Should().BeNull();
    }

    [Fact]
    public void A_warning_before_our_request_is_sent_is_not_an_answer_to_it()
    {
        // B-0411: the WARNING cannot be about a request the peer has not heard.
        var rig = new Rig();
        rig.ArqRequest(X);
        rig.Deliver(new DWarningReceived(X, Us, 8, 2));
        rig.Outputs.Should().BeEmpty();
        rig.Context.Peers.EditionOf(X).Should().Be(KnownEdition.Unknown);

        // Once sent, a WARNING about some other D_PDU type is not about our request either.
        rig.Sent(X);
        rig.Deliver(new DWarningReceived(X, Us, 0, 2));
        rig.Outputs.Should().BeEmpty();
        rig.Context.Peers.EditionOf(X).Should().Be(KnownEdition.Unknown);
    }

    [Fact]
    public void Crossed_calls_take_the_link_values_from_our_own_request_as_answered()
    {
        // B-0405 review: we asked with Ed4 clear (stale Ed3 record), X asked with Ed4 set and also
        // accepted our request. X uses the values of our request, so we must too.
        var rig = new Rig();
        rig.Context.Peers.SetEdition(X, KnownEdition.Ed3);
        rig.ArqRequest(X);
        rig.Sent(X);
        rig.Deliver(Link(X, new PhysicalLinkRequest(false, true, false)));
        rig.Deliver(Link(X, new PhysicalLinkAccepted(false, false, false)));
        rig.Dts.Peers.ConnectionOf(X).Should().Be((false, true));
    }

    [Fact]
    public void First_data_abort_terminates_the_connection_and_rejects_held_arq_cpdus()
    {
        // B-0406, C-0652: X calls us, we accept; our own client then queues ARQ data for X.
        var rig = new Rig();
        rig.Deliver(Link(X, new PhysicalLinkRequest(false, true, false)));
        rig.Sent(X);
        rig.ArqRequest(X);
        rig.Dts.HeldArq.Should().ContainSingle();
        rig.Outputs.Clear();
        rig.Expire(First(X));
        rig.Outputs.Should().Equal(new LinkState(X, LinkStateName.Unlinked), new TimerCancel(TimerName.Of(TimerKind.DtsTtd)));
        rig.Dts.HeldArq.Should().BeEmpty();
        rig.Dts.Peers.StateOf(X).Should().Be(DtsPeerState.IdleUnconnected);
        rig.Sis.SoftLinkOf(X).Should().BeNull();
    }

    [Fact]
    public void Held_arq_cpdus_expire_by_ttd_and_are_rejected()
    {
        // C-0857, C-1050 for ARQ C_PDUs held until P4-2.
        var rig = new Rig();
        rig.ArqRequest(X, ttl: 5);
        rig.Deliver(Link(X, new PhysicalLinkAccepted(false, true, false)));
        rig.Outputs.Should().EndWith(new TimerStart(TimerName.Of(TimerKind.DtsTtd), 10000));
        rig.Outputs.Clear();
        rig.Expire(TimerName.Of(TimerKind.DtsTtd), 10000);
        rig.Dts.HeldArq.Should().BeEmpty();
        rig.Outputs.Should().BeEmpty();
    }

    [Fact]
    public void Connection_made_keeps_management_and_is_ignored_twice()
    {
        // C-0631, C-0651.
        var rig = new Rig();
        rig.Dts.Peers.OnDpdu(new Management(new DpduHeader(0, 10, 3, Us, X), false, true, false, 5, Octets.Empty));
        rig.Dts.Peers.StateOf(X).Should().Be(DtsPeerState.ManagementUnconnected);
        rig.Dts.Peers.OnConnectionMade(new DConnectionMade(X, true, false));
        rig.Dts.Peers.StateOf(X).Should().Be(DtsPeerState.ManagementConnected);
        rig.Dts.Peers.OnConnectionMade(new DConnectionMade(X, true, false));
        rig.Dts.Peers.StateOf(X).Should().Be(DtsPeerState.ManagementConnected);
        rig.Dts.Peers.OnConnectionTerminated(Y);   // C-0572: unconnected, ignored
        rig.Dts.Peers.StateOf(Y).Should().Be(DtsPeerState.IdleUnconnected);
    }

    [Fact]
    public void Cas_settings_are_read_and_checked()
    {
        JsonObject defaults = Corpus.T2Defaults.DeepClone().AsObject();
        defaults["local_address"] = "0.0.1.2";
        ConfigReader.Read(defaults).Cas.Should().Be(CasConfig.Unset);
        var act = () => ConfigReader.Read(defaults).Cas.RequireMaxLinks();
        act.Should().Throw<MissingSettingException>().Which.Setting.Should().Be("cas.max_links");
        defaults["cas"] = JsonNode.Parse("""{"max_links":2,"link_request_timeout_ms":1,"link_request_retries":0,"first_data_timeout_ms":9}""");
        ConfigReader.Read(defaults).Cas.Should().Be(new CasConfig(2, 1, 0, 9));
        foreach (string key in new[] { "max_links", "link_request_timeout_ms", "first_data_timeout_ms" })
        {
            JsonObject bad = defaults.DeepClone().AsObject();
            bad["cas"]![key] = 0;
            var read = () => ConfigReader.Read(bad);
            read.Should().Throw<ConfigException>().Which.Setting.Should().Be("cas." + key);
        }
    }

    private sealed class Rig
    {
        private readonly Dispatcher _dispatcher;
        private bool _bound;

        public Rig(Func<NodeConfig, NodeConfig>? configure = null)
        {
            NodeConfig config = TestConfig.Basic();
            Context = new CoreContext(configure is null ? config : configure(config), new NullModem());
            Sis = new SisSublayer(Context);
            Cas = new CasSublayer(Context);
            Dts = new DtsSublayer(Context, new Log(MacLog));
            _dispatcher = new Dispatcher(Context, Sis, Cas, Dts, new Probe("mac", []));
        }

        public CoreContext Context { get; }

        public SisSublayer Sis { get; }

        public CasSublayer Cas { get; }

        public DtsSublayer Dts { get; }

        public List<string> MacLog { get; } = [];

        /// <summary>Outputs since the last check; each read of the list in an assertion should be followed by a new action.</summary>
        public List<CoreOutput> Outputs { get; } = [];

        public void ArqRequest(uint dst, byte confirm = 0, uint ttl = 0)
        {
            if (!_bound)
            {
                _dispatcher.Handle(new SisConnect(Context.Now, 1));
                _dispatcher.Handle(new SisBytes(Context.Now, 1, SisCodec.Encode(new SisBindRequest(5, new ServiceType(1, Confirm: confirm)))));
                Context.TakeOutputs();
                _bound = true;
            }

            Run(new SisBytes(Context.Now, 1, SisCodec.Encode(new SisUnidataRequest(0, 9, new SisAddress(dst), ServiceType.Default, ttl, new byte[] { 1 }))));
        }

        /// <summary>The DTS reports our pending link C_PDU to the peer sent (C-1100).</summary>
        public void Sent(uint peer)
        {
            int? h = Cas.PendingHandleOf(peer);
            Deliver(new DExpeditedUnidataRequestConfirm(h ?? 9999));
        }

        public void Deliver(ToCas primitive)
        {
            Outputs.Clear();
            Context.Enqueue(primitive);
            _dispatcher.Handle(new ChannelBusy(Context.Now, false));
            Outputs.AddRange(Context.TakeOutputs());
        }

        public void Expire(TimerName name, long? at = null)
        {
            long t = at ?? Context.Timers.Deadline(name) ?? throw new InvalidOperationException("not running");
            Run(new TimerExpired(t, name));
        }

        private void Run(CoreInput input)
        {
            Outputs.Clear();
            _dispatcher.Handle(input);
            Outputs.AddRange(Context.TakeOutputs());
        }
    }

    private sealed class Log(List<string> log) : IMacObserver
    {
        public void EotHeard(long predictedEndTime) => log.Add("EotHeard");

        public void DpduHeard(uint src, uint dst, bool dstIsGroup) => log.Add("DpduHeard");

        public void QueueState(bool nonEmpty) => log.Add("QueueState:" + nonEmpty);
    }
}
