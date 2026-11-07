using M0LTE.S5066.Core.Dts;

namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// The ARQ machine of one connection: the transmit and receive flow-control windows of
/// [Ed4 C.7.2, pp.C-97 to C-100] (C-0765) and the receive-side reassembly of [Ed4 C.5.1, pp.C-59 to
/// C-60]. The transmit queue (<see cref="TransmitQueue"/>) owns the C_PDUs being sent and drives the
/// transmit half; the per-peer receive machine (<see cref="DtsPeers"/>) drives the receive half. One
/// exists for each peer with a connection, created at D_CONNECTION_MADE and dropped at
/// D_CONNECTION_TERMINATED, so a new link starts from the initial window edges (C-0782, C-0787).
/// </summary>
/// <remarks>
/// <para>
/// The family is fixed by CAS-1 negotiation and never changes on the link (C-0762, C-0763): the
/// 16-bit family (types 9 to 12) with modulus 65536 and maximum window 32768, or the 8-bit family
/// (types 0 to 3) with modulus 256 and maximum window 128 (C-0190, C-0199, C-0558, C-0766, C-0771,
/// C-0782). The window logic is the same for both (C-0762); only the modulus, the maximum window
/// and the acknowledgement formats differ.
/// </para>
/// <para>
/// All sequence arithmetic goes through <see cref="SeqSpace"/> (core-architecture.md 6 rule 2).
/// The receive window a sender may use is never sent over the air, so the receiver judges every
/// FSN against the maximum window size of the family (C-0771, C-0779, C-0796).
/// </para>
/// </remarks>
internal sealed class ArqLink
{
    /// <summary>The maximum window size of the 16-bit family (C-0771).</summary>
    public const int MaxWindowSize16 = 32768;

    /// <summary>The maximum window size of the 8-bit family (C-0771).</summary>
    public const int MaxWindowSize8 = 128;

    /// <summary>
    /// The largest Ack Data this node puts in one type 10 ACK-ONLY. The field allows 255 bytes (C-0223),
    /// but ACK-ONLY D_PDUs are kept small (C-0213) and the offset mechanism lets the Ack Data stay within
    /// 32 bytes [Ed4 C.4.5.5, p.C-31], one 256-FSN unit as a bitmap; a wider acknowledgement is split
    /// by offset (C-0222, C-1151).
    /// </summary>
    public const int MaxAckBytes = 32;

    /// <summary>The largest Selective Acks field of a type 11 DATA-ACK (C-0248).</summary>
    public const int MaxDataAckBytes = 18;

    /// <summary>
    /// The largest SELECTIVE ACKS field of the 8-bit family, types 1 and 2 (C-0194, C-0236). The 127
    /// FSNs from RX LWE + 1 to the furthest a peer may send, RX LWE + 127 (C-0779, C-0798), take at
    /// most 16 bytes, so the whole window always fits one ACK-ONLY or one DATA-ACK.
    /// </summary>
    public const int MaxSelectiveAckBytes8 = 16;

    // Received segments are keyed by serial number, the FSN counted without wrapping from the start of
    // the link, so that segments of a C_PDU longer than the modulus never meet a later FSN of the same
    // value (C-0516, interp C-1194). Looked up by key only, never enumerated (core-architecture.md 6 rule 3).
    private readonly Dictionary<long, InFrame> _received = new();
    private long _rxLweSerial;   // the serial number of RX LWE
    private readonly List<CPduRun> _pendingInOrder = [];
    private IReadOnlyList<AckUnit>? _acknowledgement;   // cached until the receive window next changes

    /// <summary>A new ARQ machine with the initial window edges of its family (C-0782, C-0787).</summary>
    /// <param name="peer">The peer's address.</param>
    /// <param name="shortFamily">The 8-bit family (types 0 to 3) is in force, from D_CONNECTION_MADE (B-0141, B-0232).</param>
    public ArqLink(uint peer, bool shortFamily = false)
    {
        Peer = peer;
        ShortFamily = shortFamily;
        Seq = shortFamily ? SeqSpace.Mod256 : SeqSpace.Mod65536;
        MaxWindowSize = shortFamily ? MaxWindowSize8 : MaxWindowSize16;
        TxUwe = Seq.Modulus - 1;
        RxUwe = Seq.Modulus - 1;
    }

    public uint Peer { get; }

    /// <summary>The 8-bit family (types 0 to 3, modulus 256) rather than the 16-bit family (types 9 to 12, modulus 65536) (C-0763).</summary>
    public bool ShortFamily { get; }

    /// <summary>Modulus 256 or 65536 (C-0190, C-0199, C-0558, C-0766).</summary>
    public SeqSpace Seq { get; }

    /// <summary>The maximum window size, 128 or 32768 (C-0771), which the receiver always assumes (C-0779, C-0796).</summary>
    public int MaxWindowSize { get; }

    /// <summary>The largest acknowledgement field a DATA-ACK can carry: 16 bytes of SELECTIVE ACKS in type 2 (C-0236), 18 in type 11 (C-0248).</summary>
    public int DataAckLimit => ShortFamily ? MaxSelectiveAckBytes8 : MaxDataAckBytes;

    // ---- transmit window (C-0766 to C-0775) ----

    /// <summary>TX LWE: the lowest outstanding unacknowledged FSN (C-0768); 0 at the start (C-0782).</summary>
    public int TxLwe { get; set; }

    /// <summary>TX UWE: the FSN of the last new D_PDU sent (C-0769); 255 or 65535 at the start (C-0782).</summary>
    public int TxUwe { get; set; }

    /// <summary>The D_PDUs sent and not yet acknowledged, in FSN order from TX LWE (C-0767).</summary>
    public List<ArqFrame> Outstanding { get; } = [];

    /// <summary>
    /// The current transmitter window size, (TX UWE - (TX LWE - 1)) mod 256 or 65536, which is 0 at the
    /// initial values and counts the FSNs in use (C-0770, C-0785).
    /// </summary>
    public int WindowSize => Seq.Distance(TxLwe, Seq.Add(TxUwe, 1));

    // ---- receive window (C-0776 to C-0779) ----

    /// <summary>RX LWE: the oldest FSN not yet received (C-0776); 0 at the start (C-0782).</summary>
    public int RxLwe { get; private set; }

    /// <summary>RX UWE: the newest FSN received, the one furthest from RX LWE (C-0778); 255 or 65535 at the start (C-0782).</summary>
    public int RxUwe { get; private set; }

    /// <summary>A DATA-ONLY or DATA-ACK from the peer has been processed and no acknowledgement has gone since (C-0174, C-0242; C-1128).</summary>
    public bool AckOwed { get; set; }

    /// <summary>A regular ARQ D_PDU has been sent or received on this connection (C-0738).</summary>
    public bool ArqUsed { get; set; }

    /// <summary>Allocates the next FSN for a new D_PDU, ascending modulo 256 or 65536 (C-0189, C-0190, C-0191, C-0766), and moves TX UWE to it (C-0769).</summary>
    public int AllocateFsn()
    {
        TxUwe = Seq.Add(TxUwe, 1);
        return TxUwe;
    }

    /// <summary>
    /// A DATA-ONLY or DATA-ACK D_PDU from the peer, already past its CRC checks (C-0203; a DROP PDU
    /// whatever its data CRC, C-0204). Stores it if its FSN is in the receive window, moves the window
    /// edges, and returns the C_PDUs it completes that may now be delivered, in delivery order
    /// (C-0511 to C-0517, C-0178, C-0179). Every one owes an acknowledgement (C-0174, C-0242),
    /// including a copy behind RX LWE (C-1126). An FSN not within the maximum window ahead of RX LWE
    /// is not accepted and no acknowledgement covers it (C-0779). The caller has already applied
    /// Tests 1 to 3 (<see cref="InSyncAsDestination"/>), so such an FSN here is a copy behind RX LWE
    /// inside the arc of Equation 3, and the acknowledgement it causes carries RX LWE only; one in
    /// the gap Equation 3 leaves out of sync never reaches here and draws no acknowledgement (C-1170).
    /// </summary>
    public List<ReadOnlyMemory<byte>> Receive(int fsn, bool start, bool end, bool inOrder, bool drop, ReadOnlySpan<byte> segment)
    {
        var deliver = new List<ReadOnlyMemory<byte>>();
        ArqUsed = true;
        _acknowledgement = null;   // the receive window may change
        int ahead = Seq.Distance(RxLwe, fsn);
        if (ahead >= MaxWindowSize)
        {
            // Behind RX LWE (a copy of something received, C-0777, C-0779): nothing is stored, but the
            // acknowledgement carries RX LWE back to a sender that may have lost ours (C-1126).
            AckOwed = true;
            return deliver;
        }

        AckOwed = true;
        long serial = _rxLweSerial + ahead;
        if (_received.ContainsKey(serial))
        {
            return deliver;   // a copy of a D_PDU already held: edges unchanged (C-0777, C-0778)
        }

        _received[serial] = new InFrame(start, end, inOrder, drop, drop ? [] : segment.ToArray());
        int span = Seq.Distance(RxLwe, Seq.Add(RxUwe, 1));   // FSNs from RX LWE to RX UWE, 0 when none
        if (ahead + 1 > span)
        {
            RxUwe = fsn;   // the newest FSN received (C-0778)
        }

        AdvanceRxLwe();

        if (FindRun(serial) is CPduRun run)
        {
            if (run.Dropped)
            {
                // C-0184: a C_PDU with a DROP PDU among its segments is discarded, whichever
                // segment carried the flag (C-1125).
                Consume(run);
            }
            else if (!run.InOrder)
            {
                deliver.Add(Consume(run));   // complete and error free: at once (C-0179)
            }
            else
            {
                _pendingInOrder.Add(run);
            }
        }

        // An in-order C_PDU goes once every D_PDU before it has been received (RX LWE has passed
        // its last segment), oldest first (C-0178, C-1123).
        _pendingInOrder.Sort((a, b) => a.First.CompareTo(b.First));   // serial numbers: FSN order, each unique
        while (_pendingInOrder.Count > 0 && Behind(_pendingInOrder[0].Last))
        {
            CPduRun r = _pendingInOrder[0];
            _pendingInOrder.RemoveAt(0);
            deliver.Add(Consume(r));
        }

        return deliver;
    }

    /// <summary>
    /// The status of the receive window from RX LWE + 1 to RX UWE, one entry per FSN, true for a D_PDU
    /// received (C-0201, C-0202, C-0205, C-0207 to C-0209). Empty when nothing beyond RX LWE has been
    /// received.
    /// </summary>
    public List<bool> AckBits()
    {
        int span = Seq.Distance(RxLwe, Seq.Add(RxUwe, 1));
        int count = span <= 1 ? 0 : span - 1;
        var bits = new List<bool>(count);
        for (int i = 0; i < count; i++)
        {
            bits.Add(_received.ContainsKey(_rxLweSerial + 1 + i));
        }

        return bits;
    }

    /// <summary>The SELECTIVE ACKS bitmap of the whole receive window (C-0207 to C-0211), for tests.</summary>
    public byte[] AckBitmap()
    {
        List<bool> bits = AckBits();
        return AckData.Bitmap(bits, 0, bits.Count);
    }

    /// <summary>
    /// The acknowledgement of the receive window as it stands, as the ACK-ONLY D_PDUs that together
    /// acknowledge every D_PDU received (C-0215), in offset order. The bits run from RX LWE + 1 to
    /// RX UWE (C-0207 to C-0211, C-0233). Each D_PDU carries the more compact of the bitmap and the run
    /// length encoding, the bitmap on a tie (C-0224, C-0225, interp C-1150). Each D_PDU's Ack Data is
    /// kept to <see cref="MaxAckBytes"/> (C-0213): when neither encoding of the whole window fits, the
    /// window is split at multiples of 256 FSNs (C-0222): each D_PDU covers the most whole 256-FSN units
    /// from its offset whose more compact encoding fits, and the next starts where it ends (interp
    /// C-1151). With nothing beyond RX LWE received there is one D_PDU with no Ack Data.
    /// </summary>
    public IReadOnlyList<AckUnit> Acknowledgement() => _acknowledgement ??= BuildAcknowledgement();

    private List<AckUnit> BuildAcknowledgement()
    {
        List<bool> bits = AckBits();
        if (ShortFamily)
        {
            // Types 1 and 2 carry a bitmap only, with no encoding bit, offset or length field: the
            // SELECTIVE ACKS from RX LWE + 1 to RX UWE (C-0194, C-0200, C-0201, C-0207 to C-0211), at
            // most 16 bytes, so one D_PDU acknowledges the whole window (C-0215).
            return [new AckUnit(RxLwe, AckData.BitmapEncoding, 0, AckData.Bitmap(bits, 0, bits.Count))];
        }

        var units = new List<AckUnit>();
        int pos = 0;
        int offset = 0;
        do
        {
            int rest = bits.Count - pos;
            (int enc, byte[] data) = Compact(bits, pos, rest);
            if (data.Length <= MaxAckBytes)
            {
                units.Add(new AckUnit(RxLwe, enc, offset, data));
                break;
            }

            (int Enc, byte[] Data, int Units)? best = null;
            for (int k = 1; k * OffsetUnit < rest; k++)
            {
                (int e, byte[] d) = Compact(bits, pos, k * OffsetUnit);
                if (d.Length > MaxAckBytes)
                {
                    break;   // both encodings only grow as the range grows
                }

                best = (e, d, k);
            }

            // A 32-byte bitmap covers one unit, so best is always set.
            units.Add(new AckUnit(RxLwe, best!.Value.Enc, offset, best.Value.Data));
            pos += best.Value.Units * OffsetUnit;
            offset += best.Value.Units;
        }
        while (pos < bits.Count);

        return units;
    }

    /// <summary>The FSNs one step of the Offset field moves the start of the Ack Data (C-0222).</summary>
    public const int OffsetUnit = 256;

    private static (int Encoding, byte[] Data) Compact(List<bool> bits, int start, int count)
    {
        byte[] map = AckData.Bitmap(bits, start, count);
        byte[] rle = AckData.Rle(bits, start, count);
        return rle.Length < map.Length ? (AckData.RleEncoding, rle) : (AckData.BitmapEncoding, map);
    }

    // ---- synchronisation (C-0790 to C-0802) and reset (C-0812, C-0822, C-0265) ----

    /// <summary>
    /// Regular-data D_PDUs (DATA-ONLY, ACK-ONLY, DATA-ACK) to the peer are held back while this node
    /// waits for the acknowledgement of a RESET/WIN-RESYNC command it sent (C-0257, C-1175).
    /// </summary>
    public bool Suspended { get; set; }

    // ---- clean close (B-0236 to B-0254; interp B-0421) ----

    /// <summary>The policy of a clean close in progress on this link, or null when none was asked for (B-0241, B-0245 to B-0253).</summary>
    public ClosePolicy? Close { get; set; }

    /// <summary>The handle of the PHYSICAL LINK BREAK REQUEST queued for the close, exempt from <see cref="Close"/> (B-0242, B-0312).</summary>
    public int? CloseExemptHandle { get; set; }

    /// <summary>Clean close state has been reported to the CAS (once per close, B-0243).</summary>
    public bool CleanReported { get; set; }

    /// <summary>
    /// The destination-node tests on a DATA-ONLY or DATA-ACK from the peer, before it changes any
    /// window edge (C-0793, C-1180), each an arc on the FSN circle (C-0786) with the maximum window
    /// size (C-0796): Test 1, Equation 1, when TX WIN UWE is set (C-0794); Test 2, Equation 2, when
    /// TX WIN LWE is set (C-0797); Test 3, Equation 3, always (C-0798). The flags say the FSN is the
    /// sender's TX UWE or TX LWE. False means the peers have lost synchronisation (C-0802).
    /// </summary>
    public bool InSyncAsDestination(int fsn, bool txWinUwe, bool txWinLwe)
    {
        int span = MaxWindowSize - 1;
        if (txWinUwe && !Seq.InArc(fsn, RxUwe, Seq.Add(RxLwe, span)))
        {
            return false;   // Equation 1 (C-0794)
        }

        if (txWinLwe && !Seq.InArc(fsn, Seq.Add(RxUwe, -span), RxLwe))
        {
            return false;   // Equation 2 (C-0797)
        }

        return Seq.InArc(fsn, Seq.Add(RxUwe, -span), Seq.Add(RxLwe, span));   // Equation 3 (C-0798)
    }

    /// <summary>
    /// The originating-node tests on an ACK-ONLY or DATA-ACK from the peer, before it moves TX LWE
    /// (C-0799, C-1180): Test 4, Equation 4, on its RX LWE (C-0800), and Test 5, Equation 5, on
    /// each FSN it acknowledges explicitly (C-0801). With nothing outstanding (TX UWE = TX LWE - 1)
    /// no explicitly acknowledged FSN is in sync (C-0786). False means the peers have lost
    /// synchronisation (C-0802).
    /// </summary>
    public bool InSyncAsOriginator(int rxLwe, IEnumerable<int> acknowledged)
    {
        if (!Seq.InArc(rxLwe, TxLwe, Seq.Add(TxUwe, 1)))
        {
            return false;   // Equation 4 (C-0800)
        }

        bool nothingOutstanding = TxUwe == Seq.Add(TxLwe, -1);
        foreach (int fsn in acknowledged)
        {
            if (nothingOutstanding || !Seq.InArc(fsn, TxLwe, TxUwe, lowerExclusive: true))
            {
                return false;   // Equation 5 (C-0801, C-0786)
            }
        }

        return true;
    }

    /// <summary>
    /// The FULL RESET of this ARQ machine, in either role (C-0812, C-0814, C-0822, C-0824): window
    /// edges back to the initial values of C.7.2 (C-0782, C-0825), the receive buffers flushed
    /// (partly reassembled C_PDUs are lost), no acknowledgement owed, and nothing outstanding. The
    /// transmit queue rejects the C_PDUs whose D_PDUs this drops (C-0813, C-0823, C-0826, C-1176).
    /// </summary>
    public void Reset()
    {
        TxLwe = 0;
        TxUwe = Seq.Modulus - 1;
        RxLwe = 0;
        RxUwe = Seq.Modulus - 1;
        Outstanding.Clear();
        _received.Clear();
        _rxLweSerial = 0;
        _pendingInOrder.Clear();
        AckOwed = false;
        ArqUsed = false;
    }

    /// <summary>
    /// A RESET RX WIN CMND from the peer, read as C-0274 settles it: RX LWE becomes the NEW RECEIVE
    /// LWE the peer sent, which is its TX LWE (C-0263, C-0265, C-0274). Segments held that lie in
    /// the new receive window are kept, those behind it are discarded, RX UWE becomes the furthest
    /// segment kept (or NEW RECEIVE LWE - 1 when none), RX LWE then passes every segment already
    /// received (C-0776), and the in-order C_PDUs that leaves behind RX LWE are returned for delivery,
    /// oldest first (C-0178, C-1178). Our transmit window is not touched. A NEW RECEIVE LWE that
    /// would pass Test 2 (Equation 2, C-0797) changes nothing: the peer's TX LWE is already in step
    /// with our window, and moving RX LWE back would forget C_PDUs already delivered, so the peer's
    /// retransmissions of them would be delivered twice (C-0777, C-1178).
    /// </summary>
    public List<ReadOnlyMemory<byte>> ResyncReceive(int newRxLwe)
    {
        if (Seq.InArc(newRxLwe, Seq.Add(RxUwe, -(MaxWindowSize - 1)), RxLwe))
        {
            return [];   // in sync by Equation 2 (C-0797): RX LWE never moves back (C-0777, C-1178)
        }

        // The FSNs that can be held: every one from RX LWE to RX UWE, and the unconsumed segments
        // just behind RX LWE that belong to a C_PDU not yet complete. They are visited in FSN order
        // and looked up, so the dictionary is never enumerated (core-architecture.md 6 rule 3).
        var held = new List<long>();
        for (long f = _rxLweSerial - 1; _received.ContainsKey(f); f--)
        {
            held.Insert(0, f);
        }

        int span = Seq.Distance(RxLwe, Seq.Add(RxUwe, 1));
        for (int i = 0; i < span; i++)
        {
            long f = _rxLweSerial + i;
            if (_received.ContainsKey(f))
            {
                held.Add(f);
            }
        }

        // NEW RECEIVE LWE is not behind RX LWE within Equation 2, so it is taken as ahead of it.
        long newSerial = _rxLweSerial + Seq.Distance(RxLwe, newRxLwe);
        var kept = new Dictionary<long, InFrame>();
        int furthest = -1;
        foreach (long f in held)
        {
            long ahead = f - newSerial;
            if (ahead >= 0 && ahead < MaxWindowSize)
            {
                kept[f] = _received[f];
                furthest = Math.Max(furthest, (int)ahead);
            }
        }

        _received.Clear();
        foreach (long f in held)
        {
            if (kept.TryGetValue(f, out InFrame? frame))
            {
                _received[f] = frame;
            }
        }

        _pendingInOrder.RemoveAll(r => !_received.ContainsKey(r.First) || !_received.ContainsKey(r.Last));
        RxLwe = newRxLwe;
        _rxLweSerial = newSerial;
        RxUwe = Seq.Add(newRxLwe, furthest);   // NEW RECEIVE LWE - 1 when nothing is kept
        AdvanceRxLwe();

        var deliver = new List<ReadOnlyMemory<byte>>();
        _pendingInOrder.Sort((a, b) => a.First.CompareTo(b.First));
        while (_pendingInOrder.Count > 0 && Behind(_pendingInOrder[0].Last))
        {
            CPduRun r = _pendingInOrder[0];
            _pendingInOrder.RemoveAt(0);
            deliver.Add(Consume(r));
        }

        return deliver;
    }

    /// <summary>
    /// The connection is ending (interp C-1254): the complete in-order C_PDUs still held behind a gap
    /// are released, in FSN order, since their segments were acknowledged and the sender has confirmed
    /// them (C-0680, C-0764), and the gap can no longer be filled (C-0178). Partly received C_PDUs stay
    /// behind and are lost with the connection.
    /// </summary>
    public List<ReadOnlyMemory<byte>> TakeHeldInOrder()
    {
        var deliver = new List<ReadOnlyMemory<byte>>();
        _pendingInOrder.Sort((a, b) => a.First.CompareTo(b.First));
        foreach (CPduRun r in _pendingInOrder)
        {
            deliver.Add(Consume(r));
        }

        _pendingInOrder.Clear();
        return deliver;
    }

    /// <summary>RX LWE passes every segment already received (C-0776); it never moves back (C-0777).</summary>
    private void AdvanceRxLwe()
    {
        while (_received.TryGetValue(_rxLweSerial, out InFrame? f))
        {
            if (f.Consumed)
            {
                _received.Remove(_rxLweSerial);
            }

            RxLwe = Seq.Add(RxLwe, 1);
            _rxLweSerial++;
        }
    }

    /// <summary>The segment with this serial number lies behind RX LWE, so every segment before it has been received.</summary>
    private bool Behind(long serial) => serial < _rxLweSerial;

    /// <summary>
    /// The C_PDU whose segments include serial number <paramref name="serial"/>, when every segment from its
    /// C_PDU START to its C_PDU END has arrived (C-0512, C-0513, C-0516); null otherwise. Serial
    /// numbers, so a C_PDU may have any number of segments, more than the modulus included.
    /// </summary>
    private CPduRun? FindRun(long serial)
    {
        long first = serial;
        while (!_received[first].Start)
        {
            long prev = first - 1;
            if (!_received.TryGetValue(prev, out InFrame? p) || p.End)
            {
                return null;
            }

            first = prev;
        }

        long last = serial;
        while (!_received[last].End)
        {
            long next = last + 1;
            if (!_received.TryGetValue(next, out InFrame? n) || n.Start)
            {
                return null;
            }

            last = next;
        }

        bool dropped = false;
        bool inOrder = false;
        for (long f = first; ; f++)
        {
            InFrame frame = _received[f];
            if (frame.Consumed)
            {
                return null;
            }

            dropped |= frame.Drop;
            inOrder |= frame.InOrder;
            if (f == last)
            {
                break;
            }
        }

        return new CPduRun(first, last, inOrder, dropped);
    }

    /// <summary>Joins the segments in FSN order (C-0509, C-0516) and releases them.</summary>
    private ReadOnlyMemory<byte> Consume(CPduRun run)
    {
        var bytes = new List<byte>();
        for (long f = run.First; ; f++)
        {
            InFrame frame = _received[f];
            bytes.AddRange(frame.Data);
            frame.Consumed = true;
            if (Behind(f))
            {
                _received.Remove(f);
            }

            if (f == run.Last)
            {
                break;
            }
        }

        return bytes.ToArray();
    }

    private sealed record CPduRun(long First, long Last, bool InOrder, bool Dropped);

    private sealed class InFrame(bool start, bool end, bool inOrder, bool drop, byte[] data)
    {
        public bool Start { get; } = start;

        public bool End { get; } = end;

        public bool InOrder { get; } = inOrder;

        public bool Drop { get; } = drop;

        public byte[] Data { get; } = data;

        /// <summary>Delivered or discarded with its C_PDU; kept only while RX LWE has not passed it.</summary>
        public bool Consumed { get; set; }
    }
}

/// <summary>Where a sent ARQ D_PDU stands (C-0904, C-0908).</summary>
internal enum ArqFrameState
{
    /// <summary>Sent, and neither acknowledged nor known to have failed.</summary>
    Awaiting,

    /// <summary>Known or taken to have failed: owed a retransmission (C-0904, C-0908, C-0910, C-1120, C-1121).</summary>
    Failed,
}

/// <summary>One ARQ D_PDU that has been given an FSN: a segment of an ARQ C_PDU (C-0189).</summary>
internal sealed class ArqFrame(ArqCPdu owner, int segment, int fsn)
{
    public ArqCPdu Owner { get; } = owner;

    public int Segment { get; } = segment;

    public int Fsn { get; } = fsn;

    public ArqFrameState State { get; set; } = ArqFrameState.Awaiting;
}

/// <summary>
/// One ACK-ONLY's worth of acknowledgement (C-0195, C-0215): the RX LWE it carries, the Ack Data
/// encoding (C-0227, C-0228), the offset in units of 256 FSNs (C-0222, C-0233) and the Ack Data.
/// </summary>
internal sealed record AckUnit(int RxLwe, int Encoding, int Offset, byte[] Data);

/// <summary>
/// An ARQ C_PDU for a connected peer (C-0653, C-0673): held whole until first placed, then cut into
/// segments one at a time as each is placed, from a cursor (C-0502 to C-0510; core-architecture.md
/// 5.2), each sent as a DATA-ONLY or DATA-ACK D_PDU with its own FSN, in order. A segment is
/// normally the maximum segment size in force, the last one shorter; towards an Ed4 peer the planner
/// may cut one shorter to line D_PDUs up with modem blocks (C-0553, C-0872, interp C-1155).
/// Confirmed to the CAS when every segment has been acknowledged (C-0679, C-1130).
/// </summary>
internal sealed class ArqCPdu(long seq, DUnidataRequest request)
{
    public long Seq { get; } = seq;

    public DUnidataRequest Request { get; } = request;

    public uint Peer => Request.Dst;

    public int Priority => Request.Priority;

    /// <summary>The C_PDU length in bytes.</summary>
    public int Length => Request.CPdu.Length;

    /// <summary>The segments (offset, length) cut so far, each given an FSN when cut; null until the first.</summary>
    public List<(int Offset, int Length)>? Segments { get; private set; }

    /// <summary>The bytes cut into segments so far.</summary>
    public int Cursor { get; private set; }

    /// <summary>Segments given an FSN and not yet acknowledged.</summary>
    public int Unacked { get; set; }

    /// <summary>Its TTD passed while part of it was sent: rejected, and the rest goes as DROP PDUs (C-0859 to C-0862, C-1124).</summary>
    public bool Dropped { get; set; }

    public bool Placed => Segments is not null;

    public bool HasUnplacedSegment => !Placed || Cursor < Length;

    /// <summary>A C_PDU some of whose segments have FSNs and some not: its first transmissions must not be split by other data (C-0850, C-1127).</summary>
    public bool Partial => Placed && Cursor > 0 && Cursor < Length;

    /// <summary>The bytes not yet cut.</summary>
    public int Remaining => Length - Cursor;

    /// <summary>The length the next segment has at the maximum segment size: that size, or what is left (C-0505, C-0507, C-0508).</summary>
    public int NextSegmentLength(int maxSegment) => Math.Min(maxSegment, Remaining);

    /// <summary>Cuts the next segment, <paramref name="length"/> bytes from the cursor (C-0509), and returns its index.</summary>
    public int CutNext(int length)
    {
        Segments ??= [];
        Segments.Add((Cursor, length));
        Cursor += length;
        return Segments.Count - 1;
    }

    /// <summary>Whether a segment is the first of the C_PDU (C-0176).</summary>
    public bool IsFirst(int segment) => Segments![segment].Offset == 0;

    /// <summary>Whether a segment ends the C_PDU (C-0177).</summary>
    public bool IsLast(int segment) => Segments![segment].Offset + Segments[segment].Length == Length;
}
