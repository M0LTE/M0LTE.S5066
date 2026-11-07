using M0LTE.S5066.Core.Dts;

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

    /// <summary>The largest Ack Data an ACK-ONLY carries (C-0223); a wider acknowledgement is split by offset (C-0222, C-1151).</summary>
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
    /// is not accepted and no acknowledgement covers it (C-0779): with a window of half the circle,
    /// every such FSN lies behind RX LWE, and the acknowledgement it causes carries RX LWE only.
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
            bits.Add(_received.ContainsKey(Seq.Add(RxLwe, 1 + i)));
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
    /// length encoding, the bitmap on a tie (C-0224, C-0225, interp C-1150). When neither encoding of
    /// the whole window fits the 255-byte Ack Data (C-0223), the window is split at multiples of 256
    /// FSNs (C-0222): each D_PDU covers the most whole 256-FSN units from its offset whose more compact
    /// encoding fits, and the next starts where it ends (interp C-1151). With nothing beyond RX LWE
    /// received there is one D_PDU with no Ack Data.
    /// </summary>
    public List<AckUnit> Acknowledgement()
    {
        List<bool> bits = AckBits();
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
