using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using M0LTE.S5066.Cftp;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Mail;

/// <summary>
/// The CFTP mail gateway (P10-3). Mail that arrives by CFTP on SAP 12 is spooled and forwarded to
/// the SMTP relay (V-0069, interp V-0091); mail the SMTP listener takes is spooled and sent by CFTP
/// to the node its recipients map to (interp V-0090). Failures are tried again on
/// <see cref="RetrySchedule"/> and bounced at the end (interp V-0092).
/// </summary>
internal sealed class MailGateway : ISmtpHandler, IAsyncDisposable
{
    /// <summary>A mail with this many Received lines or more is refused as a loop.</summary>
    public const int MaxHops = 30;

    /// <summary>How long to wait before connecting to the node again.</summary>
    public static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(10);

    private readonly MailConfig _config;
    private readonly Action<string> _log;
    private readonly TimeProvider _time;
    private readonly CftpOptions _cftpOptions;
    private readonly SmtpRelayClient? _relay;
    private readonly Spool _spool;
    private readonly Wakeup _hfWork = new();
    private readonly Wakeup _smtpWork = new();
    private readonly object _gate = new();
    private readonly HashSet<string> _hfBusyNodes = [];
    private SmtpListener? _listener;
    private int _mtu = SisSublayerMtu;

    /// <summary>The largest MTU (A-0047), assumed until the node's bind says otherwise.</summary>
    private const int SisSublayerMtu = 2048;

    public MailGateway(MailConfig config, Action<string> log, TimeProvider? time = null, CftpOptions? cftpOptions = null, SmtpCredentials? credentials = null, bool acceptAnyRelayCertificate = false)
    {
        _config = config;
        _log = log;
        _time = time ?? TimeProvider.System;
        // The CFTP acknowledgement goes only once a received mail is in the spool (interp V-0091, V-0093).
        _cftpOptions = (cftpOptions ?? new CftpOptions()) with { AcceptByApplication = true };
        _spool = new Spool(config.Spool, Now);
        if (config.Relay is { } r)
        {
            _relay = new SmtpRelayClient(r, config.Hostname, credentials) { AcceptAnyCertificate = acceptAnyRelayCertificate };
        }
    }

    /// <summary>Where the SMTP listener is, once started.</summary>
    public IPEndPoint? ListenEndpoint => _listener?.Endpoint;

    /// <summary>Looks at both queues now, rather than at the next due time (tests move the clock).</summary>
    internal void Poke()
    {
        _hfWork.Set();
        _smtpWork.Set();
    }

    /// <summary>The spool, for tests.</summary>
    internal Spool Spool => _spool;

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>
    /// The largest message the listener takes: what fits in 65536 segments of the MTU less 4
    /// (interp V-0087) uncompressed, less room for the file and mail headers (interp V-0090).
    /// </summary>
    public long MaxMessageBytes
    {
        get
        {
            long room = (long)CftpEngine.MaxSegments * (Volatile.Read(ref _mtu) - 4);
            return room - (room / 100) - CftpRecipients.MaxLength - 1024;
        }
    }

    /// <summary>Starts the SMTP listener, when the configuration has one.</summary>
    public void Start()
    {
        if (_config.Listen is { } ep)
        {
            _listener = new SmtpListener(ep, _config.Hostname, this, _log);
            _listener.Start();
        }
    }

    /// <summary>
    /// Runs until cancelled: the relay queue, and sessions with the node (connecting again after
    /// <see cref="ReconnectDelay"/> whenever one ends).
    /// </summary>
    public async Task RunAsync(Func<CancellationToken, Task<SisClient>> connect, CancellationToken ct)
    {
        Task relay = Task.Run(() => RelayLoopAsync(ct), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SessionAsync(connect, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e) when (e is IOException or SocketException or SisBindRejectedException or InvalidOperationException or OperationCanceledException)
            {
                _log($"s5066-mail: node {_config.SisHost}:{_config.SisPort}: {e.Message}; trying again in {ReconnectDelay.TotalSeconds:0} s");
            }

            try
            {
                await Task.Delay(ReconnectDelay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        try
        {
            await relay.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ SMTP in, to HF

    /// <inheritdoc/>
    public SmtpReply? CheckRecipient(string returnPath, IReadOnlyList<string> accepted, string recipient)
    {
        if (_config.Routes.Lookup(recipient) is not { } node)
        {
            return new SmtpReply(550, [$"5.1.2 no STANAG 5066 route for <{recipient}>"]);
        }

        // One CFTP mail per node; its RecipientList must stay under 10240 characters (V-0060).
        int length = returnPath.Length + 1 + recipient.Length;
        foreach (string a in accepted)
        {
            if (_config.Routes.Lookup(a) == node)
            {
                length += a.Length + 1;
            }
        }

        return length < CftpRecipients.MaxLength ? null : new SmtpReply(452, ["4.5.3 too many recipients for one CFTP mail; send the rest separately"]);
    }

    /// <inheritdoc/>
    public SmtpReply Deliver(SmtpEnvelope envelope, byte[] message)
    {
        if (CountReceived(message) >= MaxHops)
        {
            _log($"s5066-mail: refused mail from <{envelope.ReturnPath}>: {MaxHops} or more Received lines, a mail loop");
            return new SmtpReply(554, ["5.4.6 mail loop: too many hops"]);
        }

        DateTimeOffset now = Now;
        string id = Spool.NewId(now);
        byte[] traced = [.. Encoding.ASCII.GetBytes(SmtpListener.ReceivedHeader(envelope, _config.Hostname, id, now)), .. message];
        try
        {
            // One spool item per node, all queued or none (Spool.AddAll). A part already queued
            // under the same key, from a try the client did not see succeed, is not queued again.
            var queued = _spool.List(SpoolQueue.Hf).Where(i => i.Key is not null).ToDictionary(i => i.Key!, i => i.Id, StringComparer.Ordinal);
            var parts = new List<(SpoolItem Item, byte[] Message)>();
            int n = 0;
            foreach (IGrouping<SisAddress, string> g in envelope.Recipients.GroupBy(r => _config.Routes.Lookup(r)!.Value))
            {
                string itemId = n++ == 0 ? id : id + "-" + n.ToString(CultureInfo.InvariantCulture);
                string key = PartKey(envelope.ReturnPath, g.Key, [.. g], message);
                if (queued.TryGetValue(key, out string? already))
                {
                    _log($"s5066-mail: mail from <{envelope.ReturnPath}> for node {g.Key.Dotted} is already queued as {already}; not queued again");
                    continue;
                }

                parts.Add((new SpoolItem(itemId, SpoolQueue.Hf, now, envelope.ReturnPath, [.. g], g.Key.Dotted, CftpMail.NewMessageId(), null) { NextAttempt = now, Key = key }, traced));
            }

            _spool.AddAll(parts);
            foreach ((SpoolItem item, _) in parts)
            {
                _log($"s5066-mail: {item.Id} taken from <{envelope.ReturnPath}> for {item.Recipients.Count} recipient(s) at node {item.Node}, {traced.Length} octets");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log($"s5066-mail: could not spool a mail from <{envelope.ReturnPath}>: {e.Message}");
            return new SmtpReply(451, ["4.3.0 could not queue the mail, try again later"]);
        }

        _hfWork.Set();
        return new SmtpReply(250, [$"2.0.0 queued as {id}"]);
    }

    /// <summary>What makes one node's part of a mail the same mail when a client tries again.</summary>
    internal static string PartKey(string returnPath, SisAddress node, IReadOnlyList<string> recipients, byte[] message)
    {
        using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        h.AppendData(Encoding.UTF8.GetBytes(returnPath + "\n" + node.Dotted + "\n" + string.Join(",", recipients) + "\n"));
        h.AppendData(message);
        return Convert.ToHexString(h.GetHashAndReset());
    }

    /// <summary>The trace line put on a mail from HF before it goes to the relay (RFC 5321 4.4).</summary>
    internal static string HfReceivedHeader(SisAddress from, string hostname, string id, IReadOnlyList<string> recipients, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Received: from s5066-{from.Dotted.Replace('.', '-')}\r\n\tby {hostname} (s5066-mail) via STANAG-5066 with CFTP id {id}");
        if (recipients.Count == 1)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\r\n\tfor <{recipients[0]}>");
        }

        sb.Append(CultureInfo.InvariantCulture, $"; {now.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture)}\r\n");
        return sb.ToString();
    }

    private static int CountReceived(ReadOnlySpan<byte> message)
    {
        ReadOnlySpan<byte> headers = Dsn.HeaderSection(message);
        int count = 0;
        foreach (string line in Encoding.Latin1.GetString(headers).Split("\r\n"))
        {
            if (line.StartsWith("Received:", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    // ------------------------------------------------------------------ the node session

    private async Task SessionAsync(Func<CancellationToken, Task<SisClient>> connect, CancellationToken ct)
    {
        await using SisClient sis = await connect(ct).ConfigureAwait(false);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sis.Unbound += reason =>
        {
            _log($"s5066-mail: the node unbound SAP {CftpClient.Sap}, reason {reason}");
            session.Cancel();
        };

        await using CftpClient cftp = await CftpClient.BindAsync(sis, _cftpOptions, ownsSis: false, ct).ConfigureAwait(false);
        cftp.Note += n => _log($"s5066-mail: cftp: {n}");
        Volatile.Write(ref _mtu, cftp.Mtu);
        _log($"s5066-mail: bound SAP {CftpClient.Sap} at node {_config.SisHost}:{_config.SisPort}, MTU {cftp.Mtu}");
        _hfWork.Set();

        Task closed = sis.Received.Completion;
        Task receive = ReceiveLoopAsync(cftp, session.Token);
        Task send = SendLoopAsync(cftp, session.Token);
        Task first = await Task.WhenAny(closed, receive, send, Task.Delay(Timeout.Infinite, session.Token)).ConfigureAwait(false);
        await session.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(Quiet(receive), Quiet(send)).ConfigureAwait(false);
        if (first == closed && !ct.IsCancellationRequested)
        {
            throw new IOException("the SIS connection closed");
        }

        if (first.IsFaulted)
        {
            await first.ConfigureAwait(false);
        }
    }

    private static async Task Quiet(Task t)
    {
        try
        {
            await t.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Mail from HF: spooled for the relay, unless the same node sent the same MessageID within a
    /// week (interp V-0092), and only then accepted, so the CFTP acknowledgement goes once it is on
    /// disk (interp V-0091, V-0093). A mail that cannot be spooled is refused, so the far end sends
    /// it again. A file that will never be delivered (not a mail, a bad address, a loop) is accepted
    /// and dropped, since sending it again would not help (interp V-0079).
    /// </summary>
    private async Task ReceiveLoopAsync(CftpClient cftp, CancellationToken ct)
    {
        await foreach (CftpReceived r in cftp.Received.ReadAllAsync(ct).ConfigureAwait(false))
        {
            string from = r.From.Dotted;
            if (r.Mail is not { } mail)
            {
                r.Accept();
                _log($"s5066-mail: a file from node {from} is not a CFTP mail ({r.Error}); dropped (interp V-0079)");
                continue;
            }

            // The MessageID keys duplicate suppression and appears in the spool and the log; one that
            // is not plain letters and digits (interp V-0085) is replaced by a digest of itself.
            string messageId = SafeMessageId(mail.MessageId);
            string returnPath = Unbracket(mail.Envelope.ReturnPath);
            List<string> recipients = [.. mail.Envelope.Recipients.Select(Unbracket)];
            if ((returnPath.Length > 0 && !SmtpListener.IsAddress(returnPath)) || !recipients.All(SmtpListener.IsAddress))
            {
                // The addresses go into SMTP commands to the relay: refuse anything that is not a
                // plain ASCII user@domain rather than pass on control characters.
                r.Accept();
                _log($"s5066-mail: mail {messageId} from node {from} has an address that is not a plain user@domain; dropped");
                continue;
            }

            if (CountReceived(mail.Message.Span) >= MaxHops)
            {
                r.Accept();
                _log($"s5066-mail: mail {messageId} from node {from} has {MaxHops} or more Received lines, a mail loop; dropped");
                continue;
            }

            DateTimeOffset now = Now;
            if (_spool.Seen(from, messageId, now))
            {
                r.Accept();
                _log($"s5066-mail: mail {messageId} from node {from} was already taken in; not forwarded again");
                continue;
            }

            var item = new SpoolItem(Spool.NewId(now), SpoolQueue.Smtp, now, returnPath,
                recipients, null, messageId, from) { NextAttempt = now };
            byte[] traced = [.. Encoding.ASCII.GetBytes(HfReceivedHeader(r.From, _config.Hostname, item.Id, recipients, now)), .. mail.Message.Span];
            try
            {
                _spool.Add(item, traced);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                r.Refuse();
                _log($"s5066-mail: could not spool mail {messageId} from node {from}: {e.Message}; not acknowledged, so it comes again");
                continue;
            }

            try
            {
                _spool.Remember(from, messageId, now);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The mail is safe; forgetting its MessageID risks only a duplicate if it comes again.
                _log($"s5066-mail: could not note mail {messageId} from node {from} as received: {e.Message}");
            }

            bool acked = r.Accept();
            _log($"s5066-mail: {item.Id} mail {messageId} from node {from} for {item.Recipients.Count} recipient(s), {mail.Message.Length} octets"
                + (acked || !r.AwaitsAccept ? "" : "; too late to acknowledge, the far end will send it again"));
            _smtpWork.Set();
        }
    }

    /// <summary>Mail to HF: one file at a time to each node, every due mail in the spool.</summary>
    private async Task SendLoopAsync(CftpClient cftp, CancellationToken ct)
    {
        var running = new List<Task>();
        while (!ct.IsCancellationRequested)
        {
            Task wake = _hfWork.Wait();
            DateTimeOffset now = Now;
            DateTimeOffset? next = null;
            foreach (SpoolItem item in _spool.List(SpoolQueue.Hf, _log))
            {
                lock (_gate)
                {
                    if (_hfBusyNodes.Contains(item.Node!))
                    {
                        continue;
                    }

                    if (item.NextAttempt > now)
                    {
                        next = next is { } n && n < item.NextAttempt ? n : item.NextAttempt;
                        continue;
                    }

                    _hfBusyNodes.Add(item.Node!);
                }

                running.Add(SendOneAsync(cftp, item, ct));
            }

            running.RemoveAll(t => t.IsCompleted);
            TimeSpan wait = next is { } d ? (d - now).Duration() + TimeSpan.FromMilliseconds(50) : TimeSpan.FromMinutes(1);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await Task.WhenAny(wake, Task.Delay(wait, _time, cts.Token)).ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(running.Select(Quiet)).ConfigureAwait(false);
    }

    private async Task SendOneAsync(CftpClient cftp, SpoolItem item, CancellationToken ct)
    {
        try
        {
            SisAddress to = MailConfig.Address(item.Node!, "spool node");
            byte[] message = _spool.Message(item);
            var mail = new CftpMail(item.MessageId, new CftpRecipients(item.ReturnPath, item.Recipients), message);
            CftpSendResult result;
            try
            {
                result = await cftp.SendAsync(to, mail, ct).ConfigureAwait(false);
            }
            catch (ArgumentException e)
            {
                _log($"s5066-mail: {item.Id} cannot go by CFTP: {e.Message}");
                Bounce(item, [.. item.Recipients.Select(a => new FailedRecipient(a, "5.3.4", "cannot be sent by CFTP: " + e.Message))], message);
                _spool.Remove(item);
                return;
            }

            if (result.Acknowledged)
            {
                _spool.Remove(item);
                _log($"s5066-mail: {item.Id} delivered to node {item.Node} as CFTP mail {item.MessageId}, {result.CompressedBytes} octets compressed");
                return;
            }

            Failed(item, $"not acknowledged by node {item.Node}: {result.Failure}", message);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException)
        {
            // The session ended under it; the mail stays as it was and goes in the next session.
        }
        catch (Exception e) when (e is MailConfigException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            _log($"s5066-mail: {item.Id}: {e.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _hfBusyNodes.Remove(item.Node!);
            }

            _hfWork.Set();
        }
    }

    // ------------------------------------------------------------------ SMTP out, to the relay

    private async Task RelayLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Task wake = _smtpWork.Wait();
            DateTimeOffset now = Now;
            DateTimeOffset? next = null;
            foreach (SpoolItem item in _spool.List(SpoolQueue.Smtp, _log))
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (item.NextAttempt > now)
                {
                    next = next is { } n && n < item.NextAttempt ? n : item.NextAttempt;
                    continue;
                }

                await RelayOneAsync(item, ct).ConfigureAwait(false);
            }

            TimeSpan wait = next is { } d ? (d - Now).Duration() + TimeSpan.FromMilliseconds(50) : TimeSpan.FromMinutes(1);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                await Task.WhenAny(wake, Task.Delay(wait, _time, cts.Token)).ConfigureAwait(false);
            }
            finally
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Hands one mail to the relay (V-0069, interp V-0091).</summary>
    private async Task RelayOneAsync(SpoolItem item, CancellationToken ct)
    {
        byte[] message;
        try
        {
            message = _spool.Message(item);
        }
        catch (IOException e)
        {
            _log($"s5066-mail: {item.Id}: {e.Message}");
            return;
        }

        IReadOnlyList<RecipientResult> results;
        try
        {
            results = await _relay!.SendAsync(item.ReturnPath, item.Recipients, message, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException or MailConfigException)
        {
            Failed(item, $"relay {_config.Relay!.Host}:{_config.Relay.Port}: {e.Message}", message);
            return;
        }

        List<RecipientResult> permanent = [.. results.Where(r => r.Outcome == RecipientOutcome.Permanent)];
        List<string> transient = [.. results.Where(r => r.Outcome == RecipientOutcome.Transient).Select(r => r.Address)];
        int accepted = results.Count(r => r.Outcome == RecipientOutcome.Accepted);
        if (accepted > 0)
        {
            _log($"s5066-mail: {item.Id} (mail {item.MessageId} from node {item.From}) relayed for {accepted} recipient(s)");
        }

        if (permanent.Count > 0)
        {
            _log($"s5066-mail: {item.Id} refused by the relay for {string.Join(", ", permanent.Select(p => $"<{p.Address}> ({p.Reply})"))}");
            Bounce(item, [.. permanent.Select(p => new FailedRecipient(p.Address, Status(p.Reply, "5.0.0"), p.Reply))], message);
        }

        if (transient.Count == 0)
        {
            _spool.Remove(item);
            return;
        }

        string reason = results.First(r => r.Outcome == RecipientOutcome.Transient).Reply;
        Failed(item with { Recipients = transient }, "relay: " + reason, message);
    }

    // ------------------------------------------------------------------ failures

    /// <summary>A try failed: schedule the next, or bounce when it has been tried long enough (interp V-0092).</summary>
    private void Failed(SpoolItem item, string reason, byte[] message)
    {
        DateTimeOffset now = Now;
        int failures = item.Failures + 1;
        if (RetrySchedule.Next(item.Created, failures, now) is { } next)
        {
            _spool.Save(item with { Failures = failures, NextAttempt = next, LastError = reason });
            _log($"s5066-mail: {item.Id} not delivered ({reason}); try {failures + 1} in {Minutes(next - now)}");
            return;
        }

        _log($"s5066-mail: {item.Id} not delivered after {failures} tries over {Minutes(now - item.Created)} ({reason}); bounced");
        Bounce(item, [.. item.Recipients.Select(a => new FailedRecipient(a, "4.4.7", "gave up after " + Minutes(now - item.Created) + ": " + reason))], message);
        _spool.Remove(item);
    }

    /// <summary>
    /// Sends a bounce to the return path, by HF when a route matches it and by the relay otherwise.
    /// A mail with the null return path is not bounced.
    /// </summary>
    private void Bounce(SpoolItem item, IReadOnlyList<FailedRecipient> failed, byte[] message)
    {
        if (item.ReturnPath.Length == 0)
        {
            _log($"s5066-mail: {item.Id} has no return path; no bounce sent");
            return;
        }

        DateTimeOffset now = Now;
        byte[] dsn = Dsn.Build(_config.Hostname, item.ReturnPath, failed, message, item.Created, now);
        string id = Spool.NewId(now);
        SisAddress? node = _config.Routes.Lookup(item.ReturnPath);
        SpoolItem b = node is { } n
            ? new SpoolItem(id, SpoolQueue.Hf, now, "", [item.ReturnPath], n.Dotted, CftpMail.NewMessageId(), null) { NextAttempt = now }
            : new SpoolItem(id, SpoolQueue.Smtp, now, "", [item.ReturnPath], null, "", null) { NextAttempt = now };
        try
        {
            _spool.Add(b, dsn);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log($"s5066-mail: could not spool the bounce for {item.Id}: {e.Message}");
            return;
        }

        _log($"s5066-mail: {id} bounce for {item.Id} to <{item.ReturnPath}>" + (node is { } nn ? $" by HF to node {nn.Dotted}" : " by the relay"));
        (node is null ? _smtpWork : _hfWork).Set();
    }

    private static string Status(string reply, string fallback)
    {
        string[] p = reply.Split(' ');
        return p.Length > 1 && p[1].Length >= 5 && p[1][0] is '4' or '5' && p[1][1] == '.' ? "5" + p[1][1..] : fallback;
    }

    private static string Minutes(TimeSpan t) => t.TotalMinutes < 120
        ? string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, t.TotalMinutes):0} min")
        : string.Create(CultureInfo.InvariantCulture, $"{t.TotalHours:0.#} h");

    /// <summary>The MessageID if it is 1 to 255 ASCII letters and digits, otherwise a digest of it.</summary>
    internal static string SafeMessageId(string id) =>
        id.Length is > 0 and < CftpMail.MaxMessageIdLength && id.All(char.IsAsciiLetterOrDigit)
            ? id
            : "x" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.Latin1.GetBytes(id)))[..40];

    /// <summary>An address as written in a RecipientList, without angle brackets (interp V-0091).</summary>
    internal static string Unbracket(string a)
    {
        string t = a.Trim();
        return t.Length >= 2 && t[0] == '<' && t[^1] == '>' ? t[1..^1].Trim() : t;
    }
}

/// <summary>A wake-up a loop waits on and anything can set.</summary>
internal sealed class Wakeup
{
    private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();

    /// <summary>A task that completes at the next <see cref="Set"/>, or at once if one came since the last wait.</summary>
    public Task Wait()
    {
        lock (_gate)
        {
            if (_tcs.Task.IsCompleted)
            {
                _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return Task.CompletedTask;
            }

            return _tcs.Task;
        }
    }

    public void Set()
    {
        lock (_gate)
        {
            _tcs.TrySetResult();
        }
    }
}
