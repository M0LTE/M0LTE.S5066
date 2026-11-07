namespace M0LTE.S5066.Core.Runtime;

// The physical link primitives of Tables B-1 and C-1 that CAS-1 link establishment adds (P4-1;
// core-architecture.md 4.1), and the DTS confirm the CAS uses to learn that its own C_PDU has been
// sent (C-1100). The reasons are ours: Table B-1 names the primitives only (B-0018), and the
// error lists of B.2.2.1 give names without values (B-0021 to B-0025).

/// <summary>Why a link make failed or a link was broken (B-0021 to B-0025).</summary>
internal enum LinkReason
{
    /// <summary>No answer after the last PHYSICAL LINK REQUEST (B-0024, B-0223).</summary>
    NoAnswer,

    /// <summary>The peer answered PHYSICAL LINK REJECTED with a reason other than 4 (B-0021, B-0220).</summary>
    PeerRejected,

    /// <summary>The peer answered PHYSICAL LINK REJECTED with reason 4, too many active links: handled like <see cref="MaxLinksInUse"/> (B-0303).</summary>
    PeerTooManyLinks,

    /// <summary>Maximum Physical Links In Use, local (B-0025, B-0402).</summary>
    MaxLinksInUse,

    /// <summary>The DTS rejected the C_PDU carrying the request (B-0022).</summary>
    DtsRejected,

    /// <summary>The Called node aborted a link that carried no first DATA C_PDU in time (B-0215, B-0406).</summary>
    FirstDataTimeout,
}

/// <summary>C_PHYSICAL_LINK_MAKE, SIS to CAS: make a physical link to the peer (B-0010, B-0196, A-0193).</summary>
/// <param name="Peer">The peer's address.</param>
internal sealed record CPhysicalLinkMake(uint Peer) : ToCas;

/// <summary>C_PHYSICAL_LINK_MADE, CAS to SIS: the link is made (B-0011, B-0217, B-0219); the soft link is established (A-0194).</summary>
/// <param name="Peer">The peer's address.</param>
internal sealed record CPhysicalLinkMade(uint Peer) : ToSis;

/// <summary>C_PHYSICAL_LINK_REJECTED, CAS to SIS: a link make failed (B-0011, B-0220, B-0223).</summary>
/// <param name="Peer">The peer's address.</param>
/// <param name="Reason">Why.</param>
internal sealed record CPhysicalLinkRejected(uint Peer, LinkReason Reason) : ToSis;

/// <summary>C_PHYSICAL_LINK_BROKEN, CAS to SIS: a made link has ended (B-0011); the soft link is terminated (A-0199).</summary>
/// <param name="Peer">The peer's address.</param>
/// <param name="Reason">Why.</param>
internal sealed record CPhysicalLinkBroken(uint Peer, LinkReason Reason) : ToSis;

/// <summary>
/// D_CONNECTION_MADE, CAS to DTS: an ARQ connection with the peer exists (B-0217, B-0219; C-0571,
/// C-0591, C-0611, C-0631). It carries what CAS-1 negotiated: whether the peer is Ed4 and whether
/// the 8-bit FSN family is in force (B-0141, B-0142), which the DTS needs and Table C-1 does not list.
/// </summary>
/// <param name="Peer">The peer's address.</param>
/// <param name="PeerIsEd4">The link was negotiated with Ed4 set (B-0138, B-0308).</param>
/// <param name="ShortFamily">Short-FSN D_PDUs are used on the link: Short accepted with Ed4, or the peer is Ed3 (B-0141, B-0232).</param>
internal sealed record DConnectionMade(uint Peer, bool PeerIsEd4, bool ShortFamily) : ToDts;

/// <summary>D_CONNECTION_TERMINATED, CAS to DTS: the connection with the peer has ended (C-0572, C-0652, B-0406).</summary>
/// <param name="Peer">The peer's address.</param>
internal sealed record DConnectionTerminated(uint Peer) : ToDts;

/// <summary>D_EXPEDITED_UNIDATA_REQUEST_CONFIRM, DTS to CAS: the transmission carrying the last D_PDU of the request has ended (C-0040, C-1100).</summary>
/// <param name="Handle">The request handle.</param>
internal sealed record DExpeditedUnidataRequestConfirm(int Handle) : ToCas;
