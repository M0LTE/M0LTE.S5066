using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// The P3-4 transmit queue and planner on paths no phase 3 trace can reach: expedited (type 8)
/// requests come from the CAS-1 linking of P4-1, and ARQ requests are held by the SIS until then.
/// The end-to-end behaviour (EOT, budget, ordering, the MAC) is in the corpus scenarios.
/// </summary>
public class TransmitQueueTests
{
    private const uint X = 0x0103;
    private const uint Y = 0x0104;

    [Fact]
    public void Expedited_goes_first_with_its_own_id_space()
    {
        // C-0288 (expedited never behind normal data), C-0328 and C-0359 (separate ID spaces), C-1040.
        var q = new Rig();
        q.Tx.Add(Normal(X, 5, [1]));
        q.Tx.Add(Normal(X, 5, [2]));
        q.Tx.AddExpedited(Expedited(Y, [3]));
        List<Dpdu> pdus = q.Decode(q.Tx.Build());
        pdus.Select(p => p.Type).Should().Equal(8, 7, 7);
        pdus.Select(Id).Should().Equal(0, 0, 1);
        q.Observer.Log.Last().Should().Be("QueueState:False");
    }

    [Fact]
    public void Normal_data_waits_behind_unplaced_expedited_data_to_the_same_destination()
    {
        // C-0615, C-0737, C-1044: the second expedited C_PDU to X does not fit, so the type 7 to X
        // waits; the type 7 to Y is not held. A misfit expedited item is skipped, not a stop.
        var q = new Rig(maxTransmissionMs: 720 + 4800);   // one 96-byte block
        q.Tx.AddExpedited(Expedited(X, new byte[10]));
        q.Tx.AddExpedited(Expedited(X, new byte[70]));
        q.Tx.Add(Normal(X, 15, [1]));
        q.Tx.Add(Normal(Y, 0, [2]));
        List<Dpdu> pdus = q.Decode(q.Tx.Build());
        pdus.Select(p => (p.Type, p.Header.Dst)).Should().Equal((8, X), (7, Y));
        q.Tx.Count.Should().Be(2);
        q.Observer.Log.Last().Should().Be("QueueState:True");

        // The 95-byte type 8 fills the next block on its own; the type 7 to X then goes last.
        q.Decode(q.Tx.Build()).Select(p => (p.Type, p.Header.Dst)).Should().Equal((8, X));
        q.Decode(q.Tx.Build()).Select(p => (p.Type, p.Header.Dst)).Should().Equal((7, X));
    }

    [Fact]
    public void Lower_priority_never_overtakes_a_data_item_that_does_not_fit()
    {
        // C-0851, C-0880: the 60-byte C_PDU does not fit after the first, so the small one waits too.
        var q = new Rig(maxTransmissionMs: 720 + 4800);
        q.Tx.Add(Normal(X, 9, new byte[20]));
        q.Tx.Add(Normal(X, 8, new byte[60]));
        q.Tx.Add(Normal(X, 1, [1]));
        q.Decode(q.Tx.Build()).Should().ContainSingle();
        q.Tx.Count.Should().Be(2);
    }

    [Fact]
    public void A_dpdu_longer_than_127_5_s_on_the_waveform_is_a_configuration_error()
    {
        var q = new Rig(blockBytes: 8, blockMs: 10000);
        q.Tx.Add(Normal(X, 0, new byte[200]));
        Action act = () => q.Tx.Build();
        act.Should().Throw<ConfigException>().Which.Setting.Should().Be("drc.default_speed");
    }

    [Fact]
    public void The_eom_counts_against_the_length_limit()
    {
        // Interp C-1290: one 64-byte block within the limit, 4 bytes of it the EOM, so it carries 60.
        // 8-byte segments make 32-byte D_PDUs; two would fill the block and push the EOM into a
        // second block, over dts.max_transmission_ms. Every transmission stays within the limit.
        var q = new Rig(maxTransmissionMs: 720 + 4800, blockBytes: 64, maxSegment: 8, eomBytes: 4);
        q.Tx.Add(Normal(X, 0, new byte[64]) with { Ttd = long.MaxValue });
        int transmissions = 0;
        while (q.Observer.Log.Last(l => l.StartsWith("QueueState", StringComparison.Ordinal)) == "QueueState:True")
        {
            Transmit t = q.Tx.Build();
            t.Bytes.Length.Should().BeLessThanOrEqualTo(60);
            q.Context.Modem.PlanBurst(2, Interleaver.Long, t.Bytes.Length).DurationMs.Should().BeLessThanOrEqualTo(720 + 4800);
            q.Context.Now += 720 + 4800;
            transmissions++;
        }

        transmissions.Should().BeGreaterThan(1);
    }

    [Fact]
    public void A_dpdu_too_long_for_the_limit_takes_the_blocks_it_needs_with_the_eom()
    {
        // Interp C-1042, C-1290: a 40-byte D_PDU (16-byte segment) does not fit the 36 bytes one block
        // carries after the EOM, so it goes alone in ceil((40 + 4) / 40) = 2 blocks.
        var q = new Rig(maxTransmissionMs: 720 + 4800, blockBytes: 40, eomBytes: 4);
        q.Tx.Add(Normal(X, 0, new byte[16]));
        Transmit t = q.Tx.Build();
        t.Bytes.Length.Should().Be(40);
        q.Context.Modem.PlanBurst(2, Interleaver.Long, t.Bytes.Length).Blocks.Should().Be(2);
    }

    [Fact]
    public void C_pdu_ids_wrap_modulo_4096()
    {
        // C-0326, C-0329. Each build is a minute after the last, so every earlier window has ended (C-0327).
        var q = new Rig();
        for (int i = 0; i < 4096; i++)
        {
            q.Context.Now = i * 60_000L;
            q.Tx.Add(Normal(X, 0, [1]) with { Ttd = long.MaxValue });
            q.Tx.Build();
        }

        q.Context.Now = 4096 * 60_000L;
        q.Tx.Add(Normal(X, 0, [1]) with { Ttd = long.MaxValue });
        Id(q.Decode(q.Tx.Build()).Single()).Should().Be(0);
    }

    [Fact]
    public void An_id_is_not_reused_inside_its_window_and_dts_ttd_wakes_the_queue_when_it_is_freed()
    {
        // C-0327, C-1053: 4097 one-byte C_PDUs fit one huge block, but the 4097th would need ID 0,
        // whose window runs to the end of this transmission. It waits; dts.ttd runs to the window end.
        var q = new Rig(blockBytes: 200_000, blockMs: 10_000);
        for (int i = 0; i < 4097; i++)
        {
            q.Tx.Add(Normal(X, 0, [1]) with { Ttd = 10_000_000 });
        }

        List<Dpdu> first = q.Decode(q.Tx.Build());
        first.Should().HaveCount(4096);
        Id(first[^1]).Should().Be(4095);
        q.Tx.Count.Should().Be(1);
        q.Observer.Log.Last().Should().Be("QueueState:False");

        // One 10 s block plus preamble 720: EOT and window ceil(10000 / 500) = 20 for every D_PDU, so ID 0 is held to 720 + 10000.
        TimerName ttd = TimerName.Of(TimerKind.DtsTtd);
        q.Context.Timers.Deadline(ttd).Should().Be(10_720);
        q.Context.Now = 10_720;
        q.Context.Timers.Expire(ttd, 10_720);
        q.Tx.OnTtdTimer();
        q.Observer.Log.Last().Should().Be("QueueState:True");
        q.Context.Timers.Deadline(ttd).Should().Be(10_000_000);   // back to the earliest TTD (C-1050)
        Id(q.Decode(q.Tx.Build()).Single()).Should().Be(0);
    }

    [Fact]
    public void A_request_whose_ttd_has_passed_is_rejected_at_once()
    {
        // C-0857, C-0859, C-1050; zero-duration rule (core-architecture.md 3.2).
        var q = new Rig();
        q.Context.Now = 5000;
        q.Tx.Add(Normal(X, 0, [1]) with { Ttd = 5000, Handle = 9 });
        q.Tx.Count.Should().Be(0);
        q.Context.TryDequeue(out InternalPrimitive p).Should().BeTrue();
        p.Should().Be(new DUnidataRequestRejected(9, DReason.TtlExpired));
        q.Observer.Log.Should().BeEmpty();
    }

    [Fact]
    public void A_normal_non_arq_request_is_confirmed_when_the_transmission_with_its_last_copy_ends()
    {
        // Interp C-1255: confirmed at transmit_done, in placement order with expedited ones (C-1100);
        // one with a copy still owed is not.
        var q = new Rig(maxTransmissionMs: 720 + 4800);   // one 96-byte block
        q.Tx.Add(Normal(X, 9, [1]) with { Handle = 1 });
        q.Tx.AddExpedited(Expedited(Y, [2]) with { Handle = 2 });
        q.Tx.Add(Normal(X, 0, new byte[60]) with { Handle = 3, MinRetransmissions = 1 });   // two 74-byte copies
        q.Tx.Build();
        q.Tx.TakeSent().Should().Equal((2, true), (1, false));
        q.Tx.Build();
        q.Tx.TakeSent().Should().BeEmpty("one copy of request 3 is still owed");
        q.Tx.Build();
        q.Tx.TakeSent().Should().Equal((3, false));
    }

    [Fact]
    public void A_normal_request_whose_owed_copies_are_dropped_is_confirmed_on_entry_to_linking()
    {
        // C-1052 drops the copies a transmission could not finish within the window already
        // announced; interp C-1255: the request has been sent once, so it is confirmed, after any
        // TTL rejections, and not again at transmit_done.
        var q = new Rig(maxTransmissionMs: 720 + 4800);
        q.Tx.Add(Normal(X, 0, new byte[60]) with { Handle = 3, MinRetransmissions = 1 });
        q.Tx.Add(Normal(Y, 0, [1]) with { Handle = 4, Ttd = 400_000 });
        q.Tx.Build();
        q.Tx.TakeSent().Should().BeEmpty();
        q.Context.Now = 500_000;
        q.Tx.ExpireDue();
        q.Context.TryDequeue(out InternalPrimitive a).Should().BeTrue();
        a.Should().Be(new DUnidataRequestRejected(4, DReason.TtlExpired));
        q.Context.TryDequeue(out InternalPrimitive b).Should().BeTrue();
        b.Should().Be(new DUnidataRequestConfirm(3));
        q.Context.TryDequeue(out _).Should().BeFalse();
        q.Tx.Count.Should().Be(0);
        q.Tx.TakeSent().Should().BeEmpty();
    }

    [Fact]
    public void Expired_requests_are_rejected_in_ttd_order_on_entry_to_linking()
    {
        // C-0857, C-0864, C-1050: ExpireDue drops everything at or before now, earliest TTD first.
        var q = new Rig();
        q.Tx.Add(Normal(X, 0, [1]) with { Ttd = 3000, Handle = 1 });
        q.Tx.Add(Normal(X, 0, [2]) with { Ttd = 2000, Handle = 2 });
        q.Tx.Add(Normal(X, 0, [3]) with { Ttd = 9000, Handle = 3 });
        q.Context.Now = 3000;
        q.Tx.ExpireDue();
        q.Context.TryDequeue(out InternalPrimitive a).Should().BeTrue();
        a.Should().Be(new DUnidataRequestRejected(2, DReason.TtlExpired));
        q.Context.TryDequeue(out InternalPrimitive b).Should().BeTrue();
        b.Should().Be(new DUnidataRequestRejected(1, DReason.TtlExpired));
        q.Tx.Count.Should().Be(1);
        q.Context.Timers.Deadline(TimerName.Of(TimerKind.DtsTtd)).Should().Be(9000);
    }

    [Fact]
    public void A_segmented_expedited_cpdu_keeps_one_id_and_sends_once()
    {
        // C-0359 to C-0361 (type 8 like type 7), C-0527 to C-0529; expedited carries no repeat count.
        var q = new Rig(maxSegment: 4);
        q.Tx.AddExpedited(Expedited(Y, [1, 2, 3, 4, 5, 6]));
        List<Dpdu> pdus = q.Decode(q.Tx.Build());
        pdus.Select(p => ((ExpeditedNonArqData)p).Fields.SegmentOffset).Should().Equal(0, 4);
        pdus.Select(Id).Should().Equal(0, 0);
        q.Tx.Count.Should().Be(0);
    }

    [Fact]
    public void Segments_that_do_not_fit_wait_and_keep_their_id_and_cut()
    {
        // C-0549 (cut once, at first placement), C-0542, C-0880: a 96-byte block holds three
        // 30-byte D_PDUs; the fourth segment goes next time with the same ID, cut as before even
        // though dts.max_segment_size changed (core-architecture.md 7.3).
        var q = new Rig(maxTransmissionMs: 720 + 4800, maxSegment: 5);
        q.Tx.Add(Normal(X, 0, new byte[20]));
        List<Dpdu> a = q.Decode(q.Tx.Build());
        a.Select(p => ((NonArqData)p).Fields.SegmentOffset).Should().Equal(0, 5, 10);
        q.Context.Config = q.Context.Config with { Dts = q.Context.Config.Dts with { MaxSegmentSize = 1023 } };
        q.Context.Now = 6000;
        List<Dpdu> b = q.Decode(q.Tx.Build());
        b.Should().ContainSingle();
        ((NonArqData)b[0]).Fields.SegmentOffset.Should().Be(15);
        ((NonArqData)b[0]).Segment.Length.Should().Be(5);
        Id(b[0]).Should().Be(0);
    }

    [Fact]
    public void Arq_requests_while_unconnected_are_rejected_to_the_cas()
    {
        // C-0573, C-0574, C-0731.
        var context = new CoreContext(TestConfig.Basic(), new NullModem());
        var log = new ObserverLog();
        var dts = new DtsSublayer(context, log);
        dts.On(Normal(X, 0, [1]) with { Arq = true, Handle = 7 });
        dts.On(new DExpeditedUnidataRequest(X, false, true, new byte[] { 1 }, 8));
        context.TryDequeue(out InternalPrimitive a).Should().BeTrue();
        a.Should().Be(new DUnidataRequestRejected(7, DReason.NoConnection));
        context.TryDequeue(out InternalPrimitive b).Should().BeTrue();
        b.Should().Be(new DExpeditedUnidataRequestRejected(8, DReason.NoConnection));
        log.Log.Should().BeEmpty();
    }

    [Fact]
    public void Unbind_and_disconnect_discard_held_arq_requests_of_that_sap_only()
    {
        // A-0260 (coordinator ruling).
        var context = new CoreContext(TestConfig.Basic(), new NullModem());
        var sis = new SisSublayer(context);
        var dispatcher = new Dispatcher(context, sis, new CasSublayer(context), new DtsSublayer(context, new ObserverLog()), new Probe("mac", []));
        foreach ((int conn, byte sap) in new[] { (1, (byte)5), (2, (byte)6) })
        {
            dispatcher.Handle(new SisConnect(0, conn));
            dispatcher.Handle(new SisBytes(0, conn, SisCodec.Encode(new SisBindRequest(sap, new ServiceType(1)))));
            dispatcher.Handle(new SisBytes(0, conn, SisCodec.Encode(new SisUnidataRequest(0, 1, new SisAddress(X), ServiceType.Default, 0, new byte[] { sap }))));
        }

        sis.AwaitingSoftLink.Should().HaveCount(2);
        dispatcher.Handle(new SisBytes(0, 1, SisCodec.Encode(new SisUnbindRequest())));
        sis.AwaitingSoftLink.Should().ContainSingle();
        dispatcher.Handle(new SisDisconnect(0, 2));
        sis.AwaitingSoftLink.Should().BeEmpty();
    }

    [Fact]
    public void Dts_and_drc_settings_are_read_and_checked()
    {
        JsonObject defaults = Corpus.T2Defaults.DeepClone().AsObject();
        defaults["local_address"] = "0.0.1.2";
        NodeConfig c = ConfigReader.Read(defaults);
        c.Dts.Should().Be(new DtsConfig(1023, 127500));
        c.Drc.DefaultSpeed.Should().Be(new Speed(2, Interleaver.Long));

        foreach ((string key, string sub, int value) in new[] { ("dts", "max_segment_size", 1024), ("dts", "max_segment_size", 0), ("dts", "max_transmission_ms", 127501) })
        {
            JsonObject bad = defaults.DeepClone().AsObject();
            bad[key]![sub] = value;
            Action act = () => ConfigReader.Read(bad);
            act.Should().Throw<ConfigException>().Which.Setting.Should().Be($"{key}.{sub}");
        }

        JsonObject il = defaults.DeepClone().AsObject();
        il["drc"]!["default_speed"]!["interleaver"] = "longer";
        Action badIl = () => ConfigReader.Read(il);
        badIl.Should().Throw<ConfigException>();
    }

    [Fact]
    public void Padding_fills_only_when_every_addressee_is_known_ed4()
    {
        // C-0367, C-0055, C-0560, C-1154: a 35-byte type 7 in a 96-byte block leaves 61 bytes.
        var q = new Rig();
        q.Tx.Add(Normal(X, 0, new byte[16]));
        q.Decode(q.Tx.Build()).Select(p => p.Type).Should().Equal(7);   // X of unknown edition: zero fill, EOW EMPTY (C-0419)

        q.Context.Peers.SetEdition(X, KnownEdition.Ed4);
        q.Tx.Add(Normal(X, 0, new byte[16]));
        List<Dpdu> padded = q.Decode(q.Tx.Build());
        padded.Select(p => p.Type).Should().Equal(7, 14, 14, 14, 14, 14);   // 35 + 5 x 11 = 90 of 96
        padded.Skip(1).Should().OnlyContain(p => p.Header.Dst == X && p.Header.Eow == 0xF01);   // CAPABILITY, the rotation being empty (C-0426, C-0373, P7-3)

        q.Context.Peers.SetEdition(Y, KnownEdition.Ed3);
        q.Tx.Add(Normal(X, 0, new byte[16]));
        q.Tx.Add(Normal(Y, 0, new byte[1]));
        q.Decode(q.Tx.Build()).Select(p => p.Type).Should().Equal(7, 7);   // one addressee recorded Ed3
    }

    [Fact]
    public void No_padding_when_an_addressee_is_a_group()
    {
        // C-0560, C-1154: the group's receivers are of unknown edition.
        var q = new Rig();
        q.Context.Peers.SetEdition(X, KnownEdition.Ed4);
        q.Tx.Add(Normal(X, 0, new byte[16]));
        q.Tx.Add(new DUnidataRequest(Y, true, false, 0, 1_000_000, false, 0, new byte[4], 2));
        q.Decode(q.Tx.Build()).Select(p => p.Type).Should().Equal(7, 7);
    }

    [Fact]
    public void A_segment_that_would_pass_the_capacity_is_cut_to_end_at_it_towards_an_Ed4_peer()
    {
        // C-1155 with C-1042: the budget is one 96-byte block, and the first 119-byte D_PDU does not fit it.
        // Towards an Ed4 peer it does not stretch the transmission: it is cut to end at the capacity, 96
        // (19 + 77), so the transmission is one block, the budget, and nothing more is placed.
        var q = new Rig(maxTransmissionMs: 720 + 4800, maxSegment: 100);
        q.Context.Peers.SetEdition(X, KnownEdition.Ed4);
        q.Tx.OpenLink(X);
        q.Tx.AddArq(Arq(X, 200));
        Transmit first = q.Tx.Build();
        first.Bytes.Length.Should().Be(96);
        q.Decode(first).Cast<DataOnly>().Select(d => (d.TxFsn, d.Segment.Length, d.Flags.CPduStart, d.Flags.CPduEnd))
            .Should().Equal((0, 77, true, false));

        // Two blocks within the budget: the 119-byte D_PDU would pass the 192-byte capacity, so it is cut
        // to end at it (96 + 19 + 77), and the last 46 bytes wait.
        var q2 = new Rig(maxTransmissionMs: 720 + 2 * 4800, maxSegment: 100);
        q2.Context.Peers.SetEdition(X, KnownEdition.Ed4);
        q2.Tx.OpenLink(X);
        q2.Tx.AddArq(Arq(X, 200));
        q2.Decode(q2.Tx.Build()).Cast<DataOnly>().Select(d => (d.TxFsn, d.Segment.Length, d.Flags.CPduStart, d.Flags.CPduEnd))
            .Should().Equal((0, 77, true, false), (1, 77, false, false));
        List<Dpdu> rest = q2.Decode(q2.Tx.Build());   // a 65-byte DATA-ONLY, then two PADDINGs in the 31 bytes left (C-1154)
        rest.Select(p => p.Type).Should().Equal(9, 14, 14);
        rest.OfType<DataOnly>().Select(d => (d.TxFsn, d.Segment.Length, d.Flags.CPduEnd)).Should().Equal((2, 46, true));
    }

    [Fact]
    public void A_first_segment_to_an_ed4_peer_leaves_room_for_the_eom_in_the_limit()
    {
        // Interp C-1042, C-1155, C-1290: one 96-byte block within the budget carries 92 bytes after the 4-byte
        // EOM. The first segment at the maximum size would be a 119-byte D_PDU; towards an Ed4 peer it is cut
        // to end where the budget's room ends, 92 (19 + 73), and the transmission is the one block allowed.
        var q = new Rig(maxTransmissionMs: 720 + 4800, maxSegment: 100, eomBytes: 4);
        q.Context.Peers.SetEdition(X, KnownEdition.Ed4);
        q.Tx.OpenLink(X);
        q.Tx.AddArq(Arq(X, 200));
        Transmit first = q.Tx.Build();
        first.Bytes.Length.Should().Be(92);
        q.Context.Modem.PlanBurst(2, Interleaver.Long, first.Bytes.Length).Blocks.Should().Be(1);
        q.Decode(first).Cast<DataOnly>().Select(d => (d.TxFsn, d.Segment.Length, d.Flags.CPduStart, d.Flags.CPduEnd))
            .Should().Equal((0, 73, true, false));
    }

    [Theory]
    [InlineData(173, 400)]    // would end at the boundary at 192
    [InlineData(171, 400)]    // would end at 190, inside the marker's room of block 2
    [InlineData(169, 400)]    // would end at 188, exactly the room of two blocks
    [InlineData(1023, 2000)]  // would need 12 blocks
    [InlineData(1023, 300)]   // the C_PDU's only segment, so its last, would need 4 blocks
    public void A_first_segment_to_an_ed4_peer_is_cut_to_the_limit_however_long(int maxSegment, int cpdu)
    {
        // Interp C-1042, C-1155, C-1290 (P7-9b): 96-byte blocks, a 4-byte EOM and a one-block budget (92
        // bytes). However long the first segment would be, towards an Ed4 peer it may be shorter than the
        // maximum (C-0553), so the transmission is never stretched for it: it is cut to the budget's room.
        var q = new Rig(maxTransmissionMs: 720 + 4800, maxSegment: maxSegment, eomBytes: 4);
        q.Context.Peers.SetEdition(X, KnownEdition.Ed4);
        q.Tx.OpenLink(X);
        q.Tx.AddArq(Arq(X, cpdu));
        Transmit first = q.Tx.Build();
        first.Bytes.Length.Should().Be(92);
        q.Context.Modem.PlanBurst(2, Interleaver.Long, first.Bytes.Length).Blocks.Should().Be(1);
        q.Decode(first).Cast<DataOnly>().Select(d => (d.TxFsn, d.Segment.Length, d.Flags.CPduEnd)).Should().Equal((0, 73, false));
    }

    [Fact]
    public void A_first_segment_to_an_ed4_peer_under_a_limit_shorter_than_a_block_takes_one_block()
    {
        // Interp C-1042, C-0879: with a budget too short for any block the transmission is one block, the
        // shortest there is, and the first segment to an Ed4 peer is cut to fill it.
        var q = new Rig(maxTransmissionMs: 2000, maxSegment: 1023, eomBytes: 4);
        q.Context.Peers.SetEdition(X, KnownEdition.Ed4);
        q.Tx.OpenLink(X);
        q.Tx.AddArq(Arq(X, 500));
        Transmit first = q.Tx.Build();
        first.Bytes.Length.Should().Be(92);
        q.Context.Modem.PlanBurst(2, Interleaver.Long, first.Bytes.Length).Blocks.Should().Be(1);
    }

    [Fact]
    public void A_first_segment_to_an_ed3_peer_stretches_the_transmission()
    {
        // Interp C-1042, C-0551, C-0554: towards an Ed3 peer a segment may not be shortened, so a 118-byte
        // type 0 D_PDU (18 + 100) that does not fit the one-block budget goes alone, in the 2 blocks it needs.
        var q = new Rig(maxTransmissionMs: 720 + 4800, maxSegment: 100);
        q.Context.Peers.SetEdition(X, KnownEdition.Ed3);
        q.Tx.OpenLink(X, shortFamily: true);
        q.Tx.AddArq(Arq(X, 200));
        Transmit first = q.Tx.Build();
        q.Context.Modem.PlanBurst(2, Interleaver.Long, first.Bytes.Length).Blocks.Should().Be(2);
        q.Decode(first).OfType<Ed3DataOnly>().Select(d => (d.TxFsn, d.Segment.Length)).Should().Equal((0, 100));
    }

    [Fact]
    public void An_ed4_peer_on_a_short_link_gets_padding_and_aligned_segments()
    {
        // Interp C-1191: on a Short link (8-bit family, C-0168) to a peer recorded Ed4, segments are cut to
        // the block boundaries (C-1155) and spare space takes PADDING (C-1154), as on a 16-bit link. A type 0
        // is 18 bytes plus its segment, so two blocks hold 78 + 78; the last 44 bytes then go as a 62-byte
        // type 0 and the 34 bytes left take three 11-byte PADDINGs.
        var q = new Rig(maxTransmissionMs: 720 + 2 * 4800, maxSegment: 100);
        q.Context.Peers.SetEdition(X, KnownEdition.Ed4);
        q.Tx.OpenLink(X, shortFamily: true);
        q.Tx.AddArq(Arq(X, 200));
        q.Decode(q.Tx.Build()).Cast<Ed3DataOnly>().Select(d => (d.TxFsn, d.Segment.Length, d.Flags.CPduStart, d.Flags.CPduEnd))
            .Should().Equal((0, 78, true, false), (1, 78, false, false));
        List<Dpdu> rest = q.Decode(q.Tx.Build());
        rest.Select(p => p.Type).Should().Equal(0, 14, 14, 14);
        rest.OfType<Ed3DataOnly>().Select(d => (d.TxFsn, d.Segment.Length, d.Flags.CPduEnd)).Should().Equal((2, 44, true));
    }

    [Fact]
    public void An_8_bit_acknowledgement_always_merges_into_an_ed3_data_ack()
    {
        // C-0236, C-0238, interp C-1190: a 16-byte bitmap (FSN 127 held) still merges into a type 2.
        var q = new Rig();
        ArqLink link = q.Tx.OpenLink(X, shortFamily: true);
        link.Receive(127, true, true, false, false, [1]);
        q.Tx.AckOwed();
        q.Tx.AddArq(Arq(X, 4));
        List<Dpdu> pdus = q.Decode(q.Tx.Build());
        Ed3DataAck d = pdus[0].Should().BeOfType<Ed3DataAck>().Which;
        (d.TxFsn, d.RxLwe, d.SelectiveAcks.Length).Should().Be((0, 0, 16));
        pdus.Skip(1).Should().NotBeEmpty().And.AllBeOfType<Ed3AckOnly>();   // the fill repeats it as type 1 copies (C-1154)
        pdus.Skip(1).Cast<Ed3AckOnly>().Should().OnlyContain(a => a.RxLwe == 0 && a.SelectiveAcks.Length == 16);
    }

    [Fact]
    public void Towards_a_peer_not_known_ed4_segments_keep_the_maximum_size()
    {
        // C-0554, C-0870, C-1155: the same C_PDU to a peer of unknown edition.
        var q = new Rig(maxTransmissionMs: 720 + 4800, maxSegment: 100);
        q.Tx.OpenLink(X);
        q.Tx.AddArq(Arq(X, 200));
        q.Decode(q.Tx.Build()).Cast<DataOnly>().Select(d => d.Segment.Length).Should().Equal(100);
    }

    [Fact]
    public void A_run_length_acknowledgement_is_processed_like_a_bitmap()
    {
        // C-0226, C-1153: FSNs 0 to 2 sent; RX LWE 0 and RLE 81 (two 1s: FSNs 1 and 2) acknowledge 1 and 2,
        // and FSN 0, at RX LWE, has failed (C-0908).
        var q = new Rig();
        q.Context.Peers.SetEdition(X, KnownEdition.Ed4);
        ArqLink link = q.Tx.OpenLink(X);
        for (int i = 0; i < 3; i++)
        {
            q.Tx.AddArq(Arq(X, 4));
        }

        q.Decode(q.Tx.Build()).OfType<DataOnly>().Select(d => d.TxFsn).Should().Equal(0, 1, 2);
        q.Tx.OnAck(X, 0, 1, 0, [0x81]);
        link.Outstanding.Should().ContainSingle().Which.Should().Match<ArqFrame>(f => f.Fsn == 0 && f.State == ArqFrameState.Failed);
    }

    [Fact]
    public void An_acknowledgement_of_several_ack_onlys_is_not_merged_into_a_data_ack()
    {
        // C-1151, C-1152: FSN 1 and every eighth FSN to 2048 need eight ACK-ONLYs of one 32-byte bitmap
        // each, so data to the peer goes as DATA-ONLY after them; the fill then repeats them in turn.
        var q = new Rig(blockBytes: 400);
        q.Context.Peers.SetEdition(X, KnownEdition.Ed4);
        ArqLink link = q.Tx.OpenLink(X);
        link.Receive(1, true, true, false, false, [1]);
        for (int f = 8; f <= 2048; f += 8)
        {
            link.Receive(f, false, false, false, false, [1]);
        }

        q.Tx.AckOwed();
        q.Tx.AddArq(Arq(X, 4));
        List<Dpdu> pdus = q.Decode(q.Tx.Build());
        pdus.Select(p => p.Type).Take(9).Should().Equal(10, 10, 10, 10, 10, 10, 10, 10, 9);
        pdus.OfType<AckOnly>().Take(8).Select(a => a.Offset).Should().Equal(Enumerable.Range(0, 8));
        pdus.OfType<AckOnly>().Take(8).Should().OnlyContain(a => a.AckData.Length == 32);
        link.AckOwed.Should().BeFalse();
    }

    [Fact]
    public void In_order_cpdus_to_one_peer_keep_submission_order_across_priorities()
    {
        // Interp C-1253 (C-0017, C-0851): B (priority 12) was submitted after A (priority 1), both in
        // order to X, so A takes it ahead with it: A FSN 0, B FSN 1. C (priority 5, any order) comes
        // after both, and D (priority 3, in order) after C, since nothing later lifts it.
        var q = new Rig();
        q.Tx.OpenLink(X);
        q.Tx.AddArq(Arq(X, 1) with { Priority = 1, DeliverInOrder = true, CPdu = new byte[] { 0xA } });
        q.Tx.AddArq(Arq(X, 1) with { Priority = 12, DeliverInOrder = true, CPdu = new byte[] { 0xB } });
        q.Tx.AddArq(Arq(X, 1) with { Priority = 5, CPdu = new byte[] { 0xC } });
        q.Tx.AddArq(Arq(X, 1) with { Priority = 3, DeliverInOrder = true, CPdu = new byte[] { 0xD } });
        q.Decode(q.Tx.Build()).Cast<DataOnly>().Select(d => (d.TxFsn, d.Segment.Span[0]))
            .Should().Equal((0, (byte)0xA), (1, (byte)0xB), (2, (byte)0xC), (3, (byte)0xD));
    }

    private static DUnidataRequest Arq(uint dst, int length) =>
        new(dst, false, true, 0, 1_000_000, false, 0, new byte[length], 1);

    private static int Id(Dpdu p) => p switch
    {
        NonArqData d => d.Fields.CPduId,
        ExpeditedNonArqData e => e.Fields.CPduId,
        _ => -1,
    };

    private static DUnidataRequest Normal(uint dst, int priority, byte[] cpdu) =>
        new(dst, false, false, priority, 1_000_000, false, 0, cpdu, 1);

    private static DExpeditedUnidataRequest Expedited(uint dst, byte[] cpdu) => new(dst, false, false, cpdu, 1);

    private sealed class Rig
    {
        public Rig(int maxTransmissionMs = 127500, int blockBytes = 96, int blockMs = 4800, int maxSegment = 1023, int eomBytes = 0)
        {
            NodeConfig config = TestConfig.Basic(CsmaMode.Jitter) with
            {
                Modem = new ModemFixture(720, 0, [new ModemWaveform(2, Interleaver.Long, blockBytes, blockMs)], eomBytes),
                Dts = new DtsConfig(maxSegment, maxTransmissionMs),
            };
            Context = new CoreContext(config, new FixtureModem(config.Modem));
            Tx = new TransmitQueue(Context, Observer);
        }

        public CoreContext Context { get; }

        public ObserverLog Observer { get; } = new();

        public TransmitQueue Tx { get; }

        public List<Dpdu> Decode(Transmit t)
        {
            var list = new List<Dpdu>();
            ReadOnlySpan<byte> rest = t.Bytes.Span;
            while (rest.Length > 0)
            {
                DecodeResult<Dpdu> r = DpduCodec.DecodeFirst(rest, PeerEdition.Ed4);
                list.Add(r.Value!);
                rest = rest[r.Consumed..];
            }

            return list;
        }
    }

    private sealed class ObserverLog : IMacObserver
    {
        public List<string> Log { get; } = [];

        public void EotHeard(long predictedEndTime) => Log.Add("EotHeard");

        public void DpduHeard(uint src, uint dst, bool dstIsGroup) => Log.Add("DpduHeard");

        public void QueueState(bool nonEmpty) => Log.Add("QueueState:" + nonEmpty);
    }
}
