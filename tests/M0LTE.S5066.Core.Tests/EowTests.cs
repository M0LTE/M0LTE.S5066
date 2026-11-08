using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// P7-3: the EOW scheduler (open question 1, interp C-1260) with synthetic recommendation content,
/// which the corpus cannot reach until P7-6 fills the repeating set, and the receive-side records
/// (interp C-1261, C-1262), which have no visible effect before P7-7. End-to-end behaviour is in
/// the drc corpus scenarios.
/// </summary>
public class EowTests
{
    private const uint Us = 0x0102;
    private const uint X = 0x0103;
    private const uint Y = 0x0104;
    private const uint G = 0x0F00;

    private static readonly int Capability = 0xF01;
    private static readonly MaxSpeedEow Max = new(new SpeedInterleaver(5, 6));
    private static readonly LowSpeedEow Low = new(new SpeedInterleaver(3, 4));
    private static readonly MaxSegmentEow Seg = new(255);
    private static readonly SenderApproachEow Approach = new(false, 2, false);

    [Fact]
    public void Capability_goes_to_ed4_peers_and_empty_to_everyone_else()
    {
        // C-0426, C-0488 to C-0491; C-0419, C-0938, interp C-0055; C-0937.
        var p = new PeerTable();
        p.SetEdition(X, KnownEdition.Ed4);
        p.SetEdition(Y, KnownEdition.Ed3);
        var s = new EowScheduler(p);
        s.BeginTransmission([]);
        s.Next(X, false).Should().Be(Capability);
        EowCodec.Decode(Capability).Should().Be(new CapabilityEow(false, true));
        s.Next(Y, false).Should().Be(0);
        s.Next(0x0105, false).Should().Be(0);   // unknown edition
        s.Next(X, true).Should().Be(0);         // X as a group address
        s.Next(G, true).Should().Be(0);
    }

    [Fact]
    public void An_owed_unrecognized_type_error_goes_first_once_to_a_peer_of_any_edition()
    {
        // C-1079 (an All-editions EOW, C-0401, C-0415); a group D_PDU does not take it.
        var p = new PeerTable();
        p.SetEdition(X, KnownEdition.Ed4);
        p.SetEdition(Y, KnownEdition.Ed3);
        var s = new EowScheduler(p);
        foreach (uint peer in new[] { X, Y, 0x0105u })
        {
            s.Owe(peer, 0x307);
        }

        s.BeginTransmission([]);
        s.Next(X, true).Should().Be(0);
        s.Next(X, false).Should().Be(0x307);
        s.Next(X, false).Should().Be(Capability);
        s.Next(Y, false).Should().Be(0x307);
        s.Next(Y, false).Should().Be(0);
        s.Next(0x0105, false).Should().Be(0x307);
        s.Next(0x0105, false).Should().Be(0);
    }

    [Fact]
    public void Headers_follow_owed_then_top_priority_then_sender_approach_then_the_rotation()
    {
        // Open question 1: per transmission, UNRECOGNIZED-TYPE-ERROR, the top-priority recommendations
        // (Table C-25, C-0949), SENDER-APPROACH when the transmission carries data to the peer, then the
        // rotation, which persists across transmissions; the top priority recurs (C-0947).
        var p = new PeerTable();
        p.SetEdition(X, KnownEdition.Ed4);
        var s = new EowScheduler(p)
        {
            TopPriority = _ => [Max, Low],
            RepeatingSet = _ => [Max, Low, Seg],
            SenderApproach = _ => Approach,
        };
        s.Owe(X, 0x30E);
        s.BeginTransmission([X]);
        Take(s, X, 7).Should().Equal(0x30E, Enc(Max), Enc(Low), Enc(Approach), Enc(Max), Enc(Low), Enc(Seg));

        s.BeginTransmission([]);   // no data to X this time: no SENDER-APPROACH
        Take(s, X, 4).Should().Equal(Enc(Max), Enc(Low), Enc(Max), Enc(Low));

        s.BeginTransmission([X]);
        Take(s, X, 4).Should().Equal(Enc(Max), Enc(Low), Enc(Approach), Enc(Seg));
    }

    [Fact]
    public void Each_peer_has_its_own_sequence()
    {
        // Open question 1, C-0936, C-0937: recommendations to several peers, each only in D_PDUs to it.
        var p = new PeerTable();
        p.SetEdition(X, KnownEdition.Ed4);
        p.SetEdition(Y, KnownEdition.Ed4);
        var s = new EowScheduler(p) { RepeatingSet = peer => peer == X ? [Max, Seg] : [Low] };
        s.BeginTransmission([]);
        s.Next(X, false).Should().Be(Enc(Max));
        s.Next(Y, false).Should().Be(Enc(Low));
        s.Next(X, false).Should().Be(Enc(Seg));
        s.Next(Y, false).Should().Be(Enc(Low));
    }

    [Fact]
    public void An_ed3_or_unknown_peer_never_gets_an_ed4_eow_even_with_recommendations_wanted()
    {
        // C-0419, C-0938: the providers are not consulted for a peer not recorded Ed4.
        var p = new PeerTable();
        p.SetEdition(Y, KnownEdition.Ed3);
        var s = new EowScheduler(p) { TopPriority = _ => [Max], RepeatingSet = _ => [Seg], SenderApproach = _ => Approach };
        s.BeginTransmission([Y, X]);
        Take(s, Y, 3).Should().Equal(0, 0, 0);
        Take(s, X, 3).Should().Equal(0, 0, 0);
    }

    [Fact]
    public void A_capability_with_the_ed4_bit_records_an_unknown_peer_ed4()
    {
        // Open question 2, interp C-1261, amending C-0055 and MAIN-0009; C-0045, C-0049, MAIN-0008.
        var r = new PeersRig();
        r.Peers.OnDpdu(Padding(0xF01, Us, X));
        r.Context.Peers.EditionOf(X).Should().Be(KnownEdition.Ed4);
        r.Context.Peers.RecordedByCas1(X).Should().BeFalse();

        r.Context.Peers.SetEdition(X, KnownEdition.Ed3);   // a later CAS-1 negotiation overwrites it (B-0408)
        r.Context.Peers.EditionOf(X).Should().Be(KnownEdition.Ed3);
    }

    [Fact]
    public void A_cas1_record_wins_over_a_capability()
    {
        // Open question 2, interp C-1261; B-0408.
        var r = new PeersRig();
        r.Context.Peers.SetEdition(X, KnownEdition.Ed3);
        r.Peers.OnDpdu(Padding(0xF01, Us, X));
        r.Context.Peers.EditionOf(X).Should().Be(KnownEdition.Ed3);
        r.Context.Peers.RecordedByCas1(X).Should().BeTrue();
    }

    [Fact]
    public void A_capability_with_the_ed4_bit_clear_or_not_to_our_individual_address_is_ignored()
    {
        // Open question 2 and 17, interp C-1261; C-0937.
        var r = new PeersRig();
        r.Peers.OnDpdu(Padding(0xF00, Us, X));
        r.Peers.OnDpdu(Padding(0xF02, Us, X));   // Duplex alone
        r.Peers.OnDpdu(Padding(0xF01, 0x0109, X));       // to another node
        var group = new NonArqFields(1, false, true, 2, 0, 10);
        r.Peers.OnDpdu(new NonArqData(new DpduHeader(0xF01, 10, 4, DpduAddress.MaxValue, X), group, new Octets([0, 5]), true));
        r.Context.Peers.EditionOf(X).Should().Be(KnownEdition.Unknown);
    }

    [Fact]
    public void Recommendation_eows_are_recorded_latest_first()
    {
        // C-0459 to C-0461, C-0472 to C-0475, C-0477 to C-0482, C-0427 to C-0441; interp C-1262.
        var r = new PeersRig();
        r.Peers.ReceivedEowsOf(X).Should().BeNull();
        r.Peers.OnDpdu(Padding(Enc(Max), Us, X));
        r.Peers.OnDpdu(Padding(Enc(Low), Us, X));
        r.Peers.OnDpdu(Padding(Enc(Seg), Us, X));
        r.Peers.OnDpdu(Padding(Enc(Approach), Us, X));
        r.Peers.OnDpdu(Padding(Enc(new Ed3BasicRateEow(5, 2, 3)), Us, X));
        ReceivedEows e = r.Peers.ReceivedEowsOf(X)!;
        (e.MaxSpeed, e.LowSpeed, e.MaxSegment, e.SenderApproach, e.Ed3BasicRate)
            .Should().Be((Max.Value, Low.Value, 255, Approach, new Ed3BasicRateEow(5, 2, 3)));

        r.Peers.OnDpdu(Padding(Enc(new MaxSpeedEow(new SpeedInterleaver(4, 13))), Us, X));
        r.Peers.OnDpdu(Padding(Enc(new MaxSegmentEow(25)), Us, X));
        (e.MaxSpeed, e.MaxSegment).Should().Be((new SpeedInterleaver(4, 13), 25));
        r.Tx.PendingControl.Should().BeEmpty();
        r.Tx.OwedEowFor(X).Should().BeNull();   // all known types: nothing to answer (C-0445)
    }

    [Theory]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(31)]
    public void A_reserved_speed_is_ignored_and_the_previous_kept(int speed)
    {
        // C-0466, interp C-0471.
        var r = new PeersRig();
        r.Peers.OnDpdu(Padding(Enc(Max), Us, X));
        r.Peers.OnDpdu(Padding(Enc(new MaxSpeedEow(new SpeedInterleaver(5, speed))), Us, X));
        r.Peers.OnDpdu(Padding(Enc(new LowSpeedEow(new SpeedInterleaver(5, speed))), Us, X));
        r.Peers.ReceivedEowsOf(X)!.MaxSpeed.Should().Be(Max.Value);
        r.Peers.ReceivedEowsOf(X)!.LowSpeed.Should().BeNull();
    }

    [Fact]
    public void Interleaver_111_max_segment_0_ed3_interleaving_11_and_speed_used_are_ignored()
    {
        // Interp C-0471, interp C-0476, C-0435, C-0944; interp C-1262.
        var r = new PeersRig();
        r.Peers.OnDpdu(Padding(Enc(new MaxSpeedEow(new SpeedInterleaver(7, 6))), Us, X));
        r.Peers.OnDpdu(Padding(Enc(new MaxSegmentEow(0)), Us, X));
        r.Peers.OnDpdu(Padding(Enc(new Ed3BasicRateEow(5, 3, 2)), Us, X));
        r.Peers.OnDpdu(Padding(Enc(new SpeedUsedEow(new SpeedInterleaver(5, 6))), Us, X));
        ReceivedEows? e = r.Peers.ReceivedEowsOf(X);
        (e?.MaxSpeed, e?.MaxSegment, e?.Ed3BasicRate).Should().Be((null, null, null));
        r.Tx.OwedEowFor(X).Should().BeNull();
    }

    [Fact]
    public void Interleaver_000_very_short_and_narrowband_speeds_are_recorded_as_sent()
    {
        // C-0464, C-0465, C-0469: not reserved, so kept for P7-7 to map (interp C-0470, open question 5).
        var r = new PeersRig();
        r.Peers.OnDpdu(Padding(Enc(new MaxSpeedEow(new SpeedInterleaver(0, 6))), Us, X));
        r.Peers.OnDpdu(Padding(Enc(new LowSpeedEow(new SpeedInterleaver(2, 22))), Us, X));
        r.Peers.ReceivedEowsOf(X)!.MaxSpeed.Should().Be(new SpeedInterleaver(0, 6));
        r.Peers.ReceivedEowsOf(X)!.LowSpeed.Should().Be(new SpeedInterleaver(2, 22));
    }

    [Fact]
    public void Ed4_recommendations_from_a_peer_recorded_ed3_are_ignored_but_ed3_basic_rate_is_kept()
    {
        // C-0419, C-0938, interp C-1262; C-0957.
        var r = new PeersRig();
        r.Context.Peers.SetEdition(X, KnownEdition.Ed3);
        r.Peers.OnDpdu(Type7(Enc(Max), Us, X));
        r.Peers.OnDpdu(Type7(Enc(Low), Us, X));
        r.Peers.OnDpdu(Type7(Enc(Seg), Us, X));
        r.Peers.OnDpdu(Type7(Enc(Approach), Us, X));
        r.Peers.OnDpdu(Type7(Enc(new Ed3BasicRateEow(4, 1, 2)), Us, X));
        ReceivedEows e = r.Peers.ReceivedEowsOf(X)!;
        (e.MaxSpeed, e.LowSpeed, e.MaxSegment, e.SenderApproach).Should().Be((null, null, null, null));
        e.Ed3BasicRate.Should().Be(new Ed3BasicRateEow(4, 1, 2));
    }

    [Fact]
    public void Recommendations_in_a_group_dpdu_are_ignored()
    {
        // C-0937, open question 17.
        var r = new PeersRig();
        var group = new NonArqFields(1, false, true, 2, 0, 10);
        r.Peers.OnDpdu(new NonArqData(new DpduHeader(Enc(Max), 10, 4, DpduAddress.MaxValue, X), group, new Octets([0, 5]), true));
        r.Peers.ReceivedEowsOf(X)?.MaxSpeed.Should().BeNull();
    }

    [Fact]
    public void A_capability_record_makes_padding_and_capability_go_to_the_peer()
    {
        // Interp C-1154 as amended (C-1261): PADDING once the peer is recorded Ed4 by either means; C-0426.
        NodeConfig config = TestConfig.Basic(CsmaMode.Jitter) with
        {
            Modem = new ModemFixture(720, 0, [new ModemWaveform(2, Interleaver.Long, 96, 4800)]),
        };
        var context = new CoreContext(config, new FixtureModem(config.Modem));
        var tx = new TransmitQueue(context, new NoObserver());
        var peers = new DtsPeers(context, tx);
        peers.OnDpdu(Padding(0xF01, Us, X));
        tx.Add(new DUnidataRequest(X, false, false, 0, 1_000_000, false, 0, new byte[16], 2));
        Transmit t = tx.Build();
        var list = new List<Dpdu>();
        ReadOnlySpan<byte> rest = t.Bytes.Span;
        while (rest.Length > 0)
        {
            DecodeResult<Dpdu> d = DpduCodec.DecodeFirst(rest, PeerEdition.Ed4);
            list.Add(d.Value!);
            rest = rest[d.Consumed..];
        }

        list.Select(d => d.Type).Should().Equal(7, 14, 14, 14, 14, 14);
        list.Should().OnlyContain(d => d.Header.Eow == Capability);
    }

    [Fact]
    public void Never_sends_ed3_capability_speed_used_or_ed3_basic_rate_to_an_ed4_peer()
    {
        // C-1019, C-0945, C-0958, C-0918: none of types 1, 2, 4, 12 is ever produced by the scheduler.
        var p = new PeerTable();
        p.SetEdition(X, KnownEdition.Ed4);
        p.SetEdition(Y, KnownEdition.Ed3);
        var s = new EowScheduler(p);
        s.BeginTransmission([X, Y]);
        var types = new[] { X, Y, 0x0105u }.SelectMany(peer => Take(s, peer, 5)).Select(e => e >> 8);
        types.Should().OnlyContain(t => t == 0 || t == 15);
    }

    [Theory]
    [InlineData(0x1A2)]
    [InlineData(0x200)]
    [InlineData(0x500)]
    [InlineData(0x600)]
    public void An_ed3_rate_or_frequency_change_management_message_is_acknowledged_and_refused_once(int eow)
    {
        // C-0919, C-0920, interp C-0921, interp C-1071: acknowledged, refused with WARNING reason 3 when new
        // by frame ID, acted on in nothing; a repeat is acknowledged again but not refused again (C-1074).
        var r = new PeersRig();
        var m = new Management(new DpduHeader(eow, 10, 4, Us, X), false, true, false, 9, Octets.Empty);
        r.Peers.OnDpdu(m);
        r.Peers.OnDpdu(m);
        var taken = new List<InternalPrimitive>();
        while (r.Context.TryDequeue(out InternalPrimitive p))
        {
            taken.Add(p);
        }

        taken.Should().Equal(new DWarningTransmitted(X, 6, 3));
        r.Tx.PendingControl.Select(c => c.Class).Should().Contain(ControlClass.Management).And.Contain(ControlClass.Warning);
        r.Peers.ReceivedEowsOf(X)?.Ed3BasicRate.Should().BeNull();   // nothing in it is acted on (C-0921), so not recorded (C-1262)
    }

    private static int Enc(Eow e) => EowCodec.Encode(e);

    private static List<int> Take(EowScheduler s, uint peer, int n) => Enumerable.Range(0, n).Select(_ => s.Next(peer, false)).ToList();

    private static Padding Padding(int eow, uint dst, uint src) => new(new DpduHeader(eow, 2, 4, dst, src));

    private static NonArqData Type7(int eow, uint dst, uint src) =>
        new(new DpduHeader(eow, 10, 4, dst, src), new NonArqFields(1, false, false, 2, 0, 10), new Octets([0, 5]), true);

    private sealed class PeersRig
    {
        public PeersRig()
        {
            NodeConfig config = TestConfig.Basic(CsmaMode.Jitter);
            Context = new CoreContext(config, new FixtureModem(config.Modem));
            Tx = new TransmitQueue(Context, new NoObserver());
            Peers = new DtsPeers(Context, Tx);
        }

        public CoreContext Context { get; }

        public TransmitQueue Tx { get; }

        public DtsPeers Peers { get; }
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
