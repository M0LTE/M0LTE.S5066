using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// P4-6: expedited ARQ receive and MANAGEMENT in the CONNECTED states, on paths the corpus scenarios
/// (dts-arq/expedited-rx-ack, queued-request-released, management-connected-then-data,
/// management-connected-warning-3) do not reach.
/// </summary>
public class ExpeditedArqTests
{
    private const uint Us = 0x0102;
    private const uint X = 0x0103;

    [Fact]
    public void Receiver_acknowledges_with_a_bitmap_and_delivers_once()
    {
        // C-0277, C-0282, C-0291, C-1008 to C-1012, C-1210.
        var rx = new ExpeditedReceiver();
        rx.Receive(5, 0, true, false, [1]).Should().BeNull();
        rx.Receive(5, 3, false, true, [4]).Should().BeNull();
        rx.Acknowledgement().RxLwe.Should().Be(1);
        rx.Acknowledgement().Bitmap.Should().Equal(0b10);   // the first bit is RX LWE + 1 = FSN 2, the second FSN 3
        rx.Partial.Should().BeTrue();
        rx.Receive(5, 2, false, false, [3]).Should().BeNull();
        rx.Receive(5, 1, false, false, [2]).Should().Equal(1, 2, 3, 4);
        rx.Partial.Should().BeFalse();
        rx.Acknowledgement().RxLwe.Should().Be(4, "one past the C_PDU's last FSN");
        rx.Acknowledgement().Bitmap.Should().BeEmpty();
        rx.Receive(5, 2, false, false, [3]).Should().BeNull("a copy is not delivered again");
        rx.Acknowledgement().RxLwe.Should().Be(4);
    }

    [Fact]
    public void A_new_cpdu_id_discards_a_partial_one_and_far_fsns_are_not_stored()
    {
        // C-0757, C-0289, C-1210.
        var rx = new ExpeditedReceiver();
        rx.Receive(1, 0, true, false, [1]);
        rx.Receive(2, 129, false, false, [9]).Should().BeNull();
        rx.Partial.Should().BeFalse("the old C_PDU is gone and FSN 129 is beyond 128 past RX LWE 0");
        rx.Receive(2, 128, false, false, [9]);
        rx.Acknowledgement().Bitmap.Should().HaveCount(16);
        rx.Reset();
        rx.Acknowledgement().RxLwe.Should().Be(0);
        rx.Acknowledgement().Bitmap.Should().BeEmpty();
    }

    [Fact]
    public void Type_4_moves_to_expedited_and_the_placed_type_5_leaves_it()
    {
        // C-0662, C-0702, C-0738, C-1212.
        var r = new Rig();
        r.Peers.OnDpdu(T4(0, 0, true, false, [0, 0]));
        r.Peers.StateOf(X).Should().Be(DtsPeerState.ExpeditedDataConnected);
        r.Peers.OnDpdu(T4(0, 0, true, false, [0, 0]));
        r.Tx.PendingControl.OfType<ExpeditedAckItem>().Should().ContainSingle("one type 5 per peer");
        r.Build().Should().Equal(5);
        r.Peers.StateOf(X).Should().Be(DtsPeerState.ExpeditedDataConnected, "the C_PDU is still partly received");
        r.Peers.OnDpdu(T4(0, 1, false, true, [0x99]));
        r.Take().OfType<DExpeditedUnidataIndication>().Should().ContainSingle().Which.Arq.Should().BeTrue();
        r.Build().Should().Equal(5);
        r.Peers.StateOf(X).Should().Be(DtsPeerState.IdleConnected, "no regular ARQ D_PDU has flowed");
    }

    [Fact]
    public void Type_5_in_idle_or_data_draws_warning_3_and_in_expedited_nothing()
    {
        // C-0663, C-0683, C-0703, C-1212.
        var r = new Rig();
        r.Peers.OnDpdu(T5());
        r.Take().Should().Equal(new DWarningTransmitted(X, 5, 3));
        r.Peers.StateOf(X).Should().Be(DtsPeerState.IdleConnected);
        r.Build();
        r.Peers.OnDpdu(T4(0, 0, true, false, [0]));
        r.Peers.OnDpdu(T5());
        r.Take().Should().BeEmpty();
        r.Tx.PendingControl.OfType<WarningItem>().Should().BeEmpty();
    }

    [Fact]
    public void Management_from_expedited_deletes_the_partial_cpdu_and_returns_to_expedited_for_the_deferred_type_5()
    {
        // C-0704, C-0761, C-0722, C-0748, C-1213, C-1215.
        var r = new Rig();
        r.Peers.OnDpdu(T4(0, 0, true, false, [1]));
        r.Peers.OnDpdu(Mgmt());
        r.Peers.StateOf(X).Should().Be(DtsPeerState.ManagementConnected);
        r.Build().Should().Equal(6, 5);
        r.Peers.StateOf(X).Should().Be(DtsPeerState.IdleConnected);

        // The type 5 went with RX LWE 0: the partial C_PDU was deleted.
        r.Peers.OnDpdu(T4(0, 1, false, true, [2]));
        r.Take().Should().BeEmpty("FSN 0 must come again");
    }

    [Fact]
    public void Type_4_in_management_is_acknowledged_after_the_management_acknowledgement()
    {
        // C-0722, C-0748, C-1213: MANAGEMENT(CONNECTED) leaves for EXPEDITED-DATA(CONNECTED), whose type 5 then ends it.
        var r = new Rig();
        r.Peers.OnDpdu(Mgmt());
        r.Peers.OnDpdu(T4(0, 0, true, true, [7]));
        r.Peers.StateOf(X).Should().Be(DtsPeerState.ManagementConnected);
        r.Take().OfType<DExpeditedUnidataIndication>().Should().ContainSingle();
        r.Build().Should().Equal(6, 5);
        r.Peers.StateOf(X).Should().Be(DtsPeerState.IdleConnected);
    }

    [Fact]
    public void A_reset_out_of_expedited_drops_the_partial_cpdu_and_the_owed_type_5()
    {
        // C-0701, C-0736, C-0761, C-1214.
        var r = new Rig();
        r.Peers.OnDpdu(T4(0, 0, true, false, [1]));
        r.Peers.OnDpdu(new ResetWinResync(H(), new ResetFlags(false, false, false, true), 0, 0));
        r.Peers.StateOf(X).Should().Be(DtsPeerState.DataConnected);
        r.Tx.PendingControl.OfType<ExpeditedAckItem>().Should().BeEmpty();
    }

    [Fact]
    public void A_new_connection_resets_the_expedited_machine()
    {
        // C-0755.
        var r = new Rig();
        r.Peers.OnDpdu(T4(4, 0, true, true, [1]));
        r.Take();
        r.Peers.OnConnectionTerminated(X);
        r.Tx.PendingControl.OfType<ExpeditedAckItem>().Should().BeEmpty();
        r.Peers.OnConnectionMade(new DConnectionMade(X, true, false));
        r.Peers.OnDpdu(T4(4, 0, true, true, [1]));
        r.Take().OfType<DExpeditedUnidataIndication>().Should().ContainSingle("C_PDU ID 4 is new on the new connection");
    }

    [Fact]
    public void Arq_data_and_type_7_wait_while_a_type_4_cpdu_is_partly_received()
    {
        // C-0693, C-0695, C-0747, C-0737, C-1215.
        var r = new Rig();
        r.Peers.OnDpdu(T4(0, 0, true, false, [1]));
        r.Peers.OnArqRequest(X);
        r.Tx.AddArq(new DUnidataRequest(X, false, true, 0, 1_000_000, false, 0, new byte[] { 0, 1 }, 1));
        r.Peers.OnNonArqRequest(X);
        r.Tx.Add(new DUnidataRequest(X, false, false, 0, 1_000_000, false, 0, new byte[] { 0, 2 }, 2));
        r.Peers.StateOf(X).Should().Be(DtsPeerState.ExpeditedDataConnected);
        r.Build().Should().Equal(5);
        r.Peers.OnDpdu(T4(0, 1, false, true, [2]));
        r.Build().Should().Equal(5, 9, 7);
        r.Peers.StateOf(X).Should().Be(DtsPeerState.DataConnected);
    }

    [Fact]
    public void A_non_arq_request_moves_idle_connected_to_data()
    {
        // C-0655.
        var r = new Rig();
        r.Peers.OnNonArqRequest(X);
        r.Peers.StateOf(X).Should().Be(DtsPeerState.DataConnected);
    }

    private static DpduHeader H() => new(0, 10, 3, Us, X);

    private static ExpeditedDataOnly T4(int id, int fsn, bool start, bool end, byte[] seg) => new(H(), start, end, id, fsn, new Octets(seg), true);

    private static ExpeditedAckOnly T5() => new(H(), 0, Octets.Empty);

    private static Management Mgmt() => new(H(), false, true, false, 0, Octets.Empty);

    private sealed class Rig
    {
        private readonly CoreContext _context;

        public Rig()
        {
            NodeConfig config = TestConfig.Basic(CsmaMode.Jitter);
            _context = new CoreContext(config, new FixtureModem(config.Modem));
            Tx = new TransmitQueue(_context, new NoObserver());
            Peers = new DtsPeers(_context, Tx);
            Peers.OnConnectionMade(new DConnectionMade(X, true, false));
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

        /// <summary>Builds one transmission and returns the D_PDU types in it, in order.</summary>
        public List<int> Build()
        {
            byte[] bytes = Tx.Build().Bytes.ToArray();
            var types = new List<int>();
            int at = 0;
            while (at < bytes.Length && bytes[at] == 0x90)
            {
                DecodeResult<Dpdu> d = DpduCodec.DecodeFirst(bytes.AsSpan(at), PeerEdition.Any);
                types.Add(d.Value!.Type);
                at += d.Consumed;
            }

            return types;
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
