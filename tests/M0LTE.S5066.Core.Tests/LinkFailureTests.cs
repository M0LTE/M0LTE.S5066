using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// The DTS side of P5-1: link failure it detects itself (C-0739, C-1250, C-1251) and its part in
/// clean close (B-0421, B-0422), driven on the DTS objects directly. End-to-end behaviour is in the
/// cas and dts-arq corpus scenarios.
/// </summary>
public class LinkFailureTests
{
    private const uint Us = 0x0102;
    private const uint X = 0x0103;

    private static DUnidataRequest Request(int handle, int priority = 0) =>
        new(X, false, true, priority, 1_000_000_000, false, 0, CpduCodec.Encode(new CpduData(new byte[] { 0x20, 0x59, 0x00, (byte)handle })), handle);

    [Fact]
    public void A_peer_silent_for_sixteen_answer_waits_loses_the_connection_and_rejects_what_is_unfinished()
    {
        // C-1250, C-1251, C-0866. How many waits expired does not matter, only how long the peer has been silent.
        var r = new Rig();
        r.Add(Request(7));
        r.Send();
        for (int k = 1; k <= 40; k++)
        {
            r.Now = k * ((DtsPeers.SilenceWaits * r.Wait) - 1) / 40;
            r.Peers.OnAckWaitExpired(X).Should().BeFalse($"wait {k}");
        }

        r.Take();
        r.Now = DtsPeers.SilenceWaits * r.Wait;
        r.Peers.OnAckWaitExpired(X).Should().BeTrue();
        List<InternalPrimitive> p = r.Take();
        p[0].Should().Be(new DConnectionLost(X));
        p[1].Should().Be(new DUnidataRequestRejected(7, DReason.ConnectionLost));
        r.Peers.IsConnected(X).Should().BeFalse();
        r.Peers.StateOf(X).Should().Be(DtsPeerState.IdleUnconnected);
        r.Tx.LinkOf(X).Should().BeNull();
    }

    [Fact]
    public void An_answer_from_the_peer_starts_the_silence_again_from_the_next_wait()
    {
        // C-1250: an ACK-ONLY that passes the synchronisation tests is an answer, even one that acknowledges nothing.
        var r = new Rig();
        r.Add(Request(7));
        r.Send();
        r.Now = (DtsPeers.SilenceWaits - 1) * r.Wait;
        r.Peers.OnAckWaitExpired(X).Should().BeFalse();
        r.Peers.OnDpdu(new AckOnly(new DpduHeader(0, 10, 3, Us, X), 0, 0, 0, Octets.Empty, null));
        r.Now += 1000;
        r.Add(Request(8));
        r.Send();   // the silence is timed from the end of this transmission
        long since = r.Now;
        r.Now = since + (DtsPeers.SilenceWaits * r.Wait) - 1;
        r.Peers.OnAckWaitExpired(X).Should().BeFalse();
        r.Peers.IsConnected(X).Should().BeTrue();
        r.Now++;
        r.Peers.OnAckWaitExpired(X).Should().BeTrue();
    }

    [Fact]
    public void A_warning_is_not_an_answer_and_a_reset_wait_expiry_checks_the_silence_too()
    {
        // C-1250: WARNINGs go to the CAS (B-0426) and are not answers; dts.reset_wait expiries check too (C-1172).
        var r = new Rig();
        r.Add(Request(7));
        r.Send();
        r.Now = r.Wait;
        r.Peers.OnDpdu(new Warning(new DpduHeader(0, 10, 3, Us, X), 9, 1));
        r.Now = DtsPeers.SilenceWaits * r.Wait;
        r.Peers.OnResetWaitTimer(X);
        r.Peers.IsConnected(X).Should().BeFalse();
        r.Take().OfType<DConnectionLost>().Should().ContainSingle();
    }

    [Fact]
    public void The_end_of_a_transmission_to_a_silent_peer_loses_the_connection_before_any_wait_starts()
    {
        // C-1250: checked when each of our transmissions to the peer ends, as well as at wait expiries.
        var r = new Rig();
        r.Add(Request(7));
        r.Send();
        r.Add(Request(8));
        r.Now = DtsPeers.SilenceWaits * r.Wait;
        r.Send(take: false);
        List<InternalPrimitive> p = r.Take(out List<CoreOutput> outputs);
        p.Should().Equal(new DConnectionLost(X), new DUnidataRequestRejected(7, DReason.ConnectionLost), new DUnidataRequestRejected(8, DReason.ConnectionLost));
        outputs.OfType<TimerStart>().Where(t => t.Name.Kind == TimerKind.DtsAckWait).Should().BeEmpty("no dts.ack_wait starts for a lost connection");
    }

    [Fact]
    public void A_long_quiet_spell_with_nothing_awaited_is_not_silence()
    {
        // C-1250: silence is timed only while an answer is awaited, so the first transmission after an idle spell does not lose the link.
        var r = new Rig();
        r.Now = 10 * DtsPeers.SilenceWaits * r.Wait;
        r.Add(Request(7));
        r.Send();
        r.Peers.IsConnected(X).Should().BeTrue();
        r.Now += (DtsPeers.SilenceWaits * r.Wait) - 1;
        r.Peers.OnAckWaitExpired(X).Should().BeFalse();
        r.Now++;
        r.Peers.OnAckWaitExpired(X).Should().BeTrue();
    }

    [Fact]
    public void Clean_close_is_answered_at_once_and_reported_once_when_the_link_becomes_clean()
    {
        // B-0421, B-0422.
        var r = new Rig();
        r.Peers.OnDpdu(new DataOnly(new DpduHeader(0, 10, 3, Us, X), new DataFlags(true, true, false, false, true, true), 0,
            new Octets(new byte[] { 0x00, 0x20, 0x59, 0x00, 0x41 }), true));
        r.Take();
        byte[] type6 = CpduCodec.Encode(new PhysicalLinkBreakRequest(0, 0));
        r.Peers.OnCloseRequest(new DCloseRequest(X, new ClosePolicy(true, 0), type6, 99));
        r.Take().Should().Equal(new DLinkCleanState(X, false));   // an acknowledgement is owed
        r.Tx.HeldArq.Should().ContainSingle().Which.Handle.Should().Be(99);
        r.Peers.CheckClean();
        r.Take().Should().BeEmpty();

        r.Send();   // the ACK-ONLY and the type 6 go; the type 6 awaits its acknowledgement
        r.Peers.CheckClean();
        r.Take().Should().BeEmpty();
        r.Peers.OnDpdu(new AckOnly(new DpduHeader(0, 10, 3, Us, X), 1, 0, 0, Octets.Empty, null));
        r.Take().Should().Equal(new DUnidataRequestConfirm(99));
        r.Peers.CheckClean();
        r.Take().Should().Equal(new DLinkCleanState(X, true));
        r.Peers.CheckClean();
        r.Take().Should().BeEmpty("reported once (B-0422)");
    }

    [Fact]
    public void A_close_holds_back_unstarted_cpdus_by_its_policy_and_lets_started_ones_finish()
    {
        // B-0247, B-0252, B-0253, B-0421.
        var r = new Rig(maxSegment: 8);
        r.Add(new DUnidataRequest(X, false, true, 3, 1_000_000_000, false, 0, new byte[20], 1));   // three segments, 8, 8 and 4 bytes
        r.Send(oneDpdu: true);   // the first segment only: the C_PDU has started
        r.Add(Request(2, priority: 4));
        r.Add(Request(3, priority: 6));
        r.Peers.OnCloseRequest(new DCloseRequest(X, new ClosePolicy(false, 5)));
        r.Take().Should().Equal(new DLinkCleanState(X, false));
        r.Tx.IsClean(X).Should().BeFalse();
        string sent = r.SendDescribe();
        sent.Should().Be("1,1,3", "the started C_PDU finishes, then the priority 6 one; the priority 4 one is held");
    }

    private sealed class Rig
    {
        private readonly CoreContext _context;

        public Rig(int maxSegment = 1023)
        {
            NodeConfig config = TestConfig.Basic(CsmaMode.Jitter) with { Dts = new DtsConfig(maxSegment, 127500) };
            _context = new CoreContext(config, new FixtureModem(config.Modem));
            Tx = new TransmitQueue(_context, new NoObserver());
            Peers = new DtsPeers(_context, Tx);
            Peers.OnConnectionMade(new DConnectionMade(X, true, false));
        }

        public TransmitQueue Tx { get; }

        /// <summary>The answer wait in force (C-1120, C-1250).</summary>
        public long Wait => _context.Config.AnswerWaitMs();

        public long Now
        {
            get => _context.Now;
            set => _context.Now = value;
        }

        public DtsPeers Peers { get; }

        /// <summary>An ARQ request for the connected peer, as the DTS takes it (C-0653).</summary>
        public void Add(DUnidataRequest request)
        {
            Peers.OnArqRequest(request.Dst);
            Tx.AddArq(request);
        }

        public List<InternalPrimitive> Take() => Take(out _);

        public List<InternalPrimitive> Take(out List<CoreOutput> outputs)
        {
            var list = new List<InternalPrimitive>();
            while (_context.TryDequeue(out InternalPrimitive p))
            {
                list.Add(p);
            }

            outputs = _context.TakeOutputs().ToList();
            return list;
        }

        /// <summary>Builds one transmission and ends it, as the MAC and the DTS would (core-architecture.md 5.7).</summary>
        public Transmit Send(bool oneDpdu = false, bool take = true)
        {
            if (oneDpdu)
            {
                // A window of 1 lets only the first segment go.
                _context.Config = _context.Config with { Dts = _context.Config.Dts with { MaxWindow = 1 } };
            }

            Transmit t = Tx.Build();
            Peers.CheckSilence(Tx.TakeSentPeers());
            Tx.OnTransmitDone();
            Peers.OnTransmitDone();
            Peers.OnWaitsStarted();
            _context.Config = _context.Config with { Dts = _context.Config.Dts with { MaxWindow = DtsConfig.MaxWindowLimit } };
            if (take)
            {
                Take();
            }

            return t;
        }

        /// <summary>Sends one transmission and lists, for each data D_PDU in it, the handle of its C_PDU (the last byte of each test segment; 0 for the long C_PDU, handle 1).</summary>
        public string SendDescribe()
        {
            Transmit t = Send();
            var handles = new List<int>();
            ReadOnlySpan<byte> bytes = t.Bytes.Span;
            int pos = 0;
            while (pos < bytes.Length && DpduCodec.DecodeFirst(bytes[pos..], PeerEdition.Ed4) is { Value: { } pdu, Consumed: int used })
            {
                if (pdu is DataOnly d)
                {
                    handles.Add(d.Segment.Span[^1] == 0 ? 1 : d.Segment.Span[^1]);
                }

                pos += used;
            }

            return string.Join(",", handles);
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
