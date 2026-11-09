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
    private readonly DtsSublayer _dts;

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
        _dts = dts;
        mac.Transmitter = dts.Transmitter;
        _dispatcher = new Dispatcher(_context, new SisSublayer(_context), new CasSublayer(_context), dts, mac);
    }

    /// <summary>
    /// A diagnostic sink for internal state changes (primitives between sublayers, MAC states, ARQ
    /// window edges), or null (issue #86). One ASCII line each, without a time; the caller knows the
    /// time of the input being handled. Never part of the T2 outputs.
    /// </summary>
    public Action<string>? Trace
    {
        get => _context.Trace;
        set => _context.Trace = value;
    }

    /// <summary>
    /// Failed trials since creation, each of which set a failed-trial ceiling (interp C-1281). A
    /// diagnostic for the stack sweep (open question 14), never part of the T2 outputs.
    /// </summary>
    internal long FailedTrials => _dts.Peers.FailedTrials;

    /// <summary>The time of the last input handled, in ms since creation.</summary>
    public long Now => _context.Now;

    /// <summary>
    /// Handles one input at its time, running to completion. Every output it causes is appended
    /// to the output list, in the order produced. An exception means a bug or an unusable
    /// configuration, never a protocol event (core-architecture.md 6 rule 9).
    /// </summary>
    /// <param name="input">The input; its time must not be earlier than the previous input's.</param>
    public void Handle(CoreInput input) => _dispatcher.Handle(input);

    /// <summary>
    /// Shuts the SIS service down, the last call before the host destroys the core (interp A-0059;
    /// core-architecture.md 10 item 1): every bound client is sent S_UNBIND_INDICATION reason 6,
    /// and every connection, bound or not, is closed, in connection order. Handled like an input at
    /// time <paramref name="t"/>, so its outputs are drained with <see cref="DrainOutputs"/>.
    /// </summary>
    /// <param name="t">The time, not earlier than the previous input's.</param>
    public void Shutdown(long t) => _dispatcher.Handle(new ShutdownInput(t));

    /// <summary>Returns and clears the outputs produced since the last call.</summary>
    public List<CoreOutput> DrainOutputs() => _context.TakeOutputs();
}
