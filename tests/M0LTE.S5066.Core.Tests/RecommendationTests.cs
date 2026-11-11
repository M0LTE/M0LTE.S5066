using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// P7-6: the receiver recommendation arithmetic of corpus-format.md 6.2 (interp C-1270 to C-1273)
/// and the drc.table reader (corpus-format.md 6.1). End-to-end behaviour is in the drc corpus
/// scenarios; these cover the branches a scenario does not reach.
/// </summary>
public class RecommendationTests
{
    private static Speed R(int wn, Interleaver il = Interleaver.Long) => new(wn, il);

    private static readonly DrcTable Table = new(
        [R(2), R(4), R(6)],
        [
            new DrcThresholds(R(2), [-100, 50, 150], [-100, 100, 200]),
            new DrcThresholds(R(4), [-100, 0, 100], [-100, 100, 300]),
            new DrcThresholds(R(6), [-100, 0, 300], [-100, 200, 400]),
        ],
        SmoothingWeight: 8, CleanRun: 2, RxErrorLimitPpt: 500, RxCeilingInitial: 2, RxCeilingCap: 5);

    [Theory]
    [InlineData(null, 60, 8, 60)]
    [InlineData(60, 100, 8, 80)]
    [InlineData(80, -40, 8, 20)]
    [InlineData(10, 0, 1, 10)]      // 10 / 16 truncates to 0
    [InlineData(10, -30, 1, 8)]     // -40: 40 / 16 = 2, taken negative
    [InlineData(-7, 0, 3, -6)]      // 7 x 3 / 16 = 1
    [InlineData(5, 250, 16, 250)]   // no smoothing
    public void Smooth_truncates_towards_zero_for_both_signs(int? s, int x, int w, int expected)
    {
        // Interp C-1270; core-architecture.md 6 rule 1 (sign tested before division).
        Recommender.Smooth(s, x, w).Should().Be(expected);
    }

    [Fact]
    public void Nothing_is_kept_with_an_empty_table()
    {
        var st = new RecommendationState();
        Recommender.OnBurst(DrcTable.Empty, st, R(6), 250, 1, 0, false);
        st.Heard.Should().BeFalse();
        st.MaxSpeed.Should().BeNull();
        Recommender.Wanted(DrcTable.Empty, st, null).Top.Should().BeEmpty();
        Recommender.Ed3(DrcTable.Empty, st).Should().BeNull();
    }

    [Fact]
    public void A_burst_without_an_estimate_gives_max_segment_alone()
    {
        // Interp C-1271: entries with no value are left out; MAX-SEGMENT 255 to a variable-speed sender (C-1272).
        var st = new RecommendationState();
        Recommender.OnBurst(Table, st, R(6), null, 1, 0, false);
        st.Heard.Should().BeTrue();
        (IReadOnlyList<Eow> top, IReadOnlyList<Eow> set) = Recommender.Wanted(Table, st, null);
        top.Should().BeEmpty();
        set.Should().Equal(new MaxSegmentEow(255));
    }

    [Fact]
    public void An_off_ladder_rung_with_thresholds_starts_at_the_target()
    {
        // Interp C-1270: the first recommendation is the target, capped by the rung received only
        // when that rung is on the ladder.
        DrcTable t = Table with { Thresholds = [.. Table.Thresholds, new DrcThresholds(R(3), [-100, 0, 100], [-100, 0, 100])] };
        var st = new RecommendationState();
        Recommender.OnBurst(t, st, R(3), 150, 1, 0, false);
        st.MaxSpeed.Should().Be(2);
        st.LowSpeed.Should().Be(2);
    }

    [Fact]
    public void A_rung_with_no_thresholds_gives_no_estimate()
    {
        var st = new RecommendationState();
        Recommender.OnBurst(Table, st, R(5), 300, 1, 0, false);
        st.MaxSpeed.Should().BeNull();
        st.Smoothed.Should().BeEmpty();
    }

    [Fact]
    public void Below_every_threshold_recommends_the_lowest_rung()
    {
        var st = new RecommendationState();
        Recommender.OnBurst(Table, st, R(6), -200, 1, 0, false);
        st.MaxSpeed.Should().Be(0);
        st.LowSpeed.Should().Be(0);
    }

    [Fact]
    public void An_estimate_drop_moves_down_at_once_and_more_than_one_rung()
    {
        var st = new RecommendationState();
        Recommender.OnBurst(Table, st, R(6), 350, 1, 0, false);
        st.MaxSpeed.Should().Be(2);
        Recommender.OnBurst(Table, st, R(6), -400, 1, 0, false);   // S = 350 - 375 = -25: below WN 4's 0
        st.MaxSpeed.Should().Be(0);
    }

    [Fact]
    public void A_cap_doubles_up_to_the_table_cap_and_a_clean_burst_on_the_rung_clears_the_count()
    {
        // Design shape item 7; interp C-1270. Caps 2, 4, then 5 (the cap), not 8.
        var st = new RecommendationState();
        Recommender.OnBurst(Table, st, R(4), null, 1, 1, false);
        st.Caps[1].Should().Be(2);
        Recommender.OnBurst(Table, st, R(4), null, 2, 2, false);
        st.Caps[1].Should().Be(4);
        Recommender.OnBurst(Table, st, R(4), null, 4, 3, false);
        st.Caps[1].Should().Be(5);
        Recommender.OnBurst(Table, st, R(4), null, 4, 0, false);   // clean on the rung
        st.Failures.Should().NotContainKey(1);
        st.Caps[1].Should().Be(4);   // the cap itself runs on
    }

    [Fact]
    public void Exactly_the_limit_is_not_over_it()
    {
        // 1 error in 2 headers is 500 ppt, not above 500.
        var st = new RecommendationState();
        Recommender.OnBurst(Table, st, R(4), null, 2, 1, false);
        st.Caps.Should().BeEmpty();
        st.CleanRun[RecommendationState.Key(R(4))].Should().Be(0);
    }

    [Fact]
    public void A_capped_lowest_rung_keeps_the_lowest_rung()
    {
        var st = new RecommendationState();
        Recommender.OnBurst(Table, st, R(2), 400, 1, 0, false);
        Recommender.OnBurst(Table, st, R(2), null, 1, 1, false);
        st.MaxSpeed.Should().Be(0);
    }

    [Fact]
    public void Max_segment_to_a_fixed_sender_halves_to_25_and_doubles_to_255()
    {
        // Interp C-1272.
        var st = new RecommendationState();
        for (int i = 0; i < 5; i++)
        {
            Recommender.OnBurst(Table, st, R(2), null, 2, 2, true);
        }

        st.Segment.Should().Be(25);   // 127, 63, 31, then 25, not 15
        for (int i = 0; i < 2; i++)
        {
            Recommender.OnBurst(Table, st, R(2), null, 1, 0, true);
        }

        st.Segment.Should().Be(50);
        for (int i = 0; i < 8; i++)
        {
            Recommender.OnBurst(Table, st, R(2), null, 1, 0, true);
        }

        st.Segment.Should().Be(255);
        Recommender.Wanted(Table, st, new SenderApproachEow(false, 0, true)).Set.Should().Equal(new MaxSegmentEow(255));
    }

    [Fact]
    public void Table_c25_orders_bulk_and_intermediate()
    {
        // C-0951, C-0954; open question 1.
        var st = new RecommendationState();
        Recommender.OnBurst(Table, st, R(6), 150, 1, 0, false);   // MAX WN 4, LOW WN 2
        var max = new MaxSpeedEow(new SpeedInterleaver(5, 4));
        var low = new LowSpeedEow(new SpeedInterleaver(5, 2));
        var seg = new MaxSegmentEow(255);
        (IReadOnlyList<Eow> top, IReadOnlyList<Eow> set) = Recommender.Wanted(Table, st, new SenderApproachEow(false, 0, false));
        top.Should().Equal(max);
        set.Should().Equal(max, max, seg);
        (top, set) = Recommender.Wanted(Table, st, new SenderApproachEow(true, 3, false));
        top.Should().Equal(max);
        set.Should().Equal(max, low, max, seg);
    }

    [Fact]
    public void Ed3_basic_rate_codes_each_rung_by_our_table()
    {
        // Interp C-1273 (C-0442), C-0440.
        Recommender.Ed3BasicRate(R(1, Interleaver.UltraShort)).Should().Be(new Ed3BasicRateEow(1, 1, 2));
        Recommender.Ed3BasicRate(R(5, Interleaver.Medium)).Should().Be(new Ed3BasicRateEow(4, 2, 2));
        Recommender.Ed3BasicRate(R(13, Interleaver.Short)).Should().Be(new Ed3BasicRateEow(5, 1, 2));
        Recommender.Ed3BasicRate(R(8)).Should().Be(new Ed3BasicRateEow(9, 2, 2));
        FluentActions.Invoking(() => Recommender.Ed3BasicRate(R(9))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_reader_takes_a_table_and_refuses_a_bad_one()
    {
        JsonObject Config(string table) => T2.ConfigMerge.Merge(Corpus.T2Defaults, JsonNode.Parse(
            "{\"local_address\": \"0.0.1.2\", \"drc\": {\"table\": " + table + "}}")!.AsObject());

        DrcTable t = ConfigReader.Read(Config("""
            {"ladder": [{"wn": 2, "interleaver": "long"}, {"wn": 13, "interleaver": "ultra_short"}],
             "thresholds": [{"wn": 0, "interleaver": "long", "bulk": [-5, null], "latency": [null, 7]}],
             "smoothing_weight": 4, "clean_run": 3, "rx_error_limit_ppt": 0, "tx_ceiling_cap": 9}
            """)).Drc.Table;
        t.Ladder.Should().Equal(R(2), R(13, Interleaver.UltraShort));
        t.ThresholdsOn(R(0))!.Bulk.Should().Equal(-5, null);
        t.SmoothingWeight.Should().Be(4);
        t.RxErrorLimitPpt.Should().Be(0);
        t.TxCeilingCap.Should().Be(9);
        t.RxCeilingInitial.Should().BeNull();
        ConfigReader.Read(Config("{}")).Drc.Table.IsEmpty.Should().BeTrue();

        foreach (string bad in new[]
        {
            """{"ladder": [{"wn": 9, "interleaver": "long"}]}""",
            """{"ladder": [{"wn": 2, "interleaver": "very_long"}]}""",
            """{"ladder": [{"wn": 2, "interleaver": "long"}, {"wn": 2, "interleaver": "long"}]}""",
            """{"ladder": [{"wn": 2, "interleaver": "long"}], "thresholds": [{"wn": 2, "interleaver": "long", "bulk": [1, 2], "latency": [1]}]}""",
            """{"smoothing_weight": 17}""",
            """{"smoothing_weight": 0}""",
            """{"rx_error_limit_ppt": 1001}""",
            """{"clean_run": 0}""",
            """{"rx_ceiling_initial": 4, "rx_ceiling_cap": 3}""",
            """{"tx_ceiling_initial": 4, "tx_ceiling_cap": 3}""",
        })
        {
            FluentActions.Invoking(() => ConfigReader.Read(Config(bad))).Should().Throw<ConfigException>(bad);
        }
    }

    [Fact]
    public void A_missing_constant_the_core_needs_is_a_missing_setting()
    {
        DrcTable t = Table with { RxErrorLimitPpt = null };
        FluentActions.Invoking(() => Recommender.OnBurst(t, new RecommendationState(), R(4), 100, 1, 0, false))
            .Should().Throw<MissingSettingException>().Which.Message.Should().Contain("drc.table.rx_error_limit_ppt");
    }

    [Fact]
    public void An_ed3_peer_gets_ed3_basic_rate_in_every_header_and_an_owed_eow_first()
    {
        // C-0957, interp C-1273, C-1079; never to an Ed4 peer (C-0958).
        var p = new PeerTable();
        p.SetEdition(0x103, KnownEdition.Ed3);
        p.SetEdition(0x104, KnownEdition.Ed4);
        var s = new EowScheduler(p) { Ed3Recommendation = _ => new Ed3BasicRateEow(4, 2, 2) };
        s.Owe(0x103, 0x307);
        s.BeginTransmission([]);
        s.Next(0x103, false).Should().Be(0x307);
        s.Next(0x103, false).Should().Be(0x14A);
        s.Next(0x103, false).Should().Be(0x14A);
        s.Next(0x104, false).Should().Be(0xF01);
        s.Next(0x103, true).Should().Be(0);
    }

    [Fact]
    public void The_probe_stops_at_the_top_rung_and_holds_without_an_estimate()
    {
        // Interp C-1291: the probe needs a rung above, and an estimate on a rung with thresholds.
        DrcTable t = Table with { ProbeRun = 1 };
        var st = new RecommendationState();
        Recommender.OnBurst(t, st, R(6), 400, 1, 0, false);   // first: bulk target 2 (300), MAX-SPEED WN 6, the top
        st.MaxSpeed.Should().Be(2);
        Recommender.OnBurst(t, st, R(6), 400, 1, 0, false);
        st.MaxSpeed.Should().Be(2);

        st = new RecommendationState();
        Recommender.OnBurst(t, st, R(4), 50, 1, 0, false);    // bulk target 1 (0; 100 is above): MAX-SPEED WN 4
        st.MaxSpeed.Should().Be(1);
        Recommender.OnBurst(t, st, R(4), null, 1, 0, false);  // no estimate: only the ceiling applies
        st.MaxSpeed.Should().Be(1);
        Recommender.OnBurst(t, st, R(4), 50, 1, 0, false);    // clean run 3 on WN 4: probe to WN 6
        st.MaxSpeed.Should().Be(2);
        st.LowSpeed.Should().Be(0);                           // LOW-SPEED is never probed
    }

    [Fact]
    public void The_probe_is_held_until_the_link_has_a_complete_cpdu_and_the_estimate_still_moves()
    {
        // Coordinator ruling 2026-11-11, interp C-1293: no probe up while ProbeHeld; estimate moves are unaffected.
        DrcTable t = Table with { ProbeRun = 1 };
        var st = new RecommendationState { ProbeHeld = true };
        Recommender.OnBurst(t, st, R(4), 50, 1, 0, false);    // first: bulk target 1, MAX-SPEED WN 4
        Recommender.OnBurst(t, st, R(4), 50, 1, 0, false);    // clean run 2: the probe would go to WN 6
        st.MaxSpeed.Should().Be(1);
        Recommender.OnBurst(t, st, R(4), 300, 1, 0, false);   // the estimate reaches WN 6 (bulk 100 on WN 4): moves up, backed by the clean run
        st.MaxSpeed.Should().Be(2);

        st = new RecommendationState { ProbeHeld = true };
        Recommender.OnBurst(t, st, R(4), 50, 1, 0, false);
        Recommender.OnBurst(t, st, R(4), 50, 1, 0, false);
        st.MaxSpeed.Should().Be(1);
        st.ProbeHeld = false;                                  // the link's first C_PDU is through
        Recommender.OnBurst(t, st, R(4), 50, 1, 0, false);
        st.MaxSpeed.Should().Be(2);                            // the probe runs again
    }

    [Fact]
    public void Probe_run_is_read_and_checked()
    {
        var table = JsonNode.Parse("""{"ladder": [], "thresholds": [], "probe_run": 4}""")!.AsObject();
        ConfigReader.ReadDrcTable(table).ProbeRun.Should().Be(4);
        table["probe_run"] = null;
        ConfigReader.ReadDrcTable(table).ProbeRun.Should().BeNull();
        table["probe_run"] = 0;
        FluentActions.Invoking(() => ConfigReader.ReadDrcTable(table)).Should().Throw<ConfigException>();
    }

    [Fact]
    public void The_estimate_moves_straight_to_its_target_after_the_clean_run()
    {
        // Interp C-1270 as revised in corpus 0.36.0 (P7-9 round 3): not one rung.
        var st = new RecommendationState();
        Recommender.OnBurst(Table, st, R(2), 200, 1, 0, false);   // bulk target 2 (150), first not above WN 2: 0
        st.MaxSpeed.Should().Be(0);
        Recommender.OnBurst(Table, st, R(2), 200, 1, 0, false);   // clean run 2 on WN 2: straight to 2
        st.MaxSpeed.Should().Be(2);
    }

    [Fact]
    public void Each_walk_uses_only_its_positions_and_a_ceiling_rounds_down_into_the_walk()
    {
        // Open question 15 (P7-9 round 3): bulk_rungs [0, 2] and latency_rungs [0, 1].
        DrcTable t = Table with { BulkRungs = [0, 2], LatencyRungs = [0, 1], ProbeRun = 1 };
        var st = new RecommendationState();
        Recommender.OnBurst(t, st, R(4), 50, 1, 0, false);       // bulk target 0 on the walk (100 at 2 is above), first not above 1
        (st.MaxSpeed, st.LowSpeed).Should().Be((0, 0));
        Recommender.OnBurst(t, st, R(4), 50, 1, 0, false);       // the probe needs a clean run on WN 2 Long, the rung at 0: none yet
        st.MaxSpeed.Should().Be(0);
        Recommender.OnBurst(t, st, R(2), 50, 1, 0, false);       // clean run on WN 2: the probe goes to the next bulk position, 2
        st.MaxSpeed.Should().Be(2);
        Recommender.OnBurst(t, st, R(6), 400, 1, 1, false);      // WN 6 fails: cap, ceiling 1, rounded down to bulk position 0
        st.MaxSpeed.Should().Be(0);
        st.LowSpeed.Should().Be(0);

        var walk = JsonNode.Parse("""{"ladder": [{"wn": 2, "interleaver": "long"}, {"wn": 4, "interleaver": "long"}], "thresholds": [], "bulk_rungs": [0, 1], "latency_rungs": null}""")!.AsObject();
        ConfigReader.ReadDrcTable(walk).BulkRungs.Should().Equal(0, 1);
        ConfigReader.ReadDrcTable(walk).LatencyRungs.Should().BeNull();
        foreach (string bad in (string[])["[1, 0]", "[0, 2]", "[]", "[-1]", "[0.5]"])
        {
            walk["bulk_rungs"] = JsonNode.Parse(bad);
            FluentActions.Invoking(() => ConfigReader.ReadDrcTable(walk)).Should().Throw<ConfigException>(bad);
        }
    }

    [Fact]
    public void A_burst_with_a_lost_block_is_not_clean()
    {
        // P7-9 round 3 ruling 6 (interp C-1270): no clean-run credit and no failure reset.
        var st = new RecommendationState();
        Recommender.OnBurst(Table, st, R(4), 50, 1, 1, false);   // a failure on WN 4
        st.Failures[1].Should().Be(1);
        Recommender.OnBurst(Table, st, R(4), 50, 0, 0, false, lostBlock: true);
        st.Failures[1].Should().Be(1);
        st.CleanRun[RecommendationState.Key(R(4))].Should().Be(0);
        Recommender.OnBurst(Table, st, R(4), 50, 0, 0, false);
        st.Failures.Should().NotContainKey(1);
        st.CleanRun[RecommendationState.Key(R(4))].Should().Be(1);
    }

    [Fact]
    public void An_errored_burst_moves_max_speed_down_one_walk_position_and_the_caps_still_apply()
    {
        // P7-9 round 4 rulings (interp C-1291 revised, corpus 0.37.0): with the probe, MAX-SPEED moves down only
        // after a burst over rx_error_limit_ppt or with a lost block, and then one walk position, not to the target.
        DrcTable t = Table with { ProbeRun = 1, RxCeilingInitial = 1 };
        var st = new RecommendationState();
        Recommender.OnBurst(t, st, R(6), 400, 1, 0, false);     // first: bulk target 2 (300): MAX-SPEED WN 6
        st.MaxSpeed.Should().Be(2);
        Recommender.OnBurst(t, st, R(6), -500, 10, 1, false);   // 100 ppt, under the limit: S = -50, target 0, but it holds
        st.MaxSpeed.Should().Be(2);
        st.LowSpeed.Should().Be(0);                              // LOW-SPEED, no probe, moves straight down
        st.Caps.Should().BeEmpty();
        Recommender.OnBurst(t, st, R(6), -500, 0, 0, false, lostBlock: true);   // a lost block: one position
        st.MaxSpeed.Should().Be(1);
        Recommender.OnBurst(t, st, R(6), -500, 10, 6, false);   // 600 ppt, over the limit: one more position (cap on 2, ceiling 1)
        st.MaxSpeed.Should().Be(0);

        st = new RecommendationState();
        Recommender.OnBurst(t, st, R(6), 400, 1, 0, false);
        Recommender.OnBurst(t, st, R(4), 50, 1, 1, false);      // WN 4 fails: cap, ceiling 0; the step gives 1, the ceiling 0
        st.MaxSpeed.Should().Be(0);
    }

    [Fact]
    public void Low_speed_moves_up_on_a_clean_run_of_a_higher_received_rung()
    {
        // P7-9 round 4 ruling (b) (interp C-1270 revised, corpus 0.37.0): MAX-SPEED still needs its own rung's run.
        var st = new RecommendationState();
        Recommender.OnBurst(Table, st, R(2), 400, 1, 0, false);   // first: both targets 2, not above WN 2: 0
        (st.MaxSpeed, st.LowSpeed).Should().Be((0, 0));
        Recommender.OnBurst(Table, st, R(4), 400, 1, 0, false);   // clean run 1 on WN 2 and on WN 4: both hold
        (st.MaxSpeed, st.LowSpeed).Should().Be((0, 0));
        Recommender.OnBurst(Table, st, R(4), 400, 1, 0, false);   // WN 4 run 2: LOW-SPEED to its target 2; WN 2 run still 1
        (st.MaxSpeed, st.LowSpeed).Should().Be((0, 2));
        Recommender.OnBurst(Table, st, R(2), 400, 1, 0, false);   // a lower rung's run never backs it: WN 2 run 2 backs MAX-SPEED
        (st.MaxSpeed, st.LowSpeed).Should().Be((2, 2));
    }

    [Fact]
    public void Without_the_probe_a_burst_off_the_ladder_does_not_read_the_error_limit()
    {
        // corpus-format.md 6.2 reads rx_error_limit_ppt only in step 2 (a burst on the ladder) and, since corpus
        // 0.37.0, in the probe's step 6; a table that leaves it null without the probe must not need it here.
        var off = new DrcTable(Table.Ladder, [.. Table.Thresholds, new DrcThresholds(R(1), [-100, 0, 100], [-100, 100, 200])],
            SmoothingWeight: 8, CleanRun: 2, RxErrorLimitPpt: null, RxCeilingInitial: 2, RxCeilingCap: 5);
        var st = new RecommendationState();
        FluentActions.Invoking(() => Recommender.OnBurst(off, st, R(1), 150, 4, 1, false)).Should().NotThrow();
        (st.MaxSpeed, st.LowSpeed).Should().Be((2, 1));
    }
}
