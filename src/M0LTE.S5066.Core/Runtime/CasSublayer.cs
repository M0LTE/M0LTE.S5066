namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// The CAS (Annex B). P3-0 stub: P3-1 adds the DATA C_PDU wrap and unwrap, P4-1 the PeerTable and
/// CAS-1 linking (core-architecture.md 4.2, section 9).
/// </summary>
internal sealed class CasSublayer : ICasSublayer
{
    private readonly CoreContext _context;

    public CasSublayer(CoreContext context) => _context = context;

    public void OnConfigChanged(NodeConfig oldConfig, NodeConfig newConfig)
    {
    }

    public void OnTimer(TimerName name) =>
        throw new InvalidOperationException($"CAS timer {name} expired, but the CAS starts no timers yet (now {_context.Now})");

    public void On(ToCas primitive) =>
        throw new InvalidOperationException($"the CAS does not handle {primitive.GetType().Name} yet");
}
