namespace M0LTE.S5066.Core;

/// <summary>
/// The 32-bit CRC over the segmented C_PDU bytes of I and I+C D_PDUs (C-0157). Polynomial
/// x^32+x^27+x^25+x^23+x^21+x^18+x^17+x^16+x^13+x^10+x^8+x^7+x^6+x^3+x^2+x+1 (C-0158), register
/// starting at zero (C-0160, C-0161), no final XOR, each byte processed least significant bit
/// first through a right-shifting register XORed with 0xF3A4E550, the bit-reversed polynomial
/// with x^32 implied (C-0162) [Ed4 C.4.2.11, p.C-21].
/// </summary>
/// <remarks>
/// The final register value is sent least significant byte first (C-0163); <see cref="Write"/>
/// does that. The test vector (C-0164) is data F0 00 00 47 05 64 02, register 0xF4178F95, on the
/// wire 95 8F 17 F4.
/// </remarks>
public static class Crc32S5066
{
    /// <summary>The register value before the first byte (C-0160, C-0161).</summary>
    public const uint Initial = 0;

    private const uint ReflectedPolynomial = 0xF3A4E550;

    private static readonly uint[] Table = BuildTable();

    /// <summary>Feeds one byte into the register and returns the new register (C-0162).</summary>
    public static uint Update(uint crc, byte data)
    {
        return (crc >> 8) ^ Table[(crc ^ data) & 0xFF];
    }

    /// <summary>Returns the register after all of <paramref name="data"/>, starting from zero (C-0160).</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = Initial;
        foreach (byte b in data)
        {
            crc = Update(crc, b);
        }

        return crc;
    }

    /// <summary>Writes the four CRC bytes as transmitted, least significant byte first (C-0163).</summary>
    public static void Write(uint crc, Span<byte> destination)
    {
        destination[0] = (byte)crc;
        destination[1] = (byte)(crc >> 8);
        destination[2] = (byte)(crc >> 16);
        destination[3] = (byte)(crc >> 24);
    }

    /// <summary>Reads the four CRC bytes as received, reassembling the register.</summary>
    public static uint Read(ReadOnlySpan<byte> source) =>
        source[0] | ((uint)source[1] << 8) | ((uint)source[2] << 16) | ((uint)source[3] << 24);

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ ReflectedPolynomial : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }
}
