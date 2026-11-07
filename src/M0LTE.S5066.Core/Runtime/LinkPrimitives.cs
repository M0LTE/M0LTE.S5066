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

    /// <summary>The peer broke the link: PHYSICAL LINK BREAK received, this node the Responder (B-0284, B-0285).</summary>
    BrokenByPeer,

    /// <summary>This node broke the link and the peer confirmed it (B-0286).</summary>
    BreakConfirmed,

    /// <summary>This node broke the link and the peer never confirmed it (B-0288).</summary>
    BreakNoAnswer,

    /// <summary>The DTS reported the link lost with D_CONNECTION_LOST (B-0277, C-0739).</summary>
    ConnectionLost,

    /// <summary>The peer has no connection with us: the link was half open (interp B-0426).</summary>
    HalfOpen,
}

/// <summary>
/// Why the SIS asks for a physical link to be broken (C_PHYSICAL_LINK_BREAK, B-0010; the soft link
/// reasons of A-0205 and the operator break). The values are ours; B-0302 maps each to the C_PDU reasons.
/// </summary>
internal enum SisBreakReason
{
    /// <summary>Soft Link Terminated for higher priority link (A-0205).</summary>
    HigherPriorityLink,

    /// <summary>Soft Link Terminated to share channel (A-0205).</summary>
    ShareChannel,

    /// <summary>Idle Soft Link Terminated (A-0205, A-0011).</summary>
    Idle,

    /// <summary>An operator asked for the link to be broken.</summary>
    Operator,
}

/// <summary>C_PHYSICAL_LINK_BREAK, SIS to CAS: break the physical link to the peer (B-0010, B-0276, B-0278, A-0196).</summary>
/// <param name="Peer">The peer's address.</param>
/// <param name="Reason">Why (B-0302).</param>
/// <param name="Priority">For <see cref="SisBreakReason.HigherPriorityLink"/>, the SIS priority (0 to 15) of the traffic waiting (B-0172, B-0311); 0 otherwise (B-0173).</param>
internal sealed record CPhysicalLinkBreak(uint Peer, SisBreakReason Reason, int Priority = 0) : ToCas;

/// <summary>D_CONNECTION_LOST, DTS to CAS: the DTS has abandoned the ARQ connection with the peer (Table C-1; C-0739, B-0276, B-0277).</summary>
/// <param name="Peer">The peer's address.</param>
internal sealed record DConnectionLost(uint Peer) : ToCas;

/// <summary>
/// What new ARQ C_PDUs may start transmission to a peer while its link is brought to clean close
/// (B-0241, B-0245 to B-0253; interp B-0421). A C_PDU already partly sent always completes (B-0247).
/// </summary>
/// <param name="StopNew">No new C_PDU starts (B-0241, B-0246).</param>
/// <param name="MinPriority">New C_PDUs below this SIS priority do not start, those at or above it do (B-0252, B-0253, B-0319); 0 holds none back.</param>
internal sealed record ClosePolicy(bool StopNew, int MinPriority)
{
    /// <summary>Nothing held back: the peer's No Data reason is advisory (B-0249, B-0250).</summary>
    public static ClosePolicy None { get; } = new(false, 0);

    /// <summary>The stricter of two policies, for a link closed from both ends.</summary>
    public ClosePolicy Merge(ClosePolicy other) => new(StopNew || other.StopNew, Math.Max(MinPriority, other.MinPriority));

    /// <summary>Whether a C_PDU of this priority, not yet started, is held back.</summary>
    public bool Holds(int priority) => StopNew || priority < MinPriority;
}

/// <summary>
/// CAS to DTS, ours (B-0246 asks for DTS co-operation; interp B-0421): bring the link with the peer
/// to clean close state (B-0236). From now on new ARQ C_PDUs to the peer start transmission only as
/// <paramref name="Policy"/> allows. The DTS answers at once with <see cref="DLinkCleanState"/>.
/// When the link is not clean and <paramref name="BreakRequest"/> is given, that C_PDU (a PHYSICAL
/// LINK BREAK REQUEST) is queued as ARQ data exempt from the policy (B-0242, B-0312, B-0317).
/// </summary>
/// <param name="Peer">The peer's address.</param>
/// <param name="Policy">What may still start.</param>
/// <param name="BreakRequest">The encoded type 6 C_PDU to send when not clean, or null.</param>
/// <param name="Handle">The request handle of <paramref name="BreakRequest"/>.</param>
internal sealed record DCloseRequest(uint Peer, ClosePolicy Policy, ReadOnlyMemory<byte>? BreakRequest = null, int Handle = 0) : ToDts;

/// <summary>
/// DTS to CAS, ours (interp B-0421): the answer to <see cref="DCloseRequest"/>, Clean true or false,
/// and later, once, Clean true when the link with the peer reaches clean close state (B-0236, B-0237, B-0243).
/// </summary>
/// <param name="Peer">The peer's address.</param>
/// <param name="Clean">The link is in clean close state.</param>
internal sealed record DLinkCleanState(uint Peer, bool Clean) : ToCas;

/// <summary>C_PHYSICAL_LINK_MAKE, SIS to CAS: make a physical link to the peer (B-0010, B-0196, A-0193).</summary>
/// <param name="Peer">The peer's address.</param>
internal sealed record CPhysicalLinkMake(uint Peer) : ToCas;

/// <summary>C_PHYSICAL_LINK_MADE, CAS to SIS: the link is made (B-0011, B-0217, B-0219); the soft link is established (A-0194).</summary>
/// <param name="Peer">The peer's address.</param>
/// <param name="Called">This node is the called node of the link (B-0213), which the SIS needs for its minimum hold (A-0197); false for the Caller and for a link already made.</param>
internal sealed record CPhysicalLinkMade(uint Peer, bool Called = false) : ToSis;

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
