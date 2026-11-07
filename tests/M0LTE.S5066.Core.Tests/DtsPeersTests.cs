using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// The P3-6 unconnected-state receive machine and non-ARQ reassembly on paths the corpus
/// scenarios do not reach: type 8 delivery (only CAS-1 linking sends type 8, P4-1), malformed and
/// reused C_PDU IDs, zero windows, and the per-peer state bookkeeping. End-to-end behaviour is in
/// the dts-nonarq and sis corpus scenarios.
/// </summary>
public class DtsPeersTests
{
    private const uint Us = 0x0102;
    private const uint X = 0x0103;

    [Fact]
    public void A_complete_type_8_cpdu_goes_up_with_the_expedited_indication()
    {
        // C-0540, C-0732.
        var r = new Rig();
        r.Peers.OnDpdu(T(X, Us, 3, [0, 1, 2], 0, 3, 10, expedited: true));
        DExpeditedUnidataIndication d = r.Take().Should().ContainSingle().Which.Should().BeOfType<DExpeditedUnidataIndication>().Which;
        (d.Src, d.Dst, d.Arq).Should().Be((X, Us, false));
        d.CPdu.ToArray().Should().Equal(0, 1, 2);
        r.Peers.StateOf(X).Should().Be(DtsPeerState.ExpeditedDataUnconnected);
    }

    [Fact]
    public void Type_7_and_type_8_ids_are_separate_spaces()
    {
        // C-0328, C-0359: the same ID in the two types names two C_PDUs.
        var r = new Rig();
        r.Peers.OnDpdu(T(X, Us, 3, [0, 1, 2, 3], 0, 2, 10));
        r.Peers.OnDpdu(T(X, Us, 3, [0, 1, 2, 3], 2, 2, 10, expedited: true));
        r.Take().Should().BeEmpty();
        r.Peers.Reassembler.PartialCount.Should().Be(2);
    }

    [Fact]
    public void The_same_id_to_different_destinations_names_different_cpdus()
    {
        // C-1076: a sender may number C_PDUs per destination, so the same source and ID sent to
        // broadcast and to this node individually are two C_PDUs, neither completing the other.
        var r = new Rig();
        var group = new NonArqFields(4, false, true, 4, 0, 10);
        r.Peers.OnDpdu(new NonArqData(new DpduHeader(0, 10, 4, DpduAddress.MaxValue, X), group, new Octets([1, 2]), true));
        r.Peers.OnDpdu(T(X, Us, 4, [3, 4, 5, 6], 2, 2, 10));
        r.Take().Should().BeEmpty();
        r.Peers.Reassembler.PartialCount.Should().Be(2);
    }

    [Fact]
    public void A_segment_outside_its_cpdu_is_discarded()
    {
        // C-1076.
        var r = new Rig();
        var f = new NonArqFields(1, false, false, 4, 3, 10);
        r.Peers.OnDpdu(new NonArqData(new DpduHeader(0, 10, 4, Us, X), f, new Octets([1, 2]), true));
        r.Take().Should().BeEmpty();
        r.Peers.Reassembler.PartialCount.Should().Be(0);
        r.Outputs().Should().BeEmpty();
    }

    [Fact]
    public void A_different_cpdu_size_under_a_held_id_starts_a_new_cpdu()
    {
        // C-1076: the held 4-byte C_PDU is replaced by the 2-byte one, which completes.
        var r = new Rig();
        r.Peers.OnDpdu(T(X, Us, 5, [9, 9, 9, 9], 0, 2, 10));
        r.Peers.OnDpdu(T(X, Us, 5, [7, 7], 0, 2, 10));
        r.Take().Should().ContainSingle().Which.As<DUnidataIndication>().CPdu.ToArray().Should().Equal(7, 7);
        r.Peers.Reassembler.PartialCount.Should().Be(0);
    }

    [Fact]
    public void A_zero_window_segment_completes_now_or_never()
    {
        // C-0350 with window 0, core-architecture.md 3.3: nothing is held and no timer starts.
        var r = new Rig();
        r.Peers.OnDpdu(T(X, Us, 6, [1, 2, 3, 4], 0, 2, 0));
        r.Peers.OnDpdu(T(X, Us, 6, [1, 2, 3, 4], 2, 2, 0));
        r.Take().Should().BeEmpty();
        r.Outputs().Should().BeEmpty();
        r.Peers.Reassembler.PartialCount.Should().Be(0);
    }

    [Fact]
    public void The_timer_follows_the_earliest_incomplete_deadline()
    {
        // Collapsing rule (core-architecture.md 3.2): an insertion behind the head emits nothing,
        // one ahead restarts the timer, and expiry discards only what is due (C-0336).
        var r = new Rig();
        r.Peers.OnDpdu(T(X, Us, 1, new byte[4], 0, 2, 10));   // deadline 5000
        r.Peers.OnDpdu(T(X, Us, 2, new byte[4], 0, 2, 20));   // 10000, behind the head
        r.Peers.OnDpdu(T(X, Us, 3, new byte[4], 0, 2, 4));    // 2000, ahead
        r.Outputs().Should().Equal(
            new TimerStart(new TimerName(TimerKind.DtsReceptionWindow), 5000),
            new TimerStart(new TimerName(TimerKind.DtsReceptionWindow), 2000));

        r.Expire(2000);
        r.Outputs().Should().Equal(new TimerStart(new TimerName(TimerKind.DtsReceptionWindow), 3000));
        r.Peers.Reassembler.PartialCount.Should().Be(2);
    }

    [Fact]
    public void Management_state_is_left_when_the_acknowledgement_is_placed()
    {
        // C-0584, C-1070, C-0738; C-0644 and C-0645: a type 7 received meanwhile does not leave MANAGEMENT.
        var r = new Rig();
        r.Peers.OnDpdu(new Management(new DpduHeader(0, 10, 4, Us, X), false, true, false, 1, Octets.Empty));
        r.Peers.StateOf(X).Should().Be(DtsPeerState.ManagementUnconnected);
        r.Peers.OnDpdu(T(X, Us, 1, [0, 5], 0, 2, 10));
        r.Peers.StateOf(X).Should().Be(DtsPeerState.ManagementUnconnected);
        r.Tx.Build();
        r.Peers.StateOf(X).Should().Be(DtsPeerState.IdleUnconnected);
    }

    [Fact]
    public void A_management_acknowledgement_received_is_not_acknowledged()
    {
        // C-0302, C-1072.
        var r = new Rig();
        r.Peers.OnDpdu(new Management(new DpduHeader(0, 10, 4, Us, X), false, false, true, 1, Octets.Empty));
        r.Tx.PendingControl.Should().BeEmpty();
        r.Peers.StateOf(X).Should().Be(DtsPeerState.IdleUnconnected);
    }

    [Fact]
    public void A_received_warning_goes_to_the_cas_and_is_not_answered()
    {
        // C-0589, C-0393.
        var r = new Rig();
        r.Peers.OnDpdu(new Warning(new DpduHeader(0, 10, 4, Us, X), 9, 1));
        r.Take().Should().Equal(new DWarningReceived(X, Us, 9, 1));
        r.Tx.PendingControl.Should().BeEmpty();
    }

    [Fact]
    public void A_warning_is_reported_to_the_cas_when_queued()
    {
        // C-0578, C-0733, C-1075.
        var r = new Rig();
        r.Peers.OnDpdu(new ExpeditedAckOnly(new DpduHeader(0, 10, 4, Us, X), 0, Octets.Empty));
        r.Take().Should().Equal(new DWarningTransmitted(X, 5, 1));
        r.Tx.PendingControl.Should().ContainSingle().Which.Should().BeOfType<WarningItem>();
    }

    [Fact]
    public void An_undefined_type_from_an_ed3_peer_draws_reason_0()
    {
        // C-0388: unreachable before P4-1 records an Ed3 peer, so driven directly.
        var r = new Rig();
        r.Peers.OnDpdu(new UndefinedTypeDpdu(new DpduHeader(0, 10, 4, Us, X), 10, Octets.Empty));
        r.Take().Should().Equal(new DWarningTransmitted(X, 10, 0));
    }

    [Fact]
    public void The_cas_delivers_a_data_cpdu_from_the_expedited_service()
    {
        // B-0298: a DATA C_PDU is unwrapped whichever DTS service delivered it; other C_PDUs wait for P4-1.
        var context = new CoreContext(TestConfig.Basic(), new NullModem());
        var cas = new CasSublayer(context);
        cas.On(new DExpeditedUnidataIndication(X, Us, false, false, new byte[] { 0x00, 0x00, 0x99, 0x00, 0x41 }));
        context.TryDequeue(out InternalPrimitive p).Should().BeTrue();
        p.Should().BeOfType<CUnidataIndication>().Which.SPdu.ToArray().Should().Equal(0x00, 0x99, 0x00, 0x41);
        cas.On(new DWarningReceived(X, Us, 9, 1));
        cas.On(new DWarningTransmitted(X, 9, 1));
        context.TryDequeue(out _).Should().BeFalse();
    }

    private static Dpdu T(uint src, uint dst, int id, byte[] cpdu, int offset, int len, int window, bool expedited = false)
    {
        var f = new NonArqFields(id, false, false, cpdu.Length, offset, window);
        var h = new DpduHeader(0, 10, 4, dst, src);
        var seg = new Octets(cpdu.AsSpan(offset, len));
        return expedited ? new ExpeditedNonArqData(h, f, seg, true) : new NonArqData(h, f, seg, true);
    }

    [Fact]
    public void An_ed3_family_dpdu_on_a_16_bit_link_draws_warning_3()
    {
        // C-0763, C-0391, interp C-1192: the families are never mixed on a link, so a type 0 on a
        // 16-bit link is not used, changes no state and draws WARNING reason 3 naming type 0.
        var r = new Rig();
        r.Peers.OnConnectionMade(new DConnectionMade(X, true, false));
        r.Peers.OnDpdu(new Ed3DataOnly(new DpduHeader(0, 10, 3, Us, X), new DataFlags(true, true, false, false, true, true), 0, new Octets([1]), true));
        r.Take().Should().ContainSingle().Which.Should().Be(new DWarningTransmitted(X, 0, 3));
        r.Tx.PendingControl.Should().ContainSingle().Which.Should().Match<WarningItem>(w => w.ReceivedFrameType == 0 && w.Reason == 3);
        r.Tx.LinkOf(X)!.RxLwe.Should().Be(0);
        r.Peers.StateOf(X).Should().Be(DtsPeerState.IdleConnected);
    }

    [Fact]
    public void Ed3_data_on_an_8_bit_link_is_delivered_and_owes_an_ed3_acknowledgement()
    {
        // C-0658, C-0730, C-1190: a type 0 on a link with the 8-bit family is processed as a DATA-ONLY.
        var r = new Rig();
        r.Peers.OnConnectionMade(new DConnectionMade(X, false, true));
        r.Peers.OnDpdu(new Ed3DataOnly(new DpduHeader(0, 10, 3, Us, X), new DataFlags(true, true, false, false, true, true), 0, new Octets([7]), true));
        List<object> up = [.. r.Take()];
        up[0].Should().Be(new DPeerArqHeard(X));   // interp B-0452: the first ARQ D_PDU from the peer
        up.Skip(1).Should().ContainSingle().Which.As<DUnidataIndication>().CPdu.ToArray().Should().Equal(7);
        ArqLink link = r.Tx.LinkOf(X)!;
        (link.ShortFamily, link.RxLwe, link.AckOwed).Should().Be((true, 1, true));
        r.Peers.StateOf(X).Should().Be(DtsPeerState.DataConnected);
    }

    [Fact]
    public void A_full_reset_on_an_8_bit_link_is_answered_with_a_type_3()
    {
        // C-0251, C-0730, C-0811 to C-0818: the FULL RESET CMD comes as a type 3 and the FULL RESET ACK goes as one.
        var r = new Rig();
        r.Peers.OnConnectionMade(new DConnectionMade(X, false, true));
        r.Peers.OnDpdu(new Ed3ResetWinResync(new DpduHeader(0, 10, 3, Us, X), new ResetFlags(true, false, false, false), 0, 5));
        ControlItem ack = r.Tx.PendingControl.Should().ContainSingle().Which;
        ack.Build(new DpduHeader(0, 0, 3, X, Us)).Should().Be(new Ed3ResetWinResync(new DpduHeader(0, 0, 3, X, Us), new ResetFlags(false, false, false, true), 0, 0));
    }

    [Fact]
    public void A_16_bit_reset_on_an_8_bit_link_draws_warning_3()
    {
        // C-0763, interp C-1192: a type 12 from an Ed4 peer on a Short link.
        var r = new Rig();
        r.Peers.OnConnectionMade(new DConnectionMade(X, true, true));
        r.Peers.OnDpdu(new ResetWinResync(new DpduHeader(0, 10, 3, Us, X), new ResetFlags(true, false, false, false), 0, 5));
        r.Take().Should().ContainSingle().Which.Should().Be(new DWarningTransmitted(X, 12, 3));
        r.Tx.PendingControl.Should().ContainSingle().Which.Should().BeOfType<WarningItem>();
    }

    private sealed class Rig
    {
        private readonly CoreContext _context;

        public Rig()
        {
            NodeConfig config = TestConfig.Basic(CsmaMode.Jitter);
            _context = new CoreContext(config, new FixtureModem(config.Modem));
            Tx = new TransmitQueue(_context, new NoObserver());
            Peers = new DtsPeers(_context, Tx);
        }

        public TransmitQueue Tx { get; }

        public DtsPeers Peers { get; }

        public List<InternalPrimitive> Take()
        {
            var list = new List<InternalPrimitive>();
            while (_context.TryDequeue(out InternalPrimitive p))
            {
                list.Add(p);
            }

            return list;
        }

        public List<CoreOutput> Outputs() => _context.TakeOutputs();

        public void Expire(long t)
        {
            _context.Now = t;
            _context.Timers.Expire(new TimerName(TimerKind.DtsReceptionWindow), t);
            Peers.OnReceptionWindowTimer();
        }
    }

    private sealed class NoObserver : IMacObserver
    {
        public void EotHeard(long predictedEndTime)
        {
        }

        public void DpduHeard(uint src, uint dst, bool dstIsGroup)
        {
        }

        public void QueueState(bool nonEmpty)
        {
        }
    }
}
