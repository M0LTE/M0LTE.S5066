using M0LTE.S5066.Core.Dts;

namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// The receiver half of rate selection for one peer (core-architecture.md 9, phase 7, P7-6): what
/// this node has measured of the peer's bursts and the recommendations it sends back (C-0459,
/// C-0472, C-0474, C-0935, C-0957). Lives in the peer's DtsPeers entry, so it is created and removed
/// with it (core-architecture.md 6 rule 11). The arithmetic is fixed in corpus-format.md 6.2 (interp
/// C-1270) so the C++ port computes the same values.
/// </summary>
internal sealed class RecommendationState
{
    /// <summary>The largest MAX-SEGMENT value, 255 (1020 bytes; C-0475, interp C-1272).</summary>
    public const int SegmentMax = 255;

    /// <summary>The smallest MAX-SEGMENT value sent to a fixed-speed sender, 25 (100 bytes; interp C-1272).</summary>
    public const int SegmentMin = 25;

    /// <summary>Smoothed estimate per measured rung, tenths of a dB (key <see cref="Key"/>).</summary>
    public SortedDictionary<int, int> Smoothed { get; } = new();

    /// <summary>Clean bursts in a row per rung (key <see cref="Key"/>).</summary>
    public SortedDictionary<int, int> CleanRun { get; } = new();

    /// <summary>Failures in a row per ladder position (bursts over the error limit).</summary>
    public SortedDictionary<int, int> Failures { get; } = new();

    /// <summary>Active caps: ladder position to bursts left.</summary>
    public SortedDictionary<int, int> Caps { get; } = new();

    /// <summary>Whether a burst with a valid header from the peer has been measured with a non-empty table (interp C-1271).</summary>
    public bool Heard { get; set; }

    /// <summary>The MAX-SPEED recommendation, a ladder position, or null.</summary>
    public int? MaxSpeed { get; set; }

    /// <summary>The LOW-SPEED recommendation, a ladder position, or null.</summary>
    public int? LowSpeed { get; set; }

    /// <summary>
    /// An ARQ connection with the peer exists and no complete C_PDU from it has been received on it yet
    /// (coordinator ruling 2026-11-11, issue #109, interp C-1293): the probe of interp C-1291 is held, so
    /// MAX-SPEED does not move up without the estimate until the link's first C_PDU is through. Set at
    /// D_CONNECTION_MADE, cleared by the first complete C_PDU received from the peer and at the
    /// connection's end.
    /// </summary>
    public bool ProbeHeld { get; set; }

    /// <summary>MAX-SEGMENT kept for a fixed-speed sender (interp C-1272).</summary>
    public int Segment { get; set; } = SegmentMax;

    /// <summary>Clean bursts since MAX-SEGMENT last changed, for a fixed-speed sender.</summary>
    public int SegmentClean { get; set; }

    /// <summary>A total order on rungs, for the per-rung tables.</summary>
    public static int Key(Speed rung) => (rung.Wn * 8) + (int)rung.Interleaver;
}

/// <summary>
/// The receiver recommendation logic of corpus-format.md 6.2 (P7-6; interp C-1270 to C-1273). Runs
/// at each <c>burst_ended</c>, once per source heard in the burst, in address order. Integer
/// arithmetic only; divisions are on non-negative operands (core-architecture.md 6 rule 1). Does
/// nothing with an empty table (core-architecture.md 9, phase 7, design shape item 6).
/// </summary>
internal static class Recommender
{
    /// <summary>
    /// The smoothing step (corpus-format.md 6.2): S = x when empty, else S + q where q is |x - S| x w
    /// / 16, truncated, with the sign of x - S (interp C-1270).
    /// </summary>
    public static int Smooth(int? smoothed, int sample, int weight)
    {
        if (smoothed is not int s)
        {
            return sample;
        }

        long d = (long)sample - s;
        long q = ((d < 0 ? -d : d) * weight) / 16;
        return (int)(d < 0 ? s - q : s + q);
    }

    /// <summary>
    /// One burst from the peer (corpus-format.md 6.2 steps 1 to 7): <paramref name="headers"/> valid
    /// D_PDU headers from it, <paramref name="errors"/> of its D_PDUs discarded for a bad data CRC
    /// (C-0833, C-0836), on <paramref name="rung"/> with estimate <paramref name="snr"/> (null when the
    /// modem gives none). <paramref name="lostBlock"/>: a block of the burst held no byte of a D_PDU with a
    /// valid header and was not all zero, so D_PDUs in it lost their headers; such a burst is not clean
    /// even with no data CRC failure (P7-9 round 3, interp C-1270).
    /// </summary>
    public static void OnBurst(DrcTable table, RecommendationState st, Speed rung, int? snr, long headers, long errors, bool fixedSender, bool lostBlock = false)
    {
        if (table.IsEmpty)
        {
            return;
        }

        st.Heard = true;
        bool clean = errors == 0 && !lostBlock;   // round 3: lost headers are not a clean burst

        // 1. Every active cap counts down by one burst from the peer.
        foreach (int p in st.Caps.Keys.ToList())
        {
            if (--st.Caps[p] <= 0)
            {
                st.Caps.Remove(p);
            }
        }

        // 2. A burst over the error limit caps its rung, with doubling backoff (design shape item 7).
        int pos = table.PositionOf(rung);
        if (pos >= 0)
        {
            if (errors * 1000 > (long)DrcTable.Need(table.RxErrorLimitPpt, "rx_error_limit_ppt") * headers)
            {
                int failures = st.Failures.GetValueOrDefault(pos) + 1;
                st.Failures[pos] = failures;
                long length = DrcTable.Need(table.RxCeilingInitial, "rx_ceiling_initial");
                int cap = DrcTable.Need(table.RxCeilingCap, "rx_ceiling_cap");
                for (int i = 1; i < failures && length < cap; i++)
                {
                    length *= 2;
                }

                st.Caps[pos] = (int)Math.Min(length, cap);
            }
            else if (clean)
            {
                st.Failures.Remove(pos);
            }
        }

        // 3. Clean run of the rung.
        int key = RecommendationState.Key(rung);
        st.CleanRun[key] = clean ? st.CleanRun.GetValueOrDefault(key) + 1 : 0;

        // 4. Smoothing, on a rung the table has thresholds for.
        DrcThresholds? thresholds = table.ThresholdsOn(rung);
        int? smoothed = null;
        if (thresholds is not null && snr is int x)
        {
            int? old = st.Smoothed.TryGetValue(key, out int s) ? s : null;
            smoothed = Smooth(old, x, DrcTable.Need(table.SmoothingWeight, "smoothing_weight"));
            st.Smoothed[key] = smoothed.Value;
        }

        // 5. The ceiling from the active caps.
        int? ceiling = null;
        foreach (int p in st.Caps.Keys)
        {
            int c = Math.Max(0, p - 1);
            ceiling = ceiling is int cc ? Math.Min(cc, c) : c;
        }

        // 6. The walk, for MAX-SPEED (bulk) and LOW-SPEED (latency). The probe of interp C-1291 is for
        // MAX-SPEED only, and only with probe_run set.
        int? probeRun = table.ProbeRun;
        // P7-9 round 4 (interp C-1291): with the probe, only a burst over the error limit or with a lost block
        // moves MAX-SPEED down; a few corrupted D_PDUs in a burst whose every block is accounted for hold it.
        // Read only with the probe on: without it 6.2 reads rx_error_limit_ppt only for a burst on the ladder (step 2).
        bool downTrigger = probeRun is not null
            && (lostBlock || errors * 1000 > (long)DrcTable.Need(table.RxErrorLimitPpt, "rx_error_limit_ppt") * headers);
        st.MaxSpeed = Walk(table, st, st.MaxSpeed, table.WalkOf(bulk: true), thresholds?.Bulk, smoothed, pos, ceiling, probeRun, downTrigger, bulk: true);
        st.LowSpeed = Walk(table, st, st.LowSpeed, table.WalkOf(bulk: false), thresholds?.Latency, smoothed, pos, ceiling, null, downTrigger, bulk: false);

        // 7. MAX-SEGMENT to a fixed-speed sender (interp C-1272).
        if (fixedSender)
        {
            if (errors * 2 > headers)
            {
                st.Segment = Math.Max(RecommendationState.SegmentMin, st.Segment / 2);
                st.SegmentClean = 0;
            }
            else if (clean && ++st.SegmentClean >= DrcTable.Need(table.CleanRun, "clean_run"))
            {
                st.Segment = Math.Min(RecommendationState.SegmentMax, st.Segment * 2);
                st.SegmentClean = 0;
            }
        }
    }

    /// <summary>
    /// Step 6 of corpus-format.md 6.2 for one recommendation, over the ladder positions <paramref name="walk"/>
    /// of its criterion (bulk_rungs or latency_rungs; open question 15). The target is the highest of them the
    /// estimate reaches; a move down to it is immediate, and a move up goes straight to it after the clean run
    /// (P7-9 round 3: an estimate-backed recommendation, interp C-1270). With <paramref name="probeRun"/> set
    /// (MAX-SPEED with <c>probe_run</c>; interp C-1291): after a clean burst the estimate alone never moves the
    /// recommendation down, only errors (an errored burst, whose estimate may) and caps do; and when the
    /// estimate's target is not above the recommendation, a run of <paramref name="probeRun"/> clean bursts on
    /// the recommended rung moves it up to the next position of the walk, the probe, which the sender bounds by
    /// its failed-trial ceiling (interp C-1281) and the receiver by its caps. P7-9 round 4 (coordinator rulings
    /// 2026-11-09): with the probe, MAX-SPEED moves down on the estimate only after a burst over
    /// <c>rx_error_limit_ppt</c> or with a lost block (<paramref name="downTrigger"/>), and then at most one
    /// position of the walk, however low the estimate's target (interp C-1291); the caps still apply after. And for LOW-SPEED
    /// (<paramref name="bulk"/> false) the clean run that backs a move up may be that of the burst's own rung
    /// when it is above the recommendation, not only that of the recommended rung (interp C-1270). The probe
    /// is held while <see cref="RecommendationState.ProbeHeld"/>: on a link whose first C_PDU from the peer has
    /// not yet been received complete (coordinator ruling 2026-11-11, interp C-1293).
    /// </summary>
    private static int? Walk(DrcTable table, RecommendationState st, int? r, IReadOnlyList<int> walk, IReadOnlyList<int?>? row, int? smoothed, int pos, int? ceiling, int? probeRun, bool downTrigger, bool bulk)
    {
        if (row is not null && smoothed is int s)
        {
            int target = walk[0];
            for (int i = walk.Count - 1; i >= 0; i--)
            {
                if (row[walk[i]] is int t && t <= s)
                {
                    target = walk[i];
                    break;
                }
            }

            if (r is not int current)
            {
                r = AtOrBelow(walk, pos >= 0 ? Math.Min(target, pos) : target) ?? walk[0];
            }
            else
            {
                int run = st.CleanRun.GetValueOrDefault(RecommendationState.Key(table.Ladder[current]));
                int backing = run;
                if (!bulk && pos > current)
                {
                    // Round 4 (interp C-1270): a higher rung received clean backs LOW-SPEED's move up too.
                    backing = Math.Max(run, st.CleanRun.GetValueOrDefault(RecommendationState.Key(table.Ladder[pos])));
                }

                if (target < current)
                {
                    if (probeRun is null)
                    {
                        r = target;   // a move down is immediate
                    }
                    else if (downTrigger)
                    {
                        // With the probe, only after a burst over the error limit or with a lost block, and then at
                        // most one position of the walk: the estimate alone pulls it no further (round 4, interp C-1291).
                        r = Math.Max(target, AtOrBelow(walk, current - 1) ?? target);
                    }
                }
                else if (target > current && backing >= DrcTable.Need(table.CleanRun, "clean_run"))
                {
                    r = target;   // straight to the target, after the clean run (round 3; was one rung)
                }

                int? next = Above(walk, current);
                if (probeRun is int n && !st.ProbeHeld && target <= current && r == current && next is int up && run >= n)
                {
                    r = up;   // the probe: the next position of the walk, after probe_run clean bursts on the rung (interp C-1291)
                }
            }
        }

        return r is int v && ceiling is int c && v > c ? AtOrBelow(walk, c) ?? c : r;
    }

    /// <summary>The highest position of the walk not above <paramref name="p"/>, or null.</summary>
    private static int? AtOrBelow(IReadOnlyList<int> walk, int p)
    {
        for (int i = walk.Count - 1; i >= 0; i--)
        {
            if (walk[i] <= p)
            {
                return walk[i];
            }
        }

        return null;
    }

    /// <summary>The lowest position of the walk above <paramref name="p"/>, or null.</summary>
    private static int? Above(IReadOnlyList<int> walk, int p)
    {
        foreach (int w in walk)
        {
            if (w > p)
            {
                return w;
            }
        }

        return null;
    }

    /// <summary>The MAX-SPEED or LOW-SPEED contents for a ladder rung: Speed = WN, the interleaver code of its name (C-0462, C-0463, C-0469).</summary>
    public static SpeedInterleaver ToSpeed(Speed rung) => new((int)rung.Interleaver + 1, rung.Wn);

    /// <summary>
    /// The EOWs a peer's last SENDER-APPROACH asks for, by Table C-25 (C-0950 to C-0954), mixed when
    /// none has come (C-0956): the top priority, and the repeating set in rotation order (open
    /// question 1, interp C-1260). Entries with no value yet are left out (interp C-1271).
    /// </summary>
    public static (IReadOnlyList<Eow> Top, IReadOnlyList<Eow> Set) Wanted(DrcTable table, RecommendationState? st, SenderApproachEow? approach)
    {
        if (table.IsEmpty || st is null || !st.Heard)
        {
            return ([], []);
        }

        Eow? max = st.MaxSpeed is int m ? new MaxSpeedEow(ToSpeed(table.Ladder[m])) : null;
        Eow? low = st.LowSpeed is int l ? new LowSpeedEow(ToSpeed(table.Ladder[l])) : null;
        bool fixedSender = approach?.Fixed == true;
        Eow seg = new MaxSegmentEow(fixedSender ? st.Segment : RecommendationState.SegmentMax);

        (Eow?[] top, Eow?[] set) = fixedSender
            ? ([], [seg])                                                  // C-0950
            : (approach?.Strategy ?? 2) switch
            {
                0 => (new[] { max }, new[] { max, max, seg }),             // bulk, C-0951
                1 => (new[] { low }, new[] { low, seg }),                  // low latency, C-0952
                3 => (new[] { max }, new[] { max, low, max, seg }),        // intermediate, C-0954
                _ => (new[] { max, low }, new[] { max, low, seg }),        // mixed, C-0953, C-0956
            };

        return (top.OfType<Eow>().ToList(), set.OfType<Eow>().ToList());
    }

    /// <summary>
    /// ED3-BASIC-RATE for a peer recorded Ed3 (C-0957, C-0959), from the MAX-SPEED rung, Other
    /// Parameters 10 (C-0440), or null when there is none.
    /// </summary>
    public static Ed3BasicRateEow? Ed3(DrcTable table, RecommendationState? st) =>
        !table.IsEmpty && st?.MaxSpeed is int m ? Ed3BasicRate(table.Ladder[m]) : null;

    /// <summary>
    /// Our C-0442 table (open question 10, interp C-1273): the highest Table C-5 rate not above the
    /// rung's data rate (WN 1 150, WN 2 300, WN 3 600, WN 4 and WN 5 1200, WN 13 2400, WN 6 3200, WN 7
    /// 4800, WN 8 6400 bps), and Ultra Short or Short as short (01), Medium or Long as long (10).
    /// </summary>
    /// <exception cref="ArgumentException">The rung is not one our ladder can hold.</exception>
    public static Ed3BasicRateEow Ed3BasicRate(Speed rung)
    {
        int rate = rung.Wn switch
        {
            1 => 1,    // 150
            2 => 2,    // 300
            3 => 3,    // 600
            4 or 5 => 4,   // 1200
            13 => 5,   // 2400
            6 => 6,    // 3200
            7 => 8,    // 4800
            8 => 9,    // 6400
            _ => throw new ArgumentException("WN 1 to 8 or 13 only", nameof(rung)),
        };
        int interleaving = rung.Interleaver switch
        {
            Interleaver.UltraShort or Interleaver.Short => 1,
            Interleaver.Medium or Interleaver.Long => 2,
            _ => throw new ArgumentException("ultra short, short, medium or long only", nameof(rung)),
        };
        return new Ed3BasicRateEow(rate, interleaving, 2);
    }
}
