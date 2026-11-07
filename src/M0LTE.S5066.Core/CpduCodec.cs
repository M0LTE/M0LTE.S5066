namespace M0LTE.S5066.Core;

/// <summary>
/// Byte-exact encode and decode of CAS-1 C_PDUs (Annex B, B.7.1). No I/O, no clock. Every layout
/// follows the matrix rows cited on the <see cref="CPdu"/> records; the corpus kind is
/// <c>cpdu</c> (conformance docs/corpus-format.md section 3.5).
/// </summary>
public static class CpduCodec
{
    /// <summary>
    /// Decodes exactly one C_PDU. Checks, stopping at the first failure: no bytes is
    /// <see cref="PduErrorKind.Truncated"/>; type 11 to 15 is <see cref="PduErrorKind.InvalidField"/> (B-0305);
    /// a type 6 shorter than 2 octets is Truncated; octets beyond the fixed length of any type
    /// except 0 are <see cref="PduErrorKind.TrailingBytes"/>. NOT USED bits are ignored (B-0306).
    /// </summary>
    /// <param name="bytes">The whole C_PDU.</param>
    public static CodecResult<CPdu> Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 1)
        {
            return CodecResult<CPdu>.Fail(PduErrorKind.Truncated);
        }

        int type = bytes[0] >> 4;
        if (type == 0)
        {
            return CodecResult<CPdu>.Ok(new CpduData(bytes[1..].ToArray()));
        }

        if (type > 10)
        {
            return CodecResult<CPdu>.Fail(PduErrorKind.InvalidField);
        }

        if (type == 6 && bytes.Length < 2)
        {
            return CodecResult<CPdu>.Fail(PduErrorKind.Truncated);
        }

        if (bytes.Length > (type == 6 ? 2 : 1))
        {
            return CodecResult<CPdu>.Fail(PduErrorKind.TrailingBytes);
        }

        byte low = (byte)(bytes[0] & 0x0F);
        CPdu value = type switch
        {
            1 => new PhysicalLinkRequest((low & 4) != 0, (low & 2) != 0, (low & 1) != 0),
            2 => new PhysicalLinkAccepted((low & 4) != 0, (low & 2) != 0, (low & 1) != 0),
            3 => new PhysicalLinkRejected(low),
            4 => new PhysicalLinkBreak(low),
            5 => new PhysicalLinkBreakConfirm(),
            6 => new PhysicalLinkBreakRequest((byte)(low & 7), bytes[1]),
            7 => new PhysicalLinkResume(),
            8 => new PhysicalLinkResumeAccept(),
            9 => new PhysicalLinkResumeReject(),
            _ => new AleRelink((byte)(low & 7)),
        };
        return CodecResult<CPdu>.Ok(value);
    }

    /// <summary>
    /// Encodes a C_PDU. NOT USED bits are written as zero (B-0110). Throws
    /// <see cref="ArgumentException"/> for what this node never sends: types 7 to 10 (ALE only),
    /// Short without Ed4 (B-0125), LINK set (B-0127), or a reason that does not fit its field.
    /// </summary>
    /// <param name="pdu">The C_PDU to encode.</param>
    public static byte[] Encode(CPdu pdu)
    {
        ArgumentNullException.ThrowIfNull(pdu);
        switch (pdu)
        {
            case CpduData d:
            {
                byte[] bytes = new byte[1 + d.SPdu.Length];
                d.SPdu.Span.CopyTo(bytes.AsSpan(1));
                return bytes;
            }

            case PhysicalLinkRequest r:
                return [(byte)(0x10 | LinkBits(r.Short, r.Ed4, r.Link))];
            case PhysicalLinkAccepted a:
                return [(byte)(0x20 | LinkBits(a.Short, a.Ed4, a.Link))];
            case PhysicalLinkRejected j:
                return [(byte)(0x30 | Fit(j.Reason, 15, "Reason"))];
            case PhysicalLinkBreak b:
                return [(byte)(0x40 | Fit(b.Reason, 15, "Reason"))];
            case PhysicalLinkBreakConfirm:
                return [0x50];
            case PhysicalLinkBreakRequest q:
                return [(byte)(0x60 | Fit(q.Reason, 7, "Reason")), q.Priority];
            default:
                throw new ArgumentException($"C_PDU type {pdu.Type} is ALE only and is never encoded", nameof(pdu));
        }
    }

    private static byte LinkBits(bool shortFsn, bool ed4, bool link)
    {
        if (shortFsn && !ed4)
        {
            throw new ArgumentException("Short may be set only with Ed4 (B-0125)");
        }

        if (link)
        {
            throw new ArgumentException("LINK is sent as 0 without HARD LINKS (B-0127)");
        }

        return (byte)((shortFsn ? 4 : 0) | (ed4 ? 2 : 0));
    }

    private static byte Fit(byte value, byte max, string name) =>
        value <= max ? value : throw new ArgumentException($"{name} {value} does not fit in its field");
}
