using System.Net;
using System.Net.Sockets;
using System.Text;
using M0LTE.S5066.Cftp;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Host;
using M0LTE.S5066.Host.Modem;
using M0LTE.S5066.Host.Tests;

namespace M0LTE.S5066.Mail.Tests;

/// <summary>A clock the test can move forward; timers still run in real time.</summary>
internal sealed class MovableTime : TimeProvider
{
    public TimeSpan Offset { get; set; }

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
}

/// <summary>
/// Two gateways over two loopback hosts, each with an in-process SMTP sink as its relay: SMTP in
/// at one end, CFTP across, SMTP out at the other (V-0069, interps V-0090 to V-0092).
/// </summary>
public class GatewayTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private sealed class Side : IAsyncDisposable
    {
        public required NodeHost Host { get; init; }
        public required SmtpSink Sink { get; init; }
        public required string Spool { get; init; }
        public required MovableTime Time { get; init; }
        public MailGateway Gateway { get; set; } = null!;
        public Task Run { get; set; } = Task.CompletedTask;
        public CancellationTokenSource Stop { get; set; } = new();
        public List<string> Log { get; } = [];

        public string LogText
        {
            get
            {
                lock (Log)
                {
                    return string.Join("\n", Log);
                }
            }
        }

        public async Task StopGatewayAsync()
        {
            await Stop.CancelAsync();
            try
            {
                await Run.WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch (OperationCanceledException)
            {
            }

            await Gateway.DisposeAsync();
            Stop.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await StopGatewayAsync();
            await Sink.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    private static string ConfigJson(string spool, int relayPort, string routes) => $$"""
        {
          "spool": "{{spool.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
          "hostname": "gw.test",
          "relay": { "host": "127.0.0.1", "port": {{relayPort}} },
          "listen": "127.0.0.1:0",
          "routes": [ {{routes}} ]
        }
        """;

    private static void StartGateway(Side side, int relayPort, string routes)
    {
        MailConfig config = MailConfig.Parse(ConfigJson(side.Spool, relayPort, routes));
        side.Stop = new CancellationTokenSource();
        side.Gateway = new MailGateway(config, l => { lock (side.Log) { side.Log.Add(l); } }, side.Time);
        side.Gateway.Start();
        NodeHost host = side.Host;
        side.Run = side.Gateway.RunAsync(ct => SisClient.ConnectAsync(TestNodes.Endpoint(host), ct), side.Stop.Token);
    }

    private static Side Start(LoopbackMedium medium, string address, string seed, string routes, int? relayPort = null)
    {
        var side = new Side
        {
            Host = TestNodes.StartLoopbackHost(medium, address, seed),
            Sink = new SmtpSink(),
            Spool = Directory.CreateTempSubdirectory("s5066-mail-gw-").FullName,
            Time = new MovableTime(),
        };
        StartGateway(side, relayPort ?? side.Sink.Port, routes);
        return side;
    }

    private static async Task Until(Func<bool> condition, string what, Side? log = null)
    {
        using var cts = new CancellationTokenSource(Timeout);
        while (!condition())
        {
            if (cts.IsCancellationRequested)
            {
                throw new TimeoutException(what + (log is null ? "" : "\n" + log.LogText));
            }

            await Task.Delay(50, CancellationToken.None);
        }
    }

    private static Task<IReadOnlyList<RecipientResult>> Submit(Side side, string from, string[] to, string message) =>
        new SmtpRelayClient(new RelayConfig("127.0.0.1", side.Gateway.ListenEndpoint!.Port, false, null), "client.test", null)
            .SendAsync(from, to, Encoding.ASCII.GetBytes(message), CancellationToken.None);

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private const string ToB = """{ "match": "b.test", "node": "0.0.1.2" }""";
    private const string ToA = """{ "match": "a.test", "node": "0.0.1.1" }""";

    [Fact]
    public async Task Mail_taken_by_smtp_at_one_end_is_relayed_by_smtp_at_the_other()
    {
        var medium = new LoopbackMedium();
        await using Side a = Start(medium, "0.0.1.1", "0000000000000001", ToB);
        await using Side b = Start(medium, "0.0.1.2", "0000000000000002", ToA);

        const string message = "From: m0lte@a.test\r\nTo: g4abc@b.test\r\nSubject: over HF\r\n\r\nHello.\r\n.a line with a dot\r\n";
        IReadOnlyList<RecipientResult> taken = await Submit(a, "m0lte@a.test", ["g4abc@b.test", "nobody@elsewhere.test", "g4xyz@B.test"], message);
        taken.Select(r => r.Outcome).Should().Equal(RecipientOutcome.Accepted, RecipientOutcome.Permanent, RecipientOutcome.Accepted);
        taken[1].Reply.Should().StartWith("550 5.1.2");

        await Until(() => b.Sink.Mail.Count == 1, "the mail reaches b's relay", b);
        (SmtpEnvelope env, byte[] got) = b.Sink.Mail[0];
        env.ReturnPath.Should().Be("m0lte@a.test");
        env.Recipients.Should().Equal("g4abc@b.test", "g4xyz@B.test");
        env.Helo.Should().Be("gw.test");
        string text = Encoding.ASCII.GetString(got);
        text.Should().StartWith("Received: from client.test ([127.0.0.1])\r\n\tby gw.test (s5066-mail) with ESMTP id ");
        text.Should().EndWith("\r\n" + message);
        await Until(() => a.Gateway.Spool.List(SpoolQueue.Hf).Count == 0 && b.Gateway.Spool.List(SpoolQueue.Smtp).Count == 0, "both spools empty", a);
        a.LogText.Should().Contain("delivered to node 0.0.1.2 as CFTP mail");
        b.LogText.Should().Contain("relayed for 2 recipient(s)");
        a.Sink.Mail.Should().BeEmpty();
    }

    [Fact]
    public async Task A_recipient_the_far_relay_refuses_is_bounced_back_over_hf()
    {
        var medium = new LoopbackMedium();
        await using Side a = Start(medium, "0.0.1.1", "0000000000000001", ToB);
        await using Side b = Start(medium, "0.0.1.2", "0000000000000002", ToA);
        b.Sink.Refuse["gone@b.test"] = new SmtpReply(550, ["5.1.1 no such user here"]);

        IReadOnlyList<RecipientResult> taken = await Submit(a, "m0lte@a.test", ["gone@b.test", "g4abc@b.test"], "Subject: bounce me\r\n\r\nbody\r\n");
        taken.Should().OnlyContain(r => r.Outcome == RecipientOutcome.Accepted);

        await Until(() => b.Sink.Mail.Count == 1 && a.Sink.Mail.Count == 1, "the mail at b and the bounce at a", a);
        b.Sink.Mail[0].Envelope.Recipients.Should().Equal("g4abc@b.test");
        (SmtpEnvelope env, byte[] dsn) = a.Sink.Mail[0];
        env.ReturnPath.Should().BeEmpty("a bounce has the null return path");
        env.Recipients.Should().Equal("m0lte@a.test");
        string text = Encoding.ASCII.GetString(dsn);
        text.Should().Contain("Final-Recipient: rfc822; gone@b.test\r\nAction: failed\r\nStatus: 5.1.1\r\n");
        text.Should().Contain("Subject: bounce me");
        b.LogText.Should().Contain("by HF to node 0.0.1.1");
    }

    [Fact]
    public async Task Mail_waits_in_the_spool_while_the_relay_is_down_and_across_a_restart()
    {
        var medium = new LoopbackMedium();
        await using Side a = Start(medium, "0.0.1.1", "0000000000000001", ToB);
        await using Side b = Start(medium, "0.0.1.2", "0000000000000002", ToA, relayPort: FreePort());

        await Submit(a, "m0lte@a.test", ["g4abc@b.test"], "Subject: patience\r\n\r\nbody\r\n");
        // The record is saved before the log line is written, so wait for both.
        await Until(() => b.Gateway.Spool.List(SpoolQueue.Smtp) is [{ Failures: 1 }] && b.LogText.Contains("try 2 in 1 min", StringComparison.Ordinal), "a failed try at b's relay", b);
        SpoolItem waiting = b.Gateway.Spool.List(SpoolQueue.Smtp)[0];
        waiting.NextAttempt.Should().BeCloseTo(b.Time.GetUtcNow() + TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        waiting.LastError.Should().StartWith("relay 127.0.0.1:");
        b.LogText.Should().Contain("not delivered (relay 127.0.0.1:").And.Contain("try 2 in 1 min");

        // b restarts with its relay back; the mail is still there and goes once its time comes.
        await b.StopGatewayAsync();
        b.Time.Offset = TimeSpan.FromMinutes(2);
        StartGateway(b, b.Sink.Port, ToA);
        await Until(() => b.Sink.Mail.Count == 1, "the mail relayed after the restart", b);
        Encoding.ASCII.GetString(b.Sink.Mail[0].Message).Should().EndWith("Subject: patience\r\n\r\nbody\r\n");
        await Until(() => b.Gateway.Spool.List(SpoolQueue.Smtp).Count == 0, "b's spool empty", b);
    }

    [Fact]
    public async Task The_same_mail_sent_twice_by_cftp_is_relayed_once()
    {
        var medium = new LoopbackMedium();
        await using Side b = Start(medium, "0.0.1.2", "0000000000000002", ToA);
        await using NodeHost c = TestNodes.StartLoopbackHost(medium, "0.0.1.3", "0000000000000003");
        await using CftpClient sender = await CftpClient.ConnectAsync("127.0.0.1", TestNodes.Endpoint(c).Port, cancellationToken: TestContext.Current.CancellationToken);

        var mail = new CftpMail("SAMEID42", new CftpRecipients("<m0lte@c.test>", ["<g4abc@b.test>"]), "Subject: twice\r\n\r\nx\r\n"u8.ToArray());
        SisAddress to = SisAddress.FromParts(0, 0, 1, 2);
        (await sender.SendAsync(to, mail, TestContext.Current.CancellationToken)).Acknowledged.Should().BeTrue();
        (await sender.SendAsync(to, mail, TestContext.Current.CancellationToken)).Acknowledged.Should().BeTrue();

        await Until(() => b.LogText.Contains("already taken in", StringComparison.Ordinal), "the second copy seen as a duplicate", b);
        await Until(() => b.Sink.Mail.Count == 1, "one copy relayed", b);
        b.Sink.Mail[0].Envelope.ReturnPath.Should().Be("m0lte@c.test", "angle brackets in the RecipientList are removed");
        b.Sink.Mail[0].Envelope.Recipients.Should().Equal("g4abc@b.test");
        await Task.Delay(500, TestContext.Current.CancellationToken);
        b.Sink.Mail.Should().HaveCount(1);
    }
}
