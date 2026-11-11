namespace M0LTE.S5066.Cftp;

/// <summary>
/// The RecipientList read as addresses: the Return-Path first, then the recipients, separated
/// by commas (V-0057 to V-0060, interp V-0086).
/// </summary>
/// <param name="ReturnPath">The Return-Path, or empty when there is none (V-0059).</param>
/// <param name="Recipients">The recipients, at least one.</param>
public sealed record CftpRecipients(string ReturnPath, IReadOnlyList<string> Recipients)
{
    /// <summary>A RecipientList is under this many characters (V-0060).</summary>
    public const int MaxLength = 10240;

    /// <summary>
    /// Encodes: the return path (nothing when there is none), then a comma before each
    /// recipient (V-0057 to V-0059, interp V-0086). Refuses a list of 10240 characters or more
    /// (V-0060).
    /// </summary>
    public string Encode()
    {
        if (Recipients.Count == 0)
        {
            throw new ArgumentException("a CFTP mail needs at least one recipient (interp V-0086)");
        }

        if (ReturnPath.Contains(',', StringComparison.Ordinal) || Recipients.Any(r => r.Length == 0 || r.Contains(',', StringComparison.Ordinal) || r.Contains('\n', StringComparison.Ordinal)) || ReturnPath.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("an address may not be empty or hold a comma or line feed");
        }

        string s = string.Join(',', Recipients.Prepend(ReturnPath));
        return s.Length < MaxLength ? s : throw new ArgumentException($"the RecipientList must be under {MaxLength} characters (V-0060)");
    }

    /// <summary>
    /// Decodes: split at every comma, spaces around each part removed; the first is the
    /// return path (so a lone space is none). No comma, or an empty part after the first, is
    /// <see cref="CftpErrorKind.InvalidField"/> (interp V-0086).
    /// </summary>
    public static bool TryDecode(string list, out CftpRecipients? recipients, out CftpErrorKind error)
    {
        recipients = null;
        error = CftpErrorKind.InvalidField;
        string[] parts = list.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = parts[i].Trim(' ');
        }

        if (parts.Length < 2 || parts.Skip(1).Any(p => p.Length == 0))
        {
            return false;
        }

        recipients = new CftpRecipients(parts[0], parts[1..]);
        return true;
    }
}
