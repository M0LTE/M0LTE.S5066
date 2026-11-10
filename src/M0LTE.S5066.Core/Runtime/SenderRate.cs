using M0LTE.S5066.Core.Dts;

namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// The sender half of rate selection (core-architecture.md 9, phase 7, P7-7): the per-peer state the
/// planner's waveform choice reads and the named events that change it. One lives in each peer's
/// DtsPeers entry and goes with it (core-architecture.md 6 rule 11). Every field is an integer or a
/// ladder position; nothing expires by time and nothing draws from the PRNG (design shape item 2).
/// </summary>
/// <remarks>
/// Positions are indexes into <c>drc.table.ladder</c>, slowest first. <see cref="Wn0"/> stands for
/// WN 0, which is never on the ladder and is used only to honour an Ed3 peer's 75 bps ceiling
/// (open question 11; C-0463, C-0470, C-0959). With an empty ladder none of this runs: every
/// transmission is at <c>drc.default_speed</c> (design shape item 6; open question 13).
/// </remarks>
internal sealed class SenderRate
{
    /// <summary>The position below every ladder rung: WN 0 (open question 11).</summary>
    public const int Wn0 = -1;

    /// <summary>
    /// The highest position that has drawn an answer from this peer since the connection was made or
    /// since the last silent wait (interp C-1281), or null when there is none to compare with (no
    /// connection made yet, or a peer we never link with). An answer to a transmission at a lower rung
    /// does not lower it: a short transmission at the LOW-SPEED rung proves nothing against the rung
    /// above (P7-9 round 2).
    /// </summary>
    public int? AnsweredRung { get; set; }

    /// <summary>The position of the last transmission that carried a D_PDU to this peer, or null.</summary>
    public int? LastSentRung { get; set; }

    /// <summary>
    /// One transmission that carried ARQ data to this peer and awaits its answer (interp C-1281): its build
    /// number, its ladder position, whether it was a trial (above the answered rung when it was sent), and
    /// the time its own answer wait runs out (its end plus one answer wait), null until it has ended.
    /// </summary>
    internal sealed record Awaited(long Transmission, int Rung, bool Trial)
    {
        public long? DeadlineMs { get; set; }
    }

    /// <summary>
    /// The transmissions that carried ARQ data to this peer and await their answer, oldest first (issue
    /// #109, interp C-1281): each is judged on its own answer wait, so a sender that sends again before an
    /// earlier wait has run out (which restarts <c>dts.ack_wait</c>, C-1120) still learns of the silence.
    /// </summary>
    public List<Awaited> Pending { get; } = [];

    /// <summary>The position of the last transmission awaiting its answer that carried ARQ data to this peer, or null.</summary>
    public int? DataRung => Pending.Count > 0 ? Pending[^1].Rung : null;

    /// <summary>The position of the latest trial in flight: ARQ data sent above <see cref="AnsweredRung"/> and not yet answered (interp C-1281).</summary>
    public int? TrialRung => Pending.LastOrDefault(p => p.Trial)?.Rung;

    /// <summary>
    /// A trial failed and the peer has not answered since (P7-9 round 3, interp C-1281): until it answers,
    /// no transmission to it goes above <see cref="AnsweredRung"/>, so failed trials never come two in a row.
    /// </summary>
    public bool FailedSinceAnswer { get; set; }

    /// <summary>
    /// Some transmission that carried ARQ data to this peer awaits its acknowledgement (coordinator ruling
    /// 2026-11-10, interp C-1281). Until none does, other answers from the peer (C-1250) say nothing about the rung.
    /// </summary>
    public bool DataAwaited => Pending.Count > 0;

    /// <summary>The last transmission that carried a D_PDU to this peer carried ARQ data.</summary>
    public bool LastSentData { get; set; }

    /// <summary>The build number of the last transmission awaiting its answer that carried ARQ data to this peer, or null.</summary>
    public long? DataTransmission => Pending.Count > 0 ? Pending[^1].Transmission : null;

    /// <summary><c>dts.ack_wait</c> expiries in a row with no answer from the peer (open question 16).</summary>
    public int SilentWaits { get; set; }

    /// <summary>Rungs stepped down after silence (plan 6.2; interp C-1282).</summary>
    public int StepDown { get; set; }

    /// <summary>The failed-trial ceiling, a position, or null when none is in force (design shape item 7).</summary>
    public int? Ceiling { get; set; }

    /// <summary>Transmissions to the peer the ceiling still lasts.</summary>
    public int CeilingLeft { get; set; }

    /// <summary>The length the next failed-trial ceiling takes, or 0 for <c>tx_ceiling_initial</c>.</summary>
    public int HoldOff { get; set; }

    /// <summary>
    /// D_CONNECTION_MADE (interp C-1281): the link was made at <c>drc.default_speed</c>, since every
    /// recommendation ended with the previous connection (open question 4), so that rung counts as
    /// answered; the silence count, step down and any trial start afresh. A ceiling and its hold-off
    /// are kept: they describe the path, not the connection.
    /// </summary>
    public void OnConnectionMade(int defaultRung)
    {
        AnsweredRung = defaultRung;
        Pending.Clear();
        FailedSinceAnswer = false;
        SilentWaits = 0;
        StepDown = 0;
    }

    /// <summary>The connection ended (open question 4): nothing awaits an answer any more.</summary>
    public void OnConnectionEnded()
    {
        AnsweredRung = null;
        Pending.Clear();
        FailedSinceAnswer = false;
        SilentWaits = 0;
        StepDown = 0;
    }

    /// <summary>
    /// A transmission at <paramref name="rung"/>, build number <paramref name="transmission"/>, carried a
    /// D_PDU to this peer; <paramref name="arqData"/> when it carried ARQ data, so its acknowledgement is
    /// awaited (open question 16; ruling 2026-11-10). A ceiling in force counts down one transmission and
    /// is lifted at 0.
    /// </summary>
    public void OnSent(int rung, bool arqData, long transmission)
    {
        LastSentRung = rung;
        LastSentData = arqData;
        if (Ceiling is not null && --CeilingLeft <= 0)
        {
            Ceiling = null;
            CeilingLeft = 0;
        }

        if (arqData)
        {
            Pending.Add(new Awaited(transmission, rung, AnsweredRung is int answered && rung > answered));
        }
    }

    /// <summary>
    /// Our transmission has ended: the transmissions awaiting an answer that have not yet ended (the one
    /// just sent) have their own answer wait run out at <paramref name="deadlineMs"/> (issue #109).
    /// </summary>
    public void OnDataTransmissionEnded(long deadlineMs)
    {
        foreach (Awaited p in Pending)
        {
            p.DeadlineMs ??= deadlineMs;
        }
    }

    /// <summary>
    /// Before a transmission is shaped (issue #109, interp C-1281): each transmission awaiting an answer
    /// whose own answer wait has run out by <paramref name="nowMs"/> with none of its ARQ data acknowledged
    /// is unanswered, oldest first, as at a <c>dts.ack_wait</c> expiry. Returns the failed trials.
    /// </summary>
    public int ExpireAnswerWaits(long nowMs, DrcTable table)
    {
        int failed = 0;
        while (Pending.Count > 0 && Pending[0].DeadlineMs is long d && d <= nowMs)
        {
            Awaited p = Pending[0];
            Pending.RemoveAt(0);
            failed += Unanswered(p, table) ? 1 : 0;
        }

        return failed;
    }

    /// <summary>
    /// The peer answered (C-1250). When our last transmission to it carried ARQ data this says nothing
    /// about the rung (ruling 2026-11-10, interp C-1281): the peer may be answering with its own traffic
    /// without having decoded ours, so only an acknowledgement of that data counts
    /// (<see cref="OnDataAcknowledged"/>). When it carried none and no ARQ data awaits its
    /// acknowledgement, the rule for such a transmission stands: the silence count and the step down end, and its
    /// rung counts as answered when it is above the highest answered so far.
    /// </summary>
    public void OnAnswered()
    {
        if (LastSentData || DataAwaited)
        {
            return;
        }

        Proven(LastSentRung);
    }

    /// <summary>
    /// The peer acknowledged a D_PDU last placed in build number <paramref name="transmission"/> (ruling
    /// 2026-11-10, interp C-1281). When that is the transmission whose acknowledgement is awaited, it is
    /// answered: the silence count and the step down end, a trial on it has succeeded (its hold-off goes
    /// back to the initial length), and its rung counts as answered when it is above the highest answered
    /// so far. An acknowledgement of an older transmission's D_PDU proves nothing about the last one.
    /// </summary>
    public void OnDataAcknowledged(long transmission)
    {
        int i = Pending.FindIndex(p => p.Transmission == transmission);
        if (i < 0)
        {
            return;
        }

        Awaited answered = Pending[i];
        Pending.RemoveRange(0, i + 1);   // the earlier ones are superseded by the later answer
        Proven(answered.Rung, answered.Trial);
    }

    private void Proven(int? rung, bool trial = false)
    {
        SilentWaits = 0;
        StepDown = 0;
        FailedSinceAnswer = false;
        if (trial)
        {
            HoldOff = 0;
        }

        if (rung is int sent)
        {
            AnsweredRung = AnsweredRung is int answered ? Math.Max(answered, sent) : sent;
        }
    }

    /// <summary>
    /// <c>dts.ack_wait</c> expired for this peer and the connection was not lost. With the ARQ data of the
    /// last data transmission still unacknowledged (open question 16; ruling 2026-11-10, whatever else the
    /// peer has sent meanwhile) it is a silent wait: a trial in flight has failed and caps the peer below it (design shape item 7); otherwise the rung of the
    /// unanswered data no longer counts as answered, so the answered rung goes below it and going back
    /// to it is a trial again (interp C-1281); and every <c>tx_step_down_waits</c> silent waits in a
    /// row step one rung down (plan 6.2). Returns true when a failed trial set a ceiling.
    /// </summary>
    public bool OnAckWaitExpired(DrcTable table)
    {
        bool failed = false;
        while (Pending.Count > 0)
        {
            Awaited p = Pending[0];
            Pending.RemoveAt(0);
            failed |= Unanswered(p, table);
        }

        return failed;
    }

    /// <summary>
    /// An ACK-ONLY or DATA-ACK from the peer acknowledged none of the D_PDUs last placed in build number
    /// <paramref name="transmission"/> and marked every one of them still awaiting an acknowledgement
    /// failed (C-0908, C-0910, interp C-1121), so <c>dts.ack_wait</c> may stop without an expiry. When that
    /// is the transmission whose acknowledgement is awaited it is unanswered, as at an expiry (reviewer
    /// decision on ruling 2026-11-10 (g), interp C-1281): the peer has reported its data lost.
    /// </summary>
    public bool OnDataAllFailed(long transmission, DrcTable table)
    {
        int i = Pending.FindIndex(p => p.Transmission == transmission);
        if (i < 0)
        {
            return false;
        }

        Awaited p = Pending[i];
        Pending.RemoveRange(0, i + 1);
        return Unanswered(p, table);
    }

    private bool Unanswered(Awaited p, DrcTable table)
    {
        bool failedTrial = false;

        if (p.Trial && p.Rung is int failed)
        {
            int cap = DrcTable.Need(table.TxCeilingCap, "tx_ceiling_cap");
            int length = Math.Min(HoldOff > 0 ? HoldOff : DrcTable.Need(table.TxCeilingInitial, "tx_ceiling_initial"), cap);
            Ceiling = failed - 1;
            CeilingLeft = length;
            HoldOff = (int)Math.Min(length * 2L, cap);
            FailedSinceAnswer = true;
            failedTrial = true;
        }
        else if (AnsweredRung is int answered && p.Rung <= answered)
        {
            AnsweredRung = Math.Max(0, p.Rung - 1);   // never below the lowest rung, so a trial is always above a rung
        }

        SilentWaits++;
        if (SilentWaits % Math.Max(1, DrcTable.Need(table.TxStepDownWaits, "tx_step_down_waits")) == 0)
        {
            StepDown++;
        }

        return failedTrial;
    }
}

/// <summary>
/// The sender's waveform choice (core-architecture.md 5.3 <c>ChooseWaveform</c>; 9, phase 7, P7-7):
/// pure functions of the table, the received EOWs and the sender state.
/// </summary>
internal static class RateChoice
{
    /// <summary>
    /// The user data rate in bit/s of a 3 kHz waveform number (M0LTE.Ms110d <c>docs/waveforms.md</c>, from
    /// MIL-STD-188-110D Table D-I). WN 9 to 12 are not in our modem; only their order above WN 8 matters.
    /// </summary>
    public static int DataRate(int wn) => wn switch
    {
        0 => 75,
        1 => 150,
        2 => 300,
        3 => 600,
        4 => 1200,
        5 => 1600,
        6 => 3200,
        7 => 4800,
        8 => 6400,
        9 => 8000,
        10 => 9600,
        11 => 12000,
        12 => 16000,
        13 => 2400,
        _ => throw new ArgumentOutOfRangeException(nameof(wn)),
    };

    /// <summary>
    /// A recommended rung on our ladder (open question 5; interp C-1280): the rung with exactly the
    /// recommended waveform and interleaver when it is on the ladder; otherwise the highest ladder rung
    /// whose data rate is not above the recommended one and whose interleaver is not shorter; failing
    /// that (a very long interleaver), the highest whose data rate is not above it; failing that, the
    /// lowest rung, the slowest we have.
    /// </summary>
    public static int Map(DrcTable table, Speed rec)
    {
        IReadOnlyList<Speed> ladder = table.Ladder;
        int exact = table.PositionOf(rec);
        if (exact >= 0)
        {
            return exact;
        }

        int rate = DataRate(rec.Wn);
        for (int i = ladder.Count - 1; i >= 0; i--)
        {
            if (DataRate(ladder[i].Wn) <= rate && ladder[i].Interleaver >= rec.Interleaver)
            {
                return i;
            }
        }

        for (int i = ladder.Count - 1; i >= 0; i--)
        {
            if (DataRate(ladder[i].Wn) <= rate)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// The rung a MAX-SPEED or LOW-SPEED value names, or null when it names none we can use: a
    /// narrowband speed (17 to 30), since we send only at 3 kHz (C-0933; interp C-1280). Interleaver 000
    /// means no interleaver recommendation, so the interleaver of <c>drc.default_speed</c> (open question 5).
    /// </summary>
    public static Speed? Named(SpeedInterleaver v, Speed defaultSpeed)
    {
        if (v.Speed is < 1 or > 13)
        {
            return null;
        }

        Interleaver il = v.Interleaver == 0 ? defaultSpeed.Interleaver : (Interleaver)(v.Interleaver - 1);
        return new Speed(v.Speed, il);
    }

    /// <summary>
    /// The waveform an ED3-BASIC-RATE names (open question 10; C-0959, C-0960): the fastest of our
    /// waveforms (WN 0 to 8 and 13) whose data rate is not above the Table C-5 rate; Table C-6 no
    /// interleaving as Ultra Short, short as Short, long as Long. Reserved interleaving 11 is never
    /// recorded (interp C-0471).
    /// </summary>
    public static Speed Ed3Named(Ed3BasicRateEow e)
    {
        int rate = e.DataRate switch
        {
            0 => 75, 1 => 150, 2 => 300, 3 => 600, 4 => 1200, 5 => 2400, 6 => 3200, 7 => 3600,
            8 => 4800, 9 => 6400, 10 => 8000, 11 => 9600, 12 => 14400, 13 => 16000, 14 => 19200, _ => 12800,
        };
        int best = 0;
        foreach (int wn in (int[])[1, 2, 3, 4, 5, 6, 7, 8, 13])
        {
            if (DataRate(wn) <= rate && DataRate(wn) > DataRate(best))
            {
                best = wn;
            }
        }

        Interleaver il = e.Interleaving switch { 0 => Interleaver.UltraShort, 1 => Interleaver.Short, _ => Interleaver.Long };
        return new Speed(best, il);
    }
}
