using System.Text;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Host.Modem;
using M0LTE.S5066.Samples.HfChat;

namespace M0LTE.S5066.Host.Tests;

/// <summary>The Annex O chat sample on the client library, over two loopback hosts.</summary>
public class HfChatTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public void Messages_are_ascii_lines_ending_cr_lf_within_the_mtu()
    {
        // O-0002, O-0003, O-0004, O-0021, O-0022, O-0023.
        HfChatSession.Encode("hello", 100).Should().Equal(Encoding.ASCII.GetBytes("hello\r\n"));
        HfChatSession.Encode("hello\r\n", 100).Should().Equal(Encoding.ASCII.GetBytes("hello\r\n"));
        HfChatSession.Encode("hello", 100).Should().OnlyContain(b => b < 0x80);
        FluentActions.Invoking(() => HfChatSession.Encode("café", 100)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => HfChatSession.Encode("two\r\nlines", 100)).Should().Throw<ArgumentException>();
        HfChatSession.Encode("12345", 7).Should().HaveCount(7, "the CR LF counts against the MTU");
        FluentActions.Invoking(() => HfChatSession.Encode("123456", 7)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task A_one_to_one_message_goes_arq_with_node_confirmation_and_is_shown_with_its_source()
    {
        // O-0008, O-0010, O-0012, O-0014, O-0016.
        var medium = new LoopbackMedium();
        await using NodeHost a = TestNodes.StartLoopbackHost(medium, "0.0.1.1", "0000000000000001");
        await using NodeHost b = TestNodes.StartLoopbackHost(medium, "0.0.1.2", "0000000000000002");
        await using SisClient ca = await SisClient.ConnectAsync(TestNodes.Endpoint(a));
        await using SisClient cb = await SisClient.ConnectAsync(TestNodes.Endpoint(b));
        using var cts = new CancellationTokenSource(Timeout);
        HfChatSession alice = await HfChatSession.StartAsync(ca, cancellationToken: cts.Token);
        HfChatSession bob = await HfChatSession.StartAsync(cb, cancellationToken: cts.Token);
        var heard = new TaskCompletionSource<ChatLine>();
        bob.LineAdded += l => heard.TrySetResult(l);

        alice.Priority.Should().Be(12);
        SendResult r = await alice.SendAsync(SisAddress.FromParts(0, 0, 1, 2), "hello bob", cts.Token);
        ChatLine line = await heard.Task.WaitAsync(cts.Token);

        r.Status.Should().Be(SendStatus.Confirmed);
        line.Text.Should().Be("0.0.1.1: hello bob");
        alice.History.Select(h => h.Text).Should().Equal("me -> 0.0.1.2: hello bob", "delivered to 0.0.1.2");
    }

    [Fact]
    public async Task A_broadcast_goes_non_arq_to_every_chat_client()
    {
        // O-0011, O-0018, O-0020.
        var medium = new LoopbackMedium();
        await using NodeHost a = TestNodes.StartLoopbackHost(medium, "0.0.1.1", "0000000000000001");
        await using NodeHost b = TestNodes.StartLoopbackHost(medium, "0.0.1.2", "0000000000000002");
        await using SisClient ca = await SisClient.ConnectAsync(TestNodes.Endpoint(a));
        await using SisClient cb = await SisClient.ConnectAsync(TestNodes.Endpoint(b));
        using var cts = new CancellationTokenSource(Timeout);
        HfChatSession alice = await HfChatSession.StartAsync(ca, cancellationToken: cts.Token);
        HfChatSession bob = await HfChatSession.StartAsync(cb, cancellationToken: cts.Token);
        var heard = new TaskCompletionSource<ChatLine>();
        bob.LineAdded += l => heard.TrySetResult(l);

        (await alice.SendAsync(HfChatSession.Broadcast, "CQ", cts.Token)).Status.Should().Be(SendStatus.Sent);

        (await heard.Task.WaitAsync(cts.Token)).Text.Should().Be("0.0.1.1 (to all): CQ");
    }

    [Fact]
    public async Task The_history_keeps_the_last_lines_only()
    {
        // O-0007.
        var medium = new LoopbackMedium();
        await using NodeHost a = TestNodes.StartLoopbackHost(medium, "0.0.1.1");
        await using SisClient ca = await SisClient.ConnectAsync(TestNodes.Endpoint(a));
        HfChatSession chat = await HfChatSession.StartAsync(ca, historySize: 10);
        for (int i = 0; i < 15; i++)
        {
            await chat.SendAsync(HfChatSession.Broadcast, $"line {i}");
        }

        chat.History.Should().HaveCount(10);
        chat.History[^1].Text.Should().EndWith("line 14");
    }
}
