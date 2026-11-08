using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Host;

/// <summary>The input classes of corpus-format.md 4.5, in handling order.</summary>
internal enum InputClass
{
    Config = 0,
    Timer = 1,
    Modem = 2,
    Sis = 3,
}

/// <summary>
/// The host's queues and timer table (core-architecture.md 12.3, 12.4). Not thread-safe: the
/// host holds one lock around every call. It decides which input the core gets next, so a real
/// node sees inputs in the order a T2 trace must list them.
/// </summary>
/// <remarks>
/// The next input is always the pending one with the smallest key (T, class, sequence). A posted
/// event's T is the host time it was posted, never less than the last T handed to the core; a
/// timer's T is its deadline, and it is pending once the host clock has reached it. Timers of one
/// T go in the order they were last started (corpus-format.md 4.5).
/// </remarks>
internal sealed class InputScheduler
{
    private readonly Queue<CoreInput>[] _queues = [new(), new(), new(), new()];
    private readonly SortedDictionary<TimerName, (long Deadline, long Seq)> _timers = [];
    private long _timerSeq;
    private long _lastPosted;

    /// <summary>The T of the last input taken, or 0.</summary>
    public long LastTaken { get; private set; }

    /// <summary>Running timers, for tests and status.</summary>
    public int RunningTimers => _timers.Count;

    /// <summary>Queues a configuration, modem or SIS input, stamped with <paramref name="now"/>.</summary>
    /// <param name="cls">The input's class; never <see cref="InputClass.Timer"/>.</param>
    /// <param name="make">Builds the input from its stamp.</param>
    /// <param name="now">Host time now.</param>
    public void Post(InputClass cls, Func<long, CoreInput> make, long now)
    {
        if (cls == InputClass.Timer)
        {
            throw new ArgumentException("timers are not posted", nameof(cls));
        }

        long t = Math.Max(now, Math.Max(_lastPosted, LastTaken));
        _lastPosted = t;
        _queues[(int)cls].Enqueue(make(t));
    }

    /// <summary>
    /// Records the timer outputs of the input just handled at <see cref="LastTaken"/>
    /// (core-architecture.md 12.3). Other outputs are ignored.
    /// </summary>
    public void Apply(CoreOutput output)
    {
        switch (output)
        {
            case TimerStart s:
                _timers[s.Name] = (LastTaken + s.Ms, ++_timerSeq);
                break;
            case TimerCancel c:
                _timers.Remove(c.Name);
                break;
        }
    }

    /// <summary>
    /// Takes the next input due at or before <paramref name="now"/>, or returns null and gives
    /// the deadline of the next timer (null when none runs) so the caller knows how long to sleep.
    /// </summary>
    public CoreInput? TakeNext(long now, out long? nextDeadline)
    {
        nextDeadline = null;
        (TimerName Name, long Deadline, long Seq)? timer = null;
        foreach ((TimerName name, (long deadline, long seq)) in _timers)
        {
            if (timer is null || deadline < timer.Value.Deadline || (deadline == timer.Value.Deadline && seq < timer.Value.Seq))
            {
                timer = (name, deadline, seq);
            }
        }

        CoreInput? best = null;
        int bestClass = int.MaxValue;
        if (timer is { } tm && tm.Deadline <= now)
        {
            best = new TimerExpired(tm.Deadline, tm.Name);
            bestClass = (int)InputClass.Timer;
        }

        for (int cls = 0; cls < _queues.Length; cls++)
        {
            if (cls == (int)InputClass.Timer || _queues[cls].Count == 0)
            {
                continue;
            }

            long t = _queues[cls].Peek().T;
            if (best is null || t < best.T || (t == best.T && cls < bestClass))
            {
                best = _queues[cls].Peek();
                bestClass = cls;
            }
        }

        if (best is null)
        {
            nextDeadline = timer?.Deadline;
            return null;
        }

        if (bestClass == (int)InputClass.Timer)
        {
            _timers.Remove(((TimerExpired)best).Name);
        }
        else
        {
            _queues[bestClass].Dequeue();
            if (best.T < LastTaken)
            {
                best = best with { T = LastTaken };
            }
        }

        LastTaken = best.T;
        return best;
    }
}
