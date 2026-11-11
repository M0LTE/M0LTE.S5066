using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Core.Tests.T2;

/// <summary>
/// Authoring aid for the P7-9 round 2 sender scenarios (corpus 0.35.0): the answered rung of interp C-1281 as
/// revised, the highest rung answered since the connection was made or since the last silent wait. Built
/// like the P7-7 scenarios, from field values with the phase 1 codecs, nothing read back from the
/// implementation. Set S5066_WRITE_P79E to a directory to write the files; otherwise the test does nothing.
/// </summary>
public partial class P77Scenarios
{
    /// <summary>WN 4 Short with a 50-byte block, so a 29-byte DATA-ONLY fits one block and a 64-byte one does not (test data).</summary>
    private static readonly Wf A4S50 = new(4, Interleaver.Short, 50, 1200);

    [Fact]
    public void WriteRound2()
    {
        string? dir = Environment.GetEnvironmentVariable("S5066_WRITE_P79E");
        if (dir is null)
        {
            return;
        }

        void Save(string area, string name, ScenarioBuilder b)
        {
            string path = Path.Combine(dir, area, name + ".jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Join("\n", b.Lines()) + "\n");
        }

        Save("drc", "tx-lower-answer-keeps-answered-rung", LowerAnswerKeepsAnsweredRung());
        Save("drc", "tx-silence-unproves-answered-rung", SilenceUnprovesAnsweredRung());
    }

    /// <summary>An ACK-ONLY from the peer (RX LWE <paramref name="rxLwe"/>) carrying an EOW, EOT 10.</summary>
    private static string AckWith(int rxLwe, int eow) => Hex(Enc(new AckOnly(H(Us, P3, eow, 10), rxLwe, 0, 0, new Octets([]), null)));

    private static ScenarioBuilder LowerAnswerKeepsAnsweredRung()
    {
        var d = new Draws();
        int low2L = LowSpeed(2, Interleaver.Long);
        var b = new ScenarioBuilder(
            "drc-tx-lower-answer-keeps-answered-rung",
            ["C-1281", "C-1293", "C-0929", "C-0894", "C-0930", "C-1250", "C-1120", "C-1284", "C-1285", "C-1286", "C-0904"],
            "Ed4 C.7.6.2, pp.C-119 to C-120; C.7.5.4.4, p.C-115",
            "An answer to a short transmission at the LOW-SPEED rung does not lower the answered rung: the next bulk transmission goes back to the MAX-SPEED rung already answered, and is not a trial.",
            ArqConfig(Table(), A2L, A4L, A4S50),
            string.Empty);
        (long t, long firstAt) = CallerOpeningConfirmed(b, d, MaxSpeed(4, Interleaver.Short), Big("big1"));
        long t1 = t;
        (string Name, long At, Wf Rung, int Fsn)[] sends =
        [
            ("big1", firstAt, A4S50, 1),
            ("big2", 0, A4S50, 2),
            ("ping", 0, A2L, 3),
            ("big3", 0, A4S50, 4),
        ];
        long answer1 = 0, big2At = 0;
        for (int i = 0; i < sends.Length; i++)
        {
            (string name, long at, Wf rung, int fsn) = sends[i];
            byte[] updu = name == "ping" ? "ping"u8.ToArray() : Big(name);
            if (i > 0)
            {
                at = t + 10000;
                if (i == 1)
                {
                    big2At = at;
                }

                b.SisBytes(at, 1, ArqRequest(updu))
                    .ExpectTimerCancel("sis.soft_link_idle/0.0.1.3").ExpectTimerStart("dts.ttd", 1296000000);
                int w = d.Next();
                b.ExpectTimerStart("csma.cont_wait", w);
                t = at + w;
            }

            byte[] cpdu = ArqCpdu(at, updu);
            var tx = DataTx(rung, fsn, cpdu);
            b.TimerExpired(t, "csma.cont_wait");
            Transmit(b, tx, rung);
            t += tx.Duration;
            b.TransmitDone(t).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000);
            if (i == sends.Length - 1)
            {
                // A silent wait after 'big3', not a failed trial: the retransmission stays at the MAX-SPEED rung.
                t += 60000;
                b.TimerExpired(t, "dts.ack_wait/0.0.1.3").ExpectTimerStart("csma.lbt_wait", 3000);
                t += 3000;
                int w = d.Next();
                b.TimerExpired(t, "csma.lbt_wait").ExpectTimerStart("csma.cont_wait", w);
                t += w;
                tx = DataTx(rung, fsn, cpdu);
                b.TimerExpired(t, "csma.cont_wait");
                Transmit(b, tx, rung);
                t += tx.Duration;
                b.TransmitDone(t).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000);
                break;
            }

            long burst = (t + 5000) / 1000 * 1000;
            if (i == 0)
            {
                answer1 = burst + 1000;
            }

            b.BurstStarted(burst).BytesReceived(burst + 1000, AckWith(fsn + 1, low2L))
                .ExpectTimerCancel("dts.ack_wait/0.0.1.3").ExpectTimerCancel("dts.ttd").ExpectTimerStart("sis.soft_link_idle/0.0.1.3", 180000);
            b.BurstEnded(burst + 2000, snrTenthDb: 60);
            t = burst + 2000;
        }

        b.SetNote(ArqCommon.Replace("MAX-SPEED WN 4 Long (0x8A4: interleaver 101, speed 4)", "MAX-SPEED WN 4 Short (0x864: interleaver 011, speed 4)")
                .Replace(" Each data transmission is one D_PDU, within one block of the MAX-SPEED rung, so for latency, and with no LOW-SPEED, MAX-SPEED stands in (interp C-1284, C-1285).", string.Empty)
            + TableNote
            + "Fixture WN 4 Short: 50 bytes and 1200 ms a block (test data). The U_PDUs 'big1', 'big2' and 'big3' are 39 bytes (the text then 0x00 fill), so each DATA-ONLY is 64 bytes, more than one block of the MAX-SPEED rung WN 4 Short: a bulk transmission, at the MAX-SPEED rung (open question 7, interp C-1285). 'ping' is a 29-byte DATA-ONLY, one block: a latency transmission, at the LOW-SPEED rung once one has come (interp C-1284). "
            + $"At {t1} 'big1' (FSN 1) goes at WN 4 Short, position 2, the MAX-SPEED rung, two above the answered rung drc.default_speed, position 0, the link's first C_PDU being confirmed: a trial (interp C-1281; corpus 0.35.0 limited it to one above, WN 4 Long, and corpus 0.36.0, P7-9 round 3, lets the sender go straight to the recommendation). The ACK-ONLY at {answer1} (RX LWE 2) answers it and carries LOW-SPEED WN 2 Long (0x9A2), recorded (interp C-1262): the answered rung is WN 4 Short. 'big2' (FSN 2, requested at {big2At}) goes at WN 4 Short, not a trial, answered by an ACK-ONLY (RX LWE 3) with LOW-SPEED WN 2 Long again. 'ping' (FSN 3) is for latency and goes at the LOW-SPEED rung WN 2 Long (two blocks there, as in the P7-7 scenarios), below the answered rung, so not a trial; its answer (RX LWE 4) leaves the answered rung at WN 4 Short, the highest answered (interp C-1281 as revised in corpus 0.35.0). 'big3' (FSN 4) goes at WN 4 Short, the MAX-SPEED rung, and is not a trial. Nothing is heard: at its dts.ack_wait expiry, a silent wait that is not a failed trial, so no ceiling and no hold; the answered rung goes one below the unanswered rung, to WN 4 Long, and FSN 4 is retransmitted at the MAX-SPEED rung WN 4 Short, keeping its size (C-0549). Had the answer to 'ping' made WN 2 Long the answered rung, 'big3' would have been a trial, its silent wait a failed trial, and the retransmission held at WN 2 Long until an answer (interp C-1281 as revised in corpus 0.36.0); this retransmission is what tells the two readings apart now that the sender goes straight to the recommendation. One silent wait is short of tx_step_down_waits 2. Each request after an answer cancels sis.soft_link_idle (A-0320) and restarts dts.ttd; the answer that confirms the queue's last C_PDU cancels dts.ttd and starts the idle wait (A-0321: three answer waits, 180000). Re-derived in corpus 0.41.0 for interp C-1293: the link's first C_PDU 'open' is confirmed first, so each FSN is one higher and the times later than before.");
        return b;
    }

    private static byte[] Big(string name)
    {
        byte[] u = new byte[39];
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(u, 0);
        return u;
    }

    private static ScenarioBuilder SilenceUnprovesAnsweredRung()
    {
        var d = new Draws();
        var b = new ScenarioBuilder(
            "drc-tx-silence-unproves-answered-rung",
            ["C-1281", "C-1282", "C-1293", "C-0929", "C-0894", "C-1250", "C-1120", "C-0904", "C-0549", "C-1286", "C-1284", "C-1285"],
            "Ed4 C.7.6.2, pp.C-119 to C-120; C.7.5.6, pp.C-116 to C-117",
            "A silent answer wait on the answered rung makes that rung unproven: the retransmission there is a trial again, and when it too draws no answer the failed-trial ceiling sends the next one a rung lower.",
            ArqConfig(Table(stepDownWaits: 3), A2L, A4L),
            string.Empty);
        (long t, long pingAt) = CallerOpeningConfirmed(b, d, MaxSpeed(4, Interleaver.Long), "ping"u8.ToArray());
        long t1 = t;
        byte[] ping = ArqCpdu(pingAt, "ping"u8.ToArray());
        var tx = DataTx(A4L, 1, ping);
        b.TimerExpired(t, "csma.cont_wait");
        Transmit(b, tx, A4L);
        t += tx.Duration;
        b.TransmitDone(t).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000);
        long burst = (t + 5000) / 1000 * 1000;
        long answer1 = burst + 1000;
        b.BurstStarted(burst).BytesReceived(burst + 1000, Ack(2))
            .ExpectTimerCancel("dts.ack_wait/0.0.1.3").ExpectTimerCancel("dts.ttd").ExpectTimerStart("sis.soft_link_idle/0.0.1.3", 180000);
        b.BurstEnded(burst + 2000, snrTenthDb: 60);
        long pongAt = burst + 5000;
        byte[] pong = ArqCpdu(pongAt, "pong"u8.ToArray());
        b.SisBytes(pongAt, 1, ArqRequest("pong"u8.ToArray()))
            .ExpectTimerCancel("sis.soft_link_idle/0.0.1.3").ExpectTimerStart("dts.ttd", 1296000000).ExpectTimerStart("csma.vdcd_end", 1000);
        int w = d.Next();
        b.TimerExpired(pongAt + 1000, "csma.vdcd_end").ExpectTimerStart("csma.cont_wait", w);
        t = pongAt + 1000 + w;
        foreach (Wf wf in (Wf[])[A4L, A4L])
        {
            tx = DataTx(wf, 2, pong);
            b.TimerExpired(t, "csma.cont_wait");
            Transmit(b, tx, wf);
            t += tx.Duration;
            b.TransmitDone(t).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000);
            t += 60000;
            b.TimerExpired(t, "dts.ack_wait/0.0.1.3").ExpectTimerStart("csma.lbt_wait", 3000);
            t += 3000;
            w = d.Next();
            b.TimerExpired(t, "csma.lbt_wait").ExpectTimerStart("csma.cont_wait", w);
            t += w;
        }

        tx = DataTx(A2L, 2, pong);
        b.TimerExpired(t, "csma.cont_wait");
        Transmit(b, tx, A2L);
        t += tx.Duration;
        b.TransmitDone(t).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000);
        burst = (t + 5000) / 1000 * 1000;
        b.BurstStarted(burst).BytesReceived(burst + 1000, Ack(3))
            .ExpectTimerCancel("dts.ack_wait/0.0.1.3").ExpectTimerCancel("dts.ttd").ExpectTimerStart("sis.soft_link_idle/0.0.1.3", 180000);
        b.BurstEnded(burst + 2000, snrTenthDb: 60);
        b.SetNote(ArqCommon + TableNote.Replace("tx_step_down_waits 2", "tx_step_down_waits 3")
            + $"At {t1} 'ping' (FSN 1) goes at WN 4 Long, a trial (the link's first C_PDU is confirmed), and is answered by the ACK-ONLY at {answer1} (RX LWE 2): WN 4 Long is the answered rung. 'pong' (FSN 2, requested at {pongAt}) goes at WN 4 Long, not a trial. Nothing is heard: at its dts.ack_wait expiry, a silent wait (open question 16) on the answered rung, the answered rung goes one below the unanswered data's rung, to WN 2 Long (interp C-1281 as revised in corpus 0.35.0). FSN 2 is retransmitted at WN 4 Long, now one above the answered rung: a trial. Nothing is heard again: a failed trial, so the peer is capped at WN 2 Long for tx_ceiling_initial 2 transmissions, and the second silent wait in a row is still below tx_step_down_waits 3. The next retransmission goes at WN 2 Long under the ceiling, keeping its size (C-0549); before corpus 0.35.0 the answered rung stayed WN 4 Long and it went at WN 4 Long a third time. The ACK-ONLY (RX LWE 3) answers it. The connection is not lost: 2 answer waits, not 16 (C-1250). Re-derived in corpus 0.41.0 for interp C-1293: the link's first C_PDU 'open' is confirmed first, so each FSN is one higher and the times later than before.");
        return b;
    }
}
