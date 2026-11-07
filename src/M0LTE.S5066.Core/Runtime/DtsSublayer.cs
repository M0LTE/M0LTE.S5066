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
    private readonly TransmitQueue _tx;

    public DtsSublayer(CoreContext context, IMacObserver mac)
    {
        _context = context;
        _mac = mac;
        _tx = new TransmitQueue(context, mac);
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
                _tx.Add(r);
                break;
            case DUnidataRequest r:
                // No ARQ connection exists before CAS-1 linking (P4-1): every peer is in an
                // UNCONNECTED state, which rejects ARQ requests (C-0573, C-0593, C-0613, C-0633; C-0731).
                _context.Enqueue(new DUnidataRequestRejected(r.Handle, DReason.NoConnection));
                break;
            case DExpeditedUnidataRequest { Arq: false } r:
                _tx.AddExpedited(r);
                break;
            case DExpeditedUnidataRequest r:
                // C-0574, C-0594, C-0614, C-0634: never requested in our profile, rejected anyway.
                _context.Enqueue(new DExpeditedUnidataRequestRejected(r.Handle, DReason.NoConnection));
                break;
            default:
                throw new InvalidOperationException($"the DTS does not handle {primitive.GetType().Name} yet");
        }
    }

    /// <summary>The non-ARQ C_PDUs queued for normal delivery, in arrival order.</summary>
    internal IReadOnlyList<DUnidataRequest> PendingNonArq => _tx.PendingNonArq;

    /// <summary>The transmit queue and planner, which the MAC asks for a transmission (core-architecture.md 5).</summary>
    internal ITransmissionSource Transmitter => _tx;
}
