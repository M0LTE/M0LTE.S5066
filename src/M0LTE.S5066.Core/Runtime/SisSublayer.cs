namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// The SIS (Annexes A and S). P3-0 stub: no connection handling yet; P3-1 adds connections, the
/// SAP table and unidata requests (core-architecture.md 4.2, section 9).
/// </summary>
internal sealed class SisSublayer : ISisSublayer
{
    private readonly CoreContext _context;

    public SisSublayer(CoreContext context) => _context = context;

    public void OnConfigChanged(NodeConfig oldConfig, NodeConfig newConfig)
    {
    }

    public void OnTimer(TimerName name) =>
        throw new InvalidOperationException($"SIS timer {name} expired, but the SIS starts no timers yet (now {_context.Now})");

    public void OnConnect(int conn)
    {
    }

    public void OnBytes(int conn, ReadOnlyMemory<byte> bytes)
    {
    }

    public void OnDisconnect(int conn)
    {
    }

    public void On(ToSis primitive) =>
        throw new InvalidOperationException($"the SIS does not handle {primitive.GetType().Name} yet");
}
