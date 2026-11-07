namespace M0LTE.S5066.Core.Sis;

/// <summary>
/// Byte-exact encoding and whole-frame decoding of SIS Access Protocol frames [Ed4 Annex S.5].
/// For streams use <see cref="SisFrameDecoder"/>.
/// </summary>
/// <remarks>
/// A frame is the preamble 90 EB (S-0061 to S-0063), the version 00 (S-0065, S-0066), the body
/// size in 2 bytes, most significant first (S-0067, S-0068), then the body, whose first byte is
/// the type (S-0071). Multi-byte fields are big-endian (S-0057), the preamble being the stated
/// exception (S-0064). All primitives are decoded whatever their direction; whether a primitive
/// is allowed from a client or from the node is the node's policy.
/// </remarks>
public static class SisCodec
{
    /// <summary>The frame bytes before the body: preamble, version and size.</summary>
    public const int HeaderSize = 5;

    /// <summary>The largest body the 2-byte size field can announce (S-0067).</summary>
    public const int MaxBodySize = 0xFFFF;

    private const byte Preamble0 = 0x90;
    private const byte Preamble1 = 0xEB;

    /// <summary>Encodes a primitive as one whole frame.</summary>
    /// <exception cref="ArgumentException">A field is out of range, or the body would exceed 65535 bytes.</exception>
    public static byte[] Encode(SisPrimitive primitive)
    {
        ArgumentNullException.ThrowIfNull(primitive);
        int bodySize = BodySize(primitive);
        if (bodySize > MaxBodySize)
        {
            throw new ArgumentException("body exceeds the 65535 byte size field", nameof(primitive));
        }

        var frame = new byte[HeaderSize + bodySize];
        frame[0] = Preamble0;
        frame[1] = Preamble1;
        frame[2] = 0;
        frame[3] = (byte)(bodySize >> 8);
        frame[4] = (byte)bodySize;
        WriteBody(primitive, frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>
    /// Decodes exactly one frame. Check order: fewer than 2 bytes is truncated; a preamble that is
    /// not 90 EB is bad sync; fewer than 5 bytes truncated; version, size, truncated body, type,
    /// body length, then trailing bytes (corpus-format.md section 3.7).
    /// </summary>
    public static SisDecodeResult DecodeFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2)
        {
            return SisDecodeResult.Fail(SisErrorKind.Truncated);
        }

        Step step = Next(frame);
        return step.Status switch
        {
            StepStatus.Need => SisDecodeResult.Fail(SisErrorKind.Truncated),
            StepStatus.Error => step.Result!,
            _ => step.Consumed != frame.Length ? SisDecodeResult.Fail(SisErrorKind.TrailingBytes) : step.Result!,
        };
    }

    internal enum StepStatus
    {
        Need,
        Error,
        Value,
    }

    /// <summary>The outcome of looking at the start of a buffer. <c>Consumed</c> is the bytes to drop.</summary>
    internal readonly record struct Step(StepStatus Status, SisDecodeResult? Result, int Consumed);

    /// <summary>
    /// Looks at the bytes where a preamble is expected. For header-level errors (bad sync, bad
    /// version, zero size) <c>Consumed</c> is 1, the stream scan resuming one byte on (S-0075); for
    /// body-level errors it is the whole frame, skipped by its size (S-0076).
    /// </summary>
    internal static Step Next(ReadOnlySpan<byte> r)
    {
        if (r.IsEmpty)
        {
            return new Step(StepStatus.Need, null, 0);
        }

        if (r[0] != Preamble0)
        {
            return Fail(SisErrorKind.BadSync, 1);
        }

        if (r.Length < 2)
        {
            return new Step(StepStatus.Need, null, 0);
        }

        if (r[1] != Preamble1)
        {
            return Fail(SisErrorKind.BadSync, 1);
        }

        if (r.Length < HeaderSize)
        {
            return new Step(StepStatus.Need, null, 0);
        }

        if (r[2] != 0)
        {
            return Fail(SisErrorKind.BadVersion, 1);
        }

        int size = (r[3] << 8) | r[4];
        if (size == 0)
        {
            return Fail(SisErrorKind.BadSize, 1);
        }

        int total = HeaderSize + size;
        if (r.Length < total)
        {
            return new Step(StepStatus.Need, null, 0);
        }

        ReadOnlySpan<byte> body = r.Slice(HeaderSize, size);
        byte type = body[0];
        if (type is >= 6 and <= 13 or >= 24 and <= 27)
        {
            return Fail(SisErrorKind.DeprecatedType, total, type);
        }

        if (type == 0 || type > 23)
        {
            return Fail(SisErrorKind.UnknownType, total, type);
        }

        SisPrimitive? p = ParseBody(type, body);
        return p is null
            ? Fail(SisErrorKind.BadLength, total, type)
            : new Step(StepStatus.Value, SisDecodeResult.Ok(p), total);
    }

    private static Step Fail(SisErrorKind kind, int consumed, byte? typeByte = null) =>
        new(StepStatus.Error, SisDecodeResult.Fail(kind, typeByte), consumed);

    private static SisPrimitive? ParseBody(byte type, ReadOnlySpan<byte> d)
    {
        int n = d.Length;
        switch ((SisPrimitiveType)type)
        {
            case SisPrimitiveType.BindRequest when n == 4:
                return new SisBindRequest((byte)(d[1] >> 4), ServiceType.FromBytes(d[2], d[3] >> 4));
            case SisPrimitiveType.UnbindRequest when n == 1:
                return new SisUnbindRequest();
            case SisPrimitiveType.BindAccepted when n == 4:
                return new SisBindAccepted((byte)(d[1] >> 4), (ushort)((d[2] << 8) | d[3]));
            case SisPrimitiveType.BindRejected when n == 2:
                return new SisBindRejected(d[1]);
            case SisPrimitiveType.UnbindIndication when n == 2:
                return new SisUnbindIndication(d[1]);
            case SisPrimitiveType.SubnetAvailability when n == 3:
                return new SisSubnetAvailability(d[1], d[2]);
            case SisPrimitiveType.DataFlowOn when n == 1:
                return new SisDataFlowOn();
            case SisPrimitiveType.DataFlowOff when n == 1:
                return new SisDataFlowOff();
            case SisPrimitiveType.KeepAlive when n == 1:
                return new SisKeepAlive();
            case SisPrimitiveType.ManagementMsgRequest when n >= 2:
                return new SisManagementMsgRequest(d[1], d[2..].ToArray());
            case SisPrimitiveType.ManagementMsgIndication when n >= 2:
                return new SisManagementMsgIndication(d[1], d[2..].ToArray());
            case SisPrimitiveType.UnidataRequest when n >= 12 && n == 12 + ((d[10] << 8) | d[11]):
                return new SisUnidataRequest(
                    (byte)(d[1] >> 4), (byte)(d[1] & 15), SisAddress.Read(d.Slice(2, 4)),
                    ServiceType.FromBytes(d[6], d[7] >> 4), ((uint)(d[7] & 15) << 16) | (uint)((d[8] << 8) | d[9]), d[12..].ToArray());
            case SisPrimitiveType.UnidataIndication when n >= 13 && n == 13 + ((d[11] << 8) | d[12]):
                return new SisUnidataIndication(
                    (byte)(d[1] >> 4), (byte)(d[1] & 15), SisAddress.Read(d.Slice(2, 4)),
                    (byte)(d[6] >> 4), (byte)(d[6] & 15), SisAddress.Read(d.Slice(7, 4)), d[13..].ToArray());
            case SisPrimitiveType.UnidataRequestConfirm when n >= 8 && n == 8 + ((d[6] << 8) | d[7]):
                return new SisUnidataRequestConfirm((byte)(d[1] & 15), SisAddress.Read(d.Slice(2, 4)), d[8..].ToArray());
            case SisPrimitiveType.UnidataRequestRejected when n >= 8 && n == 8 + ((d[6] << 8) | d[7]):
                return new SisUnidataRequestRejected((byte)(d[1] >> 4), (byte)(d[1] & 15), SisAddress.Read(d.Slice(2, 4)), d[8..].ToArray());
            default:
                return null;
        }
    }

    private static int BodySize(SisPrimitive p) => p switch
    {
        SisBindRequest => 4,
        SisUnbindRequest or SisDataFlowOn or SisDataFlowOff or SisKeepAlive => 1,
        SisBindAccepted => 4,
        SisBindRejected or SisUnbindIndication => 2,
        SisSubnetAvailability => 3,
        SisManagementMsgRequest m => 2 + m.Body.Length,
        SisManagementMsgIndication m => 2 + m.Body.Length,
        SisUnidataRequest u => 12 + CheckUPdu(u.UPdu.Length),
        SisUnidataIndication u => 13 + CheckUPdu(u.UPdu.Length),
        SisUnidataRequestConfirm u => 8 + CheckUPdu(u.UPdu.Length),
        SisUnidataRequestRejected u => 8 + CheckUPdu(u.UPdu.Length),
        _ => throw new ArgumentException("unknown primitive", nameof(p)),
    };

    private static int CheckUPdu(int length) =>
        length <= 0xFFFF ? length : throw new ArgumentException("U_PDU exceeds the 16-bit size field");

    private static void Nibble(int value, string name)
    {
        if (value is < 0 or > 15)
        {
            throw new ArgumentException(name + " must be 0 to 15");
        }
    }

    private static void CheckServiceType(ServiceType s)
    {
        if (!s.IsValid)
        {
            throw new ArgumentException("service type field out of range");
        }
    }

    private static void WriteBody(SisPrimitive p, Span<byte> d)
    {
        d[0] = (byte)p.Type;
        switch (p)
        {
            case SisBindRequest b:
                Nibble(b.Sap, "SAP");
                CheckServiceType(b.ServiceType);
                d[1] = (byte)(b.Sap << 4); // RANK is sent as 0 (S-0088)
                d[2] = b.ServiceType.HighByte;
                d[3] = (byte)(b.ServiceType.MinRetx << 4);
                break;
            case SisBindAccepted a:
                Nibble(a.Sap, "SAP");
                d[1] = (byte)(a.Sap << 4);
                d[2] = (byte)(a.Mtu >> 8);
                d[3] = (byte)a.Mtu;
                break;
            case SisBindRejected r:
                d[1] = r.Reason;
                break;
            case SisUnbindIndication r:
                d[1] = r.Reason;
                break;
            case SisSubnetAvailability s:
                d[1] = s.NodeStatus;
                d[2] = s.Reason;
                break;
            case SisManagementMsgRequest m:
                d[1] = m.MsgType;
                m.Body.Span.CopyTo(d[2..]);
                break;
            case SisManagementMsgIndication m:
                d[1] = m.MsgType;
                m.Body.Span.CopyTo(d[2..]);
                break;
            case SisUnidataRequest u:
                Nibble(u.Priority, "priority");
                Nibble(u.DestSap, "destination SAP");
                CheckServiceType(u.DeliveryMode);
                if (u.Ttl > 0xFFFFF)
                {
                    throw new ArgumentException("TTL exceeds 20 bits");
                }

                d[1] = (byte)((u.Priority << 4) | u.DestSap);
                u.DestAddress.Write(d.Slice(2, 4));
                d[6] = u.DeliveryMode.HighByte;
                d[7] = (byte)((u.DeliveryMode.MinRetx << 4) | (int)(u.Ttl >> 16));
                d[8] = (byte)(u.Ttl >> 8);
                d[9] = (byte)u.Ttl;
                d[10] = (byte)(u.UPdu.Length >> 8);
                d[11] = (byte)u.UPdu.Length;
                u.UPdu.Span.CopyTo(d[12..]);
                break;
            case SisUnidataIndication u:
                Nibble(u.Priority, "priority");
                Nibble(u.DestSap, "destination SAP");
                Nibble(u.TxMode, "transmission mode");
                Nibble(u.SrcSap, "source SAP");
                d[1] = (byte)((u.Priority << 4) | u.DestSap);
                u.DestAddress.Write(d.Slice(2, 4));
                d[6] = (byte)((u.TxMode << 4) | u.SrcSap);
                u.SrcAddress.Write(d.Slice(7, 4));
                d[11] = (byte)(u.UPdu.Length >> 8);
                d[12] = (byte)u.UPdu.Length;
                u.UPdu.Span.CopyTo(d[13..]);
                break;
            case SisUnidataRequestConfirm u:
                Nibble(u.DestSap, "destination SAP");
                d[1] = u.DestSap; // high nibble NOT USED, sent as 0
                u.DestAddress.Write(d.Slice(2, 4));
                d[6] = (byte)(u.UPdu.Length >> 8);
                d[7] = (byte)u.UPdu.Length;
                u.UPdu.Span.CopyTo(d[8..]);
                break;
            case SisUnidataRequestRejected u:
                Nibble(u.Reason, "reason");
                Nibble(u.DestSap, "destination SAP");
                d[1] = (byte)((u.Reason << 4) | u.DestSap);
                u.DestAddress.Write(d.Slice(2, 4));
                d[6] = (byte)(u.UPdu.Length >> 8);
                d[7] = (byte)u.UPdu.Length;
                u.UPdu.Span.CopyTo(d[8..]);
                break;
        }
    }
}
