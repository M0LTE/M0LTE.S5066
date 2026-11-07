namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// The receive side of the expedited ARQ service with one peer: EXPEDITED-DATA-ONLY (type 4) D_PDUs
/// are reassembled and acknowledged with EXPEDITED-ACK-ONLY (type 5) (C-0277, C-0290). Its frame
/// sequence numbers come from their own pool, kept apart from regular ARQ (C-0282, C-0283).
/// </summary>
/// <remarks>
/// <para>
/// The sender runs a C_PDU-level stop-and-wait: it starts each C_PDU's FSNs at 0 and gives each C_PDU
/// the next C_PDU ID modulo 16 (C-0756 to C-0759). So this node holds one reassembly at a time, and
/// FSNs within it run from 0 without wrapping; the FSN field is 8 bits. Interp C-1210 fixes what the
/// standard leaves open: a type 4 whose C_PDU ID is neither the one being reassembled nor the last one
/// delivered starts a new reassembly and discards a partial one; RX LWE is the lowest FSN of the
/// current C_PDU not yet received and RX UWE the highest received; an FSN more than 128 past RX LWE
/// is not stored, 128 being the most a 16-byte SELECTIVE ACKS can cover (C-0289). The C_PDU is
/// complete once FSN 0, carrying C_PDU START (C-1008), and every FSN up to one carrying C_PDU END
/// (C-1009) are held; it is delivered at once, since type 4 has no DELIVER IN ORDER flag. RX LWE then
/// stays one past its last FSN, and a copy of that C_PDU is acknowledged again but not delivered
/// again.
/// </para>
/// <para>
/// The acknowledgement follows the ED3-ACK-ONLY rules (C-0291): RX LWE, then a bitmap whose first bit
/// stands for RX LWE + 1 and whose last byte holds the bit of RX UWE, a set bit for a D_PDU received
/// [Ed4 C.4.5.4, pp.C-29 to C-30].
/// </para>
/// </remarks>
internal sealed class ExpeditedReceiver
{
    /// <summary>The most FSNs past RX LWE that SELECTIVE ACKS can acknowledge: 16 bytes of 8 bits (C-0289).</summary>
    private const int MaxAhead = 128;

    private readonly SortedDictionary<int, Segment> _held = new();
    private int? _currentId;
    private int? _deliveredId;
    private int _deliveredCount;

    /// <summary>A C_PDU is partly reassembled: the peer has not finished sending it (C-1212).</summary>
    public bool Partial => _currentId is not null && _held.Count > 0;

    /// <summary>
    /// A type 4 D_PDU from the peer, its data CRC good (C-0203). Returns the C_PDU it completes, or
    /// null. Every type 4 owes the peer an acknowledgement, a copy included (C-0277, C-1210).
    /// </summary>
    public byte[]? Receive(int cpduId, int fsn, bool start, bool end, ReadOnlySpan<byte> segment)
    {
        if (cpduId == _deliveredId && cpduId != _currentId)
        {
            return null;   // a copy of the C_PDU already delivered (C-1210)
        }

        if (cpduId != _currentId)
        {
            _held.Clear();   // a new C_PDU: the sender has moved on (C-0757, C-1210)
            _currentId = cpduId;
        }

        if (fsn - RxLwe > MaxAhead || _held.ContainsKey(fsn))
        {
            return null;
        }

        _held[fsn] = new Segment(start, end, segment.ToArray());
        int lwe = RxLwe;
        if (!_held.TryGetValue(0, out Segment? first) || !first.Start)
        {
            return null;
        }

        for (int i = 0; i < lwe; i++)
        {
            if (_held[i].End)
            {
                var cpdu = new List<byte>();
                for (int j = 0; j <= i; j++)
                {
                    cpdu.AddRange(_held[j].Data);
                }

                _deliveredId = cpduId;
                _deliveredCount = i + 1;
                _currentId = null;
                _held.Clear();
                return cpdu.ToArray();
            }
        }

        return null;
    }

    /// <summary>RX LWE: the lowest FSN of the current C_PDU not yet received, or one past the last FSN of the C_PDU last delivered.</summary>
    public int RxLwe
    {
        get
        {
            if (_currentId is null)
            {
                return _deliveredId is null ? 0 : _deliveredCount;
            }

            int lwe = 0;
            while (_held.ContainsKey(lwe))
            {
                lwe++;
            }

            return lwe;
        }
    }

    /// <summary>RX LWE and the SELECTIVE ACKS bitmap from RX LWE + 1 to RX UWE, the bits after RX UWE in its byte zero (C-0291; Ed4 C.4.5.4, p.C-30).</summary>
    public (int RxLwe, byte[] Bitmap) Acknowledgement()
    {
        int lwe = RxLwe;
        int uwe = _currentId is null || _held.Count == 0 ? lwe : _held.Keys.Max();
        int count = uwe - lwe;
        var bitmap = new byte[count <= 0 ? 0 : (count + 7) / 8];
        for (int i = 0; i < count; i++)
        {
            if (_held.ContainsKey(lwe + 1 + i))
            {
                bitmap[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        return (lwe, bitmap);
    }

    /// <summary>Leaving EXPEDITED-DATA(CONNECTED) other than by C-0738: a partly reassembled C_PDU is deleted (C-0761).</summary>
    public void DeletePartial()
    {
        _currentId = null;
        _held.Clear();
    }

    /// <summary>A new connection, or one re-established: the expedited ARQ machine starts afresh (C-0755).</summary>
    public void Reset()
    {
        DeletePartial();
        _deliveredId = null;
        _deliveredCount = 0;
    }

    private sealed record Segment(bool Start, bool End, byte[] Data);
}
