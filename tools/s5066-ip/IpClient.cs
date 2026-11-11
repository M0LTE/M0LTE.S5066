using System.Globalization;
using System.Threading.Channels;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Ip;

/// <summary>Counts of what the client did, for the log.</summary>
internal sealed class IpCounters
{
    private long _fromHost, _sentArq, _sentNonArq, _confirmed, _rejected, _sendFailed, _delivered, _icmpTooBig;
    private readonly long[] _dropped = new long[Enum.GetValues<DropReason>().Length];

    public long FromHost => Interlocked.Read(ref _fromHost);
    public long SentArq => Interlocked.Read(ref _sentArq);
    public long SentNonArq => Interlocked.Read(ref _sentNonArq);
    public long Confirmed => Interlocked.Read(ref _confirmed);
    public long Rejected => Interlocked.Read(ref _rejected);
    public long SendFailed => Interlocked.Read(ref _sendFailed);
    public long Delivered => Interlocked.Read(ref _delivered);
    public long IcmpTooBig => Interlocked.Read(ref _icmpTooBig);

    public long Dropped(DropReason r) => Interlocked.Read(ref _dropped[(int)r]);

    internal void CountFromHost() => Interlocked.Increment(ref _fromHost);
    internal void CountSent(bool arq) => _ = arq ? Interlocked.Increment(ref _sentArq) : Interlocked.Increment(ref _sentNonArq);
    internal void CountConfirmed() => Interlocked.Increment(ref _confirmed);
    internal void CountRejected() => Interlocked.Increment(ref _rejected);
    internal void CountSendFailed() => Interlocked.Increment(ref _sendFailed);
    internal void CountDelivered() => Interlocked.Increment(ref _delivered);
    internal void CountIcmpTooBig() => Interlocked.Increment(ref _icmpTooBig);
    internal void CountDrop(DropReason r) => Interlocked.Increment(ref _dropped[(int)r]);

    /// <summary>One ASCII line.</summary>
    public override string ToString() =>
        FormattableString.Invariant($"from host {FromHost}, sent arq {SentArq} non-arq {SentNonArq}, confirmed {Confirmed}, rejected {Rejected}, send failed {SendFailed}, ")
        + FormattableString.Invariant($"delivered to host {Delivered}, icmp too-big {IcmpTooBig}, dropped: ")
        + string.Join(", ", Enum.GetValues<DropReason>().Select(r => FormattableString.Invariant($"{Name(r)} {Dropped(r)}")));

    private static string Name(DropReason r) => r switch
    {
        DropReason.NotIp => "not ip",
        DropReason.NotAllowed => "not allowed",
        DropReason.NoRoute => "no route",
        DropReason.Multicast => "multicast",
        DropReason.QueueFull => "queue full",
        DropReason.TooBig => "too big",
        DropReason.ReceivedNotIp => "received not ip",
        _ => r.ToString(),
    };
}

/// <summary>
/// The Annex U IP client [Ed4 Annex U, p.U-1 to U-7]: datagrams from the host's router (the
/// packet device) go to SAP 9 of the remote node, one datagram per U_PDU (U-0006 to U-0010,
/// U-0029), with the per-datagram choices of <see cref="Policy"/>; U_PDUs received at SAP 9 go
/// to the host after a check that they are IP (interp U-0041). While the node has flow off, or
/// the client is not bound, datagrams wait in a bounded queue that drops the newest arrival when
/// full (U-0028, interp U-0040). The SIS connection is remade when it drops; the device stays.
/// </summary>
internal sealed class IpClient : IAsyncDisposable
{
    /// <summary>The wait before connecting again after the SIS connection dropped.</summary>
    public static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly IpConfig _config;
    private readonly IPacketDevice _device;
    private readonly Action<string> _log;
    private readonly Policy _policy;
    private readonly IcmpRateLimit _icmpLimit;
    private readonly Channel<Outbound> _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SisClient? _client;
    private int _mtu;
    private Thread? _reader;
    private Task? _pump;

    public IpClient(IpConfig config, IPacketDevice device, Action<string> log, TimeProvider? clock = null)
    {
        _config = config;
        _device = device;
        _log = log;
        _policy = new Policy(config);
        _icmpLimit = new IcmpRateLimit(clock ?? TimeProvider.System);
        _queue = Channel.CreateBounded<Outbound>(
            new BoundedChannelOptions(config.QueueLimit) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = true },
            _ => Counters.CountDrop(DropReason.QueueFull));
    }

    public IpCounters Counters { get; } = new();

    /// <summary>The MTU of the bind in force, or 0 when not bound.</summary>
    public int Mtu => Volatile.Read(ref _mtu);

    /// <summary>Datagrams waiting for the node (flow off or not bound).</summary>
    public int Queued => _queue.Reader.Count;

    /// <summary>
    /// Runs until cancelled: connects with <paramref name="connect"/>, binds SAP 9 (U-0012), and
    /// connects again after <see cref="ReconnectDelay"/> whenever the connection drops or the node
    /// unbinds. Each bind sets the device MTU to the node's MTU.
    /// </summary>
    public async Task RunAsync(Func<CancellationToken, Task<SisClient>> connect, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        CancellationToken ct = linked.Token;
        _reader = new Thread(() => ReadFromHost(ct)) { IsBackground = true, Name = "s5066-ip " + _device.Name };
        _reader.Start();
        _pump = Task.Run(() => PumpAsync(ct), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(connect, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or SisBindRejectedException or InvalidOperationException)
            {
                _log($"s5066-ip: node {_config.SisHost}:{_config.SisPort}: {e.Message}; trying again in {ReconnectDelay.TotalSeconds:0} s");
            }

            try
            {
                await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunSessionAsync(Func<CancellationToken, Task<SisClient>> connect, CancellationToken ct)
    {
        await using SisClient client = await connect(ct).ConfigureAwait(false);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        client.Unbound += reason =>
        {
            _log($"s5066-ip: the node unbound SAP {IpConfig.Sap}, reason {reason}");
            session.Cancel();
        };
        client.FlowChanged += _ => Signal();

        SisBindAccepted bound = await client.BindAsync(IpConfig.Sap, Policy.BindServiceType, ct).ConfigureAwait(false);
        _device.SetMtu(bound.Mtu);
        if (bound.Mtu < 1280)
        {
            _log($"s5066-ip: the node's MTU of {bound.Mtu} is below the IPv6 minimum of 1280; IPv6 will not work well");
        }

        Volatile.Write(ref _mtu, bound.Mtu);
        lock (_gate)
        {
            _client = client;
        }

        Signal();
        _log($"s5066-ip: bound to SAP {IpConfig.Sap} at {_config.SisHost}:{_config.SisPort}, MTU {bound.Mtu}; {_device.Name} MTU set to match");
        try
        {
            await foreach (SisUnidataIndication u in client.Received.ReadAllAsync(session.Token).ConfigureAwait(false))
            {
                Deliver(u);
            }

            _log("s5066-ip: the node closed the SIS connection");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_gate)
            {
                _client = null;
            }

            Volatile.Write(ref _mtu, 0);
            Signal();
        }
    }

    /// <summary>A U_PDU from SAP 9: to the host if it is one whole IP datagram (U-0006, interp U-0041).</summary>
    internal void Deliver(SisUnidataIndication u)
    {
        if (IpDatagram.TryParse(u.UPdu.Span) is null)
        {
            Counters.CountDrop(DropReason.ReceivedNotIp);
            return;
        }

        try
        {
            _device.Write(u.UPdu.Span);
            Counters.CountDelivered();
        }
        catch (IOException e)
        {
            _log($"s5066-ip: {e.Message}");
        }
    }

    private void ReadFromHost(CancellationToken ct)
    {
        byte[] buffer = new byte[65536];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                n = _device.Read(buffer, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (IOException e)
            {
                _log($"s5066-ip: {e.Message}");
                _stop.Cancel();
                return;
            }

            Offer(buffer.AsSpan(0, n));
        }
    }

    /// <summary>A datagram from the host: decided and queued, or dropped with a count.</summary>
    internal void Offer(ReadOnlySpan<byte> bytes)
    {
        Counters.CountFromHost();
        IpDatagram? d = IpDatagram.TryParse(bytes);
        if (d is null)
        {
            Counters.CountDrop(DropReason.NotIp);
            return;
        }

        (SendDecision? send, DropReason? drop) = _policy.Decide(d);
        if (send is null)
        {
            Counters.CountDrop(drop!.Value);
            return;
        }

        _queue.Writer.TryWrite(new Outbound(bytes.ToArray(), d, send));
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        while (await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            SisClient client = await ReadyAsync(ct).ConfigureAwait(false);
            if (!_queue.Reader.TryRead(out Outbound? o))
            {
                continue;
            }

            int mtu = Mtu;
            if (mtu > 0 && o.Bytes.Length > mtu)
            {
                TooBig(o, mtu);
                continue;
            }

            Send(client, o, ct);
        }
    }

    /// <summary>
    /// Starts one S_UNIDATA_REQUEST and counts how it ends, without waiting for a confirmation, so
    /// later datagrams are not held behind it. Requests are written in the order they start.
    /// </summary>
    private void Send(SisClient client, Outbound o, CancellationToken ct)
    {
        bool arq = o.Decision.Mode.TxMode == Policy.Arq.TxMode;
        var send = new UnidataSend(o.Decision.Destination, IpConfig.Sap, o.Bytes)
        {
            Priority = o.Decision.Priority,
            DeliveryMode = o.Decision.Mode,
        };
        Task<SendResult> t;
        try
        {
            t = client.SendAsync(send, ct);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Counters.CountSendFailed();
            return;
        }

        Counters.CountSent(arq);
        _ = t.ContinueWith(r =>
        {
            if (r.IsCompletedSuccessfully)
            {
                switch (r.Result.Status)
                {
                    case SendStatus.Confirmed:
                        Counters.CountConfirmed();
                        break;
                    case SendStatus.Rejected:
                        Counters.CountRejected();
                        break;
                }
            }
            else if (!r.IsCanceled)
            {
                Counters.CountSendFailed();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// A datagram over the MTU is never split across U_PDUs (U-0029). IPv4 with DF and IPv6 draw
    /// an ICMP too-big with the MTU (U-0031, U-0032); IPv4 without DF is dropped and counted, as
    /// fragmenting is left to the router (U-0030, interp U-0037).
    /// </summary>
    private void TooBig(Outbound o, int mtu)
    {
        byte[]? answer = o.Datagram.Version == 6 || o.Datagram.DontFragment ? Icmp.TooBig(o.Bytes, o.Datagram, mtu) : null;
        if (answer is null || !_icmpLimit.TryTake())
        {
            Counters.CountDrop(DropReason.TooBig);
            return;
        }

        try
        {
            _device.Write(answer);
            Counters.CountIcmpTooBig();
        }
        catch (IOException e)
        {
            _log($"s5066-ip: {e.Message}");
        }
    }

    /// <summary>Waits until bound with flow on (S-0020, S-0021) and returns the client.</summary>
    private async Task<SisClient> ReadyAsync(CancellationToken ct)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_client is { Binding: not null, FlowOn: true } c)
                {
                    return c;
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private void Signal()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            old = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        old.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _reader?.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }

    private sealed record Outbound(byte[] Bytes, IpDatagram Datagram, SendDecision Decision);
}
