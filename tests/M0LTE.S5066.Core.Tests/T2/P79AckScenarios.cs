using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Core.Tests.T2;

/// <summary>
/// Authoring aid for the P7-9e sender scenario (corpus 0.38.0, coordinator ruling 2026-11-10): for the
/// sender's rate logic a transmission carrying ARQ data counts as answered only when the peer acknowledges
/// a D_PDU it carried; the peer's own traffic meanwhile is an answer for link failure (C-1250) but not for
/// the rung, so the ack wait expiry is a failed trial (interp C-1281 as revised). Built like the P7-7
/// scenarios, from field values with the phase 1 codecs, nothing read back from the implementation. Set
/// S5066_WRITE_P79ACK to a directory to write the file; otherwise the test does nothing.
/// </summary>
public partial class P77Scenarios
{
    [Fact]
    public void WriteRoundAck()
    {
        string? dir = Environment.GetEnvironmentVariable("S5066_WRITE_P79ACK");
        if (dir is null)
        {
            return;
        }

        void Save(string name, ScenarioBuilder b)
        {
            string path = Path.Combine(dir, "drc", name + ".jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Join("\n", b.Lines()) + "\n");
        }

        Save("tx-peer-traffic-is-no-answer", PeerTrafficIsNoAnswer());
        Save("tx-step-down-despite-peer-traffic", StepDownDespitePeerTraffic());
        Save("tx-ack-failing-all-is-no-answer", AckFailingAllIsNoAnswer());
    }

    /// <summary>The peer's own ARQ U_PDU 'chat' from its SAP 9 to our SAP 5, as a C_PDU, TTD as <see cref="ArqCpdu"/>.</summary>
    private static byte[] PeerCpdu(long t, byte[] updu)
    {
        var time = new TimeConfig(2026, 1, 0);
        byte[] spdu = SpduCodec.Encode(new SpduData(0, 9, 5, false, TtdCalendar.Encode(time, t + 1296000000L), updu));
        return CpduCodec.Encode(new CpduData(spdu));
    }

    private static ScenarioBuilder PeerTrafficIsNoAnswer()
    {
        var d = new Draws();
        byte[] chat = "chat"u8.ToArray();
        var b = new ScenarioBuilder(
            "drc-tx-peer-traffic-is-no-answer",
            ["C-1281", "C-1250", "C-0912", "C-0929", "C-0894", "C-1120", "C-0174", "C-0904", "C-0549", "C-1286", "C-1284", "C-1285"],
            "Ed4 C.7.6.2, pp.C-119 to C-120; C.7.5.6, pp.C-116 to C-117",
            "A trial whose data the peer never acknowledges fails at its ack wait expiry even though the peer sent its own data meanwhile: for the rung only an acknowledgement of the transmission's D_PDUs is an answer.",
            ArqConfig(Table(), A2L, A4L),
            ArqCommon + TableNote
            + "At 108000 'ping' (FSN 0) goes at WN 4 Long, the MAX-SPEED rung, one above the answered rung drc.default_speed WN 2 Long: a trial. At 116000 the peer's own DATA-ONLY (FSN 0, 'chat' from its SAP 9 to our SAP 5, EOW 0, EOT 10) arrives: an answer for link failure (C-1250), delivered with S_UNIDATA_INDICATION to conn 1, and it owes an acknowledgement (C-0174). It acknowledges nothing of ours, so for the rung it is no answer (coordinator ruling 2026-11-10, interp C-1281 as revised in corpus 0.38.0; C-0912: a transmission heard from the peer with none of our D_PDUs accounted for suggests ours were lost). The burst was addressed only to us: LBT rule 1 from its predicted end, 121000, then contention. The ACK-ONLY (RX LWE 1, EOW the rotation's MAX-SEGMENT 255), filled with two copies of itself (interp C-1154: 3 x 15 bytes of the 50-byte block, no room for a PADDING), goes at WN 4 Long: a transmission with nothing but an acknowledgement is for latency, and with no LOW-SPEED MAX-SPEED stands in (interp C-1284, C-1285); it carries no ARQ data, so it is not a trial and starts no dts.ack_wait, and FSN 0's wait from 111150 runs on. At 171150 it expires with FSN 0 unacknowledged: a failed trial (before corpus 0.38.0 the peer's DATA-ONLY made it no silent wait, and FSN 0 went again at WN 4 Long). The peer is capped at WN 2 Long for tx_ceiling_initial 2 transmissions and, until a transmission is answered, nothing goes above the answered rung: FSN 0 is retransmitted at WN 2 Long, keeping its size (C-0549), not a trial. The ACK-ONLY (RX LWE 1) acknowledges it: answered, WN 2 Long stays the answered rung; the queue is empty, so dts.ttd is cancelled and the idle wait starts (A-0321: three answer waits, 180000).");
        long t = CallerOpening(b, d);
        byte[] ping = ArqCpdu(0, "ping"u8.ToArray());

        // The trial at WN 4 Long.
        var tx = DataTx(A4L, 0, ping);
        b.TimerExpired(t, "csma.cont_wait");
        Transmit(b, tx, A4L);
        t += tx.Duration;
        long ackWaitEnd = t + 60000;
        b.TransmitDone(t).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000);

        t = PeerChatAndOurAck(b, d, t, PeerCpdu(0, chat), chat);
        if (t + 1000 > ackWaitEnd)
        {
            throw new InvalidOperationException("timeline overran");
        }

        // The trial's wait expires with FSN 0 unacknowledged: a failed trial.
        t = ackWaitEnd;
        b.TimerExpired(t, "dts.ack_wait/0.0.1.3").ExpectTimerStart("csma.lbt_wait", 3000);
        t += 3000;
        int w = d.Next();
        b.TimerExpired(t, "csma.lbt_wait").ExpectTimerStart("csma.cont_wait", w);
        t += w;
        tx = DataTx(A2L, 0, ping);
        b.TimerExpired(t, "csma.cont_wait");
        Transmit(b, tx, A2L);
        t += tx.Duration;
        b.TransmitDone(t).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000);
        long burst = (t + 5000) / 1000 * 1000;
        b.BurstStarted(burst).BytesReceived(burst + 1000, Ack(1))
            .ExpectTimerCancel("dts.ack_wait/0.0.1.3").ExpectTimerCancel("dts.ttd").ExpectTimerStart("sis.soft_link_idle/0.0.1.3", 180000);
        b.BurstEnded(burst + 2000, snrTenthDb: 60);
        return b;
    }

    /// <summary>
    /// After our data transmission ended at <paramref name="t"/>: the peer's own DATA-ONLY (FSN 0, EOW 0,
    /// EOT 10) a second later than the next whole 4000 ms, delivered to conn 1 (S-0122) and owing an
    /// acknowledgement (C-0174); LBT rule 1 from its predicted end (K-0067), contention, and our ACK-ONLY
    /// (RX LWE 1) at WN 4 Long. Returns the time that transmission ends.
    /// </summary>
    private static long PeerChatAndOurAck(ScenarioBuilder b, Draws d, long t, byte[] cpdu, byte[] updu)
    {
        // The peer's own data, acknowledging nothing of ours.
        long burst = (t + 4000) / 1000 * 1000;
        var template = (DataOnly)Decode("90eb9000146a102103ec0a000098ef00005940000070696e671ca45ae5");
        var peerData = template with { Header = H(Us, P3, 0, 10), TxFsn = 0, Segment = new Octets(cpdu) };
        var indication = new SisUnidataIndication(0, 5, new SisAddress(Us), 1, 9, new SisAddress(P3), updu);   // TxMode 1, ARQ, as dts-arq/data-ack-merge (S-0122)
        b.BurstStarted(burst).BytesReceived(burst + 1000, Hex(Enc(peerData)))
            .ExpectSisBytes(1, ScenarioBuilder.SisHex(indication));
        b.BurstEnded(burst + 2000, wn: 2, snrTenthDb: 60).ExpectTimerStart("csma.vdcd_end", (burst + 1000 + 5000) - (burst + 2000));
        t = burst + 6000;
        int w = d.Next();
        b.TimerExpired(t, "csma.vdcd_end").ExpectTimerStart("csma.cont_wait", w);
        t += w;

        // Our acknowledgement of it: no ARQ data, at the MAX-SPEED rung.
        // C-1154: the fill is copies of the acknowledgement first, three 15-byte ACK-ONLYs in the 50-byte block; no room for an 11-byte PADDING after them.
        Item ackItem = eot => new AckOnly(H(P3, Us, MaxSegment255, eot), 1, 0, 0, new Octets([]), null);
        var ackTx = Transmission(A4L, [ackItem, ackItem, ackItem], P3, _ => MaxSegment255);
        b.TimerExpired(t, "csma.cont_wait");
        Transmit(b, ackTx, A4L);
        t += ackTx.Duration;
        b.TransmitDone(t);
        return t;
    }

    private static ScenarioBuilder StepDownDespitePeerTraffic()
    {
        var d = new Draws();
        byte[] chat = "chat"u8.ToArray();
        var b = new ScenarioBuilder(
            "drc-tx-step-down-despite-peer-traffic",
            ["C-1282", "C-1281", "C-1250", "C-0912", "C-0929", "C-0894", "C-1120", "C-0174", "C-0904", "C-0549", "C-1286", "C-1284", "C-1285"],
            "Ed4 C.7.6.2, pp.C-119 to C-120; C.7.5.6, pp.C-116 to C-117",
            "Data at the answered rung that the peer never acknowledges is a silent wait even though the peer sent its own data meanwhile, so the sender steps down.",
            ArqConfig(Table(stepDownWaits: 1), A2L, A4L),
            ArqCommon + TableNote.Replace("tx_step_down_waits 2", "tx_step_down_waits 1")
            + "At 108000 'ping' (FSN 0) goes at WN 4 Long, the MAX-SPEED rung, one above the answered WN 2 Long: a trial, acknowledged by the ACK-ONLY (RX LWE 1), so WN 4 Long is the answered rung and the queue's last C_PDU is confirmed (dts.ttd cancelled, the idle wait started, A-0321). 'pong' (FSN 1, requested at 240000) goes at WN 4 Long, the answered rung: not a trial. The peer's own DATA-ONLY ('chat', FSN 0, EOW 0, EOT 10) follows: an answer for link failure (C-1250), delivered with S_UNIDATA_INDICATION, owing an acknowledgement (C-0174), but no answer for the rung (coordinator ruling 2026-11-10, interp C-1281 as revised in corpus 0.38.0; C-0912). Our ACK-ONLY (RX LWE 1, filled with two copies, interp C-1154) goes at WN 4 Long with no ARQ data, so FSN 1's wait runs on. At its expiry FSN 1 is unacknowledged: a silent wait that is not a failed trial, so the answered rung goes one below WN 4 Long, to WN 2 Long, and with tx_step_down_waits 1 the sender steps down one rung (interp C-1282): FSN 1 is retransmitted at WN 2 Long, keeping its size (C-0549), where the MAX-SPEED rung less the step down puts it (before corpus 0.38.0 the peer's DATA-ONLY ended the silence count, and FSN 1 went again at WN 4 Long). The ACK-ONLY (RX LWE 2) acknowledges it: answered, which ends the step down; the queue is empty again.");
        long t = CallerOpening(b, d);
        byte[] ping = ArqCpdu(0, "ping"u8.ToArray());
        byte[] pong = ArqCpdu(240000, "pong"u8.ToArray());

        // The trial at WN 4 Long, acknowledged.
        var tx = DataTx(A4L, 0, ping);
        b.TimerExpired(t, "csma.cont_wait");
        Transmit(b, tx, A4L);
        t += tx.Duration;
        b.TransmitDone(t).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000);
        long burst = (t + 5000) / 1000 * 1000;
        b.BurstStarted(burst).BytesReceived(burst + 1000, Ack(1))
            .ExpectTimerCancel("dts.ack_wait/0.0.1.3").ExpectTimerCancel("dts.ttd").ExpectTimerStart("sis.soft_link_idle/0.0.1.3", 180000);
        b.BurstEnded(burst + 2000, snrTenthDb: 60);

        // 'pong' at the answered rung, not a trial.
        b.SisBytes(240000, 1, ArqRequest("pong"u8.ToArray()))
            .ExpectTimerCancel("sis.soft_link_idle/0.0.1.3").ExpectTimerStart("dts.ttd", 1296000000);
        int w = d.Next();
        b.ExpectTimerStart("csma.cont_wait", w);
        t = 240000 + w;
        tx = DataTx(A4L, 1, pong);
        b.TimerExpired(t, "csma.cont_wait");
        Transmit(b, tx, A4L);
        t += tx.Duration;
        long ackWaitEnd = t + 60000;
        b.TransmitDone(t).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000);

        t = PeerChatAndOurAck(b, d, t, PeerCpdu(240000, chat), chat);
        if (t + 1000 > ackWaitEnd)
        {
            throw new InvalidOperationException("timeline overran");
        }

        // A silent wait: the answered rung drops and the sender steps down.
        t = ackWaitEnd;
        b.TimerExpired(t, "dts.ack_wait/0.0.1.3").ExpectTimerStart("csma.lbt_wait", 3000);
        t += 3000;
        w = d.Next();
        b.TimerExpired(t, "csma.lbt_wait").ExpectTimerStart("csma.cont_wait", w);
        t += w;
        tx = DataTx(A2L, 1, pong);
        b.TimerExpired(t, "csma.cont_wait");
        Transmit(b, tx, A2L);
        t += tx.Duration;
        b.TransmitDone(t).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000);
        burst = (t + 5000) / 1000 * 1000;
        b.BurstStarted(burst).BytesReceived(burst + 1000, Ack(2))
            .ExpectTimerCancel("dts.ack_wait/0.0.1.3").ExpectTimerCancel("dts.ttd").ExpectTimerStart("sis.soft_link_idle/0.0.1.3", 180000);
        b.BurstEnded(burst + 2000, snrTenthDb: 60);
        return b;
    }

    private static ScenarioBuilder AckFailingAllIsNoAnswer()
    {
        var d = new Draws();
        var b = new ScenarioBuilder(
            "drc-tx-ack-failing-all-is-no-answer",
            ["C-1281", "C-1121", "C-0908", "C-0910", "C-0776", "C-1120", "C-0929", "C-0894", "C-0904", "C-0549", "C-1286", "C-1284", "C-1285"],
            "Ed4 C.7.6.2, pp.C-119 to C-120; C.7.5.6, pp.C-116 to C-117",
            "A trial whose D_PDUs the peer's acknowledgement marks all failed is a failed trial at once, though dts.ack_wait stops without expiring: for the rung only an acknowledgement of the transmission's D_PDUs is an answer.",
            ArqConfig(Table(), A2L, A4L),
            ArqCommon + TableNote
            + "At 108000 'ping' (FSN 0) goes at WN 4 Long, the MAX-SPEED rung, one above the answered rung drc.default_speed WN 2 Long: a trial. The peer's ACK-ONLY with RX LWE 0 and no selective bits follows: FSN 0 is at the RX LWE, so the peer has not received it (C-0776) and it has failed (C-0908, C-0910, interp C-1121); nothing awaits an acknowledgement, so dts.ack_wait stops (C-1120) without an expiry. The ACK is an answer for link failure (C-1250) but acknowledges none of the trial's D_PDUs and fails every one, so the trial is unanswered as at an expiry (interp C-1281 as revised in corpus 0.38.0): a failed trial (before, the trial was neither answered nor failed and FSN 0 went again at WN 4 Long, a trial again). The peer is capped at WN 2 Long for tx_ceiling_initial 2 transmissions and, until a transmission is answered, nothing goes above the answered rung. The burst was addressed only to us: LBT rule 1 from its predicted end, then contention, and FSN 0 is retransmitted at WN 2 Long, keeping its size (C-0549), not a trial. The ACK-ONLY (RX LWE 1) acknowledges it: answered; the queue is empty, so dts.ttd is cancelled and the idle wait starts (A-0321: three answer waits, 180000).");
        long t = CallerOpening(b, d);
        byte[] ping = ArqCpdu(0, "ping"u8.ToArray());

        // The trial at WN 4 Long.
        var tx = DataTx(A4L, 0, ping);
        b.TimerExpired(t, "csma.cont_wait");
        Transmit(b, tx, A4L);
        t += tx.Duration;
        b.TransmitDone(t).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000);

        // The peer's acknowledgement fails FSN 0: the wait stops, a failed trial.
        long burst = (t + 5000) / 1000 * 1000;
        b.BurstStarted(burst).BytesReceived(burst + 1000, Ack(0)).ExpectTimerCancel("dts.ack_wait/0.0.1.3");
        b.BurstEnded(burst + 2000, snrTenthDb: 60).ExpectTimerStart("csma.vdcd_end", (burst + 1000 + 5000) - (burst + 2000));
        t = burst + 6000;
        int w = d.Next();
        b.TimerExpired(t, "csma.vdcd_end").ExpectTimerStart("csma.cont_wait", w);
        t += w;
        tx = DataTx(A2L, 0, ping);
        b.TimerExpired(t, "csma.cont_wait");
        Transmit(b, tx, A2L);
        t += tx.Duration;
        b.TransmitDone(t).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000);
        burst = (t + 5000) / 1000 * 1000;
        b.BurstStarted(burst).BytesReceived(burst + 1000, Ack(1))
            .ExpectTimerCancel("dts.ack_wait/0.0.1.3").ExpectTimerCancel("dts.ttd").ExpectTimerStart("sis.soft_link_idle/0.0.1.3", 180000);
        b.BurstEnded(burst + 2000, snrTenthDb: 60);
        return b;
    }
}
