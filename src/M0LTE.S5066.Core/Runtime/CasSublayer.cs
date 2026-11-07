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
/// Breaking links (P5-1): the protocol of B.7.2.8 in both roles. The Initiator sends PHYSICAL LINK
/// BREAK (type 4) and starts <c>cas.break/&lt;peer&gt;</c> when it has been sent (B-0282, B-0283,
/// B-0400), repeats it at each expiry and declares the link broken after
/// <c>cas.break_retries</c> repeats (B-0287, B-0288, B-0424) or on PHYSICAL LINK BREAK CONFIRM (type
/// 5, B-0286). The Responder declares the link broken at once and confirms (B-0284, B-0285); a type
/// 4 from a node with no link to us is confirmed too (B-0323). A break the SIS asks for goes through
/// clean close first with an Ed4 peer (B-0278, B-0239): PHYSICAL LINK BREAK REQUEST (type 6) by ARQ
/// unless the link is already clean (B-0242, B-0312, B-0317), then type 4 once the DTS reports
/// clean close state (B-0243, B-0316, B-0421), or at <c>cas.clean_close/&lt;peer&gt;</c> (B-0322);
/// with an Ed3 peer straight to type 4 (B-0321). A type 6 received closes the link from our end
/// too, with what may still be sent set by its reason (B-0245 to B-0254, B-0319, B-0320, B-0423).
/// D_CONNECTION_LOST breaks the link locally (B-0277, B-0324), and so does a half-open link, seen as
/// WARNINGs reason 1 from the peer (B-0426). link_state: <c>closing</c> while
/// clean close is awaited, <c>breaking</c> once type 4 is under way, <c>unlinked</c> when broken.
/// </para>
/// <para>
/// Soft link closes (P5-2): C_PHYSICAL_LINK_MADE tells the SIS whether this node is the called node
/// (A-0325). The reason the SIS gave for a close is kept for the peer after the link is broken, so the
/// ARQ C_PDUs the DTS rejects when the connection ends go up with it (A-0207, A-0324). A
/// C_PHYSICAL_LINK_BREAK during a close the peer asked for joins that close (B-0450).
/// </para>
/// <para>
/// B-0089 (Address Not Known) never applies: no station list is configured (B-0404). Reject reason
/// 1 is never sent: the node is never broadcast-only (A-0007).
/// </para>
/// </remarks>
internal sealed class CasSublayer : ICasSublayer
{
    // PHYSICAL LINK REJECTED reasons (B-0148).
    private const byte RejectTooManyLinks = 4;
    private const byte RejectExclusiveLinks = 5;

    // The D_PDU type that carries our link C_PDUs (B-0129), as a WARNING names it (C-0589).
    private const int ExpeditedNonArqType = 8;

    // PHYSICAL LINK BREAK REQUEST reasons (B-0171).
    private const byte RequestNoData = 0;
    private const byte RequestHigherPriority = 1;
    private const byte RequestChannelShare = 2;
    private const byte RequestOperator = 3;

    // PHYSICAL LINK BREAK reasons we send (B-0157, B-0302, B-0309): 0 unknown, 3 higher priority, 4 no more data, 6 operator.
    private const byte BreakUnknown = 0;
    private const byte BreakHigherPriority = 3;
    private const byte BreakNoMoreData = 4;
    private const byte BreakOperator = 6;

    // The largest SIS priority (A-0063); a type 6 Priority above it is read as it (B-0311).
    private const int MaxPriority = 15;

    /// <summary>WARNINGs reason 1 on a made link, with no ARQ C_PDU from the peer between, that mean it is half open (interp B-0426).</summary>
    internal const int MaxRefusals = 2;

    private readonly CoreContext _context;
    private readonly SortedDictionary<uint, Link> _links = new();
    private readonly SortedDictionary<int, uint> _ownHandles = new();

    // ARQ data requests handed to the DTS and not yet confirmed or rejected, by handle, with their
    // peer; and, per peer, the reason of the last soft link close the SIS asked for, kept until a new
    // link to that peer is started, so the rejections the close causes carry it (A-0207).
    private readonly SortedDictionary<int, uint> _arqHandles = new();
    private readonly SortedDictionary<uint, SisBreakReason> _softLinkClosed = new();

    public CasSublayer(CoreContext context) => _context = context;

    private enum Phase
    {
        /// <summary>We sent or will send PHYSICAL LINK REQUEST and have no answer (B-0202).</summary>
        Calling,

        /// <summary>We accepted a request and our PHYSICAL LINK ACCEPTED is not yet sent (B-0211).</summary>
        Accepting,

        /// <summary>The link is made (B-0213, B-0219).</summary>
        Linked,

        /// <summary>The link is made and being brought to clean close (B-0239 to B-0254): link_state closing.</summary>
        Closing,

        /// <summary>The link is made and this node, the Initiator, has sent or will send PHYSICAL LINK BREAK (B-0282): link_state breaking.</summary>
        Breaking,
    }

    /// <summary>For tests: the link phase name of a peer, or null when there is no link and none being made.</summary>
    internal string? PhaseOf(uint peer) => _links.TryGetValue(peer, out Link? l) ? l.Phase.ToString() : null;

    /// <summary>For tests: the handle of our link C_PDU to the peer still awaiting its sent confirm.</summary>
    internal int? PendingHandleOf(uint peer) => _links.TryGetValue(peer, out Link? l) ? l.Handle : null;

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
            case TimerKind.CasCleanClose:
                StartBreak(peer, link);   // B-0322: clean close took too long, Step 1 anyway
                break;
            case TimerKind.CasBreak:
                OnBreakTimeout(peer, link);
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
            case CPhysicalLinkBreak b:
                OnLinkBreak(b);
                break;
            case DUnidataIndication d:
                if (d.Arq && _links.TryGetValue(d.Src, out Link? connected))
                {
                    connected.Refusals = 0;   // B-0426: the peer holds the connection
                }

                FirstDataIfArq(d.Src, d.Arq, d.CPdu);
                OnBreakRequestIfArq(d);
                OnDataIndication(d.Src, d.Dst, d.DstIsGroup, d.Arq, d.CPdu);
                break;
            case DPeerArqHeard h:
                if (_links.TryGetValue(h.Peer, out Link? heard) && IsMade(heard))
                {
                    heard.PeerHeard = true;   // interp B-0452
                }

                break;
            case DLinkCleanState c:
                OnCleanState(c.Peer, c.Clean);
                break;
            case DConnectionLost l:
                // B-0277, B-0324: broken locally, no C_PDU to the peer.
                if (_links.TryGetValue(l.Peer, out Link? lost) && IsMade(lost))
                {
                    Broken(l.Peer, LinkReason.ConnectionLost, confirm: false);
                }

                break;
            case DExpeditedUnidataIndication d:
                OnExpeditedIndication(d);
                break;
            case DExpeditedUnidataRequestConfirm c:
                OnSent(c.Handle);
                break;
            case DWarningReceived w:
                OnWarning(w.Src, w.ReceivedFrameType);
                OnRefusal(w.Src, w.Reason);
                break;
            case DWarningTransmitted:
                break;   // nothing in the CAS depends on WARNINGs we send
            case DUnidataRequestConfirm c:
                // The DTS delivered an ARQ C_PDU (C-0679): the request is confirmed upward, handle
                // unchanged (B-0018), unless it is our own PHYSICAL LINK BREAK REQUEST (B-0312).
                _arqHandles.Remove(c.Handle);
                if (!_ownHandles.Remove(c.Handle))
                {
                    _context.Enqueue(new CUnidataRequestConfirm(c.Handle));
                }

                break;
            case DUnidataRequestRejected j:
                // The DTS refused or dropped a C_PDU (C-0859, C-1050, C-0731, C-0652, C-0866): the
                // request fails upward with the same reason, handle unchanged (B-0018, A-0206), unless
                // it is our own PHYSICAL LINK BREAK REQUEST, which needs nothing.
                if (!_ownHandles.Remove(j.Handle))
                {
                    _context.Enqueue(new CUnidataRequestRejected(j.Handle, RejectReason(j)));
                }

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
        if (r.Arq)
        {
            _arqHandles[r.Handle] = r.Dst;
        }

        byte[] cpdu = CpduCodec.Encode(new CpduData(r.SPdu));
        _context.Enqueue(new DUnidataRequest(r.Dst, r.DstIsGroup, r.Arq, r.Priority, r.Ttd, r.DeliverInOrder,
                                             r.MinRetransmissions, cpdu, r.Handle));
    }

    /// <summary>
    /// The reason a DTS rejection goes up with (B-0018, A-0206): TTL Expired for a dropped C_PDU;
    /// for one rejected when the connection ended (C-0652, C-0866) after a soft link close the SIS
    /// asked for, the reason the SIS gave for it (A-0205, A-0207; an operator break has no SIS reject
    /// reason of its own); otherwise no connection.
    /// </summary>
    private CReason RejectReason(DUnidataRequestRejected j)
    {
        bool known = _arqHandles.Remove(j.Handle, out uint peer);
        if (j.Reason == DReason.TtlExpired)
        {
            return CReason.TtlExpired;
        }

        if (j.Reason == DReason.NoConnection && known && _softLinkClosed.TryGetValue(peer, out SisBreakReason closed))
        {
            switch (closed)
            {
                case SisBreakReason.HigherPriorityLink:
                    return CReason.SoftLinkHigherPriority;
                case SisBreakReason.ShareChannel:
                    return CReason.SoftLinkShareChannel;
                case SisBreakReason.Idle:
                    return CReason.SoftLinkIdle;
            }
        }

        return CReason.NoConnection;
    }

    /// <summary>
    /// C_PHYSICAL_LINK_MAKE (B-0196). A link already made is reported made at once (B-0224); one
    /// being made, closed or broken is left to finish (the SIS hears C_PHYSICAL_LINK_BROKEN when it ends); a new one that would exceed <c>cas.max_links</c> is refused
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
        _softLinkClosed.Remove(peer);
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
        Withdraw(_links[peer]);   // interp B-0453
        _links.Remove(peer);
        _context.Emit(new LinkState(peer, LinkStateName.Unlinked));
        _context.Enqueue(new CPhysicalLinkRejected(peer, reason));
    }

    /// <summary>
    /// A C_PDU of ours has been sent (B-0400, C-1100). A current request starts the answer timer
    /// (B-0203, B-0204). A current accept makes the link (B-0213), or, on a link already made,
    /// restarts the first-data wait (B-0401). Anything else (a reject, or a C_PDU the procedure has
    /// moved past) needs nothing. A current PHYSICAL LINK BREAK starts the break timer (B-0283, B-0400).
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
                _context.Timers.Start(RequestTimer(peer), _context.Config.LinkRequestTimeoutMs());
                break;
            case Phase.Accepting:
                Made(peer, link, called: true);
                break;
            case Phase.Linked when link.AwaitingFirstData:
                _context.Timers.Start(FirstDataTimer(peer), _context.Config.FirstDataTimeoutMs());
                break;
            case Phase.Breaking:
                _context.Timers.Start(BreakTimer(peer), _context.Config.BreakTimeoutMs());   // B-0283, B-0400
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
        link.Called = called;
        _context.Emit(new LinkState(peer, LinkStateName.Linked));
        _context.Enqueue(new DConnectionMade(peer, link.Ed4, link.ShortFamily));
        _context.Enqueue(new CPhysicalLinkMade(peer, called));
        if (called)
        {
            link.AwaitingFirstData = true;
            _context.Timers.Start(FirstDataTimer(peer), _context.Config.FirstDataTimeoutMs());
        }
    }

    /// <summary>
    /// A C_PDU the DTS delivered with expedited service. DATA is delivered as from the normal
    /// service (B-0298). Link C_PDUs are acted on only when addressed to this node's own address
    /// (B-0410); types 4 and 5 go to the break procedure (B-0159, B-0166); type 6 by expedited
    /// delivery, the ALE-only and the undefined types are discarded (B-0312, B-0305).
    /// </summary>
    /// <summary>
    /// B-0215, B-0403: a DATA C_PDU received by ARQ is the first data on the link and stops
    /// <c>cas.first_data</c>, whether it came by the regular or the expedited ARQ service (C-1216).
    /// </summary>
    private void FirstDataIfArq(uint src, bool arq, ReadOnlyMemory<byte> cpdu)
    {
        if (arq && _links.TryGetValue(src, out Link? link) && link.AwaitingFirstData && CpduCodec.Decode(cpdu.Span).Value is CpduData)
        {
            link.AwaitingFirstData = false;
            _context.Timers.Cancel(FirstDataTimer(src));
        }
    }

    private void OnExpeditedIndication(DExpeditedUnidataIndication d)
    {
        CPdu? c = CpduCodec.Decode(d.CPdu.Span).Value;
        if (c is CpduData)
        {
            FirstDataIfArq(d.Src, d.Arq, d.CPdu);
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
            case PhysicalLinkBreak:
                OnBreak(d.Src);
                break;
            case PhysicalLinkBreakConfirm:
                if (_links.TryGetValue(d.Src, out Link? breaking) && breaking.Phase == Phase.Breaking)
                {
                    Broken(d.Src, LinkReason.BreakConfirmed, confirm: false);   // B-0166, B-0286
                }

                break;   // otherwise stale: after the link is broken, or never asked for (B-0316)
        }
    }

    /// <summary>
    /// Step 2, a PHYSICAL LINK REQUEST received (B-0130). In order: LINK set is rejected with
    /// reason 5 (B-0128); on a made link a Called node that has had no ARQ D_PDU from the Caller
    /// answers it again with the values in force (B-0214, B-0314), once while an answer is unsent
    /// (B-0401), and any other breaks the link locally and is taken as a new request (interp
    /// B-0452); while accepting nothing more is
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
        if (link?.Phase == Phase.Breaking)
        {
            return;   // B-0425: the link is about to be broken; the peer asks again once it is
        }

        if (link?.Phase is Phase.Linked or Phase.Closing)
        {
            if (link.Called && !link.PeerHeard)
            {
                // B-0214, B-0314: the Caller has sent nothing on the link, so it may not have heard our
                // accept; answer it again, keeping the link. Nothing has come from the Caller's ARQ
                // machine, so ours still matches the fresh one it starts when the accept arrives (interp B-0452).
                if (link.Handle is null)
                {
                    link.Handle = SendControl(peer, new PhysicalLinkAccepted(link.ShortFamily && link.Ed4, link.Ed4, false));
                }

                return;
            }

            // Interp B-0452: a request from the node we called, or from a Caller that has used the link,
            // cannot repeat one we answered: the peer no longer holds the link and starts afresh. Ours
            // is broken locally (as B-0324, nothing sent; unfinished C_PDUs are rejected, C-0866) and the
            // request is handled as a new one, so both ends start the new link with fresh ARQ state (C-0782).
            Broken(peer, LinkReason.HalfOpen, confirm: false);
            link = null;
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
            _softLinkClosed.Remove(peer);
        }
        else
        {
            _context.Timers.Cancel(RequestTimer(peer));   // B-0405: our own call gives way
            Withdraw(link);   // interp B-0453
            link.Phase = Phase.Accepting;
            link.Crossed = true;
        }

        link.Ed4 = r.Ed4;
        link.ShortFamily = !r.Ed4 || r.Short;   // an Ed3 link uses the 8-bit family (B-0232)
        link.Handle = SendControl(peer, new PhysicalLinkAccepted(r.Ed4 && r.Short, r.Ed4, false));
    }

    /// <summary>
    /// Step 3 (a), PHYSICAL LINK ACCEPTED (B-0219). Only what we offered is used (B-0308): Ed4 when
    /// both had it, Short never since we never ask (B-0409). Its Ed4 bit records the peer's edition
    /// when we asked with Ed4 (B-0228, B-0408). While our own accept is unsent (crossed calls) it
    /// makes the link at once (B-0405), and if we had called, with the values of our own request as
    /// answered, so both ends agree whichever of the two requests each side accepted; otherwise it
    /// is discarded (B-0410).
    /// </summary>
    private void OnAccepted(uint peer, PhysicalLinkAccepted a)
    {
        if (!_links.TryGetValue(peer, out Link? link) || IsMade(link))
        {
            return;
        }

        if (link.Phase == Phase.Calling)
        {
            _context.Timers.Cancel(RequestTimer(peer));
            Withdraw(link);   // interp B-0453: a repeat of our request still queued is no longer wanted
            if (link.RequestEd4)
            {
                _context.Peers.SetEdition(peer, a.Ed4 ? KnownEdition.Ed4 : KnownEdition.Ed3);
            }
        }

        if (link.Phase == Phase.Calling || link.Crossed)
        {
            // The accept answers our request, so the link takes the values of our request as the
            // peer answered it, which are the values the peer itself uses (B-0405). In crossed calls
            // this replaces the values of the peer's request, taken when we accepted it.
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
    /// B-0411), stopping the timer and counting sends afresh. Only a WARNING about a type 8 D_PDU
    /// that arrives once our request has been sent can answer it; any other is about something
    /// else and is ignored (B-0411).
    /// </summary>
    private void OnWarning(uint peer, int receivedFrameType)
    {
        if (!_links.TryGetValue(peer, out Link? link) || link.Phase != Phase.Calling || !link.RequestEd4 || link.Handle is not null
            || receivedFrameType != ExpeditedNonArqType)
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
    /// A WARNING with reason 1 says the peer holds no connection with us (C-0578, C-0734): on a made
    /// link it is half open (interp B-0426). The second such WARNING with no ARQ C_PDU from the peer
    /// between breaks the link locally, as for D_CONNECTION_LOST (B-0324): no C_PDU to the peer. Not
    /// while the Called node still awaits the first DATA C_PDU: the Caller may still be calling, and
    /// its next request is answered again (B-0214, B-0401), while if it has given up the first-data
    /// wait aborts the link (B-0215).
    /// </summary>
    private void OnRefusal(uint peer, int reason)
    {
        if (reason != 1 || !_links.TryGetValue(peer, out Link? link) || !IsMade(link) || link.AwaitingFirstData)
        {
            return;
        }

        link.Refusals++;
        if (link.Refusals >= MaxRefusals)
        {
            Broken(peer, LinkReason.HalfOpen, confirm: false);
        }
    }

    /// <summary>
    /// The DTS refused an expedited request (C-0731). For one of our link requests the call fails
    /// (B-0022). For an accept not yet sent the link is not made: in crossed calls the call the SIS
    /// asked for fails the same way, otherwise the entry is dropped with no output (it had none),
    /// so the peer's next request is answered afresh. For a repeated accept on a made link the next
    /// request is answered again. A reject needs nothing; a client request fails upward.
    /// </summary>
    private void OnExpeditedRejected(int handle)
    {
        if (!_ownHandles.Remove(handle, out uint peer))
        {
            _context.Enqueue(new CUnidataRequestRejected(handle, CReason.NoConnection));
            return;
        }

        if (!_links.TryGetValue(peer, out Link? link) || link.Handle != handle)
        {
            return;
        }

        link.Handle = null;
        switch (link.Phase)
        {
            case Phase.Calling:
            case Phase.Accepting when link.Crossed:
                Fail(peer, LinkReason.DtsRejected);
                break;
            case Phase.Accepting:
                _links.Remove(peer);
                break;
        }
    }

    /// <summary>
    /// C_PHYSICAL_LINK_BREAK from the SIS (B-0276, B-0278), for a link made and neither closing nor
    /// breaking (anything else is left as it is). With an Ed4 peer the clean close procedure
    /// (B-0239, B-0278): this node stops starting new C_PDUs on the link (B-0241) and asks the DTS for
    /// clean close, giving it a PHYSICAL LINK BREAK REQUEST with the SIS reason (B-0302) to send by
    /// ARQ if the link is not already clean (B-0242, B-0312, B-0317); <see cref="OnCleanState"/> goes
    /// on from the answer. With an Ed3 peer, straight to Step 1 (B-0240, B-0321).
    /// </summary>
    private void OnLinkBreak(CPhysicalLinkBreak b)
    {
        if (!_links.TryGetValue(b.Peer, out Link? link))
        {
            return;
        }

        if (link.Phase == Phase.Closing && !link.CloseLocal)
        {
            JoinPeerClose(b, link);
            return;
        }

        if (link.Phase != Phase.Linked)
        {
            return;
        }

        link.SisReason = b.Reason;

        (byte requestReason, byte breakReason) = b.Reason switch
        {
            SisBreakReason.HigherPriorityLink => (RequestHigherPriority, BreakHigherPriority),
            SisBreakReason.ShareChannel => (RequestChannelShare, BreakUnknown),
            SisBreakReason.Idle => (RequestNoData, BreakNoMoreData),
            _ => (RequestOperator, BreakOperator),
        };
        link.BreakReason = breakReason;
        if (!link.Ed4)
        {
            StartBreak(b.Peer, link);   // B-0321
            return;
        }

        EnterClosing(b.Peer, link, local: true);
        byte priority = requestReason == RequestHigherPriority ? (byte)Math.Clamp(b.Priority, 0, MaxPriority) : (byte)0;   // B-0172, B-0173, B-0311
        int handle = _context.AllocateHandle();
        _ownHandles[handle] = b.Peer;
        _context.Enqueue(new DCloseRequest(b.Peer, new ClosePolicy(true, 0), CpduCodec.Encode(new PhysicalLinkBreakRequest(requestReason, priority)), handle));
    }

    /// <summary>
    /// C_PHYSICAL_LINK_BREAK while a close the peer asked for is under way (interp B-0450): the close
    /// becomes ours too. New C_PDUs stop as for any close of ours (B-0241, B-0246), merged with what the
    /// peer's reason held back; no PHYSICAL LINK BREAK REQUEST is sent, since the peer has asked for the
    /// close already (B-0242 is for the node that starts it); <c>cas.clean_close</c> runs if the link is
    /// not clean (B-0322); the PHYSICAL LINK BREAK keeps the reason of the peer's request (B-0423); and
    /// rejections the close causes carry the SIS reason (A-0207).
    /// </summary>
    private void JoinPeerClose(CPhysicalLinkBreak b, Link link)
    {
        link.CloseLocal = true;
        link.SisReason = b.Reason;
        _context.Enqueue(new DCloseRequest(b.Peer, new ClosePolicy(true, 0)));
    }

    /// <summary>
    /// A PHYSICAL LINK BREAK REQUEST (type 6) delivered by ARQ from a peer whose link is made (B-0244,
    /// B-0312); from anyone else it is discarded (B-0410). The link closes from this end too, as soon
    /// as it is clean (B-0248, B-0251, B-0254): link_state closing, and the DTS is asked for clean
    /// close with what the reason lets start (B-0423): nothing new on Channel Share, Operator Request
    /// or a reserved reason (B-0245, B-0246, B-0320), only the stated priority and above on Higher
    /// Priority (B-0252, B-0253, B-0311, B-0319), everything on No Data (B-0249, B-0250). While
    /// breaking it changes nothing.
    /// </summary>
    private void OnBreakRequestIfArq(DUnidataIndication d)
    {
        if (!d.Arq || d.DstIsGroup || CpduCodec.Decode(d.CPdu.Span).Value is not PhysicalLinkBreakRequest q
            || !_links.TryGetValue(d.Src, out Link? link) || link.Phase is not (Phase.Linked or Phase.Closing))
        {
            return;
        }

        int priority = Math.Min((int)q.Priority, MaxPriority);
        (ClosePolicy policy, byte breakReason) = q.Reason switch
        {
            RequestNoData => (ClosePolicy.None, BreakNoMoreData),
            RequestHigherPriority => (new ClosePolicy(false, priority), BreakHigherPriority),
            RequestOperator => (new ClosePolicy(true, 0), BreakOperator),
            _ => (new ClosePolicy(true, 0), BreakUnknown),
        };
        if (link.Phase == Phase.Linked)
        {
            link.BreakReason = breakReason;
            EnterClosing(d.Src, link, local: false);
        }

        _context.Enqueue(new DCloseRequest(d.Src, policy));
    }

    /// <summary>
    /// The link enters clean close (B-0239, B-0244): the first-data wait is over, since the link is
    /// in use and closing (B-0423). A close the peer asked for is shown at once; one the SIS asked for
    /// when the DTS answers that the link is not clean, since a clean link goes straight to Step 1
    /// (B-0317).
    /// </summary>
    private void EnterClosing(uint peer, Link link, bool local)
    {
        link.Phase = Phase.Closing;
        link.CloseLocal = local;
        link.AwaitingFirstData = false;
        _context.Timers.Cancel(FirstDataTimer(peer));
        if (!local)
        {
            link.ShownClosing = true;
            _context.Emit(new LinkState(peer, LinkStateName.Closing));
        }
    }

    /// <summary>
    /// The DTS's answer on clean close state (interp B-0421). Clean: Step 1 (B-0243, B-0248,
    /// B-0251, B-0254, B-0316, B-0317). Not clean, in answer to a close the SIS asked for: link_state
    /// closing, and <c>cas.clean_close/&lt;peer&gt;</c> starts (B-0322); a close the peer asked for
    /// needs no timer of ours (B-0423).
    /// </summary>
    private void OnCleanState(uint peer, bool clean)
    {
        if (!_links.TryGetValue(peer, out Link? link) || link.Phase != Phase.Closing)
        {
            return;
        }

        if (clean)
        {
            StartBreak(peer, link);
            return;
        }

        if (!link.ShownClosing)
        {
            link.ShownClosing = true;
            _context.Emit(new LinkState(peer, LinkStateName.Closing));
        }

        if (link.CloseLocal && !_context.Timers.IsRunning(CleanCloseTimer(peer)))
        {
            _context.Timers.Start(CleanCloseTimer(peer), _context.Config.CleanCloseTimeoutMs());
        }
    }

    /// <summary>
    /// Step 1 (B-0282): the Initiator sends PHYSICAL LINK BREAK with the reason of the close (B-0302,
    /// B-0423); link_state breaking. The clean close and first-data waits stop, in that order; the
    /// break timer starts when the C_PDU has been sent (<see cref="OnSent"/>).
    /// </summary>
    private void StartBreak(uint peer, Link link)
    {
        _context.Timers.Cancel(CleanCloseTimer(peer));
        _context.Timers.Cancel(FirstDataTimer(peer));
        link.AwaitingFirstData = false;
        link.Phase = Phase.Breaking;
        link.Sends = 0;
        _context.Emit(new LinkState(peer, LinkStateName.Breaking));
        SendBreak(peer, link);
    }

    private void SendBreak(uint peer, Link link)
    {
        link.Sends++;
        link.Handle = SendControl(peer, new PhysicalLinkBreak(link.BreakReason));
    }

    /// <summary>
    /// Step 3 (b) and (c): no PHYSICAL LINK BREAK CONFIRM in time. The break is sent again until it has
    /// been sent 1 + <c>cas.break_retries</c> times (B-0287, B-0424); then the link is declared broken
    /// anyway (B-0288).
    /// </summary>
    private void OnBreakTimeout(uint peer, Link link)
    {
        if (link.Phase != Phase.Breaking)
        {
            throw new InvalidOperationException("cas.break expired for a link that is not breaking");
        }

        if (link.Sends <= _context.Config.Cas.RequireBreakRetries())
        {
            SendBreak(peer, link);
            return;
        }

        Broken(peer, LinkReason.BreakNoAnswer, confirm: false);
    }

    /// <summary>
    /// Step 2, a PHYSICAL LINK BREAK received (B-0159, B-0284). On a made link, in any phase, this
    /// node is the Responder: the link is broken at once and confirmed (B-0284, B-0285), even with a
    /// break of our own under way (B-0316). From a node with no made link to us it is confirmed
    /// anyway, so that node stops repeating it, and nothing else changes (B-0323, B-0425).
    /// </summary>
    private void OnBreak(uint peer)
    {
        if (_links.TryGetValue(peer, out Link? link) && IsMade(link))
        {
            Broken(peer, LinkReason.BrokenByPeer, confirm: true);
            return;
        }

        SendControl(peer, new PhysicalLinkBreakConfirm());
    }

    /// <summary>
    /// The link is broken (B-0284 to B-0288, B-0324): the CAS timers for the peer stop, in the order
    /// first data, clean close, break; link_state unlinked; the Responder's PHYSICAL LINK BREAK CONFIRM
    /// (B-0284); then D_CONNECTION_TERMINATED to the DTS and C_PHYSICAL_LINK_BROKEN to the SIS (B-0285,
    /// B-0286, B-0288).
    /// </summary>
    private void Broken(uint peer, LinkReason reason, bool confirm)
    {
        Link link = _links[peer];
        _context.Timers.Cancel(FirstDataTimer(peer));
        _context.Timers.Cancel(CleanCloseTimer(peer));
        _context.Timers.Cancel(BreakTimer(peer));
        _links.Remove(peer);
        if (link.SisReason is SisBreakReason sisReason && reason is not (LinkReason.ConnectionLost or LinkReason.HalfOpen))
        {
            // A link lost during the close keeps its own reason (interp A-0324).
            _softLinkClosed[peer] = sisReason;   // A-0207: for the rejections D_CONNECTION_TERMINATED causes
        }

        _context.Emit(new LinkState(peer, LinkStateName.Unlinked));
        if (confirm)
        {
            SendControl(peer, new PhysicalLinkBreakConfirm());
        }

        _context.Enqueue(new DConnectionTerminated(peer));
        _context.Enqueue(new CPhysicalLinkBroken(peer, reason));
    }

    private static bool IsMade(Link link) => link.Phase is Phase.Linked or Phase.Closing or Phase.Breaking;

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

    /// <summary>
    /// Interp B-0453: our link C_PDU still waiting in the DTS (a PHYSICAL LINK REQUEST repeated just
    /// before the call ended) is withdrawn, so a request the procedure has moved past never goes on air
    /// to be read by the peer as a new call (interp B-0452).
    /// </summary>
    private void Withdraw(Link link)
    {
        if (link.Handle is int handle)
        {
            link.Handle = null;
            _ownHandles.Remove(handle);
            _context.Enqueue(new DExpeditedWithdraw(handle));
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

    private static TimerName BreakTimer(uint peer) => TimerName.ForPeer(TimerKind.CasBreak, peer);

    private static TimerName CleanCloseTimer(uint peer) => TimerName.ForPeer(TimerKind.CasCleanClose, peer);

    /// <summary>One entry of the CAS link table: a link made or being made with one peer (B-0327).</summary>
    private sealed class Link(Phase phase)
    {
        public Phase Phase { get; set; } = phase;

        /// <summary>The handle of our link C_PDU awaiting its sent confirm (B-0400), if any.</summary>
        public int? Handle { get; set; }

        /// <summary>Caller: the Ed4 bit of our request (B-0122 to B-0124).</summary>
        public bool RequestEd4 { get; set; }

        /// <summary>Caller: PHYSICAL LINK REQUEST sends in this attempt (B-0222, B-0407); Initiator: PHYSICAL LINK BREAK sends (B-0424).</summary>
        public int Sends { get; set; }

        /// <summary>The PHYSICAL LINK BREAK reason for this link's break (B-0302, B-0423).</summary>
        public byte BreakReason { get; set; }

        /// <summary>The reason our SIS gave when it asked for this link to be broken (A-0205, A-0207), if it did.</summary>
        public SisBreakReason? SisReason { get; set; }

        /// <summary>The clean close was asked for by our SIS, not by the peer (B-0322), or joined by it (B-0450).</summary>
        public bool CloseLocal { get; set; }

        /// <summary>link_state closing has been emitted for this close.</summary>
        public bool ShownClosing { get; set; }

        /// <summary>WARNINGs reason 1 from the peer since its last ARQ C_PDU (B-0426).</summary>
        public int Refusals { get; set; }

        /// <summary>The link is negotiated Ed4 (B-0138, B-0308).</summary>
        public bool Ed4 { get; set; }

        /// <summary>The 8-bit FSN family is used on the link: Short with Ed4, or an Ed3 link (B-0141, B-0142, B-0232).</summary>
        public bool ShortFamily { get; set; }

        /// <summary>Crossed calls: we were calling when the peer's request arrived, so we accepted it (B-0405).</summary>
        public bool Crossed { get; set; }

        /// <summary>The link was made as the Called node, by sending PHYSICAL LINK ACCEPTED (B-0213), not as Caller or by a crossed accept received (B-0219, B-0405).</summary>
        public bool Called { get; set; }

        /// <summary>A regular ARQ D_PDU from the peer has arrived on this link, so the peer has held it (interp B-0452).</summary>
        public bool PeerHeard { get; set; }

        /// <summary>Called node: no DATA C_PDU received yet (B-0215).</summary>
        public bool AwaitingFirstData { get; set; }
    }
}
