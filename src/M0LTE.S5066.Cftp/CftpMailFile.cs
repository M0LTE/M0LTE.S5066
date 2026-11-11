using System.Globalization;
using System.Text;

namespace M0LTE.S5066.Cftp;

/// <summary>
/// The uncompressed CFTP mail file (Table V-1, V.7 step 3): MessageID, RecipientList and
/// MessageSize, each ended by a line feed, then the Message (V-0047, V-0053 to V-0062).
/// Strings are octets, one character per octet (Table V-1 note 1).
/// </summary>
/// <param name="MessageId">The MessageID, unique to the message (V-0053, V-0054); not empty, no line feed.</param>
/// <param name="RecipientList">The RecipientList as carried; <see cref="CftpRecipients"/> reads it.</param>
/// <param name="Message">The Message field: the SMTP DATA octets (interp V-0083); <see cref="SmtpData"/> converts.</param>
public sealed record CftpMailFile(string MessageId, string RecipientList, ReadOnlyMemory<byte> Message)
{
    /// <summary>The field separator, line feed (V-0047).</summary>
    public const byte Lf = 0x0A;

    /// <summary>One octet per character, so any octet round-trips.</summary>
    internal static readonly Encoding Octets = Encoding.Latin1;

    /// <summary>Encodes. MessageSize is written in decimal with no leading zeros (V-0061).</summary>
    public byte[] Encode()
    {
        if (MessageId.Length == 0 || MessageId.Contains('\n', StringComparison.Ordinal) || RecipientList.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("MessageID must not be empty, and neither field may hold a line feed (V-0047, interp V-0085)");
        }

        byte[] id = Octets.GetBytes(MessageId);
        byte[] rl = Octets.GetBytes(RecipientList);
        byte[] size = Encoding.ASCII.GetBytes(Message.Length.ToString(CultureInfo.InvariantCulture));
        var b = new byte[id.Length + rl.Length + size.Length + 3 + Message.Length];
        var w = new Span<byte>(b);
        id.CopyTo(w);
        w = w[id.Length..];
        w[0] = Lf;
        rl.CopyTo(w[1..]);
        w = w[(1 + rl.Length)..];
        w[0] = Lf;
        size.CopyTo(w[1..]);
        w = w[(1 + size.Length)..];
        w[0] = Lf;
        Message.Span.CopyTo(w[1..]);
        return b;
    }

    /// <summary>
    /// Decodes (corpus-format.md 3.10): no line feed after the MessageID truncated, an empty
    /// MessageID invalid; no line feed after the RecipientList or MessageSize truncated; a
    /// MessageSize that is empty, over 15 characters or not all digits invalid (leading zeros
    /// accepted); fewer octets than it says truncated, more trailing (interp V-0085).
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> b, out CftpMailFile? file, out CftpErrorKind error)
    {
        file = null;
        error = CftpErrorKind.Truncated;
        int i = b.IndexOf(Lf);
        if (i < 0)
        {
            return false;
        }

        if (i == 0)
        {
            error = CftpErrorKind.InvalidField;
            return false;
        }

        int j = b[(i + 1)..].IndexOf(Lf);
        if (j < 0)
        {
            return false;
        }

        j += i + 1;
        int k = b[(j + 1)..].IndexOf(Lf);
        if (k < 0)
        {
            return false;
        }

        k += j + 1;
        ReadOnlySpan<byte> size = b[(j + 1)..k];
        if (size.Length == 0 || size.Length > 15 || size.IndexOfAnyExceptInRange((byte)'0', (byte)'9') >= 0)
        {
            error = CftpErrorKind.InvalidField;
            return false;
        }

        long n = long.Parse(Encoding.ASCII.GetString(size), NumberStyles.None, CultureInfo.InvariantCulture);
        ReadOnlySpan<byte> rest = b[(k + 1)..];
        if (rest.Length < n)
        {
            return false;
        }

        if (rest.Length > n)
        {
            error = CftpErrorKind.TrailingBytes;
            return false;
        }

        file = new CftpMailFile(Octets.GetString(b[..i]), Octets.GetString(b[(i + 1)..j]), rest.ToArray());
        return true;
    }
}
