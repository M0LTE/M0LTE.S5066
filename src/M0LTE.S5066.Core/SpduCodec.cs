namespace M0LTE.S5066.Core;

/// <summary>
/// Byte-exact encode and decode of SIS peer S_PDUs (Annex A, A.3.1). No I/O, no clock. The
/// corpus kind is <c>spdu</c> (conformance docs/corpus-format.md section 3.6).
/// </summary>
public static class SpduCodec
{
    /// <summary>
    /// Decodes exactly one S_PDU; everything after the fixed S_PCI is the U_PDU copy. Checks,
    /// stopping at the first failure: no bytes is <see cref="PduErrorKind.Truncated"/>; type 3 to
    /// 15 is <see cref="PduErrorKind.InvalidField"/> (A-0141); fewer octets than the S_PCI (2, 3 for
    /// DATA, 5 for DATA with a valid TTD) is Truncated. Unused bits are ignored (A-0157).
    /// </summary>
    /// <param name="bytes">The whole S_PDU.</param>
    public static CodecResult<SPdu> Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 1)
        {
            return CodecResult<SPdu>.Fail(PduErrorKind.Truncated);
        }

        int type = bytes[0] >> 4;
        if (type > 2)
        {
            return CodecResult<SPdu>.Fail(PduErrorKind.InvalidField);
        }

        if (bytes.Length < (type == 0 ? 3 : 2))
        {
            return CodecResult<SPdu>.Fail(PduErrorKind.Truncated);
        }

        byte low = (byte)(bytes[0] & 0x0F);
        byte src = (byte)(bytes[1] >> 4);
        byte dst = (byte)(bytes[1] & 0x0F);
        switch (type)
        {
            case 1:
                return CodecResult<SPdu>.Ok(new SpduDeliveryConfirm(low, src, dst, bytes[2..].ToArray()));
            case 2:
                return CodecResult<SPdu>.Ok(new SpduDeliveryFail(low, src, dst, bytes[2..].ToArray()));
        }

        bool confirm = (bytes[2] & 0x80) != 0;
        if ((bytes[2] & 0x40) == 0)
        {
            return CodecResult<SPdu>.Ok(new SpduData(low, src, dst, confirm, null, bytes[3..].ToArray()));
        }

        if (bytes.Length < 5)
        {
            return CodecResult<SPdu>.Fail(PduErrorKind.Truncated);
        }

        var ttd = new SpduTtd((byte)(bytes[2] & 0x0F), (ushort)((bytes[3] << 8) | bytes[4]));
        return CodecResult<SPdu>.Ok(new SpduData(low, src, dst, confirm, ttd, bytes[5..].ToArray()));
    }

    /// <summary>
    /// Encodes an S_PDU. Unused bits are written as zero (A-0157). Throws
    /// <see cref="ArgumentException"/> when a priority, SAP ID, reason or TTD day does not fit its field.
    /// </summary>
    /// <param name="pdu">The S_PDU to encode.</param>
    public static byte[] Encode(SPdu pdu)
    {
        ArgumentNullException.ThrowIfNull(pdu);
        switch (pdu)
        {
            case SpduData d:
            {
                int pci = d.Ttd is null ? 3 : 5;
                byte[] bytes = new byte[pci + d.UPdu.Length];
                bytes[0] = Nibble(d.Priority, "Priority");
                bytes[1] = Saps(d.SrcSap, d.DstSap);
                bytes[2] = (byte)(d.ClientConfirm ? 0x80 : 0);
                if (d.Ttd is { } ttd)
                {
                    bytes[2] |= (byte)(0x40 | Nibble(ttd.DayMod16, "TTD day"));
                    bytes[3] = (byte)(ttd.HalfSeconds >> 8);
                    bytes[4] = (byte)ttd.HalfSeconds;
                }

                d.UPdu.Span.CopyTo(bytes.AsSpan(pci));
                return bytes;
            }

            case SpduDeliveryConfirm c:
                return WithCopy((byte)(0x10 | Nibble(c.Priority, "Priority")), Saps(c.SrcSap, c.DstSap), c.UPdu);
            case SpduDeliveryFail f:
                return WithCopy((byte)(0x20 | Nibble(f.Reason, "Reason")), Saps(f.SrcSap, f.DstSap), f.UPdu);
            default:
                throw new ArgumentException($"unknown S_PDU type {pdu.Type}", nameof(pdu));
        }
    }

    private static byte[] WithCopy(byte first, byte saps, ReadOnlyMemory<byte> copy)
    {
        byte[] bytes = new byte[2 + copy.Length];
        bytes[0] = first;
        bytes[1] = saps;
        copy.Span.CopyTo(bytes.AsSpan(2));
        return bytes;
    }

    private static byte Saps(byte src, byte dst) => (byte)((Nibble(src, "Source SAP ID") << 4) | Nibble(dst, "Destination SAP ID"));

    private static byte Nibble(byte value, string name) =>
        value <= 15 ? value : throw new ArgumentException($"{name} {value} does not fit in 4 bits");
}
