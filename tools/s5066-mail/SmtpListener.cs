using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace M0LTE.S5066.Mail;

/// <summary>One mail as an SMTP client handed it over.</summary>
/// <param name="ReturnPath">MAIL FROM, without angle brackets; empty for the null return path.</param>
/// <param name="Recipients">RCPT TO addresses, without angle brackets, in order.</param>
/// <param name="Helo">The name the client gave in EHLO or HELO.</param>
/// <param name="Remote">The client's address.</param>
internal sealed record SmtpEnvelope(string ReturnPath, IReadOnlyList<string> Recipients, string Helo, EndPoint? Remote);

/// <summary>What an SMTP listener does with what it is given.</summary>
internal interface ISmtpHandler
{
    /// <summary>The largest message taken, in octets after the dot-stuffing is removed.</summary>
    long MaxMessageBytes { get; }

    /// <summary>Null to accept a recipient, or the reply refusing it.</summary>
    SmtpReply? CheckRecipient(string returnPath, IReadOnlyList<string> accepted, string recipient);

    /// <summary>Takes a whole message; the reply is sent to the client (250 once it is safely kept).</summary>
    SmtpReply Deliver(SmtpEnvelope envelope, byte[] message);
}

/// <summary>
/// A small SMTP server (RFC 5321) for mail on its way to HF: EHLO or HELO, MAIL FROM with SIZE
/// and BODY, RCPT TO, DATA, RSET, NOOP, VRFY, HELP and QUIT, with SIZE, 8BITMIME and
/// PIPELINING. No AUTH or TLS: it listens on localhost unless told otherwise, and takes mail
/// only for recipients the handler accepts.
/// </summary>
internal sealed class SmtpListener : IAsyncDisposable
{
    private const int MaxCommandLine = 2048;
    private const int MaxTextLine = 65536;
    private const int MaxRecipients = 1000;
    private const int MaxConnections = 16;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DataTimeout = TimeSpan.FromMinutes(10);

    private readonly TcpListener _listener;
    private readonly ISmtpHandler _handler;
    private readonly string _hostname;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections);
    private Task _accept = Task.CompletedTask;

    public SmtpListener(IPEndPoint endpoint, string hostname, ISmtpHandler handler, Action<string> log)
    {
        _listener = new TcpListener(endpoint);
        _hostname = hostname;
        _handler = handler;
        _log = log;
    }

    /// <summary>Where it listens, with the real port when 0 was asked for.</summary>
    public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndpoint;

    public void Start()
    {
        _listener.Start();
        _accept = Task.Run(AcceptLoopAsync);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _accept.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
        _slots.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            if (!_slots.Wait(0))
            {
                using (client)
                {
                    var busy = new SmtpLines(client.GetStream());
                    await Ignore(busy.WriteLineAsync($"421 4.3.2 {_hostname} too many connections, try again later", _stop.Token)).ConfigureAwait(false);
                }

                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    using (client)
                    {
                        await SessionAsync(client, _stop.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                }
                catch (Exception e)
                {
                    _log($"s5066-mail: SMTP session failed: {e.GetType().Name}: {e.Message}");
                }
                finally
                {
                    _slots.Release();
                }
            });
        }
    }

    private static async Task Ignore(Task t)
    {
        try
        {
            await t.ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
        {
        }
    }

    private async Task SessionAsync(TcpClient client, CancellationToken stop)
    {
        var lines = new SmtpLines(client.GetStream());
        EndPoint? remote = client.Client.RemoteEndPoint;
        string? helo = null;
        string? from = null;
        var rcpts = new List<string>();

        async Task Say(string reply) => await lines.WriteLineAsync(reply, stop).ConfigureAwait(false);

        await Say($"220 {_hostname} ESMTP s5066-mail").ConfigureAwait(false);
        while (true)
        {
            string? line;
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                t.CancelAfter(CommandTimeout);
                try
                {
                    line = await lines.ReadTextLineAsync(MaxCommandLine, t.Token).ConfigureAwait(false);
                }
                catch (SmtpLineTooLongException)
                {
                    await Say("500 5.5.2 line too long").ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    await Ignore(Say($"421 4.4.2 {_hostname} timeout")).ConfigureAwait(false);
                    return;
                }
            }

            if (line is null)
            {
                return;
            }

            int sp = line.IndexOf(' ', StringComparison.Ordinal);
            string verb = (sp < 0 ? line : line[..sp]).ToUpperInvariant();
            string arg = sp < 0 ? "" : line[(sp + 1)..].Trim();
            switch (verb)
            {
                case "EHLO":
                case "HELO":
                    if (arg.Length == 0)
                    {
                        await Say($"501 5.5.4 {verb} needs a name").ConfigureAwait(false);
                        break;
                    }

                    helo = arg;
                    from = null;
                    rcpts.Clear();
                    if (verb == "HELO")
                    {
                        await Say($"250 {_hostname}").ConfigureAwait(false);
                    }
                    else
                    {
                        await Say($"250-{_hostname}").ConfigureAwait(false);
                        await Say(string.Create(CultureInfo.InvariantCulture, $"250-SIZE {_handler.MaxMessageBytes}")).ConfigureAwait(false);
                        await Say("250-8BITMIME").ConfigureAwait(false);
                        await Say("250-ENHANCEDSTATUSCODES").ConfigureAwait(false);
                        await Say("250 PIPELINING").ConfigureAwait(false);
                    }

                    break;
                case "MAIL":
                    if (helo is null)
                    {
                        await Say("503 5.5.1 EHLO or HELO first").ConfigureAwait(false);
                    }
                    else if (from is not null)
                    {
                        await Say("503 5.5.1 a mail is already started").ConfigureAwait(false);
                    }
                    else if (!TryPath(arg, "FROM:", out string path, out string rest) || (path.Length > 0 && !IsAddress(path)))
                    {
                        await Say("501 5.1.7 MAIL FROM:<address> please").ConfigureAwait(false);
                    }
                    else if (SizeParameter(rest) is long size && size > _handler.MaxMessageBytes)
                    {
                        await Say(string.Create(CultureInfo.InvariantCulture, $"552 5.3.4 the most this gateway takes is {_handler.MaxMessageBytes} octets")).ConfigureAwait(false);
                    }
                    else
                    {
                        from = path;
                        await Say("250 2.1.0 ok").ConfigureAwait(false);
                    }

                    break;
                case "RCPT":
                    if (from is null)
                    {
                        await Say("503 5.5.1 MAIL first").ConfigureAwait(false);
                    }
                    else if (!TryPath(arg, "TO:", out string to, out _) || !IsAddress(to))
                    {
                        await Say("501 5.1.3 RCPT TO:<user@domain> please, in plain ASCII").ConfigureAwait(false);
                    }
                    else if (rcpts.Count >= MaxRecipients)
                    {
                        await Say("452 4.5.3 too many recipients").ConfigureAwait(false);
                    }
                    else if (_handler.CheckRecipient(from, rcpts, to) is { } refused)
                    {
                        await Say(refused.ToString()).ConfigureAwait(false);
                    }
                    else
                    {
                        rcpts.Add(to);
                        await Say("250 2.1.5 ok").ConfigureAwait(false);
                    }

                    break;
                case "DATA":
                    if (from is null || rcpts.Count == 0)
                    {
                        await Say("503 5.5.1 MAIL and RCPT first").ConfigureAwait(false);
                        break;
                    }

                    await Say("354 end with <CR><LF>.<CR><LF>").ConfigureAwait(false);
                    byte[]? message;
                    using (var t = CancellationTokenSource.CreateLinkedTokenSource(stop))
                    {
                        t.CancelAfter(DataTimeout);
                        message = await ReadDataAsync(lines, _handler.MaxMessageBytes, t.Token).ConfigureAwait(false);
                    }

                    if (message is null)
                    {
                        await Say(string.Create(CultureInfo.InvariantCulture, $"552 5.3.4 the most this gateway takes is {_handler.MaxMessageBytes} octets")).ConfigureAwait(false);
                    }
                    else
                    {
                        SmtpReply r = _handler.Deliver(new SmtpEnvelope(from, [.. rcpts], helo ?? "", remote), message);
                        await Say(r.ToString()).ConfigureAwait(false);
                    }

                    from = null;
                    rcpts.Clear();
                    break;
                case "RSET":
                    from = null;
                    rcpts.Clear();
                    await Say("250 2.0.0 ok").ConfigureAwait(false);
                    break;
                case "NOOP":
                    await Say("250 2.0.0 ok").ConfigureAwait(false);
                    break;
                case "VRFY":
                    await Say("252 2.5.0 cannot verify, but will take mail for routed recipients").ConfigureAwait(false);
                    break;
                case "HELP":
                    await Say("214 2.0.0 EHLO MAIL RCPT DATA RSET NOOP VRFY QUIT").ConfigureAwait(false);
                    break;
                case "QUIT":
                    await Say($"221 2.0.0 {_hostname} closing").ConfigureAwait(false);
                    return;
                default:
                    await Say("500 5.5.2 command not recognised").ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>
    /// Reads the DATA lines up to the lone dot, removing the dot-stuffing. Null when the message is
    /// over the limit (read to the end and dropped). Only CR LF . CR LF ends it.
    /// </summary>
    private static async Task<byte[]?> ReadDataAsync(SmtpLines lines, long max, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        bool over = false;
        while (true)
        {
            byte[] line;
            try
            {
                line = await lines.ReadLineAsync(MaxTextLine, bareLf: false, ct).ConfigureAwait(false) ?? throw new IOException("the client went away during DATA");
            }
            catch (SmtpLineTooLongException)
            {
                over = true;
                continue;
            }

            if (line is [(byte)'.'])
            {
                return over ? null : ms.ToArray();
            }

            ReadOnlySpan<byte> l = line is [(byte)'.', ..] ? line.AsSpan(1) : line;
            if (!over && ms.Length + l.Length + 2 <= max)
            {
                ms.Write(l);
                ms.Write("\r\n"u8);
            }
            else
            {
                over = true;
            }
        }
    }

    /// <summary>Reads "FROM:&lt;path&gt; params" leniently (spaces allowed after the colon).</summary>
    internal static bool TryPath(string arg, string keyword, out string path, out string rest)
    {
        path = "";
        rest = "";
        if (!arg.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string s = arg[keyword.Length..].TrimStart();
        if (!s.StartsWith('<'))
        {
            return false;
        }

        int close = s.IndexOf('>', StringComparison.Ordinal);
        if (close < 0)
        {
            return false;
        }

        path = s[1..close];
        // A source route (RFC 5321 4.1.2, obsolete) is dropped.
        int colon = path.IndexOf(':', StringComparison.Ordinal);
        if (path.StartsWith('@') && colon > 0)
        {
            path = path[(colon + 1)..];
        }

        rest = s[(close + 1)..].Trim();
        return true;
    }

    /// <summary>A plain ASCII user@domain, with no comma (CFTP separates addresses with commas, V-0057).</summary>
    internal static bool IsAddress(string a)
    {
        int at = a.LastIndexOf('@');
        return at > 0 && at < a.Length - 1 && a.Length <= 254 && a.All(c => c is > ' ' and < (char)0x7F && c is not (',' or '<' or '>'));
    }

    private static long? SizeParameter(string rest)
    {
        foreach (string p in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (p.StartsWith("SIZE=", StringComparison.OrdinalIgnoreCase) && long.TryParse(p.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out long n))
            {
                return n;
            }
        }

        return null;
    }

    /// <summary>A Received trace line for a message taken in (RFC 5321 4.4).</summary>
    internal static string ReceivedHeader(SmtpEnvelope env, string hostname, string id, DateTimeOffset now)
    {
        // The EHLO name is the client's own text: printable ASCII only, so it cannot break the header.
        string helo = new([.. env.Helo.Take(255).Select(c => c is > ' ' and < (char)0x7F ? c : '?')]);
        string from = env.Remote is IPEndPoint ip ? $"{helo} ([{ip.Address}])" : helo;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Received: from {from}\r\n\tby {hostname} (s5066-mail) with ESMTP id {id}");
        if (env.Recipients.Count == 1)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\r\n\tfor <{env.Recipients[0]}>");
        }

        sb.Append(CultureInfo.InvariantCulture, $"; {now.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture)}\r\n");
        return sb.ToString();
    }
}
