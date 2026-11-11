using System.Diagnostics;
using System.Threading.Channels;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Cftp;

/// <summary>The outcome of <see cref="CftpClient.SendAsync"/>.</summary>
/// <param name="Acknowledged">True when the far end's Message Acknowledgement came (V-0045).</param>
/// <param name="Attempts">How many times the file was sent (interp V-0076).</param>
/// <param name="Failure">Why it failed, when it did.</param>
/// <param name="CompressedBytes">The size of the compressed file sent.</param>
public sealed record CftpSendResult(bool Acknowledged, int Attempts, string? Failure, int CompressedBytes);

/// <summary>A file that arrived whole and was acknowledged.</summary>
/// <param name="From">The node it came from.</param>
/// <param name="FileName">The BFTPv1 file name as sent (V-0041); make it safe with <see cref="CftpFileNames"/> before storing it.</param>
/// <param name="CompressedFile">The file as carried.</param>
/// <param name="Mail">The mail it holds, or null when it is not a readable CFTP mail (interp V-0079).</param>
/// <param name="Error">Why <paramref name="Mail"/> is null.</param>
public sealed record CftpReceived(SisAddress From, string FileName, byte[] CompressedFile, CftpMail? Mail, string? Error);

/// <summary>
/// A CFTP client (Annex V) over the SIS: bound to SAP 12 (V-0010) for ARQ, it sends mail to one
/// node at a time and passes on the mail that arrives. It drives a <see cref="CftpEngine"/> with a
/// monotonic clock; see the engine for the protocol.
/// </summary>
public sealed class CftpClient : IAsyncDisposable
{
    /// <summary>The CFTP SAP (V-0010).</summary>
    public const byte Sap = 12;

    private readonly SisClient _sis;
    private readonly bool _ownsSis;
    private readonly CftpOptions _options;
    private readonly CftpEngine _engine;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<long, (TaskCompletionSource<CftpSendResult> Done, int Bytes)> _transfers = [];
    private readonly Dictionary<uint, SisAddress> _addresses = [];
    private readonly Channel<CftpReceived> _received = Channel.CreateUnbounded<CftpReceived>();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _receiveLoop;
    private readonly Task _timerLoop;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CftpClient(SisClient sis, bool ownsSis, ushort mtu, CftpOptions options)
    {
        _sis = sis;
        _ownsSis = ownsSis;
        Mtu = mtu;
        _options = options;
        _engine = new CftpEngine(options);
        _receiveLoop = Task.Run(ReceiveLoopAsync);
        _timerLoop = Task.Run(TimerLoopAsync);
    }

    /// <summary>The MTU the node gave at bind (interp V-0072).</summary>
    public ushort Mtu { get; }

    /// <summary>Mail and other files received, in the order they completed.</summary>
    public ChannelReader<CftpReceived> Received => _received.Reader;

    /// <summary>A line worth logging: a file sent again, a duplicate, something ignored.</summary>
    public event Action<string>? Note;

    /// <summary>Connects to a node's SIS and binds SAP 12 for ARQ (interp V-0074).</summary>
    public static async Task<CftpClient> ConnectAsync(string host = "127.0.0.1", int port = SisClient.DefaultPort, CftpOptions? options = null, CancellationToken cancellationToken = default)
    {
        SisClient sis = await SisClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        try
        {
            return await BindAsync(sis, options, ownsSis: true, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await sis.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Binds SAP 12 on a connection that has no bind yet. Disposing the client then disposes <paramref name="sis"/> only when <paramref name="ownsSis"/>.</summary>
    public static async Task<CftpClient> BindAsync(SisClient sis, CftpOptions? options = null, bool ownsSis = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sis);
        SisBindAccepted bind = await sis.BindAsync(Sap, new ServiceType(1), cancellationToken).ConfigureAwait(false);
        return new CftpClient(sis, ownsSis, bind.Mtu, options ?? new CftpOptions());
    }

    /// <summary>
    /// Sends a mail to one node and waits until it is acknowledged or fails (interp V-0076). The
    /// BFTPv1 file name is the MessageID (interp V-0084).
    /// </summary>
    public Task<CftpSendResult> SendAsync(SisAddress to, CftpMail mail, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mail);
        return SendFileAsync(to, CftpMailFile.Octets.GetBytes(mail.MessageId), mail.ToCompressedFile(), cancellationToken);
    }

    /// <summary>Sends a file that is already a compressed CFTP mail file, under the given BFTPv1 name.</summary>
    public async Task<CftpSendResult> SendFileAsync(SisAddress to, ReadOnlyMemory<byte> fileName, ReadOnlyMemory<byte> compressedFile, CancellationToken cancellationToken = default)
    {
        if (to.Group)
        {
            throw new ArgumentException("CFTP sends to one node, which acknowledges the file (interp V-0074)", nameof(to));
        }

        var done = new TaskCompletionSource<CftpSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _addresses[to.Value] = to;
            long id = _engine.Submit(to.Value, fileName, compressedFile, Mtu, Now);
            _transfers[id] = (done, compressedFile.Length);
        }

        Run();
        return await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _wake.TrySetResult();
        if (_ownsSis)
        {
            await _sis.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(_timerLoop, _ownsSis ? _receiveLoop : Task.CompletedTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        lock (_gate)
        {
            foreach ((TaskCompletionSource<CftpSendResult> d, _) in _transfers.Values)
            {
                d.TrySetException(new ObjectDisposedException(nameof(CftpClient)));
            }

            _transfers.Clear();
        }

        _received.Writer.TryComplete();
        _stop.Dispose();
    }

    private long Now => _clock.ElapsedMilliseconds;

    /// <summary>Carries out what the engine asked for, outside the lock.</summary>
    private void Run()
    {
        IReadOnlyList<CftpAction> actions;
        lock (_gate)
        {
            actions = _engine.Drain();
        }

        foreach (CftpAction a in actions)
        {
            switch (a)
            {
                case CftpSendUPdu s:
                    _ = SendOneAsync(s);
                    break;
                case CftpTransferDone d:
                    (TaskCompletionSource<CftpSendResult> Done, int Bytes) t;
                    lock (_gate)
                    {
                        if (!_transfers.Remove(d.TransferId, out t))
                        {
                            break;
                        }
                    }

                    t.Done.TrySetResult(new CftpSendResult(d.Acknowledged, d.Attempts, d.Failure, t.Bytes));
                    break;
                case CftpFileReceived f:
                    Deliver(f);
                    break;
                case CftpNote n:
                    Say(n.Text);
                    break;
            }
        }

        _wake.TrySetResult();
    }

    private void Deliver(CftpFileReceived f)
    {
        SisAddress from;
        lock (_gate)
        {
            from = _addresses.TryGetValue(f.Source, out SisAddress a) ? a : new SisAddress(f.Source);
        }

        bool ok = CftpMail.TryFromCompressedFile(f.FileData, out CftpMail? mail, out _, out string? what);
        if (!ok)
        {
            Say($"a file from {f.Source:x7} was acknowledged but {what} (interp V-0079)");
        }

        _received.Writer.TryWrite(new CftpReceived(from, CftpMailFile.Octets.GetString(f.FileName), f.FileData, mail, what));
    }

    private async Task SendOneAsync(CftpSendUPdu s)
    {
        SisAddress to;
        lock (_gate)
        {
            to = _addresses.TryGetValue(s.Destination, out SisAddress a) ? a : new SisAddress(s.Destination);
        }

        // ARQ, in order; node delivery confirmation for file segments only (interp V-0074).
        var send = new UnidataSend(to, Sap, s.UPdu)
        {
            Priority = _options.Priority,
            DeliveryMode = new ServiceType(1, Confirm: s.IsAck ? (byte)0 : (byte)1, Order: 0),
        };
        bool confirmed;
        byte reason = 0;
        try
        {
            SendResult r = await _sis.SendAsync(send, _stop.Token).ConfigureAwait(false);
            confirmed = r.Status != SendStatus.Rejected;
            reason = r.Reason;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            Say($"could not send to {to.Value:x7}: {e.Message}");
            confirmed = false;
        }

        if (s.IsAck)
        {
            return;
        }

        lock (_gate)
        {
            _engine.SendDone(s.SendId, confirmed, reason, Now);
        }

        Run();
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            await foreach (SisUnidataIndication u in _sis.Received.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                if (u.DestSap != Sap)
                {
                    continue;
                }

                lock (_gate)
                {
                    _addresses.TryAdd(u.SrcAddress.Value, u.SrcAddress with { Group = false });
                    _engine.Receive(u.SrcAddress.Value, u.UPdu.Span, Now);
                }

                Run();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task TimerLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Task wake;
            long? deadline;
            lock (_gate)
            {
                _engine.Tick(Now);
                deadline = _engine.NextDeadline;
                wake = _wake.Task;
            }

            Run();
            lock (_gate)
            {
                if (_wake.Task.IsCompleted)
                {
                    _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                wake = _wake.Task;
                deadline = _engine.NextDeadline;
            }

            TimeSpan wait = deadline is { } d ? TimeSpan.FromMilliseconds(Math.Max(1, d - Now)) : Timeout.InfiniteTimeSpan;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            await Task.WhenAny(wake, Task.Delay(wait, cts.Token)).ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);
        }
    }

    private void Say(string text)
    {
        try
        {
            Note?.Invoke(text);
        }
        catch (Exception)
        {
            // A logging problem must not stop the client.
        }
    }
}
