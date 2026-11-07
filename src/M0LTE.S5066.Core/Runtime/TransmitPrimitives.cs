namespace M0LTE.S5066.Core.Runtime;

// The CAS-to-DTS request primitives for expedited delivery and the DTS's refusals of requests
// (Table C-1; core-architecture.md 4.1), added by P3-4 with the unconnected request tables
// C-9, C-11, C-13 and C-15. Like the P3-1 records they carry the group flag beside the destination
// (C-0122) and the dispatcher's request handle (section 10 item 10).

/// <summary>Why the DTS refused a request. The reason values are NOT STATED (C-0731).</summary>
internal enum DReason
{
    /// <summary>An ARQ request with no connection to the peer (C-0573, C-0593, C-0613, C-0633; C-0731).</summary>
    NoConnection,

    /// <summary>The C_PDU's TTL expired before it was sent (C-0859, C-1050).</summary>
    TtlExpired,

    /// <summary>A FULL RESET discarded the C_PDU part way through (C-0813, C-0823, C-0826, C-1176): a local reason (C-0866).</summary>
    Reset,

    /// <summary>The DTS abandoned the connection itself (C-0739): a local reason (C-0866).</summary>
    ConnectionLost,
}

/// <summary>Why the CAS refused a C_UNIDATA_REQUEST (Table B-1, B-0018). The values are ours.</summary>
internal enum CReason
{
    /// <summary>The DTS dropped the C_PDU because its TTL expired (C-0859, C-1050); the SIS reason 1, TTL Expired (A-0107).</summary>
    TtlExpired,

    /// <summary>The DTS refused an ARQ C_PDU with no connection to the peer (C-0731), or rejected it when the connection ended (C-0652).</summary>
    NoConnection,
}

/// <summary>D_EXPEDITED_UNIDATA_REQUEST, CAS to DTS: one C_PDU for a peer with expedited delivery (C-0025, C-0029).</summary>
/// <param name="Dst">Destination node address.</param>
/// <param name="DstIsGroup">True when <paramref name="Dst"/> is a group address (non-ARQ only).</param>
/// <param name="Arq">True for ARQ delivery (type 4), false for non-ARQ (type 8).</param>
/// <param name="CPdu">The encoded C_PDU.</param>
/// <param name="Handle">The request handle.</param>
internal sealed record DExpeditedUnidataRequest(uint Dst, bool DstIsGroup, bool Arq, ReadOnlyMemory<byte> CPdu, int Handle) : ToDts;

/// <summary>D_UNIDATA_REQUEST_REJECTED, DTS to CAS: the request named by the handle was refused (Table C-1; C-0731).</summary>
/// <param name="Handle">The request handle.</param>
/// <param name="Reason">Why.</param>
internal sealed record DUnidataRequestRejected(int Handle, DReason Reason) : ToCas;

/// <summary>D_EXPEDITED_UNIDATA_REQUEST_REJECTED, DTS to CAS (Table C-1; C-0731).</summary>
/// <param name="Handle">The request handle.</param>
/// <param name="Reason">Why.</param>
internal sealed record DExpeditedUnidataRequestRejected(int Handle, DReason Reason) : ToCas;

/// <summary>C_UNIDATA_REQUEST_REJECTED, CAS to SIS: the request named by the handle was not delivered (Table B-1, B-0018).</summary>
/// <param name="Handle">The request handle.</param>
/// <param name="Reason">Why.</param>
internal sealed record CUnidataRequestRejected(int Handle, CReason Reason) : ToSis;
