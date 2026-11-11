using System.Buffers.Binary;

namespace M0LTE.S5066.Cftp;

/// <summary>
/// One RCOPv1 PDU, the original RCOP PDU that CFTP uses, with no application identifier
/// (V-0011): a 4-octet header and the data (Figure V-2, V-0012).
/// </summary>
/// <param name="ConnectionId">Connection ID number, 0 to 15; 0 for a non-multiplexed connection (V-0013, V-0014).</param>
/// <param name="UPduId">U_PDU ID number, 0 to 255, one per file (V-0016).</param>
/// <param name="Segment">U_PDU segment number, 0 to 65535, from 0 within a file (V-0017 to V-0019).</param>
/// <param name="Data">The data: a segment of a BFTPv1 PDU, or a Message Acknowledgement.</param>
public sealed record Rcop1Pdu(byte ConnectionId, byte UPduId, ushort Segment, ReadOnlyMemory<byte> Data)
{
    /// <summary>The header size; the data of one RCOPv1 PDU are at most the MTU less this (V-0039, interp V-0072).</summary>
    public const int HeaderBytes = 4;

    /// <summary>The Message Acknowledgement body, 0x10 0x0B (Figure V-5, V-0044).</summary>
    public static ReadOnlySpan<byte> AckBody => [0x10, 0x0B];

    /// <summary>
    /// True for a Message Acknowledgement: segment 0 and data exactly 0x10 0x0B (interp V-0075).
    /// Segment 0 of a file always starts 0x10 0x02, so the two never meet.
    /// </summary>
    public bool IsAck => Segment == 0 && Data.Span.SequenceEqual(AckBody);

    /// <summary>The Message Acknowledgement for a file: its connection and U_PDU ID, segment 0 (V-0045, interp V-0075).</summary>
    public static Rcop1Pdu Ack(byte connectionId, byte uPduId) => new(connectionId, uPduId, 0, AckBody.ToArray());

    /// <summary>Encodes, with the reserved bits 0 (V-0015).</summary>
    public byte[] Encode()
    {
        if (ConnectionId > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectionId), "connection ID is 0 to 15 (V-0013)");
        }

        byte[] b = new byte[HeaderBytes + Data.Length];
        b[0] = (byte)(ConnectionId << 4);
        b[1] = UPduId;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), Segment);
        Data.Span.CopyTo(b.AsSpan(HeaderBytes));
        return b;
    }

    /// <summary>
    /// Decodes. Fewer than 4 octets is <see cref="CftpErrorKind.Truncated"/>; the reserved bits are
    /// ignored (interp V-0081).
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> b, out Rcop1Pdu? pdu, out CftpErrorKind error)
    {
        pdu = null;
        error = default;
        if (b.Length < HeaderBytes)
        {
            error = CftpErrorKind.Truncated;
            return false;
        }

        pdu = new Rcop1Pdu((byte)(b[0] >> 4), b[1], BinaryPrimitives.ReadUInt16BigEndian(b[2..]), b[HeaderBytes..].ToArray());
        return true;
    }
}
