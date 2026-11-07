using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// Delivery confirmation in the SIS (P6-1) on paths the sis-confirm corpus scenarios do not reach:
/// the two orders of a client confirmation, DATA DELIVERY FAIL reasons, answers that match nothing,
/// a held U_PDU reaching its TTD, the deadline list behind <c>sis.confirm_wait</c>, the TTD check on
/// receipt and the settings. The rig is <see cref="CasLinkTests.Rig"/>: real SIS, CAS and DTS.
/// </summary>
public class SisConfirmTests
{
    private const uint X = 0x0103;
    private const uint Y = 0x0104;
    private const uint Us = 0x0102;
    private const byte ConfirmNode = 1;
    private const byte ConfirmClient = 2;

    private static readonly TimerName Wait = new(TimerKind.SisConfirmWait);

    private static DExpeditedUnidataIndication Link(uint src, CPdu c) => new(src, Us, false, false, CpduCodec.Encode(c));

    private static CasLinkTests.Rig Rig(int? wait = null, int? frag = null) =>
        new(c => c with { Sis = c.Sis with { ConfirmWaitMs = wait, FragSize = frag }, Cas = new CasConfig(4, 60000, 2, 120000) });

    /// <summary>We call X for one U_PDU ({1}, SAP 5 to SAP 9) and X accepts; returns its handle.</summary>
    private static int CallerLink(CasLinkTests.Rig rig, byte confirm)
    {
        rig.ArqRequest(X, confirm: confirm);
        rig.Sent(X);
        rig.Deliver(Link(X, new PhysicalLinkAccepted(false, true, false)));
        return rig.Sis.Outstanding.Single().Handle;
    }

    /// <summary>An S_PDU from <paramref name="src"/> by ARQ, as the CAS passes it up.</summary>
    private static DUnidataIndication FromPeer(uint src, SPdu spdu) =>
        new(src, Us, false, true, CpduCodec.Encode(new CpduData(SpduCodec.Encode(spdu))));

    private static byte[] Primitive(SisPrimitive p) => SisCodec.Encode(p);

    private static List<byte[]> SisOut(CasLinkTests.Rig rig) => rig.Outputs.OfType<SisBytesOut>().Select(b => b.Bytes.ToArray()).ToList();

    private static readonly byte[] Confirmed = Primitive(new SisUnidataRequestConfirm(9, new SisAddress(X), new byte[] { 1 }));

    private static byte[] Rejected(byte reason) => Primitive(new SisUnidataRequestRejected(reason, 9, new SisAddress(X), new byte[] { 1 }));

    [Fact]
    public void Client_confirmation_needs_the_cas_confirm_and_the_peers_confirm_in_either_order()
    {
        // A-0227: both, whichever comes first.
        var rig = Rig();
        int handle = CallerLink(rig, ConfirmClient);
        rig.Deliver(new DUnidataRequestConfirm(handle));
        SisOut(rig).Should().BeEmpty("the DATA DELIVERY CONFIRM is still awaited");
        rig.Deliver(FromPeer(X, new SpduDeliveryConfirm(0, 5, 9, new byte[] { 1 })));
        SisOut(rig).Should().ContainSingle().Which.Should().Equal(Confirmed);
        rig.Outputs.Should().Contain(new TimerCancel(Wait));

        var other = Rig();
        handle = CallerLink(other, ConfirmClient);
        other.Deliver(FromPeer(X, new SpduDeliveryConfirm(0, 5, 9, new byte[] { 1 })));
        SisOut(other).Should().BeEmpty("the CAS has not confirmed yet");
        other.Deliver(new DUnidataRequestConfirm(handle));
        SisOut(other).Should().ContainSingle().Which.Should().Equal(Confirmed);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(0, 15)]
    [InlineData(5, 15)]
    public void A_delivery_fail_rejects_with_the_reason_mapped_from_it(byte failReason, byte rejectReason)
    {
        // A-0228, A-0229; interp A-0185: DDF 1 is SAP not bound, anything else Other.
        var rig = Rig();
        CallerLink(rig, ConfirmClient);
        rig.Deliver(FromPeer(X, new SpduDeliveryFail(failReason, 5, 9, new byte[] { 1 })));
        SisOut(rig).Should().ContainSingle().Which.Should().Equal(Rejected(rejectReason));
    }

    [Fact]
    public void A_delivery_fail_before_the_cas_confirm_still_rejects_and_the_later_confirm_is_ignored()
    {
        var rig = Rig();
        int handle = CallerLink(rig, ConfirmClient);
        rig.Deliver(FromPeer(X, new SpduDeliveryFail(1, 5, 9, new byte[] { 1 })));
        SisOut(rig).Should().ContainSingle().Which.Should().Equal(Rejected(2));
        rig.Deliver(new DUnidataRequestConfirm(handle));
        SisOut(rig).Should().BeEmpty();
    }

    [Fact]
    public void An_answer_from_another_node_with_other_saps_or_other_bytes_matches_nothing()
    {
        // Interp A-0343: node, both SAP IDs and the copy as the start of the U_PDU.
        var rig = Rig();
        int handle = CallerLink(rig, ConfirmClient);
        rig.Deliver(new DUnidataRequestConfirm(handle));
        rig.Deliver(FromPeer(Y, new SpduDeliveryConfirm(0, 5, 9, new byte[] { 1 })));
        rig.Deliver(FromPeer(X, new SpduDeliveryConfirm(0, 9, 5, new byte[] { 1 })));
        rig.Deliver(FromPeer(X, new SpduDeliveryConfirm(0, 5, 9, new byte[] { 2 })));
        rig.Deliver(FromPeer(X, new SpduDeliveryConfirm(0, 5, 9, new byte[] { 1, 0 })));
        SisOut(rig).Should().BeEmpty();

        // A partial copy, even an empty one, is the start of the U_PDU (A-0100, A-0103).
        rig.Deliver(FromPeer(X, new SpduDeliveryConfirm(0, 5, 9, ReadOnlyMemory<byte>.Empty)));
        SisOut(rig).Should().ContainSingle().Which.Should().Equal(Confirmed);
    }

    [Fact]
    public void A_node_confirmation_ignores_a_peer_confirm_and_follows_the_cas()
    {
        // A-0222: node confirmation is the CAS answer alone.
        var rig = Rig();
        int handle = CallerLink(rig, ConfirmNode);
        rig.Deliver(FromPeer(X, new SpduDeliveryConfirm(0, 5, 9, new byte[] { 1 })));
        SisOut(rig).Should().BeEmpty();
        rig.Deliver(new DUnidataRequestConfirm(handle));
        SisOut(rig).Should().ContainSingle().Which.Should().Equal(Confirmed);
    }

    [Theory]
    [InlineData((int)DReason.TtlExpired, 1)]
    [InlineData((int)DReason.NoConnection, 3)]
    [InlineData((int)DReason.ConnectionLost, 3)]
    [InlineData((int)DReason.Reset, 3)]
    public void A_cas_rejection_is_reported_with_its_mapped_reason(int reason, byte expected)
    {
        // A-0223, A-0229; interp A-0340.
        var rig = Rig();
        int handle = CallerLink(rig, ConfirmNode);
        rig.Deliver(new DUnidataRequestRejected(handle, (DReason)reason));
        SisOut(rig).Should().ContainSingle().Which.Should().Equal(Rejected(expected));
    }

    [Fact]
    public void A_held_unidata_reaching_its_ttd_is_dropped_with_reason_1()
    {
        // Interp A-0232, A-0342: TTL 5 is 10 s; the soft link is still being made.
        var rig = Rig(wait: 1_000_000);
        rig.ArqRequest(X, confirm: ConfirmNode, ttl: 5);
        rig.Outputs.Should().Contain(new TimerStart(Wait, 10_000));
        rig.Sis.AwaitingSoftLink.Should().ContainSingle();
        rig.Expire(Wait);
        SisOut(rig).Should().ContainSingle().Which.Should().Equal(Rejected(1));
        rig.Sis.AwaitingSoftLink.Should().BeEmpty();

        // The link is made later: nothing goes down for it.
        rig.Sent(X);
        rig.Deliver(Link(X, new PhysicalLinkAccepted(false, true, false)));
        rig.Sis.Outstanding.Should().BeEmpty();
    }

    [Fact]
    public void One_timer_runs_to_the_earliest_deadline_and_moves_only_when_the_head_changes()
    {
        // Collapsing rule (core-architecture.md 3.2): two U_PDUs handed down 1000 ms apart.
        var rig = Rig(wait: 50_000);
        int first = CallerLink(rig, ConfirmNode);
        rig.Outputs.Should().Contain(new TimerStart(Wait, 50_000));
        rig.Context.Now = 1000;
        rig.ArqRequest(X);
        rig.Outputs.OfType<TimerStart>().Where(t => t.Name == Wait).Should().BeEmpty("behind the head");
        int second = rig.Sis.Outstanding.Last().Handle;

        rig.Deliver(new DUnidataRequestConfirm(first));
        rig.Outputs.Should().Contain(new TimerStart(Wait, 50_000), "the head is now the second, due at 51000");
        rig.Expire(Wait);
        SisOut(rig).Should().ContainSingle().Which.Should().Equal(Rejected(3));
        rig.Outputs.Should().NotContain(o => o is TimerStart || o is TimerCancel, "the list is empty and the timer has expired");

        // A late CAS answer is ignored (A-0225: the wait is over).
        rig.Deliver(new DUnidataRequestConfirm(second));
        SisOut(rig).Should().BeEmpty();
    }

    [Fact]
    public void A_client_confirmation_with_the_cas_confirm_but_no_peer_answer_times_out_with_reason_3()
    {
        // A-0230: the wait ends with the DATA DELIVERY CONFIRM still missing; a late one is ignored.
        var rig = Rig(wait: 50_000);
        int handle = CallerLink(rig, ConfirmClient);
        rig.Deliver(new DUnidataRequestConfirm(handle));
        SisOut(rig).Should().BeEmpty();
        rig.Expire(Wait);
        SisOut(rig).Should().ContainSingle().Which.Should().Equal(Rejected(3));
        rig.Deliver(FromPeer(X, new SpduDeliveryConfirm(0, 5, 9, new byte[] { 1 })));
        SisOut(rig).Should().BeEmpty();
    }

    [Fact]
    public void Two_waits_ending_in_the_same_millisecond_are_both_answered_at_one_expiry()
    {
        // Collapsing rule (core-architecture.md 3.2): two U_PDUs held for the same soft link are handed
        // down by one link made, so their waits end together; one expiry rejects both, in insertion
        // order, and no timer is restarted for the second (it is due now) or cancelled (it has expired).
        var rig = Rig(wait: 50_000);
        rig.ArqRequest(X, confirm: ConfirmNode);
        rig.ArqRequest(X);
        rig.Sent(X);
        rig.Deliver(Link(X, new PhysicalLinkAccepted(false, true, false)));
        rig.Sis.Outstanding.Should().HaveCount(2);
        rig.Expire(Wait);
        List<byte[]> answers = SisOut(rig);
        answers.Should().HaveCount(2);
        answers.Should().AllSatisfy(a => a.Should().Equal(Rejected(3)));
        rig.Outputs.Should().NotContain(o => (o is TimerStart && ((TimerStart)o).Name == Wait) || (o is TimerCancel && ((TimerCancel)o).Name == Wait));
    }

    [Fact]
    public void An_unbind_ends_every_wait_of_its_sap_with_no_answer()
    {
        // A-0260: the client went away.
        var rig = Rig();
        int handle = CallerLink(rig, ConfirmNode);
        rig.Disconnect();
        rig.Outputs.Should().Equal(new TimerCancel(Wait));
        rig.Deliver(new DUnidataRequestConfirm(handle));
        SisOut(rig).Should().BeEmpty();
    }

    [Fact]
    public void Every_copy_is_cut_to_the_fragment_size()
    {
        // A-0101, A-0112, A-0113; interp A-0341: the over-MTU rejection and the confirmation both.
        var rig = Rig(frag: 2);
        int handle = CallerLink(rig, ConfirmNode);
        rig.Client(new SisUnidataRequest(0, 9, new SisAddress(X), ServiceType.Default, 0, new byte[SisSublayer.Mtu + 1]));
        SisOut(rig).Should().ContainSingle().Which.Should().Equal(Primitive(new SisUnidataRequestRejected(4, 9, new SisAddress(X), new byte[2])));
        rig.Deliver(new DUnidataRequestConfirm(handle));
        SisOut(rig).Should().ContainSingle().Which.Should().Equal(Confirmed, "a U_PDU no longer than the size is copied whole");
    }

    [Fact]
    public void A_null_fragment_size_copies_the_whole_unidata()
    {
        // Issue #30: null means a full copy, as before P6-1.
        var rig = Rig();
        rig.ArqRequest(X);
        byte[] big = new byte[SisSublayer.Mtu + 1];
        big[^1] = 7;
        rig.Client(new SisUnidataRequest(0, 9, new SisAddress(X), ServiceType.Default, 0, big));
        SisOut(rig).Should().ContainSingle().Which.Should().Equal(Primitive(new SisUnidataRequestRejected(4, 9, new SisAddress(X), big)));
    }

    [Fact]
    public void A_received_data_spdu_past_its_ttd_is_not_delivered_or_answered()
    {
        // Interp A-0167, A-0241: TTD day 1, second 0 is core time 0, passed at 61000.
        var rig = Rig();
        rig.ArqRequest(X);   // binds SAP 5
        rig.Context.Now = 61_000;
        rig.Deliver(FromPeer(X, new SpduData(0, 9, 5, true, new SpduTtd(1, 0), new byte[] { 1 })));
        SisOut(rig).Should().BeEmpty();
        rig.Dts.HeldArq.Should().NotContain(r => r.ClientConfirmation);
    }

    [Fact]
    public void A_received_data_spdu_asking_for_confirmation_is_answered_with_a_marked_confirm_or_fail()
    {
        // A-0237 to A-0239; interp A-0344, B-0318: priority and SAPs as received, the copy cut to the size.
        var rig = Rig(frag: 1);
        rig.ArqRequest(X);   // binds SAP 5 and holds a U_PDU for X
        rig.Deliver(FromPeer(X, new SpduData(3, 9, 5, true, null, new byte[] { 1, 2 })));
        SisOut(rig).Should().ContainSingle("the indication");
        rig.Deliver(FromPeer(X, new SpduData(4, 9, 6, true, null, new byte[] { 3, 4 })));
        SisOut(rig).Should().BeEmpty("SAP 6 is not bound");

        // No soft link yet, so both wait for it with the U_PDU, in arrival order.
        rig.Sent(X);
        rig.Deliver(Link(X, new PhysicalLinkAccepted(false, true, false)));
        List<CUnidataRequest> answers = rig.Sis.Outstanding.Where(r => r.ClientConfirmation).ToList();
        answers.Should().HaveCount(2);
        answers[0].Should().Match<CUnidataRequest>(r => r.Dst == X && r.Arq && r.Priority == 3 && !r.DeliverInOrder);
        SpduCodec.Decode(answers[0].SPdu.Span).Value.Should().Be(new SpduDeliveryConfirm(3, 9, 5, new byte[] { 1 }));
        SpduCodec.Decode(answers[1].SPdu.Span).Value.Should().Be(new SpduDeliveryFail(1, 9, 6, new byte[] { 3 }));
    }

    [Theory]
    [InlineData(2026, 1, 0, 61_000, 1, 0, true)]               // today, second 0: passed
    [InlineData(2026, 1, 0, 61_000, 1, 31, false)]             // today, second 62: ahead
    [InlineData(2026, 1, 0, 61_000, 0, 0, false)]              // day 16, 15 days ahead: the latest allowed
    [InlineData(2026, 1, 0, 61_000, 13, 21600, true)]          // 31 December 2025 (day 365) at noon: within the day before, passed
    [InlineData(2025, 365, 86000, 1_000_000, 1, 400, false)]   // across the year end: now 1 January at 600 s, TTD 800 s, ahead
    [InlineData(2025, 365, 86000, 1_000_000, 1, 200, true)]    // the same day at 400 s: passed
    [InlineData(2026, 1, 0, 61_000, 5, 0, false)]              // day 5, four days ahead
    [InlineData(2026, 20, 0, 0, 3, 100, true)]                 // day 19 at 200 s, yesterday, is the only day 3 in the window: passed
    public void A_received_ttd_is_resolved_within_a_day_back_and_fifteen_ahead(int year, int day, int second, long now, byte dayMod16, int halfSeconds, bool passed)
    {
        // Interp A-0167.
        TtdCalendar.HasPassed(new TimeConfig(year, day, second), new SpduTtd(dayMod16, (ushort)halfSeconds), now).Should().Be(passed);
    }

    [Fact]
    public void The_settings_are_read_null_when_unset_and_a_zero_wait_is_refused()
    {
        // A-0102, A-0114, A-0221, A-0226; issue #30: unset or null, the fragment size is a full copy.
        JsonObject Config(string sis) => ConfigMerge.Merge(Corpus.T2Defaults, JsonNode.Parse("{\"local_address\":\"0.0.1.2\",\"sis\":" + sis + "}")!.AsObject());
        NodeConfig unset = ConfigReader.Read(Config("{}"));
        unset.Sis.FragSize.Should().BeNull();
        unset.Sis.ConfirmWaitMs.Should().BeNull();
        NodeConfig set = ConfigReader.Read(Config("{\"frag_size\":0,\"confirm_wait_ms\":1}"));
        set.Sis.FragSize.Should().Be(0);
        set.Sis.ConfirmWaitMs.Should().Be(1);
        FluentActions.Invoking(() => ConfigReader.Read(Config("{\"confirm_wait_ms\":0}"))).Should().Throw<ConfigException>()
            .Which.Message.Should().Contain("sis.confirm_wait_ms");
    }
}
