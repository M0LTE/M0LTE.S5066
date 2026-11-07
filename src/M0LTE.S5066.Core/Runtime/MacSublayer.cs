namespace M0LTE.S5066.Core.Runtime;

/// <summary>The Annex K states a trace can show; OFFLINE is never visible (core-architecture.md 4.2, section 10 item 1).</summary>
internal enum MacState
{
    Sense,
    LbtWait,
    ContWait,
    Linking,
}

/// <summary>
/// The Annex K MAC. P3-0 stub: it records the channel facts its entry calls report and stays in
/// SENSE, which is exactly Annex K for a node with nothing queued (K-0043, K-0065), and nothing
/// can be queued before the SIS lands in P3-1. P3-2 adds the transitions of Tables 1 to 5 in
/// <see cref="AfterInput"/>, and the <c>csma.jitter</c> draw, the only PRNG site.
/// </summary>
internal sealed class MacSublayer : IMacSublayer
{
    private readonly CoreContext _context;
    private readonly SplitMix64 _prng;

    public MacSublayer(CoreContext context, SplitMix64 prng)
    {
        _context = context;
        _prng = prng;
    }

    public MacState State { get; private set; } = MacState.Sense;

    /// <summary>DCD from the modem: true between burst_started and burst_ended.</summary>
    public bool Dcd { get; private set; }

    /// <summary>The external busy input, recorded and ignored until P5-4.</summary>
    public bool ExternalBusy { get; private set; }

    /// <summary>Set by transmit_done: our own transmission ended last (K-0074). P3-2 owns the full last-transmission record.</summary>
    public bool OwnTransmissionEnded { get; private set; }

    /// <summary>The last predicted EOT end time reported by the DTS (combining rule: P3-3).</summary>
    public long? PredictedEndTime { get; private set; }

    /// <summary>The last queue state reported by the DTS (K-0096).</summary>
    public bool QueueNonEmpty { get; private set; }

    /// <summary>The PRNG state, for tests only: no draw is made in P3-0.</summary>
    internal ulong PrngState => _prng.State;

    public void OnConfigChanged(NodeConfig oldConfig, NodeConfig newConfig)
    {
    }

    public void OnTimer(TimerName name) =>
        throw new InvalidOperationException($"MAC timer {name} expired, but the MAC starts no timers yet (now {_context.Now})");

    public void OnBurstStarted() => Dcd = true;

    public void OnBurstEnded(BurstEnded burst) => Dcd = false;

    public void OnTransmitDone() => OwnTransmissionEnded = true;

    public void OnChannelBusy(bool busy) => ExternalBusy = busy;

    public void EotHeard(long predictedEndTime) => PredictedEndTime = predictedEndTime;

    public void DpduHeard(uint src, uint dst, bool dstIsGroup)
    {
    }

    public void QueueState(bool nonEmpty) => QueueNonEmpty = nonEmpty;

    public void AfterInput()
    {
        // P3-2: the SENSE, LBT_WAIT, CONT_WAIT and LINKING transitions. Until then the node never
        // has data queued, and Annex K keeps such a node in SENSE whatever the channel does.
        if (State != MacState.Sense)
        {
            throw new InvalidOperationException($"MAC stub left SENSE (state {State})");
        }
    }
}
