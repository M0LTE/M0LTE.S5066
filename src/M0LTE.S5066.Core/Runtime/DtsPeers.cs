using M0LTE.S5066.Core.Dts;

namespace M0LTE.S5066.Core.Runtime;

/// <summary>The eight DTS states of Figure C-54 (C-0562).</summary>
internal enum DtsPeerState
{
    IdleUnconnected,
    DataUnconnected,
    ExpeditedDataUnconnected,
    ManagementUnconnected,
    IdleConnected,
    DataConnected,
    ExpeditedDataConnected,
    ManagementConnected,
}

/// <summary>
/// The per-peer DTS state machine. For peers with no ARQ connection, the receive side of Tables C-10, C-12, C-14 and C-16 (C-0577 to C-0589, C-0597 to
/// C-0609, C-0617 to C-0629, C-0637 to C-0649), the MANAGEMENT receive procedure [Ed4 C.4.10,
/// p.C-43 to C-45], WARNING generation [Ed4 C.4.15, p.C-54 to C-56] and the UNRECOGNIZED-TYPE-ERROR
/// EOW [Ed4 C.6.3, p.C-70].
/// </summary>
/// <remarks>
/// <para>
/// Connections (P4-1): D_CONNECTION_MADE moves a peer from an UNCONNECTED state to IDLE(CONNECTED),
/// or from MANAGEMENT(UNCONNECTED) to MANAGEMENT(CONNECTED), and is ignored when connected (C-0571,
/// C-0591, C-0611, C-0631, C-0651, C-0671, C-0691, C-0711). D_CONNECTION_TERMINATED returns a
/// connected peer to IDLE(UNCONNECTED) and rejects its held ARQ C_PDUs, and is ignored when
/// unconnected, with no anomaly report (C-0572, C-0592, C-0612, C-0632, C-0652, C-0672, C-0692,
/// C-0712). In the CONNECTED states types 7 and 8 are received as in the UNCONNECTED ones, with the
/// CONNECTED transitions (C-0665, C-0666, C-0685, C-0686, C-0705, C-0706, C-0725, C-0726), and
/// MANAGEMENT, EXTENSION, PADDING and WARNING as there.
/// </para>
/// <para>
/// ARQ (P4-2, P4-5): DATA-ONLY, ACK-ONLY and DATA-ACK of the link's family, types 9, 10 and 11 on a
/// 16-bit link or types 0, 1 and 2 on an 8-bit link (C-0730, C-0762), are processed in every CONNECTED state, as Tables C-18,
/// C-20, C-22 and C-24 say: the data part is reassembled, delivered with D_UNIDATA_INDICATION and
/// owes an acknowledgement (C-0658, C-0660, C-0678, C-0680, C-0698, C-0700, C-0718, C-0720; C-1129), and the
/// acknowledgement part moves the transmit window (C-0679, C-0680, C-0699, C-0719). IDLE(CONNECTED)
/// moves to DATA(CONNECTED) on a DATA-ONLY or DATA-ACK that passes the synchronisation tests (C-1230); an ACK-ONLY there acknowledges nothing we
/// sent and draws WARNING reason 3 instead, with D_WARNING_TRANSMITTED (C-0659). A regular ARQ
/// D_PDU of the other family (types 0 to 3 on a 16-bit link, 9 to 12 on an 8-bit link) is not used
/// and draws WARNING reason 3, since the families are never mixed on a link (C-0763, C-0391, interp
/// C-1192); from a peer recorded Ed3, types 9 to 12 are undefined and draw reason 0 (C-0388).
/// </para>
/// <para>
/// Expedited ARQ and MANAGEMENT in the CONNECTED states (P4-6): EXPEDITED-DATA-ONLY (type 4) from a
/// connected peer, whatever its family, is reassembled by <see cref="ExpeditedReceiver"/> and
/// acknowledged with EXPEDITED-ACK-ONLY (type 5) (C-0277, C-0662, C-0682, C-0702, C-0722; C-1210,
/// C-1211); a type 5 received acknowledges nothing, since we never send type 4 (C-0663, C-0683,
/// C-0703, C-0723). EXPEDITED-DATA(CONNECTED) and MANAGEMENT(CONNECTED) are left when their exchange
/// is over (C-0738, C-1212, C-1213), and other exits delete a partial type 4 C_PDU (C-0761, C-1214).
/// A DATA-ONLY or DATA-ACK in a later received transmission than the last type 4 of a partly
/// received expedited C_PDU means the peer has abandoned that C_PDU: it is deleted and the type 5
/// owed for it dropped, as C-1214 does (C-1231).
/// While either exchange is open the planner holds the D_PDU types the state may not send, and
/// releases them, with any queued request, once the D_PDU that ends the exchange is placed (C-0737,
/// C-0740 to C-0748, C-1215).
/// </para>
/// <para>
/// Synchronisation and reset (P4-4): every DATA-ONLY, ACK-ONLY and DATA-ACK first passes Tests 1 to
/// 5 (C-0793 to C-0801, C-1180); one that fails is not used at all and starts a FULL RESET as
/// initiator (C-0791, C-0792, C-1170). RESET/WIN-RESYNC (type 12, or type 3 on an 8-bit link) is handled in every CONNECTED
/// state (C-0661, C-0681, C-0701, C-0721): the FULL RESET in both roles (C-0804 to C-0826), the
/// answer to RESET TX WIN RQST (C-0261, C-1177) and RESET RX WIN CMND as C-0274 settles it (C-1178).
/// A command of ours is stop-and-wait: it is repeated at each <c>dts.reset_wait/&lt;peer&gt;</c>
/// expiry until its RESET ACK comes, and meanwhile no regular data goes to the peer and none from
/// it is used (C-0255, C-0257, C-1172, C-1175).
/// </para>
/// <para>
/// State is kept per remote node (C-0565, C-0568) and driven only by D_PDUs addressed to this node,
/// or for types 7 and 8 to a group it belongs to, from that node (C-0566). Every valid D_PDU is
/// processed whatever the state (C-0749). The four tables give the same response to each received
/// type and differ only in the state they move to, so the response is written once.
/// </para>
/// <para>
/// WARNINGs (C-0376, C-0385): only for D_PDUs addressed to this node's own individual address, never
/// for group or broadcast destinations (C-0394, C-0735, J-0053). Reason 1 for every
/// connection-related type, 0 to 5 and 9 to 12, in all four states (C-0578 to C-0583 and the matching
/// rows; C-0734 over C-0389 and C-0390). Reason 4 for every EXTENSION, since no extended type is
/// implemented (C-0392, C-0735; the stream receiver already resumed after its header CRC, C-0365).
/// Reason 0 for a type undefined in Ed3 from a peer the CAS recorded Ed3 (C-0388, B-0408). Reason 3
/// for an Ed3 DRC or frequency change request in a MANAGEMENT D_PDU, after acknowledging it (C-0920,
/// C-0921, C-1071). Never for a received WARNING (C-0393). A WARNING already queued and unsent with
/// the same peer, type and reason is not queued again, and the CAS is told D_WARNING_TRANSMITTED
/// when the WARNING is queued (C-0578, C-0733; C-1075). WARNINGs are not acknowledged (C-0386).
/// </para>
/// <para>
/// MANAGEMENT (C-0584, C-0604, C-0624, C-0644): a type 6 with VALID MESSAGE set is a message. Its
/// frame ID is compared with the peer's RX MANAGEMENT FRAME ID NUMBER, which starts above 255
/// (C-0310, C-0314); a different ID is new and becomes the RX value (C-0315, C-1015), an equal one is
/// a repeat and leaves it (C-0316, C-1016). Every copy is acknowledged (C-0296), but an
/// acknowledgement already queued for the same ID is not queued twice (C-1074); only a new message is
/// acted on. A type 6 with VALID MESSAGE clear is an acknowledgement only (C-0302): it is not itself
/// acknowledged, and since this node never sends a MANAGEMENT message before phase 7, there is
/// nothing for it to acknowledge (C-1072). Extended content is ignored (C-0321). The acknowledgement
/// echoes the received frame ID and does not touch TX MANAGEMENT FRAME ID NUMBER (C-1073). The
/// receiving side of the stop-and-wait exchange is finished when the acknowledgement is placed in a
/// transmission (C-1070); the peer then leaves MANAGEMENT(UNCONNECTED) by C-0738.
/// </para>
/// <para>
/// EOWs (C-0445): an EOW type this node does not recognise (2, 5, 6, 7, 14; C-0405, C-0412, C-0421)
/// in a D_PDU addressed to its individual address is answered with an
/// UNRECOGNIZED-TYPE-ERROR (C-0446 to C-0448), carried in the EOW of the next D_PDU sent to that
/// peer (C-0396, C-1079). Recognised EOWs are for data rate selection and capability, phase 7.
/// EOW 13, the WTRP token, is known and kept from the state machine (C.7.5.1), so it draws no
/// UNRECOGNIZED-TYPE-ERROR (J-0070, interp J-0081).
/// </para>
/// <para>
/// MAC-mode discovery, the WARNING element (P5-3; J-0030 to J-0039): a MANAGEMENT message to this
/// node whose EOW, its subtype, is not recognised (7 or 14) draws WARNING reason 0 (J-0035, J-0036,
/// J-0054); one whose EOW is the WTRP token, a subtype of a MAC protocol this node does not run,
/// draws reason 3 (J-0037, J-0038, J-0070); both after the acknowledgement and only for a new
/// message, as for C-1071 (interp J-0081). Every EXTENSION to our individual address, a WTRP type
/// or not, draws reason 4 (J-0039, J-0052, J-0053, C-0735). The channel analysis element is
/// <see cref="ChannelAnalysis"/>.
/// </para>
/// </remarks>
internal sealed class DtsPeers
{
    /// <summary>RX MANAGEMENT FRAME ID NUMBER before any MANAGEMENT D_PDU is received: above 255 (C-0310).</summary>
    private const int NoFrameId = 256;

    /// <summary>
    /// How long, in answer waits (<see cref="NodeConfig.AnswerWaitMs"/>), a connected peer may go
    /// without answering before the DTS declares the connection lost (C-0739; interp C-1250).
    /// </summary>
    public const int SilenceWaits = 16;

    private readonly CoreContext _context;
    private readonly TransmitQueue _tx;
    private readonly NonArqReassembler _reassembler;
    private readonly SortedDictionary<uint, Peer> _peers = new();

    /// <summary>Counts received transmissions (burst starts), so a D_PDU can be placed in one (C-1231).</summary>
    private long _transmission;

    public DtsPeers(CoreContext context, TransmitQueue tx)
    {
        _context = context;
        _tx = tx;
        _reassembler = new NonArqReassembler(context);
        _tx.HoldsPartialExpedited = p => _peers.TryGetValue(p, out Peer? peer) && peer.Connected && peer.Expedited.Partial;
    }

    /// <summary>The non-ARQ reassembly, for tests and the <c>dts.reception_window</c> expiry.</summary>
    internal NonArqReassembler Reassembler => _reassembler;

    /// <summary>For tests: the Ed4 and short-family flags D_CONNECTION_MADE gave a connected peer, or null when not connected.</summary>
    internal (bool PeerIsEd4, bool ShortFamily)? ConnectionOf(uint peer) =>
        _peers.TryGetValue(peer, out Peer? p) && p.Connected ? (p.PeerIsEd4, p.ShortFamily) : null;

    /// <summary>The state kept for a peer, for tests; IDLE(UNCONNECTED) for a peer never heard.</summary>
    internal DtsPeerState StateOf(uint peer) => _peers.TryGetValue(peer, out Peer? p) ? p.State : DtsPeerState.IdleUnconnected;

    /// <summary>A whole D_PDU with good CRCs, whoever it is addressed to (C-0838), or a DATA-ACK whose data CRC alone failed (C-1133).</summary>
    public void OnDpdu(Dpdu pdu)
    {
        NodeConfig config = _context.Config;
        DpduHeader h = pdu.Header;
        bool toUs = h.Dst == config.LocalAddress;   // the individual address (C-0394)

        if (pdu is NonArqData or ExpeditedNonArqData)
        {
            NonArqFields f = pdu is NonArqData d ? d.Fields : ((ExpeditedNonArqData)pdu).Fields;
            bool forUs = f.GroupAddress ? IsOurGroup(h.Dst) : toUs;
            if (!forUs)
            {
                return;   // C-0566
            }

            OweEowIfUnrecognised(h, toUs && !f.GroupAddress);
            bool expedited = pdu is ExpeditedNonArqData;
            Peer peer = PeerOf(h.Src);
            if (!peer.Connected)
            {
                if (peer.State != DtsPeerState.ManagementUnconnected)
                {
                    // C-0585, C-0586, C-0605, C-0606, C-0625, C-0626; C-0645 and C-0646 stay in MANAGEMENT.
                    peer.State = expedited ? DtsPeerState.ExpeditedDataUnconnected : DtsPeerState.DataUnconnected;
                }
            }
            else if (peer.State == DtsPeerState.IdleConnected || (expedited && peer.State == DtsPeerState.DataConnected))
            {
                // C-0665, C-0666, C-0686; DATA stays on type 7 (C-0685), EXPEDITED-DATA and
                // MANAGEMENT stay on both (C-0705, C-0706, C-0725, C-0726).
                peer.State = expedited ? DtsPeerState.ExpeditedDataConnected : DtsPeerState.DataConnected;
            }

            Octets segment = pdu is NonArqData d2 ? d2.Segment : ((ExpeditedNonArqData)pdu).Segment;
            _reassembler.OnSegment(h.Src, h.Dst, f.GroupAddress, expedited, f, segment.Span);
            if (peer.Connected)
            {
                LeaveExpeditedIfDone(h.Src, peer);   // a type 8 owes no acknowledgement and holds no state (C-0738, C-1212)
            }

            return;
        }

        if (!toUs)
        {
            return;   // C-0566; no other type has a group form (C-0122)
        }

        if (pdu is DataAck { SegmentCrcOk: false, Flags.DropCPdu: false } or Ed3DataAck { SegmentCrcOk: false, Flags.DropCPdu: false }
            && _tx.LinkOf(h.Src) is null)
        {
            return;   // a failed data CRC with no ARQ machine to take the acknowledgement: discarded (C-0836, C-0839)
        }

        OweEowIfUnrecognised(h, true);
        switch (pdu)
        {
            case DataOnly or AckOnly or DataAck or Ed3DataOnly or Ed3AckOnly or Ed3DataAck
                when _tx.LinkOf(h.Src) is ArqLink link && OfFamily(pdu, link):
                OnArq(PeerOf(h.Src), link, pdu);
                break;
            case ResetWinResync or Ed3ResetWinResync when _tx.LinkOf(h.Src) is ArqLink link && OfFamily(pdu, link):
                OnReset(PeerOf(h.Src), link, pdu);
                break;
            case ExpeditedDataOnly x when PeerOf(h.Src).Connected:
                OnExpeditedData(PeerOf(h.Src), x);
                break;
            case ExpeditedAckOnly when PeerOf(h.Src).Connected:
                OnExpeditedAck(PeerOf(h.Src), pdu);
                break;
            case Ed3DataOnly or Ed3AckOnly or Ed3DataAck or Ed3ResetWinResync or ExpeditedDataOnly or ExpeditedAckOnly
                or DataOnly or AckOnly or DataAck or ResetWinResync:
                if (PeerOf(h.Src).Connected)
                {
                    // A regular ARQ D_PDU of the family the link does not use (expedited types 4 and 5
                    // from a connected peer are taken above): the families are never mixed (C-0763),
                    // so it is not allowed here and is not used (C-0391, interp C-1192); no state change.
                    Warn(h.Src, pdu.Type, 3);
                    break;
                }

                // Connection-related D_PDU while unconnected: no state change, WARNING reason 1
                // (C-0578 to C-0583, C-0598 to C-0603, C-0618 to C-0623, C-0638 to C-0643; C-0734).
                Warn(h.Src, pdu.Type, 1);
                break;
            case Management m:
                OnManagement(m);
                break;
            case Extension:
                // C-0735: no WTRP, so every EXTENSION to us is unrecognised: WARNING reason 4 (C-0392),
                // no state change, nothing else done with it (C-0587 and the matching rows do not apply).
                Warn(h.Src, 13, 4);
                break;
            case UndefinedTypeDpdu u:
                Warn(h.Src, u.Type, 0);   // C-0388: a type undefined for an Ed3 peer
                break;
            case Padding:
                break;   // C-0588, C-0608, C-0628, C-0648: ignored; its EOT was used by the receiver
            case Warning w:
                // C-0589, C-0609, C-0629, C-0649, C-0669, C-0689, C-0709, C-0729: tell the CAS, which
                // acts on it (B-0229); never answer it (C-0393).
                _context.Enqueue(new DWarningReceived(h.Src, h.Dst, w.ReceivedFrameType, w.Reason));
                break;
            default:
                throw new InvalidOperationException($"unhandled D_PDU {pdu.GetType().Name}");
        }
    }

    /// <summary>A burst has started at the modem: the D_PDUs that follow belong to a new transmission (C-1231).</summary>
    public void OnBurstStarted() => _transmission++;

    /// <summary>Whether an ARQ connection with the peer exists (D_CONNECTION_MADE received and not terminated).</summary>
    public bool IsConnected(uint peer) => _peers.TryGetValue(peer, out Peer? p) && p.Connected;

    /// <summary>
    /// D_CONNECTION_MADE (C-0571, C-0591, C-0611, C-0631): ARQ state starts afresh, IDLE(CONNECTED),
    /// or MANAGEMENT(CONNECTED) from MANAGEMENT(UNCONNECTED). Ignored when already connected (C-0651,
    /// C-0671, C-0691, C-0711). The ARQ machine of the negotiated family starts (C-0762, C-0782).
    /// </summary>
    public void OnConnectionMade(DConnectionMade c)
    {
        Peer peer = PeerOf(c.Peer);
        if (peer.Connected)
        {
            return;
        }

        peer.Connected = true;
        peer.SilentSince = null;   // C-1250: nothing awaited yet
        peer.Exchange = null;
        peer.ResetSent = false;
        peer.RxResetFrameId = NoFrameId;   // C-1173: each connection starts its RESET FRAME ID sequences afresh
        peer.TxResetFrameId = 0;
        peer.Expedited.Reset();   // C-0755
        peer.PeerIsEd4 = c.PeerIsEd4;
        peer.ShortFamily = c.ShortFamily;
        peer.State = peer.State == DtsPeerState.ManagementUnconnected ? DtsPeerState.ManagementConnected : DtsPeerState.IdleConnected;
        _tx.OpenLink(c.Peer, c.ShortFamily);   // the ARQ machine starts from the initial window edges of its family (C-0782, C-0787)
    }

    /// <summary>
    /// An ARQ D_UNIDATA_REQUEST for a connected peer: IDLE(CONNECTED) goes to DATA(CONNECTED) (C-0653);
    /// DATA stays (C-0673). In EXPEDITED-DATA(CONNECTED) and MANAGEMENT(CONNECTED) the request is queued
    /// and the state stays (C-0693, C-0713): the planner holds the C_PDU until the expedited exchange or
    /// the management exchange is over, and the state then goes to DATA(CONNECTED) (C-0737, C-1212, C-1215).
    /// </summary>
    public void OnArqRequest(uint peerAddress)
    {
        Peer peer = PeerOf(peerAddress);
        if (peer.State == DtsPeerState.IdleConnected)
        {
            peer.State = DtsPeerState.DataConnected;
        }
    }

    /// <summary>
    /// A non-ARQ D_UNIDATA_REQUEST to a connected peer's individual address: IDLE(CONNECTED) goes to
    /// DATA(CONNECTED) (C-0655); DATA stays (C-0675); in EXPEDITED-DATA(CONNECTED) and
    /// MANAGEMENT(CONNECTED) it is queued and the state stays (C-0695, C-0715, C-1215).
    /// </summary>
    public void OnNonArqRequest(uint peerAddress)
    {
        if (_peers.TryGetValue(peerAddress, out Peer? peer) && peer.State == DtsPeerState.IdleConnected)
        {
            peer.State = DtsPeerState.DataConnected;
        }
    }

    /// <summary>
    /// A DATA-ONLY, ACK-ONLY or DATA-ACK of the link's family (types 9 to 11, or 0 to 2 on an 8-bit link, C-0730) (a DATA-ACK whose data
    /// CRC failed brings its acknowledgement part only, which is neither stored nor acknowledged, C-1133). The data part first:
    /// each C_PDU it lets be delivered goes to the CAS with D_UNIDATA_INDICATION (C-0514, C-0517);
    /// then the acknowledgement part, which may confirm C_PDUs to the CAS (C-0680).
    /// </summary>
    private void OnArq(Peer peer, ArqLink link, Dpdu pdu)
    {
        DpduHeader h = pdu.Header;
        if (!link.PeerHeard)
        {
            link.PeerHeard = true;
            _context.Enqueue(new DPeerArqHeard(h.Src));   // interp B-0452: the peer has held this connection
        }
        if (pdu is DataOnly or DataAck or Ed3DataOnly or Ed3DataAck && peer.Expedited.Partial && peer.Type4Transmission < _transmission)
        {
            // C-1231: regular ARQ data in a later transmission than the last type 4 of a partly sent
            // expedited C_PDU: the peer has abandoned it. Deleted, with the type 5 owed for it, as C-1214.
            AbandonExpedited(h.Src, peer);
            LeaveExpeditedIfDone(h.Src, peer);
        }

        if (peer.Exchange is not null)
        {
            return;   // C-1175: discarded while we wait for the peer's RESET ACK
        }

        if (peer.State == DtsPeerState.IdleConnected && pdu is AckOnly or Ed3AckOnly)
        {
            peer.State = DtsPeerState.DataConnected;   // C-0659
            Warn(h.Src, pdu.Type, 3);   // C-0659: nothing of ours to acknowledge yet
            return;
        }

        if (!InSync(link, pdu))
        {
            // C-0791, C-0792, C-1170: out of synchronisation. Nothing in the D_PDU is used, its data is
            // neither stored nor acknowledged (issue #49 (c)), no state changes (C-1230), and a FULL RESET starts.
            StartFullReset(h.Src, peer, link);
            return;
        }

        Answered(peer);   // C-1250
        if (peer.State == DtsPeerState.IdleConnected)
        {
            peer.State = DtsPeerState.DataConnected;   // C-0658, C-0660
        }

        (DataFlags flags, int fsn, Octets segment)? data = pdu switch
        {
            DataOnly d => (d.Flags, d.TxFsn, d.Segment),
            DataAck d when d.SegmentCrcOk || d.Flags.DropCPdu => (d.Flags, d.TxFsn, d.Segment),
            Ed3DataOnly d => (d.Flags, d.TxFsn, d.Segment),
            Ed3DataAck d when d.SegmentCrcOk || d.Flags.DropCPdu => (d.Flags, d.TxFsn, d.Segment),
            _ => null,   // an ACK-ONLY, or a DATA-ACK whose data CRC failed: acknowledgement part only (C-1133)
        };
        if (data is var (f, txFsn, seg))
        {
            foreach (ReadOnlyMemory<byte> cpdu in link.Receive(txFsn, f.CPduStart, f.CPduEnd, f.DeliverInOrder, f.DropCPdu, seg.Span))
            {
                _context.Enqueue(new DUnidataIndication(h.Src, h.Dst, false, true, cpdu));
            }

            _tx.AckOwed();
        }

        switch (pdu)
        {
            case AckOnly a:
                _tx.OnAck(h.Src, a.RxLwe, a.Encoding, a.Offset, a.AckData.Span);
                break;
            case DataAck d:
                _tx.OnAck(h.Src, d.RxLwe, d.Encoding, 0, d.SelectiveAcks.Span);   // offset 0 (C-0250)
                break;
            case Ed3AckOnly a:
                _tx.OnAck(h.Src, a.RxLwe, AckData.BitmapEncoding, 0, a.SelectiveAcks.Span);   // a bitmap from RX LWE + 1 (C-0201, C-0207)
                break;
            case Ed3DataAck d:
                _tx.OnAck(h.Src, d.RxLwe, AckData.BitmapEncoding, 0, d.SelectiveAcks.Span);   // C-0245
                break;
        }
    }

    /// <summary>
    /// Whether a regular ARQ D_PDU belongs to the link's family: types 0 to 3 on an 8-bit link, 9 to
    /// 12 on a 16-bit link (C-0730, C-0762, C-0763).
    /// </summary>
    private static bool OfFamily(Dpdu pdu, ArqLink link) => (pdu.Type <= 3) == link.ShortFamily;

    /// <summary>
    /// D_CONNECTION_TERMINATED: a connected peer goes to IDLE(UNCONNECTED) and its held ARQ C_PDUs
    /// are rejected (C-0652, C-0672, C-0692, C-0712); an unconnected one is left as it is, with no
    /// anomaly report (C-0572, C-0592, C-0612, C-0632).
    /// </summary>
    public void OnConnectionTerminated(uint peerAddress)
    {
        if (!_peers.TryGetValue(peerAddress, out Peer? peer) || !peer.Connected)
        {
            return;
        }

        Terminate(peerAddress, peer, DReason.NoConnection);
    }

    private void Terminate(uint peerAddress, Peer peer, DReason reason)
    {
        if (_tx.LinkOf(peerAddress) is ArqLink link)
        {
            // Interp C-1254: complete in-order C_PDUs held behind a gap go up, in FSN order, before
            // the connection's state is dropped; they were acknowledged, so the sender confirmed them.
            foreach (ReadOnlyMemory<byte> cpdu in link.TakeHeldInOrder())
            {
                _context.Enqueue(new DUnidataIndication(peerAddress, _context.Config.LocalAddress, false, true, cpdu));
            }
        }

        peer.Connected = false;
        peer.State = DtsPeerState.IdleUnconnected;
        EndExchange(peerAddress, peer);
        peer.Expedited.Reset();
        _tx.RemoveControl(c => c is ResetItem or ExpeditedAckItem && c.Dst == peerAddress);
        _tx.RejectArq(peerAddress, reason);
    }

    /// <summary>
    /// Tests 1 to 5 on a regular-data D_PDU from the peer (C-0793, C-0799, C-1180): the destination
    /// tests on a DATA-ONLY or DATA-ACK, the originating tests on an ACK-ONLY or DATA-ACK. Equation 5
    /// covers the FSNs the Ack Data acknowledges, in either encoding (C-0226), from RX LWE + 1 + 256 x
    /// offset (C-0233, C-0250), as far as the acknowledgement part reads them (C-1153).
    /// </summary>
    private static bool InSync(ArqLink link, Dpdu pdu) => pdu switch
    {
        DataOnly d => link.InSyncAsDestination(d.TxFsn, d.Flags.TxWinUwe, d.Flags.TxWinLwe),
        DataAck d => link.InSyncAsDestination(d.TxFsn, d.Flags.TxWinUwe, d.Flags.TxWinLwe)
            && link.InSyncAsOriginator(d.RxLwe, Acknowledged(link, d.RxLwe, d.Encoding, 0, d.SelectiveAcks.Span)),
        AckOnly a => link.InSyncAsOriginator(a.RxLwe, Acknowledged(link, a.RxLwe, a.Encoding, a.Offset, a.AckData.Span)),
        Ed3DataOnly d => link.InSyncAsDestination(d.TxFsn, d.Flags.TxWinUwe, d.Flags.TxWinLwe),
        Ed3DataAck d => link.InSyncAsDestination(d.TxFsn, d.Flags.TxWinUwe, d.Flags.TxWinLwe)
            && link.InSyncAsOriginator(d.RxLwe, Acknowledged(link, d.RxLwe, AckData.BitmapEncoding, 0, d.SelectiveAcks.Span)),
        Ed3AckOnly a => link.InSyncAsOriginator(a.RxLwe, Acknowledged(link, a.RxLwe, AckData.BitmapEncoding, 0, a.SelectiveAcks.Span)),
        _ => true,
    };

    /// <summary>
    /// The FSNs an Ack Data field acknowledges explicitly, decoded as <see cref="TransmitQueue.OnAck"/>
    /// decodes it: either encoding, bits reaching the maximum window ahead of RX LWE ignored (C-1153).
    /// </summary>
    private static List<int> Acknowledged(ArqLink link, int rxLwe, int encoding, int offset, ReadOnlySpan<byte> ackData)
    {
        var fsns = new List<int>();
        List<bool> bits = AckData.Decode(encoding, ackData);
        int first = (offset * ArqLink.OffsetUnit) + 1;
        for (int i = 0; i < bits.Count && first + i < link.MaxWindowSize; i++)
        {
            if (bits[i])
            {
                fsns.Add(link.Seq.Add(rxLwe, first + i));
            }
        }

        return fsns;
    }

    /// <summary>
    /// Starts a FULL RESET as its initiator (C-0791, C-0792, C-0804): a FULL RESET CMD, FULL RESET
    /// CMD flag 1, the next RESET FRAME ID NUMBER of our sequence, NEW RECEIVE LWE 0 and the two
    /// window flags 0 (C-0805 to C-0809, C-1173), queued ahead of client data (C-1174); regular data
    /// to the peer is suspended until the FULL RESET ACK comes (C-0257). Nothing more while an
    /// exchange of ours is already pending (C-1170).
    /// </summary>
    private void StartFullReset(uint address, Peer peer, ArqLink link)
    {
        if (peer.Exchange is not null)
        {
            return;
        }

        BeginExchange(address, peer, link, new ResetItem(address, new ResetFlags(true, false, false, false), 0, peer.TxResetFrameId, link.ShortFamily));
    }

    private void BeginExchange(uint address, Peer peer, ArqLink link, ResetItem command)
    {
        var item = new ResetItem(address, command.Flags, command.NewRxLwe, command.FrameId, command.ShortFamily) { Placed = () => peer.ResetSent = true };
        peer.Exchange = item;
        link.Suspended = true;
        _tx.AddControl(item);
        _tx.SuspensionChanged();
    }

    /// <summary>Ends any exchange of ours with the peer: <c>dts.reset_wait</c> stops, a command not yet sent again is withdrawn, and regular data may flow (C-0257).</summary>
    private void EndExchange(uint address, Peer peer)
    {
        if (peer.Exchange is not ResetItem item)
        {
            return;
        }

        peer.Exchange = null;
        peer.ResetSent = false;
        _context.Timers.Cancel(TimerName.ForPeer(TimerKind.DtsResetWait, address));
        _tx.RemoveControl(c => ReferenceEquals(c, item));
        if (_tx.LinkOf(address) is ArqLink link)
        {
            link.Suspended = false;
        }

        _tx.SuspensionChanged();
    }

    /// <summary>
    /// A RESET/WIN-RESYNC of the link's family, type 12, or type 3 on an 8-bit link (C-0661, C-0681,
    /// C-0701, C-0721; C-0251, C-0252, C-0730); our answers are of the same family. In MANAGEMENT(CONNECTED) it draws WARNING reason 3 and nothing else (C-0721).
    /// Otherwise, in this order (C-1179):
    /// <list type="number">
    /// <item>RESET ACK set: the acknowledgement of our pending command (C-0264, C-0267). The exchange
    /// ends (C-0257); if it was a FULL RESET CMD the initiator now resets (C-0821 to C-0824). With no
    /// command of ours pending it is ignored.</item>
    /// <item>FULL RESET CMD set: the responder's procedure (C-0811 to C-0820), whatever the other
    /// flags; a copy, recognised by its RESET FRAME ID NUMBER, is acknowledged again but not acted on
    /// again (C-0271, C-1173).</item>
    /// <item>While a command of ours is still pending, RESET RX WIN CMND and RESET TX WIN RQST are
    /// neither acted on nor acknowledged (C-1175).</item>
    /// <item>RESET TX WIN RQST set: answered with one D_PDU that acknowledges it and forces the
    /// resynchronisation, RESET ACK and RESET RX WIN CMND both set, NEW RECEIVE LWE our TX LWE (C-0261,
    /// C-0263, C-0268, C-1177); it is a command of ours, so we wait for its RESET ACK (C-0264).</item>
    /// <item>RESET RX WIN CMND set: RX LWE becomes NEW RECEIVE LWE (C-0265 as C-0274 settles it,
    /// C-1178), then a RESET ACK (C-0266).</item>
    /// </list>
    /// An acknowledgement carries RESET ACK only and RESET FRAME ID NUMBER 0 (C-0816 to C-0818, C-1173),
    /// and is never itself acknowledged (C-0820). NEW RECEIVE LWE is read only with RESET RX WIN CMND
    /// (C-0269, C-0270). DATA(CONNECTED) and EXPEDITED-DATA(CONNECTED) go to IDLE(CONNECTED) on a new
    /// FULL RESET CMD (C-0681, C-0701), then to DATA(CONNECTED) if ARQ C_PDUs are still queued for the
    /// peer (C-1176); EXPEDITED-DATA(CONNECTED) goes to DATA(CONNECTED) otherwise (C-0701, C-0736).
    /// </summary>
    private void OnReset(Peer peer, ArqLink link, Dpdu pdu)
    {
        (ResetFlags flags, int newRxLwe, int resetFrameId) = pdu switch
        {
            ResetWinResync x => (x.Flags, x.NewRxLwe, x.ResetFrameId),
            Ed3ResetWinResync x => (x.Flags, x.NewRxLwe, x.ResetFrameId),
            _ => throw new InvalidOperationException("not a RESET/WIN-RESYNC"),
        };
        uint src = pdu.Header.Src;
        if (peer.State == DtsPeerState.ManagementConnected)
        {
            Warn(src, pdu.Type, 3);   // C-0721
            return;
        }

        if ((flags.ResetAck && peer.Exchange is not null) || flags.FullResetCmnd || (peer.Exchange is null && (flags.ResetTxWinRqst || flags.ResetRxWinCmnd)))
        {
            Answered(peer);   // a RESET/WIN-RESYNC acted on (C-1250)
        }

        bool wasExpedited = peer.State == DtsPeerState.ExpeditedDataConnected;
        if (flags.ResetAck && peer.Exchange is ResetItem pending)
        {
            EndExchange(src, peer);
            peer.TxResetFrameId = (peer.TxResetFrameId + 1) % 256;   // acknowledged, so the next command takes the next ID (C-0272, C-0273)
            if (pending.Flags.FullResetCmnd)
            {
                _tx.ResetArq(src);   // C-0822 to C-0824, C-0825
            }
        }

        if (flags.FullResetCmnd)
        {
            OnFullResetCmd(peer, link, src, resetFrameId);
            if (wasExpedited && peer.State != DtsPeerState.ExpeditedDataConnected)
            {
                AbandonExpedited(src, peer);   // C-0761, C-1214
            }

            return;
        }

        if (peer.State == DtsPeerState.ExpeditedDataConnected)
        {
            peer.State = DtsPeerState.DataConnected;   // C-0701, C-0736
            AbandonExpedited(src, peer);   // C-0761, C-1214
        }

        if (peer.Exchange is not null || !(flags.ResetTxWinRqst || flags.ResetRxWinCmnd))
        {
            return;   // C-1175; or an acknowledgement only, or no flag at all (C-1179)
        }

        bool copy = resetFrameId == peer.RxResetFrameId;
        peer.RxResetFrameId = resetFrameId;
        if (flags.ResetRxWinCmnd && !copy)
        {
            foreach (ReadOnlyMemory<byte> cpdu in link.ResyncReceive(newRxLwe))   // C-0265, C-0274, C-1178
            {
                _context.Enqueue(new DUnidataIndication(src, pdu.Header.Dst, false, true, cpdu));
            }
        }

        if (flags.ResetTxWinRqst)
        {
            if (!copy)
            {
                // C-0261, C-0263, C-0268, C-1177: acknowledge the request and force the resynchronisation in one D_PDU.
                BeginExchange(src, peer, link, new ResetItem(src, new ResetFlags(false, false, true, true), link.TxLwe, peer.TxResetFrameId, link.ShortFamily));
            }

            return;
        }

        QueueResetAck(src);   // C-0266, for a copy too (C-0271)
    }

    /// <summary>
    /// The responder's FULL RESET (C-0811 to C-0820). A new command (its RESET FRAME ID NUMBER differs
    /// from the last one received, C-0271, C-1173): any RESET RX WIN CMND exchange of ours is dropped,
    /// its NEW RECEIVE LWE now stale (C-1175); the ARQ machine resets, partly completed C_PDUs are
    /// rejected and the receive buffers flushed (C-0812 to C-0814, C-0826, C-1176); the state goes to
    /// IDLE(CONNECTED) (C-0661, C-0681, C-0701), or DATA(CONNECTED) when ARQ C_PDUs remain queued
    /// (C-1176). A pending FULL RESET of our own stays pending. Every copy is acknowledged (C-0815).
    /// </summary>
    private void OnFullResetCmd(Peer peer, ArqLink link, uint src, int frameId)
    {
        if (frameId != peer.RxResetFrameId)
        {
            peer.RxResetFrameId = frameId;
            if (peer.Exchange is ResetItem { Flags.FullResetCmnd: false })
            {
                EndExchange(src, peer);
            }

            _tx.ResetArq(src);
            peer.State = _tx.HasArqFor(src) ? DtsPeerState.DataConnected : DtsPeerState.IdleConnected;
        }

        QueueResetAck(src);
    }

    /// <summary>
    /// Queues a RESET ACK (C-0816 to C-0818; C-1173 for the RX WIN CMND acknowledgement), unless one to
    /// the peer is already queued and unsent: a second copy acknowledges nothing more.
    /// </summary>
    private void QueueResetAck(uint dst)
    {
        var ackFlags = new ResetFlags(false, false, false, true);
        if (!_tx.PendingControl.Any(c => c is ResetItem a && a.Dst == dst && a.Flags == ackFlags))
        {
            _tx.AddControl(new ResetItem(dst, ackFlags, 0, 0, _tx.LinkOf(dst)?.ShortFamily ?? false));
        }
    }

    /// <summary>
    /// Our transmission has ended: <c>dts.reset_wait/&lt;peer&gt;</c> starts for each peer it carried a
    /// RESET/WIN-RESYNC command to, in ascending address order, with the time allowed for an answer
    /// (C-0255, C-0264, C-1172).
    /// </summary>
    public void OnTransmitDone()
    {
        foreach ((uint address, Peer peer) in _peers)
        {
            if (peer.ResetSent && peer.Exchange is not null)
            {
                _context.Timers.Start(TimerName.ForPeer(TimerKind.DtsResetWait, address), _tx.AnswerWaitMs());
            }

            peer.ResetSent = false;
        }
    }

    /// <summary>
    /// <c>dts.reset_wait/&lt;peer&gt;</c> expired with no RESET ACK: the same command goes again, same
    /// RESET FRAME ID NUMBER (C-0255, C-0272, C-1172), unless the peer has been silent too long, when
    /// the connection is lost instead (C-1172 note, C-1250).
    /// </summary>
    public void OnResetWaitTimer(uint address)
    {
        if (!_peers.TryGetValue(address, out Peer? peer) || !peer.Connected || TooSilent(address, peer))
        {
            return;
        }

        if (peer.Exchange is ResetItem item && !_tx.PendingControl.Contains(item))
        {
            _tx.AddControl(item);
        }
    }

    /// <summary>
    /// <c>dts.ack_wait/&lt;peer&gt;</c> expired with no acknowledgement. Returns true when the peer
    /// has been silent too long and the connection has been lost (C-1250); the caller then does not
    /// retransmit (C-1120).
    /// </summary>
    public bool OnAckWaitExpired(uint address) =>
        _peers.TryGetValue(address, out Peer? peer) && peer.Connected && TooSilent(address, peer);

    /// <summary>
    /// Our transmission has ended, before its answer waits start (C-1250): each connected peer it
    /// carried a D_PDU to that has been silent too long loses its connection, in ascending address
    /// order, so no wait starts for it.
    /// </summary>
    public void CheckSilence(IEnumerable<uint> sentTo)
    {
        foreach (uint address in sentTo)
        {
            if (_peers.TryGetValue(address, out Peer? peer) && peer.Connected)
            {
                TooSilent(address, peer);
            }
        }
    }

    /// <summary>
    /// Our transmission has ended and its answer waits have started (C-1250): for each connected
    /// peer now awaiting an answer (<c>dts.ack_wait</c> or <c>dts.reset_wait</c> running) whose
    /// silence is not yet being timed, it starts now. Silence is timed only while an answer is
    /// awaited, so an idle link is never lost by its first transmission after a quiet spell.
    /// </summary>
    public void OnWaitsStarted()
    {
        foreach ((uint address, Peer peer) in _peers)
        {
            if (peer.Connected && peer.SilentSince is null
                && (_context.Timers.IsRunning(TimerName.ForPeer(TimerKind.DtsAckWait, address))
                    || _context.Timers.IsRunning(TimerName.ForPeer(TimerKind.DtsResetWait, address))))
            {
                peer.SilentSince = _context.Now;
            }
        }
    }

    /// <summary>
    /// Link failure detected by the DTS (C-0739; interp C-1250): the peer has given no answer for
    /// <see cref="SilenceWaits"/> answer waits since we began awaiting one, so the connection is
    /// lost. Returns true when it was.
    /// </summary>
    private bool TooSilent(uint address, Peer peer)
    {
        if (peer.SilentSince is not long since || _context.Now - since < SilenceWaits * _tx.AnswerWaitMs())
        {
            return false;
        }

        LoseConnection(address, peer);
        return true;
    }

    /// <summary>The peer answered: a D_PDU from it that its ARQ machine acted on (C-1250). Its silence is timed afresh from the next answer wait.</summary>
    private static void Answered(Peer peer) => peer.SilentSince = null;

    /// <summary>
    /// The DTS abandons the connection itself (C-0739): D_CONNECTION_LOST to the CAS, then, as for
    /// D_CONNECTION_TERMINATED, ARQ processing stops, its variables reset, the peer goes to
    /// IDLE(UNCONNECTED) and every ARQ C_PDU not fully processed is rejected with a local reason
    /// (C-0866, C-1251).
    /// </summary>
    private void LoseConnection(uint address, Peer peer)
    {
        _context.Enqueue(new DConnectionLost(address));
        Terminate(address, peer, DReason.ConnectionLost);
    }

    /// <summary>
    /// The CAS asked for clean close with the peer (<see cref="DCloseRequest"/>, interp B-0421): the
    /// transmit queue takes the policy and, if the link is not clean, the PHYSICAL LINK BREAK REQUEST;
    /// the answer goes back at once (<see cref="DLinkCleanState"/>).
    /// </summary>
    public void OnCloseRequest(DCloseRequest r)
    {
        if (!IsConnected(r.Peer))
        {
            return;   // the link has already ended; the CAS learns of it by its own primitives
        }

        bool clean = _tx.RequestClose(r.Peer, r.Policy, r.BreakRequest, r.Handle, ReceiveClean(r.Peer));
        _context.Enqueue(new DLinkCleanState(r.Peer, clean));
    }

    /// <summary>
    /// The DTS's turn to judge clean close state (B-0237; interp B-0422): after a received burst
    /// ends, after our own transmission ends, and after <c>dts.ttd</c>. Each connected peer whose link
    /// is closing and has just become clean is reported once, in ascending address order.
    /// </summary>
    public void CheckClean()
    {
        foreach ((uint address, Peer peer) in _peers)
        {
            if (peer.Connected && _tx.LinkOf(address) is { Close: not null, CleanReported: false } link
                && ReceiveClean(address) && _tx.IsClean(address))
            {
                link.CleanReported = true;
                _context.Enqueue(new DLinkCleanState(address, true));
            }
        }
    }

    /// <summary>
    /// The receive side of clean close state (B-0236 (a); interp B-0422): no RESET/WIN-RESYNC
    /// exchange of ours pending, no type 4 C_PDU from the peer partly received and no type 5, RESET
    /// acknowledgement or MANAGEMENT acknowledgement owed to it.
    /// </summary>
    private bool ReceiveClean(uint address) =>
        _peers.TryGetValue(address, out Peer? peer)
        && peer.Exchange is null
        && !peer.Expedited.Partial
        && !_tx.PendingControl.Any(c => c.Dst == address && c is ExpeditedAckItem or ResetItem or ManagementAck);

    /// <summary>The <c>dts.reception_window</c> expiry (C-0336).</summary>
    public void OnReceptionWindowTimer() => _reassembler.OnTimer();

    private void OnManagement(Management m)
    {
        Peer peer = PeerOf(m.Header.Src);
        if (!m.ValidMessage)
        {
            return;   // an acknowledgement only (C-0302, C-1072)
        }

        // C-0584, C-0604, C-0624, C-0644: go to (or stay in) MANAGEMENT(UNCONNECTED); connected,
        // MANAGEMENT(CONNECTED) (C-0664, C-0684, C-0704, C-0724). Leaving EXPEDITED-DATA(CONNECTED)
        // deletes a partly reassembled type 4 C_PDU (C-0761); a type 5 owed is deferred (C-0722, C-1213).
        if (peer.State == DtsPeerState.ExpeditedDataConnected)
        {
            peer.Expedited.DeletePartial();
        }

        peer.State = peer.Connected ? DtsPeerState.ManagementConnected : DtsPeerState.ManagementUnconnected;
        bool isNew = m.ManagementFrameId != peer.RxFrameId;   // C-0314 to C-0316
        if (isNew)
        {
            peer.RxFrameId = m.ManagementFrameId;   // C-1015
        }

        if (!_tx.PendingControl.Any(c => c is ManagementAck a && a.Dst == m.Header.Src && a.FrameId == m.ManagementFrameId))
        {
            peer.AcksOutstanding++;
            uint src = m.Header.Src;
            _tx.AddControl(new ManagementAck(src, m.ManagementFrameId) { Placed = () => AckPlaced(src, peer) });   // C-0296, C-1073, C-1074
        }

        int subtype = EowCodec.Decode(m.Header.Eow).Type;
        if (isNew && subtype is 1 or 2 or 5 or 6)
        {
            // An Ed3 stop/start data rate change or Annex I frequency change request (C-0919): not
            // supported, so refused with WARNING reason 3 after the acknowledgement (C-0920, C-0921,
            // C-1071). Nothing in it is acted on.
            Warn(m.Header.Src, 6, 3);
        }
        else if (isNew && subtype == ChannelAnalysis.TokenEowType)
        {
            // The WTRP token (EOW 13): a known subtype for a MAC protocol this node does not run,
            // so the D_PDU is unexpected: WARNING reason 3, and no UNRECOGNIZED-TYPE-ERROR (J-0038,
            // J-0070, interp J-0081).
            Warn(m.Header.Src, 6, 3);
        }
        else if (isNew && subtype is 7 or 14)
        {
            // A subtype this node does not recognise, RESERVED (C-0405, C-0412): the D_PDU is
            // treated as unrecognised, WARNING reason 0 (J-0035, J-0036), beside the
            // UNRECOGNIZED-TYPE-ERROR EOW already owed (C-0445; J-0054, interp J-0081).
            Warn(m.Header.Src, 6, 0);
        }
    }

    /// <summary>
    /// The acknowledgement went into a transmission: when it was the last one owed, the management
    /// exchange with this peer is finished (C-1070) and the peer leaves MANAGEMENT(UNCONNECTED) for
    /// IDLE(UNCONNECTED) (C-0738: no non-ARQ data is pending in a state that would hold it), or
    /// MANAGEMENT(CONNECTED) for EXPEDITED-DATA(CONNECTED) when a type 5 is owed or a type 4 C_PDU is
    /// partly received (C-1213), else DATA(CONNECTED) or IDLE(CONNECTED) (C-0738, C-1212). D_PDUs held
    /// back by the management exchange may follow in the same transmission (C-1070, C-1129, C-1215).
    /// </summary>
    private void AckPlaced(uint address, Peer peer)
    {
        peer.AcksOutstanding--;
        if (peer.AcksOutstanding == 0 && peer.State == DtsPeerState.ManagementUnconnected)
        {
            peer.State = DtsPeerState.IdleUnconnected;
        }
        else if (peer.AcksOutstanding == 0 && peer.State == DtsPeerState.ManagementConnected)
        {
            peer.State = ExpeditedPending(address, peer) ? DtsPeerState.ExpeditedDataConnected : AfterExchange(address);
        }
    }

    /// <summary>
    /// The CONNECTED state a finished expedited or management exchange returns to (C-0738, C-1212):
    /// DATA(CONNECTED) once regular ARQ D_PDUs have flowed on this connection, or while ARQ or type 7
    /// C_PDUs are queued for the peer, which the end of the exchange releases as if just requested
    /// (C-0737, C-0693, C-0695, C-0713, C-0715); otherwise IDLE(CONNECTED).
    /// </summary>
    private DtsPeerState AfterExchange(uint address) =>
        _tx.LinkOf(address)?.ArqUsed == true || _tx.HasArqFor(address) || _tx.HasNonArqFor(address)
            ? DtsPeerState.DataConnected
            : DtsPeerState.IdleConnected;

    /// <summary>The expedited receive exchange with the peer is not over: a type 5 is owed and unsent, or a type 4 C_PDU is partly received (C-1212).</summary>
    private bool ExpeditedPending(uint address, Peer peer) =>
        peer.Expedited.Partial || _tx.PendingControl.Any(c => c is ExpeditedAckItem && c.Dst == address);

    /// <summary>
    /// EXPEDITED-DATA(CONNECTED) is left once the expedited exchange is over (C-0738, C-1212), for
    /// DATA(CONNECTED) or IDLE(CONNECTED) as <see cref="AfterExchange"/> says.
    /// </summary>
    private void LeaveExpeditedIfDone(uint address, Peer peer)
    {
        if (peer.State == DtsPeerState.ExpeditedDataConnected && !ExpeditedPending(address, peer))
        {
            peer.State = AfterExchange(address);
            _tx.SuspensionChanged();
        }
    }

    /// <summary>
    /// EXPEDITED-DATA(CONNECTED) left by a RESET/WIN-RESYNC (C-0701, C-0736): a partly reassembled type 4
    /// C_PDU is deleted (C-0761) and an unsent type 5 is dropped, since neither DATA(CONNECTED) nor
    /// IDLE(CONNECTED) sends one (C-0745, C-0746, C-1214).
    /// </summary>
    private void AbandonExpedited(uint address, Peer peer)
    {
        peer.Expedited.DeletePartial();
        _tx.RemoveControl(c => c is ExpeditedAckItem && c.Dst == address);
    }

    /// <summary>
    /// An EXPEDITED-DATA-ONLY (type 4) from a connected peer (C-0662, C-0682, C-0702, C-0722): IDLE and
    /// DATA(CONNECTED) go to EXPEDITED-DATA(CONNECTED), EXPEDITED-DATA and MANAGEMENT(CONNECTED) stay.
    /// The segment is reassembled (C-1210) and a complete C_PDU delivered with
    /// D_EXPEDITED_UNIDATA_INDICATION; a type 5 is owed, one queued per peer and built when it is
    /// placed (C-0277, C-1210, C-1211). In MANAGEMENT(CONNECTED) it waits until the management
    /// exchange is over (C-0722, C-1215).
    /// </summary>
    private void OnExpeditedData(Peer peer, ExpeditedDataOnly x)
    {
        uint src = x.Header.Src;
        Answered(peer);   // C-1250
        if (peer.State is DtsPeerState.IdleConnected or DtsPeerState.DataConnected)
        {
            peer.State = DtsPeerState.ExpeditedDataConnected;
        }

        peer.Type4Transmission = _transmission;   // C-1231
        if (peer.Expedited.Receive(x.CPduId, x.TxFsn, x.CPduStart, x.CPduEnd, x.Segment.Span) is byte[] cpdu)
        {
            _context.Enqueue(new DExpeditedUnidataIndication(src, x.Header.Dst, false, true, cpdu));
        }

        if (!_tx.PendingControl.Any(c => c is ExpeditedAckItem && c.Dst == src))
        {
            ExpeditedReceiver rx = peer.Expedited;
            _tx.AddControl(new ExpeditedAckItem(src, rx.Acknowledgement) { Placed = () => ExpeditedAckPlaced(src, peer) });
        }
    }

    /// <summary>The type 5 went into a transmission: EXPEDITED-DATA(CONNECTED) may now be over (C-0738, C-1212).</summary>
    private void ExpeditedAckPlaced(uint address, Peer peer) => LeaveExpeditedIfDone(address, peer);

    /// <summary>
    /// An EXPEDITED-ACK-ONLY (type 5) from a connected peer. This node never sends type 4 (C-0276), so
    /// it acknowledges nothing: IDLE and DATA(CONNECTED) go to EXPEDITED-DATA(CONNECTED) and send
    /// WARNING reason 3 (C-0663, C-0683), and with no expedited exchange open the state is left again
    /// at once (C-1212); MANAGEMENT(CONNECTED) sends WARNING reason 3 (C-0723); in
    /// EXPEDITED-DATA(CONNECTED) there is nothing to process (C-0703, C-1212).
    /// </summary>
    private void OnExpeditedAck(Peer peer, Dpdu pdu)
    {
        uint src = pdu.Header.Src;
        switch (peer.State)
        {
            case DtsPeerState.IdleConnected or DtsPeerState.DataConnected:
                peer.State = DtsPeerState.ExpeditedDataConnected;
                Warn(src, pdu.Type, 3);
                LeaveExpeditedIfDone(src, peer);
                break;
            case DtsPeerState.ManagementConnected:
                Warn(src, pdu.Type, 3);
                break;
        }
    }

    private void Warn(uint dst, int receivedFrameType, int reason)
    {
        if (_tx.PendingControl.Any(c => c is WarningItem w && w.Dst == dst && w.ReceivedFrameType == receivedFrameType && w.Reason == reason))
        {
            return;   // C-1075
        }

        _tx.AddControl(new WarningItem(dst, receivedFrameType, reason));
        _context.Enqueue(new DWarningTransmitted(dst, receivedFrameType, reason));   // C-0733, C-1075
    }

    /// <summary>
    /// C-0445, C-0421, C-1079: an unrecognised EOW from a peer, in a D_PDU to our individual address.
    /// The WTRP token, EOW 13, is a known type that Annex C keeps from the state machine when WTRP is
    /// not in use, so it is not answered (C.7.5.1; J-0070, interp J-0081).
    /// </summary>
    private void OweEowIfUnrecognised(DpduHeader h, bool individual)
    {
        if (individual && EowCodec.Decode(h.Eow) is UnrecognisedEow { EowType: not ChannelAnalysis.TokenEowType } u)
        {
            _tx.OweEow(h.Src, EowCodec.Encode(new UnrecognizedTypeErrorEow(u.EowType)));
        }
    }

    /// <summary>A group address this node receives: one configured in <c>groups</c> (A-0120), or broadcast (C-0123).</summary>
    private bool IsOurGroup(uint dst) => dst == DpduAddress.MaxValue || _context.Config.Groups.Contains(dst);

    private Peer PeerOf(uint src)
    {
        if (!_peers.TryGetValue(src, out Peer? p))
        {
            p = new Peer();
            _peers[src] = p;
        }

        return p;
    }

    private sealed class Peer
    {
        public DtsPeerState State { get; set; } = DtsPeerState.IdleUnconnected;

        /// <summary>RX MANAGEMENT FRAME ID NUMBER (C-0308, C-0310). TX MANAGEMENT FRAME ID NUMBER is not kept: no MANAGEMENT message is sent before phase 7.</summary>
        public int RxFrameId { get; set; } = NoFrameId;

        public int AcksOutstanding { get; set; }

        /// <summary>D_CONNECTION_MADE received and not terminated.</summary>
        public bool Connected { get; set; }

        /// <summary>From D_CONNECTION_MADE: the link was negotiated Ed4 (for P4-2 and P4-3).</summary>
        public bool PeerIsEd4 { get; set; }

        /// <summary>From D_CONNECTION_MADE: the 8-bit FSN family is in force (B-0141, B-0232).</summary>
        public bool ShortFamily { get; set; }

        /// <summary>Our RESET/WIN-RESYNC command awaiting its RESET ACK, or null (C-0255, C-0264).</summary>
        public ResetItem? Exchange { get; set; }

        /// <summary>The pending command went into the transmission now in flight (C-1172).</summary>
        public bool ResetSent { get; set; }

        /// <summary>The RESET FRAME ID NUMBER of the last command received, or above 255 when none (C-0271, C-1173).</summary>
        public int RxResetFrameId { get; set; } = NoFrameId;

        /// <summary>The RESET FRAME ID NUMBER our next command takes, modulo 256 (C-0272, C-0273, C-1173).</summary>
        public int TxResetFrameId { get; set; }

        /// <summary>
        /// When the first transmission after the peer's last answer (or since the connection was
        /// made) that left us awaiting an answer ended; null while nothing has been awaited since (C-1250).
        /// </summary>
        public long? SilentSince { get; set; }

        /// <summary>The received transmission that carried the peer's last type 4 (C-1231).</summary>
        public long Type4Transmission { get; set; }

        /// <summary>The receive side of the expedited ARQ service (C-0282, C-1210).</summary>
        public ExpeditedReceiver Expedited { get; } = new();
    }
}
