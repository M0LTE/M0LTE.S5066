namespace M0LTE.S5066.Core.Runtime;

// The data primitives of Tables B-1 and C-1 that the thin SIS and CAS (P3-1) send
// (core-architecture.md 4.1). Each carries the control information the annexes list for it,
// explicitly, never derived from the PDU it carries (B-0014, B-0017, C-0036, C-0038). Addresses are
// 28-bit values; the group flag travels beside the destination, because a non-ARQ D_PDU to a group
// says so in its own header (C-0122) and the destination value alone cannot tell. That flag is the
// one addition to the field lists of core-architecture.md 4.1. Handle is ours: one integer from
// CoreContext.AllocateHandle names the request in its confirm or reject (section 10 item 10).

/// <summary>C_UNIDATA_REQUEST, SIS to CAS: one S_PDU for a peer (B-0012, B-0297).</summary>
/// <param name="Dst">Destination node address.</param>
/// <param name="DstIsGroup">True when <paramref name="Dst"/> is a group address; non-ARQ only (S-0135).</param>
/// <param name="Arq">True for ARQ delivery, false for non-ARQ (B-0116).</param>
/// <param name="Priority">0 to 15, 15 highest (A-0063).</param>
/// <param name="Ttd">TimeToDie as an absolute core time in ms (A-0089, A-0090).</param>
/// <param name="DeliverInOrder">In-order delivery; always false for non-ARQ (A-0078, S-0150).</param>
/// <param name="MinRetransmissions">Non-ARQ retransmission count, N giving N+1 transmissions (A-0081, A-0083); 0 for ARQ.</param>
/// <param name="SPdu">The encoded S_PDU.</param>
/// <param name="Handle">The request handle.</param>
internal sealed record CUnidataRequest(uint Dst, bool DstIsGroup, bool Arq, int Priority, long Ttd, bool DeliverInOrder,
                                       int MinRetransmissions, ReadOnlyMemory<byte> SPdu, int Handle) : ToCas;

/// <summary>C_UNIDATA_INDICATION, CAS to SIS: an S_PDU received from a peer (B-0013, B-0298).</summary>
/// <param name="Src">Source node address.</param>
/// <param name="Dst">Destination node address: ours or a group we belong to (A-0119).</param>
/// <param name="DstIsGroup">True when <paramref name="Dst"/> is a group address.</param>
/// <param name="Arq">True when received by ARQ (A-0121).</param>
/// <param name="SPdu">The S_PDU, with the C_PCI removed.</param>
internal sealed record CUnidataIndication(uint Src, uint Dst, bool DstIsGroup, bool Arq, ReadOnlyMemory<byte> SPdu) : ToSis;

/// <summary>D_UNIDATA_REQUEST, CAS to DTS: one C_PDU for a peer, with normal (not expedited) delivery (B-0115, C-0025).</summary>
/// <param name="Dst">Destination node address.</param>
/// <param name="DstIsGroup">True when <paramref name="Dst"/> is a group address.</param>
/// <param name="Arq">True for ARQ delivery (B-0116).</param>
/// <param name="Priority">0 to 15.</param>
/// <param name="Ttd">TimeToDie, absolute core ms.</param>
/// <param name="DeliverInOrder">In-order delivery.</param>
/// <param name="MinRetransmissions">Non-ARQ retransmission count.</param>
/// <param name="CPdu">The encoded C_PDU.</param>
/// <param name="Handle">The request handle.</param>
internal sealed record DUnidataRequest(uint Dst, bool DstIsGroup, bool Arq, int Priority, long Ttd, bool DeliverInOrder,
                                       int MinRetransmissions, ReadOnlyMemory<byte> CPdu, int Handle) : ToDts;

/// <summary>D_UNIDATA_INDICATION, DTS to CAS: a C_PDU received from a peer (C-0026).</summary>
/// <param name="Src">Source node address.</param>
/// <param name="Dst">Destination node address.</param>
/// <param name="DstIsGroup">True when <paramref name="Dst"/> is a group address.</param>
/// <param name="Arq">True when received by ARQ.</param>
/// <param name="CPdu">The C_PDU.</param>
internal sealed record DUnidataIndication(uint Src, uint Dst, bool DstIsGroup, bool Arq, ReadOnlyMemory<byte> CPdu) : ToCas;
