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
    private readonly DtsPeers _peers;
    private readonly ChannelAnalysis _analysis;

    public DtsSublayer(CoreContext context, IMacObserver mac)
    {
        _context = context;
        _mac = mac;
        _analysis = new ChannelAnalysis(context);
        _receiver = new DtsReceiver(context, mac, OnDpduReceived, _analysis.OnHeader);
        _tx = new TransmitQueue(context, mac);
        _peers = new DtsPeers(context, _tx);
    }

    /// <summary>The unconnected-state receive machine (P3-6), for tests.</summary>
    internal DtsPeers Peers => _peers;

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
                _peers.CheckClean();   // a C_PDU dropped for its TTD may leave a closing link clean (B-0422)
                break;
            case TimerKind.DtsAckWait:
                if (!_peers.OnAckWaitExpired(name.Peer!.Value))   // C-1250: or the connection is lost
                {
                    _tx.OnAckWaitTimer(name.Peer!.Value);   // C-0911, C-1120
                }

                break;
            case TimerKind.DtsReceptionWindow:
                _peers.OnReceptionWindowTimer();
                break;
            case TimerKind.DtsResetWait:
                _peers.OnResetWaitTimer(name.Peer!.Value);   // C-0255, C-1172
                break;
            default:
                throw new InvalidOperationException($"DTS timer {name} expired, but the DTS does not start it (now {_context.Now})");
        }
    }

    public void OnBurstStarted()
    {
        _peers.OnBurstStarted();   // a new transmission from some node (C-1231)
        _receiver.OnBurstStarted();
        _analysis.OnBurstStarted();
    }

    public void OnBytesReceived(ReadOnlyMemory<byte> bytes) => _receiver.OnBytesReceived(bytes.Span);

    /// <summary>
    /// The received burst ended: the turn to judge clean close state that usually follows a peer's
    /// transmission (B-0237, B-0422), then the channel analysis of the burst (J-0046 to J-0051).
    /// </summary>
    public void OnBurstEnded(BurstEnded burst)
    {
        _peers.OnBurstMeasured(burst, _receiver.OnBurstEnded(burst));   // P7-6 recommendations
        _peers.CheckClean();
        _analysis.OnBurstEnded();
    }

    /// <summary>
    /// A whole D_PDU with good CRCs, whoever it is addressed to (C-0838), to the per-peer state
    /// machine, which acts only on the ones addressed to this node or its groups (C-0566).
    /// </summary>
    private void OnDpduReceived(Dts.Dpdu pdu)
    {
        _analysis.OnDpdu(pdu);   // J-0043: what it says about the sender's MAC mode
        _peers.OnDpdu(pdu);
    }

    /// <summary>
    /// Our transmission has ended: each expedited request (C-1100) and each normal non-ARQ request
    /// (interp C-1255) whose last D_PDU it carried is confirmed to the CAS, in placement order. First, a peer it carried a D_PDU to that has been silent
    /// too long loses its connection (C-1250). Then the ARQ bookkeeping of core-architecture.md 5.7:
    /// <c>dts.ack_wait</c> for each peer the transmission carried ARQ data to (C-1120), then
    /// <c>dts.reset_wait</c> for each peer it carried a RESET/WIN-RESYNC command to (C-1172), and a
    /// peer newly awaited has its silence timed from now (C-1250). Last, closing links that are now clean are reported (B-0422).
    /// </summary>
    public void OnTransmitDone()
    {
        _peers.CheckSilence(_tx.TakeSentPeers());   // C-1250: before any wait starts
        _tx.OnTransmitDone();
        _peers.OnTransmitDone();
        _peers.OnWaitsStarted();   // C-1250
        foreach ((int handle, bool expedited) in _tx.TakeSent())
        {
            _context.Enqueue(expedited ? new DExpeditedUnidataRequestConfirm(handle) : new DUnidataRequestConfirm(handle));
        }

        _peers.CheckClean();   // B-0237, B-0422: after the confirms
    }

    public void On(ToDts primitive)
    {
        switch (primitive)
        {
            case DUnidataRequest { Arq: false } r:
                if (!r.DstIsGroup)
                {
                    _peers.OnNonArqRequest(r.Dst);   // C-0655
                }

                _tx.Add(r);
                break;
            case DUnidataRequest r when _peers.IsConnected(r.Dst):
                _peers.OnArqRequest(r.Dst);   // C-0653, C-0673
                _tx.AddArq(r);
                break;
            case DUnidataRequest r:
                // An UNCONNECTED state rejects ARQ requests (C-0573, C-0593, C-0613, C-0633; C-0731).
                _context.Enqueue(new DUnidataRequestRejected(r.Handle, DReason.NoConnection));
                break;
            case DConnectionMade c:
                _peers.OnConnectionMade(c);
                break;
            case DConnectionTerminated t:
                _peers.OnConnectionTerminated(t.Peer);
                break;
            case DCloseRequest c:
                _peers.OnCloseRequest(c);   // B-0246, interp B-0421
                break;
            case DExpeditedWithdraw w:
                _tx.Withdraw(w.Handle);   // interp B-0453
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

    /// <summary>The ARQ C_PDUs held for connected peers, in arrival order (placed from P4-2).</summary>
    internal IReadOnlyList<DUnidataRequest> HeldArq => _tx.HeldArq;

    /// <summary>The non-ARQ C_PDUs queued for normal delivery, in arrival order.</summary>
    internal IReadOnlyList<DUnidataRequest> PendingNonArq => _tx.PendingNonArq;

    /// <summary>The transmit queue and planner, which the MAC asks for a transmission (core-architecture.md 5).</summary>
    internal ITransmissionSource Transmitter => _tx;
}
