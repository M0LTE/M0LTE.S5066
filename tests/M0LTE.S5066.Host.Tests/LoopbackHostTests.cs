using System.Text;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Host.Modem;

namespace M0LTE.S5066.Host.Tests;

/// <summary>Two hosts in one process on the byte-level loopback medium, driven by real clients over TCP.</summary>
public class LoopbackHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task An_arq_u_pdu_with_node_confirmation_crosses_and_is_confirmed()
    {
        var medium = new LoopbackMedium();
        await using NodeHost a = TestNodes.StartLoopbackHost(medium, "0.0.1.1", "0000000000000001");
        await using NodeHost b = TestNodes.StartLoopbackHost(medium, "0.0.1.2", "0000000000000002");
        await using SisClient ca = await SisClient.ConnectAsync(TestNodes.Endpoint(a));
        await using SisClient cb = await SisClient.ConnectAsync(TestNodes.Endpoint(b));
        using var cts = new CancellationTokenSource(Timeout);

        SisBindAccepted bound = await ca.BindAsync(5, new ServiceType(1), cts.Token);
        bound.Sap.Should().Be(5);
        await cb.BindAsync(5, new ServiceType(1), cts.Token);

        byte[] u = Encoding.ASCII.GetBytes("hello over the loopback\r\n");
        SendResult r = await ca.SendAsync(
            new UnidataSend(SisAddress.FromParts(0, 0, 1, 2), 5, u) { DeliveryMode = new ServiceType(1, Confirm: 1) }, cts.Token);
        SisUnidataIndication got = await cb.Received.ReadAsync(cts.Token);

        r.Status.Should().Be(SendStatus.Confirmed);
        got.UPdu.ToArray().Should().Equal(u);
        got.SrcAddress.Value.Should().Be(SisAddress.FromParts(0, 0, 1, 1).Value);
        got.SrcSap.Should().Be(5);
    }

    [Fact]
    public async Task A_non_arq_broadcast_reaches_every_other_node()
    {
        var medium = new LoopbackMedium();
        await using NodeHost a = TestNodes.StartLoopbackHost(medium, "0.0.1.1", "0000000000000001");
        await using NodeHost b = TestNodes.StartLoopbackHost(medium, "0.0.1.2", "0000000000000002");
        await using NodeHost c = TestNodes.StartLoopbackHost(medium, "0.0.1.3", "0000000000000003");
        await using SisClient ca = await SisClient.ConnectAsync(TestNodes.Endpoint(a));
        await using SisClient cb = await SisClient.ConnectAsync(TestNodes.Endpoint(b));
        await using SisClient cc = await SisClient.ConnectAsync(TestNodes.Endpoint(c));
        using var cts = new CancellationTokenSource(Timeout);
        foreach (SisClient x in new[] { ca, cb, cc })
        {
            await x.BindAsync(5, new ServiceType(2), cts.Token);
        }

        byte[] u = Encoding.ASCII.GetBytes("to everyone\r\n");
        var broadcast = new SisAddress(0x0FFFFFFF, Group: true);
        (await ca.SendAsync(new UnidataSend(broadcast, 5, u), cts.Token)).Status.Should().Be(SendStatus.Sent);

        (await cb.Received.ReadAsync(cts.Token)).UPdu.ToArray().Should().Equal(u);
        (await cc.Received.ReadAsync(cts.Token)).UPdu.ToArray().Should().Equal(u);
    }

    [Fact]
    public async Task Node_information_answers_over_tcp_and_shows_the_peer_after_an_exchange()
    {
        var medium = new LoopbackMedium();
        await using NodeHost a = TestNodes.StartLoopbackHost(medium, "0.0.1.1", "0000000000000001");
        await using NodeHost b = TestNodes.StartLoopbackHost(medium, "0.0.1.2", "0000000000000002");
        await using SisClient ca = await SisClient.ConnectAsync(TestNodes.Endpoint(a));
        await using SisClient cb = await SisClient.ConnectAsync(TestNodes.Endpoint(b));
        using var cts = new CancellationTokenSource(Timeout);
        await ca.BindAsync(5, new ServiceType(1), cts.Token);
        await cb.BindAsync(5, new ServiceType(1), cts.Token);
        await ca.SendAsync(new UnidataSend(SisAddress.FromParts(0, 0, 1, 2), 5, new byte[] { 1, 2, 3 }) { DeliveryMode = new ServiceType(1, Confirm: 1) }, cts.Token);

        NodeInformation info = await ca.GetNodeInformationAsync(cts.Token);

        info.Address.Value.Should().Be(SisAddress.FromParts(0, 0, 1, 1).Value);
        info.TxWn.Should().Be(6);
        info.Peers.Should().ContainSingle(p => p.Address.Value == SisAddress.FromParts(0, 0, 1, 2).Value)
            .Which.HeardWn.Should().Be(6);
    }

    [Fact]
    public async Task The_host_reports_link_state_changes()
    {
        var medium = new LoopbackMedium();
        await using NodeHost a = TestNodes.StartLoopbackHost(medium, "0.0.1.1", "0000000000000001");
        await using NodeHost b = TestNodes.StartLoopbackHost(medium, "0.0.1.2", "0000000000000002");
        var states = new List<LinkStateName>();
        var linked = new TaskCompletionSource();
        a.LinkStateChanged += (_, s) =>
        {
            lock (states)
            {
                states.Add(s);
            }

            if (s == LinkStateName.Linked)
            {
                linked.TrySetResult();
            }
        };
        await using SisClient ca = await SisClient.ConnectAsync(TestNodes.Endpoint(a));
        await using SisClient cb = await SisClient.ConnectAsync(TestNodes.Endpoint(b));
        using var cts = new CancellationTokenSource(Timeout);
        await ca.BindAsync(5, new ServiceType(1), cts.Token);
        await cb.BindAsync(5, new ServiceType(1), cts.Token);
        await ca.SendAsync(new UnidataSend(SisAddress.FromParts(0, 0, 1, 2), 5, new byte[] { 9 }), cts.Token);

        await linked.Task.WaitAsync(cts.Token);
        lock (states)
        {
            states.Should().StartWith([LinkStateName.Calling, LinkStateName.Linked]);
        }

    }
}
