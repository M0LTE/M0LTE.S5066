using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// The SIS (Annexes A and S), thin slice of P3-1: client connections, bind and unbind, the SAP
/// table, keep-alive, invalid-primitive counting, unidata request validation and the DATA S_PDU
/// with its TTD, in both directions (core-architecture.md 4.2, section 9).
/// </summary>
/// <remarks>
/// <para>
/// A connection is any number of sis_bytes on one conn. Sixteen or more are accepted; one client
/// per SAP is enforced at bind, not by refusing connections (S-0010, S-0015). Each has its own
/// stream decoder, so primitives are framed with nothing else on the stream (S-0011, S-0012).
/// A disconnect unbinds at once with no S_UNBIND_INDICATION (A-0040, S-0016). The node keeps a
/// connection open after an unbind so the client can bind again (A-0039, S-0102), and closes it
/// only after the invalid-primitive limit (S-0077); shutdown (reason 6) is the host's.
/// </para>
/// <para>
/// Soft links (P4-1): an accepted ARQ U_PDU for a peer with no soft link is held, and the SIS asks
/// the CAS for a physical link to that peer (A-0008, A-0010, A-0188, A-0192, A-0193). When the CAS
/// reports the link made, as Caller or Called node, the soft link is established and the U_PDUs held
/// for that peer go down in arrival order (A-0194, A-0200). A failed make rejects them (B-0029,
/// A-0300), except for Maximum Physical Links In Use or the peer's reason 4, where the SIS holds off
/// and asks again when a link closes or another call ends (B-0028, B-0303, B-0412). A broken link
/// ends the soft link (A-0199). Non-ARQ data needs no soft link (A-0189).
/// </para>
/// <para>
/// Soft link sessions (P5-2): the SIS closes a soft link with C_PHYSICAL_LINK_BREAK and one of the
/// three reasons of A-0205, and the CAS breaks the physical link (A-0195, A-0196). It closes one that
/// has had no ARQ U_PDU outstanding and none received for <c>sis.soft_link_idle_ms</c> (A-0011,
/// A-0013; interp A-0320); one that has run past <c>sis.soft_link_max_ms</c> while U_PDUs for another
/// node are held off for want of a link (A-0014, A-0015; interp A-0322); and one whose outstanding
/// U_PDUs are all of lower priority than those held off for another node (A-0198; interp A-0323).
/// A called node closes nothing on its own until <c>sis.soft_link_min_hold_ms</c> has passed since
/// the link was made (A-0197). A null setting is derived from the answer wait (interp A-0321). The rejections a
/// close causes come back with its reason (A-0207, interp A-0324).
/// </para>
/// <para>
/// Not here yet: delivery confirmation and the TTD check on receipt are P6-1; flow control and
/// other management messages are P6-2 (P5-3 sends the MAC-mode discovery report, interp J-0082). The node sends no unprompted keep-alive and never unbind
/// reason 2 (S-0045), never unbind reasons 1 or 4 (A-0060), and never bind reason 4, having no
/// exclusive broadcast session (A-0052).
/// </para>
/// </remarks>
internal sealed class SisSublayer : ISisSublayer
{
    /// <summary>The MTU of a general-purpose node, in bytes (A-0047).</summary>
    public const int Mtu = 2048;

    /// <summary>Invalid primitives in a row, with no valid one between, that end a connection (S-0077).</summary>
    public const int InvalidPrimitiveLimit = 10;

    /// <summary>The MSG TYPE of our MAC-mode discovery report in S_MANAGEMENT_MSG_INDICATION (S-0038; interp J-0082).</summary>
    public const byte MacModeMsgType = 1;

    // Bind reject reasons (A-0052, A-0053).
    private const byte BindRejectNotEnoughResources = 1;
    private const byte BindRejectSapAllocated = 3;

    // Unbind indication reason (A-0057, A-0058).
    private const byte UnbindTooManyInvalid = 3;

    // Unidata reject reasons (A-0107, A-0108).
    private const byte RejectOverMtu = 4;
    private const byte RejectTxModeNotSpecified = 5;
    private const byte RejectBroadcastNotAllowed = 6;
    private const byte RejectNodeNotResponding = 3;
    private const byte RejectSoftLinkHigherPriority = 9;
    private const byte RejectSoftLinkShareChannel = 10;
    private const byte RejectSoftLinkIdle = 11;
    private const byte RejectOther = 15;

    // Transmission Mode values (S-0080, S-0152).
    private const byte TxModeDefault = 0;
    private const byte TxModeArq = 1;
    private const byte TxModeNonArq = 2;

    // Delivery Confirmation values (S-0081).
    private const byte ConfirmNone = 0;
    private const byte ConfirmClient = 2;
    private const byte ConfirmUndefined = 3;

    private readonly CoreContext _context;
    private readonly SortedDictionary<int, Connection> _connections = [];
    private readonly SortedDictionary<byte, int> _sapOwner = [];
    private readonly List<Held> _awaitingSoftLink = [];
    private readonly SortedDictionary<uint, SoftLink> _softLinks = new();
    private readonly SortedDictionary<int, Held> _outstanding = new();

    public SisSublayer(CoreContext context) => _context = context;

    /// <summary>Accepted ARQ requests waiting for the soft link P4-1 will make, in arrival order.</summary>
    internal IReadOnlyList<CUnidataRequest> AwaitingSoftLink => _awaitingSoftLink.Select(a => a.Request).ToList();

    private enum SoftLinkState
    {
        /// <summary>C_PHYSICAL_LINK_MAKE sent, no answer yet (A-0193).</summary>
        Making,

        /// <summary>Held off by Maximum Physical Links In Use or peer reason 4 (B-0028, B-0303).</summary>
        HeldOff,

        /// <summary>The physical link exists: the soft link is established (A-0194).</summary>
        Established,
    }

    /// <summary>For tests: the soft link state of a peer (Closing once the SIS has asked for the break), or null when there is none.</summary>
    internal string? SoftLinkOf(uint peer) =>
        !_softLinks.TryGetValue(peer, out SoftLink? l) ? null : l.Closing ? "Closing" : l.State.ToString();

    /// <summary>For tests: the ARQ requests handed to the CAS and not yet confirmed or rejected, in handle order.</summary>
    internal IReadOnlyList<CUnidataRequest> Outstanding => _outstanding.Values.Select(h => h.Request).ToList();

    /// <summary>A changed soft link time applies from the next start of that timer; running timers keep their deadlines.</summary>
    public void OnConfigChanged(NodeConfig oldConfig, NodeConfig newConfig)
    {
    }

    public void OnTimer(TimerName name)
    {
        uint peer = name.Peer ?? throw new InvalidOperationException($"SIS timer {name} has no peer");
        SoftLink link = _softLinks.TryGetValue(peer, out SoftLink? l) && l.State == SoftLinkState.Established && !l.Closing
            ? l
            : throw new InvalidOperationException($"SIS timer {name} for a peer with no open soft link (now {_context.Now})");
        switch (name.Kind)
        {
            case TimerKind.SisSoftLinkIdle:
                OnIdle(peer, link);
                break;
            case TimerKind.SisSoftLinkMinHold:
                // A-0197: the called node may now close on its own: an idle close that waited for the
                // minimum hold goes ahead, otherwise the link may now be closed for another node.
                if (link.IdleDue)
                {
                    Close(peer, link, SisBreakReason.Idle, 0);
                }
                else
                {
                    CheckHeldOff();
                }

                break;
            case TimerKind.SisSoftLinkMax:
                link.Overdue = true;   // A-0014: closed as soon as another node waits for a link
                CheckHeldOff();
                break;
            default:
                throw new InvalidOperationException($"SIS timer {name} expired, but the SIS does not start it (now {_context.Now})");
        }
    }

    public void OnConnect(int conn)
    {
        if (_connections.ContainsKey(conn))
        {
            throw new InvalidOperationException($"conn {conn} is already connected");
        }

        _connections.Add(conn, new Connection());
    }

    /// <summary>
    /// Feeds the client's bytes to its decoder and handles every result in stream order. Bytes for
    /// a connection the node has closed are dropped: a host may deliver some after the close.
    /// </summary>
    public void OnBytes(int conn, ReadOnlyMemory<byte> bytes)
    {
        if (!_connections.TryGetValue(conn, out Connection? c))
        {
            return;
        }

        c.Decoder.Push(bytes.Span);
        while (c.Decoder.TryRead(out SisDecodeResult result))
        {
            if (!Handle(conn, c, result))
            {
                return;
            }
        }
    }

    /// <summary>The client went away: its SAP is free at once and nothing is sent (A-0040, S-0016).</summary>
    public void OnDisconnect(int conn)
    {
        if (_connections.Remove(conn, out Connection? c))
        {
            Release(c);
        }
    }

    public void On(ToSis primitive)
    {
        switch (primitive)
        {
            case CUnidataRequestConfirm c:
                // S_UNIDATA_REQUEST_CONFIRM for node delivery confirmation is P6-1's (A-0094 to
                // A-0103). Here the request is no longer outstanding on its soft link (A-0011).
                Settled(c.Handle);
                break;
            case CUnidataRequestRejected j:
                OnRejected(j);
                break;
            case CUnidataIndication ind:
                OnUnidataIndication(ind);
                break;
            case CPhysicalLinkMade m:
                OnLinkMade(m.Peer, m.Called);
                break;
            case CPhysicalLinkRejected j:
                OnLinkRejected(j.Peer, j.Reason);
                break;
            case MacModeReport r:
                OnMacModeReport(r);
                break;
            case CPhysicalLinkBroken b:
                // A-0199: the soft link ends, and its timers stop (idle, minimum hold, maximum). Anything
                // the SIS still holds for the peer waits for a new one.
                CancelTimers(b.Peer);
                _softLinks.Remove(b.Peer);
                if (_awaitingSoftLink.Any(a => a.Request.Dst == b.Peer))
                {
                    AskForLink(b.Peer);
                }

                RetryHeldOff(b.Peer);
                break;
            default:
                throw new InvalidOperationException($"the SIS does not handle {primitive.GetType().Name} yet");
        }
    }

    /// <summary>
    /// A transmission or delivery mode the node can serve (S-0098, S-0150): Transmission Mode 1
    /// (ARQ) or 2 (non-ARQ), Delivery Confirmation not 11, no confirmation with non-ARQ
    /// (A-0073, S-0092), and Extended Field 0 (S-0094, S-0146). Mode 3, non-ARQ with errors, is an
    /// option we do not offer (A-0071). Non-ARQ with in-order delivery is accepted and sent in
    /// arrival order (A-0078, S-0098, S-0150).
    /// </summary>
    private static bool IsUsable(ServiceType s) =>
        (s.TxMode == TxModeArq || s.TxMode == TxModeNonArq)
        && s.Confirm != ConfirmUndefined
        && !(s.TxMode == TxModeNonArq && s.Confirm != ConfirmNone)
        && s.Extended == 0;

    /// <summary>
    /// Handles one decoder result. Returns false when the connection has been closed. Valid and
    /// invalid follow S-0162: framing errors, deprecated and unknown types, node-to-client types and
    /// primitives that need a binding the client lacks are invalid (S-0075 to S-0077, S-0100,
    /// S-0157, S-0159, S-0160); everything else is valid, refused requests included.
    /// </summary>
    private bool Handle(int conn, Connection c, SisDecodeResult result)
    {
        bool valid = result.Primitive switch
        {
            SisBindRequest b => OnBind(conn, c, b),
            SisUnbindRequest => OnUnbind(c),
            SisKeepAlive => OnKeepAlive(conn),
            SisManagementMsgRequest => true,   // no published types yet: ignored, not invalid (S-0038)
            SisUnidataRequest u => c.Sap is { } sap && OnUnidata(conn, c, sap, u),
            _ => false,                        // a framing error, or a node-to-client type (S-0077)
        };
        if (valid)
        {
            c.InvalidCount = 0;
            return true;
        }

        c.InvalidCount++;
        if (c.InvalidCount < InvalidPrimitiveLimit)
        {
            return true;
        }

        // S-0077: S_UNBIND_INDICATION reason 3 if bound, then close. The unbind frees the SAP (A-0037).
        if (c.Sap is not null)
        {
            Send(conn, new SisUnbindIndication(UnbindTooManyInvalid));
        }

        Release(c);
        _connections.Remove(conn);
        _context.Emit(new SisClose(conn));
        return false;
    }

    /// <summary>
    /// S_BIND_REQUEST (A-0032 to A-0035, A-0041 to A-0053). Checks in the S-0161 order: a
    /// connection already bound is refused with reason 3 and keeps its binding (S-0099); a Service
    /// Type the node cannot serve with reason 1 (S-0090 to S-0094, S-0098); a SAP held by another
    /// client with reason 3 (A-0033). Reason 2 (invalid SAP) cannot arise: a SAP is a 4-bit field,
    /// so every value is a SAP (A-0001). RANK is ignored (S-0088). Flow is on once accepted, with
    /// no S_DATA_FLOW_ON (S-0027). Always a valid primitive.
    /// </summary>
    private bool OnBind(int conn, Connection c, SisBindRequest b)
    {
        if (c.Sap is not null)
        {
            Send(conn, new SisBindRejected(BindRejectSapAllocated));
        }
        else if (!IsUsable(b.ServiceType))
        {
            Send(conn, new SisBindRejected(BindRejectNotEnoughResources));
        }
        else if (_sapOwner.ContainsKey(b.Sap))
        {
            Send(conn, new SisBindRejected(BindRejectSapAllocated));
        }
        else
        {
            c.Sap = b.Sap;
            c.ServiceType = b.ServiceType;
            _sapOwner.Add(b.Sap, conn);
            Send(conn, new SisBindAccepted(b.Sap, Mtu));   // the SAP asked for, and the MTU (A-0042 to A-0044)
        }

        return true;
    }

    /// <summary>
    /// S_UNBIND_REQUEST releases the SAP for any other client (A-0037, A-0038) and sends nothing.
    /// From an unbound client it is invalid (S-0160).
    /// </summary>
    private bool OnUnbind(Connection c)
    {
        if (c.Sap is null)
        {
            return false;
        }

        Release(c);
        return true;
    }

    /// <summary>Answers S_KEEP_ALIVE at once, bound or not, however often it comes (S-0041, S-0044, S-0100).</summary>
    private bool OnKeepAlive(int conn)
    {
        Send(conn, new SisKeepAlive());
        return true;
    }

    /// <summary>
    /// S_UNIDATA_REQUEST from a bound client (A-0061 to A-0093). Checks in the S-0161 order: a
    /// U_PDU over the MTU is rejected with reason 4 whether or not confirmation was asked (A-0045,
    /// A-0106, A-0115, S-0076); an unusable non-DEFAULT DELIVERY MODE with reason 5 (S-0150); ARQ to
    /// a group address, by default or explicitly, with reason 6 (S-0135, S-0136, S-0138). DEFAULT
    /// (Transmission Mode 0) takes the bind Service Type and ignores its other subfields; any other
    /// value overrides it for this U_PDU (A-0035, A-0068, S-0086, S-0140 to S-0143). A rejection
    /// echoes the destination SAP and address and carries the whole U_PDU (A-0099, A-0109 to
    /// A-0111); a partial copy needs sis.frag_size, which P6-1 adds. Always a valid primitive.
    /// </summary>
    private bool OnUnidata(int conn, Connection c, byte srcSap, SisUnidataRequest r)
    {
        if (r.UPdu.Length > Mtu)
        {
            Reject(conn, r, RejectOverMtu);
            return true;
        }

        bool isDefault = r.DeliveryMode.TxMode == TxModeDefault;
        if (!isDefault && !IsUsable(r.DeliveryMode))
        {
            Reject(conn, r, RejectTxModeNotSpecified);
            return true;
        }

        ServiceType mode = isDefault ? c.ServiceType : r.DeliveryMode;
        bool arq = mode.TxMode == TxModeArq;
        if (arq && r.DestAddress.Group)
        {
            Reject(conn, r, RejectBroadcastNotAllowed);
            return true;
        }

        // TTL: 0 is the configured default maximum (A-0086, A-0089, A-0091); a field above 648000
        // is read as 648000 (S-0121). The TTD is fixed now and travels with the U_PDU (A-0089,
        // A-0090); every DATA S_PDU we send carries it (A-0168).
        long ttl = r.Ttl == 0 ? _context.Config.Sis.DefaultTtl : Math.Min(r.Ttl, SisConfig.MaxTtl);
        long ttd = _context.Now + (ttl * 2000);
        var spdu = new SpduData(
            r.Priority,                          // A-0148
            srcSap,                              // the sending client's SAP (A-0149)
            r.DestSap,                           // A-0152
            mode.Confirm == ConfirmClient,       // CLIENT DELIVERY CONFIRM REQUIRED (A-0153, A-0154)
            TtdCalendar.Encode(_context.Config.Time, ttd),   // A-0158 to A-0163, A-0251
            r.UPdu);
        var request = new CUnidataRequest(
            r.DestAddress.Value,                 // the source is always this node (S-0119)
            r.DestAddress.Group,
            arq,
            r.Priority,
            ttd,
            arq && mode.Order == 0,              // in-order only with ARQ (A-0077, A-0078)
            arq ? 0 : mode.MinRetx,              // valid only for non-ARQ (A-0080, A-0081, S-0095)
            SpduCodec.Encode(spdu),              // A-0143 to A-0147
            _context.AllocateHandle());
        var held = new Held(srcSap, request, r.DestSap, r.DestAddress, r.UPdu, mode.Confirm != ConfirmNone);
        if (arq && _softLinks.TryGetValue(request.Dst, out SoftLink? link) && link.State == SoftLinkState.Established && !link.Closing)
        {
            HandDown(held);                      // the soft link exists (A-0194, A-0200)
            UpdateIdle(request.Dst);
            CheckHeldOff();
        }
        else if (arq)
        {
            // A soft link first (A-0008, A-0188): held, and a physical link asked for unless one is
            // already being made or the SIS is holding off (A-0193, B-0028).
            // One the SIS is closing gets a new link once the old one is broken.
            _awaitingSoftLink.Add(held);
            if (!_softLinks.TryGetValue(request.Dst, out SoftLink? existing))
            {
                AskForLink(request.Dst);
            }
            else if (existing.State == SoftLinkState.HeldOff)
            {
                CheckHeldOff();                  // its priority may now displace another link (interp A-0323)
            }
        }
        else
        {
            _context.Enqueue(request);           // non-ARQ needs no soft link under MSPA (A-0189)
        }

        return true;
    }

    /// <summary>
    /// A DATA S_PDU from the CAS (A-0233): the U_PDU and its attributes are taken from it (A-0234)
    /// and delivered with S_UNIDATA_INDICATION to the client bound to the destination SAP
    /// (A-0116 to A-0124, A-0133, A-0236), whatever its size (A-0046); with no such client it is
    /// discarded (A-0235). The indication carries no error fields (A-0125). Addresses go out with
    /// SIZE 7 (S-0137). DATA DELIVERY CONFIRM and FAIL, client confirmation and the TTD check are
    /// P6-1; anything that does not decode is discarded.
    /// </summary>
    private void OnUnidataIndication(CUnidataIndication ind)
    {
        if (ind.Arq)
        {
            Received(ind.Src);
        }

        if (SpduCodec.Decode(ind.SPdu.Span).Value is not SpduData d || !_sapOwner.TryGetValue(d.DstSap, out int conn))
        {
            return;
        }

        Send(conn, new SisUnidataIndication(
            d.Priority,
            d.DstSap,
            new SisAddress(ind.Dst, ind.DstIsGroup),
            ind.Arq ? TxModeArq : TxModeNonArq,
            d.SrcSap,
            new SisAddress(ind.Src),
            d.UPdu));
    }

    private void AskForLink(uint peer)
    {
        _softLinks[peer] = new SoftLink(SoftLinkState.Making);
        _context.Enqueue(new CPhysicalLinkMake(peer));
    }

    /// <summary>Passes an ARQ request to the CAS and keeps it outstanding until it is confirmed or rejected.</summary>
    private void HandDown(Held h)
    {
        _outstanding.Add(h.Request.Handle, h);
        _context.Enqueue(h.Request);
    }

    /// <summary>
    /// C_PHYSICAL_LINK_MADE: the soft link is established on both sides with no SIS exchange
    /// (A-0192, A-0194), and the U_PDUs held for the peer go to the CAS in arrival order (A-0200).
    /// The session timers start in the order minimum hold (called node only, A-0197), maximum
    /// (A-0014) and idle (A-0011). A report for a soft link already established changes nothing but
    /// the release.
    /// </summary>
    private void OnLinkMade(uint peer, bool called)
    {
        if (!_softLinks.TryGetValue(peer, out SoftLink? link) || link.State != SoftLinkState.Established)
        {
            link = new SoftLink(SoftLinkState.Established) { Called = called, EstablishedAt = _context.Now };
            _softLinks[peer] = link;
            NodeConfig config = _context.Config;
            if (called && config.SoftLinkMinHoldMs() is long hold and > 0)
            {
                _context.Timers.Start(MinHoldTimer(peer), hold);
            }

            _context.Timers.Start(MaxTimer(peer), config.SoftLinkMaxMs());
        }

        List<Held> release = _awaitingSoftLink.Where(a => a.Request.Dst == peer).ToList();
        _awaitingSoftLink.RemoveAll(a => a.Request.Dst == peer);
        foreach (Held h in release)
        {
            HandDown(h);
        }

        UpdateIdle(peer);
        CheckHeldOff();
    }

    /// <summary>
    /// C_UNIDATA_REQUEST_REJECTED. One caused by a soft link close of ours carries the reason the
    /// close gave (A-0207), and the client gets S_UNIDATA_REQUEST_REJECTED with it if it asked for
    /// confirmation (A-0105, A-0107: reasons 9, 10 and 11), with the whole U_PDU (A-0109 to A-0111).
    /// Rejections for any other reason are answered by P6-1 (A-0223, A-0232), so nothing is sent for
    /// them yet.
    /// </summary>
    private void OnRejected(CUnidataRequestRejected j)
    {
        Held? h = _outstanding.TryGetValue(j.Handle, out Held? found) ? found : null;
        byte? reason = j.Reason switch
        {
            CReason.SoftLinkHigherPriority => RejectSoftLinkHigherPriority,
            CReason.SoftLinkShareChannel => RejectSoftLinkShareChannel,
            CReason.SoftLinkIdle => RejectSoftLinkIdle,
            _ => null,
        };
        if (h is not null && h.Confirm && reason is byte r && _sapOwner.TryGetValue(h.Sap, out int conn))
        {
            Send(conn, new SisUnidataRequestRejected(r, h.DestSap, h.DestAddress, h.UPdu));
        }

        Settled(j.Handle);
    }

    /// <summary>An outstanding ARQ request was confirmed or rejected: its soft link may now be idle, and its priority no longer counts.</summary>
    private void Settled(int handle)
    {
        if (_outstanding.Remove(handle, out Held? h))
        {
            UpdateIdle(h.Request.Dst);
            CheckHeldOff();
        }
    }

    /// <summary>
    /// An ARQ C_PDU received from the peer is traffic on the soft link: an idle wait that is running
    /// starts again, and an idle close waiting for the minimum hold is off (interp A-0320).
    /// </summary>
    private void Received(uint peer)
    {
        if (_softLinks.TryGetValue(peer, out SoftLink? link) && link.State == SoftLinkState.Established && !link.Closing
            && (_context.Timers.IsRunning(IdleTimer(peer)) || link.IdleDue))
        {
            link.IdleDue = false;
            StartIdle(peer, link);
        }
    }

    /// <summary>
    /// A-0011, interp A-0320: an open soft link with no ARQ request outstanding runs
    /// <c>sis.soft_link_idle/&lt;peer&gt;</c>; one with any outstanding does not. The time is derived when the
    /// setting is null (interp A-0321).
    /// </summary>
    private void UpdateIdle(uint peer)
    {
        if (!_softLinks.TryGetValue(peer, out SoftLink? link)
            || link.State != SoftLinkState.Established || link.Closing)
        {
            return;
        }

        if (_outstanding.Values.Any(h => h.Request.Dst == peer))
        {
            link.IdleDue = false;
            _context.Timers.Cancel(IdleTimer(peer));
        }
        else if (!_context.Timers.IsRunning(IdleTimer(peer)) && !link.IdleDue)
        {
            StartIdle(peer, link);
        }
    }

    /// <summary>Starts or restarts the idle wait; a zero wait (A-0013) expires at once (core-architecture.md 3, zero-duration rule).</summary>
    private void StartIdle(uint peer, SoftLink link)
    {
        long ms = _context.Config.SoftLinkIdleMs();
        if (ms == 0)
        {
            _context.Timers.Cancel(IdleTimer(peer));
            OnIdle(peer, link);
            return;
        }

        _context.Timers.Start(IdleTimer(peer), ms);
    }

    /// <summary>The idle wait is over (A-0011): close, unless a called node's minimum hold is still running, when the close waits for it (A-0197).</summary>
    private void OnIdle(uint peer, SoftLink link)
    {
        if (link.Called && _context.Timers.IsRunning(MinHoldTimer(peer)))
        {
            link.IdleDue = true;
            return;
        }

        Close(peer, link, SisBreakReason.Idle, 0);
    }

    /// <summary>
    /// Closes an open soft link (A-0196, A-0205): the session timers stop and C_PHYSICAL_LINK_BREAK
    /// goes to the CAS with the reason, and for Higher Priority the priority of the traffic waiting
    /// (B-0172). The soft link stays until the CAS reports it broken (A-0199).
    /// </summary>
    private void Close(uint peer, SoftLink link, SisBreakReason reason, int priority)
    {
        link.Closing = true;
        link.IdleDue = false;
        CancelTimers(peer);
        _context.Enqueue(new CPhysicalLinkBreak(peer, reason, priority));
    }

    private void CancelTimers(uint peer)
    {
        _context.Timers.Cancel(IdleTimer(peer));
        _context.Timers.Cancel(MinHoldTimer(peer));
        _context.Timers.Cancel(MaxTimer(peer));
    }

    /// <summary>
    /// U_PDUs held off for want of a link (B-0028) may make the SIS close another soft link (A-0014,
    /// A-0198). Each held-off peer with U_PDUs waiting needs one link closed, less the soft links the
    /// SIS is already closing; the peers are served highest waiting priority first, then by address.
    /// For each, an open soft link that may be closed (not a called node's within its minimum hold,
    /// A-0197) is chosen: first one whose outstanding requests are all of lower priority than the
    /// highest waiting, closed with Higher Priority (interp A-0323), the lowest such priority first;
    /// otherwise one past <c>sis.soft_link_max_ms</c>, closed with Share Channel (interp A-0322). Ties
    /// go to the earliest made, then the lowest address. An open link with nothing outstanding is left
    /// to its idle wait.
    /// </summary>
    private void CheckHeldOff()
    {
        var waiting = _softLinks
            .Where(kv => kv.Value.State == SoftLinkState.HeldOff)
            .Select(kv => (Peer: kv.Key, Priority: _awaitingSoftLink.Where(a => a.Request.Dst == kv.Key).Select(a => a.Request.Priority).DefaultIfEmpty(-1).Max()))
            .Where(w => w.Priority >= 0)
            .OrderByDescending(w => w.Priority).ThenBy(w => w.Peer)
            .ToList();
        int need = waiting.Count - _softLinks.Values.Count(l => l.Closing);
        foreach ((uint _, int priority) in waiting)
        {
            if (need <= 0)
            {
                return;
            }

            var open = _softLinks
                .Where(kv => kv.Value.State == SoftLinkState.Established && !kv.Value.Closing
                             && !(kv.Value.Called && _context.Timers.IsRunning(MinHoldTimer(kv.Key))))
                .Select(kv => (Peer: kv.Key, Link: kv.Value,
                               Top: _outstanding.Values.Where(h => h.Request.Dst == kv.Key).Select(h => h.Request.Priority).DefaultIfEmpty(-1).Max()))
                .ToList();
            var lower = open.Where(o => o.Top >= 0 && o.Top < priority)
                .OrderBy(o => o.Top).ThenBy(o => o.Link.EstablishedAt).ThenBy(o => o.Peer).ToList();
            if (lower.Count > 0)
            {
                Close(lower[0].Peer, lower[0].Link, SisBreakReason.HigherPriorityLink, priority);
                need--;
                continue;
            }

            var overdue = open.Where(o => o.Link.Overdue).OrderBy(o => o.Link.EstablishedAt).ThenBy(o => o.Peer).ToList();
            if (overdue.Count > 0)
            {
                Close(overdue[0].Peer, overdue[0].Link, SisBreakReason.ShareChannel, 0);
                need--;
            }
        }
    }

    /// <summary>
    /// C_PHYSICAL_LINK_REJECTED. Maximum Physical Links In Use, local or the peer's reason 4: hold
    /// off, keeping the U_PDUs (B-0028, B-0303). Any other: reject every U_PDU held for the peer
    /// (B-0029); those that asked for confirmation get S_UNIDATA_REQUEST_REJECTED, reason 3 when the
    /// call had no answer and 15 otherwise, with the whole U_PDU (A-0105, A-0107, A-0109 to A-0111,
    /// A-0300). A call that ended frees a slot, so held-off peers ask again (B-0412).
    /// </summary>
    private void OnLinkRejected(uint peer, LinkReason reason)
    {
        if (reason is LinkReason.MaxLinksInUse or LinkReason.PeerTooManyLinks)
        {
            _softLinks[peer] = new SoftLink(SoftLinkState.HeldOff);
            CheckHeldOff();
            return;
        }

        _softLinks.Remove(peer);
        List<Held> failed = _awaitingSoftLink.Where(a => a.Request.Dst == peer).ToList();
        _awaitingSoftLink.RemoveAll(a => a.Request.Dst == peer);
        foreach (Held h in failed)
        {
            if (h.Confirm && _sapOwner.TryGetValue(h.Sap, out int conn))
            {
                Send(conn, new SisUnidataRequestRejected(
                    reason == LinkReason.NoAnswer ? RejectNodeNotResponding : RejectOther, h.DestSap, h.DestAddress, h.UPdu));
            }
        }

        RetryHeldOff(peer);
    }

    /// <summary>B-0412: every held-off peer other than the one just closed asks again, in address order.</summary>
    private void RetryHeldOff(uint closed)
    {
        List<uint> heldOff = _softLinks.Where(kv => kv.Value.State == SoftLinkState.HeldOff && kv.Key != closed).Select(kv => kv.Key).ToList();
        foreach (uint peer in heldOff)
        {
            AskForLink(peer);
        }
    }

    private void Reject(int conn, SisUnidataRequest r, byte reason) =>
        Send(conn, new SisUnidataRequestRejected(reason, r.DestSap, r.DestAddress, r.UPdu));

    /// <summary>
    /// A channel analysis finding for the operator (J-0051): S_MANAGEMENT_MSG_INDICATION, sent
    /// unprompted (S-0034, S-0037), to every bound client in connection order, with our published
    /// encoding (S-0035, S-0038; interp J-0082): MSG TYPE <see cref="MacModeMsgType"/>, and a 9-byte
    /// MSG BODY: the finding code, then the node the finding is about and the addressee of the
    /// WARNING behind it, each a 4-byte address field as in every other S_primitive (Fig S-19,
    /// S-0058: SIZE 7, individual), the addressee all zero bytes (SIZE 0, no address) when none.
    /// </summary>
    private void OnMacModeReport(MacModeReport r)
    {
        byte[] body = new byte[9];
        body[0] = (byte)r.Finding;
        new SisAddress(r.Node).Write(body.AsSpan(1, 4));
        if (r.Finding is MacModeFinding.CsmaOnly or MacModeFinding.OtherMode)
        {
            new SisAddress(r.Addressee).Write(body.AsSpan(5, 4));
        }

        foreach ((int conn, Connection c) in _connections)
        {
            if (c.Sap is not null)
            {
                Send(conn, new SisManagementMsgIndication(MacModeMsgType, body));
            }
        }
    }

    private void Send(int conn, SisPrimitive primitive) =>
        _context.Emit(new SisBytesOut(conn, SisCodec.Encode(primitive)));   // one output per primitive (core-architecture.md 2.2)

    /// <summary>
    /// Ends a binding, by unbind, disconnect or the invalid-primitive limit. U_PDUs from its SAP
    /// still held in the SIS are discarded with no confirmation; C_PDUs already handed down run to
    /// completion or TTL expiry (A-0260, NOT STATED). The held ones are the ARQ requests waiting
    /// for a soft link; non-ARQ requests went to the CAS when they arrived.
    /// </summary>
    private void Release(Connection c)
    {
        if (c.Release(out byte sap))
        {
            _sapOwner.Remove(sap);
            _awaitingSoftLink.RemoveAll(a => a.Sap == sap);

            // Requests already handed down stay outstanding on their soft link, but their client is
            // gone, so a later rejection is reported to nobody, even a new client of the SAP (A-0260).
            foreach (int handle in _outstanding.Where(kv => kv.Value.Sap == sap).Select(kv => kv.Key).ToList())
            {
                _outstanding[handle] = _outstanding[handle] with { Confirm = false };
            }
        }
    }

    /// <summary>An ARQ U_PDU held for a soft link, with what a rejection to its client needs (A-0109 to A-0111).</summary>
    private sealed record Held(byte Sap, CUnidataRequest Request, byte DestSap, SisAddress DestAddress, ReadOnlyMemory<byte> UPdu, bool Confirm);

    private static TimerName IdleTimer(uint peer) => TimerName.ForPeer(TimerKind.SisSoftLinkIdle, peer);

    private static TimerName MinHoldTimer(uint peer) => TimerName.ForPeer(TimerKind.SisSoftLinkMinHold, peer);

    private static TimerName MaxTimer(uint peer) => TimerName.ForPeer(TimerKind.SisSoftLinkMax, peer);

    /// <summary>The SIS's record of a soft link with one peer, made, being made or held off.</summary>
    private sealed class SoftLink(SoftLinkState state)
    {
        public SoftLinkState State { get; } = state;

        /// <summary>This node is the called node of the link (A-0197).</summary>
        public bool Called { get; init; }

        /// <summary>When the soft link was established, for the order in which links are closed.</summary>
        public long EstablishedAt { get; init; }

        /// <summary>The SIS has asked for the link to be broken (A-0196).</summary>
        public bool Closing { get; set; }

        /// <summary>The idle wait ended during a called node's minimum hold, so the close waits for it (A-0197).</summary>
        public bool IdleDue { get; set; }

        /// <summary><c>sis.soft_link_max_ms</c> has passed since the link was made (A-0014).</summary>
        public bool Overdue { get; set; }
    }

    private sealed class Connection
    {
        public SisFrameDecoder Decoder { get; } = new();

        public byte? Sap { get; set; }

        public ServiceType ServiceType { get; set; }

        public int InvalidCount { get; set; }

        /// <summary>Clears the binding; true with the SAP it held, if it was bound.</summary>
        public bool Release(out byte sap)
        {
            sap = Sap.GetValueOrDefault();
            bool wasBound = Sap is not null;
            Sap = null;
            ServiceType = default;
            return wasBound;
        }
    }
}
