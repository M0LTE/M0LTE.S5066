namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// The whole protocol stack of one STANAG 5066 node (core-architecture.md 1). Created from a
/// configuration, a PRNG seed and a modem port, at time 0; construction is the Annex K START,
/// so the node is in SENSE from time 0 with no timer running (K-0062). It owns no thread, socket,
/// clock or random source: everything it does is a pure function of the inputs it is given.
/// </summary>
/// <remarks>
/// Two instances share nothing but code (core-architecture.md 6 rule 12), so a simulation can run
/// several in one process.
/// </remarks>
public sealed class Node
{
    private readonly CoreContext _context;
    private readonly Dispatcher _dispatcher;

    /// <summary>Creates a node at time 0.</summary>
    /// <param name="config">The configuration.</param>
    /// <param name="prngSeed">The SplitMix64 seed (prng.md 3); a run-time configuration change never re-seeds.</param>
    /// <param name="modem">The modem port, used only for <see cref="IModemPort.PlanBurst"/>.</param>
    public Node(NodeConfig config, ulong prngSeed, IModemPort modem)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(modem);
        _context = new CoreContext(config, modem);
        var mac = new MacSublayer(_context, new SplitMix64(prngSeed));
        var dts = new DtsSublayer(_context, mac);
        mac.Transmitter = dts.Transmitter;
        _dispatcher = new Dispatcher(_context, new SisSublayer(_context), new CasSublayer(_context), dts, mac);
    }

    /// <summary>The time of the last input handled, in ms since creation.</summary>
    public long Now => _context.Now;

    /// <summary>
    /// Handles one input at its time, running to completion. Every output it causes is appended
    /// to the output list, in the order produced. An exception means a bug or an unusable
    /// configuration, never a protocol event (core-architecture.md 6 rule 9).
    /// </summary>
    /// <param name="input">The input; its time must not be earlier than the previous input's.</param>
    public void Handle(CoreInput input) => _dispatcher.Handle(input);

    /// <summary>Returns and clears the outputs produced since the last call.</summary>
    public List<CoreOutput> DrainOutputs() => _context.TakeOutputs();
}
