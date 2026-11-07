namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// The CAS (Annex B): the DATA C_PDU path in both directions (P3-1; B-0297, B-0298) and CAS-1
/// physical link establishment (P4-1): the Caller and Called procedures of B.7.2.1 with Ed4 and
/// Short negotiation and the Ed3 fallback of B.7.2.2 (core-architecture.md 4.2, section 9).
/// </summary>
/// <remarks>
/// <para>
/// Link C_PDUs (types 1 to 3) go to the DTS as expedited non-ARQ requests, type 8 D_PDUs (B-0129,
/// B-0143, B-0152, B-0190, B-0191), one link per peer address (B-0096, B-0327). A C_PDU of ours
/// counts as sent when the DTS confirms that the transmission carrying it has ended (B-0400,
/// C-1100): the Caller starts <c>cas.link_request/&lt;peer&gt;</c> then (B-0203), and the Called
/// node declares the link made then (B-0213) and starts <c>cas.first_data/&lt;peer&gt;</c> (B-0215).
/// </para>
/// <para>
/// <c>link_state</c> outputs: <c>calling</c> when the Caller procedure starts, <c>linked</c> when a
/// link is made, <c>unlinked</c> when a call fails or a link is aborted. A Called node whose accept
/// is not yet sent has no visible state. Links being made count towards <c>cas.max_links</c> (B-0402).
/// </para>
/// <para>
/// When a link is made the DTS is told before the SIS (D_CONNECTION_MADE, then C_PHYSICAL_LINK_MADE),
/// so ARQ data the SIS releases finds the connection in place (B-0217, B-0219). Timers are cancelled
/// by name, in the order the code lists them (core-architecture.md 3.3).
/// </para>
/// <para>
/// Not here yet: breaking links, clean close and D_CONNECTION_LOST (P5-1). B-0089 (Address Not
/// Known) never applies: no station list is configured (B-0404). Reject reason 1 is never sent:
/// the node is never broadcast-only (A-0007).
/// </para>
/// </remarks>
internal sealed class CasSublayer : ICasSublayer
{
    // PHYSICAL LINK REJECTED reasons (B-0148).
    private const byte RejectTooManyLinks = 4;
    private const byte RejectExclusiveLinks = 5;

    private readonly CoreContext _context;
    private readonly SortedDictionary<uint, Link> _links = new();
    private readonly SortedDictionary<int, uint> _ownHandles = new();

    public CasSublayer(CoreContext context) => _context = context;

    private enum Phase
    {
        /// <summary>We sent or will send PHYSICAL LINK REQUEST and have no answer (B-0202).</summary>
        Calling,

        /// <summary>We accepted a request and our PHYSICAL LINK ACCEPTED is not yet sent (B-0211).</summary>
        Accepting,

        /// <summary>The link is made (B-0213, B-0219).</summary>
        Linked,
    }

    /// <summary>For tests: the link phase name of a peer, or null when there is no link and none being made.</summary>
    internal string? PhaseOf(uint peer) => _links.TryGetValue(peer, out Link? l) ? l.Phase.ToString() : null;

    public void OnConfigChanged(NodeConfig oldConfig, NodeConfig newConfig)
    {
    }

    public void OnTimer(TimerName name)
    {
        uint peer = name.Peer ?? throw new InvalidOperationException($"CAS timer {name} has no peer");
        Link link = _links.TryGetValue(peer, out Link? l) ? l : throw new InvalidOperationException($"CAS timer {name} for a peer with no link");
        switch (name.Kind)
        {
            case TimerKind.CasLinkRequest:
                OnRequestTimeout(peer, link);
                break;
            case TimerKind.CasFirstData:
                // B-0215, B-0406: abort locally, no C_PDU to the peer.
                _links.Remove(peer);
                _context.Emit(new LinkState(peer, LinkStateName.Unlinked));
                _context.Enqueue(new DConnectionTerminated(peer));
                _context.Enqueue(new CPhysicalLinkBroken(peer, LinkReason.FirstDataTimeout));
                break;
            default:
                throw new InvalidOperationException($"CAS timer {name} expired, but the CAS does not start it (now {_context.Now})");
        }
    }

    public void On(ToCas primitive)
    {
        switch (primitive)
        {
            case CUnidataRequest r:
                OnUnidataRequest(r);
                break;
            case CPhysicalLinkMake m:
                OnLinkMake(m.Peer);
                break;
            case DUnidataIndication d:
                if (d.Arq && _links.TryGetValue(d.Src, out Link? link) && link.AwaitingFirstData)
                {
                    // B-0215, B-0403: a DATA C_PDU received by ARQ is the first data on the link.
                    if (CpduCodec.Decode(d.CPdu.Span).Value is CpduData)
                    {
                        link.AwaitingFirstData = false;
                        _context.Timers.Cancel(FirstDataTimer(d.Src));
                    }
                }

                OnDataIndication(d.Src, d.Dst, d.DstIsGroup, d.Arq, d.CPdu);
                break;
            case DExpeditedUnidataIndication d:
                OnExpeditedIndication(d);
                break;
            case DExpeditedUnidataRequestConfirm c:
                OnSent(c.Handle);
                break;
            case DWarningReceived w:
                OnWarning(w.Src);
                break;
            case DWarningTransmitted:
                break;   // nothing in the CAS depends on WARNINGs we send
            case DUnidataRequestRejected j:
                // The DTS refused or dropped a C_PDU (C-0859, C-1050, C-0731, C-0652): the request
                // fails upward with the same reason, handle unchanged (B-0018, A-0206).
                _context.Enqueue(new CUnidataRequestRejected(j.Handle, j.Reason == DReason.TtlExpired ? CReason.TtlExpired : CReason.NoConnection));
                break;
            case DExpeditedUnidataRequestRejected j:
                OnExpeditedRejected(j.Handle);
                break;
            default:
                throw new InvalidOperationException($"the CAS does not handle {primitive.GetType().Name} yet");
        }
    }

    /// <summary>
    /// Wraps the S_PDU in a DATA C_PDU, type 0 in the first nibble and then the S_PDU and nothing
    /// else (B-0112 to B-0114, B-0297), and passes it to the DTS's normal delivery service
    /// (B-0115) with the delivery mode the SIS gave it (B-0116). The SIS sends ARQ data only once
    /// the soft link exists (A-0188).
    /// </summary>
    private void OnUnidataRequest(CUnidataRequest r)
    {
        byte[] cpdu = CpduCodec.Encode(new CpduData(r.SPdu));
        _context.Enqueue(new DUnidataRequest(r.Dst, r.DstIsGroup, r.Arq, r.Priority, r.Ttd, r.DeliverInOrder,
                                             r.MinRetransmissions, cpdu, r.Handle));
    }

    /// <summary>
    /// C_PHYSICAL_LINK_MAKE (B-0196). A link already made is reported made at once (B-0224); one
    /// being made is left to finish; a new one that would exceed <c>cas.max_links</c> is refused
    /// locally with Maximum Physical Links In Use (B-0025, B-0402). Otherwise the Caller procedure
    /// starts (B-0202): Ed4 set unless the peer is recorded Ed3 (B-0122 to B-0124, B-0408), Short
    /// never (B-0409), LINK never (B-0127).
    /// </summary>
    private void OnLinkMake(uint peer)
    {
        if (_links.TryGetValue(peer, out Link? existing))
        {
            if (existing.Phase == Phase.Linked)
            {
                _context.Enqueue(new CPhysicalLinkMade(peer));
            }

            return;
        }

        if (_links.Count >= _context.Config.Cas.RequireMaxLinks())
        {
            _context.Enqueue(new CPhysicalLinkRejected(peer, LinkReason.MaxLinksInUse));
            return;
        }

        var link = new Link(Phase.Calling) { RequestEd4 = _context.Peers.EditionOf(peer) != KnownEdition.Ed3 };
        _links.Add(peer, link);
        _context.Emit(new LinkState(peer, LinkStateName.Calling));
        SendRequest(peer, link);
    }

    /// <summary>Step 1 (a): hands a PHYSICAL LINK REQUEST to the DTS (B-0202, B-0129); the timer waits for it to be sent (B-0400).</summary>
    private void SendRequest(uint peer, Link link)
    {
        link.Sends++;
        link.Handle = SendControl(peer, new PhysicalLinkRequest(false, link.RequestEd4, false));
    }

    /// <summary>
    /// Step 3 (c): the answer timer expired. The request is sent again until it has been sent
    /// 1 + <c>cas.link_request_retries</c> times (B-0221, B-0222, B-0407); then the link has failed
    /// (B-0223) with the no-answer reason (B-0024).
    /// </summary>
    private void OnRequestTimeout(uint peer, Link link)
    {
        if (link.Phase != Phase.Calling)
        {
            throw new InvalidOperationException($"cas.link_request expired for a peer that is not being called");
        }

        if (link.Sends <= _context.Config.Cas.RequireLinkRequestRetries())
        {
            SendRequest(peer, link);
            return;
        }

        Fail(peer, LinkReason.NoAnswer);
    }

    /// <summary>The call failed (B-0220, B-0223): link_state unlinked, then C_PHYSICAL_LINK_REJECTED to the SIS.</summary>
    private void Fail(uint peer, LinkReason reason)
    {
        _links.Remove(peer);
        _context.Emit(new LinkState(peer, LinkStateName.Unlinked));
        _context.Enqueue(new CPhysicalLinkRejected(peer, reason));
    }

    /// <summary>
    /// A C_PDU of ours has been sent (B-0400, C-1100). A current request starts the answer timer
    /// (B-0203, B-0204). A current accept makes the link (B-0213), or, on a link already made,
    /// restarts the first-data wait (B-0401). Anything else (a reject, or a C_PDU the procedure has
    /// moved past) needs nothing.
    /// </summary>
    private void OnSent(int handle)
    {
        if (!_ownHandles.Remove(handle, out uint peer) || !_links.TryGetValue(peer, out Link? link) || link.Handle != handle)
        {
            return;
        }

        link.Handle = null;
        switch (link.Phase)
        {
            case Phase.Calling:
                _context.Timers.Start(RequestTimer(peer), _context.Config.Cas.RequireLinkRequestTimeoutMs());
                break;
            case Phase.Accepting:
                Made(peer, link, called: true);
                break;
            case Phase.Linked when link.AwaitingFirstData:
                _context.Timers.Start(FirstDataTimer(peer), _context.Config.Cas.RequireFirstDataTimeoutMs());
                break;
        }
    }

    /// <summary>
    /// The link is made (B-0213, B-0219): link_state linked, D_CONNECTION_MADE to the DTS, then
    /// C_PHYSICAL_LINK_MADE to the SIS (B-0217), and for the Called node the first-data wait
    /// (B-0215, B-0216).
    /// </summary>
    private void Made(uint peer, Link link, bool called)
    {
        link.Phase = Phase.Linked;
        _context.Emit(new LinkState(peer, LinkStateName.Linked));
        _context.Enqueue(new DConnectionMade(peer, link.Ed4, link.ShortFamily));
        _context.Enqueue(new CPhysicalLinkMade(peer));
        if (called)
        {
            link.AwaitingFirstData = true;
            _context.Timers.Start(FirstDataTimer(peer), _context.Config.Cas.RequireFirstDataTimeoutMs());
        }
    }

    /// <summary>
    /// A C_PDU the DTS delivered with expedited service. DATA is delivered as from the normal
    /// service (B-0298). Link C_PDUs are acted on only when addressed to this node's own address
    /// (B-0410); types 4 to 6 are the break procedure's (P5-1) and are discarded until then, as are
    /// the ALE-only and undefined types (B-0305).
    /// </summary>
    private void OnExpeditedIndication(DExpeditedUnidataIndication d)
    {
        CPdu? c = CpduCodec.Decode(d.CPdu.Span).Value;
        if (c is CpduData)
        {
            OnDataIndication(d.Src, d.Dst, d.DstIsGroup, d.Arq, d.CPdu);
            return;
        }

        if (d.DstIsGroup || d.Dst != _context.Config.LocalAddress)
        {
            return;   // B-0410
        }

        switch (c)
        {
            case PhysicalLinkRequest r:
                OnRequest(d.Src, r);
                break;
            case PhysicalLinkAccepted a:
                OnAccepted(d.Src, a);
                break;
            case PhysicalLinkRejected j:
                OnRejected(d.Src, j);
                break;
        }
    }

    /// <summary>
    /// Step 2, a PHYSICAL LINK REQUEST received (B-0130). In order: LINK set is rejected with
    /// reason 5 (B-0128); on a made link the request is answered again with the values in force
    /// (B-0214, B-0314), once while an answer is unsent (B-0401); while accepting nothing more is
    /// needed; a call of ours to the same peer gives way to it (B-0405); otherwise reason 4 when
    /// accepting would exceed <c>cas.max_links</c> (B-0209, B-0313, B-0402), else accept (B-0210,
    /// B-0211): Ed4 echoed (B-0138, B-0139), Short when offered with Ed4 (B-0140, B-0409), LINK 0
    /// (B-0137). The request's Ed4 bit records the peer's edition, except on a made link (B-0227,
    /// B-0408, B-0314).
    /// </summary>
    private void OnRequest(uint peer, PhysicalLinkRequest r)
    {
        if (r.Link)
        {
            SendControl(peer, new PhysicalLinkRejected(RejectExclusiveLinks));
            return;
        }

        _links.TryGetValue(peer, out Link? link);
        if (link?.Phase == Phase.Linked)
        {
            if (link.Handle is null)
            {
                link.Handle = SendControl(peer, new PhysicalLinkAccepted(link.ShortFamily && link.Ed4, link.Ed4, false));
            }

            return;
        }

        if (link?.Phase == Phase.Accepting)
        {
            return;
        }

        _context.Peers.SetEdition(peer, r.Ed4 ? KnownEdition.Ed4 : KnownEdition.Ed3);
        if (link is null && _links.Count >= _context.Config.Cas.RequireMaxLinks())
        {
            SendControl(peer, new PhysicalLinkRejected(RejectTooManyLinks));
            return;
        }

        if (link is null)
        {
            link = new Link(Phase.Accepting);
            _links.Add(peer, link);
        }
        else
        {
            _context.Timers.Cancel(RequestTimer(peer));   // B-0405: our own call gives way
            link.Phase = Phase.Accepting;
        }

        link.Ed4 = r.Ed4;
        link.ShortFamily = !r.Ed4 || r.Short;   // an Ed3 link uses the 8-bit family (B-0232)
        link.Handle = SendControl(peer, new PhysicalLinkAccepted(r.Ed4 && r.Short, r.Ed4, false));
    }

    /// <summary>
    /// Step 3 (a), PHYSICAL LINK ACCEPTED (B-0219). Only what we offered is used (B-0308): Ed4 when
    /// both had it, Short never since we never ask (B-0409). Its Ed4 bit records the peer's edition
    /// when we asked with Ed4 (B-0228, B-0408). While our own accept is unsent (crossed calls) it
    /// makes the link at once (B-0405); otherwise it is discarded (B-0410).
    /// </summary>
    private void OnAccepted(uint peer, PhysicalLinkAccepted a)
    {
        if (!_links.TryGetValue(peer, out Link? link) || link.Phase == Phase.Linked)
        {
            return;
        }

        if (link.Phase == Phase.Calling)
        {
            _context.Timers.Cancel(RequestTimer(peer));
            if (link.RequestEd4)
            {
                _context.Peers.SetEdition(peer, a.Ed4 ? KnownEdition.Ed4 : KnownEdition.Ed3);
            }

            link.Ed4 = link.RequestEd4 && a.Ed4;
            link.ShortFamily = !link.Ed4;
        }

        link.Handle = null;
        Made(peer, link, called: false);
    }

    /// <summary>
    /// Step 3 (b), PHYSICAL LINK REJECTED (B-0220): the call has failed. Reason 4 is reported so
    /// the SIS holds off (B-0303); any other is a peer rejection (B-0021). Discarded when we are
    /// not calling (B-0410).
    /// </summary>
    private void OnRejected(uint peer, PhysicalLinkRejected j)
    {
        if (!_links.TryGetValue(peer, out Link? link) || link.Phase != Phase.Calling)
        {
            return;
        }

        _context.Timers.Cancel(RequestTimer(peer));
        Fail(peer, j.Reason == RejectTooManyLinks ? LinkReason.PeerTooManyLinks : LinkReason.PeerRejected);
    }

    /// <summary>
    /// B.7.2.2: a WARNING from a peer we are calling with Ed4 set means the peer is not Ed4 (B-0229,
    /// B-0315): record Ed3 (B-0408) and start link establishment again with Ed4 clear (B-0230,
    /// B-0411), stopping the timer and counting sends afresh.
    /// </summary>
    private void OnWarning(uint peer)
    {
        if (!_links.TryGetValue(peer, out Link? link) || link.Phase != Phase.Calling || !link.RequestEd4)
        {
            return;
        }

        _context.Peers.SetEdition(peer, KnownEdition.Ed3);
        _context.Timers.Cancel(RequestTimer(peer));
        link.RequestEd4 = false;
        link.Sends = 0;
        SendRequest(peer, link);
    }

    /// <summary>
    /// The DTS refused an expedited request (C-0731). For one of our link requests the call fails
    /// (B-0022); for an accept or reject there is nothing to undo; a client request fails upward.
    /// </summary>
    private void OnExpeditedRejected(int handle)
    {
        if (!_ownHandles.Remove(handle, out uint peer))
        {
            _context.Enqueue(new CUnidataRequestRejected(handle, CReason.NoConnection));
            return;
        }

        if (_links.TryGetValue(peer, out Link? link) && link.Handle == handle && link.Phase == Phase.Calling)
        {
            Fail(peer, LinkReason.DtsRejected);
        }
    }

    /// <summary>
    /// Checks a received C_PDU and, for a valid DATA C_PDU, removes the C_PCI and delivers the
    /// S_PDU to the SIS (B-0298). Valid means type 0 with at least one S_PDU octet; the low nibble
    /// of octet 0 is ignored; anything else is discarded (B-0325).
    /// </summary>
    private void OnDataIndication(uint src, uint dst, bool dstIsGroup, bool arq, ReadOnlyMemory<byte> cpdu)
    {
        CodecResult<CPdu> decoded = CpduCodec.Decode(cpdu.Span);
        if (decoded.Value is CpduData data && !data.SPdu.IsEmpty)
        {
            _context.Enqueue(new CUnidataIndication(src, dst, dstIsGroup, arq, data.SPdu));
        }
    }

    /// <summary>Hands a link C_PDU to the DTS for one type 8 D_PDU (B-0191) and returns its handle.</summary>
    private int SendControl(uint peer, CPdu c)
    {
        int handle = _context.AllocateHandle();
        _ownHandles[handle] = peer;
        _context.Enqueue(new DExpeditedUnidataRequest(peer, false, false, CpduCodec.Encode(c), handle));
        return handle;
    }

    private static TimerName RequestTimer(uint peer) => TimerName.ForPeer(TimerKind.CasLinkRequest, peer);

    private static TimerName FirstDataTimer(uint peer) => TimerName.ForPeer(TimerKind.CasFirstData, peer);

    /// <summary>One entry of the CAS link table: a link made or being made with one peer (B-0327).</summary>
    private sealed class Link(Phase phase)
    {
        public Phase Phase { get; set; } = phase;

        /// <summary>The handle of our link C_PDU awaiting its sent confirm (B-0400), if any.</summary>
        public int? Handle { get; set; }

        /// <summary>Caller: the Ed4 bit of our request (B-0122 to B-0124).</summary>
        public bool RequestEd4 { get; set; }

        /// <summary>Caller: PHYSICAL LINK REQUEST sends in this attempt (B-0222, B-0407).</summary>
        public int Sends { get; set; }

        /// <summary>The link is negotiated Ed4 (B-0138, B-0308).</summary>
        public bool Ed4 { get; set; }

        /// <summary>The 8-bit FSN family is used on the link: Short with Ed4, or an Ed3 link (B-0141, B-0142, B-0232).</summary>
        public bool ShortFamily { get; set; }

        /// <summary>Called node: no DATA C_PDU received yet (B-0215).</summary>
        public bool AwaitingFirstData { get; set; }
    }
}
