using System.Net;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Host.Modem;
using M0LTE.S5066.Host.Sis;

namespace M0LTE.S5066.Host;

/// <summary>Options for a <see cref="NodeHost"/>.</summary>
public sealed record NodeHostOptions
{
    /// <summary>
    /// Where the SIS server listens: by default 127.0.0.1 port 5066 (S-0007, S-0009; localhost by
    /// default, plan 10.1). Another port or address may be configured (S-0008); port 0 picks a free
    /// port (see <see cref="NodeHost.SisEndpoint"/>). Null runs no SIS server.
    /// </summary>
    public IPEndPoint? SisEndpoint { get; init; } = new(IPAddress.Loopback, 5066);

    /// <summary>A sink for one-line log messages, or null.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>
    /// A sink for the per-burst trace (issue #86), or null for none: each D_PDU sent and received,
    /// timers started, fired and cancelled, link, MAC and inter-sublayer changes, and each burst's
    /// planned and actual timing. Diagnostics only; it changes nothing the node does.
    /// </summary>
    public Action<string>? Trace { get; init; }

    /// <summary>
    /// The interp S-0029 write bound. Fixed in production at <see cref="SisServer.DefaultWriteBound"/>;
    /// tests shorten it.
    /// </summary>
    internal TimeSpan SisWriteBound { get; init; } = SisServer.DefaultWriteBound;

    /// <summary>
    /// The interp S-0029 byte bound. Fixed in production at <see cref="SisServer.DefaultQueueByteBound"/>;
    /// tests lower it.
    /// </summary>
    internal long SisQueueByteBound { get; init; } = SisServer.DefaultQueueByteBound;
}

/// <summary>
/// Hosts one STANAG 5066 node in real time (core-architecture.md 12): the sans-IO
/// <see cref="Node"/> on one serialising loop with a host clock and timer table, a modem port and
/// the Annex S server.
/// </summary>
/// <remarks>
/// Every call on the core happens on the loop, one input at a time, in the order of
/// corpus-format.md 4.5 (core-architecture.md 12.4). An exception from the core is a bug; the host
/// stops and <see cref="Completion"/> faults with it.
/// </remarks>
public sealed class NodeHost : IAsyncDisposable
{
    private readonly NodeHostOptions _options;
    private readonly IHostModem _modem;
    private readonly InputScheduler _scheduler = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly long _startTimestamp;
    private readonly long _offsetMs;
    private Node _node;
    private SisServer? _server;
    private Task? _loop;
    private bool _shutdownRequested;
    private TaskCompletionSource? _shutdownDone;
    private readonly HostTrace? _trace;

    /// <summary>
    /// Creates a host. The configuration's <c>time.*</c> is replaced with the UTC second at which
    /// host time 0 falls (core-architecture.md 12.2), and its <c>drc.table</c> with the calibrated
    /// table for the modem, when there is one (<see cref="CalibrationFor"/>).
    /// </summary>
    /// <param name="config">The node configuration (from <see cref="ConfigReader"/> or <see cref="HostConfig"/>).</param>
    /// <param name="prngSeed">The PRNG seed (prng.md 3).</param>
    /// <param name="modem">The modem port.</param>
    /// <param name="options">Host options, or null for the defaults.</param>
    public NodeHost(NodeConfig config, ulong prngSeed, IHostModem modem, NodeHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(modem);
        CheckConfig(config, modem);
        _options = options ?? new NodeHostOptions();
        _modem = modem;
        LogWarnings(config);

        DateTimeOffset utc = TimeProvider.System.GetUtcNow();
        _startTimestamp = TimeProvider.System.GetTimestamp();
        var second = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, TimeSpan.Zero);
        _offsetMs = (long)(utc - second).TotalMilliseconds;
        Epoch = second;
        Calibration = CalibrationFor(modem, _options.Log);
        Config = WithCalibration(config, Calibration) with { Time = new TimeConfig(second.Year, second.DayOfYear, (int)second.TimeOfDay.TotalSeconds) };
        _node = new Node(Config, prngSeed, modem);
        if (_options.Trace is { } sink)
        {
            _trace = new HostTrace(sink, modem);
            _node.Trace = line => _trace.CoreLine(_node.Now, line);
            IHostModem inner = modem;
            while (inner is IHostModemWrapper wrapper)
            {
                inner = wrapper.Inner;
            }

            if (inner is Ms110dHostModem m)
            {
                m.Trace = line => _trace.Modem(Now, line);
            }
        }
    }

    /// <summary>The UTC instant of host and core time 0.</summary>
    public DateTimeOffset Epoch { get; }

    /// <summary>The configuration in force, with <c>time.*</c> and <c>drc.table</c> set by the host.</summary>
    public NodeConfig Config { get; private set; }

    /// <summary>
    /// The calibrated <c>drc.table</c> the host loaded over the configured one, or null when it loaded
    /// none (a modem with no calibration, or a calibration measured on another version).
    /// </summary>
    public CalibratedTable? Calibration { get; }

    /// <summary>
    /// The calibration a host loads for a modem (core-architecture.md 9, phase 7, design shape item 6):
    /// <see cref="CalibratedTable.Default"/> when the modem is M0LTE.Ms110d at the version it was
    /// measured on, otherwise none. A calibration of another version is refused with a warning, and
    /// the configured <c>drc.table</c> (empty by default: no rate selection) stays.
    /// </summary>
    /// <param name="modem">The modem the node will run on.</param>
    /// <param name="log">A sink for the one-line warning when a calibration is refused, or null.</param>
    public static CalibratedTable? CalibrationFor(IModemPort modem, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(modem);
        IModemPort inner = modem;
        while (inner is IHostModemWrapper wrapper)
        {
            inner = wrapper.Inner;
        }

        if (inner is not Ms110dHostModem)
        {
            return null;
        }

        CalibratedTable c = CalibratedTable.Default;
        if (!c.Matches(Ms110dHostModem.ModemPackage, Ms110dHostModem.ModemVersion))
        {
            log?.Invoke($"warning: drc.table calibration {c.Calibration} is for {c.ModemPackage} {c.ModemVersion}, not {Ms110dHostModem.ModemVersion}; not loaded; the configured drc.table stays (empty: no rate selection)");
            return null;
        }

        return c;
    }

    private static NodeConfig WithCalibration(NodeConfig config, CalibratedTable? calibration) =>
        calibration is null ? config : config with { Drc = config.Drc with { Table = calibration.Table } };

    /// <summary>The SIS endpoint actually listening, once started, or null.</summary>
    public IPEndPoint? SisEndpoint => _server?.LocalEndpoint;

    /// <summary>Completes when the host stops; faults if the core threw.</summary>
    public Task Completion => _loop ?? Task.CompletedTask;

    /// <summary>
    /// A link changed state (the core's <c>link_state</c> output). Raised on the host's loop: keep
    /// handlers short and never call back into the host from one.
    /// </summary>
    public event Action<uint, LinkStateName>? LinkStateChanged;

    /// <summary>Every core output, as produced, on the loop. For tests and diagnostics.</summary>
    public event Action<CoreOutput>? OutputProduced;

    /// <summary>Host time now: ms since <see cref="Epoch"/>.</summary>
    public long Now => _offsetMs + (long)TimeProvider.System.GetElapsedTime(_startTimestamp).TotalMilliseconds;

    /// <summary>
    /// Refuses a configuration a hosted node would accept and then stop on (issue #83). The core reads
    /// some settings only when an exchange needs them, so that a T2 scenario fails with
    /// <c>missing_setting</c> only if it exercises the setting (corpus-format.md section 6), and an
    /// exception from the core stops the host. A hosted node meets every exchange sooner or later, so
    /// the host asks at load for the settings the standard leaves to the operator with no value
    /// (interp B-0454): <c>csma.mode</c> (decision D7, needed by every transmission),
    /// <c>csma.node_slot_position</c> when slotted, in 1 to <c>csma.num_cont_slots</c> (K-0104), <c>cas.max_links</c> (B-0193, needed when
    /// any peer calls), <c>cas.link_request_retries</c> (B-0222) and <c>cas.break_retries</c> (B-0288).
    /// The timers the core derives when null (B-0420, A-0321, C-1120) need nothing here.
    /// </summary>
    /// <param name="config">The node configuration.</param>
    /// <exception cref="ConfigException">A setting a node needs is not set (<see cref="MissingSettingException"/>) or the slot position is out of range; the message names it.</exception>
    public static void CheckConfig(NodeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Csma.Mode is null)
        {
            throw new MissingSettingException("csma.mode", "not set; add jitter, slotted or ed3");
        }

        if (config.Csma.Mode == CsmaMode.Slotted && config.Csma.NodeSlotPosition is null)
        {
            throw new MissingSettingException("csma.node_slot_position", $"not set; slotted mode needs this node's slot, 1 to {config.Csma.NumContSlots}");
        }

        if (config.Csma.Mode == CsmaMode.Slotted && config.Csma.NodeSlotPosition is int p && (p < 1 || p > config.Csma.NumContSlots))
        {
            // The core checks the range only at the first slotted contention (K-0104).
            throw new ConfigException("csma.node_slot_position", $"must be 1 to csma.num_cont_slots ({config.Csma.NumContSlots}), got {p}");
        }

        if (config.Cas.MaxLinks is null)
        {
            throw new MissingSettingException("cas.max_links", "not set; add the most links this node keeps at once, 1 or more");
        }

        if (config.Cas.LinkRequestRetries is null)
        {
            throw new MissingSettingException("cas.link_request_retries", "not set; add how many times an unanswered link request is repeated, 0 or more");
        }

        if (config.Cas.BreakRetries is null)
        {
            throw new MissingSettingException("cas.break_retries", "not set; add how many times an unanswered link break is repeated, 0 or more");
        }
    }

    /// <summary>
    /// <see cref="CheckConfig(NodeConfig)"/>, then that <paramref name="modem"/> can plan a burst at
    /// <c>drc.default_speed</c>, the speed of every transmission until phase 7 (core-architecture.md
    /// 5.3): the core plans one only when it first transmits, and a speed the modem refuses then
    /// stops the node (issue #83). A contention slot no longer than the modem's preamble is not
    /// refused (see <see cref="ConfigWarnings"/>).
    /// </summary>
    /// <param name="config">The node configuration.</param>
    /// <param name="modem">The modem the node will run on.</param>
    /// <exception cref="ConfigException">A setting is missing, or the modem cannot send at the default speed.</exception>
    public static void CheckConfig(NodeConfig config, IModemPort modem)
    {
        CheckConfig(config);
        ArgumentNullException.ThrowIfNull(modem);
        _ = PlanDefaultSpeed(config, modem);
    }

    /// <summary>
    /// Settings <see cref="CheckConfig(NodeConfig, IModemPort)"/> accepts but that are probably wrong,
    /// one line each; <see cref="NodeHost"/> writes them to <see cref="NodeHostOptions.Log"/> when it
    /// is made and on <see cref="UpdateConfig"/>. Today one: a <c>csma.cont_slot_width_ms</c> no longer
    /// than the modem's preamble at <c>drc.default_speed</c>. K-0048 wants the slot longer than the
    /// whole channel-sensing delay, and the modem declares carrier only once the preamble is in, so
    /// with the modem alone such slots make two contending nodes collide whatever slots they draw
    /// (issue #86). It is a warning, not a refusal, because an external busy input (K-0006), ORed
    /// into DCD, can sense a burst sooner than the modem, and the host cannot know that delay.
    /// </summary>
    /// <param name="config">The node configuration, already accepted by <see cref="CheckConfig(NodeConfig, IModemPort)"/>.</param>
    /// <param name="modem">The modem the node will run on.</param>
    /// <returns>The warnings, empty when there are none.</returns>
    public static IReadOnlyList<string> ConfigWarnings(NodeConfig config, IModemPort modem)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(modem);
        BurstPlan plan = PlanDefaultSpeed(config, modem);
        var warnings = new List<string>();
        if (config.Csma.ContSlotWidthMs <= plan.PreambleMs)
        {
            warnings.Add($"warning: csma.cont_slot_width_ms {config.Csma.ContSlotWidthMs} is not longer than this modem's {plan.PreambleMs} ms preamble at drc.default_speed; slots shorter than the time the far station needs to detect a burst cause collisions (K-0048), so use a longer slot unless an external busy input senses bursts sooner");
        }

        return warnings;
    }

    private static BurstPlan PlanDefaultSpeed(NodeConfig config, IModemPort modem)
    {
        Speed speed = config.Drc.DefaultSpeed;
        try
        {
            return modem.PlanBurst(speed.Wn, speed.Interleaver, 0);
        }
        catch (ArgumentException e)
        {
            throw new ConfigException("drc.default_speed", $"wn {speed.Wn} {InterleaverNames.ToName(speed.Interleaver)} is not a speed this modem can send ({e.Message})");
        }
    }

    private void LogWarnings(NodeConfig config)
    {
        foreach (string line in ConfigWarnings(config, _modem))
        {
            _options.Log?.Invoke(line);
        }
    }

    /// <summary>Starts the modem, the SIS server and the loop.</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("already started");
        }

        if (_options.SisEndpoint is { } ep)
        {
            _server = new SisServer(ep, new SisEvents(this), _options.SisWriteBound, _options.SisQueueByteBound, _options.Log);
            _server.Start();
            _options.Log?.Invoke($"SIS listening on {_server.LocalEndpoint}");
        }

        _modem.Start(new ModemEvents(this));
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>
    /// Replaces the configuration at run time, as a class 0 input (core-architecture.md 7.3, 12.4).
    /// <c>time.*</c> and <c>drc.table</c> are kept as the host set them.
    /// </summary>
    public void UpdateConfig(NodeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        CheckConfig(config, _modem);
        LogWarnings(config);
        NodeConfig c = WithCalibration(config, Calibration) with { Time = Config.Time };
        Post(InputClass.Config, t => new ConfigInput(t, c));
        Config = c;
    }

    /// <summary>
    /// Shuts the node down: every bound client is sent S_UNBIND_INDICATION reason 6 and closed
    /// (interp A-0059), a client that never bound is closed too, what is queued to clients is
    /// flushed within the S-0029 bound, then the server and loop stop.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_loop is not null && !_loop.IsCompleted)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _shutdownDone = done;
                _shutdownRequested = true;
            }

            _wake.Release();
            await Task.WhenAny(done.Task, _loop).ConfigureAwait(false);
            if (_server is not null)
            {
                // The core closes only the connections it has bound; close the rest, so the drain
                // waits only for real output and not for a client that never bound (issue #80).
                _server.CloseAll();
                await _server.DrainAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        _wake.Release();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception) when (_loop.IsFaulted)
            {
                // Reported through Completion.
            }
        }

        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }

        if (_modem is IAsyncDisposable ad)
        {
            await ad.DisposeAsync().ConfigureAwait(false);
        }
        else if (_modem is IDisposable d)
        {
            d.Dispose();
        }

        _stop.Dispose();
    }

    private void Post(InputClass cls, Func<long, CoreInput> make)
    {
        lock (_gate)
        {
            _scheduler.Post(cls, make, Now);
        }

        _wake.Release();
    }

    private async Task LoopAsync()
    {
        CancellationToken ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            CoreInput? input;
            long? next;
            bool shutdown;
            long now;
            lock (_gate)
            {
                now = Now;
                input = _scheduler.TakeNext(now, out next);
                shutdown = input is null && _shutdownRequested;
            }

            if (input is not null)
            {
                _trace?.Input(input, now);
                _node.Handle(input);
                Dispatch(_node.DrainOutputs());
                continue;
            }

            if (shutdown)
            {
                lock (_gate)
                {
                    _shutdownRequested = false;
                }

                _node.Shutdown(Math.Max(now, _scheduler.LastTaken));
                Dispatch(_node.DrainOutputs());
                _shutdownDone?.TrySetResult();
                return;
            }

            int waitMs = next is long d ? (int)Math.Clamp(d - now, 1, int.MaxValue) : Timeout.Infinite;
            try
            {
                await _wake.WaitAsync(waitMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Dispatch(List<CoreOutput> outputs)
    {
        foreach (CoreOutput o in outputs)
        {
            _trace?.Output(o, _node.Now);
            OutputProduced?.Invoke(o);
            switch (o)
            {
                case TimerStart or TimerCancel:
                    lock (_gate)
                    {
                        _scheduler.Apply(o);
                    }

                    break;
                case Transmit t:
                    _modem.Transmit(t.Bytes, t.Wn, t.Interleaver, t.K, t.Eom);
                    break;
                case TerminateReceive:
                    _modem.TerminateReceive();
                    break;
                case SetMaxInputBlocks m:
                    _modem.SetMaxInputBlocks(m.N);
                    break;
                case SisBytesOut b:
                    _server?.Send(b.Conn, b.Bytes);
                    break;
                case SisClose c:
                    _server?.Close(c.Conn);
                    break;
                case LinkState l:
                    _options.Log?.Invoke($"link {Core.Dts.DpduAddress.ToDotted(l.Peer)} {l.State}");
                    LinkStateChanged?.Invoke(l.Peer, l.State);
                    break;
            }
        }
    }

    private sealed class SisEvents(NodeHost host) : ISisServerEvents
    {
        public void Connected(int conn) => host.Post(InputClass.Sis, t => new SisConnect(t, conn));

        public void Received(int conn, ReadOnlyMemory<byte> bytes) => host.Post(InputClass.Sis, t => new SisBytes(t, conn, bytes));

        public void Disconnected(int conn) => host.Post(InputClass.Sis, t => new SisDisconnect(t, conn));
    }

    private sealed class ModemEvents(NodeHost host) : IModemEvents
    {
        public void BurstStarted(int? blockMs) => host.Post(InputClass.Modem, t => new Core.Runtime.BurstStarted(t, blockMs));

        public void BytesReceived(ReadOnlyMemory<byte> bytes) => host.Post(InputClass.Modem, t => new Core.Runtime.BytesReceived(t, bytes));

        public void BurstEnded(BurstEndReason reason, int wn, Interleaver interleaver, int k, int? snrTenthDb, int blocks) =>
            host.Post(InputClass.Modem, t => new Core.Runtime.BurstEnded(t, reason, wn, interleaver, k, snrTenthDb, blocks));

        public void TransmitDone() => host.Post(InputClass.Modem, t => new Core.Runtime.TransmitDone(t));

        public void ChannelBusy(bool busy) => host.Post(InputClass.Modem, t => new Core.Runtime.ChannelBusy(t, busy));
    }
}
