using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using M0LTE.S5066.Cftp;

namespace M0LTE.S5066.Mail.Tests;

/// <summary>An in-process SMTP sink: keeps every mail it takes, refuses recipients it is told to.</summary>
internal sealed class SmtpSink : ISmtpHandler, IAsyncDisposable
{
    private readonly SmtpListener _listener;
    private readonly List<(SmtpEnvelope Envelope, byte[] Message)> _mail = [];

    public SmtpSink()
    {
        _listener = new SmtpListener(new IPEndPoint(IPAddress.Loopback, 0), "sink.test", this, _ => { });
        _listener.Start();
    }

    public int Port => _listener.Endpoint.Port;

    public long MaxMessageBytes { get; set; } = 10_000_000;

    /// <summary>Replies for recipients to refuse, by address.</summary>
    public Dictionary<string, SmtpReply> Refuse { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<(SmtpEnvelope Envelope, byte[] Message)> Mail
    {
        get
        {
            lock (_mail)
            {
                return [.. _mail];
            }
        }
    }

    public SmtpReply? CheckRecipient(string returnPath, IReadOnlyList<string> accepted, string recipient) =>
        Refuse.TryGetValue(recipient, out SmtpReply? r) ? r : null;

    public SmtpReply Deliver(SmtpEnvelope envelope, byte[] message)
    {
        lock (_mail)
        {
            _mail.Add((envelope, message));
        }

        return new SmtpReply(250, ["2.0.0 ok"]);
    }

    public ValueTask DisposeAsync() => _listener.DisposeAsync();
}

/// <summary>The SMTP client the gateway uses for the relay, and its SMTP listener.</summary>
public class SmtpTests
{
    private static SmtpRelayClient ClientFor(int port, SmtpCredentials? c = null, bool tls = false) =>
        new(new RelayConfig("127.0.0.1", port, tls, null), "gw.test", c) { AcceptAnyCertificate = true };

    [Fact]
    public async Task A_mail_goes_through_byte_for_byte_with_dots_and_eight_bit_octets()
    {
        await using var sink = new SmtpSink();
        byte[] message = [.. "Subject: dots\r\n\r\n.leading dot\r\n..two\r\n.\r\n"u8, 0xC3, 0xA9, .. "\r\nend\r\n"u8];
        IReadOnlyList<RecipientResult> r = await ClientFor(sink.Port).SendAsync("m0lte@example.org", ["a@x.test", "b@x.test"], message, TestContext.Current.CancellationToken);
        r.Select(x => x.Outcome).Should().Equal(RecipientOutcome.Accepted, RecipientOutcome.Accepted);
        (SmtpEnvelope env, byte[] got) = sink.Mail.Should().ContainSingle().Subject;
        env.ReturnPath.Should().Be("m0lte@example.org");
        env.Recipients.Should().Equal("a@x.test", "b@x.test");
        env.Helo.Should().Be("gw.test");
        got.Should().Equal(message);
    }

    [Fact]
    public async Task Each_recipient_gets_its_own_outcome_and_the_null_return_path_is_sent()
    {
        await using var sink = new SmtpSink();
        sink.Refuse["gone@x.test"] = new SmtpReply(550, ["5.1.1 no such user"]);
        sink.Refuse["busy@x.test"] = new SmtpReply(451, ["4.3.0 try later"]);
        IReadOnlyList<RecipientResult> r = await ClientFor(sink.Port).SendAsync("", ["ok@x.test", "gone@x.test", "busy@x.test"], "Subject: x\r\n\r\nx\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
        r.Select(x => x.Outcome).Should().Equal(RecipientOutcome.Accepted, RecipientOutcome.Permanent, RecipientOutcome.Transient);
        r[1].Reply.Should().Be("550 5.1.1 no such user");
        sink.Mail.Should().ContainSingle().Which.Envelope.ReturnPath.Should().BeEmpty();
        sink.Mail[0].Envelope.Recipients.Should().Equal("ok@x.test");
    }

    [Fact]
    public async Task A_message_over_the_size_limit_is_refused_permanently()
    {
        await using var sink = new SmtpSink { MaxMessageBytes = 100 };
        byte[] message = Encoding.ASCII.GetBytes("Subject: big\r\n\r\n" + new string('x', 200) + "\r\n");
        IReadOnlyList<RecipientResult> r = await ClientFor(sink.Port).SendAsync("a@x.test", ["b@x.test"], message, TestContext.Current.CancellationToken);
        r.Should().ContainSingle().Which.Outcome.Should().Be(RecipientOutcome.Permanent);
        r[0].Reply.Should().StartWith("552 5.3.4");
        sink.Mail.Should().BeEmpty();
    }

    [Fact]
    public async Task The_listener_keeps_to_the_command_order_and_only_a_crlf_dot_crlf_ends_data()
    {
        await using var sink = new SmtpSink();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, sink.Port, TestContext.Current.CancellationToken);
        var lines = new SmtpLines(tcp.GetStream());
        CancellationToken ct = TestContext.Current.CancellationToken;

        async Task<SmtpReply> Cmd(string c)
        {
            await lines.WriteLineAsync(c, ct);
            return await SmtpReply.ReadAsync(lines, ct);
        }

        (await SmtpReply.ReadAsync(lines, ct)).Code.Should().Be(220);
        (await Cmd("MAIL FROM:<a@x.test>")).Code.Should().Be(503);
        SmtpReply ehlo = await Cmd("EHLO me");
        ehlo.Lines.Should().Contain(["8BITMIME", "PIPELINING", "SIZE 10000000"]);
        (await Cmd("RCPT TO:<b@x.test>")).Code.Should().Be(503);
        (await Cmd("MAIL FROM:<a@x.test> SIZE=20000000")).Code.Should().Be(552);
        (await Cmd("MAIL FROM:<a@x.test> SIZE=100 BODY=8BITMIME")).Code.Should().Be(250);
        (await Cmd("RCPT TO:<b,c@x.test>")).Code.Should().Be(501);
        (await Cmd("RCPT TO:<b@x.test>")).Code.Should().Be(250);
        (await Cmd("FROB")).Code.Should().Be(500);
        (await Cmd("DATA")).Code.Should().Be(354);
        await tcp.GetStream().WriteAsync("Subject: s\r\n\r\nline\n.\nstill data\r\n.\r\n"u8.ToArray(), ct);
        (await SmtpReply.ReadAsync(lines, ct)).Code.Should().Be(250);
        (await Cmd("QUIT")).Code.Should().Be(221);
        Encoding.ASCII.GetString(sink.Mail.Should().ContainSingle().Subject.Message).Should().Be("Subject: s\r\n\r\nline\n.\nstill data\r\n");
    }

    [Fact]
    public async Task An_over_long_data_line_is_dropped_whole_so_its_tail_cannot_end_data()
    {
        await using var sink = new SmtpSink();
        using var tcp = new TcpClient { NoDelay = true };
        CancellationToken ct = TestContext.Current.CancellationToken;
        await tcp.ConnectAsync(IPAddress.Loopback, sink.Port, ct);
        var lines = new SmtpLines(tcp.GetStream());

        async Task<SmtpReply> Cmd(string c)
        {
            await lines.WriteLineAsync(c, ct);
            return await SmtpReply.ReadAsync(lines, ct);
        }

        (await SmtpReply.ReadAsync(lines, ct)).Code.Should().Be(220);
        (await Cmd("EHLO me")).Code.Should().Be(250);
        (await Cmd("MAIL FROM:<a@x.test>")).Code.Should().Be(250);
        (await Cmd("RCPT TO:<b@x.test>")).Code.Should().Be(250);
        (await Cmd("DATA")).Code.Should().Be(354);

        // One line: exactly the limit of x, then (in a later read) a dot, CR LF. The dot is the tail
        // of an over-long line, not a lone dot, so VRFY after it is still message text.
        await tcp.GetStream().WriteAsync(Enumerable.Repeat((byte)'x', 65537).ToArray(), ct);
        await Task.Delay(300, ct);
        await tcp.GetStream().WriteAsync(".\r\nVRFY x\r\n.\r\n"u8.ToArray(), ct);
        (await SmtpReply.ReadAsync(lines, ct)).Code.Should().Be(552);
        (await Cmd("HELP")).Code.Should().Be(214, "nothing inside the message was taken as a command");
        (await Cmd("QUIT")).Code.Should().Be(221);
        sink.Mail.Should().BeEmpty();
    }

    [Fact]
    public void Bare_cr_and_lf_go_to_the_relay_as_cr_lf()
    {
        // RFC 5321 2.3.8; a bare LF before a dot must not reach a relay that might end DATA on it.
        SmtpRelayClient.CrLf("a\n.\r\nb\rc\r\nd"u8.ToArray()).Should().Equal("a\r\n.\r\nb\r\nc\r\nd"u8.ToArray());
        SmtpData.Stuff(SmtpRelayClient.CrLf("x\n.\nMAIL FROM:<e@vil>\n"u8.ToArray())).Should().Equal("x\r\n..\r\nMAIL FROM:<e@vil>\r\n.\r\n"u8.ToArray());
    }

    [Fact]
    public async Task Starttls_then_auth_plain_against_a_scripted_relay()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using X509Certificate2 cert = SelfSigned();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var seen = new List<string>();
        byte[]? data = null;

        Task server = Task.Run(async () =>
        {
            using TcpClient c = await listener.AcceptTcpClientAsync(ct);
            var lines = new SmtpLines(c.GetStream());
            async Task<string> Read()
            {
                string l = (await lines.ReadTextLineAsync(4096, ct))!;
                seen.Add(l);
                return l;
            }

            await lines.WriteLineAsync("220 relay.test", ct);
            await Read();
            await lines.WriteLineAsync("250-relay.test\r\n250-STARTTLS\r\n250 8BITMIME", ct);
            (await Read()).Should().Be("STARTTLS");
            await lines.WriteLineAsync("220 go ahead", ct);
            var ssl = new SslStream(c.GetStream());
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, ct);
            lines.Replace(ssl);
            await Read();
            await lines.WriteLineAsync("250-relay.test\r\n250 AUTH LOGIN PLAIN", ct);
            await Read();
            await lines.WriteLineAsync("235 2.7.0 ok", ct);
            await Read();
            await lines.WriteLineAsync("250 ok", ct);
            await Read();
            await lines.WriteLineAsync("250 ok", ct);
            await Read();
            await lines.WriteLineAsync("354 go", ct);
            using var ms = new MemoryStream();
            while (await lines.ReadLineAsync(4096, false, ct) is { } l && !l.SequenceEqual("."u8.ToArray()))
            {
                ms.Write(l);
                ms.Write("\r\n"u8);
            }

            data = ms.ToArray();
            await lines.WriteLineAsync("250 queued", ct);
            await Read();
            await lines.WriteLineAsync("221 bye", ct);
        }, ct);

        IReadOnlyList<RecipientResult> r = await ClientFor(port, new SmtpCredentials("user", "pa ss"), tls: true)
            .SendAsync("a@x.test", ["b@x.test"], "Subject: t\r\n\r\nhi\r\n"u8.ToArray(), ct);
        await server;
        listener.Stop();
        r.Should().ContainSingle().Which.Outcome.Should().Be(RecipientOutcome.Accepted);
        seen.Should().Equal(
            "EHLO gw.test", "STARTTLS", "EHLO gw.test",
            "AUTH PLAIN " + Convert.ToBase64String("\0user\0pa ss"u8.ToArray()),
            "MAIL FROM:<a@x.test>", "RCPT TO:<b@x.test>", "DATA", "QUIT");
        Encoding.ASCII.GetString(data!).Should().Be("Subject: t\r\n\r\nhi\r\n");
    }

    [Fact]
    public async Task A_relay_without_starttls_is_not_used_when_tls_is_required()
    {
        await using var sink = new SmtpSink();
        Func<Task> act = () => ClientFor(sink.Port, tls: true).SendAsync("a@x.test", ["b@x.test"], "x\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<IOException>().WithMessage("*does not offer STARTTLS*");
        sink.Mail.Should().BeEmpty();
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pfx), null);
    }
    [Fact]
    public async Task A_relay_reply_of_more_than_100_lines_is_refused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(async () =>
        {
            using TcpClient c = await listener.AcceptTcpClientAsync(ct);
            var lines = new SmtpLines(c.GetStream());
            for (int n = 0; n < 200; n++)
            {
                await lines.WriteLineAsync("220-more", ct);
            }

            await lines.WriteLineAsync("220 done", ct);
        }, ct);

        Func<Task> send = () => ClientFor(port).SendAsync("a@x.test", ["b@x.test"], "x"u8.ToArray(), ct);
        await send.Should().ThrowAsync<IOException>().WithMessage("*more than 100 lines*");
        try
        {
            await server;
        }
        catch (IOException)
        {
            // The client hung up while the server was still writing.
        }

        listener.Stop();
    }

    /// <summary>A handler whose Deliver blocks until the test lets it go.</summary>
    private sealed class SlowHandler : ISmtpHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Go { get; } = new();

        public long MaxMessageBytes => 1_000_000;

        public SmtpReply? CheckRecipient(string returnPath, IReadOnlyList<string> accepted, string recipient) => null;

        public SmtpReply Deliver(SmtpEnvelope envelope, byte[] message)
        {
            Entered.TrySetResult();
            Go.Wait(TimeSpan.FromSeconds(30));
            return new SmtpReply(250, ["2.0.0 queued"]);
        }
    }

    [Fact]
    public async Task Shutdown_lets_a_session_finish_the_mail_it_is_taking()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var h = new SlowHandler();
        var l = new SmtpListener(new IPEndPoint(IPAddress.Loopback, 0), "gw.test", h, _ => { });
        l.Start();
        Task<IReadOnlyList<RecipientResult>> send = ClientFor(l.Endpoint.Port).SendAsync("a@x.test", ["b@x.test"], "Subject: t\r\n\r\nhi\r\n"u8.ToArray(), ct);
        await h.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);

        Task dispose = l.DisposeAsync().AsTask();
        await Task.Delay(300, ct);
        dispose.IsCompleted.Should().BeFalse("the listener waits for the open session");
        h.Go.Set();
        (await send.WaitAsync(TimeSpan.FromSeconds(20), ct)).Should().ContainSingle().Which.Outcome.Should().Be(RecipientOutcome.Accepted);
        await dispose.WaitAsync(TimeSpan.FromSeconds(20), ct);
    }

    [Fact]
    public async Task Shutdown_cuts_off_an_idle_session_after_the_grace_period()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var l = new SmtpListener(new IPEndPoint(IPAddress.Loopback, 0), "gw.test", new SlowHandler(), _ => { }) { ShutdownGrace = TimeSpan.FromMilliseconds(300) };
        l.Start();
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, l.Endpoint.Port, ct);
        var lines = new SmtpLines(c.GetStream());
        (await lines.ReadTextLineAsync(512, ct)).Should().StartWith("220 ");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await l.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20), ct);
        clock.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250)).And.BeLessThan(TimeSpan.FromSeconds(5));
    }
}
