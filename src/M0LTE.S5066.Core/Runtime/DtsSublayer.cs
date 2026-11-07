namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// The DTS (Annex C) and the receive side of the modem. The D_PDU stream receiver
/// (<see cref="DtsReceiver"/>, P3-3) finds D_PDUs and tells the MAC what it heard through
/// <see cref="IMacObserver"/>; P3-4 onwards the transmit queue and the planner
/// (core-architecture.md 4.2, 5, section 9).
/// </summary>
internal sealed class DtsSublayer : IDtsSublayer
{
    private readonly CoreContext _context;
    private readonly IMacObserver _mac;
    private readonly DtsReceiver _receiver;
    private readonly TransmitQueue _tx;
    private readonly UnconnectedPeers _peers;

    public DtsSublayer(CoreContext context, IMacObserver mac)
    {
        _context = context;
        _mac = mac;
        _receiver = new DtsReceiver(context, mac, OnDpduReceived);
        _tx = new TransmitQueue(context, mac);
        _peers = new UnconnectedPeers(context, _tx);
    }

    /// <summary>The unconnected-state receive machine (P3-6), for tests.</summary>
    internal UnconnectedPeers Peers => _peers;

    /// <summary>The D_PDU stream receiver, for tests.</summary>
    internal DtsReceiver Receiver => _receiver;

    public void OnConfigChanged(NodeConfig oldConfig, NodeConfig newConfig)
    {
    }

    public void OnTimer(TimerName name)
    {
        switch (name.Kind)
        {
            case TimerKind.DtsTtd:
                _tx.OnTtdTimer();   // C-0857, C-0864, C-1050
                break;
            case TimerKind.DtsReceptionWindow:
                _peers.OnReceptionWindowTimer();
                break;
            default:
                throw new InvalidOperationException($"DTS timer {name} expired, but the DTS does not start it (now {_context.Now})");
        }
    }

    public void OnBurstStarted() => _receiver.OnBurstStarted();

    public void OnBytesReceived(ReadOnlyMemory<byte> bytes) => _receiver.OnBytesReceived(bytes.Span);

    public void OnBurstEnded(BurstEnded burst) => _receiver.OnBurstEnded(burst);

    /// <summary>
    /// A whole D_PDU with good CRCs, whoever it is addressed to (C-0838), to the per-peer state
    /// machine, which acts only on the ones addressed to this node or its groups (C-0566). Before
    /// CAS-1 linking (P4-1) every peer is unconnected (P3-6).
    /// </summary>
    private void OnDpduReceived(Dts.Dpdu pdu) => _peers.OnDpdu(pdu);

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
