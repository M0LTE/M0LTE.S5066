using System.Security.Cryptography;

namespace M0LTE.S5066.Cftp;

/// <summary>
/// A mail as CFTP carries it: the MessageID, the envelope (return path and recipients) and the
/// message itself (RFC 5322 octets, not dot-stuffed). <see cref="ToCompressedFile"/> builds the
/// file BFTPv1 carries (V.7 steps 3 and 4); <see cref="TryFromCompressedFile"/> reads one (step 9).
/// </summary>
/// <param name="MessageId">The MessageID: 1 to 255 letters and digits, unique to the message (V-0053 to V-0056, interp V-0085).</param>
/// <param name="Envelope">Return path and recipients (V-0057 to V-0060).</param>
/// <param name="Message">The message octets, headers and body, as an SMTP client would send them before stuffing.</param>
public sealed record CftpMail(string MessageId, CftpRecipients Envelope, ReadOnlyMemory<byte> Message)
{
    /// <summary>A MessageID is under this many characters (V-0056).</summary>
    public const int MaxMessageIdLength = 256;

    /// <summary>Characters of a generated MessageID (V-0056).</summary>
    private const string IdChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>A new MessageID: 20 random letters and digits (interp V-0085).</summary>
    public static string NewMessageId() => RandomNumberGenerator.GetString(IdChars, 20);

    /// <summary>
    /// The mail file (MessageID, RecipientList, MessageSize, Message as SMTP DATA octets,
    /// interp V-0083), uncompressed. Refuses a MessageID that is not 1 to 255 letters and digits.
    /// </summary>
    public CftpMailFile ToMailFile()
    {
        if (MessageId.Length is 0 or >= MaxMessageIdLength || !MessageId.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("a MessageID we send is 1 to 255 letters and digits (V-0056, interp V-0085)");
        }

        return new CftpMailFile(MessageId, Envelope.Encode(), SmtpData.Stuff(Message.Span));
    }

    /// <summary>The compressed file to send in a BFTPv1 PDU (V-0063, V-0064).</summary>
    public byte[] ToCompressedFile() => CftpCompression.Compress(ToMailFile().Encode());

    /// <summary>
    /// Reads a received file: decompress (interp V-0082), decode the mail file and the
    /// RecipientList, and remove the SMTP stuffing (interp V-0083).
    /// </summary>
    public static bool TryFromCompressedFile(ReadOnlyMemory<byte> file, out CftpMail? mail, out CftpErrorKind error, out string? what)
    {
        mail = null;
        what = null;
        if (!CftpCompression.TryDecompress(file, out byte[]? plain, out error))
        {
            what = "the file does not decompress";
            return false;
        }

        if (!CftpMailFile.TryDecode(plain, out CftpMailFile? mf, out error))
        {
            what = "the file is not a CFTP mail file";
            return false;
        }

        if (!CftpRecipients.TryDecode(mf!.RecipientList, out CftpRecipients? env, out error))
        {
            what = "the RecipientList has no recipient or an empty address";
            return false;
        }

        mail = new CftpMail(mf.MessageId, env!, SmtpData.Unstuff(mf.Message.Span));
        return true;
    }
}
