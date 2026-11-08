using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Host.Modem;
using M0LTE.S5066.Host.Sis;

namespace M0LTE.S5066.Host.Tests;

/// <summary>The Annex S server of a host (core-architecture.md 12.7).</summary>
public class SisServerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void The_default_endpoint_is_localhost_port_5066()
    {
        // S-0007, S-0009; plan 10.1 (localhost by default).
        new NodeHostOptions().SisEndpoint.Should().Be(new IPEndPoint(IPAddress.Loopback, 5066));
        HostConfig.Parse("""{ "local_address": "0.0.1.1", "csma": { "mode": "jitter" }, "cas": { "max_links": 1, "link_request_retries": 0, "break_retries": 0 } }""").SisEndpoint.Should().Be(new IPEndPoint(IPAddress.Loopback, 5066));
        SisClient.DefaultPort.Should().Be(5066);
    }

    [Fact]
    public void Another_port_can_be_configured()
    {
        // S-0008.
        HostConfig.Parse("""{ "local_address": "0.0.1.1", "csma": { "mode": "jitter" }, "cas": { "max_links": 1, "link_request_retries": 0, "break_retries": 0 }, "host": { "sis_listen": "0.0.0.0:6000" } }""")
            .SisEndpoint.Should().Be(new IPEndPoint(IPAddress.Any, 6000));
    }

    [Fact]
    public async Task Sixteen_clients_bind_every_sap_and_a_seventeenth_connection_is_accepted_but_its_bind_is_refused()
    {
        // S-0010, interp S-0015: every connection is accepted; the SAP table limits binds.
        var medium = new LoopbackMedium();
        await using NodeHost host = TestNodes.StartLoopbackHost(medium, "0.0.1.1");
        using var cts = new CancellationTokenSource(Timeout);
        var clients = new List<SisClient>();
        try
        {
            for (byte sap = 0; sap < 16; sap++)
            {
                SisClient c = await SisClient.ConnectAsync(TestNodes.Endpoint(host), cts.Token);
                clients.Add(c);
                (await c.BindAsync(sap, new ServiceType(2), cts.Token)).Sap.Should().Be(sap);
            }

            SisClient extra = await SisClient.ConnectAsync(TestNodes.Endpoint(host), cts.Token);
            clients.Add(extra);
            Func<Task> bind = () => extra.BindAsync(7, new ServiceType(2), cts.Token);
            (await bind.Should().ThrowAsync<SisBindRejectedException>()).Which.Reason.Should().NotBe(0);
            extra.Closed.IsCompleted.Should().BeFalse("the connection itself is kept");
        }
        finally
        {
            foreach (SisClient c in clients)
            {
                await c.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_closed_connection_frees_its_sap()
    {
        // interp S-0016.
        var medium = new LoopbackMedium();
        await using NodeHost host = TestNodes.StartLoopbackHost(medium, "0.0.1.1");
        using var cts = new CancellationTokenSource(Timeout);
        SisClient first = await SisClient.ConnectAsync(TestNodes.Endpoint(host), cts.Token);
        await first.BindAsync(5, new ServiceType(2), cts.Token);
        await first.DisposeAsync();

        await using SisClient second = await SisClient.ConnectAsync(TestNodes.Endpoint(host), cts.Token);
        SisBindAccepted? accepted = null;
        while (accepted is null)
        {
            try
            {
                accepted = await second.BindAsync(5, new ServiceType(2), cts.Token);
            }
            catch (SisBindRejectedException)
            {
                // The disconnect may not have reached the core yet.
                await Task.Delay(50, cts.Token);
            }
        }

        accepted.Sap.Should().Be(5);
    }

    [Fact]
    public async Task Bytes_from_the_core_reach_the_client_in_order_and_a_close_flushes_first()
    {
        var events = new RecordingEvents();
        await using var server = new SisServer(new IPEndPoint(IPAddress.Loopback, 0), events, TimeSpan.FromSeconds(5), null);
        server.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(server.LocalEndpoint);
        int conn = await events.Connected.Reader.ReadAsync();
        conn.Should().Be(1, "connection numbers start at 1");

        server.Send(conn, new byte[] { 1, 2, 3 });
        server.Send(conn, new byte[] { 4, 5 });
        server.Close(conn);

        byte[] all = await ReadToEndAsync(client.GetStream());
        all.Should().Equal(1, 2, 3, 4, 5);
        (await events.Disconnected.Reader.ReadAsync()).Should().Be(conn);
    }

    [Fact]
    public async Task Connection_numbers_are_never_reused()
    {
        var events = new RecordingEvents();
        await using var server = new SisServer(new IPEndPoint(IPAddress.Loopback, 0), events, TimeSpan.FromSeconds(5), null);
        server.Start();
        var seen = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(server.LocalEndpoint);
            seen.Add(await events.Connected.Reader.ReadAsync());
            client.Close();
            await events.Disconnected.Reader.ReadAsync();
        }

        seen.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Client_bytes_reach_the_host_whatever_the_chunking()
    {
        var events = new RecordingEvents();
        await using var server = new SisServer(new IPEndPoint(IPAddress.Loopback, 0), events, TimeSpan.FromSeconds(5), null);
        server.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(server.LocalEndpoint);
        await events.Connected.Reader.ReadAsync();
        byte[] frame = SisCodec.Encode(new SisBindRequest(5, new ServiceType(1)));
        NetworkStream s = client.GetStream();
        foreach (byte b in frame)
        {
            await s.WriteAsync(new[] { b });
            await s.FlushAsync();
        }

        var got = new List<byte>();
        using var cts = new CancellationTokenSource(Timeout);
        while (got.Count < frame.Length)
        {
            got.AddRange((await events.Received.Reader.ReadAsync(cts.Token)).ToArray());
        }

        got.Should().Equal(frame);
    }

    [Fact]
    public async Task A_client_that_stops_reading_is_disconnected_after_the_write_bound()
    {
        // S-0026, interp S-0029. The bound is 30 s in production; a test bound of 1 s, and no byte bound.
        var events = new RecordingEvents();
        await using var server = new SisServer(new IPEndPoint(IPAddress.Loopback, 0), events, TimeSpan.FromSeconds(1), long.MaxValue, null);
        server.Start();
        using var client = new TcpClient { ReceiveBufferSize = 4096 };
        await client.ConnectAsync(server.LocalEndpoint);
        int conn = await events.Connected.Reader.ReadAsync();

        // Far more than the socket buffers hold, and the client never reads.
        byte[] chunk = new byte[64 * 1024];
        for (int i = 0; i < 256; i++)
        {
            server.Send(conn, chunk);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        (await events.Disconnected.Reader.ReadAsync(cts.Token)).Should().Be(conn);
    }

    [Fact]
    public async Task A_flooding_client_that_does_not_read_is_closed_at_the_byte_bound()
    {
        // S-0026, interp S-0029 (issue #80): more than the byte bound waiting closes the connection
        // at once, long before the 30 s time bound. Production bound 1 MiB; a test bound of 256 KB.
        SisServer.DefaultQueueByteBound.Should().Be(1024 * 1024);
        var events = new RecordingEvents();
        var log = new ConcurrentQueue<string>();
        await using var server = new SisServer(new IPEndPoint(IPAddress.Loopback, 0), events, TimeSpan.FromSeconds(30), 256 * 1024, log.Enqueue);
        server.Start();
        using var client = new TcpClient { ReceiveBufferSize = 4096 };
        await client.ConnectAsync(server.LocalEndpoint);
        int conn = await events.Connected.Reader.ReadAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        byte[] chunk = new byte[64 * 1024];
        for (int i = 0; i < 1024; i++)
        {
            server.Send(conn, chunk);
        }

        using var cts = new CancellationTokenSource(Timeout);
        (await events.Disconnected.Reader.ReadAsync(cts.Token)).Should().Be(conn);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        log.Should().ContainSingle(l => l.Contains("S-0029") && l.Contains("bytes"));
    }

    [Fact]
    public async Task A_reading_client_far_below_the_byte_bound_is_not_closed()
    {
        // The byte bound counts only what is waiting: 4 MB through a client that reads is fine.
        var events = new RecordingEvents();
        await using var server = new SisServer(new IPEndPoint(IPAddress.Loopback, 0), events, TimeSpan.FromSeconds(30), 256 * 1024, null);
        server.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(server.LocalEndpoint);
        int conn = await events.Connected.Reader.ReadAsync();
        Task<byte[]> read = ReadToEndAsync(client.GetStream());

        byte[] chunk = new byte[16 * 1024];
        for (int i = 0; i < 256; i++)
        {
            server.Send(conn, chunk);
            await Task.Delay(1);
        }

        server.Close(conn);
        (await read).Length.Should().Be(256 * chunk.Length);
    }

    [Fact]
    public async Task The_accept_loop_logs_and_keeps_accepting_after_an_unexpected_failure()
    {
        // Issue #80: an accept error such as EMFILE gives one log line and the loop goes on.
        var events = new RecordingEvents();
        var log = new ConcurrentQueue<string>();
        await using var server = new SisServer(new IPEndPoint(IPAddress.Loopback, 0), events, TimeSpan.FromSeconds(30), log.Enqueue);
        int calls = 0;
        server.Accept = (listener, ct) => Interlocked.Increment(ref calls) == 1
            ? ValueTask.FromException<Socket>(new SocketException((int)SocketError.TooManyOpenSockets))
            : listener.AcceptSocketAsync(ct);
        server.AcceptRetry = TimeSpan.FromMilliseconds(50);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(server.LocalEndpoint);
        using var cts = new CancellationTokenSource(Timeout);
        (await events.Connected.Reader.ReadAsync(cts.Token)).Should().Be(1);
        log.Should().ContainSingle(l => l.StartsWith("SIS accept failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_slow_client_does_not_hold_up_another()
    {
        var events = new RecordingEvents();
        await using var server = new SisServer(new IPEndPoint(IPAddress.Loopback, 0), events, TimeSpan.FromSeconds(30), null);
        server.Start();
        using var stalled = new TcpClient { ReceiveBufferSize = 4096 };
        await stalled.ConnectAsync(server.LocalEndpoint);
        int a = await events.Connected.Reader.ReadAsync();
        using var reader = new TcpClient();
        await reader.ConnectAsync(server.LocalEndpoint);
        int b = await events.Connected.Reader.ReadAsync();

        for (int i = 0; i < 256; i++)
        {
            server.Send(a, new byte[64 * 1024]);
        }

        server.Send(b, new byte[] { 42 });
        byte[] one = new byte[1];
        using var cts = new CancellationTokenSource(Timeout);
        (await reader.GetStream().ReadAsync(one, cts.Token)).Should().Be(1);
        one[0].Should().Be(42);
    }

    private static async Task<byte[]> ReadToEndAsync(Stream s)
    {
        using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource(Timeout);
        await s.CopyToAsync(ms, cts.Token);
        return ms.ToArray();
    }

    private sealed class RecordingEvents : ISisServerEvents
    {
        public System.Threading.Channels.Channel<int> Connected { get; } = System.Threading.Channels.Channel.CreateUnbounded<int>();

        public System.Threading.Channels.Channel<int> Disconnected { get; } = System.Threading.Channels.Channel.CreateUnbounded<int>();

        public System.Threading.Channels.Channel<ReadOnlyMemory<byte>> Received { get; } = System.Threading.Channels.Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        void ISisServerEvents.Connected(int conn) => Connected.Writer.TryWrite(conn);

        void ISisServerEvents.Received(int conn, ReadOnlyMemory<byte> bytes) => Received.Writer.TryWrite(bytes);

        void ISisServerEvents.Disconnected(int conn) => Disconnected.Writer.TryWrite(conn);
    }
}
