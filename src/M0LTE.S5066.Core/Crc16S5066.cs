namespace M0LTE.S5066.Core;

/// <summary>
/// The 16-bit D_PDU header CRC of Annex C. Polynomial x^16+x^15+x^12+x^11+x^8+x^6+x^3+1
/// (matrix row C-0138), register starting at zero (C-0139), no final XOR, each byte processed
/// least significant bit first through a right-shifting register XORed with 0x9299, the
/// bit-reversed polynomial with x^16 implied (C-0143) [Ed4 C.4.2.8, p.C-18].
/// </summary>
/// <remarks>
/// The CRC covers every header bit except the sync (C-0140). The final register value is sent
/// least significant byte first (C-0145, C-0142); <see cref="Write"/> does that. The worked
/// example (C-0147) is header bytes F0 00 00 47 05 64 02, register 0x1E5F, on the air 5F 1E.
/// </remarks>
public static class Crc16S5066
{
    /// <summary>The register value before the first byte (C-0139).</summary>
    public const ushort Initial = 0x0000;

    private const ushort ReflectedPolynomial = 0x9299;

    private static readonly ushort[] Table = BuildTable();

    /// <summary>Feeds one byte into the register and returns the new register (C-0143).</summary>
    public static ushort Update(ushort crc, byte data)
    {
        return (ushort)((crc >> 8) ^ Table[(crc ^ data) & 0xFF]);
    }

    /// <summary>Returns the register after all of <paramref name="data"/>, starting from zero (C-0139).</summary>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = Initial;
        foreach (byte b in data)
        {
            crc = Update(crc, b);
        }

        return crc;
    }

    /// <summary>Writes the two CRC bytes as transmitted, least significant byte first (C-0145).</summary>
    public static void Write(ushort crc, Span<byte> destination)
    {
        destination[0] = (byte)crc;
        destination[1] = (byte)(crc >> 8);
    }

    /// <summary>Reads the two CRC bytes as received, reassembling the register (C-0146).</summary>
    public static ushort Read(ReadOnlySpan<byte> source) => (ushort)(source[0] | (source[1] << 8));

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort crc = (ushort)i;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ ReflectedPolynomial) : (ushort)(crc >> 1);
            }

            table[i] = crc;
        }

        return table;
    }
}
