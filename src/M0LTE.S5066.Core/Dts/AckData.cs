namespace M0LTE.S5066.Core.Dts;

/// <summary>
/// The two encodings of the acknowledgement bits carried by ACK-ONLY (type 10) Ack Data and DATA-ACK
/// (type 11) Selective Acks [Ed4 C.4.5.5, pp.C-31 to C-32; C.4.6.4, pp.C-35 to C-36]. The bits are
/// the receive window status from a starting FSN upwards, one per FSN, true for a D_PDU received
/// correctly (C-0202, C-0205, C-0227).
/// </summary>
/// <remarks>
/// Encoding 0 is the bitmap of C.4.5.4: bit 0 of byte 0 for the first FSN, each byte filled from bit 0
/// to bit 7, zero padded to a whole byte (C-0207, C-0208, C-0210, C-0227). Encoding 1 is run length
/// encoding of the same bits (C-0228): a one-byte code (bit 7 the value, bit 6 Extended = 0, bits 5 to
/// 0 the length, C-0229) or a two-byte code (bit 7 the value, bit 6 Extended = 1, a 14-bit length from
/// bit 5 of byte 0, the most significant, to bit 0 of byte 1, C-0230), each covering length + 1 bits
/// (C-0231), one after another through the whole field (C-0232).
/// </remarks>
public static class AckData
{
    /// <summary>The bitmap encoding (C-0227).</summary>
    public const int BitmapEncoding = 0;

    /// <summary>The run length encoding (C-0228).</summary>
    public const int RleEncoding = 1;

    /// <summary>The most bits one one-byte RLE code covers: a 6-bit length of 63, plus one (C-0229, C-0231).</summary>
    public const int MaxShortRun = 64;

    /// <summary>The most bits one two-byte RLE code covers: a 14-bit length of 16383, plus one (C-0230, C-0231).</summary>
    public const int MaxLongRun = 16384;

    /// <summary>The bitmap of <paramref name="count"/> bits from <paramref name="start"/> (C-0207 to C-0211, C-0227).</summary>
    public static byte[] Bitmap(IReadOnlyList<bool> bits, int start, int count)
    {
        var map = new byte[(count + 7) / 8];
        for (int i = 0; i < count; i++)
        {
            if (bits[start + i])
            {
                map[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        return map;
    }

    /// <summary>
    /// The run length encoding of <paramref name="count"/> bits from <paramref name="start"/> (C-0228 to
    /// C-0232), using the fewest bytes: each run of equal bits becomes one-byte codes when it is at most
    /// 64 bits and two-byte codes otherwise, a run longer than 16384 bits taking as many full codes as it
    /// needs and one code for the rest (interp C-1150).
    /// </summary>
    public static byte[] Rle(IReadOnlyList<bool> bits, int start, int count)
    {
        var codes = new List<byte>();
        int i = 0;
        while (i < count)
        {
            bool value = bits[start + i];
            int run = 1;
            while (i + run < count && bits[start + i + run] == value)
            {
                run++;
            }

            i += run;
            while (run > 0)
            {
                int take = Math.Min(run, MaxLongRun);
                int length = take - 1;
                byte v = value ? (byte)0x80 : (byte)0x00;
                if (take <= MaxShortRun)
                {
                    codes.Add((byte)(v | length));
                }
                else
                {
                    codes.Add((byte)(v | 0x40 | (length >> 8)));
                    codes.Add((byte)(length & 0xFF));
                }

                run -= take;
            }
        }

        return codes.ToArray();
    }

    /// <summary>
    /// The bits an Ack Data field carries, in FSN order from its first FSN (C-0226: a receiver supports
    /// both encodings). A bitmap gives eight bits per byte, padding included (C-0227). A run length
    /// field gives the bits of each code in turn (C-0231, C-0232); a two-byte code cut short by the end
    /// of the field adds nothing (interp C-1153).
    /// </summary>
    public static List<bool> Decode(int encoding, ReadOnlySpan<byte> data)
    {
        var bits = new List<bool>();
        if (encoding == BitmapEncoding)
        {
            for (int i = 0; i < data.Length * 8; i++)
            {
                bits.Add((data[i / 8] & (1 << (i % 8))) != 0);
            }

            return bits;
        }

        int p = 0;
        while (p < data.Length)
        {
            byte b = data[p];
            bool value = (b & 0x80) != 0;
            int length;
            if ((b & 0x40) == 0)
            {
                length = b & 0x3F;
                p++;
            }
            else
            {
                if (p + 1 >= data.Length)
                {
                    break;
                }

                length = ((b & 0x3F) << 8) | data[p + 1];
                p += 2;
            }

            for (int k = 0; k <= length; k++)
            {
                bits.Add(value);
            }
        }

        return bits;
    }
}
