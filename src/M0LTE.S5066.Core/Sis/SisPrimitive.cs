namespace M0LTE.S5066.Core.Sis;

/// <summary>Primitive type numbers, the first body byte of a frame [Ed4 S.5.3 to S.5.15] (matrix S-0071).</summary>
public enum SisPrimitiveType : byte
{
    /// <summary>S_BIND_REQUEST, type 1 (S-0078).</summary>
    BindRequest = 1,

    /// <summary>S_UNBIND_REQUEST, type 2 (S-0101).</summary>
    UnbindRequest = 2,

    /// <summary>S_BIND_ACCEPTED, type 3 (S-0103).</summary>
    BindAccepted = 3,

    /// <summary>S_BIND_REJECTED, type 4 (S-0104).</summary>
    BindRejected = 4,

    /// <summary>S_UNBIND_INDICATION, type 5 (S-0106).</summary>
    UnbindIndication = 5,

    /// <summary>S_SUBNET_AVAILABILITY, type 14 (S-0108).</summary>
    SubnetAvailability = 14,

    /// <summary>S_DATA_FLOW_ON, type 15 (S-0110).</summary>
    DataFlowOn = 15,

    /// <summary>S_DATA_FLOW_OFF, type 16, not the 15 printed in Fig S-12 (S-0111).</summary>
    DataFlowOff = 16,

    /// <summary>S_KEEP_ALIVE, type 17 (S-0112).</summary>
    KeepAlive = 17,

    /// <summary>S_MANAGEMENT_MSG_REQUEST, type 18 (S-0113).</summary>
    ManagementMsgRequest = 18,

    /// <summary>S_MANAGEMENT_MSG_INDICATION, type 19 (S-0113).</summary>
    ManagementMsgIndication = 19,

    /// <summary>S_UNIDATA_REQUEST, type 20 (S-0115).</summary>
    UnidataRequest = 20,

    /// <summary>S_UNIDATA_INDICATION, type 21 (S-0122).</summary>
    UnidataIndication = 21,

    /// <summary>S_UNIDATA_REQUEST_CONFIRM, type 22 (S-0127).</summary>
    UnidataRequestConfirm = 22,

    /// <summary>S_UNIDATA_REQUEST_REJECTED, type 23 (S-0129).</summary>
    UnidataRequestRejected = 23,
}

/// <summary>
/// A decoded or to-be-encoded SIS Access Protocol primitive (Annex S). Fields marked NOT USED are
/// not represented: they are sent as 0 and ignored on receipt (S-0073, S-0074), and so is the
/// Edition 3 RANK of a bind (S-0088).
/// </summary>
public abstract record SisPrimitive
{
    /// <summary>The type number of this primitive.</summary>
    public abstract SisPrimitiveType Type { get; }
}

/// <summary>S_BIND_REQUEST: SAP ID and default Service Type [Ed4 S.5.3, Fig S-5, p.S-10] (S-0078, S-0079).</summary>
public sealed record SisBindRequest(byte Sap, ServiceType ServiceType) : SisPrimitive
{
    /// <inheritdoc/>
    public override SisPrimitiveType Type => SisPrimitiveType.BindRequest;
}

/// <summary>S_UNBIND_REQUEST [Ed4 S.5.4, Fig S-7, p.S-12] (S-0101).</summary>
public sealed record SisUnbindRequest : SisPrimitive
{
    /// <inheritdoc/>
    public override SisPrimitiveType Type => SisPrimitiveType.UnbindRequest;
}

/// <summary>S_BIND_ACCEPTED: the SAP ID and the MTU [Ed4 S.5.5, Fig S-8, p.S-13] (S-0103).</summary>
public sealed record SisBindAccepted(byte Sap, ushort Mtu) : SisPrimitive
{
    /// <inheritdoc/>
    public override SisPrimitiveType Type => SisPrimitiveType.BindAccepted;
}

/// <summary>S_BIND_REJECTED with a reason from A.2.2.4 [Ed4 S.5.6, Fig S-9, p.S-13] (S-0104, S-0105, A-0052).</summary>
public sealed record SisBindRejected(byte Reason) : SisPrimitive
{
    /// <inheritdoc/>
    public override SisPrimitiveType Type => SisPrimitiveType.BindRejected;
}

/// <summary>S_UNBIND_INDICATION with a reason from A.2.2.5 [Ed4 S.5.7, Fig S-10, p.S-13] (S-0106, S-0107, A-0057).</summary>
public sealed record SisUnbindIndication(byte Reason) : SisPrimitive
{
    /// <inheritdoc/>
    public override SisPrimitiveType Type => SisPrimitiveType.UnbindIndication;
}

/// <summary>S_SUBNET_AVAILABILITY: node status and reason [Ed4 S.5.8, Fig S-11, p.S-14] (S-0108, S-0049, S-0050). Any byte values are carried (S-0053).</summary>
public sealed record SisSubnetAvailability(byte NodeStatus, byte Reason) : SisPrimitive
{
    /// <inheritdoc/>
    public override SisPrimitiveType Type => SisPrimitiveType.SubnetAvailability;
}

/// <summary>S_DATA_FLOW_ON [Ed4 S.5.9, Fig S-12, p.S-14] (S-0110).</summary>
public sealed record SisDataFlowOn : SisPrimitive
{
    /// <inheritdoc/>
    public override SisPrimitiveType Type => SisPrimitiveType.DataFlowOn;
}

/// <summary>S_DATA_FLOW_OFF, type 16 by our reading of Fig S-12 (S-0111).</summary>
public sealed record SisDataFlowOff : SisPrimitive
{
    /// <inheritdoc/>
    public override SisPrimitiveType Type => SisPrimitiveType.DataFlowOff;
}

/// <summary>S_KEEP_ALIVE [Ed4 S.5.10, Fig S-13, p.S-14] (S-0112).</summary>
public sealed record SisKeepAlive : SisPrimitive
{
    /// <inheritdoc/>
    public override SisPrimitiveType Type => SisPrimitiveType.KeepAlive;
}

/// <summary>S_MANAGEMENT_MSG_REQUEST: implementation-dependent message type and body [Ed4 S.5.11, Fig S-14, p.S-15] (S-0113, S-0114).</summary>
public sealed record SisManagementMsgRequest(byte MsgType, ReadOnlyMemory<byte> Body) : SisPrimitive
{
    /// <inheritdoc/>
    public override SisPrimitiveType Type => SisPrimitiveType.ManagementMsgRequest;

    /// <inheritdoc/>
    public bool Equals(SisManagementMsgRequest? other) =>
        other is not null && MsgType == other.MsgType && Body.Span.SequenceEqual(other.Body.Span);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(MsgType, Body.Length);
}

/// <summary>S_MANAGEMENT_MSG_INDICATION, the same layout as the request [Ed4 S.5.11, p.S-15] (S-0113).</summary>
public sealed record SisManagementMsgIndication(byte MsgType, ReadOnlyMemory<byte> Body) : SisPrimitive
{
    /// <inheritdoc/>
    public override SisPrimitiveType Type => SisPrimitiveType.ManagementMsgIndication;

    /// <inheritdoc/>
    public bool Equals(SisManagementMsgIndication? other) =>
        other is not null && MsgType == other.MsgType && Body.Span.SequenceEqual(other.Body.Span);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(MsgType, Body.Length);
}

/// <summary>
/// S_UNIDATA_REQUEST [Ed4 S.5.12, Fig S-15, p.S-15] (S-0115). The Priority is 0 to 15, the
/// <see cref="Ttl"/> is the 20-bit field as sent in units of 2 seconds (S-0116; clamping above
/// 648000 is the node's job, S-0121), and the U_PDU may be empty (S-0117).
/// </summary>
public sealed record SisUnidataRequest(
    byte Priority, byte DestSap, SisAddress DestAddress, ServiceType DeliveryMode, uint Ttl, ReadOnlyMemory<byte> UPdu) : SisPrimitive
{
    /// <inheritdoc/>
    public override SisPrimitiveType Type => SisPrimitiveType.UnidataRequest;

    /// <inheritdoc/>
    public bool Equals(SisUnidataRequest? other) =>
        other is not null && Priority == other.Priority && DestSap == other.DestSap && DestAddress == other.DestAddress
        && DeliveryMode == other.DeliveryMode && Ttl == other.Ttl && UPdu.Span.SequenceEqual(other.UPdu.Span);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Priority, DestSap, DestAddress, DeliveryMode, Ttl, UPdu.Length);
}

/// <summary>
/// S_UNIDATA_INDICATION [Ed4 S.5.13, Fig S-16, p.S-16] (S-0122). The error fields of non-ARQ with
/// errors are not supported: that option is not implemented (S-0123, plan 4.3).
/// </summary>
public sealed record SisUnidataIndication(
    byte Priority, byte DestSap, SisAddress DestAddress, byte TxMode, byte SrcSap, SisAddress SrcAddress, ReadOnlyMemory<byte> UPdu) : SisPrimitive
{
    /// <inheritdoc/>
    public override SisPrimitiveType Type => SisPrimitiveType.UnidataIndication;

    /// <inheritdoc/>
    public bool Equals(SisUnidataIndication? other) =>
        other is not null && Priority == other.Priority && DestSap == other.DestSap && DestAddress == other.DestAddress
        && TxMode == other.TxMode && SrcSap == other.SrcSap && SrcAddress == other.SrcAddress && UPdu.Span.SequenceEqual(other.UPdu.Span);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Priority, DestSap, DestAddress, TxMode, SrcSap, SrcAddress, UPdu.Length);
}

/// <summary>S_UNIDATA_REQUEST_CONFIRM with a copy of the U_PDU or its first bytes [Ed4 S.5.14, Fig S-17, p.S-17] (S-0127).</summary>
public sealed record SisUnidataRequestConfirm(byte DestSap, SisAddress DestAddress, ReadOnlyMemory<byte> UPdu) : SisPrimitive
{
    /// <inheritdoc/>
    public override SisPrimitiveType Type => SisPrimitiveType.UnidataRequestConfirm;

    /// <inheritdoc/>
    public bool Equals(SisUnidataRequestConfirm? other) =>
        other is not null && DestSap == other.DestSap && DestAddress == other.DestAddress && UPdu.Span.SequenceEqual(other.UPdu.Span);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(DestSap, DestAddress, UPdu.Length);
}

/// <summary>
/// S_UNIDATA_REQUEST_REJECTED: a 4-bit reason from A.2.2.8, 1 to 15, and a copy of the U_PDU or its
/// first bytes [Ed4 S.5.15, Fig S-18, p.S-18] (S-0129, S-0131).
/// </summary>
public sealed record SisUnidataRequestRejected(byte Reason, byte DestSap, SisAddress DestAddress, ReadOnlyMemory<byte> UPdu) : SisPrimitive
{
    /// <inheritdoc/>
    public override SisPrimitiveType Type => SisPrimitiveType.UnidataRequestRejected;

    /// <inheritdoc/>
    public bool Equals(SisUnidataRequestRejected? other) =>
        other is not null && Reason == other.Reason && DestSap == other.DestSap && DestAddress == other.DestAddress
        && UPdu.Span.SequenceEqual(other.UPdu.Span);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Reason, DestSap, DestAddress, UPdu.Length);
}
