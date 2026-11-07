using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// The D_PDU stream receiver (P3-3) and the MAC's use of what it hears, for behaviour a T2 trace
/// cannot show (what reaches the state machine, the quality record) or that is cheaper to pin
/// here (ed3 mode, prediction carry-over, cancelling csma.vdcd_end). The real DTS and MAC with
/// probe SIS and CAS; data is queued by calling the observer as the DTS would.
/// </summary>
public class DtsReceiverTests
{
    private const uint Us = 0x00000102;
    private const uint Peer = 0x00000103;
    private const uint Other = 0x00000109;

    private static readonly TimerName Lbt = TimerName.Of(TimerKind.CsmaLbtWait);
    private static readonly TimerName Cont = TimerName.Of(TimerKind.CsmaContWait);
    private static readonly TimerName Vdcd = TimerName.Of(TimerKind.CsmaVdcdEnd);

    private static readonly BurstEnded EndEom = new(0, BurstEndReason.Eom, 2, Interleaver.Long, 7, 85, 3);

    private static byte[] T7(uint src, uint dst, int eot, int id = 1, int segLength = 6, bool group = false) =>
        DpduCodec.Encode(new NonArqData(
            new DpduHeader(0, eot, group ? 7 : 4, dst, src),
            new NonArqFields(id, false, group, segLength, 0, 20),
            new Octets(Enumerable.Range(1, segLength).Select(i => (byte)i).ToArray()),
            true));

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    // ---------------------------------------------------------------- receiver

    [Fact]
    public void Good_dpdus_reach_the_state_machine_in_byte_order_whoever_they_are_addressed_to()
    {
        // C-0838, C-0868: every whole D_PDU with good CRCs is passed on; addressing is the state machine's job.
        var rig = new ReceiverRig();
        rig.Receiver.OnBurstStarted();
        rig.Receiver.OnBytesReceived(Cat(T7(Peer, Us, 4, id: 1), T7(Peer, Other, 4, id: 2)));
        rig.Delivered.Select(p => ((NonArqData)p).Fields.CPduId).Should().Equal(1, 2);
    }

    [Fact]
    public void A_bad_data_crc_is_discarded_after_its_header_is_used()
    {
        // C-0832, C-0836, C-0839: the header's EOT and addresses are used, the D_PDU is not passed on.
        byte[] bad = T7(Peer, Other, 10);
        bad[^5] ^= 0x80;
        var rig = new ReceiverRig { Now = 2000 };
        rig.Receiver.OnBurstStarted();
        rig.Receiver.OnBytesReceived(bad);
        rig.Delivered.Should().BeEmpty();
        rig.Log.Should().Equal("eot 7000", $"heard {Peer}->{Other} group=False");

        rig.Now = 3000;
        rig.Receiver.OnBurstEnded(EndEom);
        rig.Receiver.Quality[Peer].Should().Be(new RxQuality(3000, 85, 2, Interleaver.Long, 7, 1, 1));
    }

    [Fact]
    public void Eot_zero_reports_no_end_time_but_the_header_is_still_heard()
    {
        // C-0105: EOT 0 carries no end time.
        var rig = new ReceiverRig();
        rig.Receiver.OnBurstStarted();
        rig.Receiver.OnBytesReceived(T7(Peer, Other, 0));
        rig.Log.Should().Equal($"heard {Peer}->{Other} group=False");
    }

    [Fact]
    public void A_group_destination_is_reported_as_a_group()
    {
        // C-0122, C-0338, C-0339: GROUP ADDRESS of a type 7 is read from its header.
        var rig = new ReceiverRig();
        rig.Receiver.OnBurstStarted();
        rig.Receiver.OnBytesReceived(T7(Peer, 0x0FFFFFFF, 0, group: true));
        rig.Log.Should().Equal($"heard {Peer}->{0x0FFFFFFFu} group=True");
    }

    [Fact]
    public void Header_effects_happen_once_when_the_header_completes_even_if_data_arrives_later()
    {
        // C-1024: t_rx is the time of the block that completed the header; C-1025: act on a valid header at once.
        byte[] d = T7(Peer, Other, 10, segLength: 40);
        var rig = new ReceiverRig { Now = 1000 };
        rig.Receiver.OnBurstStarted();
        rig.Receiver.OnBytesReceived(d.AsSpan(0, 10));
        rig.Log.Should().BeEmpty();

        rig.Now = 2000;
        rig.Receiver.OnBytesReceived(d.AsSpan(10, 20));
        rig.Log.Should().Equal("eot 7000", $"heard {Peer}->{Other} group=False");
        rig.Delivered.Should().BeEmpty();

        rig.Now = 3000;
        rig.Receiver.OnBytesReceived(d.AsSpan(30));
        rig.Log.Should().HaveCount(2);
        rig.Delivered.Should().ContainSingle();
    }

    [Fact]
    public void A_partial_dpdu_is_dropped_at_the_burst_end_and_the_next_burst_starts_fresh()
    {
        // core-architecture.md 3.5 rule 1: the stream buffer is per burst.
        byte[] d = T7(Peer, Other, 10, id: 1);
        var rig = new ReceiverRig();
        rig.Receiver.OnBurstStarted();
        rig.Receiver.OnBytesReceived(d.AsSpan(0, d.Length - 3));
        rig.Receiver.OnBurstEnded(EndEom);
        rig.Receiver.OnBurstStarted();
        rig.Receiver.OnBytesReceived(d.AsSpan(d.Length - 3));
        rig.Receiver.OnBytesReceived(T7(Peer, Other, 4, id: 2));
        rig.Delivered.Select(p => ((NonArqData)p).Fields.CPduId).Should().Equal(2);
    }

    [Fact]
    public void A_false_sync_waiting_for_bytes_at_the_burst_end_does_not_hide_a_real_dpdu_inside_it()
    {
        // C-1025: at the burst end the waiting stops and the search moves on by one byte. A false
        // sync claiming a 21-byte header followed by a 12-byte PADDING, then the burst ends.
        byte[] pad = DpduCodec.Encode(new Padding(new DpduHeader(0, 6, 1, 0, 0)));
        pad.Length.Should().BeLessThan(21 - 6);
        var rig = new ReceiverRig { Now = 1000 };
        rig.Receiver.OnBurstStarted();
        rig.Receiver.OnBytesReceived(Cat([0x90, 0xEB, 0x70, 0x00, 0x00, 0x8F], pad));
        rig.Delivered.Should().BeEmpty();

        rig.Receiver.OnBurstEnded(EndEom);
        rig.Delivered.Should().ContainSingle().Which.Should().BeOfType<Padding>();
        rig.Log.Should().Equal("eot 4000", "heard 0->0 group=False");
    }

    [Fact]
    public void An_extension_is_passed_on_and_the_search_resumes_after_its_header()
    {
        // C-0365, C-0735: the state machine decides about WARNING reason 4 (P3-6).
        byte[] ext = DpduCodec.Encode(new Extension(new DpduHeader(0, 0, 4, Other, Peer), 7, Octets.Empty));
        var rig = new ReceiverRig();
        rig.Receiver.OnBurstStarted();
        rig.Receiver.OnBytesReceived(Cat(ext, [0x11, 0x22, 0x90, 0x33], T7(Peer, Other, 0)));
        rig.Delivered.Select(p => p.Type).Should().Equal(13, 7);
    }

    [Fact]
    public void A_bad_header_crc_is_counted_and_the_search_moves_on()
    {
        byte[] bad = T7(Peer, Other, 4);
        bad[4] ^= 0x01;
        var rig = new ReceiverRig();
        rig.Receiver.OnBurstStarted();
        rig.Receiver.OnBytesReceived(Cat(bad, T7(Peer, Other, 0, id: 2)));
        rig.Receiver.RejectedHeaders.Should().Be(1);
        rig.Delivered.Should().ContainSingle();
    }

    [Fact]
    public void A_sync_split_one_byte_per_block_is_found()
    {
        byte[] d = T7(Peer, Other, 0);
        var rig = new ReceiverRig();
        rig.Receiver.OnBurstStarted();
        foreach (byte b in Cat([0x90, 0x90], d))
        {
            rig.Receiver.OnBytesReceived([b]);
        }

        rig.Delivered.Should().ContainSingle();
    }

    // ---------------------------------------------------------------- MAC with the receiver

    [Fact]
    public void Ed3_mode_uses_the_one_common_value_and_no_rule_1_after_a_vdcd_idle()
    {
        // K-0107: ed3 mode has one LBT value (lbt_wait_dcd_ms) and LBT rule 1 off.
        var rig = new NodeRig(CsmaMode.Ed3);
        rig.Handle(new BurstStarted(1000));
        rig.Queue(1500).Should().BeEmpty();
        rig.Handle(new BytesReceived(2000, T7(Peer, Us, 10))).Should().BeEmpty();
        rig.Handle(EndEom with { T = 4000 }).Should().Equal(new TimerStart(Vdcd, 3000));
        rig.Handle(new TimerExpired(7000, Vdcd)).Should().Equal(new TimerStart(Lbt, 30000));
    }

    [Fact]
    public void A_new_burst_cancels_vdcd_end_and_the_earlier_prediction_still_binds()
    {
        // C-0844, C-1024: the old end time is carried; the later of the two is used. K-0111: the
        // timer runs only while DCD is false.
        var rig = new NodeRig(CsmaMode.Jitter);
        rig.Handle(new BurstStarted(1000));
        rig.Queue(1500);
        rig.Handle(new BytesReceived(2000, T7(Peer, Other, 20))).Should().BeEmpty();           // end 12000
        rig.Handle(EndEom with { T = 3000 }).Should().Equal(new TimerStart(Vdcd, 9000));
        rig.Handle(new BurstStarted(4000)).Should().Equal(new TimerCancel(Vdcd));
        rig.Handle(new BytesReceived(5000, T7(Peer, Other, 4))).Should().BeEmpty();            // end 7000
        rig.Handle(EndEom with { T = 6000 }).Should().Equal(new TimerStart(Vdcd, 6000));        // to 12000
        rig.Mac.PredictedEndTime.Should().Be(12000);
        rig.Handle(new TimerExpired(12000, Vdcd)).Should().Equal(new TimerStart(Lbt, 3000));
    }

    [Fact]
    public void An_emptied_queue_cancels_vdcd_end()
    {
        // K-0111: csma.vdcd_end runs only while data is queued.
        var rig = new NodeRig(CsmaMode.Jitter);
        rig.Handle(new BurstStarted(1000));
        rig.Queue(1500);
        rig.Handle(new BytesReceived(2000, T7(Peer, Other, 10)));
        rig.Handle(EndEom with { T = 3000 }).Should().Equal(new TimerStart(Vdcd, 4000));
        rig.Unqueue(3500).Should().Equal(new TimerCancel(Vdcd));
        rig.Mac.State.Should().Be(MacState.Sense);
    }

    [Fact]
    public void A_burst_addressed_only_to_us_meets_condition_a()
    {
        // K-0068, K-0072: condition (a) is recorded for the burst whose end produced the idle; an
        // end time already passed at the burst end leaves the idle DCD-determined (K-0073).
        var rig = new NodeRig(CsmaMode.Jitter);
        rig.Handle(new BurstStarted(1000));
        rig.Handle(new BytesReceived(2000, T7(Peer, Us, 2)));
        rig.Handle(EndEom with { T = 4000 });
        rig.Mac.LastBurstTwoNode.Should().BeTrue();
        rig.Mac.IdleByVdcd.Should().BeFalse();
    }

    [Fact]
    public void A_burst_with_only_group_dpdus_does_not_meet_condition_a()
    {
        // K-0068: D_PDUs addressed directly to this node are required.
        var rig = new NodeRig(CsmaMode.Jitter);
        rig.Handle(new BurstStarted(1000));
        rig.Queue(1500);
        rig.Handle(new BytesReceived(2000, T7(Peer, 0x0FFFFFFF, 4, group: true)));
        rig.Handle(EndEom with { T = 4000 }).Should().Equal(new TimerStart(Lbt, 3000));
        rig.Mac.LastBurstTwoNode.Should().BeFalse();
    }

    [Fact]
    public void Rule_1_zero_wait_chains_into_cont_wait_at_the_burst_end()
    {
        // K-0067, K-0079: with the prediction already passed at the burst end, rule 1 is tried
        // first (K-0072) and the zero LBT wait chains into CONT_WAIT within the same input.
        var rig = new NodeRig(CsmaMode.Jitter);
        rig.Handle(new BurstStarted(1000));
        rig.Queue(1500);
        rig.Handle(new BytesReceived(2000, T7(Peer, Us, 2)));
        rig.Handle(EndEom with { T = 4000 }).Should().Equal(new TimerStart(Cont, 45000));
    }

    [Fact]
    public void Rule_1_does_not_apply_when_an_earlier_bursts_eot_produced_the_idle()
    {
        // K-0067, K-0072: a burst to another node is lost before its end; a short burst to us
        // follows. The idle comes from the first burst's EOT, so the transmission whose end
        // produced it is not the one meeting condition (a): rule 3, not rule 1.
        var rig = new NodeRig(CsmaMode.Jitter);
        rig.Handle(new BurstStarted(1000));
        rig.Queue(1500);
        rig.Handle(new BytesReceived(2000, T7(Peer, Other, 20)));                               // end 12000
        rig.Handle(EndEom with { T = 3000 }).Should().Equal(new TimerStart(Vdcd, 9000));
        rig.Handle(new BurstStarted(4000)).Should().Equal(new TimerCancel(Vdcd));
        rig.Handle(new BytesReceived(5000, T7(Other, Us, 4)));                                  // end 7000
        rig.Handle(EndEom with { T = 6000 }).Should().Equal(new TimerStart(Vdcd, 6000));
        rig.Mac.LastBurstTwoNode.Should().BeTrue();
        rig.Handle(new TimerExpired(12000, Vdcd)).Should().Equal(new TimerStart(Lbt, 3000));
    }

    [Fact]
    public void Rule_1_applies_when_the_last_burst_outlasts_an_earlier_prediction()
    {
        // K-0067, K-0072: the earlier burst's end time has passed by the time the last burst's
        // own EOT clears, so the last burst produced the idle and rule 1 applies.
        var rig = new NodeRig(CsmaMode.Jitter);
        rig.Handle(new BurstStarted(1000));
        rig.Queue(1500);
        rig.Handle(new BytesReceived(2000, T7(Peer, Other, 8)));                                // end 6000
        rig.Handle(EndEom with { T = 3000 });
        rig.Handle(new BurstStarted(4000));
        rig.Handle(new BytesReceived(5000, T7(Peer, Us, 10)));                                  // end 10000
        rig.Handle(EndEom with { T = 6000 }).Should().Equal(new TimerStart(Vdcd, 4000));
        rig.Handle(new TimerExpired(10000, Vdcd)).Should().Equal(new TimerStart(Cont, 45000));
    }

    [Fact]
    public void A_header_waits_for_its_whole_address_field_before_the_edition_lookup()
    {
        // core-architecture.md 10 item 8: the sender's edition comes from the source address, so a
        // candidate whose address field is split across blocks waits rather than being checked.
        byte[] d = T7(Peer, Other, 10);
        var rig = new ReceiverRig { Now = 1000 };
        rig.Receiver.OnBurstStarted();
        rig.Receiver.OnBytesReceived(d.AsSpan(0, 7));
        rig.Log.Should().BeEmpty();
        rig.Receiver.RejectedHeaders.Should().Be(0);
        rig.Now = 2000;
        rig.Receiver.OnBytesReceived(d.AsSpan(7));
        rig.Log.Should().Equal("eot 7000", $"heard {Peer}->{Other} group=False");
        rig.Delivered.Should().ContainSingle();
    }

    private sealed class ReceiverRig : IMacObserver
    {
        private readonly CoreContext _context = new(TestConfig.Basic(CsmaMode.Jitter), new NullModem());

        public ReceiverRig() => Receiver = new DtsReceiver(_context, this, Delivered.Add);

        public long Now
        {
            get => _context.Now;
            set => _context.Now = value;
        }

        public DtsReceiver Receiver { get; }

        public List<Dpdu> Delivered { get; } = [];

        public List<string> Log { get; } = [];

        public void EotHeard(long predictedEndTime) => Log.Add($"eot {predictedEndTime}");

        public void DpduHeard(uint src, uint dst, bool dstIsGroup) => Log.Add($"heard {src}->{dst} group={dstIsGroup}");

        public void QueueState(bool nonEmpty) => Log.Add($"queue {nonEmpty}");
    }

    private sealed class NodeRig
    {
        private readonly Dispatcher _dispatcher;
        private readonly CoreContext _context;

        public NodeRig(CsmaMode mode)
        {
            var log = new List<string>();
            _context = new CoreContext(TestConfig.Basic(mode), new NullModem());
            Mac = new MacSublayer(_context, new SplitMix64(0));
            _dispatcher = new Dispatcher(_context, new Probe("sis", log), new Probe("cas", log), new DtsSublayer(_context, Mac), Mac);
        }

        public MacSublayer Mac { get; }

        public List<CoreOutput> Queue(long t)
        {
            Mac.QueueState(true);
            return Handle(new SisBytes(t, 1, new byte[] { 0 }));
        }

        public List<CoreOutput> Unqueue(long t)
        {
            Mac.QueueState(false);
            return Handle(new SisBytes(t, 1, new byte[] { 0 }));
        }

        public List<CoreOutput> Handle(CoreInput input)
        {
            _dispatcher.Handle(input);
            return _context.TakeOutputs();
        }
    }
}
