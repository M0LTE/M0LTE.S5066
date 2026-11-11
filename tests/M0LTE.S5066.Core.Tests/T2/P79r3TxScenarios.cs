using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Core.Tests.T2;

/// <summary>
/// Authoring aid for the P7-9 round 3 sender scenario (corpus 0.36.0): the sender goes straight to the
/// recommended rung, with no one-rung climb limit, and after a failed trial no higher than the answered rung
/// until the peer answers (interp C-1281 as revised). Built like the P7-7 scenarios, from field values with
/// the phase 1 codecs, nothing read back from the implementation. Set S5066_WRITE_P79R3 to a directory to
/// write the files (with P77Scenarios.WriteRound2 and Write for the scenarios they share notes with).
/// </summary>
public partial class P77Scenarios
{
    /// <summary>WN 6 Long with a 100-byte block (test data), so the 29-byte DATA-ONLY fits one block.</summary>
    private static readonly Wf A6L = new(6, Interleaver.Long, 100, 2400);

    [Fact]
    public void WriteRound3()
    {
        string? dir = Environment.GetEnvironmentVariable("S5066_WRITE_P79R3");
        if (dir is null)
        {
            return;
        }

        string path = Path.Combine(dir, "drc", "tx-straight-to-recommendation.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", StraightToRecommendation().Lines()) + "\n");
    }

    private static ScenarioBuilder StraightToRecommendation()
    {
        var d = new Draws();
        var b = new ScenarioBuilder(
            "drc-tx-straight-to-recommendation",
            ["C-1281", "C-1293", "C-0929", "C-0894", "C-1250", "C-1120", "C-0904", "C-0549", "C-1286", "C-1284", "C-1285"],
            "Ed4 C.7.6.2, pp.C-119 to C-120; C.7.5.4.4, p.C-115",
            "The sender goes straight to the recommended rung, several above the answered one; when that trial fails it goes back to the answered rung until the peer answers, then no higher than the failed-trial ceiling while it lasts.",
            ArqConfig(Table(), A2L, A4L, A4S, A6L),
            string.Empty);
        (long t, long pingAt) = CallerOpeningConfirmed(b, d, MaxSpeed(6, Interleaver.Long), "ping"u8.ToArray());
        long t1 = t;
        byte[] ping = ArqCpdu(pingAt, "ping"u8.ToArray());

        // The jump to WN 6 Long, a trial that fails.
        var tx = DataTx(A6L, 1, ping);
        b.TimerExpired(t, "csma.cont_wait");
        Transmit(b, tx, A6L);
        t += tx.Duration;
        b.TransmitDone(t).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000);
        t += 60000;
        b.TimerExpired(t, "dts.ack_wait/0.0.1.3").ExpectTimerStart("csma.lbt_wait", 3000);
        t += 3000;
        int w = d.Next();
        b.TimerExpired(t, "csma.lbt_wait").ExpectTimerStart("csma.cont_wait", w);
        t += w;

        // Back to the answered rung until the peer answers.
        tx = DataTx(A2L, 1, ping);
        b.TimerExpired(t, "csma.cont_wait");
        Transmit(b, tx, A2L);
        t += tx.Duration;
        b.TransmitDone(t).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000);
        long burst = (t + 5000) / 1000 * 1000;
        b.BurstStarted(burst).BytesReceived(burst + 1000, Ack(2))
            .ExpectTimerCancel("dts.ack_wait/0.0.1.3").ExpectTimerCancel("dts.ttd").ExpectTimerStart("sis.soft_link_idle/0.0.1.3", 180000);
        b.BurstEnded(burst + 2000, snrTenthDb: 60);

        // Under the ceiling: WN 4 Short, a trial, answered.
        long pongAt = burst + 60000;
        byte[] pong = ArqCpdu(pongAt, "pong"u8.ToArray());
        b.SisBytes(pongAt, 1, ArqRequest("pong"u8.ToArray()))
            .ExpectTimerCancel("sis.soft_link_idle/0.0.1.3").ExpectTimerStart("dts.ttd", 1296000000);
        w = d.Next();
        b.ExpectTimerStart("csma.cont_wait", w);
        t = pongAt + w;
        tx = DataTx(A4S, 2, pong);
        b.TimerExpired(t, "csma.cont_wait");
        Transmit(b, tx, A4S);
        t += tx.Duration;
        b.TransmitDone(t).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000);
        burst = (t + 5000) / 1000 * 1000;
        b.BurstStarted(burst).BytesReceived(burst + 1000, Ack(3))
            .ExpectTimerCancel("dts.ack_wait/0.0.1.3").ExpectTimerCancel("dts.ttd").ExpectTimerStart("sis.soft_link_idle/0.0.1.3", 180000);
        b.BurstEnded(burst + 2000, snrTenthDb: 60);

        // The ceiling has ended: WN 6 Long again.
        long lastAt = burst + 60000;
        byte[] last = ArqCpdu(lastAt, "last"u8.ToArray());
        b.SisBytes(lastAt, 1, ArqRequest("last"u8.ToArray()))
            .ExpectTimerCancel("sis.soft_link_idle/0.0.1.3").ExpectTimerStart("dts.ttd", 1296000000);
        w = d.Next();
        b.ExpectTimerStart("csma.cont_wait", w);
        t = lastAt + w;
        tx = DataTx(A6L, 3, last);
        b.TimerExpired(t, "csma.cont_wait");
        Transmit(b, tx, A6L);
        t += tx.Duration;
        b.TransmitDone(t).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000);
        b.SetNote(ArqCommon.Replace("MAX-SPEED WN 4 Long (0x8A4: interleaver 101, speed 4)", "MAX-SPEED WN 6 Long (0x8A6: interleaver 101, speed 6)")
            + TableNote
            + "Fixture WN 4 Short: 25 bytes and 1200 ms a block, WN 6 Long: 100 bytes and 2400 ms (test data); the 29-byte DATA-ONLY is one block at WN 6 Long (EOT ceil(2550 / 500) = 6, then six PADDING of 11 bytes, 3150 ms) and two at WN 4 Short (EOT 6, then one PADDING in block 1 with EOT ceil(1350 / 500) = 3, 3150 ms). "
            + $"At {t1} 'ping' (FSN 1) goes at WN 6 Long, the MAX-SPEED rung, position 3, three above the answered rung drc.default_speed, position 0, the link's first C_PDU being confirmed: a trial (interp C-1281 as revised in corpus 0.36.0, P7-9 round 3; before, it could go only one above, WN 4 Long). Nothing is heard; at its dts.ack_wait expiry, a failed trial: the peer is capped at position 2, WN 4 Short, for tx_ceiling_initial 2 transmissions, and until the peer answers no transmission goes above the answered rung. So FSN 1 is retransmitted at WN 2 Long, keeping its size (C-0549), not at the ceiling: not a trial, and failed trials never come two in a row (C-1250 counts 16 answer waits). The ceiling counts it, one transmission left. The ACK-ONLY (RX LWE 2) answers it: WN 2 Long is the answered rung. 'pong' (FSN 2, requested at {pongAt}) goes at WN 4 Short, the MAX-SPEED rung under the ceiling, its last transmission: a trial, answered (RX LWE 3), so the hold-off goes back to tx_ceiling_initial and WN 4 Short is the answered rung. 'last' (FSN 3, requested at {lastAt}) goes at WN 6 Long again, the ceiling having ended: a trial. Each request after an answer cancels sis.soft_link_idle (A-0320) and restarts dts.ttd; the answer that confirms the queue's last C_PDU cancels dts.ttd and starts the idle wait (A-0321: three answer waits, 180000). Re-derived in corpus 0.41.0 for interp C-1293: the link's first C_PDU 'open' is confirmed first, so each FSN is one higher and the times later than before.");
        return b;
    }
}
