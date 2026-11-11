using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using M0LTE.S5066.Cftp;

namespace M0LTE.S5066.Mail;

/// <summary>What happened to one recipient of a mail handed to the relay.</summary>
internal enum RecipientOutcome
{
    Accepted,
    Transient,
    Permanent,
}

/// <summary>The relay's answer for one recipient.</summary>
internal sealed record RecipientResult(string Address, RecipientOutcome Outcome, string Reply);

/// <summary>A user name and password for SMTP AUTH.</summary>
internal sealed record SmtpCredentials(string User, string Password)
{
    /// <summary>
    /// Reads a credentials file: the user name on the first line, the password on the second.
    /// On Linux the file must not be readable by group or others.
    /// </summary>
    public static SmtpCredentials Load(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
            {
                throw new MailConfigException($"relay.credentials: {path} is readable by others; chmod 600 it, owned by the service user");
            }
        }

        string[] lines = File.ReadAllLines(path);
        return lines.Length >= 2 && lines[0].Length > 0
            ? new SmtpCredentials(lines[0], lines[1])
            : throw new MailConfigException($"relay.credentials: {path} needs the user name on line 1 and the password on line 2");
    }
}

/// <summary>
/// A minimal SMTP client (RFC 5321) for the relay: EHLO (HELO when refused), STARTTLS when asked
/// for, AUTH PLAIN or LOGIN when credentials are given, then one mail. Mail from HF is forwarded
/// with MAIL FROM the CFTP return path and RCPT TO each recipient, and the Message field as DATA
/// (V-0069, interp V-0091).
/// </summary>
internal sealed class SmtpRelayClient(RelayConfig relay, string hostname, SmtpCredentials? credentials)
{
    /// <summary>The longest the relay may take over one step.</summary>
    public static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(5);

    /// <summary>For tests: accept any server certificate.</summary>
    public bool AcceptAnyCertificate { get; init; }

    /// <summary>
    /// Sends one mail. Returns the result per recipient; a failure of the whole transaction is
    /// given to every recipient. A connection failure throws <see cref="IOException"/> or
    /// <see cref="SocketException"/>, which the caller treats as transient.
    /// </summary>
    public async Task<IReadOnlyList<RecipientResult>> SendAsync(string returnPath, IReadOnlyList<string> recipients, byte[] message, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(StepTimeout * 4);
        CancellationToken ct = cts.Token;
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(relay.Host, relay.Port, ct).ConfigureAwait(false);
        Stream stream = tcp.GetStream();
        var lines = new SmtpLines(stream);
        try
        {
            return await DialogueAsync(lines, returnPath, recipients, message, ct).ConfigureAwait(false);
        }
        finally
        {
            await lines.Stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<RecipientResult>> DialogueAsync(SmtpLines lines, string returnPath, IReadOnlyList<string> recipients, byte[] message, CancellationToken ct)
    {
        IReadOnlyList<RecipientResult> All(SmtpReply r) => [.. recipients.Select(a => new RecipientResult(a, r.Transient ? RecipientOutcome.Transient : RecipientOutcome.Permanent, r.ToString()))];

        SmtpReply greeting = await SmtpReply.ReadAsync(lines, ct).ConfigureAwait(false);
        if (!greeting.Positive)
        {
            return All(greeting);
        }

        (SmtpReply ehlo, HashSet<string> ext) = await HelloAsync(lines, ct).ConfigureAwait(false);
        if (!ehlo.Positive)
        {
            return All(ehlo);
        }

        if (relay.StartTls)
        {
            if (!ext.Contains("STARTTLS"))
            {
                throw new IOException($"the relay {relay.Host} does not offer STARTTLS, which the configuration requires");
            }

            SmtpReply go = await CommandAsync(lines, "STARTTLS", ct).ConfigureAwait(false);
            if (go.Code != 220)
            {
                throw new IOException($"STARTTLS refused: {go}");
            }

            var ssl = new SslStream(lines.Stream, leaveInnerStreamOpen: false, AcceptAnyCertificate ? (_, _, _, _) => true : null);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = relay.Host }, ct).ConfigureAwait(false);
            lines.Replace(ssl);
            (ehlo, ext) = await HelloAsync(lines, ct).ConfigureAwait(false);
            if (!ehlo.Positive)
            {
                return All(ehlo);
            }
        }

        if (credentials is { } c)
        {
            await AuthenticateAsync(lines, ext, c, ct).ConfigureAwait(false);
        }

        bool eightBit = message.Any(b => b >= 0x80);
        string body = eightBit && ext.Contains("8BITMIME") ? " BODY=8BITMIME" : "";
        SmtpReply mail = await CommandAsync(lines, $"MAIL FROM:<{returnPath}>{body}", ct).ConfigureAwait(false);
        if (!mail.Positive)
        {
            return All(mail);
        }

        var results = new List<RecipientResult>();
        foreach (string r in recipients)
        {
            SmtpReply rr = await CommandAsync(lines, $"RCPT TO:<{r}>", ct).ConfigureAwait(false);
            results.Add(new RecipientResult(r, rr.Positive ? RecipientOutcome.Accepted : rr.Transient ? RecipientOutcome.Transient : RecipientOutcome.Permanent, rr.ToString()));
        }

        if (results.All(r => r.Outcome != RecipientOutcome.Accepted))
        {
            await QuitAsync(lines, ct).ConfigureAwait(false);
            return results;
        }

        SmtpReply data = await CommandAsync(lines, "DATA", ct).ConfigureAwait(false);
        if (data.Code != 354)
        {
            return [.. results.Select(r => r.Outcome == RecipientOutcome.Accepted ? r with { Outcome = data.Transient ? RecipientOutcome.Transient : RecipientOutcome.Permanent, Reply = data.ToString() } : r)];
        }

        await lines.Stream.WriteAsync(SmtpData.Stuff(CrLf(message)), ct).ConfigureAwait(false);
        await lines.Stream.FlushAsync(ct).ConfigureAwait(false);
        SmtpReply done = await SmtpReply.ReadAsync(lines, ct).ConfigureAwait(false);
        await QuitAsync(lines, ct).ConfigureAwait(false);
        return [.. results.Select(r => r.Outcome == RecipientOutcome.Accepted && !done.Positive
            ? r with { Outcome = done.Transient ? RecipientOutcome.Transient : RecipientOutcome.Permanent, Reply = done.ToString() }
            : r)];
    }

    /// <summary>
    /// The message with every bare CR and bare LF made CR LF: they are not sent on the wire (RFC 5321
    /// 2.3.8), and a relay that took a bare LF as a line end could be fed a second mail inside the
    /// first (SMTP smuggling).
    /// </summary>
    internal static byte[] CrLf(byte[] message)
    {
        using var ms = new MemoryStream(message.Length + 16);
        for (int i = 0; i < message.Length; i++)
        {
            byte c = message[i];
            if (c == '\r')
            {
                ms.Write("\r\n"u8);
                if (i + 1 < message.Length && message[i + 1] == '\n')
                {
                    i++;
                }
            }
            else if (c == '\n')
            {
                ms.Write("\r\n"u8);
            }
            else
            {
                ms.WriteByte(c);
            }
        }

        return ms.ToArray();
    }

    private async Task<(SmtpReply Reply, HashSet<string> Extensions)> HelloAsync(SmtpLines lines, CancellationToken ct)
    {
        SmtpReply ehlo = await CommandAsync(lines, "EHLO " + hostname, ct).ConfigureAwait(false);
        if (ehlo.Positive)
        {
            return (ehlo, [.. ehlo.Lines.Skip(1).Select(l => l.ToUpperInvariant())]);
        }

        SmtpReply helo = await CommandAsync(lines, "HELO " + hostname, ct).ConfigureAwait(false);
        return (helo, []);
    }

    private static async Task AuthenticateAsync(SmtpLines lines, HashSet<string> ext, SmtpCredentials c, CancellationToken ct)
    {
        string? auth = ext.FirstOrDefault(e => e.StartsWith("AUTH ", StringComparison.Ordinal) || e.StartsWith("AUTH=", StringComparison.Ordinal));
        string[] mechanisms = auth is null ? [] : auth[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        SmtpReply r;
        if (mechanisms.Contains("PLAIN"))
        {
            string token = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0" + c.User + "\0" + c.Password));
            r = await CommandAsync(lines, "AUTH PLAIN " + token, ct).ConfigureAwait(false);
        }
        else if (mechanisms.Contains("LOGIN"))
        {
            r = await CommandAsync(lines, "AUTH LOGIN", ct).ConfigureAwait(false);
            if (r.Code == 334)
            {
                r = await CommandAsync(lines, Convert.ToBase64String(Encoding.UTF8.GetBytes(c.User)), ct).ConfigureAwait(false);
            }

            if (r.Code == 334)
            {
                r = await CommandAsync(lines, Convert.ToBase64String(Encoding.UTF8.GetBytes(c.Password)), ct).ConfigureAwait(false);
            }
        }
        else
        {
            throw new IOException("the relay offers neither AUTH PLAIN nor AUTH LOGIN" + (auth is null ? " (STARTTLS first?)" : ""));
        }

        if (r.Code != 235)
        {
            // Wrong credentials are the operator's to fix; the mail waits and is tried again.
            throw new IOException($"the relay refused the credentials: {r}");
        }
    }

    private static async Task<SmtpReply> CommandAsync(SmtpLines lines, string command, CancellationToken ct)
    {
        await lines.WriteLineAsync(command, ct).ConfigureAwait(false);
        return await SmtpReply.ReadAsync(lines, ct).ConfigureAwait(false);
    }

    private static async Task QuitAsync(SmtpLines lines, CancellationToken ct)
    {
        try
        {
            await CommandAsync(lines, "QUIT", ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }
}
