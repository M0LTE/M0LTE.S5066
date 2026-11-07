namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// The core's record of running timers and their deadlines (core-architecture.md 3.3). It emits
/// the <c>timer_start</c> and <c>timer_cancel</c> outputs; nothing fires by itself, the harness or
/// host feeds <see cref="TimerExpired"/> at the deadline (3.4).
/// </summary>
/// <remarks>
/// Protocol code never enumerates this table: a sublayer that stops several timers cancels them by
/// name in the order its code lists them, so the order of outputs never depends on how
/// <see cref="TimerKind"/> is numbered. Hence there is deliberately no enumeration member.
/// </remarks>
internal sealed class TimerTable
{
    private readonly SortedDictionary<TimerName, long> _deadline = new();
    private readonly CoreContext _context;

    public TimerTable(CoreContext context) => _context = context;

    /// <summary>
    /// Starts or restarts a timer: one <see cref="TimerStart"/> output, deadline now + ms.
    /// <paramref name="ms"/> must be at least 1 (corpus-format.md 4.4); a zero wait goes through
    /// <see cref="StartUnlessZero"/>.
    /// </summary>
    public void Start(TimerName name, long ms)
    {
        name.Validate();
        if (ms < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ms), ms, $"timer {name} needs ms >= 1; use StartUnlessZero for a wait that may be zero");
        }

        _deadline[name] = _context.Now + ms;
        _context.Emit(new TimerStart(name, ms));
    }

    /// <summary>
    /// The zero-duration rule (core-architecture.md 3.3, corpus-format.md 4.4). With ms &gt;= 1 this
    /// is <see cref="Start"/> and returns true. With ms = 0 it starts nothing, emits nothing and
    /// returns false, and the caller must take the action the expiry would have taken, at once,
    /// within the same input. Asking for a zero wait on a running timer is a bug: the caller
    /// cancels it explicitly first, so the cancel shows in the trace.
    /// </summary>
    public bool StartUnlessZero(TimerName name, long ms)
    {
        if (ms < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ms), ms, $"timer {name} with a negative duration");
        }

        if (ms > 0)
        {
            Start(name, ms);
            return true;
        }

        name.Validate();
        if (_deadline.ContainsKey(name))
        {
            throw new InvalidOperationException($"zero wait asked for running timer {name}; cancel it first");
        }

        return false;
    }

    /// <summary>Stops a timer. Emits <see cref="TimerCancel"/> only if it was running.</summary>
    public void Cancel(TimerName name)
    {
        if (_deadline.Remove(name))
        {
            _context.Emit(new TimerCancel(name));
        }
    }

    /// <summary>True when the timer is running.</summary>
    public bool IsRunning(TimerName name) => _deadline.ContainsKey(name);

    /// <summary>The deadline of a running timer, or null.</summary>
    public long? Deadline(TimerName name) => _deadline.TryGetValue(name, out long d) ? d : null;

    /// <summary>
    /// Removes a timer whose expiry is being delivered. The harness has already checked the
    /// deadline; the core checks again and treats a mismatch as a bug, never a silent no-op.
    /// </summary>
    public void Expire(TimerName name, long now)
    {
        if (!_deadline.TryGetValue(name, out long deadline))
        {
            throw new InvalidOperationException($"expiry of timer {name}, which is not running");
        }

        if (deadline != now)
        {
            throw new InvalidOperationException($"expiry of timer {name} at {now}, but its deadline is {deadline}");
        }

        _deadline.Remove(name);
    }
}
