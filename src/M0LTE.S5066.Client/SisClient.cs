using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Client;

/// <summary>How a send ended.</summary>
public enum SendStatus
{
    /// <summary>Written to the node; no confirmation was asked for.</summary>
    Sent,

    /// <summary>The node sent S_UNIDATA_REQUEST_CONFIRM.</summary>
    Confirmed,

    /// <summary>The node sent S_UNIDATA_REQUEST_REJECTED; see <see cref="SendResult.Reason"/>.</summary>
    Rejected,
}

/// <summary>The outcome of <see cref="SisClient.SendAsync"/>.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Reason">For <see cref="SendStatus.Rejected"/>, the A.2.2.8 reason (1 to 15); otherwise 0.</param>
public sealed record SendResult(SendStatus Status, byte Reason = 0);

/// <summary>A U_PDU to send.</summary>
/// <param name="Destination">The destination node or group address.</param>
/// <param name="DestSap">The destination SAP, 0 to 15.</param>
/// <param name="UPdu">The U_PDU, at most the MTU the node gave at bind.</param>
public sealed record UnidataSend(SisAddress Destination, byte DestSap, ReadOnlyMemory<byte> UPdu)
{
    /// <summary>SIS priority, 0 to 15.</summary>
    public byte Priority { get; init; }

    /// <summary>The DELIVERY MODE. <see cref="ServiceType.Default"/> uses the service type of the bind (S-0142).</summary>
    public ServiceType DeliveryMode { get; init; } = ServiceType.Default;

    /// <summary>TTL in units of 2 s as sent (S-0116); 0 for the node's default.</summary>
    public uint Ttl { get; init; }
}

/// <summary>The node refused a bind.</summary>
/// <param name="reason">The S_BIND_REJECTED reason [Ed4 A.2.1.1].</param>
public sealed class SisBindRejectedException(byte reason)
    : Exception($"bind rejected, reason {reason}")
{
    /// <summary>The reason.</summary>
    public byte Reason { get; } = reason;
}

/// <summary>
/// A client of a STANAG 5066 node's SIS over TCP (Annex S). It reads the connection all the time,
/// whatever the application does with what arrives (S-0024, S-0025), answers keep-alives, follows
/// the node's flow control (S-0020, S-0021), and matches delivery confirmations to sends.
/// </summary>
/// <remarks>
/// Received U_PDUs and management messages are queued without bound in <see cref="Received"/> and
/// <see cref="ManagementMessages"/>, so a slow application never stops the client reading: the
/// node may disconnect a client that does not read (S-0026). Events are raised on the reading
/// task; keep handlers short. An exception thrown by a handler is caught and passed to
/// <see cref="HandlerFailed"/>, so it never stops the client reading.
/// </remarks>
public sealed class SisClient : IAsyncDisposable
{
    /// <summary>The default SIS port (S-0007).</summary>
    public const int DefaultPort = 5066;

    /// <summary>The least time between two keep-alives this client sends unprompted (S-0043, S-0044).</summary>
    public static readonly TimeSpan KeepAliveSpacing = TimeSpan.FromSeconds(120);

    private readonly Socket _socket;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _gate = new();
    private readonly Channel<SisUnidataIndication> _received = Channel.CreateUnbounded<SisUnidataIndication>();
    private readonly Channel<SisManagementMsgIndication> _management = Channel.CreateUnbounded<SisManagementMsgIndication>();
    private readonly List<PendingSend> _pending = [];
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _readLoop;
    private TaskCompletionSource<SisBindAccepted>? _bind;
    private TaskCompletionSource _flowOn = NewOpenGate();
    private long _lastKeepAliveSentMs = long.MinValue / 2;
    private readonly long _startMs = Environment.TickCount64;

    private SisClient(Socket socket)
    {
        _socket = socket;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>The bind in force, or null when not bound.</summary>
    public SisBindAccepted? Binding { get; private set; }

    /// <summary>True while the node allows U_PDUs: from each bind (interp S-0027) until S_DATA_FLOW_OFF, and again after S_DATA_FLOW_ON.</summary>
    public bool FlowOn => _flowOn.Task.IsCompleted;

    /// <summary>The last S_SUBNET_AVAILABILITY received, or null (S-0048).</summary>
    public SisSubnetAvailability? SubnetAvailability { get; private set; }

    /// <summary>U_PDUs the node delivered to this client.</summary>
    public ChannelReader<SisUnidataIndication> Received => _received.Reader;

    /// <summary>Every S_MANAGEMENT_MSG_INDICATION received (S-0036), answers and unprompted alike.</summary>
    public ChannelReader<SisManagementMsgIndication> ManagementMessages => _management.Reader;

    /// <summary>Completes when the connection has closed, for any reason.</summary>
    public Task Closed => _closed.Task;

    /// <summary>
    /// The node unbound this client (S_UNBIND_INDICATION); the argument is the reason. Raised after
    /// <see cref="Binding"/> is cleared and before sends waiting for confirmation fail.
    /// </summary>
    public event Action<byte>? Unbound;

    /// <summary>The node sent S_DATA_FLOW_ON (true) or S_DATA_FLOW_OFF (false).</summary>
    public event Action<bool>? FlowChanged;

    /// <summary>An S_UNIDATA_REQUEST_REJECTED that matches no send waiting for confirmation.</summary>
    public event Action<SisUnidataRequestRejected>? UnmatchedRejection;

    /// <summary>
    /// A handler of <see cref="Unbound"/>, <see cref="FlowChanged"/> or <see cref="UnmatchedRejection"/>
    /// threw; the argument is the exception. The client goes on reading. An exception from a handler
    /// of this event is dropped.
    /// </summary>
    public event Action<Exception>? HandlerFailed;

    /// <summary>Connects to a node's SIS (S-0007; another port may be given, S-0008).</summary>
    public static async Task<SisClient> ConnectAsync(string host = "127.0.0.1", int port = DefaultPort, CancellationToken cancellationToken = default)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new SisClient(socket);
    }

    /// <summary>Connects to a node's SIS at an endpoint.</summary>
    public static Task<SisClient> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return ConnectAsync(endpoint.Address.ToString(), endpoint.Port, cancellationToken);
    }

    /// <summary>
    /// Binds to a SAP with a default service type (A-0030). To change the service type, unbind and
    /// bind again (S-0087).
    /// </summary>
    /// <exception cref="SisBindRejectedException">The node refused.</exception>
    public async Task<SisBindAccepted> BindAsync(byte sap, ServiceType serviceType, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<SisBindAccepted>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_bind is not null)
            {
                throw new InvalidOperationException("a bind is already in progress");
            }

            _bind = tcs;
        }

        try
        {
            await WriteAsync(new SisBindRequest(sap, serviceType), cancellationToken).ConfigureAwait(false);
            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate)
            {
                _bind = null;
            }
        }
    }

    /// <summary>Goes off-line (A-0036). The connection stays open; bind again to come back (A-0055).</summary>
    public async Task UnbindAsync(CancellationToken cancellationToken = default)
    {
        await WriteAsync(new SisUnbindRequest(), cancellationToken).ConfigureAwait(false);
        OnUnbound();
    }

    /// <summary>
    /// Sends a U_PDU. Waits while the node has flow off (S-0020, S-0021). When the delivery mode asks
    /// for confirmation, completes when the node confirms or rejects it; otherwise once written.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not bound.</exception>
    /// <exception cref="ArgumentException">The U_PDU is longer than the MTU of the bind (the client splits messages, A-0003).</exception>
    public async Task<SendResult> SendAsync(UnidataSend send, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(send);
        SisBindAccepted binding = Binding ?? throw new InvalidOperationException("not bound");
        if (send.UPdu.Length > binding.Mtu)
        {
            throw new ArgumentException($"U_PDU of {send.UPdu.Length} bytes is over the MTU of {binding.Mtu}", nameof(send));
        }

        while (true)
        {
            Task gate;
            lock (_gate)
            {
                gate = _flowOn.Task;
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            PendingSend? pending = null;
            bool confirm = send.DeliveryMode.Confirm != 0;
            lock (_gate)
            {
                if (!_flowOn.Task.IsCompleted)
                {
                    continue;
                }

                if (confirm)
                {
                    pending = new PendingSend(send);
                    _pending.Add(pending);
                }
            }

            var request = new SisUnidataRequest(send.Priority, send.DestSap, send.Destination, send.DeliveryMode, send.Ttl, send.UPdu);
            try
            {
                await WriteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Remove(pending);
                throw;
            }

            if (pending is null)
            {
                return new SendResult(SendStatus.Sent);
            }

            using (cancellationToken.Register(() =>
            {
                Remove(pending);
                pending.Result.TrySetCanceled(cancellationToken);
            }))
            {
                return await pending.Result.Task.ConfigureAwait(false);
            }
        }
    }

    /// <summary>Sends S_MANAGEMENT_MSG_REQUEST (S-0030). Any answer arrives in <see cref="ManagementMessages"/>.</summary>
    public Task SendManagementAsync(byte msgType, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) =>
        WriteAsync(new SisManagementMsgRequest(msgType, body), cancellationToken);

    /// <summary>
    /// Sends an unprompted S_KEEP_ALIVE, unless one was sent less than <see cref="KeepAliveSpacing"/>
    /// ago (S-0043, interp S-0044, S-0045). Returns false when it was not sent.
    /// </summary>
    public async Task<bool> ProbeAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (NowMs - _lastKeepAliveSentMs < (long)KeepAliveSpacing.TotalMilliseconds)
            {
                return false;
            }

            _lastKeepAliveSentMs = NowMs;
        }

        await WriteAsync(new SisKeepAlive(), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
        }

        _socket.Dispose();
        await _readLoop.ConfigureAwait(false);
        _writeLock.Dispose();
        _stop.Dispose();
    }

    private long NowMs => Environment.TickCount64 - _startMs;

    private static TaskCompletionSource NewOpenGate()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    private async Task WriteAsync(SisPrimitive primitive, CancellationToken cancellationToken)
    {
        byte[] bytes = SisCodec.Encode(primitive);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int sent = 0;
            while (sent < bytes.Length)
            {
                sent += await _socket.SendAsync(bytes.AsMemory(sent), SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void Remove(PendingSend? pending)
    {
        if (pending is null)
        {
            return;
        }

        lock (_gate)
        {
            _pending.Remove(pending);
        }
    }

    private async Task ReadLoopAsync()
    {
        var decoder = new SisFrameDecoder();
        var buffer = new byte[8192];
        Exception? error = null;
        try
        {
            while (true)
            {
                int n = await _socket.ReceiveAsync(buffer, SocketFlags.None, _stop.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                decoder.Push(buffer.AsSpan(0, n));
                while (decoder.TryRead(out SisDecodeResult r))
                {
                    if (r.Primitive is { } p)
                    {
                        await HandleAsync(p).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception e)
        {
            // A socket error or a close, or a fault of our own: either way the connection is done
            // and everything waiting on it must hear so.
            error = e;
            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception e2) when (e2 is SocketException or ObjectDisposedException)
            {
            }
        }

        var closed = new IOException("the SIS connection closed", error);
        PendingSend[] pending;
        TaskCompletionSource<SisBindAccepted>? bind;
        lock (_gate)
        {
            pending = [.. _pending];
            _pending.Clear();
            bind = _bind;
            Binding = null;
        }

        foreach (PendingSend p in pending)
        {
            p.Result.TrySetException(closed);
        }

        bind?.TrySetException(closed);
        _received.Writer.TryComplete();
        _management.Writer.TryComplete();
        _closed.TrySetResult();
    }

    private async Task HandleAsync(SisPrimitive p)
    {
        switch (p)
        {
            case SisUnidataIndication u:
                _received.Writer.TryWrite(u);
                break;
            case SisBindAccepted a:
                lock (_gate)
                {
                    Binding = a;
                    _flowOn = NewOpenGate();
                }

                _bind?.TrySetResult(a);
                break;
            case SisBindRejected r:
                _bind?.TrySetException(new SisBindRejectedException(r.Reason));
                break;
            case SisUnbindIndication u:
                // Handlers run before the sends waiting for confirmation fail, so code awaiting a
                // send sees the Unbound event already raised.
                OnUnbound(() => Raise(Unbound, u.Reason));
                break;
            case SisDataFlowOff:
                lock (_gate)
                {
                    if (_flowOn.Task.IsCompleted)
                    {
                        _flowOn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }

                Raise(FlowChanged, false);
                break;
            case SisDataFlowOn:
                lock (_gate)
                {
                    _flowOn.TrySetResult();
                }

                Raise(FlowChanged, true);
                break;
            case SisKeepAlive:
                // Answered at once, whatever the 120 s limit (S-0041, interp S-0044).
                lock (_gate)
                {
                    _lastKeepAliveSentMs = NowMs;
                }

                try
                {
                    await WriteAsync(new SisKeepAlive(), _stop.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
                {
                }

                break;
            case SisManagementMsgIndication m:
                _management.Writer.TryWrite(m);
                break;
            case SisSubnetAvailability s:
                SubnetAvailability = s;
                break;
            case SisUnidataRequestConfirm c:
                Match(c.DestSap, c.DestAddress, c.UPdu.Span)?.Result.TrySetResult(new SendResult(SendStatus.Confirmed));
                break;
            case SisUnidataRequestRejected r:
                if (Match(r.DestSap, r.DestAddress, r.UPdu.Span) is { } pending)
                {
                    pending.Result.TrySetResult(new SendResult(SendStatus.Rejected, r.Reason));
                }
                else
                {
                    Raise(UnmatchedRejection, r);
                }

                break;
        }
    }

    /// <summary>Calls each handler of an event in turn, catching what it throws (issue #80).</summary>
    private void Raise<T>(Action<T>? handlers, T arg)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action<T> h in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                h(arg);
            }
            catch (Exception e)
            {
                try
                {
                    HandlerFailed?.Invoke(e);
                }
                catch (Exception)
                {
                    // Dropped, as documented.
                }
            }
        }
    }

    private void OnUnbound(Action? beforeFailingSends = null)
    {
        PendingSend[] pending;
        lock (_gate)
        {
            Binding = null;
            _flowOn.TrySetResult();
            pending = [.. _pending];
            _pending.Clear();
        }

        beforeFailingSends?.Invoke();
        foreach (PendingSend p in pending)
        {
            p.Result.TrySetException(new InvalidOperationException("unbound before the send was confirmed"));
        }
    }

    /// <summary>
    /// The oldest send waiting for confirmation to the same SAP and address whose U_PDU starts with
    /// the copy the node returned (the copy may be the U_PDU's first bytes, S-0127, S-0129).
    /// </summary>
    private PendingSend? Match(byte destSap, SisAddress dest, ReadOnlySpan<byte> copy)
    {
        lock (_gate)
        {
            foreach (PendingSend p in _pending)
            {
                if (p.Send.DestSap == destSap && p.Send.Destination.Value == dest.Value && p.Send.UPdu.Span.StartsWith(copy))
                {
                    _pending.Remove(p);
                    return p;
                }
            }
        }

        return null;
    }

    private sealed class PendingSend(UnidataSend send)
    {
        public UnidataSend Send { get; } = send;

        public TaskCompletionSource<SendResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
