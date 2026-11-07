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
/// The channel model: DCD is true from <c>burst_started</c> to <c>burst_ended</c> (K-0019,
/// D-0018, C-0845). VDCD (K-0020, K-0021) is the predicted end time the DTS derives from parsed
/// EOTs (C-0842, C-0843; see <see cref="PredictedEndTime"/>). The channel is busy while DCD is true
/// or the predicted end time lies in the future (K-0073, C-0844, C-0889), and <c>burst_ended</c>,
/// whatever its reason, never clears the prediction [Ed4 C.7.5.1.1, p.C-110]. As no input arrives
/// at the predicted end time, the MAC runs <c>csma.vdcd_end</c> to it (core-architecture.md 4.3);
/// its expiry is the VDCD idle event. The external busy input is recorded and ignored until P5-4.
/// </para>
/// <para>
/// LBT rules on entry to LBT_WAIT from SENSE, tried in the order 1, 2, 3 (K-0072): rule 1, zero,
/// when the most recent received transmission held D_PDUs addressed directly to this node and none
/// addressed directly to another node (K-0067, K-0068); rule 2, <c>lbt_wait_dcd_ms</c>, when DCD
/// cleared last (K-0070); rule 3, <c>lbt_wait_vdcd_ms</c>, when the EOT prediction cleared last or
/// at the same time (K-0071, K-0073). The end of this node's own transmission counts as
/// VDCD-determined (K-0074). Ed3 mode uses the one common value and no rule 1 (K-0107).
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
    private static readonly TimerName VdcdEnd = TimerName.Of(TimerKind.CsmaVdcdEnd);

    private readonly CoreContext _context;
    private readonly SplitMix64 _prng;

    // Facts recorded by entry calls and OnTimer, consumed by AfterInput.
    private bool _lbtExpired;
    private bool _contExpired;
    private bool _ownTransmissionEnded;

    // LBT_WAIT with csma.lbt_wait deliberately not running because the channel is busy (K-0087).
    private bool _lbtHeld;

    // The channel model fed by the DTS (C-0842, C-0843, K-0021; core-architecture.md 10 item 2).
    // _burstEnd: minimum over the EOTs of the current burst, reset at burst_started.
    // _priorEnd: the prediction carried from earlier bursts, never cleared (C-0844).
    private long? _burstEnd;
    private long? _priorEnd;
    private long? _dcdClearedAt;

    // Two-node condition (a) over the current burst, and its value for the last burst (K-0068, K-0072).
    private bool _burstToUs;
    private bool _burstToOther;
    private bool _lastBurstTwoNode;

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
    /// K-0073, K-0074: the last idle was determined by VDCD rather than DCD. True after this node's
    /// own transmission (K-0074), or when the EOT prediction cleared at or after the moment DCD
    /// cleared (a tie counts as VDCD); false when DCD cleared last, including when no EOT was parsed.
    /// </summary>
    public bool IdleByVdcd =>
        LastTransmissionOwn || (PredictedEndTime is long p && _dcdClearedAt is long c && p >= c);

    /// <summary>
    /// The predicted end time of the channel activity heard so far (C-0842, C-0843, K-0021): the
    /// later of the current burst's estimate (the minimum of its EOT end times) and the prediction
    /// carried from earlier bursts. Null when no EOT has been parsed. Never cleared by a burst end
    /// (C-0844); once it is in the past it simply no longer makes the channel busy.
    /// </summary>
    public long? PredictedEndTime => _burstEnd is long b ? (_priorEnd is long p ? Math.Max(b, p) : b) : _priorEnd;

    /// <summary>Whether the last received burst met two-node condition (a) (K-0068, K-0072).</summary>
    public bool LastBurstTwoNode => _lastBurstTwoNode;

    /// <summary>The last queue state reported by the DTS (K-0096).</summary>
    public bool QueueNonEmpty { get; private set; }

    /// <summary>The PRNG state, for tests only.</summary>
    internal ulong PrngState => _prng.State;

    /// <summary>The number of PRNG draws made, for tests only.</summary>
    internal long PrngDraws => _prng.Draws;

    /// <summary>
    /// Set by tests to stand in for the planner, which arrives in P3-4. Called when the node wins
    /// contention and enters LINKING (K-0090).
    /// </summary>
    internal Action? LinkingEntered { get; set; }

    /// <summary>
    /// Busy for Annex K: DCD, or a predicted EOT end time still in the future (K-0063, K-0073,
    /// C-0844, C-0889). A prediction equal to now has passed. P5-4 ORs in the external busy input (K-0006).
    /// </summary>
    private bool Busy => Dcd || VdcdBusy;

    private bool VdcdBusy => PredictedEndTime is long p && p > _context.Now;

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
            case TimerKind.CsmaVdcdEnd:
                // The predicted end time has been reached: VDCD has cleared (K-0073). Nothing to
                // record, since Busy is computed from the prediction and the clock.
                break;
            default:
                throw new InvalidOperationException($"MAC timer {name} is not started by this MAC (now {_context.Now})");
        }
    }

    public void OnBurstStarted()
    {
        Dcd = true;

        // A new burst starts a new EOT estimate; the old prediction still binds (C-0844), so it is
        // carried, and the later of the two is used (core-architecture.md 10 item 2).
        _priorEnd = PredictedEndTime;
        _burstEnd = null;
        _burstToUs = false;
        _burstToOther = false;

        // K-0086: a busy period has been seen since our own transmission ended.
        LastTransmissionOwn = false;
    }

    public void OnBurstEnded(BurstEnded burst)
    {
        // Whatever the reason, the burst end clears DCD only; the EOT prediction stays (C-0844).
        Dcd = false;
        _dcdClearedAt = _context.Now;

        // K-0072: condition (a) is evaluated over the transmission whose end produced the idle.
        _lastBurstTwoNode = _burstToUs && !_burstToOther;
    }

    public void OnTransmitDone() => _ownTransmissionEnded = true;

    public void OnChannelBusy(bool busy) => ExternalBusy = busy;

    /// <summary>
    /// One end time from a valid header with EOT &gt; 0 (C-0842, K-0021). Each is an upper bound on
    /// the true end, so the burst's estimate is the minimum of them (C-0843; core-architecture.md
    /// 10 item 2).
    /// </summary>
    public void EotHeard(long predictedEndTime) =>
        _burstEnd = _burstEnd is long b ? Math.Min(b, predictedEndTime) : predictedEndTime;

    /// <summary>
    /// A valid header was heard (C-0868, K-0068). Group destinations (non-ARQ to multicast or
    /// broadcast) may be present without spoiling condition (a); any other destination is
    /// "directly addressed" (K-0072), to this node or to another.
    /// </summary>
    public void DpduHeard(uint src, uint dst, bool dstIsGroup)
    {
        if (dstIsGroup)
        {
            return;
        }

        if (dst == _context.Config.LocalAddress)
        {
            _burstToUs = true;
        }
        else
        {
            _burstToOther = true;
        }
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
            State = MacState.Sense;
        }

        while (Step())
        {
        }

        ReconcileVdcdEnd();

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
                EnterLinking();
                return false;

            default:
                // LINKING: one visit is one transmission (K-0095); it ends at transmit_done (Table 5),
                // handled at the top of AfterInput. Channel inputs meanwhile only update the model.
                return false;
        }
    }

    /// <summary>
    /// The LBT_WAIT_TIMER value on entry from SENSE (K-0066), rules tried in the order 1, 2, 3
    /// (K-0072). Rule 1: zero when the last transmission was a received one meeting two-node
    /// condition (a) (K-0067, K-0068). Rule 2: a DCD-determined idle gets <c>lbt_wait_dcd_ms</c>
    /// (K-0070). Rule 3: a VDCD-determined one gets <c>lbt_wait_vdcd_ms</c> (K-0071, K-0073), and so
    /// does the end of our own transmission (K-0074). In ed3 mode both are the one common value,
    /// <c>lbt_wait_dcd_ms</c>, and rule 1 is off (K-0107).
    /// </summary>
    private int LbtValueOnIdle()
    {
        CsmaConfig csma = _context.Config.Csma;
        if (csma.RequireMode() == CsmaMode.Ed3)
        {
            return csma.LbtWaitDcdMs;
        }

        if (!LastTransmissionOwn && _lastBurstTwoNode)
        {
            return 0;
        }

        return IdleByVdcd ? csma.LbtWaitVdcdMs : csma.LbtWaitDcdMs;
    }

    /// <summary>
    /// <c>csma.vdcd_end</c> runs exactly while the MAC is in SENSE or LBT_WAIT, data is queued,
    /// DCD is false and the predicted end time is later than now; its deadline is the predicted
    /// end time (core-architecture.md 4.3, 10 item 3; K-0073, C-0844). Started, restarted or
    /// cancelled here, after every transition of the input.
    /// </summary>
    private void ReconcileVdcdEnd()
    {
        bool wanted = (State is MacState.Sense or MacState.LbtWait) && QueueNonEmpty && !Dcd && VdcdBusy;
        if (!wanted)
        {
            _context.Timers.Cancel(VdcdEnd);
            return;
        }

        long end = PredictedEndTime!.Value;
        if (_context.Timers.Deadline(VdcdEnd) != end)
        {
            _context.Timers.Start(VdcdEnd, end - _context.Now);
        }
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

    private void EnterLinking()
    {
        State = MacState.Linking;

        // The transmission planner (core-architecture.md 5; Dts.ExpireDue, then the build and the
        // transmit output) arrives in P3-4.
        (LinkingEntered ?? throw new NotSupportedException("LINKING needs the transmission planner, which arrives in P3-4")).Invoke();
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
