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
/// only after the invalid-primitive limit (S-0077) or at shutdown (reason 6, interp A-0059).
/// </para>
/// <para>
/// Soft links (P4-1): an accepted ARQ U_PDU for a peer with no soft link is held, and the SIS asks
/// the CAS for a physical link to that peer (A-0008, A-0010, A-0188, A-0192, A-0193). When the CAS
/// reports the link made, as Caller or Called node, the soft link is established and the U_PDUs held
/// for that peer go down in arrival order (A-0194, A-0200). A failed make rejects them (B-0029,
/// A-0300), except for Maximum Physical Links In Use or the peer's reason 4, where the SIS holds off
/// and asks again when a link closes or another call ends (B-0028, B-0303, B-0412), and after the
/// peer's reason 4 also when <c>sis.held_off/&lt;peer&gt;</c> expires (interp B-0451). A broken link
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
/// Delivery confirmation (P6-1): a U_PDU that asked for node or client delivery confirmation is
/// answered with S_UNIDATA_REQUEST_CONFIRM or S_UNIDATA_REQUEST_REJECTED (A-0094, A-0105). Node
/// confirmation follows the CAS answer (A-0222, A-0223); client confirmation needs the CAS confirm and
/// the peer's DATA DELIVERY CONFIRM, and a CAS reject or a DATA DELIVERY FAIL rejects (A-0227 to
/// A-0229; interp A-0185, A-0340, A-0343). The waits are one collapsed timer, <c>sis.confirm_wait</c>
/// (A-0221, A-0225, A-0226, A-0230; interp A-0232, A-0342). On receipt, a DATA S_PDU whose TTD has
/// passed is discarded (interp A-0167, A-0241); one that asks for client confirmation is answered
/// with DATA DELIVERY CONFIRM, or DATA DELIVERY FAIL when no client is bound (A-0169, A-0177, A-0237
/// to A-0239; interp A-0344), marked so a clean close lets it start (B-0246; interp B-0318). Every
/// copy of a U_PDU in a response is cut to <c>sis.frag_size</c> (A-0099 to A-0102, A-0112 to A-0114;
/// interp A-0341).
/// </para>
/// <para>
/// Flow control and management (P6-2): each client's accepted U_PDUs count against it until the
/// lower sublayers are finished with them; S_DATA_FLOW_OFF goes out when the count reaches
/// <see cref="FlowOffBytes"/> and S_DATA_FLOW_ON when it is back down to <see cref="FlowOnBytes"/>
/// (S-0019, S-0022, B-0279, C-0019; interp S-0163). A U_PDU sent while flow is off is discarded,
/// with S_UNIDATA_REQUEST_REJECTED reason 13 if it asked for confirmation (S-0023; interp S-0028,
/// S-0163). S_MANAGEMENT_MSG_REQUEST MSG TYPE 2 is answered with our node information (S-0030 to
/// S-0038; interp S-0164); the MAC-mode report of P5-3 is MSG TYPE 1 (interp J-0082). The node's
/// status never changes, so it never sends S_SUBNET_AVAILABILITY (S-0046; interp S-0165). Shutdown
/// sends unbind reason 6 to every bound client and closes every connection (interp A-0059).
/// Precedence of higher-priority U_PDUs across clients is the planner's (A-0064, C-0853).
/// </para>
/// <para>
/// The node sends no unprompted keep-alive and never unbind
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

    /// <summary>The MSG TYPE of our node information request and answer (S-0038; interp S-0164).</summary>
    public const byte NodeInfoMsgType = 2;

    /// <summary>
    /// A client's unfinished U_PDU bytes at which the node sends S_DATA_FLOW_OFF: eight MTUs (interp
    /// S-0163). Fixed, not a setting (core-architecture.md 7.2).
    /// </summary>
    public const int FlowOffBytes = 8 * Mtu;

    /// <summary>The unfinished bytes at or below which a client held off gets S_DATA_FLOW_ON: four MTUs (interp S-0163).</summary>
    public const int FlowOnBytes = 4 * Mtu;

    /// <summary>The most peer records in one node information answer (interp S-0164): the count is one byte.</summary>
    public const int MaxNodeInfoPeers = 255;

    // Bind reject reasons (A-0052, A-0053).
    private const byte BindRejectNotEnoughResources = 1;
    private const byte BindRejectSapAllocated = 3;

    // Unbind indication reason (A-0057, A-0058).
    private const byte UnbindTooManyInvalid = 3;
    private const byte UnbindServiceTerminating = 6;   // interp A-0059

    // Unidata reject reasons (A-0107, A-0108).
    private const byte RejectTtlExpired = 1;
    private const byte RejectSapNotBound = 2;
    private const byte RejectOverMtu = 4;
    private const byte RejectTxModeNotSpecified = 5;
    private const byte RejectBroadcastNotAllowed = 6;
    private const byte RejectNodeNotResponding = 3;
    private const byte RejectSoftLinkHigherPriority = 9;
    private const byte RejectSoftLinkShareChannel = 10;
    private const byte RejectSoftLinkIdle = 11;
    private const byte RejectDataBuffersFull = 13;   // interp S-0028
    private const byte RejectOther = 15;

    // Transmission Mode values (S-0080, S-0152).
    private const byte TxModeDefault = 0;
    private const byte TxModeArq = 1;
    private const byte TxModeNonArq = 2;

    // Delivery Confirmation values (S-0081).
    private const byte ConfirmNone = 0;
    private const byte ConfirmClient = 2;

    // DATA DELIVERY FAIL reason (A-0181).
    private const byte FailSapNotBound = 1;

    private static readonly TimerName ConfirmWaitTimer = new(TimerKind.SisConfirmWait);
    private const byte ConfirmUndefined = 3;

    private readonly CoreContext _context;
    private readonly SortedDictionary<int, Connection> _connections = [];
    private readonly SortedDictionary<byte, int> _sapOwner = [];
    private readonly List<Held> _awaitingSoftLink = [];
    private readonly SortedDictionary<uint, SoftLink> _softLinks = new();
    private readonly SortedDictionary<int, Held> _outstanding = new();

    // Flow control (P6-2; interp S-0163): every accepted client U_PDU the lower sublayers are not
    // finished with, by handle, with its connection and U_PDU size.
    private readonly SortedDictionary<int, (int Conn, int Bytes)> _unfinished = new();

    // Delivery confirmation (P6-1): the U_PDUs awaiting an answer for their client, by handle, and the
    // deadline list behind the one sis.confirm_wait timer (core-architecture.md 3.2, collapsing rule),
    // ordered by deadline then insertion sequence number.
    private readonly SortedDictionary<int, Pending> _pending = new();
    private readonly SortedSet<(long Deadline, long Seq, int Handle)> _deadlines = new();
    private long _deadlineSeq;
    private long? _armedDeadline;
    private bool _expiring;   // inside OnConfirmWait: the timer is re-armed once, after every due entry

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
        if (name.Kind == TimerKind.SisConfirmWait)
        {
            OnConfirmWait();
            return;
        }

        uint peer = name.Peer ?? throw new InvalidOperationException($"SIS timer {name} has no peer");
        if (name.Kind == TimerKind.SisHeldOff)
        {
            OnHeldOffExpired(peer);
            return;
        }

        SoftLink link = _softLinks.TryGetValue(peer, out SoftLink? l) && l.State == SoftLinkState.Established && !l.Closing
            ? l
            : throw new InvalidOperationException($"SIS timer {name} for a peer with no open soft link (now {_context.Now})");
        switch (name.Kind)
        {
            case TimerKind.SisSoftLinkIdle:
                if (HeardDuringIdle(peer, link) is long remaining)
                {
                    _context.Timers.Start(IdleTimer(peer), remaining);
                    break;
                }

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
            Release(conn, c);
        }
    }

    public void On(ToSis primitive)
    {
        switch (primitive)
        {
            case CUnidataRequestConfirm c:
                OnCasConfirm(c.Handle);
                Settled(c.Handle);   // no longer outstanding on its soft link (A-0011)
                Finished(c.Handle);
                break;
            case CUnidataRequestRejected j:
                OnRejected(j);
                Finished(j.Handle);
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
            SisUnbindRequest => OnUnbind(conn, c),
            SisKeepAlive => OnKeepAlive(conn),
            SisManagementMsgRequest m => OnManagement(conn, m),
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

        Release(conn, c);
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
    private bool OnUnbind(int conn, Connection c)
    {
        if (c.Sap is null)
        {
            return false;
        }

        Release(conn, c);
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
    /// echoes the destination SAP and address and carries the U_PDU cut to <c>sis.frag_size</c>
    /// (A-0109 to A-0113; interp A-0341). An accepted U_PDU that asks for node or client
    /// confirmation is recorded for its answer (A-0094, A-0105). Always a valid primitive.
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

        if (c.FlowOff)
        {
            // S-0023, interp S-0028 and S-0163: sent after S_DATA_FLOW_OFF, so discarded; one that
            // asked for confirmation is told so with reason 13. The checks above come first.
            if (mode.Confirm != ConfirmNone)
            {
                Reject(conn, r, RejectDataBuffersFull);
            }

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
        var held = new Held(srcSap, request, r.DestSap, r.DestAddress, r.UPdu);
        if (mode.Confirm != ConfirmNone)
        {
            // Only ARQ gets here: confirmation with non-ARQ is refused above (A-0073, S-0092).
            _pending.Add(request.Handle, new Pending(held, mode.Confirm == ConfirmClient));
        }

        _unfinished.Add(request.Handle, (conn, r.UPdu.Length));
        c.Unfinished += r.UPdu.Length;
        Submit(held);
        if (!c.FlowOff && c.Unfinished >= FlowOffBytes)
        {
            c.FlowOff = true;   // interp S-0163
            Send(conn, new SisDataFlowOff());
        }

        return true;
    }

    /// <summary>
    /// The lower sublayers are finished with a client U_PDU: confirmed or rejected by the CAS, or
    /// dropped by the SIS while held for a soft link. Its bytes no longer count against its client,
    /// and a client held off whose count is now at or below <see cref="FlowOnBytes"/> gets
    /// S_DATA_FLOW_ON (S-0019, S-0021; interp S-0163). A handle that is not a client U_PDU, or whose
    /// client has gone, is ignored.
    /// </summary>
    private void Finished(int handle)
    {
        if (!_unfinished.Remove(handle, out (int Conn, int Bytes) u) || !_connections.TryGetValue(u.Conn, out Connection? c))
        {
            return;
        }

        c.Unfinished -= u.Bytes;
        if (c.FlowOff && c.Unfinished <= FlowOnBytes)
        {
            c.FlowOff = false;
            Send(u.Conn, new SisDataFlowOn());
        }
    }

    /// <summary>
    /// S_MANAGEMENT_MSG_REQUEST, valid bound or not (S-0032, S-0038). MSG TYPE 2 is answered at once,
    /// to the asking connection only, with S_MANAGEMENT_MSG_INDICATION MSG TYPE 2 (S-0034, S-0037;
    /// interp S-0164); its MSG BODY is ignored. Any other MSG TYPE is ignored, with no reply.
    /// </summary>
    private bool OnManagement(int conn, SisManagementMsgRequest m)
    {
        if (m.MsgType == NodeInfoMsgType)
        {
            Send(conn, new SisManagementMsgIndication(NodeInfoMsgType, NodeInfoBody()));
        }

        return true;
    }

    /// <summary>
    /// Our published node information (docs/management-messages.md; interp S-0164): this node's
    /// address field (Fig S-19, SIZE 7), the transmit waveform number and interleaver, the peer count,
    /// then one 13-byte record per peer in address order, at most <see cref="MaxNodeInfoPeers"/>. The
    /// peers are those the receiver has a quality record for (C-0833) and those the SIS has a soft
    /// link record for. A record: the address field; the soft link state (0 none, 1 being made or held
    /// off, 2 established, 3 closing); the transmit waveform and interleaver to it; the waveform and
    /// interleaver of the last burst heard from it (0xFF when never heard); that burst's SNR in tenths
    /// of a dB, signed 16-bit big-endian (0x8000 when not given or never heard); and the whole seconds
    /// since it, big-endian, saturating at 65534 (0xFFFF when never heard).
    /// </summary>
    private byte[] NodeInfoBody()
    {
        Speed speed = _context.Config.Drc.DefaultSpeed;   // the transmit speed to every peer until phase 7
        var peers = new SortedSet<uint>(_context.RxQuality.Keys);
        peers.UnionWith(_softLinks.Keys);
        List<uint> listed = peers.Take(MaxNodeInfoPeers).ToList();
        byte[] body = new byte[7 + (13 * listed.Count)];
        new SisAddress(_context.Config.LocalAddress).Write(body.AsSpan(0, 4));
        body[4] = (byte)speed.Wn;
        body[5] = (byte)speed.Interleaver;
        body[6] = (byte)listed.Count;
        int at = 7;
        foreach (uint peer in listed)
        {
            new SisAddress(peer).Write(body.AsSpan(at, 4));
            body[at + 4] = !_softLinks.TryGetValue(peer, out SoftLink? link) ? (byte)0
                : link.State != SoftLinkState.Established ? (byte)1
                : link.Closing ? (byte)3 : (byte)2;
            body[at + 5] = (byte)speed.Wn;
            body[at + 6] = (byte)speed.Interleaver;
            int snr = 0x8000;
            int age = 0xFFFF;
            if (_context.RxQuality.TryGetValue(peer, out RxQuality? q))
            {
                body[at + 7] = (byte)q.Wn;
                body[at + 8] = (byte)q.Interleaver;
                if (q.SnrTenthDb is int tenths)
                {
                    snr = Math.Clamp(tenths, -32767, 32767) & 0xFFFF;
                }

                age = (int)Math.Min((_context.Now - q.LastHeard) / 1000, 65534);
            }
            else
            {
                body[at + 7] = 0xFF;
                body[at + 8] = 0xFF;
            }

            body[at + 9] = (byte)(snr >> 8);
            body[at + 10] = (byte)snr;
            body[at + 11] = (byte)(age >> 8);
            body[at + 12] = (byte)age;
            at += 13;
        }

        return body;
    }

    /// <summary>
    /// Interp A-0059: the SIS service is terminating. In connection order, each bound client is sent
    /// S_UNBIND_INDICATION reason 6 and each connection, bound or not, is closed. Nothing held for a
    /// client is answered (A-0260); the host destroys the core next.
    /// </summary>
    public void OnShutdown()
    {
        foreach ((int conn, Connection c) in _connections.ToList())
        {
            if (c.Sap is not null)
            {
                Send(conn, new SisUnbindIndication(UnbindServiceTerminating));
            }

            Release(conn, c);
            _connections.Remove(conn);
            _context.Emit(new SisClose(conn));
        }
    }

    /// <summary>
    /// Sends an accepted request on: ARQ down an established soft link (A-0194, A-0200), or held for
    /// one (A-0008, A-0188), with a U_PDU that asked for confirmation waiting at most until its TTD
    /// (interp A-0232, A-0342); non-ARQ straight to the CAS (A-0189). A client confirmation goes down
    /// a soft link the SIS is closing too, since the close lets it start (B-0246; interp B-0318).
    /// </summary>
    private void Submit(Held held)
    {
        CUnidataRequest request = held.Request;
        bool arq = request.Arq;
        if (arq && _softLinks.TryGetValue(request.Dst, out SoftLink? link) && link.State == SoftLinkState.Established
            && (!link.Closing || request.ClientConfirmation))
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
            if (_pending.TryGetValue(request.Handle, out Pending? p))
            {
                SetDeadline(p, request.Ttd);     // reason 1 if the TTD passes first (interp A-0232)
            }

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
    }

    /// <summary>
    /// An S_PDU from the CAS (A-0233). A DATA S_PDU's U_PDU and attributes are taken from it (A-0234):
    /// one whose valid TTD has passed is discarded with no answer (interp A-0167, A-0241); otherwise
    /// it is delivered with S_UNIDATA_INDICATION to the client bound to the destination SAP
    /// (A-0116 to A-0124, A-0133, A-0236), whatever its size (A-0046), or discarded when there is none
    /// (A-0235). The indication carries no error fields (A-0125); addresses go out with SIZE 7
    /// (S-0137). When it asks for client confirmation the sender gets DATA DELIVERY CONFIRM, or DATA
    /// DELIVERY FAIL reason 1 when no client was bound (A-0237 to A-0239). A DATA DELIVERY CONFIRM or
    /// FAIL answers a U_PDU of ours (A-0176, A-0184). Anything that does not decode is discarded.
    /// </summary>
    private void OnUnidataIndication(CUnidataIndication ind)
    {
        if (ind.Arq)
        {
            Received(ind.Src);
        }

        switch (SpduCodec.Decode(ind.SPdu.Span).Value)
        {
            case SpduData d:
                OnData(ind, d);
                break;
            case SpduDeliveryConfirm c:
                OnPeerAnswer(ind.Src, c.SrcSap, c.DstSap, c.UPdu, null);
                break;
            case SpduDeliveryFail f:
                OnPeerAnswer(ind.Src, f.SrcSap, f.DstSap, f.UPdu, f.Reason);
                break;
        }
    }

    private void OnData(CUnidataIndication ind, SpduData d)
    {
        if (d.Ttd is SpduTtd ttd && TtdCalendar.HasPassed(_context.Config.Time, ttd, _context.Now))
        {
            return;   // A-0241: not delivered, and no DATA DELIVERY FAIL
        }

        bool bound = _sapOwner.TryGetValue(d.DstSap, out int conn);
        if (bound)
        {
            Send(conn, new SisUnidataIndication(
                d.Priority,
                d.DstSap,
                new SisAddress(ind.Dst, ind.DstIsGroup),
                ind.Arq ? TxModeArq : TxModeNonArq,
                d.SrcSap,
                new SisAddress(ind.Src),
                d.UPdu));
        }

        if (d.ClientConfirm)
        {
            SendClientConfirmation(ind, d, bound);
        }
    }

    /// <summary>
    /// DATA DELIVERY CONFIRM (A-0169 to A-0175) or DATA DELIVERY FAIL reason 1 (A-0177 to A-0183) to
    /// the peer SIS that sent the DATA S_PDU, with its priority and its two SAP IDs unswapped and a
    /// copy of the U_PDU cut to <c>sis.frag_size</c>. Interp A-0344: it goes as the DATA S_PDU came,
    /// ARQ over the soft link or non-ARQ with no repeats, to the sender's own address, with the TTD
    /// of a U_PDU given TTL 0 and no in-order delivery, marked as a client confirmation so a clean
    /// close lets it start (B-0246; interp B-0318). Its own rejection is told to nobody.
    /// </summary>
    private void SendClientConfirmation(CUnidataIndication ind, SpduData d, bool delivered)
    {
        ReadOnlyMemory<byte> copy = Copy(d.UPdu);
        SPdu answer = delivered
            ? new SpduDeliveryConfirm(d.Priority, d.SrcSap, d.DstSap, copy)
            : new SpduDeliveryFail(FailSapNotBound, d.SrcSap, d.DstSap, copy);
        var request = new CUnidataRequest(
            ind.Src,
            false,
            ind.Arq,
            d.Priority,
            _context.Now + (_context.Config.Sis.DefaultTtl * 2000L),
            false,
            0,
            SpduCodec.Encode(answer),
            _context.AllocateHandle(),
            ClientConfirmation: true);
        Submit(new Held(null, request, d.SrcSap, new SisAddress(ind.Src), d.UPdu));
    }

    /// <summary>
    /// A DATA DELIVERY CONFIRM or FAIL from <paramref name="src"/>. Interp A-0343: it answers the
    /// oldest U_PDU handed down to that node that asked for client confirmation and has no peer answer
    /// yet, with the same source and destination SAP IDs, whose start the returned copy is (A-0103).
    /// A confirm completes the client confirmation once the CAS has confirmed too (A-0227); a fail
    /// rejects it with the reason mapped by interp A-0185 (A-0228, A-0229). One that answers nothing is
    /// discarded.
    /// </summary>
    private void OnPeerAnswer(uint src, byte srcSap, byte dstSap, ReadOnlyMemory<byte> copy, byte? failReason)
    {
        Pending? match = _pending.Values.FirstOrDefault(p =>
            p.Client && p.HandedDown && !p.PeerConfirmed
            && p.Held.Request.Dst == src && p.Held.Sap == srcSap && p.Held.DestSap == dstSap
            && copy.Length <= p.Held.UPdu.Length && p.Held.UPdu.Span[..copy.Length].SequenceEqual(copy.Span));
        if (match is null)
        {
            return;
        }

        if (failReason is byte reason)
        {
            Answer(match, reason == FailSapNotBound ? RejectSapNotBound : RejectOther);
        }
        else if (match.CasConfirmed)
        {
            Answer(match, null);
        }
        else
        {
            match.PeerConfirmed = true;
        }
    }

    /// <summary>C_UNIDATA_REQUEST_CONFIRM: node confirmation is complete (A-0222); client confirmation once the peer's DATA DELIVERY CONFIRM is in too (A-0227).</summary>
    private void OnCasConfirm(int handle)
    {
        if (!_pending.TryGetValue(handle, out Pending? p))
        {
            return;
        }

        if (!p.Client || p.PeerConfirmed)
        {
            Answer(p, null);
        }
        else
        {
            p.CasConfirmed = true;
        }
    }

    /// <summary>
    /// The wait of every U_PDU whose deadline has come is over (A-0225, A-0230; interp A-0232,
    /// A-0342), in list order: one still held for a soft link reached its TTD and is dropped with
    /// reason 1, one handed down had no answer in time and gets reason 3. A later answer is ignored.
    /// </summary>
    private void OnConfirmWait()
    {
        // Entries due together are all handled before the timer is re-armed: re-arming between them
        // would start it with 0 ms for the next one (core-architecture.md 3.2, 3.3).
        _armedDeadline = null;
        _expiring = true;
        while (_deadlines.Count > 0 && _deadlines.Min.Deadline <= _context.Now)
        {
            Expire(_pending[_deadlines.Min.Handle]);
        }

        _expiring = false;
        ArmConfirmWait();
    }

    private void Expire(Pending p)
    {
        if (p.HandedDown)
        {
            Answer(p, RejectNodeNotResponding);
        }
        else
        {
            _awaitingSoftLink.Remove(p.Held);
            Answer(p, RejectTtlExpired);
            Finished(p.Held.Request.Handle);
        }
    }

    /// <summary>
    /// Ends the confirmation of a U_PDU: S_UNIDATA_REQUEST_CONFIRM when <paramref name="reason"/> is
    /// null, otherwise S_UNIDATA_REQUEST_REJECTED with it, each with the request's destination SAP and
    /// address and the U_PDU cut to <c>sis.frag_size</c> (A-0096 to A-0101, A-0109 to A-0113).
    /// </summary>
    private void Answer(Pending p, byte? reason)
    {
        Held h = p.Held;
        if (h.Sap is byte sap && _sapOwner.TryGetValue(sap, out int conn))
        {
            Send(conn, reason is byte r
                ? new SisUnidataRequestRejected(r, h.DestSap, h.DestAddress, Copy(h.UPdu))
                : new SisUnidataRequestConfirm(h.DestSap, h.DestAddress, Copy(h.UPdu)));
        }

        Forget(p);   // the answer first, then the timer change it causes
    }

    /// <summary>Drops a confirmation record and its deadline, re-arming the timer.</summary>
    private void Forget(Pending p)
    {
        _pending.Remove(p.Held.Request.Handle);
        if (p.Entry is { } e)
        {
            _deadlines.Remove(e);
            p.Entry = null;
        }

        ArmConfirmWait();
    }

    /// <summary>Gives a confirmation record a new deadline; one already due is handled at once (core-architecture.md 3.2).</summary>
    private void SetDeadline(Pending p, long deadline)
    {
        if (p.Entry is { } e)
        {
            if (e.Deadline == deadline)
            {
                return;
            }

            _deadlines.Remove(e);
            p.Entry = null;
        }

        if (deadline <= _context.Now)
        {
            Expire(p);   // never put in the list (zero-duration rule)
            return;
        }

        p.Entry = (deadline, _deadlineSeq++, p.Held.Request.Handle);
        _deadlines.Add(p.Entry.Value);
        ArmConfirmWait();
    }

    /// <summary>Runs <c>sis.confirm_wait</c> to the head deadline: started or restarted only when the head changes, cancelled when the list empties.</summary>
    private void ArmConfirmWait()
    {
        if (_expiring)
        {
            return;
        }

        if (_deadlines.Count == 0)
        {
            if (_armedDeadline is not null)
            {
                _context.Timers.Cancel(ConfirmWaitTimer);
                _armedDeadline = null;
            }

            return;
        }

        long head = _deadlines.Min.Deadline;
        if (_armedDeadline != head)
        {
            _context.Timers.Start(ConfirmWaitTimer, head - _context.Now);
            _armedDeadline = head;
        }
    }

    /// <summary>A copy of the U_PDU for a response: its first <c>sis.frag_size</c> bytes, or all of it when the setting is null or the U_PDU no longer (interp A-0341, issue #30).</summary>
    private ReadOnlyMemory<byte> Copy(ReadOnlyMemory<byte> uPdu) =>
        _context.Config.Sis.FragSize is int f && f < uPdu.Length ? uPdu[..f] : uPdu;

    private void AskForLink(uint peer)
    {
        StopHeldOff(peer);
        _softLinks[peer] = new SoftLink(SoftLinkState.Making);
        _context.Enqueue(new CPhysicalLinkMake(peer));
    }

    /// <summary>
    /// Passes an ARQ request to the CAS and keeps it outstanding until it is confirmed or rejected.
    /// One that asked for confirmation now waits <c>sis.confirm_wait_ms</c>, or until its TTD when
    /// that is null (A-0221, A-0226; interp A-0232, A-0342).
    /// </summary>
    private void HandDown(Held h)
    {
        _outstanding.Add(h.Request.Handle, h);
        if (_pending.TryGetValue(h.Request.Handle, out Pending? p))
        {
            p.HandedDown = true;
            SetDeadline(p, _context.Config.Sis.ConfirmWaitMs is int wait ? _context.Now + wait : h.Request.Ttd);
        }

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
        StopHeldOff(peer);   // the peer called us while we held off (interp B-0451)
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
    /// C_UNIDATA_REQUEST_REJECTED: a U_PDU that asked for node or client confirmation is rejected to
    /// its client (A-0223, A-0228), with the reason derived from the CAS one (A-0229; interp A-0340):
    /// TTL Expired 1; for a rejection caused by a soft link close of ours, the reason the close gave,
    /// 9, 10 or 11 (A-0207; interp A-0324); with no connection to the peer, 3.
    /// </summary>
    private void OnRejected(CUnidataRequestRejected j)
    {
        if (_pending.TryGetValue(j.Handle, out Pending? p))
        {
            Answer(p, j.Reason switch
            {
                CReason.TtlExpired => RejectTtlExpired,
                CReason.SoftLinkHigherPriority => RejectSoftLinkHigherPriority,
                CReason.SoftLinkShareChannel => RejectSoftLinkShareChannel,
                CReason.SoftLinkIdle => RejectSoftLinkIdle,
                _ => RejectNodeNotResponding,
            });
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

        link.IdleSince = _context.Now;
        _context.Timers.Start(IdleTimer(peer), ms);
    }

    /// <summary>
    /// The idle wait ran out, but the peer was heard during it: a valid D_PDU header from it (C-0833)
    /// in a burst that began after the wait did. The link is not idle, since the peer may be part way
    /// through a C_PDU whose segments have not all arrived. Returns the time left until one idle wait
    /// after the end of the burst it was last heard in, from which the wait now counts; null when it
    /// was not heard. A burst begun before the wait, such as the one whose C_PDU started it, does not
    /// count (interp A-0320; issue #86).
    /// </summary>
    private long? HeardDuringIdle(uint peer, SoftLink link)
    {
        if (!_context.RxQuality.TryGetValue(peer, out RxQuality? q) || q.LastBurstStart < link.IdleSince)
        {
            return null;
        }

        // A run-time change may have shortened the wait to less than the silence already passed.
        long remaining = q.LastHeard + _context.Config.SoftLinkIdleMs() - _context.Now;
        link.IdleSince = q.LastHeard;
        return remaining > 0 ? remaining : null;
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
            if (reason == LinkReason.PeerTooManyLinks)
            {
                // Interp B-0451: the peer's links are not visible here, so ask again after the
                // longest a peer keeps a soft link while others wait (A-0014), as well as on B-0412.
                _context.Timers.Start(HeldOffTimer(peer), _context.Config.SoftLinkMaxMs());
            }

            CheckHeldOff();
            return;
        }

        _softLinks.Remove(peer);
        List<Held> failed = _awaitingSoftLink.Where(a => a.Request.Dst == peer).ToList();
        _awaitingSoftLink.RemoveAll(a => a.Request.Dst == peer);
        foreach (Held h in failed)
        {
            if (_pending.TryGetValue(h.Request.Handle, out Pending? p))
            {
                Answer(p, reason == LinkReason.NoAnswer ? RejectNodeNotResponding : RejectOther);
            }

            Finished(h.Request.Handle);
        }

        RetryHeldOff(peer);
    }

    /// <summary>
    /// <c>sis.held_off/&lt;peer&gt;</c> expired (interp B-0451): a peer still held off by its own reason 4
    /// is asked again if U_PDUs still wait for it; with none left the hold is dropped.
    /// </summary>
    private void OnHeldOffExpired(uint peer)
    {
        if (!_softLinks.TryGetValue(peer, out SoftLink? link) || link.State != SoftLinkState.HeldOff)
        {
            throw new InvalidOperationException($"sis.held_off expired for a peer not held off (now {_context.Now})");
        }

        if (_awaitingSoftLink.Any(a => a.Request.Dst == peer))
        {
            AskForLink(peer);
        }
        else
        {
            _softLinks.Remove(peer);
        }
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
        Send(conn, new SisUnidataRequestRejected(reason, r.DestSap, r.DestAddress, Copy(r.UPdu)));

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
    private void Release(int conn, Connection c)
    {
        // Its flow control count goes with the binding: a new binding starts with flow on and nothing
        // counted (interp S-0027, S-0163).
        foreach (int handle in _unfinished.Where(kv => kv.Value.Conn == conn).Select(kv => kv.Key).ToList())
        {
            _unfinished.Remove(handle);
        }

        if (c.Release(out byte sap))
        {
            _sapOwner.Remove(sap);
            _awaitingSoftLink.RemoveAll(a => a.Sap == sap);

            // Requests already handed down stay outstanding on their soft link, but their client is
            // gone, so a later answer is reported to nobody, even a new client of the SAP (A-0260).
            foreach (Pending p in _pending.Values.Where(p => p.Held.Sap == sap).ToList())
            {
                Forget(p);
            }
        }
    }

    /// <summary>
    /// A request held for a soft link or outstanding on one, with what an answer to its client needs
    /// (A-0096 to A-0099, A-0109 to A-0111). <paramref name="Sap"/> is the sending client's SAP, null
    /// for a client confirmation of ours.
    /// </summary>
    private sealed record Held(byte? Sap, CUnidataRequest Request, byte DestSap, SisAddress DestAddress, ReadOnlyMemory<byte> UPdu);

    /// <summary>A U_PDU awaiting the answer to its delivery confirmation (A-0221, A-0226).</summary>
    private sealed class Pending(Held held, bool client)
    {
        public Held Held { get; } = held;

        /// <summary>Client delivery confirmation; otherwise node (A-0153, S-0081).</summary>
        public bool Client { get; } = client;

        /// <summary>Handed to the CAS: its wait is <c>sis.confirm_wait_ms</c>; before, its TTD (interp A-0232).</summary>
        public bool HandedDown { get; set; }

        /// <summary>The CAS has confirmed it (A-0227).</summary>
        public bool CasConfirmed { get; set; }

        /// <summary>The peer's DATA DELIVERY CONFIRM is in (A-0227).</summary>
        public bool PeerConfirmed { get; set; }

        /// <summary>Its entry in the deadline list.</summary>
        public (long Deadline, long Seq, int Handle)? Entry { get; set; }
    }

    private static TimerName IdleTimer(uint peer) => TimerName.ForPeer(TimerKind.SisSoftLinkIdle, peer);

    private static TimerName MinHoldTimer(uint peer) => TimerName.ForPeer(TimerKind.SisSoftLinkMinHold, peer);

    private static TimerName MaxTimer(uint peer) => TimerName.ForPeer(TimerKind.SisSoftLinkMax, peer);

    /// <summary>Stops <c>sis.held_off/&lt;peer&gt;</c> if it runs: the SIS asks again, or the link is made, by another way.</summary>
    private void StopHeldOff(uint peer)
    {
        if (_context.Timers.IsRunning(HeldOffTimer(peer)))
        {
            _context.Timers.Cancel(HeldOffTimer(peer));
        }
    }

    private static TimerName HeldOffTimer(uint peer) => TimerName.ForPeer(TimerKind.SisHeldOff, peer);

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

        /// <summary>The time the running idle wait counts from: when it was started, or the burst the peer was last heard in (interp A-0320).</summary>
        public long IdleSince { get; set; }

        /// <summary><c>sis.soft_link_max_ms</c> has passed since the link was made (A-0014).</summary>
        public bool Overdue { get; set; }
    }

    private sealed class Connection
    {
        public SisFrameDecoder Decoder { get; } = new();

        public byte? Sap { get; set; }

        public ServiceType ServiceType { get; set; }

        public int InvalidCount { get; set; }

        /// <summary>The bytes of its accepted U_PDUs the lower sublayers are not finished with (interp S-0163).</summary>
        public long Unfinished { get; set; }

        /// <summary>S_DATA_FLOW_OFF has been sent and no S_DATA_FLOW_ON since (interp S-0163).</summary>
        public bool FlowOff { get; set; }

        /// <summary>Clears the binding; true with the SAP it held, if it was bound.</summary>
        public bool Release(out byte sap)
        {
            sap = Sap.GetValueOrDefault();
            bool wasBound = Sap is not null;
            Sap = null;
            ServiceType = default;
            Unfinished = 0;
            FlowOff = false;
            return wasBound;
        }
    }
}
