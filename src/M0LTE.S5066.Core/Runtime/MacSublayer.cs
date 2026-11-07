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
/// The Annex K MAC: carrier sense multiple access with collision avoidance (K-0005, C-0841), with
/// the four visible states of Figure K-1 and the outbound transitions of Tables 1 to 5 (K-0059).
/// </summary>
/// <remarks>
/// <para>
/// Its entry calls and <see cref="OnTimer"/> only record facts. Every transition is taken in
/// <see cref="AfterInput"/>, once per input after the primitive FIFO is empty, chaining through
/// zero-duration waits (core-architecture.md 3.3 and 3.5 rule 4).
/// </para>
/// <para>
/// The channel model in this item is DCD only: DCD is true from <c>burst_started</c> to
/// <c>burst_ended</c> (K-0019, D-0018, C-0845). VDCD from parsed EOTs, the <c>csma.vdcd_end</c>
/// timer and LBT rules 1 and 3 come with the D_PDU receiver (P3-3); the external busy input is
/// recorded and ignored until P5-4. So every idle here is DCD-determined (K-0070, K-0073), except
/// the end of this node's own transmission, which counts as VDCD-determined (K-0074).
/// </para>
/// <para>
/// Hold while busy (K-0077, K-0087, K-0089): a busy indication in LBT_WAIT or CONT_WAIT cancels
/// the running timer and leaves the node in LBT_WAIT; when the channel is idle again
/// <c>csma.lbt_wait</c> starts afresh with <c>lbt_wait_dcd_ms</c>. A trace shows exactly a
/// <c>timer_cancel</c> at the busy indication and a <c>timer_start</c> at the idle one.
/// </para>
/// <para>
/// The one PRNG draw site, <c>csma.jitter</c> (prng.md 6): one <c>uniform(num_cont_slots)</c> per
/// entry into CONT_WAIT in jitter or ed3 mode (K-0080, K-0084). Slotted mode never draws.
/// </para>
/// </remarks>
internal sealed class MacSublayer : IMacSublayer
{
    private static readonly TimerName LbtWait = TimerName.Of(TimerKind.CsmaLbtWait);
    private static readonly TimerName ContWait = TimerName.Of(TimerKind.CsmaContWait);

    private readonly CoreContext _context;
    private readonly SplitMix64 _prng;

    // Facts recorded by entry calls and OnTimer, consumed by AfterInput.
    private bool _lbtExpired;
    private bool _contExpired;
    private bool _ownTransmissionEnded;

    // LBT_WAIT with csma.lbt_wait deliberately not running because the channel is busy (K-0087).
    private bool _lbtHeld;

    public MacSublayer(CoreContext context, SplitMix64 prng)
    {
        _context = context;
        _prng = prng;
        CheckSlotPosition(context.Config.Csma);
    }

    public MacState State { get; private set; } = MacState.Sense;

    /// <summary>DCD from the modem: true between burst_started and burst_ended (K-0019, D-0018).</summary>
    public bool Dcd { get; private set; }

    /// <summary>The external busy input, recorded and ignored until P5-4.</summary>
    public bool ExternalBusy { get; private set; }

    /// <summary>
    /// K-0086: the last transmission on the channel was this node's own, that is, no busy period
    /// has been seen since this node's own last transmission ended. False before any transmission.
    /// </summary>
    public bool LastTransmissionOwn { get; private set; }

    /// <summary>
    /// K-0073, K-0074: the last idle was determined by VDCD rather than DCD. In this item only the
    /// end of our own transmission sets it (K-0074); P3-3 adds the EOT prediction clearing last.
    /// </summary>
    public bool IdleByVdcd { get; private set; }

    /// <summary>The last predicted EOT end time reported by the DTS (used from P3-3).</summary>
    public long? PredictedEndTime { get; private set; }

    /// <summary>The last queue state reported by the DTS (K-0096).</summary>
    public bool QueueNonEmpty { get; private set; }

    /// <summary>The PRNG state, for tests only.</summary>
    internal ulong PrngState => _prng.State;

    /// <summary>The number of PRNG draws made, for tests only.</summary>
    internal long PrngDraws => _prng.Draws;

    /// <summary>The DTS transmit queue and planner, asked for a transmission on entry to LINKING (core-architecture.md 5).</summary>
    internal ITransmissionSource? Transmitter { get; set; }

    /// <summary>
    /// Busy for Annex K: DCD (K-0063). P3-3 adds a predicted EOT end time in the future (K-0073);
    /// P5-4 ORs in the external busy input (K-0006).
    /// </summary>
    private bool Busy => Dcd;

    public void OnConfigChanged(NodeConfig oldConfig, NodeConfig newConfig) => CheckSlotPosition(newConfig.Csma);

    public void OnTimer(TimerName name)
    {
        switch (name.Kind)
        {
            case TimerKind.CsmaLbtWait:
                _lbtExpired = true;
                break;
            case TimerKind.CsmaContWait:
                _contExpired = true;
                break;
            default:
                throw new InvalidOperationException($"MAC timer {name} is not started by this MAC (now {_context.Now})");
        }
    }

    public void OnBurstStarted()
    {
        Dcd = true;

        // K-0086: a busy period has been seen since our own transmission ended.
        LastTransmissionOwn = false;
    }

    public void OnBurstEnded(BurstEnded burst)
    {
        Dcd = false;

        // K-0070, K-0073: DCD cleared, so this idle is DCD-determined (P3-3 refines this with EOTs).
        IdleByVdcd = false;
    }

    public void OnTransmitDone() => _ownTransmissionEnded = true;

    public void OnChannelBusy(bool busy) => ExternalBusy = busy;

    public void EotHeard(long predictedEndTime) => PredictedEndTime = predictedEndTime;

    public void DpduHeard(uint src, uint dst, bool dstIsGroup)
    {
    }

    public void QueueState(bool nonEmpty) => QueueNonEmpty = nonEmpty;

    /// <summary>
    /// Takes every transition the recorded facts and the queue state allow (K-0059), in order, and
    /// emits the resulting timer outputs. Zero waits chain within this call (core-architecture.md 3.3).
    /// </summary>
    public void AfterInput()
    {
        if (_ownTransmissionEnded)
        {
            _ownTransmissionEnded = false;
            if (State != MacState.Linking)
            {
                throw new InvalidOperationException($"transmit_done in {State}: the MAC was not transmitting");
            }

            // Table 5 (K-0092, K-0093): the transmission ended, back to SENSE with no timer. Our
            // transmission is now the last one (K-0086) and its end counts as VDCD-determined (K-0074).
            LastTransmissionOwn = true;
            IdleByVdcd = true;
            State = MacState.Sense;
        }

        while (Step())
        {
        }

        if (_lbtExpired || _contExpired)
        {
            throw new InvalidOperationException("a CSMA timer expiry was left unconsumed");
        }
    }

    /// <summary>One transition. Returns true when the state changed and the next state must be examined.</summary>
    private bool Step()
    {
        switch (State)
        {
            case MacState.Sense:
                // Table 2 (K-0064 to K-0066): stay in SENSE with nothing queued, whatever the channel
                // does; with data queued, wait for idle (K-0063), then LBT_WAIT.
                if (!QueueNonEmpty || Busy)
                {
                    return false;
                }

                EnterLbtWait(LbtValueOnIdle());
                return true;

            case MacState.LbtWait:
                // Table 3 (K-0076). An empty queue forces SENSE (K-0078).
                if (!QueueNonEmpty)
                {
                    _context.Timers.Cancel(LbtWait);
                    _lbtHeld = false;
                    _lbtExpired = false;
                    State = MacState.Sense;
                    return true;
                }

                // Busy: hold the timer at its restart value (K-0075, K-0077, K-0087).
                if (Busy)
                {
                    _context.Timers.Cancel(LbtWait);
                    _lbtHeld = true;
                    return false;
                }

                if (_lbtHeld)
                {
                    // Idle again after a hold: the restart value is LBT_WAIT_TIMER_VALUE_DCD (K-0077, K-0087).
                    _lbtHeld = false;
                    if (_context.Timers.StartUnlessZero(LbtWait, _context.Config.Csma.LbtWaitDcdMs))
                    {
                        return false;
                    }

                    _lbtExpired = true;
                }

                if (!_lbtExpired)
                {
                    return false;
                }

                // LBT_WAIT_TIMER expired, data queued: compute the contention wait and go to CONT_WAIT (K-0079).
                _lbtExpired = false;
                EnterContWait();
                return true;

            case MacState.ContWait:
                // Table 4 (K-0088). An empty queue forces SENSE (K-0091).
                if (!QueueNonEmpty)
                {
                    _context.Timers.Cancel(ContWait);
                    _contExpired = false;
                    State = MacState.Sense;
                    return true;
                }

                // Busy: the contention round is lost. Back to LBT_WAIT, whose timer restarts with the
                // DCD value once the channel is idle again (K-0089, K-0087).
                if (Busy)
                {
                    _context.Timers.Cancel(ContWait);
                    _contExpired = false;
                    _lbtHeld = true;
                    State = MacState.LbtWait;
                    return true;
                }

                if (!_contExpired)
                {
                    return false;
                }

                // Won the contention round with data still queued: LINKING, starting no timer (K-0090).
                _contExpired = false;
                return EnterLinking();

            default:
                // LINKING: one visit is one transmission (K-0095); it ends at transmit_done (Table 5),
                // handled at the top of AfterInput. Channel inputs meanwhile only update the model.
                return false;
        }
    }

    /// <summary>
    /// The LBT_WAIT_TIMER value on entry from SENSE (K-0066). Rule 2: a DCD-determined idle gets
    /// <c>lbt_wait_dcd_ms</c> (K-0070); a VDCD-determined one, which here is only the end of our
    /// own transmission (K-0074), gets <c>lbt_wait_vdcd_ms</c>. In ed3 mode both are the one
    /// common value, <c>lbt_wait_dcd_ms</c> (K-0107). Rules 1 and 3 from parsed D_PDUs: P3-3.
    /// </summary>
    private int LbtValueOnIdle()
    {
        CsmaConfig csma = _context.Config.Csma;
        if (csma.RequireMode() == CsmaMode.Ed3)
        {
            return csma.LbtWaitDcdMs;
        }

        return IdleByVdcd ? csma.LbtWaitVdcdMs : csma.LbtWaitDcdMs;
    }

    private void EnterLbtWait(int ms)
    {
        State = MacState.LbtWait;
        _lbtHeld = false;
        if (!_context.Timers.StartUnlessZero(LbtWait, ms))
        {
            // Zero wait: act on the expiry now (core-architecture.md 3.3).
            _lbtExpired = true;
        }
    }

    /// <summary>
    /// CONT_WAIT_TIMER_VALUE (K-0079 to K-0082, K-0085). Jitter and ed3: <c>cont_slot_width_ms</c>
    /// times <c>uniform(num_cont_slots)</c>, the <c>csma.jitter</c> draw (K-0080, K-0084, K-0107).
    /// Slotted: (position - 1) slots, or position slots when the last transmission was our own
    /// (K-0081, K-0082, K-0086); no draw. Slots count from the end of the last transmission, so no
    /// shared clock is needed (K-0110).
    /// </summary>
    private void EnterContWait()
    {
        CsmaConfig csma = _context.Config.Csma;
        long slots = csma.RequireMode() switch
        {
            CsmaMode.Slotted => SlotPosition(csma) - (LastTransmissionOwn ? 0 : 1),
            _ => (long)_prng.Uniform((ulong)csma.NumContSlots),
        };

        State = MacState.ContWait;
        if (!_context.Timers.StartUnlessZero(ContWait, slots * csma.ContSlotWidthMs))
        {
            _contExpired = true;
        }
    }

    /// <summary>
    /// Entry to LINKING (core-architecture.md 3.5 rule 4): the DTS first rejects expired C_PDUs and
    /// the FIFO is drained; if that emptied the queue the node goes back to SENSE (K-0091).
    /// Otherwise one transmission is built and emitted (K-0093, K-0095); the node stays in LINKING
    /// until transmit_done (Table 5). Returns true when the state must be examined again.
    /// </summary>
    private bool EnterLinking()
    {
        ITransmissionSource tx = Transmitter ?? throw new InvalidOperationException("the MAC has no transmission source");
        tx.ExpireDue();
        _context.DrainPrimitives();
        if (!QueueNonEmpty)
        {
            State = MacState.Sense;
            return true;
        }

        State = MacState.Linking;
        _context.Emit(tx.Build());
        return false;
    }

    private static int SlotPosition(CsmaConfig csma)
    {
        int position = csma.RequireNodeSlotPosition();
        CheckSlotPosition(csma);
        return position;
    }

    /// <summary>
    /// Slot settings as configuration validation: NODE_SLOT_POSITION has no default and must lie
    /// in 1 to NUM_CONT_SLOTS (K-0039, K-0104), which is as much of K-0037 (enough slots for every
    /// node) and K-0040 (a different position per node) as one node can check on its own.
    /// </summary>
    private static void CheckSlotPosition(CsmaConfig csma)
    {
        if (csma.Mode == CsmaMode.Slotted && csma.NodeSlotPosition is int p && (p < 1 || p > csma.NumContSlots))
        {
            throw new ConfigException("csma.node_slot_position", $"must be 1 to csma.num_cont_slots ({csma.NumContSlots}), got {p}");
        }
    }
}
