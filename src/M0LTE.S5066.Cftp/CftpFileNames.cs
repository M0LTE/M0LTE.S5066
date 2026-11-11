using System.Text;

namespace M0LTE.S5066.Cftp;

/// <summary>Local file names for received files (V-0041, interp V-0084).</summary>
public static class CftpFileNames
{
    /// <summary>
    /// A name safe to use in a local directory: every octet other than a letter, digit, '-',
    /// '_' or '.' becomes '_'; a name that would be empty or start with '.' gets the prefix
    /// "cftp_" (interp V-0084).
    /// </summary>
    public static string Safe(ReadOnlySpan<byte> name)
    {
        var sb = new StringBuilder(name.Length + 5);
        foreach (byte b in name)
        {
            char c = (char)b;
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }

        return sb.Length == 0 || sb[0] == '.' ? "cftp_" + sb : sb.ToString();
    }

    /// <summary>The same for a string of octets, one per character.</summary>
    public static string Safe(string name) => Safe(CftpMailFile.Octets.GetBytes(name));
}
