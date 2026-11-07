namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// The ARQ machine of one connection with the 16-bit FSN family (types 9, 10 and 11): the transmit
/// and receive flow-control windows of [Ed4 C.7.2, pp.C-97 to C-100] (C-0765) and the receive-side
/// reassembly of [Ed4 C.5.1, pp.C-59 to C-60]. The transmit queue (<see cref="TransmitQueue"/>)
/// owns the C_PDUs being sent and drives the transmit half; the per-peer receive machine
/// (<see cref="DtsPeers"/>) drives the receive half. One exists for each peer with a connection
/// negotiated with the 16-bit family, created at D_CONNECTION_MADE and dropped at
/// D_CONNECTION_TERMINATED, so a new link starts from the initial window edges (C-0782, C-0787).
/// </summary>
/// <remarks>
/// All sequence arithmetic goes through <see cref="SeqSpace"/> (core-architecture.md 6 rule 2).
/// The receive window a sender may use is never sent over the air, so the receiver judges every
/// FSN against the maximum window size, 32768 (C-0771, C-0779).
/// </remarks>
internal sealed class ArqLink
{
    /// <summary>The maximum window size of the 16-bit family (C-0771), which the receiver always assumes (C-0779).</summary>
    public const int MaxWindowSize = 32768;

    /// <summary>The largest Ack Data an ACK-ONLY carries (C-0223). Covering a wider window takes offsets, P4-3's.</summary>
    public const int MaxAckBytes = 255;

    /// <summary>The largest Selective Acks field of a DATA-ACK (C-0248).</summary>
    public const int MaxDataAckBytes = 18;

    private readonly Dictionary<int, InFrame> _received = new();   // looked up by FSN only, never enumerated (core-architecture.md 6 rule 3)
    private readonly List<CPduRun> _pendingInOrder = [];

    public ArqLink(uint peer) => Peer = peer;

    public uint Peer { get; }

    public SeqSpace Seq => SeqSpace.Mod65536;

    // ---- transmit window (C-0766 to C-0775) ----

    /// <summary>TX LWE: the lowest outstanding unacknowledged FSN (C-0768); 0 at the start (C-0782).</summary>
    public int TxLwe { get; set; }

    /// <summary>TX UWE: the FSN of the last new D_PDU sent (C-0769); 65535 at the start (C-0782).</summary>
    public int TxUwe { get; set; } = 65535;

    /// <summary>The D_PDUs sent and not yet acknowledged, in FSN order from TX LWE (C-0767).</summary>
    public List<ArqFrame> Outstanding { get; } = [];

    /// <summary>
    /// The current transmitter window size, (TX UWE - (TX LWE - 1)) mod 65536, which is 0 at the
    /// initial values and counts the FSNs in use (C-0770, C-0785).
    /// </summary>
    public int WindowSize => Seq.Distance(TxLwe, Seq.Add(TxUwe, 1));

    // ---- receive window (C-0776 to C-0779) ----

    /// <summary>RX LWE: the oldest FSN not yet received (C-0776); 0 at the start (C-0782).</summary>
    public int RxLwe { get; private set; }

    /// <summary>RX UWE: the newest FSN received, the one furthest from RX LWE (C-0778); 65535 at the start (C-0782).</summary>
    public int RxUwe { get; private set; } = 65535;

    /// <summary>A DATA-ONLY or DATA-ACK from the peer has been processed and no acknowledgement has gone since (C-0174, C-0242; C-1128).</summary>
    public bool AckOwed { get; set; }

    /// <summary>A regular ARQ D_PDU has been sent or received on this connection (C-0738).</summary>
    public bool ArqUsed { get; set; }

    /// <summary>Allocates the next FSN for a new D_PDU, ascending modulo 65536 (C-0189, C-0191, C-0766), and moves TX UWE to it (C-0769).</summary>
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
        int ahead = Seq.Distance(RxLwe, fsn);
        if (ahead >= MaxWindowSize)
        {
            // Behind RX LWE (a copy of something received, C-0777, C-0779): nothing is stored, but the
            // acknowledgement carries RX LWE back to a sender that may have lost ours (C-1126).
            AckOwed = true;
            return deliver;
        }

        AckOwed = true;
        if (_received.ContainsKey(fsn))
        {
            return deliver;   // a copy of a D_PDU already held: edges unchanged (C-0777, C-0778)
        }

        _received[fsn] = new InFrame(start, end, inOrder, drop, drop ? [] : segment.ToArray());
        int span = Seq.Distance(RxLwe, Seq.Add(RxUwe, 1));   // FSNs from RX LWE to RX UWE, 0 when none
        if (ahead + 1 > span)
        {
            RxUwe = fsn;   // the newest FSN received (C-0778)
        }

        while (_received.TryGetValue(RxLwe, out InFrame? f))
        {
            if (f.Consumed)
            {
                _received.Remove(RxLwe);
            }

            RxLwe = Seq.Add(RxLwe, 1);   // C-0776; it never moves back (C-0777)
        }

        if (FindRun(fsn) is CPduRun run)
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
        // Held runs lie within the maximum window behind or ahead of RX LWE, so measuring each from
        // RX LWE - 32768 puts them in FSN order even when some are still ahead of RX LWE.
        int origin = Seq.Add(RxLwe, -MaxWindowSize);
        _pendingInOrder.Sort((a, b) => Seq.Distance(origin, a.First).CompareTo(Seq.Distance(origin, b.First)));
        while (_pendingInOrder.Count > 0 && Behind(_pendingInOrder[0].Last))
        {
            CPduRun r = _pendingInOrder[0];
            _pendingInOrder.RemoveAt(0);
            deliver.Add(Consume(r));
        }

        return deliver;
    }

    /// <summary>
    /// The SELECTIVE ACKS bitmap of the receive window: bit 0 of byte 0 for RX LWE + 1, then each
    /// FSN in turn up to RX UWE, set for each D_PDU received, zero padded to a whole byte, and no
    /// byte after the one holding RX UWE (C-0201, C-0202, C-0205, C-0207 to C-0211). Empty when
    /// nothing beyond RX LWE has been received. Capped at <see cref="MaxAckBytes"/>: wider coverage
    /// with offsets is P4-3's.
    /// </summary>
    public byte[] AckBitmap()
    {
        int span = Seq.Distance(RxLwe, Seq.Add(RxUwe, 1));
        int bits = span <= 1 ? 0 : span - 1;
        int bytes = Math.Min(MaxAckBytes, (bits + 7) / 8);
        bits = Math.Min(bits, bytes * 8);
        var map = new byte[bytes];
        for (int i = 0; i < bits; i++)
        {
            if (_received.ContainsKey(Seq.Add(RxLwe, 1 + i)))
            {
                map[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        return map;
    }

    // ---- synchronisation (C-0790 to C-0802) and reset (C-0812, C-0822, C-0265) ----

    /// <summary>
    /// Regular-data D_PDUs (DATA-ONLY, ACK-ONLY, DATA-ACK) to the peer are held back while this node
    /// waits for the acknowledgement of a RESET/WIN-RESYNC command it sent (C-0257, C-1175).
    /// </summary>
    public bool Suspended { get; set; }

    /// <summary>
    /// The destination-node tests on a DATA-ONLY or DATA-ACK from the peer, before it changes any
    /// window edge (C-0793, C-1180), each an arc on the FSN circle (C-0786) with the maximum window
    /// size (C-0796): Test 1, Equation 1, when TX WIN UWE is set (C-0794); Test 2, Equation 2, when
    /// TX WIN LWE is set (C-0797); Test 3, Equation 3, always (C-0798). The flags say the FSN is the
    /// sender's TX UWE or TX LWE. False means the peers have lost synchronisation (C-0802).
    /// </summary>
    public bool InSyncAsDestination(int fsn, bool txWinUwe, bool txWinLwe)
    {
        const int span = MaxWindowSize - 1;
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
        TxUwe = 65535;
        RxLwe = 0;
        RxUwe = 65535;
        Outstanding.Clear();
        _received.Clear();
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
    /// oldest first (C-0178, C-1178). Our transmit window is not touched.
    /// </summary>
    public List<ReadOnlyMemory<byte>> ResyncReceive(int newRxLwe)
    {
        // The FSNs that can be held: every one from RX LWE to RX UWE, and the unconsumed segments
        // just behind RX LWE that belong to a C_PDU not yet complete. They are visited in FSN order
        // and looked up, so the dictionary is never enumerated (core-architecture.md 6 rule 3).
        var held = new List<int>();
        for (int f = Seq.Add(RxLwe, -1); _received.ContainsKey(f) && held.Count < MaxWindowSize; f = Seq.Add(f, -1))
        {
            held.Insert(0, f);
        }

        int span = Seq.Distance(RxLwe, Seq.Add(RxUwe, 1));
        for (int i = 0; i < span; i++)
        {
            int f = Seq.Add(RxLwe, i);
            if (_received.ContainsKey(f))
            {
                held.Add(f);
            }
        }

        var kept = new Dictionary<int, InFrame>();
        int furthest = -1;
        foreach (int f in held)
        {
            int ahead = Seq.Distance(newRxLwe, f);
            if (ahead < MaxWindowSize)
            {
                kept[f] = _received[f];
                furthest = Math.Max(furthest, ahead);
            }
        }

        _received.Clear();
        foreach (int f in held)
        {
            if (kept.TryGetValue(f, out InFrame? frame))
            {
                _received[f] = frame;
            }
        }

        _pendingInOrder.RemoveAll(r => !_received.ContainsKey(r.First) || !_received.ContainsKey(r.Last));
        RxLwe = newRxLwe;
        RxUwe = Seq.Add(newRxLwe, furthest);   // NEW RECEIVE LWE - 1 when nothing is kept
        while (_received.TryGetValue(RxLwe, out InFrame? f))
        {
            if (f.Consumed)
            {
                _received.Remove(RxLwe);
            }

            RxLwe = Seq.Add(RxLwe, 1);
        }

        var deliver = new List<ReadOnlyMemory<byte>>();
        int origin = Seq.Add(RxLwe, -MaxWindowSize);
        _pendingInOrder.Sort((a, b) => Seq.Distance(origin, a.First).CompareTo(Seq.Distance(origin, b.First)));
        while (_pendingInOrder.Count > 0 && Behind(_pendingInOrder[0].Last))
        {
            CPduRun r = _pendingInOrder[0];
            _pendingInOrder.RemoveAt(0);
            deliver.Add(Consume(r));
        }

        return deliver;
    }

    private bool Behind(int fsn) => Seq.Distance(RxLwe, fsn) >= MaxWindowSize;

    /// <summary>
    /// The C_PDU whose segments include FSN <paramref name="fsn"/>, when every segment from its
    /// C_PDU START to its C_PDU END has arrived (C-0512, C-0513, C-0516); null otherwise.
    /// </summary>
    private CPduRun? FindRun(int fsn)
    {
        int first = fsn;
        int steps = 0;
        while (!_received[first].Start)
        {
            int prev = Seq.Add(first, -1);
            if (++steps >= MaxWindowSize || !_received.TryGetValue(prev, out InFrame? p) || p.End)
            {
                return null;
            }

            first = prev;
        }

        int last = fsn;
        steps = 0;
        while (!_received[last].End)
        {
            int next = Seq.Add(last, 1);
            if (++steps >= MaxWindowSize || !_received.TryGetValue(next, out InFrame? n) || n.Start)
            {
                return null;
            }

            last = next;
        }

        bool dropped = false;
        bool inOrder = false;
        for (int f = first; ; f = Seq.Add(f, 1))
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
        for (int f = run.First; ; f = Seq.Add(f, 1))
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

    private sealed record CPduRun(int First, int Last, bool InOrder, bool Dropped);

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
/// An ARQ C_PDU for a connected peer (C-0653, C-0673): held whole until first placed, then cut into
/// segments (C-0502 to C-0510), each sent as a DATA-ONLY or DATA-ACK D_PDU with its own FSN, in
/// order. Confirmed to the CAS when every segment has been acknowledged (C-0679, C-1130).
/// </summary>
internal sealed class ArqCPdu(long seq, DUnidataRequest request)
{
    public long Seq { get; } = seq;

    public DUnidataRequest Request { get; } = request;

    public uint Peer => Request.Dst;

    public int Priority => Request.Priority;

    /// <summary>The segments (offset, length), cut at first placement; null until then.</summary>
    public List<(int Offset, int Length)>? Segments { get; private set; }

    /// <summary>The first segment not yet given an FSN.</summary>
    public int NextSegment { get; set; }

    /// <summary>Segments given an FSN and not yet acknowledged.</summary>
    public int Unacked { get; set; }

    /// <summary>Its TTD passed while part of it was sent: rejected, and the rest goes as DROP PDUs (C-0859 to C-0862, C-1124).</summary>
    public bool Dropped { get; set; }

    public bool Placed => Segments is not null;

    public bool HasUnplacedSegment => !Placed || NextSegment < Segments!.Count;

    /// <summary>A C_PDU some of whose segments have FSNs and some not: its first transmissions must not be split by other data (C-0850, C-1127).</summary>
    public bool Partial => Placed && NextSegment > 0 && NextSegment < Segments!.Count;

    /// <summary>The segment length the next segment has, cutting with this size if not yet cut.</summary>
    public int NextSegmentLength(int maxSegment) =>
        Placed ? Segments![NextSegment].Length : Math.Min(maxSegment, Request.CPdu.Length);

    /// <summary>
    /// Cuts the C_PDU into segments of the maximum segment size in force, the last one shorter
    /// (C-0505, C-0507, C-0508, C-0509), each a contiguous run in order.
    /// </summary>
    public void Cut(int maxSegment)
    {
        var segments = new List<(int, int)>();
        int length = Request.CPdu.Length;
        for (int offset = 0; offset < length; offset += maxSegment)
        {
            segments.Add((offset, Math.Min(maxSegment, length - offset)));
        }

        Segments = segments;
    }
}
