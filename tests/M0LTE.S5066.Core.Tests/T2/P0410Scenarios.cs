using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Core.Tests.T2;

/// <summary>
/// Authoring aid for the issue #109 sender scenario of corpus 0.41.0 (coordinator ruling 2026-11-11, interp
/// C-1293): no trial above the answered rung until the link's first C_PDU is confirmed. Built like the P7-7
/// scenarios, from field values with the phase 1 codecs, nothing read back from the implementation. Set
/// S5066_WRITE_P0410 to a directory to write the file; otherwise the test does nothing.
/// </summary>
public partial class P77Scenarios
{
    [Fact]
    public void WriteFirstCpdu()
    {
        string? dir = Environment.GetEnvironmentVariable("S5066_WRITE_P0410");
        if (dir is null)
        {
            return;
        }

        string path = Path.Combine(dir, "drc", "tx-no-trial-before-first-cpdu-confirmed.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", NoTrialBeforeFirstCpdu().Lines()) + "\n");
    }

    private static ScenarioBuilder NoTrialBeforeFirstCpdu()
    {
        var d = new Draws();
        var b = new ScenarioBuilder(
            "drc-tx-no-trial-before-first-cpdu-confirmed",
            ["C-1293", "C-1281", "C-1282", "C-0929", "C-0894", "C-1250", "C-1120", "C-0904", "C-0549", "C-0679", "C-0680", "C-1286", "C-1284", "C-1285"],
            "Ed4 C.7.6.2, pp.C-119 to C-120; C.7.5.4.4, p.C-115",
            "On a new link nothing goes above the answered rung until a C_PDU of the link has been confirmed, even with a higher recommendation in force and after a silent wait; once the first C_PDU is acknowledged in full the next goes straight to the recommendation, a trial.",
            ArqConfig(Table(), A2L, A4L),
            string.Empty);
        long t = CallerOpeningWith(b, d, MaxSpeed(4, Interleaver.Long), Open);
        long t0 = t;
        byte[] open = ArqCpdu(0, Open);

        // The link's first C_PDU at the answered rung, not the MAX-SPEED rung; nothing heard.
        var tx = DataTx(A2L, 0, open);
        b.TimerExpired(t, "csma.cont_wait");
        Transmit(b, tx, A2L);
        t += tx.Duration;
        b.TransmitDone(t).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000);
        t += 60000;
        long expiry = t;
        b.TimerExpired(t, "dts.ack_wait/0.0.1.3").ExpectTimerStart("csma.lbt_wait", 3000);
        t += 3000;
        int w = d.Next();
        b.TimerExpired(t, "csma.lbt_wait").ExpectTimerStart("csma.cont_wait", w);
        t += w;
        long retx = t;

        // Still unconfirmed: the retransmission stays at the answered rung.
        tx = DataTx(A2L, 0, open);
        b.TimerExpired(t, "csma.cont_wait");
        Transmit(b, tx, A2L);
        t += tx.Duration;
        b.TransmitDone(t).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000);
        long burst = (t + 5000) / 1000 * 1000;
        long answer = burst + 1000;
        b.BurstStarted(burst).BytesReceived(burst + 1000, Ack(1))
            .ExpectTimerCancel("dts.ack_wait/0.0.1.3").ExpectTimerCancel("dts.ttd").ExpectTimerStart("sis.soft_link_idle/0.0.1.3", 180000);
        b.BurstEnded(burst + 2000, snrTenthDb: 60);

        // Confirmed: 'ping' goes straight to the MAX-SPEED rung, a trial, and is answered.
        long pingAt = burst + 5000;
        byte[] ping = ArqCpdu(pingAt, "ping"u8.ToArray());
        b.SisBytes(pingAt, 1, ArqRequest("ping"u8.ToArray()))
            .ExpectTimerCancel("sis.soft_link_idle/0.0.1.3").ExpectTimerStart("dts.ttd", 1296000000).ExpectTimerStart("csma.vdcd_end", 1000);
        w = d.Next();
        b.TimerExpired(pingAt + 1000, "csma.vdcd_end").ExpectTimerStart("csma.cont_wait", w);
        t = pingAt + 1000 + w;
        long t1 = t;
        tx = DataTx(A4L, 1, ping);
        b.TimerExpired(t, "csma.cont_wait");
        Transmit(b, tx, A4L);
        t += tx.Duration;
        b.TransmitDone(t).ExpectTimerStart("dts.ack_wait/0.0.1.3", 60000);
        burst = (t + 5000) / 1000 * 1000;
        b.BurstStarted(burst).BytesReceived(burst + 1000, Ack(2))
            .ExpectTimerCancel("dts.ack_wait/0.0.1.3").ExpectTimerCancel("dts.ttd").ExpectTimerStart("sis.soft_link_idle/0.0.1.3", 180000);
        b.BurstEnded(burst + 2000, snrTenthDb: 60);

        string common = ArqCommon[..ArqCommon.IndexOf(" The link's first C_PDU:", StringComparison.Ordinal)];
        b.SetNote(common + " " + TableNote + $"Coordinator ruling 2026-11-11 (issue #109, interp C-1293): on a new link the sender does not trial a rung above the answered one until the first complete C_PDU of the link has been confirmed, acknowledged in full (C-0679, C-0680). At {t0} 'open' (FSN 0), the link's first C_PDU, goes at WN 2 Long, the answered rung (drc.default_speed, interp C-1281), though MAX-SPEED WN 4 Long is in force and one block of it would hold the D_PDU: not a trial. Nothing is heard: at {expiry} dts.ack_wait expires, a silent wait that is not a failed trial (no ceiling); the answered rung stays the lowest rung (it never goes below it), and one silent wait is short of tx_step_down_waits 2. FSN 0 is retransmitted at {retx}, still at WN 2 Long, since the link has no confirmed C_PDU yet (without the ruling it would go at WN 4 Long, a trial). The ACK-ONLY (RX LWE 1) at {answer} acknowledges it: the whole C_PDU is acknowledged, so the request is confirmed and the link's first C_PDU is through; the queue is empty, so dts.ttd is cancelled and the idle wait starts (A-0321: three answer waits, 180000). 'ping' (FSN 1), requested 3000 ms after that burst's end (the idle wait cancelled, A-0320, dts.ttd restarted, csma.vdcd_end 1000 for the rest of the ACK's predicted burst, K-0067), goes at {t1} straight to the MAX-SPEED rung WN 4 Long, one above the answered rung: a trial, now allowed. The ACK-ONLY (RX LWE 2) answers it: WN 4 Long is the answered rung.");
        return b;
    }
}
