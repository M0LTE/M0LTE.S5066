using System.Globalization;

namespace M0LTE.S5066.Core.Sis;

/// <summary>
/// A node address as carried in the S_primitives: 4 bytes holding a 28-bit address, a group flag
/// and a SIZE field in half-bytes [Ed4 S.5.16.1, Fig S-19, p.S-19] (matrix S-0058, S-0133, S-0134).
/// </summary>
/// <remarks>
/// The address is right-aligned: its lowest bit is bit 0 of the last byte (S-0059). Fig S-19 says
/// only the first SIZE half-bytes are valid, so the codec sends SIZE 7 by default and always reads
/// all 28 bits, whatever SIZE says, so that both readings agree (S-0137). <see cref="Size"/> is
/// kept as sent so a value round-trips.
/// </remarks>
/// <param name="Value">The 28-bit address, 0 to 0x0FFFFFFF.</param>
/// <param name="Group">True for a group address, valid only as the destination of non-ARQ (S-0135, S-0136).</param>
/// <param name="Size">The SIZE field, 0 to 7 half-bytes.</param>
public readonly record struct SisAddress(uint Value, bool Group = false, byte Size = 7)
{
    /// <summary>The largest 28-bit address value.</summary>
    public const uint MaxValue = 0x0FFFFFFF;

    /// <summary>Builds an individual address from dotted-decimal parts, w 0 to 15 and x, y, z 0 to 255.</summary>
    public static SisAddress FromParts(int w, int x, int y, int z, bool group = false, byte size = 7)
    {
        if (w is < 0 or > 15 || x is < 0 or > 255 || y is < 0 or > 255 || z is < 0 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(w), "address parts out of range");
        }

        return new SisAddress((uint)((w << 24) | (x << 16) | (y << 8) | z), group, size);
    }

    /// <summary>Parses "w.x.y.z" (the corpus notation).</summary>
    public static bool TryParse(string text, bool group, byte size, out SisAddress address)
    {
        address = default;
        string[] parts = text.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        var n = new int[4];
        for (int i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i]))
            {
                return false;
            }
        }

        if (n[0] > 15 || n[1] > 255 || n[2] > 255 || n[3] > 255)
        {
            return false;
        }

        address = FromParts(n[0], n[1], n[2], n[3], group, size);
        return true;
    }

    /// <summary>Dotted-decimal text, "w.x.y.z".</summary>
    public string Dotted => string.Create(CultureInfo.InvariantCulture,
        $"{(Value >> 24) & 15}.{(Value >> 16) & 255}.{(Value >> 8) & 255}.{Value & 255}");

    /// <summary>Writes the 4 wire bytes.</summary>
    public void Write(Span<byte> destination)
    {
        if (Value > MaxValue || Size > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(Value), "address value or size out of range");
        }

        destination[0] = (byte)((Size << 5) | (Group ? 0x10 : 0) | (int)((Value >> 24) & 0x0F));
        destination[1] = (byte)(Value >> 16);
        destination[2] = (byte)(Value >> 8);
        destination[3] = (byte)Value;
    }

    /// <summary>Reads the 4 wire bytes.</summary>
    public static SisAddress Read(ReadOnlySpan<byte> source) =>
        new(
            ((uint)(source[0] & 0x0F) << 24) | ((uint)source[1] << 16) | ((uint)source[2] << 8) | source[3],
            (source[0] & 0x10) != 0,
            (byte)(source[0] >> 5));

    /// <inheritdoc/>
    public override string ToString() => Dotted + (Group ? " (group)" : "");
}
