using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// P7-7: the sender half of rate selection below the corpus: the rung mapping of open question 5
/// (interp C-1280), the ED3-BASIC-RATE mapping of open question 10 (interp C-1288), and the sender
/// state's counters (interp C-1281, C-1282). End-to-end behaviour is in the drc/tx-* scenarios.
/// </summary>
public class SenderRateTests
{
    private static readonly Speed W2L = new(2, Interleaver.Long);
    private static readonly Speed W4L = new(4, Interleaver.Long);
    private static readonly Speed W4S = new(4, Interleaver.Short);
    private static readonly Speed W6L = new(6, Interleaver.Long);

    private static readonly DrcTable Table = new([W2L, W4L, W4S, W6L], [], TxStepDownWaits: 2, TxCeilingInitial: 2, TxCeilingCap: 5);

    [Fact]
    public void A_rung_on_the_ladder_maps_to_itself_and_one_off_it_to_the_highest_not_faster_and_not_shorter()
    {
        RateChoice.Map(Table, W4S).Should().Be(2);
        RateChoice.Map(Table, new Speed(5, Interleaver.Short)).Should().Be(2);    // 1600: WN 4 Short
        RateChoice.Map(Table, new Speed(5, Interleaver.Medium)).Should().Be(1);   // Short is shorter than Medium: WN 4 Long
        RateChoice.Map(Table, new Speed(13, Interleaver.Long)).Should().Be(1);    // WN 13 is 2400 bit/s, below WN 6
        RateChoice.Map(Table, new Speed(8, Interleaver.UltraShort)).Should().Be(3);
        RateChoice.Map(Table, new Speed(6, Interleaver.VeryLong)).Should().Be(3);  // nothing as long: the interleaver is dropped
        RateChoice.Map(Table, new Speed(1, Interleaver.Long)).Should().Be(0);      // nothing as slow: the lowest rung
    }

    [Fact]
    public void Interleaver_000_takes_the_default_interleaver_and_narrowband_speeds_name_no_rung()
    {
        RateChoice.Named(new SpeedInterleaver(0, 6), W2L).Should().Be(new Speed(6, Interleaver.Long));
        RateChoice.Named(new SpeedInterleaver(3, 4), W2L).Should().Be(W4S);
        RateChoice.Named(new SpeedInterleaver(5, 20), W2L).Should().BeNull();
    }

    [Theory]
    [InlineData(0, 0, 0, Interleaver.UltraShort)]   // 75 bps, no interleaving: WN 0 (open question 11)
    [InlineData(1, 1, 1, Interleaver.Short)]
    [InlineData(4, 2, 4, Interleaver.Long)]
    [InlineData(5, 1, 13, Interleaver.Short)]       // 2400: WN 13, not WN 5
    [InlineData(7, 2, 6, Interleaver.Long)]         // 3600: WN 6 (3200)
    [InlineData(13, 2, 8, Interleaver.Long)]        // 16000: WN 8, our fastest
    public void Ed3_basic_rate_names_the_fastest_waveform_not_above_its_rate(int code, int interleaving, int wn, Interleaver il) =>
        RateChoice.Ed3Named(new Ed3BasicRateEow(code, interleaving, 2)).Should().Be(new Speed(wn, il));

    [Fact]
    public void An_answer_to_a_lower_rung_keeps_the_answered_rung_and_a_silent_wait_lowers_it()
    {
        // Interp C-1281 as revised in corpus 0.35.0 (P7-9 round 2).
        var s = new SenderRate();
        s.OnConnectionMade(0);
        s.OnSent(1, true, 1);
        s.OnDataAcknowledged(1);
        s.OnSent(2, true, 2);
        s.OnDataAcknowledged(2);
        s.AnsweredRung.Should().Be(2);
        s.OnSent(0, true, 3);                 // a short transmission at the LOW-SPEED rung
        s.OnDataAcknowledged(3);
        s.AnsweredRung.Should().Be(2);     // before: 0, and the next bulk transmission could go no higher than 1
        s.OnSent(2, true, 4);
        s.TrialRung.Should().BeNull();
        s.OnAckWaitExpired(Table).Should().BeFalse("silent, not a trial, so no ceiling");   // rung 2 is no longer answered
        (s.AnsweredRung, s.Ceiling).Should().Be((1, null));
        s.OnSent(2, true, 5);
        s.TrialRung.Should().Be(2);        // going back to it is a trial
        s.OnAckWaitExpired(Table).Should().BeTrue();
        (s.Ceiling, s.AnsweredRung).Should().Be((1, 1));
        var low = new SenderRate();
        low.OnConnectionMade(0);
        low.OnSent(0, true, 6);
        low.OnAckWaitExpired(Table);
        low.AnsweredRung.Should().Be(0);   // never below the lowest rung
    }

    [Fact]
    public void A_failed_trial_caps_below_it_and_the_cap_doubles_up_to_the_limit()
    {
        var s = new SenderRate();
        s.OnConnectionMade(0);
        s.OnSent(1, true, 7);
        s.TrialRung.Should().Be(1);
        s.OnAckWaitExpired(Table).Should().BeTrue("a failed trial sets a ceiling (the stack sweep counts them, open question 14)");
        (s.Ceiling, s.CeilingLeft, s.HoldOff).Should().Be((0, 2, 4));
        s.OnSent(0, true, 8);
        s.OnSent(0, true, 9);
        s.Ceiling.Should().BeNull();
        s.OnDataAcknowledged(9);
        s.AnsweredRung.Should().Be(0);
        s.OnSent(1, true, 10);
        s.OnAckWaitExpired(Table);
        (s.Ceiling, s.CeilingLeft, s.HoldOff).Should().Be((0, 4, 5));
        for (int i = 0; i < 4; i++)
        {
            s.OnSent(0, true, 11);
        }

        s.OnSent(1, true, 12);
        s.OnAckWaitExpired(Table);
        (s.CeilingLeft, s.HoldOff).Should().Be((5, 5));
    }

    [Fact]
    public void A_successful_trial_resets_the_hold_off_and_an_answer_ends_the_step_down()
    {
        var s = new SenderRate();
        s.OnConnectionMade(0);
        s.OnSent(1, true, 13);
        s.OnAckWaitExpired(Table);
        s.HoldOff.Should().Be(4);
        s.OnSent(0, true, 14);
        s.OnDataAcknowledged(14);
        s.OnSent(0, true, 15);
        s.OnSent(1, true, 16);
        s.OnDataAcknowledged(16);
        (s.HoldOff, s.AnsweredRung, s.TrialRung).Should().Be((0, 1, (int?)null));

        s.OnSent(1, true, 17);
        s.OnAckWaitExpired(Table);
        s.StepDown.Should().Be(0);
        s.OnSent(1, true, 18);
        s.OnAckWaitExpired(Table);
        s.StepDown.Should().Be(1);
        s.OnDataAcknowledged(18);          // after the expiry: too late, it proves nothing
        s.StepDown.Should().Be(1);
        s.OnSent(0, true, 30);
        s.OnAnswered();                    // the peer's own traffic, no acknowledgement: nothing changes
        s.StepDown.Should().Be(1);
        s.OnDataAcknowledged(30);
        (s.StepDown, s.SilentWaits).Should().Be((0, 0));
    }

    [Fact]
    public void An_expiry_after_an_answer_is_not_silent_and_non_arq_sends_are_never_trials()
    {
        var s = new SenderRate();
        s.OnConnectionMade(0);
        s.OnSent(1, true, 19);
        s.OnDataAcknowledged(19);
        s.OnAckWaitExpired(Table);   // the answer came after the transmission: not a silent wait
        (s.SilentWaits, s.Ceiling).Should().Be((0, (int?)null));
        s.OnSent(3, false, 20);
        s.TrialRung.Should().BeNull();
        s.OnConnectionEnded();
        s.AnsweredRung.Should().BeNull();
    }

    [Fact]
    public void A_ceiling_and_its_hold_off_outlive_the_connection_and_silence_steps_down_once_per_run()
    {
        var s = new SenderRate();
        s.OnConnectionMade(0);
        s.OnSent(1, true, 21);
        s.OnAckWaitExpired(Table);   // a failed trial: ceiling 0 for 2 transmissions, next length 4
        s.OnConnectionEnded();
        s.OnConnectionMade(0);
        (s.Ceiling, s.CeilingLeft, s.HoldOff).Should().Be((0, 2, 4));   // interp C-1281: a property of the path
        (s.SilentWaits, s.StepDown, s.TrialRung).Should().Be((0, 0, (int?)null));

        for (int i = 1; i <= 4; i++)
        {
            s.OnSent(0, true, 22);
            s.OnAckWaitExpired(Table);
            s.StepDown.Should().Be(i / 2);   // every tx_step_down_waits silent waits, one more rung (interp C-1282)
        }
    }

    [Fact]
    public void After_a_failed_trial_nothing_goes_above_the_answered_rung_until_an_answer()
    {
        // Interp C-1281 as revised in corpus 0.36.0 (P7-9 round 3): no climb limit, but a failed trial is
        // followed by transmissions at or below the answered rung until the peer answers, so failed trials
        // never come two in a row.
        var s = new SenderRate();
        s.OnConnectionMade(0);
        s.OnSent(3, true, 23);                    // straight to position 3: a trial
        s.TrialRung.Should().Be(3);
        s.OnAckWaitExpired(Table);
        (s.Ceiling, s.FailedSinceAnswer).Should().Be((2, true));
        s.OnSent(0, true, 24);                    // the planner keeps it at the answered rung: not a trial
        s.TrialRung.Should().BeNull();
        s.OnAckWaitExpired(Table);            // silent again, but not a failed trial: the ceiling stays
        (s.Ceiling, s.FailedSinceAnswer).Should().Be((2, true));
        s.OnAnswered();                       // heard, but the last transmission carried unacknowledged data
        s.FailedSinceAnswer.Should().BeTrue();
        s.OnSent(0, true, 25);
        s.OnDataAcknowledged(25);
        s.FailedSinceAnswer.Should().BeFalse();
        s.OnConnectionMade(0);
        s.FailedSinceAnswer.Should().BeFalse();
    }

    [Fact]
    public void Only_an_acknowledgement_of_its_data_answers_a_transmission()
    {
        // Coordinator ruling 2026-11-10 (interp C-1281 as revised in corpus 0.38.0, P7-9e): the peer's own
        // traffic while our data goes unacknowledged is no answer, so the trial fails at the expiry.
        var s = new SenderRate();
        s.OnConnectionMade(0);
        s.OnSent(2, true, 1);                 // a trial
        s.OnAnswered();                       // the peer's own DATA-ONLY, acknowledging nothing of ours
        s.OnSent(2, false, 2);                // our ACK-ONLY for it, no ARQ data
        s.OnAnswered();                       // still nothing acknowledges the trial's data
        (s.AnsweredRung, s.TrialRung).Should().Be((0, 2));
        s.OnDataAcknowledged(0);              // an older transmission's D_PDU: not this one
        s.OnAckWaitExpired(Table);
        (s.Ceiling, s.FailedSinceAnswer, s.SilentWaits).Should().Be((1, true, 1));

        s.OnSent(0, true, 3);
        s.OnDataAcknowledged(3);              // acknowledged: answered
        (s.AnsweredRung, s.FailedSinceAnswer, s.SilentWaits).Should().Be((0, false, 0));
        s.OnSent(1, false, 4);                // no ARQ data: any answer still proves its rung
        s.OnAnswered();
        s.AnsweredRung.Should().Be(1);
        s.OnAckWaitExpired(Table);            // nothing awaited: not a silent wait
        s.SilentWaits.Should().Be(0);
    }

    [Fact]
    public void An_acknowledgement_that_fails_all_of_the_awaited_data_is_no_answer()
    {
        // Reviewer decision on ruling 2026-11-10 (g), interp C-1281: an ACK marking every D_PDU of the awaited
        // transmission failed (C-0908, C-0910) stops dts.ack_wait without an expiry, so it counts as one.
        var s = new SenderRate();
        s.OnConnectionMade(0);
        s.OnSent(2, true, 1);                 // a trial
        s.OnDataAllFailed(0, Table);          // an older transmission: nothing
        s.TrialRung.Should().Be(2);
        s.OnDataAllFailed(1, Table);          // the trial's data reported lost: a failed trial
        (s.Ceiling, s.FailedSinceAnswer, s.SilentWaits, s.TrialRung).Should().Be((1, true, 1, (int?)null));
        s.OnAckWaitExpired(Table);            // nothing awaited any more: not counted twice
        s.SilentWaits.Should().Be(1);

        s.OnSent(0, true, 2);
        s.OnDataAcknowledged(2);
        s.OnDataAllFailed(2, Table);          // already answered: nothing
        (s.FailedSinceAnswer, s.SilentWaits).Should().Be((false, 0));
    }
}
