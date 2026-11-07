namespace M0LTE.S5066.Core.Runtime;

// The DTS-to-CAS primitives of Table C-1 that the non-ARQ receive side and the unconnected-state
// WARNING handling send (P3-6; core-architecture.md 4.1). Like the P3-1 records they carry the group
// flag beside the destination (C-0122).

/// <summary>D_EXPEDITED_UNIDATA_INDICATION, DTS to CAS: a C_PDU received by expedited delivery (C-0026, C-0540, C-0732).</summary>
/// <param name="Src">Source node address.</param>
/// <param name="Dst">Destination node address.</param>
/// <param name="DstIsGroup">True when <paramref name="Dst"/> is a group address.</param>
/// <param name="Arq">True when received by ARQ (type 4); false for type 8.</param>
/// <param name="CPdu">The C_PDU.</param>
internal sealed record DExpeditedUnidataIndication(uint Src, uint Dst, bool DstIsGroup, bool Arq, ReadOnlyMemory<byte> CPdu) : ToCas;

/// <summary>D_WARNING_RECEIVED, DTS to CAS: a WARNING D_PDU arrived from a peer (Table C-1; C-0589, C-0609, C-0629, C-0649).</summary>
/// <param name="Src">The node that sent the WARNING.</param>
/// <param name="Dst">The destination it carried.</param>
/// <param name="ReceivedFrameType">Its RECEIVED FRAME TYPE (C-0378).</param>
/// <param name="Reason">Its REASON WARNING SENT (C-0380).</param>
internal sealed record DWarningReceived(uint Src, uint Dst, int ReceivedFrameType, int Reason) : ToCas;

/// <summary>D_WARNING_TRANSMITTED, DTS to CAS: this node is sending a WARNING to a peer (Table C-1; C-0578, C-0733).</summary>
/// <param name="Dst">The peer the WARNING is for.</param>
/// <param name="ReceivedFrameType">The type of the D_PDU that caused it.</param>
/// <param name="Reason">The reason sent.</param>
internal sealed record DWarningTransmitted(uint Dst, int ReceivedFrameType, int Reason) : ToCas;
