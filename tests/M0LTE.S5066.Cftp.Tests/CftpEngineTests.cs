using M0LTE.S5066.Cftp;

namespace M0LTE.S5066.Cftp.Tests;

/// <summary>
/// The CFTP sender and receiver engine on a virtual clock, wired back to back through a fake
/// node that confirms or rejects each segment and can lose U_PDUs.
/// </summary>
public class CftpEngineTests
{
    private const uint A = 0x0000101;
    private const uint B = 0x0000102;

    /// <summary>Two engines and a lossless node between them, one step at a time.</summary>
    private sealed class Link
    {
        public Link(CftpOptions? options = null)
        {
            Engines[A] = new CftpEngine(options);
            Engines[B] = new CftpEngine(options);
        }

        public Dictionary<uint, CftpEngine> Engines { get; } = [];

        public long Now { get; set; }

        /// <summary>Every U_PDU sent, as (from, to, bytes).</summary>
        public List<(uint From, uint To, byte[] UPdu)> Sent { get; } = [];

        public List<(uint At, CftpAction Action)> Events { get; } = [];

        /// <summary>Decides each U_PDU's fate: deliver (true) or lose (false); segments lost are still confirmed.</summary>
        public Func<uint, byte[], bool> Deliver { get; set; } = (_, _) => true;

        /// <summary>Reject this segment send (the node's reason), or null to confirm.</summary>
        public Func<uint, byte[], byte?> Reject { get; set; } = (_, _) => null;

        public void Pump()
        {
            bool any = true;
            while (any)
            {
                any = false;
                foreach ((uint at, CftpEngine e) in Engines)
                {
                    foreach (CftpAction a in e.Drain())
                    {
                        any = true;
                        Events.Add((at, a));
                        if (a is CftpSendUPdu s)
                        {
                            Sent.Add((at, s.Destination, s.UPdu));
                            byte? rej = s.IsAck ? null : Reject(at, s.UPdu);
                            if (rej is null && Deliver(at, s.UPdu))
                            {
                                Engines[s.Destination].Receive(at, s.UPdu, Now);
                            }

                            if (!s.IsAck)
                            {
                                e.SendDone(s.SendId, rej is null, rej ?? 0, Now);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Moves the clock to each deadline in turn up to <paramref name="until"/>.</summary>
        public void RunUntil(long until)
        {
            Pump();
            while (true)
            {
                long? next = Engines.Values.Select(e => e.NextDeadline).Where(d => d is not null).Min();
                if (next is not { } n || n > until)
                {
                    Now = until;
                    return;
                }

                Now = Math.Max(Now, n);
                foreach (CftpEngine e in Engines.Values)
                {
                    e.Tick(Now);
                }

                Pump();
            }
        }

        public List<T> Of<T>(uint at) => [.. Events.Where(x => x.At == at).Select(x => x.Action).OfType<T>()];
    }

    private static byte[] Data(int n) => [.. Enumerable.Range(0, n).Select(i => (byte)(i * 7))];

    [Fact]
    public void A_small_file_goes_as_one_segment_0_with_U_PDU_ID_0_and_is_acknowledged()
    {
        var l = new Link();
        long id = l.Engines[A].Submit(B, "f"u8.ToArray(), Data(10), 2048, 0);
        l.Pump();

        // V-0018, V-0019, V-0080, V-0081: one segment, number 0, U_PDU ID 0, connection 0.
        l.Sent[0].UPdu[..4].Should().Equal(0x00, 0x00, 0x00, 0x00);
        l.Sent[0].UPdu[4..].Should().Equal(new Bftp1Pdu("f"u8.ToArray(), Data(10)).Encode());

        // V-0045, interp V-0075: the acknowledgement is 10 0B with the same IDs, segment 0.
        l.Sent[1].Should().Be((B, A, l.Sent[1].UPdu));
        l.Sent[1].UPdu.Should().Equal(0x00, 0x00, 0x00, 0x00, 0x10, 0x0B);

        CftpFileReceived got = l.Of<CftpFileReceived>(B).Single();
        got.FileName.Should().Equal("f"u8.ToArray());
        got.FileData.Should().Equal(Data(10));
        l.Of<CftpTransferDone>(A).Single().Should().Be(new CftpTransferDone(id, true, 1, null));
    }

    [Fact]
    public void A_large_file_is_cut_into_MTU_less_4_segments_numbered_from_0_and_reassembled()
    {
        var l = new Link();
        byte[] data = Data(1000);
        l.Engines[A].Submit(B, "big"u8.ToArray(), data, 100, 0);
        l.Pump();

        var segs = l.Sent.Where(s => s.From == A).ToList();
        int total = 7 + 3 + 1000;
        segs.Should().HaveCount((total + 95) / 96);
        for (int i = 0; i < segs.Count; i++)
        {
            // V-0017, V-0039, interp V-0071, V-0072: same U_PDU ID, consecutive segment numbers, full but the last.
            segs[i].UPdu[1].Should().Be(0);
            (segs[i].UPdu[2] << 8 | segs[i].UPdu[3]).Should().Be(i);
            segs[i].UPdu.Length.Should().BeLessThanOrEqualTo(100);
            if (i < segs.Count - 1)
            {
                segs[i].UPdu.Length.Should().Be(100);
            }
        }

        l.Of<CftpFileReceived>(B).Single().FileData.Should().Equal(data);
        l.Of<CftpTransferDone>(A).Single().Acknowledged.Should().BeTrue();
    }

    [Fact]
    public void Segments_arriving_out_of_order_still_make_the_file()
    {
        var e = new CftpEngine();
        byte[] pdu = new Bftp1Pdu("x"u8.ToArray(), Data(50)).Encode();
        var segs = Enumerable.Range(0, 3).Select(i => new Rcop1Pdu(2, 9, (ushort)i, pdu.AsSpan(i * 20, Math.Min(20, pdu.Length - (i * 20))).ToArray()).Encode()).ToList();
        e.Receive(A, segs[2], 0);
        e.Receive(A, segs[1], 1);
        e.Drain().Should().BeEmpty();
        e.Receive(A, segs[0], 2);
        IReadOnlyList<CftpAction> acts = e.Drain();

        // V-0040, interp V-0073; the acknowledgement echoes connection 2 and U_PDU ID 9 (V-0045, interp V-0081).
        acts.OfType<CftpSendUPdu>().Single().UPdu.Should().Equal(0x20, 9, 0, 0, 0x10, 0x0B);
        acts.OfType<CftpFileReceived>().Single().Should().Match<CftpFileReceived>(f => f.ConnectionId == 2 && f.UPduId == 9 && f.FileData.SequenceEqual(Data(50)));
    }

    [Fact]
    public void Octets_after_the_declared_end_are_ignored()
    {
        var e = new CftpEngine();
        byte[] pdu = [.. new Bftp1Pdu("x"u8.ToArray(), Data(5)).Encode(), 0xEE, 0xEE];
        e.Receive(A, new Rcop1Pdu(0, 0, 0, pdu).Encode(), 0);
        IReadOnlyList<CftpAction> acts = e.Drain();
        acts.OfType<CftpFileReceived>().Single().FileData.Should().Equal(Data(5));
        acts.OfType<CftpNote>().Should().ContainSingle();
    }

    [Fact]
    public void Files_are_stop_and_wait_and_U_PDU_IDs_are_consecutive_per_destination()
    {
        var l = new Link();
        l.Deliver = (from, u) => from == A ? true : l.Now >= 1000;
        l.Engines[A].Submit(B, "1"u8.ToArray(), Data(5), 2048, 0);
        l.Engines[A].Submit(B, "2"u8.ToArray(), Data(5), 2048, 0);
        l.Pump();

        // The first acknowledgement was lost, so the second file has not gone (V-0023).
        l.Sent.Count(s => s.From == A).Should().Be(1);

        l.Now = 1000;
        l.RunUntil(400_000);

        // After the ack wait the first went again under ID 0 (interp V-0076), then the second as ID 1 (V-0016, interp V-0080).
        l.Sent.Where(s => s.From == A).Select(s => s.UPdu[1]).Should().Equal(0, 0, 1);
        l.Of<CftpFileReceived>(B).Select(f => f.UPduId).Should().Equal(0, 1);
        l.Of<CftpTransferDone>(A).Should().HaveCount(2).And.OnlyContain(d => d.Acknowledged);
    }

    [Fact]
    public void A_file_sent_again_after_a_lost_acknowledgement_is_acknowledged_but_not_delivered_twice()
    {
        var l = new Link();
        int acks = 0;
        l.Deliver = (from, u) => from == A || ++acks > 1;
        l.Engines[A].Submit(B, "f"u8.ToArray(), Data(30), 2048, 0);
        l.RunUntil(1_000_000);

        // Interp V-0077: two acknowledgements, one delivery.
        l.Sent.Count(s => s.From == B).Should().Be(2);
        l.Of<CftpFileReceived>(B).Should().ContainSingle();
        l.Of<CftpTransferDone>(A).Single().Should().Match<CftpTransferDone>(d => d.Acknowledged && d.Attempts == 2);
    }

    [Fact]
    public void Ack_wait_counts_from_the_last_confirmation_and_the_file_fails_after_max_attempts()
    {
        var l = new Link(new CftpOptions { AckWait = TimeSpan.FromSeconds(10), MaxAttempts = 3 });
        l.Deliver = (from, _) => from == A;
        l.Engines[A].Submit(B, "f"u8.ToArray(), Data(5), 2048, 0);
        l.Pump();
        l.Engines[A].NextDeadline.Should().Be(10_000);
        l.RunUntil(1_000_000);

        // Interp V-0076: sent at 0, 10 s and 20 s, failed at 30 s.
        l.Sent.Count(s => s.From == A).Should().Be(3);
        l.Of<CftpTransferDone>(A).Single().Should().Match<CftpTransferDone>(d => !d.Acknowledged && d.Attempts == 3 && d.Failure!.Contains("no acknowledgement"));
        l.Engines[A].NextDeadline.Should().BeNull();
    }

    [Fact]
    public void A_rejected_segment_sends_the_file_again_after_the_retry_delay()
    {
        var l = new Link(new CftpOptions { RetryDelay = TimeSpan.FromSeconds(60) });
        int sends = 0;
        l.Reject = (_, _) => ++sends == 1 ? (byte)3 : null;
        l.Engines[A].Submit(B, "f"u8.ToArray(), Data(5), 2048, 0);
        l.Pump();
        l.Engines[A].NextDeadline.Should().Be(60_000);
        l.Of<CftpFileReceived>(B).Should().BeEmpty();
        l.RunUntil(100_000);
        l.Of<CftpFileReceived>(B).Should().ContainSingle();
        l.Of<CftpTransferDone>(A).Single().Attempts.Should().Be(2);
    }

    [Fact]
    public void Rejected_on_every_attempt_fails_with_the_reason()
    {
        var l = new Link(new CftpOptions { MaxAttempts = 2 });
        l.Reject = (_, _) => 5;
        l.Engines[A].Submit(B, "f"u8.ToArray(), Data(5), 2048, 0);
        l.RunUntil(10_000_000);
        l.Of<CftpTransferDone>(A).Single().Failure.Should().Contain("reason 5");
    }

    [Fact]
    public void Concurrent_files_go_together_when_the_option_allows()
    {
        var l = new Link(new CftpOptions { ConcurrentFiles = 2 });
        l.Deliver = (from, _) => from == A;
        for (int i = 0; i < 3; i++)
        {
            l.Engines[A].Submit(B, "f"u8.ToArray(), Data(5), 2048, 0);
        }

        l.Pump();

        // V-0024: two in transfer, IDs 0 and 1; the receiver keeps them apart (V-0025).
        l.Sent.Where(s => s.From == A).Select(s => s.UPdu[1]).Should().Equal(0, 1);
        l.Of<CftpFileReceived>(B).Select(f => f.UPduId).Should().Equal(0, 1);
    }

    [Fact]
    public void The_U_PDU_ID_wraps_from_255_to_0()
    {
        var l = new Link();
        for (int i = 0; i < 258; i++)
        {
            l.Engines[A].Submit(B, "f"u8.ToArray(), Data(i % 7), 2048, 0);
        }

        l.Pump();
        l.Of<CftpFileReceived>(B).Select(f => (int)f.UPduId).Should().Equal(Enumerable.Range(0, 258).Select(i => i % 256));
    }

    [Fact]
    public void Timers_due_together_run_in_deadline_order_not_destination_order()
    {
        // Determinism for the port: a destination added first but due later goes second.
        const uint C = 0x0000103;
        var e = new CftpEngine(new CftpOptions { AckWait = TimeSpan.FromSeconds(10) });
        e.Submit(B, "b"u8.ToArray(), Data(5), 2048, 0);
        e.Submit(C, "c"u8.ToArray(), Data(5), 2048, 0);
        CftpSendUPdu[] first = [.. e.Drain().OfType<CftpSendUPdu>()];
        e.SendDone(first[1].SendId, true, 0, 1_000);
        e.SendDone(first[0].SendId, true, 0, 2_000);
        e.Tick(20_000);
        e.Drain().OfType<CftpSendUPdu>().Select(s => s.Destination).Should().Equal(C, B);

        // Partly received files are dropped oldest first, whatever order they were created in.
        var r = new CftpEngine(new CftpOptions { ReassemblyTimeout = TimeSpan.FromMinutes(1) });
        byte[] pdu = new Bftp1Pdu("x"u8.ToArray(), Data(50)).Encode();
        r.Receive(B, new Rcop1Pdu(0, 0, 0, pdu[..20]).Encode(), 5_000);
        r.Receive(A, new Rcop1Pdu(0, 0, 0, pdu[..20]).Encode(), 1_000);
        r.Tick(100_000);
        r.Drain().OfType<CftpNote>().Select(n => n.Text).Should().SatisfyRespectively(
            n => n.Should().Contain($"{A:x7}"),
            n => n.Should().Contain($"{B:x7}"));
    }

    [Fact]
    public void A_partly_received_file_is_dropped_after_the_reassembly_timeout()
    {
        var e = new CftpEngine(new CftpOptions { ReassemblyTimeout = TimeSpan.FromMinutes(1) });
        byte[] pdu = new Bftp1Pdu("x"u8.ToArray(), Data(50)).Encode();
        e.Receive(A, new Rcop1Pdu(0, 0, 0, pdu[..20]).Encode(), 1000);
        e.NextDeadline.Should().Be(61_000);
        e.Tick(61_000);
        e.Drain().OfType<CftpNote>().Should().ContainSingle();
        e.NextDeadline.Should().BeNull();

        // Interp V-0078: the rest alone no longer makes a file.
        e.Receive(A, new Rcop1Pdu(0, 0, 1, pdu[20..]).Encode(), 62_000);
        e.Drain().OfType<CftpFileReceived>().Should().BeEmpty();
    }

    [Fact]
    public void A_segment_0_that_is_not_BFTPv1_is_dropped_unacknowledged()
    {
        var e = new CftpEngine();
        e.Receive(A, new Rcop1Pdu(0, 0, 0, "\x10\x03abcdefg"u8.ToArray()).Encode(), 0);
        IReadOnlyList<CftpAction> acts = e.Drain();
        acts.OfType<CftpSendUPdu>().Should().BeEmpty();
        acts.OfType<CftpNote>().Should().ContainSingle();
    }

    [Fact]
    public void An_acknowledgement_that_matches_nothing_is_ignored()
    {
        var e = new CftpEngine();
        e.Receive(A, Rcop1Pdu.Ack(0, 4).Encode(), 0);
        e.Drain().Should().ContainSingle().Which.Should().BeOfType<CftpNote>();
    }

    [Fact]
    public void A_file_needing_more_than_65536_segments_is_refused()
    {
        var e = new CftpEngine();
        Action big = () => e.Submit(B, ReadOnlyMemory<byte>.Empty, new byte[65536], 5, 0);
        big.Should().Throw<ArgumentException>().WithMessage("*V-0087*");
        e.Submit(B, ReadOnlyMemory<byte>.Empty, new byte[65536 - 7], 5, 0).Should().Be(1);
    }

    [Fact]
    public void A_segment_0_received_again_restarts_the_file()
    {
        var e = new CftpEngine();
        byte[] pdu = new Bftp1Pdu("x"u8.ToArray(), Data(30)).Encode();
        e.Receive(A, new Rcop1Pdu(0, 0, 0, pdu[..20]).Encode(), 0);
        e.Receive(A, new Rcop1Pdu(0, 0, 0, pdu[..20]).Encode(), 1);
        e.Receive(A, new Rcop1Pdu(0, 0, 1, pdu[20..]).Encode(), 2);
        e.Drain().OfType<CftpFileReceived>().Should().ContainSingle();
    }
    // ---- interp V-0093: the application accepts a file before it is acknowledged

    private static readonly CftpOptions AppAccepts = new() { AcceptByApplication = true };

    [Fact]
    public void With_application_accept_the_acknowledgement_waits_for_accept()
    {
        var l = new Link(AppAccepts);
        long id = l.Engines[A].Submit(B, "f"u8.ToArray(), Data(10), 2048, 0);
        l.Pump();

        CftpFileReceived got = l.Of<CftpFileReceived>(B).Single();
        got.AwaitsAccept.Should().BeTrue();
        got.FileData.Should().Equal(Data(10));
        l.Sent.Should().NotContain(s => s.From == B, "nothing is acknowledged before the application accepts");
        l.Of<CftpTransferDone>(A).Should().BeEmpty();

        l.Engines[B].Accept(got.ReceiveId, 5).Should().BeTrue();
        l.Pump();

        // V-0045, interp V-0075: the acknowledgement carries the file's IDs.
        l.Sent.Where(s => s.From == B).Select(s => s.UPdu).Should().ContainSingle().Which.Should().Equal(0x00, 0x00, 0x00, 0x00, 0x10, 0x0B);
        l.Of<CftpTransferDone>(A).Single().Should().Be(new CftpTransferDone(id, true, 1, null));
        l.Engines[B].Accept(got.ReceiveId, 6).Should().BeFalse("it was answered already");
    }

    [Fact]
    public void A_refused_file_is_not_acknowledged_and_is_delivered_again_when_sent_again()
    {
        var l = new Link(AppAccepts);
        l.Engines[A].Submit(B, "f"u8.ToArray(), Data(30), 2048, 0);
        l.Pump();
        CftpFileReceived first = l.Of<CftpFileReceived>(B).Single();
        l.Engines[B].Refuse(first.ReceiveId, 0).Should().BeTrue();
        l.Pump();
        l.Sent.Should().NotContain(s => s.From == B);

        // Interp V-0076: no acknowledgement, so the sender sends it again after ack_wait.
        l.RunUntil(300_000);
        List<CftpFileReceived> got = l.Of<CftpFileReceived>(B);
        got.Should().HaveCount(2);
        got[1].ReceiveId.Should().NotBe(first.ReceiveId);
        l.Engines[B].Accept(got[1].ReceiveId, l.Now).Should().BeTrue();
        l.Pump();
        l.Of<CftpTransferDone>(A).Single().Should().Match<CftpTransferDone>(d => d.Acknowledged && d.Attempts == 2);
    }

    [Fact]
    public void A_copy_arriving_while_the_application_holds_the_file_is_not_delivered_twice()
    {
        var l = new Link(AppAccepts with { AckWait = TimeSpan.FromSeconds(10) });
        l.Engines[A].Submit(B, "f"u8.ToArray(), Data(30), 2048, 0);
        l.RunUntil(15_000);

        // The sender sent it again while the application had not answered.
        l.Sent.Count(s => s.From == A).Should().Be(2);
        CftpFileReceived got = l.Of<CftpFileReceived>(B).Should().ContainSingle().Subject;
        l.Engines[B].Accept(got.ReceiveId, l.Now).Should().BeTrue();
        l.Pump();
        l.Sent.Count(s => s.From == B).Should().Be(1);
        l.Of<CftpTransferDone>(A).Single().Acknowledged.Should().BeTrue();
    }

    [Fact]
    public void After_accept_a_copy_sent_again_for_a_lost_acknowledgement_is_acknowledged_at_once()
    {
        var l = new Link(AppAccepts);
        int acks = 0;
        l.Deliver = (from, u) => from == A || ++acks > 1;
        l.Engines[A].Submit(B, "f"u8.ToArray(), Data(30), 2048, 0);
        l.Pump();
        l.Engines[B].Accept(l.Of<CftpFileReceived>(B).Single().ReceiveId, 0).Should().BeTrue();
        l.RunUntil(1_000_000);

        // Interp V-0077: the second copy is acknowledged without asking the application again.
        l.Of<CftpFileReceived>(B).Should().ContainSingle();
        l.Sent.Count(s => s.From == B).Should().Be(2);
        l.Of<CftpTransferDone>(A).Single().Should().Match<CftpTransferDone>(d => d.Acknowledged && d.Attempts == 2);
    }

    [Fact]
    public void A_file_the_application_does_not_answer_is_let_go_unacknowledged_after_the_reassembly_timeout()
    {
        var e = new CftpEngine(AppAccepts with { ReassemblyTimeout = TimeSpan.FromMinutes(1) });
        byte[] pdu = new Bftp1Pdu("x"u8.ToArray(), Data(20)).Encode();
        e.Receive(A, new Rcop1Pdu(0, 0, 0, pdu).Encode(), 1000);
        CftpFileReceived got = e.Drain().OfType<CftpFileReceived>().Single();
        e.NextDeadline.Should().Be(61_000);
        e.Tick(61_000);
        IReadOnlyList<CftpAction> acts = e.Drain();
        acts.OfType<CftpSendUPdu>().Should().BeEmpty();
        acts.OfType<CftpNote>().Should().ContainSingle().Which.Text.Should().Contain("V-0093");
        e.NextDeadline.Should().BeNull();
        e.Accept(got.ReceiveId, 62_000).Should().BeFalse();
        e.Drain().Should().BeEmpty();

        // Sent again, it is delivered again.
        e.Receive(A, new Rcop1Pdu(0, 0, 0, pdu).Encode(), 63_000);
        e.Drain().OfType<CftpFileReceived>().Should().ContainSingle().Which.ReceiveId.Should().NotBe(got.ReceiveId);
    }

    [Fact]
    public void Without_the_option_files_are_acknowledged_on_the_last_byte_and_need_no_answer()
    {
        var e = new CftpEngine();
        e.Receive(A, new Rcop1Pdu(0, 0, 0, new Bftp1Pdu("x"u8.ToArray(), Data(20)).Encode()).Encode(), 0);
        IReadOnlyList<CftpAction> acts = e.Drain();
        acts[0].Should().BeOfType<CftpSendUPdu>().Which.IsAck.Should().BeTrue();
        CftpFileReceived got = acts.OfType<CftpFileReceived>().Single();
        got.AwaitsAccept.Should().BeFalse();
        got.ReceiveId.Should().Be(0);
        e.NextDeadline.Should().BeNull();
    }
}
