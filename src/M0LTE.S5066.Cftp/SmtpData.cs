namespace M0LTE.S5066.Cftp;

/// <summary>
/// The CFTP Message field as SMTP DATA octets (interp V-0083): dot-stuffed (RFC 5321
/// 4.5.2) and ending CR LF . CR LF (Table V-1 note 2, V-0051, V-0070).
/// </summary>
public static class SmtpData
{
    /// <summary>The terminator CR LF . CR LF.</summary>
    public static ReadOnlySpan<byte> Terminator => [0x0D, 0x0A, 0x2E, 0x0D, 0x0A];

    /// <summary>
    /// A message (RFC 5322 octets) as a Message field: a dot added in front of every line that
    /// starts with one, a CR LF added if the message does not end with one, then . CR LF.
    /// </summary>
    public static byte[] Stuff(ReadOnlySpan<byte> message)
    {
        using var ms = new MemoryStream(message.Length + 8);
        bool lineStart = true;
        for (int i = 0; i < message.Length; i++)
        {
            byte c = message[i];
            if (lineStart && c == (byte)'.')
            {
                ms.WriteByte((byte)'.');
            }

            ms.WriteByte(c);
            lineStart = c == 0x0A && i > 0 && message[i - 1] == 0x0D;
        }

        if (!message.EndsWith("\r\n"u8))
        {
            ms.Write("\r\n"u8);
        }

        ms.Write(".\r\n"u8);
        return ms.ToArray();
    }

    /// <summary>
    /// A Message field back to the message: when it ends with the terminator, the final . CR LF
    /// is removed and every line's leading dot of a pair dropped; otherwise it is taken as it is
    /// (interp V-0083).
    /// </summary>
    public static byte[] Unstuff(ReadOnlySpan<byte> field)
    {
        if (!(field.EndsWith(Terminator) || field.SequenceEqual(".\r\n"u8)))
        {
            return field.ToArray();
        }

        ReadOnlySpan<byte> body = field[..^3];
        using var ms = new MemoryStream(body.Length);
        bool lineStart = true;
        for (int i = 0; i < body.Length; i++)
        {
            byte c = body[i];
            if (!(lineStart && c == (byte)'.'))
            {
                ms.WriteByte(c);
            }

            lineStart = c == 0x0A && i > 0 && body[i - 1] == 0x0D;
        }

        return ms.ToArray();
    }
}
