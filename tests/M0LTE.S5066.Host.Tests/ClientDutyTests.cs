using System.Net;
using System.Net.Sockets;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Host.Tests;

/// <summary>
/// The client side of Annex S, against a scripted node: a TCP listener the test drives one
/// primitive at a time, so each duty is seen on the wire.
/// </summary>
public class ClientDutyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly SisAddress Peer = SisAddress.FromParts(0, 0, 1, 2);

    [Fact]
    public async Task Bind_sends_the_request_and_returns_the_acceptance()
    {
        // A-0030.
        await using var node = await ScriptedNode.StartAsync();
        await using SisClient client = await SisClient.ConnectAsync(node.Endpoint);
        Task<SisBindAccepted> bind = client.BindAsync(5, new ServiceType(1));

        (await node.ReadAsync()).Should().Be(new SisBindRequest(5, new ServiceType(1)));
        await node.SendAsync(new SisBindAccepted(5, 2048));

        (await bind).Mtu.Should().Be(2048);
        client.Binding!.Sap.Should().Be(5);
        client.FlowOn.Should().BeTrue("flow is on once the bind is accepted (interp S-0027)");
    }

    [Fact]
    public async Task A_rejected_bind_throws_with_the_reason()
    {
        await using var node = await ScriptedNode.StartAsync();
        await using SisClient client = await SisClient.ConnectAsync(node.Endpoint);
        Task<SisBindAccepted> bind = client.BindAsync(5, new ServiceType(1));
        await node.ReadAsync();
        await node.SendAsync(new SisBindRejected(3));

        (await bind.Awaiting(b => b).Should().ThrowAsync<SisBindRejectedException>()).Which.Reason.Should().Be(3);
        client.Binding.Should().BeNull();
    }

    [Fact]
    public async Task No_u_pdu_is_sent_while_flow_is_off_and_sending_resumes_on_flow_on()
    {
        // S-0020, S-0021.
        await using var node = await ScriptedNode.StartAsync();
        await using SisClient client = await BoundAsync(node);
        await node.SendAsync(new SisDataFlowOff());
        await WaitUntil(() => !client.FlowOn);

        Task<SendResult> send = client.SendAsync(new UnidataSend(Peer, 5, new byte[] { 1 }));
        await Task.Delay(300);
        send.IsCompleted.Should().BeFalse();
        node.Available.Should().Be(0, "nothing goes to the node while flow is off");

        await node.SendAsync(new SisDataFlowOn());
        (await send.WaitAsync(Timeout)).Status.Should().Be(SendStatus.Sent);
        (await node.ReadAsync()).Should().BeOfType<SisUnidataRequest>();
    }

    [Fact]
    public async Task A_new_bind_starts_with_flow_on()
    {
        // interp S-0027.
        await using var node = await ScriptedNode.StartAsync();
        await using SisClient client = await BoundAsync(node);
        await node.SendAsync(new SisDataFlowOff());
        await WaitUntil(() => !client.FlowOn);
        await client.UnbindAsync();
        (await node.ReadAsync()).Should().BeOfType<SisUnbindRequest>();

        Task<SisBindAccepted> bind = client.BindAsync(5, new ServiceType(1));
        await node.ReadAsync();
        await node.SendAsync(new SisBindAccepted(5, 2048));
        await bind;

        client.FlowOn.Should().BeTrue();
    }

    [Fact]
    public async Task The_client_reads_everything_the_node_sends_even_when_the_application_does_not()
    {
        // S-0024, S-0025: the client never pushes back on the node.
        await using var node = await ScriptedNode.StartAsync();
        await using SisClient client = await BoundAsync(node);
        var indication = new SisUnidataIndication(0, 5, SisAddress.FromParts(0, 0, 1, 1), 1, 5, Peer, new byte[2000]);
        byte[] frame = SisCodec.Encode(indication);
        byte[] lots = new byte[frame.Length * 2000];
        for (int i = 0; i < 2000; i++)
        {
            frame.CopyTo(lots, i * frame.Length);
        }

        // 4 MB that the application never reads: the write completes because the client keeps reading.
        await node.Stream.WriteAsync(lots).AsTask().WaitAsync(Timeout);

        await WaitUntil(() => client.Received.Count == 2000);
    }

    [Fact]
    public async Task A_keep_alive_from_the_node_is_answered_at_once_however_often_it_comes()
    {
        // S-0041, interp S-0044.
        await using var node = await ScriptedNode.StartAsync();
        await using SisClient client = await SisClient.ConnectAsync(node.Endpoint);
        for (int i = 0; i < 3; i++)
        {
            await node.SendAsync(new SisKeepAlive());
            (await node.ReadAsync().WaitAsync(TimeSpan.FromSeconds(10))).Should().BeOfType<SisKeepAlive>();
        }
    }

    [Fact]
    public async Task An_unprompted_keep_alive_is_sent_at_most_once_in_120_seconds()
    {
        // S-0043, interp S-0044 and S-0045.
        await using var node = await ScriptedNode.StartAsync();
        await using SisClient client = await SisClient.ConnectAsync(node.Endpoint);

        (await client.ProbeAsync()).Should().BeTrue();
        (await node.ReadAsync()).Should().BeOfType<SisKeepAlive>();
        (await client.ProbeAsync()).Should().BeFalse();
        await node.SendAsync(new SisKeepAlive());
        (await node.ReadAsync()).Should().BeOfType<SisKeepAlive>("an answer is always sent");
        (await client.ProbeAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Management_indications_and_subnet_availability_are_received_without_duty()
    {
        // S-0036, S-0048.
        await using var node = await ScriptedNode.StartAsync();
        await using SisClient client = await SisClient.ConnectAsync(node.Endpoint);
        await node.SendAsync(new SisManagementMsgIndication(99, new byte[] { 1, 2 }));
        await node.SendAsync(new SisSubnetAvailability(1, 0));

        (await client.ManagementMessages.ReadAsync().AsTask().WaitAsync(Timeout)).MsgType.Should().Be(99);
        await WaitUntil(() => client.SubnetAvailability is not null);
        client.SubnetAvailability!.NodeStatus.Should().Be(1);
    }

    [Fact]
    public async Task Confirmations_and_rejections_complete_the_matching_sends()
    {
        await using var node = await ScriptedNode.StartAsync();
        await using SisClient client = await BoundAsync(node);
        var confirm = new ServiceType(1, Confirm: 1);
        Task<SendResult> first = client.SendAsync(new UnidataSend(Peer, 5, new byte[] { 1, 1, 1 }) { DeliveryMode = confirm });
        Task<SendResult> second = client.SendAsync(new UnidataSend(Peer, 5, new byte[] { 2, 2, 2 }) { DeliveryMode = confirm });
        await node.ReadAsync();
        await node.ReadAsync();

        // Answered out of order; the copy may be only the first bytes (S-0127, S-0129).
        await node.SendAsync(new SisUnidataRequestRejected(13, 5, Peer, new byte[] { 2 }));
        await node.SendAsync(new SisUnidataRequestConfirm(5, Peer, new byte[] { 1, 1, 1 }));

        (await second.WaitAsync(Timeout)).Should().Be(new SendResult(SendStatus.Rejected, 13));
        (await first.WaitAsync(Timeout)).Should().Be(new SendResult(SendStatus.Confirmed));
    }

    [Fact]
    public async Task An_unbind_indication_unbinds_and_fails_sends_waiting_for_confirmation()
    {
        await using var node = await ScriptedNode.StartAsync();
        await using SisClient client = await BoundAsync(node);
        byte? reason = null;
        client.Unbound += r => reason = r;
        Task<SendResult> send = client.SendAsync(new UnidataSend(Peer, 5, new byte[] { 1 }) { DeliveryMode = new ServiceType(1, Confirm: 1) });
        await node.ReadAsync();

        await node.SendAsync(new SisUnbindIndication(6));

        await send.Awaiting(s => s.WaitAsync(Timeout)).Should().ThrowAsync<InvalidOperationException>();
        reason.Should().Be(6);
        client.Binding.Should().BeNull();
    }

    [Fact]
    public async Task A_u_pdu_over_the_mtu_is_refused_by_the_client()
    {
        // A-0003: the client splits messages; the bind gives the MTU.
        await using var node = await ScriptedNode.StartAsync();
        await using SisClient client = await BoundAsync(node, mtu: 100);

        await client.Awaiting(c => c.SendAsync(new UnidataSend(Peer, 5, new byte[101]))).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_node_closing_the_connection_completes_closed_and_fails_waiting_sends()
    {
        await using var node = await ScriptedNode.StartAsync();
        await using SisClient client = await BoundAsync(node);
        Task<SendResult> send = client.SendAsync(new UnidataSend(Peer, 5, new byte[] { 1 }) { DeliveryMode = new ServiceType(1, Confirm: 1) });
        await node.ReadAsync();

        node.Drop();

        await client.Closed.WaitAsync(Timeout);
        await send.Awaiting(s => s.WaitAsync(Timeout)).Should().ThrowAsync<IOException>();
    }

    private static async Task<SisClient> BoundAsync(ScriptedNode node, ushort mtu = 2048)
    {
        SisClient client = await SisClient.ConnectAsync(node.Endpoint);
        Task<SisBindAccepted> bind = client.BindAsync(5, new ServiceType(1));
        await node.ReadAsync();
        await node.SendAsync(new SisBindAccepted(5, mtu));
        await bind;
        return client;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Timeout);
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }

    /// <summary>A node the test scripts: one accepted connection, primitives in and out by hand.</summary>
    private sealed class ScriptedNode : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly SisFrameDecoder _decoder = new();
        private Socket? _socket;

        private ScriptedNode(TcpListener listener) => _listener = listener;

        public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndpoint;

        public NetworkStream Stream { get; private set; } = null!;

        public int Available => _socket!.Available + _decoder.BufferedBytes;

        public static Task<ScriptedNode> StartAsync()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var node = new ScriptedNode(l);
            _ = node.AcceptAsync();
            return Task.FromResult(node);
        }

        public async Task SendAsync(SisPrimitive p)
        {
            await Accepted();
            await Stream.WriteAsync(SisCodec.Encode(p));
        }

        public async Task<SisPrimitive> ReadAsync()
        {
            await Accepted();
            using var cts = new CancellationTokenSource(Timeout);
            var buffer = new byte[4096];
            while (true)
            {
                if (_decoder.TryRead(out SisDecodeResult r))
                {
                    return r.Primitive ?? throw new InvalidDataException($"bad frame: {r.Error}");
                }

                int n = await Stream.ReadAsync(buffer, cts.Token);
                if (n == 0)
                {
                    throw new EndOfStreamException();
                }

                _decoder.Push(buffer.AsSpan(0, n));
            }
        }

        public void Drop() => _socket!.Close();

        public ValueTask DisposeAsync()
        {
            _socket?.Dispose();
            _listener.Stop();
            return ValueTask.CompletedTask;
        }

        private readonly TaskCompletionSource _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Task Accepted() => _accepted.Task.WaitAsync(Timeout);

        private async Task AcceptAsync()
        {
            _socket = await _listener.AcceptSocketAsync();
            Stream = new NetworkStream(_socket, ownsSocket: false);
            _accepted.SetResult();
        }
    }
}
