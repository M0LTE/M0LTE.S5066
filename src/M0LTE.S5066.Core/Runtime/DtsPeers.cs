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
/// ARQ (P4-2, 16-bit family): DATA-ONLY, ACK-ONLY and DATA-ACK (types 9, 10 and 11) from a peer
/// whose connection uses the 16-bit family are processed in every CONNECTED state, as Tables C-18,
/// C-20, C-22 and C-24 say: the data part is reassembled, delivered with D_UNIDATA_INDICATION and
/// owes an acknowledgement (C-0658, C-0660, C-0678, C-0680, C-0698, C-0700, C-0718, C-0720; C-1129), and the
/// acknowledgement part moves the transmit window (C-0679, C-0680, C-0699, C-0719). IDLE(CONNECTED)
/// moves to DATA(CONNECTED) on DATA-ONLY or DATA-ACK; an ACK-ONLY there acknowledges nothing we
/// sent and draws WARNING reason 3 instead, with D_WARNING_TRANSMITTED (C-0659). Still discarded
/// with no response: RESET/WIN-RESYNC (P4-4), the 8-bit family and the 16-bit types on an 8-bit
/// link (P4-5), EXPEDITED-DATA and EXPEDITED-ACK (P4-6).
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
/// EOWs (C-0445): an EOW type this node does not recognise (2, 5, 6, 7, 13, 14; C-0405, C-0411,
/// C-0412, C-0421) in a D_PDU addressed to its individual address is answered with an
/// UNRECOGNIZED-TYPE-ERROR (C-0446 to C-0448), carried in the EOW of the next D_PDU sent to that
/// peer (C-0396, C-1079). Recognised EOWs are for data rate selection and capability, phase 7.
/// </para>
/// </remarks>
internal sealed class DtsPeers
{
    /// <summary>RX MANAGEMENT FRAME ID NUMBER before any MANAGEMENT D_PDU is received: above 255 (C-0310).</summary>
    private const int NoFrameId = 256;

    private readonly CoreContext _context;
    private readonly TransmitQueue _tx;
    private readonly NonArqReassembler _reassembler;
    private readonly SortedDictionary<uint, Peer> _peers = new();

    public DtsPeers(CoreContext context, TransmitQueue tx)
    {
        _context = context;
        _tx = tx;
        _reassembler = new NonArqReassembler(context);
    }

    /// <summary>The non-ARQ reassembly, for tests and the <c>dts.reception_window</c> expiry.</summary>
    internal NonArqReassembler Reassembler => _reassembler;

    /// <summary>For tests: the Ed4 and short-family flags D_CONNECTION_MADE gave a connected peer, or null when not connected.</summary>
    internal (bool PeerIsEd4, bool ShortFamily)? ConnectionOf(uint peer) =>
        _peers.TryGetValue(peer, out Peer? p) && p.Connected ? (p.PeerIsEd4, p.ShortFamily) : null;

    /// <summary>The state kept for a peer, for tests; IDLE(UNCONNECTED) for a peer never heard.</summary>
    internal DtsPeerState StateOf(uint peer) => _peers.TryGetValue(peer, out Peer? p) ? p.State : DtsPeerState.IdleUnconnected;

    /// <summary>A whole D_PDU with good CRCs, whoever it is addressed to (C-0838).</summary>
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
            return;
        }

        if (!toUs)
        {
            return;   // C-0566; no other type has a group form (C-0122)
        }

        OweEowIfUnrecognised(h, true);
        switch (pdu)
        {
            case DataOnly or AckOnly or DataAck when _tx.LinkOf(h.Src) is ArqLink link:
                OnArq(PeerOf(h.Src), link, pdu);
                break;
            case Ed3DataOnly or Ed3AckOnly or Ed3DataAck or Ed3ResetWinResync or ExpeditedDataOnly or ExpeditedAckOnly
                or DataOnly or AckOnly or DataAck or ResetWinResync:
                if (PeerOf(h.Src).Connected)
                {
                    break;   // RESET (P4-4), the 8-bit family (P4-5), expedited ARQ (P4-6)
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

    /// <summary>Whether an ARQ connection with the peer exists (D_CONNECTION_MADE received and not terminated).</summary>
    public bool IsConnected(uint peer) => _peers.TryGetValue(peer, out Peer? p) && p.Connected;

    /// <summary>
    /// D_CONNECTION_MADE (C-0571, C-0591, C-0611, C-0631): ARQ state starts afresh, IDLE(CONNECTED),
    /// or MANAGEMENT(CONNECTED) from MANAGEMENT(UNCONNECTED). Ignored when already connected (C-0651,
    /// C-0671, C-0691, C-0711). The negotiated family is kept for P4-2 and P4-5.
    /// </summary>
    public void OnConnectionMade(DConnectionMade c)
    {
        Peer peer = PeerOf(c.Peer);
        if (peer.Connected)
        {
            return;
        }

        peer.Connected = true;
        peer.PeerIsEd4 = c.PeerIsEd4;
        peer.ShortFamily = c.ShortFamily;
        peer.State = peer.State == DtsPeerState.ManagementUnconnected ? DtsPeerState.ManagementConnected : DtsPeerState.IdleConnected;
        if (!c.ShortFamily)
        {
            _tx.OpenLink(c.Peer);   // the ARQ machine starts from the initial window edges (C-0782, C-0787)
        }
    }

    /// <summary>An ARQ D_UNIDATA_REQUEST for a connected peer: IDLE(CONNECTED) goes to DATA(CONNECTED) (C-0653); DATA stays (C-0673).</summary>
    public void OnArqRequest(uint peerAddress)
    {
        Peer peer = PeerOf(peerAddress);
        if (peer.State == DtsPeerState.IdleConnected)
        {
            peer.State = DtsPeerState.DataConnected;
        }
    }

    /// <summary>
    /// A DATA-ONLY, ACK-ONLY or DATA-ACK from a peer with a 16-bit ARQ machine. The data part first:
    /// each C_PDU it lets be delivered goes to the CAS with D_UNIDATA_INDICATION (C-0514, C-0517);
    /// then the acknowledgement part, which may confirm C_PDUs to the CAS (C-0680).
    /// </summary>
    private void OnArq(Peer peer, ArqLink link, Dpdu pdu)
    {
        DpduHeader h = pdu.Header;
        if (peer.State == DtsPeerState.IdleConnected)
        {
            peer.State = DtsPeerState.DataConnected;   // C-0658, C-0659, C-0660
            if (pdu is AckOnly)
            {
                Warn(h.Src, pdu.Type, 3);   // C-0659: nothing of ours to acknowledge yet
                return;
            }
        }

        (DataFlags flags, int fsn, Octets segment)? data = pdu switch
        {
            DataOnly d => (d.Flags, d.TxFsn, d.Segment),
            DataAck d => (d.Flags, d.TxFsn, d.Segment),
            _ => null,
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
        }
    }

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

        peer.Connected = false;
        peer.State = DtsPeerState.IdleUnconnected;
        _tx.RejectArq(peerAddress);
    }

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
        // MANAGEMENT(CONNECTED) (C-0664, C-0684, C-0704, C-0724).
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

        if (isNew && EowCodec.Decode(m.Header.Eow).Type is 1 or 2 or 5 or 6)
        {
            // An Ed3 stop/start data rate change or Annex I frequency change request (C-0919): not
            // supported, so refused with WARNING reason 3 after the acknowledgement (C-0920, C-0921,
            // C-1071). Nothing in it is acted on.
            Warn(m.Header.Src, 6, 3);
        }
    }

    /// <summary>
    /// The acknowledgement went into a transmission: when it was the last one owed, the management
    /// exchange with this peer is finished (C-1070) and the peer leaves MANAGEMENT(UNCONNECTED) for
    /// IDLE(UNCONNECTED) (C-0738: no non-ARQ data is pending in a state that would hold it), or
    /// MANAGEMENT(CONNECTED) for DATA(CONNECTED) or IDLE(CONNECTED) (C-0738).
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
            // C-0738: DATA(CONNECTED) once regular ARQ D_PDUs have flowed on this connection, else IDLE(CONNECTED).
            peer.State = _tx.LinkOf(address)?.ArqUsed == true ? DtsPeerState.DataConnected : DtsPeerState.IdleConnected;
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

    /// <summary>C-0445, C-0421, C-1079: an unrecognised EOW from a peer, in a D_PDU to our individual address.</summary>
    private void OweEowIfUnrecognised(DpduHeader h, bool individual)
    {
        if (individual && EowCodec.Decode(h.Eow) is UnrecognisedEow u)
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

        /// <summary>From D_CONNECTION_MADE: the 8-bit FSN family is in force (for P4-5).</summary>
        public bool ShortFamily { get; set; }
    }
}
