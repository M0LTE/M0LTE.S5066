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

    public void On(ToDts primitive) =>
        throw new InvalidOperationException($"the DTS does not handle {primitive.GetType().Name} yet (queue observer: {_mac.GetType().Name})");
}
