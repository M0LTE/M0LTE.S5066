using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace M0LTE.S5066.Mail;

/// <summary>A recipient a bounce reports.</summary>
/// <param name="Address">The recipient.</param>
/// <param name="Status">An enhanced status code, 5.x.y (RFC 3463).</param>
/// <param name="Diagnostic">What went wrong, in one line.</param>
internal sealed record FailedRecipient(string Address, string Status, string Diagnostic);

/// <summary>
/// Delivery status notifications (RFC 3464) for mail that could not be passed on (interp V-0092).
/// A bounce goes with the null return path, so a bounce is never bounced.
/// </summary>
internal static class Dsn
{
    /// <summary>
    /// Builds a bounce to <paramref name="returnPath"/> reporting <paramref name="failed"/>, with the
    /// original message's header section attached.
    /// </summary>
    public static byte[] Build(string hostname, string returnPath, IReadOnlyList<FailedRecipient> failed, ReadOnlySpan<byte> original, DateTimeOffset arrived, DateTimeOffset now)
    {
        string boundary = "s5066-mail-" + RandomNumberGenerator.GetHexString(16, lowercase: true);
        string date = Date(now);
        var sb = new StringBuilder();
        void Line(string s) => sb.Append(s).Append("\r\n");

        Line($"From: Mail Delivery System <MAILER-DAEMON@{hostname}>");
        Line($"To: <{returnPath}>");
        Line("Subject: Undelivered mail returned to sender");
        Line($"Date: {date}");
        Line($"Message-ID: <{RandomNumberGenerator.GetHexString(24, lowercase: true)}@{hostname}>");
        Line("Auto-Submitted: auto-replied");
        Line("MIME-Version: 1.0");
        Line($"Content-Type: multipart/report; report-type=delivery-status; boundary=\"{boundary}\"");
        Line("");
        Line("This is a delivery status notification in MIME format.");
        Line("");
        Line($"--{boundary}");
        Line("Content-Type: text/plain; charset=us-ascii");
        Line("");
        Line($"This is the STANAG 5066 mail gateway at {hostname}.");
        Line("");
        Line("Your message could not be delivered to the following recipients:");
        Line("");
        foreach (FailedRecipient f in failed)
        {
            Line($"  <{f.Address}>: {Ascii(f.Diagnostic)}");
        }

        Line("");
        Line("The header of your message is attached.");
        Line("");
        Line($"--{boundary}");
        Line("Content-Type: message/delivery-status");
        Line("");
        Line($"Reporting-MTA: dns; {hostname}");
        Line($"Arrival-Date: {Date(arrived)}");
        foreach (FailedRecipient f in failed)
        {
            Line("");
            Line($"Final-Recipient: rfc822; {f.Address}");
            Line("Action: failed");
            Line($"Status: {f.Status}");
            Line($"Diagnostic-Code: smtp; {Ascii(f.Diagnostic)}");
            Line($"Last-Attempt-Date: {date}");
        }

        Line("");
        Line($"--{boundary}");
        Line("Content-Type: text/rfc822-headers");
        Line("");
        var bytes = new List<byte>(Encoding.ASCII.GetBytes(sb.ToString()));
        bytes.AddRange(HeaderSection(original).ToArray());
        bytes.AddRange(Encoding.ASCII.GetBytes($"\r\n--{boundary}--\r\n"));
        return [.. bytes];
    }

    /// <summary>The header section of a message: everything up to the first empty line, ending CR LF.</summary>
    public static ReadOnlySpan<byte> HeaderSection(ReadOnlySpan<byte> message)
    {
        int end = message.IndexOf("\r\n\r\n"u8);
        return end < 0 ? message : message[..(end + 2)];
    }

    /// <summary>An RFC 5322 date in UTC.</summary>
    private static string Date(DateTimeOffset t) => t.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture);

    private static string Ascii(string s) => new([.. s.Select(c => c is >= ' ' and < (char)0x7F ? c : '?')]);
}
