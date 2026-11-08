namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// The shared services every sublayer reads through (core-architecture.md 1): the clock, the
/// configuration, the timer table, the output list, the primitive FIFO, the handle counter and the
/// modem port. The PRNG is deliberately not here: only the MAC is given it, so the one registered
/// draw site (<c>csma.jitter</c>, prng.md 6) is the only place a draw can happen.
/// </summary>
internal sealed class CoreContext
{
    private readonly List<CoreOutput> _outputs = [];
    private readonly Queue<InternalPrimitive> _fifo = new();
    private int _nextHandle = 1;

    public CoreContext(NodeConfig config, IModemPort modem)
    {
        Config = config;
        Modem = modem;
        Timers = new TimerTable(this);
    }

    /// <summary>The <c>t</c> of the input being handled. No sublayer stores a time it did not get from here.</summary>
    public long Now { get; internal set; }

    /// <summary>The configuration in force. Read when used, never cached (core-architecture.md 7.3).</summary>
    public NodeConfig Config { get; internal set; }

    public TimerTable Timers { get; }

    /// <summary>The per-peer facts CAS-1 negotiation establishes: written by the CAS, read by the DTS (core-architecture.md 4.2).</summary>
    public PeerTable Peers { get; } = new();

    public IModemPort Modem { get; }

    /// <summary>
    /// The receive quality record per sender (C-0833), in address order: written only by the DTS
    /// receiver, which attaches its own table here at construction, and read by the SIS for the node
    /// information message (S-0038; interp S-0164). Like <see cref="Peers"/>, a shared read, never a call.
    /// </summary>
    public IReadOnlyDictionary<uint, RxQuality> RxQuality { get; internal set; } = new SortedDictionary<uint, RxQuality>();

    /// <summary>
    /// Per sender, the last burst in which it addressed this node, in address order; kept by the DTS
    /// receiver and read by the SIS (interp A-0320).
    /// </summary>
    public IReadOnlyDictionary<uint, HeardBurst> HeardToUs { get; internal set; } = new SortedDictionary<uint, HeardBurst>();

    /// <summary>
    /// The waveform of the last transmission built, or null before the first: written by the DTS
    /// planner, read by the SIS for the node information message (interp S-0164; P7-7).
    /// </summary>
    public Speed? LastTransmitSpeed { get; internal set; }

    /// <summary>
    /// Per individual peer, in address order, the waveform of the last transmission that carried a
    /// D_PDU to it: written by the DTS planner, read by the SIS (interp S-0164; P7-7).
    /// </summary>
    public SortedDictionary<uint, Speed> TransmitSpeeds { get; } = new();

    /// <summary>
    /// A diagnostic sink (issue #86), or null. It sees internal state changes the T2 outputs do not
    /// carry; it is never read by protocol code and changes no output.
    /// </summary>
    public Action<string>? Trace { get; set; }

    /// <summary>Set by the dispatcher: drains the FIFO on the MAC's request during AfterInput.</summary>
    internal Action? DrainHandler { get; set; }

    internal bool HasPendingPrimitives => _fifo.Count > 0;

    internal int PendingCount => _fifo.Count;

    internal int OutputCount => _outputs.Count;

    /// <summary>Appends an output the moment it is produced (core-architecture.md 6 rule 6).</summary>
    public void Emit(CoreOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _outputs.Add(output);
    }

    /// <summary>Queues a primitive for another sublayer; the dispatcher delivers it in FIFO order.</summary>
    public void Enqueue(InternalPrimitive primitive)
    {
        ArgumentNullException.ThrowIfNull(primitive);
        _fifo.Enqueue(primitive);
    }

    /// <summary>Allocates the next request handle (core-architecture.md 4.1, section 10 item 10). Never on the wire.</summary>
    public int AllocateHandle() => _nextHandle++;

    /// <summary>Asks the dispatcher to drain the FIFO now. Allowed only inside the MAC's AfterInput.</summary>
    public void DrainPrimitives() =>
        (DrainHandler ?? throw new InvalidOperationException("no dispatcher is attached"))();

    internal bool TryDequeue(out InternalPrimitive primitive) => _fifo.TryDequeue(out primitive!);

    internal List<CoreOutput> TakeOutputs()
    {
        var taken = new List<CoreOutput>(_outputs);
        _outputs.Clear();
        return taken;
    }
}
