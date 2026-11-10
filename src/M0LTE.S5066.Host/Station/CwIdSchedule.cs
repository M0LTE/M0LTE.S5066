namespace M0LTE.S5066.Host.Station;

/// <summary>
/// When the identification hook sends (plan 9.1, decision D4; core-architecture.md 10 item 13).
/// The ID is never a transmission the node decides on by itself: it is keyed only at the start of
/// a keyup the core has already been granted by Annex K, ahead of the 5066 burst. So this class
/// answers one question at each keyup, "does the ID go before this one?", and keeps the times it
/// needs. Pure: the caller gives the time, so tests run it on a virtual clock.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Pre-session (<c>at_start</c>): before the first keyup since start-up, and before the
/// first keyup after <see cref="QuietMs"/> or more with none.</item>
/// <item>Periodic (<c>every_minutes</c>): before the first keyup once that long has passed since
/// the last ID, or, before any ID, since the first keyup. An idle node therefore stays silent,
/// as D4 asks, and the ID goes with the next transmission rather than at the exact minute.</item>
/// <item>Nothing at the end of a transmission (D4).</item>
/// </list>
/// Thread-safe: the radio calls <see cref="Keyup"/> on its modem thread while the status page reads.
/// </remarks>
public sealed class CwIdSchedule
{
    /// <summary>The quiet spell after which <c>at_start</c> identifies again: 10 minutes with no keyup.</summary>
    public const long QuietMs = 10 * 60_000;

    private readonly object _gate = new();
    private long? _lastId;
    private long? _lastKeyup;
    private long? _firstKeyup;
    private long _ids;

    /// <summary>Creates the schedule.</summary>
    /// <param name="atStart">Identify before the first keyup after a quiet spell.</param>
    /// <param name="everyMinutes">Identify again once this many minutes have passed since the last ID, or null for no periodic ID.</param>
    public CwIdSchedule(bool atStart, int? everyMinutes)
    {
        if (everyMinutes is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(everyMinutes), "must be at least 1");
        }

        AtStart = atStart;
        EveryMs = everyMinutes * 60_000L;
    }

    /// <summary>Whether a pre-session ID is sent.</summary>
    public bool AtStart { get; }

    /// <summary>The periodic interval in ms, or null.</summary>
    public long? EveryMs { get; }

    /// <summary>
    /// A keyup starts at <paramref name="nowMs"/>: true when the ID goes before it. The answer is
    /// recorded, so call this once per keyup.
    /// </summary>
    public bool Keyup(long nowMs)
    {
        lock (_gate)
        {
            bool startDue = AtStart && (_lastKeyup is not long last || nowMs - last >= QuietMs);
            bool periodicDue = EveryMs is long every && (_lastId ?? _firstKeyup) is long since && nowMs - since >= every;
            _firstKeyup ??= nowMs;
            _lastKeyup = nowMs;
            if (startDue || periodicDue)
            {
                _lastId = nowMs;
                _ids++;
                return true;
            }

            return false;
        }
    }

    /// <summary>A snapshot for the status page.</summary>
    public CwIdState State()
    {
        lock (_gate)
        {
            long? periodicFrom = EveryMs is long every && (_lastId ?? _firstKeyup) is long since ? since + every : null;
            return new CwIdState(_ids, _lastId, _lastKeyup, periodicFrom);
        }
    }
}

/// <summary>Where the identification schedule stands.</summary>
/// <param name="Sent">IDs sent so far.</param>
/// <param name="LastIdMs">When the last ID was keyed, or null.</param>
/// <param name="LastKeyupMs">When the last keyup started, or null.</param>
/// <param name="PeriodicDueFromMs">From when the next periodic ID goes with the next keyup, or null when there is none yet.</param>
public sealed record CwIdState(long Sent, long? LastIdMs, long? LastKeyupMs, long? PeriodicDueFromMs);
