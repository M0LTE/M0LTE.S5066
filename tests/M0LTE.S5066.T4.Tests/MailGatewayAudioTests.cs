using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using M0LTE.S5066.Mail;

namespace M0LTE.S5066.T4.Tests;

/// <summary>
/// P10-3 T4: two s5066d over UDP audio, an s5066-mail process beside each, and an in-process SMTP
/// sink as both relays. A mail goes into gateway a by SMTP, across by CFTP, and out of gateway b
/// to the sink (V-0069); the sink's refusal of one recipient comes back to a as a bounce over HF
/// (interp V-0092).
/// </summary>
public class MailGatewayAudioTests
{
    private readonly ITestOutputHelper _output;

    public MailGatewayAudioTests(ITestOutputHelper output) => _output = output;

    private sealed class Sink : ISmtpHandler
    {
        public List<(SmtpEnvelope Envelope, byte[] Message)> Mail { get; } = [];

        public long MaxMessageBytes => 10_000_000;

        public SmtpReply? CheckRecipient(string returnPath, IReadOnlyList<string> accepted, string recipient) =>
            recipient.StartsWith("gone@", StringComparison.Ordinal) ? new SmtpReply(550, ["5.1.1 no such user"]) : null;

        public SmtpReply Deliver(SmtpEnvelope envelope, byte[] message)
        {
            lock (Mail)
            {
                Mail.Add((envelope, message));
            }

            return new SmtpReply(250, ["2.0.0 ok"]);
        }

        public int Count
        {
            get
            {
                lock (Mail)
                {
                    return Mail.Count;
                }
            }
        }
    }

    [Fact]
    public async Task Mail_crosses_two_nodes_over_audio_through_two_gateways_and_a_bounce_comes_back()
    {
        string dir = Directory.CreateTempSubdirectory("s5066-t4-mail-").FullName;
        (int sisA, int sisB, int audioA, int audioB) = TwoNodeAudioTests.FreePorts();
        (int smtpA, int smtpB, _, _) = TwoNodeAudioTests.FreePorts();
        string configA = TwoNodeAudioTests.WriteConfig(dir, "bench-node1.json", sisA, audioA, audioB);
        string configB = TwoNodeAudioTests.WriteConfig(dir, "bench-node2.json", sisB, audioB, audioA);

        var sink = new Sink();
        await using var relay = new SmtpListener(new IPEndPoint(IPAddress.Loopback, 0), "sink.test", sink, _ => { });
        relay.Start();
        string GatewayConfig(string name, int sis, int listen, string routeMatch, string routeNode)
        {
            string path = Path.Combine(dir, name + ".json");
            File.WriteAllText(path, $$"""
                {
                  "sis": "127.0.0.1:{{sis}}",
                  "spool": "{{Path.Combine(dir, "spool-" + name)}}",
                  "hostname": "gw-{{name}}.test",
                  "relay": { "host": "127.0.0.1", "port": {{relay.Endpoint.Port}} },
                  "listen": "127.0.0.1:{{listen}}",
                  "routes": [ { "match": "{{routeMatch}}", "node": "{{routeNode}}" } ]
                }
                """);
            return path;
        }

        string mailA = GatewayConfig("a", sisA, smtpA, "b.test", "0.0.1.2");
        string mailB = GatewayConfig("b", sisB, smtpB, "a.test", "0.0.1.1");

        var log = new StringBuilder();
        var processes = new List<Process>();
        try
        {
            processes.Add(TwoNodeAudioTests.Start("s5066d.dll", configA, "a", log));
            processes.Add(TwoNodeAudioTests.Start("s5066d.dll", configB, "b", log));
            await TwoNodeAudioTests.WaitForListenAsync(sisA, processes[0]);
            await TwoNodeAudioTests.WaitForListenAsync(sisB, processes[1]);
            processes.Add(TwoNodeAudioTests.Start("s5066-mail.dll", $"\"{mailA}\"", "mail-a", log));
            processes.Add(TwoNodeAudioTests.Start("s5066-mail.dll", $"\"{mailB}\"", "mail-b", log));
            await TwoNodeAudioTests.WaitForListenAsync(smtpA, processes[2]);
            await TwoNodeAudioTests.WaitForListenAsync(smtpB, processes[3]);

            const string message = "From: alice@a.test\r\nTo: bob@b.test\r\nSubject: over HF\r\n\r\nHello Bob, over the air.\r\n";
            IReadOnlyList<RecipientResult> taken = await new SmtpRelayClient(new RelayConfig("127.0.0.1", smtpA, false, null), "client.test", null)
                .SendAsync("alice@a.test", ["bob@b.test", "gone@b.test"], Encoding.ASCII.GetBytes(message), CancellationToken.None);
            taken.Should().OnlyContain(r => r.Outcome == RecipientOutcome.Accepted);

            var sw = Stopwatch.StartNew();
            while (sink.Count < 2 && sw.Elapsed < TimeSpan.FromMinutes(6))
            {
                await Task.Delay(500);
            }

            List<(SmtpEnvelope Envelope, byte[] Message)> got;
            lock (sink.Mail)
            {
                got = [.. sink.Mail];
            }

            got.Should().HaveCount(2, "the mail at b's relay and the bounce at a's");
            (SmtpEnvelope env, byte[] body) = got.Single(m => m.Envelope.Helo == "gw-b.test");
            env.ReturnPath.Should().Be("alice@a.test");
            env.Recipients.Should().Equal("bob@b.test");
            Encoding.ASCII.GetString(body).Should().StartWith("Received: from s5066-").And.Contain("\r\nReceived: from client.test").And.EndWith("\r\n" + message);

            (SmtpEnvelope bounceEnv, byte[] bounce) = got.Single(m => m.Envelope.Helo == "gw-a.test");
            bounceEnv.ReturnPath.Should().BeEmpty();
            bounceEnv.Recipients.Should().Equal("alice@a.test");
            Encoding.ASCII.GetString(bounce).Should().Contain("Final-Recipient: rfc822; gone@b.test").And.Contain("Status: 5.1.1");
        }
        finally
        {
            foreach (Process p in processes)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    await p.WaitForExitAsync();
                }
                catch (InvalidOperationException)
                {
                }

                p.Dispose();
            }

            lock (log)
            {
                _output.WriteLine(log.ToString());
            }

            Directory.Delete(dir, recursive: true);
        }
    }
}
