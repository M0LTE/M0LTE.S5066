namespace M0LTE.S5066.Core.Dts;

/// <summary>
/// Byte-exact encoder and decoder for D_PDU types 0 to 15 [Ed4 C.4, pp.C-8 to C-56]. Sans-IO: bytes in,
/// typed result out; no clock, no I/O.
/// </summary>
/// <remarks>
/// Wire layout (C-0080): sync 90 EB, byte 0 = type (high nibble) and EOW bits 11-8, byte 1 = EOW bits
/// 7-0, byte 2 = EOT, byte 3 = size of address m (bits 7-5) and size of header h (bits 4-0), then m address
/// bytes, then the type-specific header, then the 2-byte header CRC. I and I+C types continue with the
/// segmented C_PDU and its 4-byte CRC-32. h counts bytes 0 to 3, the type-specific part and the header CRC
/// (C-0111, C-0112). Sync, h, the segment size and both CRCs are computed by the encoder and checked by
/// the decoder, so they are not in the value. Multi-byte header fields are most significant byte first
/// (C-0128, C-0134); the CRCs go least significant byte first (C-0145, C-0163).
/// </remarks>
public static class DpduCodec
{
    private const byte Sync0 = 0x90;
    private const byte Sync1 = 0xEB;
    private const int MaxSegment = 1023;
    private const int MaxLongAckShortForm = 21;

    // Allowed size-of-header ranges per type (corpus-format.md section 3.3).
    private static readonly (int Min, int Max)[] HeaderSize =
    [
        (9, 9), (7, 23), (10, 26), (9, 9), (9, 9), (7, 23), (8, 31), (15, 15),
        (15, 15), (10, 10), (10, 31), (13, 31), (10, 10), (7, 31), (6, 6), (7, 7),
    ];

    private const int UndefinedMinHeader = 6; // C-1021

    // ---------------------------------------------------------------- encode

    /// <summary>
    /// Encodes one D_PDU from its sync to its last CRC byte. CRC-OK flags in the value are ignored: the
    /// encoder always writes correct CRCs.
    /// </summary>
    /// <exception cref="ArgumentException">A field is outside the range its figure allows.</exception>
    public static byte[] Encode(Dpdu pdu)
    {
        ArgumentNullException.ThrowIfNull(pdu);
        var ts = new List<byte>();
        var tail = new List<byte>();
        int t = pdu.Type;

        switch (pdu)
        {
            case Ed3DataOnly p:
                AddDataFlags(ts, p.Flags, p.Segment);
                ts.Add(Byte(p.TxFsn, 255, nameof(p.TxFsn)));
                AddSegment(tail, p.Segment);
                break;
            case DataOnly p:
                AddDataFlags(ts, p.Flags, p.Segment);
                AddU16(ts, p.TxFsn, nameof(p.TxFsn));
                AddSegment(tail, p.Segment);
                break;
            case Ed3AckOnly p:
                ts.Add(Byte(p.RxLwe, 255, nameof(p.RxLwe)));
                AddAcks(ts, p.SelectiveAcks, 16);
                break;
            case ExpeditedAckOnly p:
                ts.Add(Byte(p.RxLwe, 255, nameof(p.RxLwe)));
                AddAcks(ts, p.SelectiveAcks, 16);
                break;
            case Ed3DataAck p:
                AddDataFlags(ts, p.Flags, p.Segment);
                ts.Add(Byte(p.TxFsn, 255, nameof(p.TxFsn)));
                ts.Add(Byte(p.RxLwe, 255, nameof(p.RxLwe)));
                AddAcks(ts, p.SelectiveAcks, 16);
                AddSegment(tail, p.Segment);
                break;
            case DataAck p:
                AddDataFlags(ts, p.Flags, p.Segment);
                AddU16(ts, p.TxFsn, nameof(p.TxFsn));
                AddU16(ts, p.RxLwe, nameof(p.RxLwe));
                ts.Add((byte)(Range(p.Encoding, 1, nameof(p.Encoding)) << 7));
                AddAcks(ts, p.SelectiveAcks, 18);
                AddSegment(tail, p.Segment);
                break;
            case AckOnly p:
                AddU16(ts, p.RxLwe, nameof(p.RxLwe));
                ts.Add((byte)((Range(p.Encoding, 1, nameof(p.Encoding)) << 7) | Range(p.Offset, 127, nameof(p.Offset))));
                if (p.AckData.Length > 255)
                {
                    throw new ArgumentException("ack data is at most 255 bytes (C-0223)", nameof(pdu));
                }

                ts.Add((byte)p.AckData.Length);
                if (p.AckData.Length <= MaxLongAckShortForm)
                {
                    ts.AddRange(p.AckData.Span.ToArray());
                }
                else
                {
                    tail.AddRange(p.AckData.Span.ToArray());
                    AddCrc32(tail, p.AckData.Span);
                }

                break;
            case Ed3ResetWinResync p:
                ts.Add(ResetFlagsByte(p.Flags));
                ts.Add(Byte(p.NewRxLwe, 255, nameof(p.NewRxLwe)));
                ts.Add(Byte(p.ResetFrameId, 255, nameof(p.ResetFrameId)));
                break;
            case ResetWinResync p:
                ts.Add(ResetFlagsByte(p.Flags));
                AddU16(ts, p.NewRxLwe, nameof(p.NewRxLwe));
                ts.Add(Byte(p.ResetFrameId, 255, nameof(p.ResetFrameId)));
                break;
            case ExpeditedDataOnly p:
            {
                int n = SegmentLength(p.Segment);
                ts.Add((byte)((p.CPduStart ? 0x80 : 0) | (p.CPduEnd ? 0x40 : 0) | (Range(p.CPduId, 15, nameof(p.CPduId)) << 2) | (n >> 8)));
                ts.Add((byte)n);
                ts.Add(Byte(p.TxFsn, 255, nameof(p.TxFsn)));
                AddSegment(tail, p.Segment);
                break;
            }

            case Management p:
                if (p.ExtendedData.Length > 23)
                {
                    throw new ArgumentException("extended management data is at most 23 bytes", nameof(pdu));
                }

                ts.Add((byte)((p.ExtendedMessage ? 4 : 0) | (p.ValidMessage ? 2 : 0) | (p.Ack ? 1 : 0)));
                ts.Add(Byte(p.ManagementFrameId, 255, nameof(p.ManagementFrameId)));
                ts.AddRange(p.ExtendedData.Span.ToArray());
                break;
            case NonArqData p:
                AddNonArq(ts, p.Fields, p.Segment);
                AddSegment(tail, p.Segment);
                break;
            case ExpeditedNonArqData p:
                AddNonArq(ts, p.Fields, p.Segment);
                AddSegment(tail, p.Segment);
                break;
            case Extension p:
                if (p.ExtendedHeader.Length > 24)
                {
                    throw new ArgumentException("extended header is at most 24 bytes", nameof(pdu));
                }

                ts.Add(Byte(p.ExtendedType, 255, nameof(p.ExtendedType)));
                ts.AddRange(p.ExtendedHeader.Span.ToArray());
                break;
            case Padding:
                break;
            case Warning p:
                ts.Add((byte)((Range(p.ReceivedFrameType, 15, nameof(p.ReceivedFrameType)) << 4) | Range(p.Reason, 15, nameof(p.Reason))));
                break;
            case UndefinedTypeDpdu:
                throw new ArgumentException("a D_PDU of an undefined type can only be decoded", nameof(pdu));
            default:
                throw new ArgumentException("unknown D_PDU record", nameof(pdu));
        }

        DpduHeader h = pdu.Header;
        Range(h.Eow, 4095, "Eow");
        Range(h.Eot, 255, "Eot");
        int hsize = 4 + ts.Count + 2;
        if (hsize < HeaderSize[t].Min || hsize > HeaderSize[t].Max)
        {
            throw new ArgumentException($"header size {hsize} is not allowed for type {t}", nameof(pdu));
        }

        var output = new byte[2 + hsize + h.AddressSize + tail.Count];
        output[0] = Sync0;
        output[1] = Sync1;
        output[2] = (byte)((t << 4) | (h.Eow >> 8));
        output[3] = (byte)h.Eow;
        output[4] = (byte)h.Eot;
        output[5] = (byte)((h.AddressSize << 5) | hsize);
        DpduAddress.Write(h.AddressSize, h.Dst, h.Src, output.AsSpan(6, h.AddressSize));
        ts.CopyTo(output, 6 + h.AddressSize);
        int crcAt = 2 + hsize + h.AddressSize - 2;
        Crc16S5066.Write(Crc16S5066.Compute(output.AsSpan(2, crcAt - 2)), output.AsSpan(crcAt, 2));
        tail.CopyTo(output, crcAt + 2);
        return output;
    }

    private static void AddNonArq(List<byte> ts, NonArqFields f, Octets segment)
    {
        int n = SegmentLength(segment);
        int id = Range(f.CPduId, 4095, nameof(f.CPduId));
        ts.Add((byte)(((id >> 8) << 4) | (f.DeliverInOrder ? 8 : 0) | (f.GroupAddress ? 4 : 0) | (n >> 8)));
        ts.Add((byte)n);
        ts.Add((byte)id);
        AddU16(ts, f.CPduSize, nameof(f.CPduSize));
        AddU16(ts, f.SegmentOffset, nameof(f.SegmentOffset));
        AddU16(ts, f.ReceptionWindow, nameof(f.ReceptionWindow));
    }

    private static void AddDataFlags(List<byte> ts, DataFlags f, Octets segment)
    {
        int n = SegmentLength(segment);
        ts.Add((byte)((f.CPduStart ? 0x80 : 0) | (f.CPduEnd ? 0x40 : 0) | (f.DeliverInOrder ? 0x20 : 0)
            | (f.DropCPdu ? 0x10 : 0) | (f.TxWinUwe ? 8 : 0) | (f.TxWinLwe ? 4 : 0) | (n >> 8)));
        ts.Add((byte)n);
    }

    private static byte ResetFlagsByte(ResetFlags f) =>
        (byte)((f.FullResetCmnd ? 8 : 0) | (f.ResetTxWinRqst ? 4 : 0) | (f.ResetRxWinCmnd ? 2 : 0) | (f.ResetAck ? 1 : 0));

    private static void AddAcks(List<byte> ts, Octets acks, int max)
    {
        if (acks.Length > max)
        {
            throw new ArgumentException($"selective acks are at most {max} bytes");
        }

        ts.AddRange(acks.Span.ToArray());
    }

    private static void AddSegment(List<byte> tail, Octets segment)
    {
        tail.AddRange(segment.Span.ToArray());
        AddCrc32(tail, segment.Span);
    }

    private static void AddCrc32(List<byte> tail, ReadOnlySpan<byte> data)
    {
        Span<byte> crc = stackalloc byte[4];
        Crc32S5066.Write(Crc32S5066.Compute(data), crc);
        tail.AddRange(crc.ToArray());
    }

    private static int SegmentLength(Octets segment) =>
        segment.Length <= MaxSegment ? segment.Length : throw new ArgumentException("segment is at most 1023 bytes (C-0154)");

    private static void AddU16(List<byte> ts, int value, string name)
    {
        Range(value, 65535, name);
        ts.Add((byte)(value >> 8));
        ts.Add((byte)value);
    }

    private static byte Byte(int value, int max, string name) => (byte)Range(value, max, name);

    private static int Range(int value, int max, string name) =>
        value >= 0 && value <= max ? value : throw new ArgumentException($"{name} must be 0 to {max}");

    // ---------------------------------------------------------------- decode

    /// <summary>
    /// Decodes exactly one D_PDU from <paramref name="bytes"/>, which must start at the sync and hold the
    /// whole PDU and nothing else. Checks run in the order of corpus-format.md section 3.3 and the first
    /// failure is reported. An <see cref="Extension"/> is the exception: what follows its header is
    /// unknown, so bytes after its header CRC are not reported as trailing (C-0365).
    /// </summary>
    public static DecodeResult<Dpdu> Decode(ReadOnlySpan<byte> bytes, PeerEdition peer)
    {
        DecodeResult<Dpdu> r = DecodeFirst(bytes, peer);
        return r.IsSuccess && r.Value is not Extension && r.Consumed < bytes.Length
            ? DecodeResult<Dpdu>.Fail(PduErrorKind.TrailingBytes)
            : r;
    }

    /// <summary>
    /// Decodes the D_PDU at the start of <paramref name="bytes"/> and reports how many bytes it used.
    /// Bytes after it are left alone, so a caller can step through a transmission. For an
    /// <see cref="Extension"/> the count covers the header only, and the caller resumes the sync search
    /// after it (C-0365).
    /// </summary>
    public static DecodeResult<Dpdu> DecodeFirst(ReadOnlySpan<byte> bytes, PeerEdition peer)
    {
        static DecodeResult<Dpdu> Fail(PduErrorKind k) => DecodeResult<Dpdu>.Fail(k);

        if (bytes.Length < 2)
        {
            return Fail(PduErrorKind.Truncated);
        }

        if (bytes[0] != Sync0 || bytes[1] != Sync1)
        {
            return Fail(PduErrorKind.BadSync);
        }

        if (bytes.Length < 6)
        {
            return Fail(PduErrorKind.Truncated);
        }

        int type = bytes[2] >> 4;
        int m = bytes[5] >> 5;
        int h = bytes[5] & 31;
        if (m == 0)
        {
            return Fail(PduErrorKind.BadAddressSize);
        }

        bool undefined = peer == PeerEdition.Ed3 && type >= 9 && type <= 14;
        bool okSize = undefined ? h >= UndefinedMinHeader : h >= HeaderSize[type].Min && h <= HeaderSize[type].Max;
        if (!okSize)
        {
            return Fail(PduErrorKind.BadHeaderSize);
        }

        int end = 2 + h + m;
        if (bytes.Length < end)
        {
            return Fail(PduErrorKind.Truncated);
        }

        if (Crc16S5066.Compute(bytes[2..(end - 2)]) != Crc16S5066.Read(bytes[(end - 2)..end]))
        {
            return Fail(PduErrorKind.HeaderCrc);
        }

        DpduAddress.Read(bytes.Slice(6, m), out uint dst, out uint src);
        var header = new DpduHeader(((bytes[2] & 15) << 8) | bytes[3], bytes[4], m, dst, src);
        ReadOnlySpan<byte> ts = bytes[(6 + m)..(end - 2)];
        ReadOnlySpan<byte> rest = bytes[end..];
        byte[] tb = ts.ToArray(); // lambdas below cannot capture a span

        if (undefined)
        {
            return DecodeResult<Dpdu>.Ok(new UndefinedTypeDpdu(header, type, new Octets(ts)), end);
        }

        switch (type)
        {
            case 0:
            {
                DataFlags f = ReadDataFlags(ts, out int n);
                return WithSegment(rest, n, end, (seg, ok) => new Ed3DataOnly(header, f, tb[2], seg, ok));
            }

            case 9:
            {
                DataFlags f = ReadDataFlags(ts, out int n);
                return WithSegment(rest, n, end, (seg, ok) => new DataOnly(header, f, U16(tb.AsSpan(2)), seg, ok));
            }

            case 2:
            {
                DataFlags f = ReadDataFlags(ts, out int n);
                var acks = new Octets(ts[4..]);
                return WithSegment(rest, n, end, (seg, ok) => new Ed3DataAck(header, f, tb[2], tb[3], acks, seg, ok));
            }

            case 11:
            {
                DataFlags f = ReadDataFlags(ts, out int n);
                var acks = new Octets(ts[7..]);
                return WithSegment(rest, n, end, (seg, ok) => new DataAck(header, f, U16(tb.AsSpan(2)), U16(tb.AsSpan(4)), tb[6] >> 7, acks, seg, ok));
            }

            case 1:
                return DecodeResult<Dpdu>.Ok(new Ed3AckOnly(header, ts[0], new Octets(ts[1..])), end);
            case 5:
                return DecodeResult<Dpdu>.Ok(new ExpeditedAckOnly(header, ts[0], new Octets(ts[1..])), end);
            case 3:
                return DecodeResult<Dpdu>.Ok(new Ed3ResetWinResync(header, ReadResetFlags(ts[0]), ts[1], ts[2]), end);
            case 12:
                return DecodeResult<Dpdu>.Ok(new ResetWinResync(header, ReadResetFlags(ts[0]), U16(ts[1..]), ts[3]), end);
            case 4:
            {
                int n = ((ts[0] & 3) << 8) | ts[1];
                return WithSegment(rest, n, end, (seg, ok) =>
                    new ExpeditedDataOnly(header, (tb[0] & 0x80) != 0, (tb[0] & 0x40) != 0, (tb[0] >> 2) & 15, tb[2], seg, ok));
            }

            case 6:
                return DecodeResult<Dpdu>.Ok(
                    new Management(header, (ts[0] & 4) != 0, (ts[0] & 2) != 0, (ts[0] & 1) != 0, ts[1], new Octets(ts[2..])), end);
            case 7:
            case 8:
            {
                int n = ((ts[0] & 3) << 8) | ts[1];
                var f = new NonArqFields(((ts[0] >> 4) << 8) | ts[2], (ts[0] & 8) != 0, (ts[0] & 4) != 0, U16(ts[3..]), U16(ts[5..]), U16(ts[7..]));
                return type == 7
                    ? WithSegment(rest, n, end, (seg, ok) => new NonArqData(header, f, seg, ok))
                    : WithSegment(rest, n, end, (seg, ok) => new ExpeditedNonArqData(header, f, seg, ok));
            }

            case 10:
                return DecodeAckOnly(header, ts, rest, end);
            case 13:
                // Unrecognised extension: header and extended type only, nothing after the header CRC (C-0365).
                return DecodeResult<Dpdu>.Ok(new Extension(header, ts[0], new Octets(ts[1..])), end);
            case 14:
                return DecodeResult<Dpdu>.Ok(new Padding(header), end);
            default:
                return DecodeResult<Dpdu>.Ok(new Warning(header, ts[0] >> 4, ts[0] & 15), end);
        }
    }

    private static DecodeResult<Dpdu> DecodeAckOnly(DpduHeader header, ReadOnlySpan<byte> ts, ReadOnlySpan<byte> rest, int end)
    {
        int aa = ts[3];
        int rxLwe = U16(ts);
        int encoding = ts[2] >> 7;
        int offset = ts[2] & 127;
        if (aa <= MaxLongAckShortForm)
        {
            // Header form: h must equal 10 + aa (C-0235).
            return ts.Length == 4 + aa
                ? DecodeResult<Dpdu>.Ok(new AckOnly(header, rxLwe, encoding, offset, new Octets(ts[4..]), null), end)
                : DecodeResult<Dpdu>.Fail(PduErrorKind.BadHeaderSize);
        }

        if (ts.Length != 4)
        {
            return DecodeResult<Dpdu>.Fail(PduErrorKind.BadHeaderSize);
        }

        if (rest.Length < aa + 4)
        {
            return DecodeResult<Dpdu>.Fail(PduErrorKind.Truncated);
        }

        ReadOnlySpan<byte> ack = rest[..aa];
        bool ok = Crc32S5066.Compute(ack) == Crc32S5066.Read(rest.Slice(aa, 4));
        return DecodeResult<Dpdu>.Ok(new AckOnly(header, rxLwe, encoding, offset, new Octets(ack), ok), end + aa + 4);
    }

    private static DecodeResult<Dpdu> WithSegment(ReadOnlySpan<byte> rest, int n, int end, Func<Octets, bool, Dpdu> make)
    {
        if (rest.Length < n + 4)
        {
            return DecodeResult<Dpdu>.Fail(PduErrorKind.Truncated);
        }

        ReadOnlySpan<byte> seg = rest[..n];
        bool ok = Crc32S5066.Compute(seg) == Crc32S5066.Read(rest.Slice(n, 4));
        return DecodeResult<Dpdu>.Ok(make(new Octets(seg), ok), end + n + 4);
    }

    private static DataFlags ReadDataFlags(ReadOnlySpan<byte> ts, out int size)
    {
        size = ((ts[0] & 3) << 8) | ts[1];
        return new DataFlags((ts[0] & 0x80) != 0, (ts[0] & 0x40) != 0, (ts[0] & 0x20) != 0, (ts[0] & 0x10) != 0, (ts[0] & 8) != 0, (ts[0] & 4) != 0);
    }

    private static ResetFlags ReadResetFlags(byte b) => new((b & 8) != 0, (b & 4) != 0, (b & 2) != 0, (b & 1) != 0);

    private static int U16(ReadOnlySpan<byte> s) => (s[0] << 8) | s[1];
}
