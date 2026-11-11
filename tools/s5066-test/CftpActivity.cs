using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using M0LTE.S5066.Cftp;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.TestClient;

/// <summary>A CFTP mail this station received.</summary>
internal sealed record MailReceived(SisAddress From, string? MessageId, string? ReturnPath, IReadOnlyList<string> Recipients, long Bytes, string? Sha256, string? Error, IReadOnlyList<string> SavedTo);

/// <summary>The outcome of a CFTP send.</summary>
internal sealed record CftpSendOutcome(
    string To, string MessageId, string Name, long MessageBytes, int CompressedBytes, string Sha256,
    bool Acknowledged, int Attempts, string? Failure, double ElapsedMs, double? GoodputBytesPerSecond);

/// <summary>The outcome of waiting for a CFTP mail.</summary>
internal sealed record CftpExpectOutcome(string From, string? MessageId, IReadOnlyList<string> Recipients, long Bytes, string? Sha256, bool? MatchesSent, string? Error, IReadOnlyList<string> SavedTo, double WaitedMs);

/// <summary>
/// The file activity over CFTP (Annex V, SAP 12, plan 11.6): a file goes as a mail, so any CFTP
/// node can take it. An .eml file goes as it is; anything else is wrapped as a base64 MIME
/// attachment; <c>bytes</c> sends a text mail of that size.
/// </summary>
internal static class CftpActivity
{
    /// <summary>What each MessageID sent from this process should arrive as, so an expect step can check it.</summary>
    private static readonly ConcurrentDictionary<string, string> SentHashes = new();

    /// <summary>The message for a file: an .eml as it is, anything else as an attachment.</summary>
    public static byte[] MessageFor(string name, byte[] data, string from, string to)
    {
        if (name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
        {
            return data;
        }

        string safe = CftpFileNames.Safe(name);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"From: <{from}>\r\nTo: <{to}>\r\nSubject: s5066-test file {safe}\r\n");
        sb.Append("MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"s5066-test\"\r\n\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"--s5066-test\r\nContent-Type: text/plain\r\n\r\nFile {safe}, {data.Length} bytes, sent by s5066-test over CFTP.\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"--s5066-test\r\nContent-Type: application/octet-stream\r\nContent-Transfer-Encoding: base64\r\nContent-Disposition: attachment; filename=\"{safe}\"\r\n\r\n");
        string b64 = Convert.ToBase64String(data);
        for (int i = 0; i < b64.Length; i += 76)
        {
            sb.Append(b64.AsSpan(i, Math.Min(76, b64.Length - i))).Append("\r\n");
        }

        sb.Append("--s5066-test--\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>A plain text mail with a body of about <paramref name="bytes"/> random printable characters.</summary>
    public static byte[] TextMessage(int bytes, string from, string to)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"From: <{from}>\r\nTo: <{to}>\r\nSubject: s5066-test {bytes} bytes\r\n\r\n");
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,";
        for (int done = 0; done < bytes; done += 74)
        {
            sb.Append(RandomNumberGenerator.GetString(chars, Math.Min(72, Math.Max(1, bytes - done)))).Append("\r\n");
        }

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Sends one mail over CFTP and waits for its acknowledgement or failure.</summary>
    public static async Task<CftpSendOutcome> SendAsync(Station s, SisAddress to, string name, byte[] message, string? returnPath, IReadOnlyList<string>? recipients, CancellationToken cancellationToken)
    {
        CftpClient c = s.Cftp ?? throw new InvalidOperationException("this node did not let s5066-test bind SAP 12 for CFTP");
        var env = new CftpRecipients(returnPath ?? DefaultFrom, recipients is { Count: > 0 } ? recipients : [DefaultTo(to)]);
        var mail = new CftpMail(CftpMail.NewMessageId(), env, message);
        string hash = Convert.ToHexStringLower(SHA256.HashData(message));
        SentHashes[mail.MessageId] = hash;
        long t0 = Stopwatch.GetTimestamp();
        CftpSendResult r = await c.SendAsync(to, mail, cancellationToken).ConfigureAwait(false);
        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return new CftpSendOutcome(Addresses.Show(to), mail.MessageId, name, message.Length, r.CompressedBytes, hash, r.Acknowledged, r.Attempts, r.Failure,
            Math.Round(ms, 1), r.Acknowledged && ms > 0 ? Math.Round(message.Length * 1000.0 / ms, 1) : null);
    }

    /// <summary>Waits for a mail, from a node if given; checks it against what this process sent under its MessageID.</summary>
    public static async Task<CftpExpectOutcome> ExpectAsync(Station s, SisAddress? from, CancellationToken cancellationToken)
    {
        long t0 = Stopwatch.GetTimestamp();
        MailReceived m = await s.Mails.TakeAsync(x => from is null || x.From.Value == from.Value.Value, cancellationToken).ConfigureAwait(false);
        bool? match = m.MessageId is not null && SentHashes.TryGetValue(m.MessageId, out string? want) ? want == m.Sha256 : null;
        return new CftpExpectOutcome(Addresses.Show(m.From), m.MessageId, m.Recipients, m.Bytes, m.Sha256, match, m.Error, m.SavedTo,
            Math.Round(Stopwatch.GetElapsedTime(t0).TotalMilliseconds, 1));
    }

    /// <summary>The return path s5066-test uses unless told otherwise.</summary>
    public const string DefaultFrom = "s5066-test@localhost";

    /// <summary>The recipient s5066-test uses unless told otherwise.</summary>
    public static string DefaultTo(SisAddress to) => $"postmaster@{Addresses.Show(to)}";

    /// <summary>Turns a received file into a <see cref="MailReceived"/>, saving it when asked (V-0041, V-0055, interp V-0084).</summary>
    public static MailReceived Receive(CftpReceived r, string? saveDir)
    {
        var saved = new List<string>();
        if (saveDir is not null)
        {
            Directory.CreateDirectory(saveDir);
            string p = Path.Combine(saveDir, CftpFileNames.Safe(r.FileName));
            File.WriteAllBytes(p, r.CompressedFile);
            saved.Add(p);
            if (r.Mail is { } mail)
            {
                string root = Path.Combine(saveDir, CftpFileNames.Safe(mail.MessageId));
                File.WriteAllBytes(root + ".eml", mail.Message.ToArray());
                File.WriteAllLines(root + ".rcpt", mail.Envelope.Recipients.Prepend(mail.Envelope.ReturnPath));
                saved.Add(root + ".eml");
                saved.Add(root + ".rcpt");
            }
        }

        return r.Mail is { } m
            ? new MailReceived(r.From, m.MessageId, m.Envelope.ReturnPath, m.Envelope.Recipients, m.Message.Length, Convert.ToHexStringLower(SHA256.HashData(m.Message.Span)), null, saved)
            : new MailReceived(r.From, null, null, [], 0, null, r.Error, saved);
    }
}
