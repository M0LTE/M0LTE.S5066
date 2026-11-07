namespace M0LTE.S5066.Core.Dts;

/// <summary>
/// The address field of a D_PDU: a destination and a source of equal width, packed into m bytes
/// (0.5 to 3.5 bytes each, m = 1 to 7) [Ed4 C.4.2.4 to C.4.2.6, pp.C-13 to C-16].
/// </summary>
/// <remarks>
/// The m-byte field holds the 8m-bit number (dst x 2^(4m)) + src, most significant byte first
/// (C-0115, C-0116, C-0117, C-0127). Addresses are 28-bit values; the group flag of types 7 and 8
/// is a separate field (C-0122), never a 29th bit here.
/// </remarks>
public static class DpduAddress
{
    /// <summary>Largest 28-bit address value (15.255.255.255, C-0121).</summary>
    public const uint MaxValue = 0x0FFFFFFF;

    /// <summary>
    /// The smallest m that holds both addresses: whole nibbles needed, at least 1 (C-0133). This is
    /// our sender rule; the standard only says leading zero bytes may be omitted (C-0130).
    /// </summary>
    public static int MinimumSize(uint dst, uint src)
    {
        uint both = dst | src;
        int nibbles = 1;
        while (nibbles < 7 && (both >> (4 * nibbles)) != 0)
        {
            nibbles++;
        }

        return nibbles;
    }

    /// <summary>Writes the m-byte address field (C-0114 to C-0117, C-0127) and returns m.</summary>
    /// <exception cref="ArgumentOutOfRangeException">m is not 1 to 7, or an address does not fit 4m bits.</exception>
    public static void Write(int addressSize, uint dst, uint src, Span<byte> destination)
    {
        if (addressSize is < 1 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(addressSize), "address size must be 1 to 7");
        }

        ulong limit = 1UL << (4 * addressSize);
        if (dst >= limit || src >= limit)
        {
            throw new ArgumentOutOfRangeException(nameof(dst), "address does not fit the address size");
        }

        ulong packed = ((ulong)dst << (4 * addressSize)) | src;
        for (int i = addressSize - 1; i >= 0; i--)
        {
            destination[i] = (byte)packed;
            packed >>= 8;
        }
    }

    /// <summary>
    /// Reads an address field of any length. Returns false, setting both addresses to 0, when the length is
    /// not 1 to 7 (C-0108, C-0110); a decoder reports that as <see cref="PduErrorKind.BadAddressSize"/>.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> field, out uint dst, out uint src)
    {
        if (field.Length is < 1 or > 7)
        {
            dst = 0;
            src = 0;
            return false;
        }

        Read(field, out dst, out src);
        return true;
    }

    /// <summary>Reads an m-byte address field, m being the length of <paramref name="field"/> (C-0127, C-0133).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The field is not 1 to 7 bytes.</exception>
    public static void Read(ReadOnlySpan<byte> field, out uint dst, out uint src)
    {
        int m = field.Length;
        if (m is < 1 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(field), "address size must be 1 to 7");
        }

        ulong packed = 0;
        foreach (byte b in field)
        {
            packed = (packed << 8) | b;
        }

        dst = (uint)(packed >> (4 * m));
        src = (uint)(packed & ((1UL << (4 * m)) - 1));
    }

    /// <summary>Formats a 28-bit address as w.x.y.z, w being 0 to 15 (C-0119, C-0126).</summary>
    public static string ToDotted(uint address) =>
        $"{(address >> 24) & 15}.{(address >> 16) & 255}.{(address >> 8) & 255}.{address & 255}";

    /// <summary>Parses w.x.y.z with w 0 to 15 and x, y, z 0 to 255.</summary>
    public static bool TryParse(string text, out uint address)
    {
        address = 0;
        string[] parts = text.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        uint value = 0;
        for (int i = 0; i < 4; i++)
        {
            if (!uint.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint n)
                || n > (i == 0 ? 15u : 255u))
            {
                return false;
            }

            value = (value << 8) | n;
        }

        address = value;
        return true;
    }
}
