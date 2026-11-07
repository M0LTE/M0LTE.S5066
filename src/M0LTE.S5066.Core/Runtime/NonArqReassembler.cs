using M0LTE.S5066.Core.Dts;

namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// Non-ARQ reassembly of received type 7 and type 8 D_PDUs [Ed4 C.5.2, p.C-63] (C-0530, C-0531),
/// error-free mode only (C-0532; deliver-with-errors, C-0533 to C-0536, is not offered). Only
/// segments that passed their CRC reach here (C-0007, C-0008; the stream receiver discards the rest).
/// </summary>
/// <remarks>
/// <para>
/// A C_PDU is identified by (source, type 7 or 8, C_PDU ID): the two ID spaces are separate
/// (C-0328, C-0359) and an ID names one C_PDU of one sender until its reception window has passed
/// (C-0327). A segment's place is its offset and size within the C_PDU size it states (C-0531,
/// C-0343, C-0346). Completion is every byte of the C_PDU covered; overlapping copies (repeats) are
/// harmless. A complete C_PDU is handed to the CAS at once (C-0539) with the primitive matching its
/// type (C-0540, C-0732), so C_PDUs are delivered in the order they complete (C-0010, C-0537). The
/// DELIVER IN ORDER bit is ignored (C-0337, C-0538).
/// </para>
/// <para>
/// Reception window (C-0350, C-1077): the deadline of a segment is the time of the
/// <c>bytes_received</c> that completed it plus its window in half seconds, never earlier than the
/// sender meant. A C_PDU's deadline is the latest of its segments'. An incomplete C_PDU whose
/// deadline passes is discarded with its segments and nothing is delivered (C-0336). The deadlines
/// of incomplete C_PDUs are one sorted list with one named timer, <c>dts.reception_window</c>, at
/// its head (core-architecture.md 3.2, collapsing rule). A segment whose deadline is already at or
/// before now is processed at once, never put in the list.
/// </para>
/// <para>
/// Copies after completion (C-1078): a delivered C_PDU leaves a record of its size and deadline, so
/// later copies of it (non-ARQ repeats, A-0081) are not delivered again. The record has no timer:
/// it is consulted only when a segment with the same key arrives, is stale once its deadline is at
/// or before now, and is replaced by the next C_PDU with that key.
/// </para>
/// <para>
/// A segment that does not fit inside the C_PDU size it states is discarded. A segment whose stated
/// C_PDU size differs from the C_PDU held under its key starts a new C_PDU in place of the held one
/// (C-1076): the sender has reused the ID, which it may do once the window it stamped has passed,
/// and our deadline is a little later than its window.
/// </para>
/// </remarks>
internal sealed class NonArqReassembler
{
    private static readonly TimerName WindowTimer = new(TimerKind.DtsReceptionWindow);

    private readonly CoreContext _context;
    private readonly SortedDictionary<Key, Partial> _partial = new();
    private readonly SortedDictionary<Key, (int Size, long Deadline)> _delivered = new();
    private long _nextSeq;

    public NonArqReassembler(CoreContext context) => _context = context;

    /// <summary>Incomplete C_PDUs held, for tests.</summary>
    internal int PartialCount => _partial.Count;

    /// <summary>
    /// One received type 7 or 8 D_PDU addressed to this node or a group it belongs to (C-0566).
    /// Enqueues the delivery primitive when it completes a C_PDU.
    /// </summary>
    public void OnSegment(uint src, uint dst, bool dstIsGroup, bool expedited, NonArqFields f, ReadOnlySpan<byte> segment)
    {
        if ((long)f.SegmentOffset + segment.Length > f.CPduSize)
        {
            return;   // does not fit the C_PDU it claims to belong to (C-1076)
        }

        var key = new Key(src, expedited, f.CPduId);
        long deadline = _context.Now + (f.ReceptionWindow * 500L);   // C-0350, C-1077

        if (_delivered.TryGetValue(key, out (int Size, long Deadline) done))
        {
            if (done.Size == f.CPduSize && done.Deadline > _context.Now)
            {
                // A copy of a C_PDU already delivered (C-1078).
                _delivered[key] = (done.Size, Math.Max(done.Deadline, deadline));
                return;
            }

            _delivered.Remove(key);
        }

        long? headBefore = Head();
        if (_partial.TryGetValue(key, out Partial? p) && p.Size != f.CPduSize)
        {
            _partial.Remove(key);   // ID reused with a different C_PDU (C-1076)
            p = null;
        }

        if (p is null)
        {
            p = new Partial(f.CPduSize, _nextSeq++);
            if (deadline <= _context.Now)
            {
                // Zero window: complete now or never (core-architecture.md 3.3, zero-duration rule).
                p.Add(f.SegmentOffset, segment);
                Rearm(headBefore);
                if (p.Complete)
                {
                    Deliver(src, dst, dstIsGroup, expedited, p.Bytes);
                }

                return;
            }

            p.Deadline = deadline;
            _partial[key] = p;
        }
        else
        {
            p.Deadline = Math.Max(p.Deadline, deadline);
        }

        p.Add(f.SegmentOffset, segment);
        if (p.Complete)
        {
            _partial.Remove(key);
            if (p.Deadline > _context.Now)
            {
                _delivered[key] = (p.Size, p.Deadline);
            }

            Rearm(headBefore);
            Deliver(src, dst, dstIsGroup, expedited, p.Bytes);
            return;
        }

        Rearm(headBefore);
    }

    /// <summary>
    /// The <c>dts.reception_window</c> expiry: every incomplete C_PDU whose deadline is at or before
    /// now is discarded, in (deadline, arrival) order, delivering nothing (C-0336); then the timer is
    /// re-armed for the new head.
    /// </summary>
    public void OnTimer()
    {
        // The dispatcher has already removed the timer from the table (core-architecture.md 3.3).
        foreach (Key key in Ordered().Where(k => _partial[k].Deadline <= _context.Now).ToList())
        {
            _partial.Remove(key);
        }

        long? head = Head();
        if (head is long h)
        {
            _context.Timers.Start(WindowTimer, h - _context.Now);
        }
    }

    private void Deliver(uint src, uint dst, bool dstIsGroup, bool expedited, byte[] cpdu)
    {
        // C-0540, C-0732: type 8 C_PDUs go up with D_EXPEDITED_UNIDATA_INDICATION, type 7 with D_UNIDATA_INDICATION.
        _context.Enqueue(expedited
            ? new DExpeditedUnidataIndication(src, dst, dstIsGroup, false, cpdu)
            : new DUnidataIndication(src, dst, dstIsGroup, false, cpdu));
    }

    /// <summary>Starts, restarts or cancels the timer when the head deadline has changed (collapsing rule).</summary>
    private void Rearm(long? headBefore)
    {
        long? head = Head();
        if (head == headBefore)
        {
            return;
        }

        if (head is long h)
        {
            _context.Timers.Start(WindowTimer, h - _context.Now);
        }
        else
        {
            _context.Timers.Cancel(WindowTimer);
        }
    }

    private long? Head()
    {
        Key? first = Ordered().Cast<Key?>().FirstOrDefault();
        return first is Key k ? _partial[k].Deadline : null;
    }

    /// <summary>The incomplete C_PDUs in the total order (deadline, arrival sequence).</summary>
    private IEnumerable<Key> Ordered()
    {
        var keys = _partial.Keys.ToList();
        keys.Sort((a, b) =>
        {
            Partial x = _partial[a];
            Partial y = _partial[b];
            return x.Deadline != y.Deadline ? x.Deadline.CompareTo(y.Deadline) : x.Seq.CompareTo(y.Seq);
        });
        return keys;
    }

    private readonly record struct Key(uint Src, bool Expedited, int Id) : IComparable<Key>
    {
        public int CompareTo(Key other)
        {
            int c = Src.CompareTo(other.Src);
            if (c != 0)
            {
                return c;
            }

            c = Expedited.CompareTo(other.Expedited);
            return c != 0 ? c : Id.CompareTo(other.Id);
        }
    }

    private sealed class Partial(int size, long seq)
    {
        private readonly bool[] _have = new bool[size];
        private int _missing = size;

        public int Size { get; } = size;

        public long Seq { get; } = seq;

        public long Deadline { get; set; }

        public byte[] Bytes { get; } = new byte[size];

        public bool Complete => _missing == 0;

        public void Add(int offset, ReadOnlySpan<byte> segment)
        {
            segment.CopyTo(Bytes.AsSpan(offset));
            for (int i = 0; i < segment.Length; i++)
            {
                if (!_have[offset + i])
                {
                    _have[offset + i] = true;
                    _missing--;
                }
            }
        }
    }
}
