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
    /// The interp S-0029 write bound. Fixed in production at <see cref="SisServer.DefaultWriteBound"/>;
    /// tests shorten it.
    /// </summary>
    internal TimeSpan SisWriteBound { get; init; } = SisServer.DefaultWriteBound;
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

    /// <summary>
    /// Creates a host. The configuration's <c>time.*</c> is replaced with the UTC second at which
    /// host time 0 falls (core-architecture.md 12.2).
    /// </summary>
    /// <param name="config">The node configuration (from <see cref="ConfigReader"/> or <see cref="HostConfig"/>).</param>
    /// <param name="prngSeed">The PRNG seed (prng.md 3).</param>
    /// <param name="modem">The modem port.</param>
    /// <param name="options">Host options, or null for the defaults.</param>
    public NodeHost(NodeConfig config, ulong prngSeed, IHostModem modem, NodeHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(modem);
        _options = options ?? new NodeHostOptions();
        _modem = modem;

        DateTimeOffset utc = TimeProvider.System.GetUtcNow();
        _startTimestamp = TimeProvider.System.GetTimestamp();
        var second = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, TimeSpan.Zero);
        _offsetMs = (long)(utc - second).TotalMilliseconds;
        Epoch = second;
        Config = config with { Time = new TimeConfig(second.Year, second.DayOfYear, (int)second.TimeOfDay.TotalSeconds) };
        _node = new Node(Config, prngSeed, modem);
    }

    /// <summary>The UTC instant of host and core time 0.</summary>
    public DateTimeOffset Epoch { get; }

    /// <summary>The configuration in force, with <c>time.*</c> set by the host.</summary>
    public NodeConfig Config { get; private set; }

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

    /// <summary>Starts the modem, the SIS server and the loop.</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("already started");
        }

        if (_options.SisEndpoint is { } ep)
        {
            _server = new SisServer(ep, new SisEvents(this), _options.SisWriteBound, _options.Log);
            _server.Start();
            _options.Log?.Invoke($"SIS listening on {_server.LocalEndpoint}");
        }

        _modem.Start(new ModemEvents(this));
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>
    /// Replaces the configuration at run time, as a class 0 input (core-architecture.md 7.3, 12.4).
    /// <c>time.*</c> is kept as the host set it.
    /// </summary>
    public void UpdateConfig(NodeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        NodeConfig c = config with { Time = Config.Time };
        Post(InputClass.Config, t => new ConfigInput(t, c));
        Config = c;
    }

    /// <summary>
    /// Shuts the node down: every bound client is sent S_UNBIND_INDICATION reason 6 and closed
    /// (interp A-0059), what is queued to clients is flushed within the S-0029 bound, then the
    /// server and loop stop.
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
        public void BurstStarted() => host.Post(InputClass.Modem, t => new Core.Runtime.BurstStarted(t));

        public void BytesReceived(ReadOnlyMemory<byte> bytes) => host.Post(InputClass.Modem, t => new Core.Runtime.BytesReceived(t, bytes));

        public void BurstEnded(BurstEndReason reason, int wn, Interleaver interleaver, int k, int? snrTenthDb, int blocks) =>
            host.Post(InputClass.Modem, t => new Core.Runtime.BurstEnded(t, reason, wn, interleaver, k, snrTenthDb, blocks));

        public void TransmitDone() => host.Post(InputClass.Modem, t => new Core.Runtime.TransmitDone(t));

        public void ChannelBusy(bool busy) => host.Post(InputClass.Modem, t => new Core.Runtime.ChannelBusy(t, busy));
    }
}
