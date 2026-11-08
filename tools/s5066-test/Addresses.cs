using System.Globalization;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.TestClient;

/// <summary>Addresses as an operator writes them.</summary>
internal static class Addresses
{
    /// <summary>The broadcast address, group 31.255.255.255 (C-0123).</summary>
    public static readonly SisAddress Broadcast = new(0x0FFFFFFF, Group: true);

    /// <summary>
    /// Parses "w.x.y.z": w 0 to 15 is an individual node, 16 to 31 a group with the group flag as a
    /// 29th bit, so 31.255.255.255 is the broadcast address (C-0122, C-0123). "all" is broadcast.
    /// </summary>
    public static bool TryParse(string text, out SisAddress address)
    {
        address = default;
        if (text == "all")
        {
            address = Broadcast;
            return true;
        }

        int dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot < 1 || !int.TryParse(text[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out int w) || w > 31)
        {
            return false;
        }

        return w < 16
            ? SisAddress.TryParse(text, false, 7, out address)
            : SisAddress.TryParse((w - 16).ToString(CultureInfo.InvariantCulture) + text[dot..], true, 7, out address);
    }

    /// <summary>Parses an address or throws <see cref="FormatException"/>.</summary>
    public static SisAddress Parse(string text) =>
        TryParse(text, out SisAddress a) ? a : throw new FormatException($"not an address: {text} (w.x.y.z, w 16 to 31 for a group, or all)");

    /// <summary>An address as an operator reads it (the inverse of <see cref="TryParse"/>).</summary>
    public static string Show(SisAddress a) => a.Group
        ? string.Create(CultureInfo.InvariantCulture, $"{16 + ((a.Value >> 24) & 15)}.{(a.Value >> 16) & 255}.{(a.Value >> 8) & 255}.{a.Value & 255}")
        : a.Dotted;

    /// <summary>Splits "host:port".</summary>
    public static (string Host, int Port) ParseEndpoint(string text)
    {
        int colon = text.LastIndexOf(':');
        if (colon < 1 || !int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
        {
            throw new FormatException($"not host:port: {text}");
        }

        return (text[..colon], port);
    }
}
