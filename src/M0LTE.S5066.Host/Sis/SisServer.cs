using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace M0LTE.S5066.Host.Sis;

/// <summary>What the SIS server tells its host. Calls come from socket threads.</summary>
internal interface ISisServerEvents
{
    void Connected(int conn);

    void Received(int conn, ReadOnlyMemory<byte> bytes);

    void Disconnected(int conn);
}

/// <summary>
/// The Annex S TCP server of one node (core-architecture.md 12.7). It carries bytes only: framing,
/// binding and every SIS rule are the core's. It accepts every connection (S-0010; interp S-0015),
/// numbers them from 1 without reuse, and closes a connection whose oldest unwritten output has
/// waited <see cref="WriteBound"/> (S-0026; interp S-0029).
/// </summary>
internal sealed class SisServer : IAsyncDisposable
{
    /// <summary>
    /// The fixed write bound of interp S-0029: three times the 10 s keep-alive answer bound (S-0041).
    /// Not a setting (plan 3.2 principle 5).
    /// </summary>
    public static readonly TimeSpan DefaultWriteBound = TimeSpan.FromSeconds(30);

    private readonly TcpListener _listener;
    private readonly ISisServerEvents _events;
    private readonly ConcurrentDictionary<int, Connection> _connections = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<string>? _log;
    private Task? _acceptLoop;
    private int _lastConn;

    public SisServer(IPEndPoint endpoint, ISisServerEvents events, TimeSpan writeBound, Action<string>? log)
    {
        _listener = new TcpListener(endpoint);
        _events = events;
        WriteBound = writeBound;
        _log = log;
    }

    /// <summary>How long the oldest unwritten output to a client may wait before the client is closed.</summary>
    public TimeSpan WriteBound { get; }

    /// <summary>The endpoint actually bound, once started (port 0 picks a free port).</summary>
    public IPEndPoint LocalEndpoint => (IPEndPoint)_listener.LocalEndpoint;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Queues bytes for a client. Bytes for a connection that has gone are dropped.</summary>
    public void Send(int conn, ReadOnlyMemory<byte> bytes)
    {
        if (_connections.TryGetValue(conn, out Connection? c))
        {
            c.Enqueue(bytes);
        }
    }

    /// <summary>The core closed the connection: flush what is queued, within the bound, then close.</summary>
    public void Close(int conn)
    {
        if (_connections.TryGetValue(conn, out Connection? c))
        {
            c.CloseAfterFlush();
        }
    }

    /// <summary>Waits until every connection has finished, or the timeout passes.</summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        Task all = Task.WhenAll(_connections.Values.Select(c => c.Finished));
        await Task.WhenAny(all, Task.Delay(timeout)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        foreach (Connection c in _connections.Values)
        {
            c.Abort();
        }

        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        await Task.WhenAll(_connections.Values.Select(c => c.Finished)).ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            socket.NoDelay = true;
            int conn = Interlocked.Increment(ref _lastConn);
            var c = new Connection(this, conn, socket);
            _connections[conn] = c;
            _events.Connected(conn);
            c.Run();
        }
    }

    private void Finish(Connection c)
    {
        _connections.TryRemove(c.Id, out _);
        _events.Disconnected(c.Id);
    }

    private sealed class Connection
    {
        private readonly SisServer _server;
        private readonly Socket _socket;
        private readonly Channel<(ReadOnlyMemory<byte> Bytes, long Queued)> _out =
            Channel.CreateUnbounded<(ReadOnlyMemory<byte>, long)>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _abort = new();
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _finishedFlag;

        public Connection(SisServer server, int id, Socket socket)
        {
            _server = server;
            Id = id;
            _socket = socket;
        }

        public int Id { get; }

        public Task Finished => _finished.Task;

        public void Enqueue(ReadOnlyMemory<byte> bytes) => _out.Writer.TryWrite((bytes, Stopwatch.GetTimestamp()));

        public void CloseAfterFlush() => _out.Writer.TryComplete();

        public void Abort()
        {
            _out.Writer.TryComplete();
            _abort.Cancel();
        }

        public void Run()
        {
            Task reader = Task.Run(ReadLoopAsync);
            Task writer = Task.Run(WriteLoopAsync);
            _ = Task.WhenAll(reader, writer).ContinueWith(_ => Done(), TaskScheduler.Default);
        }

        private async Task ReadLoopAsync()
        {
            var buffer = new byte[4096];
            try
            {
                while (true)
                {
                    int n = await _socket.ReceiveAsync(buffer, SocketFlags.None, _abort.Token).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    _server._events.Received(Id, buffer.AsSpan(0, n).ToArray());
                }
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }

            // The client has gone or the connection was closed: stop writing too.
            Abort();
        }

        private async Task WriteLoopAsync()
        {
            try
            {
                await foreach ((ReadOnlyMemory<byte> bytes, long queued) in _out.Reader.ReadAllAsync(_abort.Token).ConfigureAwait(false))
                {
                    TimeSpan left = _server.WriteBound - Stopwatch.GetElapsedTime(queued);
                    if (left <= TimeSpan.Zero)
                    {
                        _server._log?.Invoke($"SIS conn {Id}: client not reading for {_server.WriteBound.TotalSeconds:0} s, closing (S-0029)");
                        break;
                    }

                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token);
                    deadline.CancelAfter(left);
                    int sent = 0;
                    while (sent < bytes.Length)
                    {
                        sent += await _socket.SendAsync(bytes[sent..], SocketFlags.None, deadline.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (!_abort.IsCancellationRequested)
            {
                _server._log?.Invoke($"SIS conn {Id}: client not reading for {_server.WriteBound.TotalSeconds:0} s, closing (S-0029)");
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }

            // Everything written (a close the core asked for), the bound passed, or the socket failed.
            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
            }

            _abort.Cancel();
        }

        private void Done()
        {
            if (Interlocked.Exchange(ref _finishedFlag, 1) != 0)
            {
                return;
            }

            _socket.Dispose();
            _server.Finish(this);
            _finished.TrySetResult();
        }
    }
}
