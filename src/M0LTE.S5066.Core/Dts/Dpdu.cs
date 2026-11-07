namespace M0LTE.S5066.Core.Dts;

/// <summary>
/// The generic header every D_PDU starts with after the sync (C-0085): the 12-bit EOW (C-0087,
/// bits 11-8 are the EOW type, see <see cref="EowCodec"/>), the 8-bit EOT in half seconds (C-0088,
/// C-0104), and the address field with its size m (C-0107). Sizes, CRCs and sync are not held here
/// because they follow from the rest and the encoder computes them.
/// </summary>
/// <param name="Eow">The 12-bit EOW, 0 to 4095 (C-0098).</param>
/// <param name="Eot">Half seconds remaining in the transmission, 0 to 255 (C-0103, C-0106).</param>
/// <param name="AddressSize">The size-of-address field m, 1 to 7 (C-0107, C-0108). The sender chooses it (C-0133).</param>
/// <param name="Dst">Destination, a 28-bit value (C-0115).</param>
/// <param name="Src">Source, a 28-bit value (C-0116).</param>
public readonly record struct DpduHeader(int Eow, int Eot, int AddressSize, uint Dst, uint Src);

/// <summary>Flags shared by the data-carrying ARQ types 0, 2, 9 and 11 (C-0170, C-0175).</summary>
public readonly record struct DataFlags(
    bool CPduStart,
    bool CPduEnd,
    bool DeliverInOrder,
    bool DropCPdu,
    bool TxWinUwe,
    bool TxWinLwe);

/// <summary>Flags of the reset types 3 and 12 (C-0251, C-0258).</summary>
public readonly record struct ResetFlags(
    bool FullResetCmnd,
    bool ResetTxWinRqst,
    bool ResetRxWinCmnd,
    bool ResetAck);

/// <summary>The type-specific fields shared by NON-ARQ-DATA (7) and EXPEDITED-NON-ARQ-DATA (8) (C-0322, C-0358).</summary>
/// <param name="CPduId">12-bit C_PDU ID, 0 to 4095, split across two header bytes (C-0329 to C-0332).</param>
/// <param name="DeliverInOrder">Kept for Ed3 compatibility (C-0333).</param>
/// <param name="GroupAddress">The destination is a group address (C-0338 to C-0340).</param>
/// <param name="CPduSize">Size of the whole C_PDU, 16 bits (C-0343, C-0344).</param>
/// <param name="SegmentOffset">Offset of this segment in the C_PDU, 16 bits (C-0346, C-0348, C-0357).</param>
/// <param name="ReceptionWindow">Reception window in half seconds, 16 bits (C-0350, C-0355).</param>
public readonly record struct NonArqFields(
    int CPduId,
    bool DeliverInOrder,
    bool GroupAddress,
    int CPduSize,
    int SegmentOffset,
    int ReceptionWindow);

/// <summary>A decoded or to-be-encoded D_PDU. One sealed record per type [Ed4 Table C-2, p.C-8].</summary>
/// <param name="Header">The generic header.</param>
public abstract record Dpdu(DpduHeader Header)
{
    /// <summary>The 4-bit D_PDU type, 0 to 15 (C-0086).</summary>
    public abstract int Type { get; }
}

/// <summary>Type 0 ED3-DATA-ONLY (C-0059, C-0170): 8-bit TX FSN, segmented C_PDU.</summary>
public sealed record Ed3DataOnly(DpduHeader Header, DataFlags Flags, int TxFsn, Octets Segment, bool SegmentCrcOk) : Dpdu(Header)
{
    /// <inheritdoc/>
    public override int Type => 0;
}

/// <summary>Type 1 ED3-ACK-ONLY (C-0194): 8-bit RX LWE and 0 to 16 selective ack bytes.</summary>
public sealed record Ed3AckOnly(DpduHeader Header, int RxLwe, Octets SelectiveAcks) : Dpdu(Header)
{
    /// <inheritdoc/>
    public override int Type => 1;
}

/// <summary>Type 2 ED3-DATA-ACK (C-0236): type 0 fields plus RX LWE and selective acks.</summary>
public sealed record Ed3DataAck(DpduHeader Header, DataFlags Flags, int TxFsn, int RxLwe, Octets SelectiveAcks, Octets Segment, bool SegmentCrcOk) : Dpdu(Header)
{
    /// <inheritdoc/>
    public override int Type => 2;
}

/// <summary>Type 3 ED3-RESET/WIN-RESYNC (C-0251): flags, 8-bit new RX LWE, reset frame ID.</summary>
public sealed record Ed3ResetWinResync(DpduHeader Header, ResetFlags Flags, int NewRxLwe, int ResetFrameId) : Dpdu(Header)
{
    /// <inheritdoc/>
    public override int Type => 3;
}

/// <summary>Type 4 EXPEDITED-DATA-ONLY (C-0275): 4-bit C_PDU ID, 8-bit TX FSN.</summary>
public sealed record ExpeditedDataOnly(DpduHeader Header, bool CPduStart, bool CPduEnd, int CPduId, int TxFsn, Octets Segment, bool SegmentCrcOk) : Dpdu(Header)
{
    /// <inheritdoc/>
    public override int Type => 4;
}

/// <summary>Type 5 EXPEDITED-ACK-ONLY (C-0289): the same format as type 1.</summary>
public sealed record ExpeditedAckOnly(DpduHeader Header, int RxLwe, Octets SelectiveAcks) : Dpdu(Header)
{
    /// <inheritdoc/>
    public override int Type => 5;
}

/// <summary>Type 6 MANAGEMENT (C-0293): flags, frame ID and 0 to 23 extended bytes.</summary>
/// <param name="Header">The generic header.</param>
/// <param name="ExtendedMessage">The EXTENDED MESSAGE bit, carried as sent (C-0303, C-0304).</param>
/// <param name="ValidMessage">The VALID MESSAGE bit (C-0300 to C-0302).</param>
/// <param name="Ack">The ACK bit (C-0296).</param>
/// <param name="ManagementFrameId">0 to 255 (C-0305).</param>
/// <param name="ExtendedData">Extended management bytes, 0 to 23.</param>
public sealed record Management(DpduHeader Header, bool ExtendedMessage, bool ValidMessage, bool Ack, int ManagementFrameId, Octets ExtendedData) : Dpdu(Header)
{
    /// <inheritdoc/>
    public override int Type => 6;
}

/// <summary>Type 7 NON-ARQ-DATA (C-0322).</summary>
public sealed record NonArqData(DpduHeader Header, NonArqFields Fields, Octets Segment, bool SegmentCrcOk) : Dpdu(Header)
{
    /// <inheritdoc/>
    public override int Type => 7;
}

/// <summary>Type 8 EXPEDITED-NON-ARQ-DATA: the type 7 format with type value 8 (C-0358).</summary>
public sealed record ExpeditedNonArqData(DpduHeader Header, NonArqFields Fields, Octets Segment, bool SegmentCrcOk) : Dpdu(Header)
{
    /// <inheritdoc/>
    public override int Type => 8;
}

/// <summary>Type 9 DATA-ONLY (C-0171): 16-bit TX FSN.</summary>
public sealed record DataOnly(DpduHeader Header, DataFlags Flags, int TxFsn, Octets Segment, bool SegmentCrcOk) : Dpdu(Header)
{
    /// <inheritdoc/>
    public override int Type => 9;
}

/// <summary>
/// Type 10 ACK-ONLY (C-0195). Ack data of 21 bytes or fewer rides in the header; longer data follows
/// the header CRC with its own CRC-32 (C-0218, C-0219, C-0235). The form follows from the length.
/// </summary>
/// <param name="Header">The generic header.</param>
/// <param name="RxLwe">16-bit receive lower window edge.</param>
/// <param name="Encoding">0 bitmap, 1 run length encoding (C-0227, C-0228).</param>
/// <param name="Offset">7 bits (C-0222).</param>
/// <param name="AckData">0 to 255 bytes (C-0223).</param>
/// <param name="AckDataCrcOk">Null in the header form; in the long form whether the CRC-32 matched.</param>
public sealed record AckOnly(DpduHeader Header, int RxLwe, int Encoding, int Offset, Octets AckData, bool? AckDataCrcOk) : Dpdu(Header)
{
    /// <inheritdoc/>
    public override int Type => 10;
}

/// <summary>Type 11 DATA-ACK (C-0237): 16-bit FSN and RX LWE, an Encoding bit, 0 to 18 ack bytes.</summary>
public sealed record DataAck(DpduHeader Header, DataFlags Flags, int TxFsn, int RxLwe, int Encoding, Octets SelectiveAcks, Octets Segment, bool SegmentCrcOk) : Dpdu(Header)
{
    /// <inheritdoc/>
    public override int Type => 11;
}

/// <summary>Type 12 RESET/WIN-RESYNC (C-0252): as type 3 with a 16-bit new RX LWE.</summary>
public sealed record ResetWinResync(DpduHeader Header, ResetFlags Flags, int NewRxLwe, int ResetFrameId) : Dpdu(Header)
{
    /// <inheritdoc/>
    public override int Type => 12;
}

/// <summary>
/// Type 13 EXTENSION (C-0360). No extended type is implemented, so a decoded EXTENSION is always an
/// unrecognised extension (<see cref="IsRecognised"/> is false): the generic header and the extended
/// type are decoded, and whatever follows the header CRC is not read or reported. A receiver resumes
/// the sync search after the header CRC (C-0365).
/// </summary>
/// <param name="Header">The generic header.</param>
/// <param name="ExtendedType">The extended D_PDU type, 0 to 255.</param>
/// <param name="ExtendedHeader">Bytes between the extended type and the header CRC, 0 to 24.</param>
public sealed record Extension(DpduHeader Header, int ExtendedType, Octets ExtendedHeader) : Dpdu(Header)
{
    /// <inheritdoc/>
    public override int Type => 13;

    /// <summary>Whether the extended type is one this codec implements. Always false (C-0365).</summary>
    public bool IsRecognised => false;
}

/// <summary>Type 14 PADDING (C-0366): generic header only.</summary>
public sealed record Padding(DpduHeader Header) : Dpdu(Header)
{
    /// <inheritdoc/>
    public override int Type => 14;
}

/// <summary>Type 15 WARNING (C-0375): the offending type and a reason (Table C-3, C-0381, C-0382).</summary>
public sealed record Warning(DpduHeader Header, int ReceivedFrameType, int Reason) : Dpdu(Header)
{
    /// <inheritdoc/>
    public override int Type => 15;
}

/// <summary>
/// A type 9 to 14 D_PDU read as from an Ed3 peer, for which the type is undefined (C-1021). The bytes
/// between the address field and the header CRC are kept raw.
/// </summary>
/// <param name="Header">The generic header.</param>
/// <param name="UndefinedType">The type value, 9 to 14.</param>
/// <param name="TypeSpecific">Bytes between the address field and the header CRC.</param>
public sealed record UndefinedTypeDpdu(DpduHeader Header, int UndefinedType, Octets TypeSpecific) : Dpdu(Header)
{
    /// <inheritdoc/>
    public override int Type => UndefinedType;
}
