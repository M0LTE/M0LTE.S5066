namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// The DTS (Annex C) and the receive side of the modem. P3-0 stub: it ignores received bytes.
/// P3-3 adds the D_PDU stream receiver (sync search, edition peek, EOT to the MAC through
/// <see cref="IMacObserver"/>); P3-4 onwards the transmit queue and the planner
/// (core-architecture.md 4.2, 5, section 9).
/// </summary>
internal sealed class DtsSublayer : IDtsSublayer
{
    private readonly CoreContext _context;
    private readonly IMacObserver _mac;
    private readonly List<DUnidataRequest> _pendingNonArq = [];

    public DtsSublayer(CoreContext context, IMacObserver mac)
    {
        _context = context;
        _mac = mac;
    }

    public void OnConfigChanged(NodeConfig oldConfig, NodeConfig newConfig)
    {
    }

    public void OnTimer(TimerName name) =>
        throw new InvalidOperationException($"DTS timer {name} expired, but the DTS starts no timers yet (now {_context.Now})");

    public void OnBurstStarted()
    {
    }

    public void OnBytesReceived(ReadOnlyMemory<byte> bytes)
    {
    }

    public void OnBurstEnded(BurstEnded burst)
    {
    }

    public void OnTransmitDone()
    {
    }

    public void On(ToDts primitive)
    {
        switch (primitive)
        {
            case DUnidataRequest { Arq: false } r:
                OnNonArqRequest(r);
                break;
            default:
                throw new InvalidOperationException($"the DTS does not handle {primitive.GetType().Name} yet");
        }
    }

    /// <summary>The non-ARQ C_PDUs accepted for transmission, in arrival order (P3-1 placeholder; P3-4 builds the real queue).</summary>
    internal IReadOnlyList<DUnidataRequest> PendingNonArq => _pendingNonArq;

    /// <summary>
    /// P3-1: a non-ARQ C_PDU is accepted into the transmit queue and the MAC is told the queue
    /// holds something it may send (K-0096; core-architecture.md 4.1). Nothing is transmitted until
    /// the planner (P3-4), which replaces this list with the queue of core-architecture.md 5.2.
    /// </summary>
    private void OnNonArqRequest(DUnidataRequest r)
    {
        _pendingNonArq.Add(r);
        _mac.QueueState(true);
    }
}
