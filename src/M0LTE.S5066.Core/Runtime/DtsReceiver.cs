using M0LTE.S5066.Core.Dts;

namespace M0LTE.S5066.Core.Runtime;

/// <summary>What the receiver last learned about one sender's signal, for data rate selection (C-0833; phase 7).</summary>
/// <param name="LastHeard">Core time of the burst end at which it was recorded.</param>
/// <param name="SnrTenthDb">The modem's SNR for that burst, in tenths of a dB, or null.</param>
/// <param name="Wn">The waveform number the modem locked to.</param>
/// <param name="Interleaver">The interleaver the modem locked to.</param>
/// <param name="K">The constraint length the modem locked to.</param>
/// <param name="ValidHeaders">D_PDU headers from this sender with a good header CRC, all bursts.</param>
/// <param name="DataCrcFailures">D_PDUs from this sender discarded for a bad data CRC, all bursts (C-0836, C-0839).</param>
internal sealed record RxQuality(long LastHeard, int? SnrTenthDb, int Wn, Interleaver Interleaver, int K, long ValidHeaders, long DataCrcFailures);

/// <summary>
/// The last burst in which a sender was heard addressing this node: a D_PDU header with a good
/// header CRC from it, to our individual address (C-0833, C-0394). Read by the SIS: interp A-0320
/// counts a soft link's idle wait from such a burst begun during it.
/// </summary>
/// <param name="Start">Core time of the burst's burst_started.</param>
/// <param name="End">Core time of the burst's burst_ended.</param>
internal sealed record HeardBurst(long Start, long End);

/// <summary>
/// The D_PDU stream receiver of the DTS (core-architecture.md 4.3). It turns the modem's bytes
/// into D_PDUs and does the header-level processing of [Ed4 C.7.5.1, p.C-110]; the per-peer state
/// machines that act on the D_PDUs come later (P3-6, phase 4).
/// </summary>
/// <remarks>
/// <para>
/// Sync search (C-0973, D-0007, D-0016): the bytes of one burst are one stream, searched for the
/// Maury-Styles sync <c>90 EB</c> across <c>bytes_received</c> boundaries. Each burst starts a fresh
/// buffer (core-architecture.md 3.5 rule 1); a partial D_PDU left at the burst end is dropped. The
/// modem port delivers bytes, not bits, so the search is bytewise (D-0016).
/// </para>
/// <para>
/// For each sync candidate: the source address is read from the address field first and its
/// edition looked up, because the edition changes the header-size check (core-architecture.md 10
/// item 8: Ed4 unless the peer is recorded Ed3; no peer is recorded before P4-1). Then the phase 1
/// codec checks the header up to its CRC (no second parser, determinism rule 7). A candidate whose
/// header fails any check is not a D_PDU: the search moves on by one byte. A candidate that needs
/// more bytes waits for the next <c>bytes_received</c>; at the burst end the waiting stops and the
/// search moves on by one byte, so a false sync near the end cannot hide a real D_PDU.
/// </para>
/// <para>
/// Per D_PDU, in byte order (core-architecture.md 3.5 rule 3), the header effects happen as soon as
/// the header is complete and valid, even if its data has not all arrived: an EOT &gt; 0 gives the
/// end time (C-0842, K-0021; EOT 0 gives nothing, C-0105). When the modem gave the block duration
/// of the burst at <c>burst_started</c> (interp C-1289), the block holding the D_PDU's first byte
/// started on air no later than its <c>bytes_received</c> time less one block (the modem cannot
/// decode a block before all of it has been on air). From there, <c>eot * 500</c> is the end the
/// EOT announces under our reading of where a D_PDU starts (C-1043), and adding first the byte's
/// share of the block, rounded up, gives an end that bounds a sender counting from the byte's place
/// too. The burst's estimate is the later of the latest announced end (no EOT heard is overruled,
/// C-0889) and the earliest bound (each is an upper bound on the true end, C-0843). Without the
/// block duration each header gives <c>t_rx + eot * 500</c>, where <c>t_rx</c> is the time of the
/// <c>bytes_received</c> that completed the header, or the burst end for a header found only by the
/// search at the burst end, and the estimate is the earliest of them (C-1024). The (src, dst, group)
/// triple goes to the MAC for the two-node rule (K-0068). This holds for every valid header,
/// whoever it is addressed to (C-0832, C-0868). Then, once the whole D_PDU is there, one whose data
/// CRC fails is discarded (C-0834, C-0836, C-0839) and the rest go to the state machine (C-0838).
/// </para>
/// <para>
/// EXTENSION (type 13): no extended type is implemented, so only its header is read and the
/// search resumes after its header CRC, at the next sync (C-0365). It still goes to the state
/// machine, which decides about WARNING reason 4 (C-0735, P3-6).
/// </para>
/// </remarks>
internal sealed class DtsReceiver
{
    private const byte Sync0 = 0x90;
    private const byte Sync1 = 0xEB;

    private readonly CoreContext _context;
    private readonly IMacObserver _mac;
    private readonly Action<Dpdu> _toStateMachine;
    private readonly Action<DpduHeaderInfo>? _onHeader;
    private readonly SortedDictionary<uint, RxQuality> _quality = new();
    private long _burstStart;
    private readonly SortedSet<uint> _heardThisBurst = new();
    private readonly SortedSet<uint> _toUsThisBurst = new();
    private readonly SortedDictionary<uint, HeardBurst> _heardToUs = new();
    private readonly SortedDictionary<uint, (long Headers, long Checked, long DataCrc)> _countsThisBurst = new();

    private byte[] _buffer = new byte[256];
    private int _length;
    private int _pos;

    // Stream offset of _buffer[0] within the current burst, the block duration the modem gave at
    // burst_started (or null), and each block of the burst: its first stream offset, its length and
    // the time it arrived (interp C-1289).
    private long _streamBase;
    private int? _blockMs;
    private readonly List<(long Start, int Length, long T)> _blocks = [];

    // With block_ms: the latest end announced under C-1043 and the earliest bound counting the byte's
    // share of its block, over the burst's headers so far (interp C-1289).
    private long? _latestAnnounced;
    private long? _earliestBound;

    // Start offset of the D_PDU whose header effects have been applied but whose data is still
    // arriving, or -1. Stops the header effects being applied twice.
    private int _headerDoneAt = -1;

    public DtsReceiver(CoreContext context, IMacObserver mac, Action<Dpdu> toStateMachine, Action<DpduHeaderInfo>? onHeader = null)
    {
        _context = context;
        _mac = mac;
        _toStateMachine = toStateMachine;
        _onHeader = onHeader;
        context.RxQuality = _quality;   // read by the SIS for node information (interp S-0164)
        context.HeardToUs = _heardToUs; // read by the SIS for the soft link idle wait (interp A-0320)
    }

    /// <summary>Headers with a sync that failed a header check (bad CRC, impossible sizes), all bursts.</summary>
    public long RejectedHeaders { get; private set; }

    /// <summary>The receive quality record per sender, in address order (C-0833).</summary>
    public IReadOnlyDictionary<uint, RxQuality> Quality => _quality;

    /// <summary>A new burst: a fresh stream buffer and fresh per-burst records (core-architecture.md 3.5 rule 1).</summary>
    /// <param name="blockMs">The duration of one block of the burst in ms, from the modem, or null (interp C-1289).</param>
    public void OnBurstStarted(int? blockMs = null)
    {
        _burstStart = _context.Now;
        _blockMs = blockMs;
        _latestAnnounced = null;
        _earliestBound = null;
        Reset();
        _heardThisBurst.Clear();
        _toUsThisBurst.Clear();
        _countsThisBurst.Clear();
    }

    /// <summary>Appends one block's bytes and processes every D_PDU they complete, in byte order.</summary>
    public void OnBytesReceived(ReadOnlySpan<byte> bytes)
    {
        _blocks.Add((_streamBase + _length, bytes.Length, _context.Now));
        Append(bytes);
        Scan(endOfBurst: false);
    }

    /// <summary>
    /// The burst ended: finish the search over what is left, drop any partial D_PDU, and record the
    /// burst's quality for every sender heard in it (C-0833; core-architecture.md 3.5 rule 1).
    /// </summary>
    /// <returns>
    /// Each sender heard in the burst, in address order, with its D_PDUs in it whose data part was checked by a CRC-32
    /// and the data CRC failures among them (for P7-6; corpus-format.md 6.2, interp C-1270 as revised in corpus 0.35.0).
    /// </returns>
    public IReadOnlyList<(uint Src, long Checked, long DataCrcFailures)> OnBurstEnded(BurstEnded burst)
    {
        Scan(endOfBurst: true);
        Reset();

        var heard = new List<(uint, long, long)>();
        foreach (uint src in _heardThisBurst)
        {
            (long headers, long dataChecked, long dataCrc) = _countsThisBurst[src];
            heard.Add((src, dataChecked, dataCrc));
            _quality.TryGetValue(src, out RxQuality? old);
            _quality[src] = new RxQuality(
                _context.Now, burst.SnrTenthDb, burst.Wn, burst.Interleaver, burst.K,
                (old?.ValidHeaders ?? 0) + headers, (old?.DataCrcFailures ?? 0) + dataCrc);
        }

        foreach (uint src in _toUsThisBurst)
        {
            _heardToUs[src] = new HeardBurst(_burstStart, _context.Now);
        }

        _heardThisBurst.Clear();
        _toUsThisBurst.Clear();
        _countsThisBurst.Clear();
        return heard;
    }

    /// <summary>
    /// The edition a sender's D_PDUs are decoded with: Ed3 only for a peer the CAS recorded Ed3,
    /// else Ed4 (core-architecture.md 10 item 8; B-0408, C-0051).
    /// </summary>
    private PeerEdition EditionOf(uint src) => _context.Peers.DecodeEditionOf(src);

    private void Scan(bool endOfBurst)
    {
        while (true)
        {
            int sync = FindSync(_pos);
            if (sync < 0)
            {
                // Keep a trailing 90: it may be the first byte of a sync split across blocks.
                _pos = _length > _pos && _buffer[_length - 1] == Sync0 ? _length - 1 : _length;
                _headerDoneAt = -1;
                Compact();
                return;
            }

            if (sync != _pos)
            {
                _headerDoneAt = -1;
            }

            _pos = sync;
            ReadOnlySpan<byte> span = _buffer.AsSpan(_pos, _length - _pos);
            bool peekedOk = DpduCodec.TryPeekSource(span, out uint peeked);
            if (!peekedOk && !endOfBurst && span.Length >= 6 && (span[5] >> 5) != 0)
            {
                // The address field is not all here yet: wait for it, so the edition is known
                // before the header-size check (core-architecture.md 10 item 8).
                Compact();
                return;
            }

            PeerEdition edition = peekedOk ? EditionOf(peeked) : PeerEdition.Ed4;

            DecodeResult<DpduHeaderInfo> header = DpduCodec.DecodeHeader(span, edition);
            if (!header.IsSuccess)
            {
                if (header.Error == PduErrorKind.Truncated && !endOfBurst)
                {
                    Compact();
                    return;
                }

                if (header.Error != PduErrorKind.Truncated)
                {
                    RejectedHeaders++;
                }

                _pos++;
                continue;
            }

            if (_headerDoneAt != _pos)
            {
                ApplyHeader(header.Value!);
                _headerDoneAt = _pos;
            }

            DecodeResult<Dpdu> whole = DpduCodec.DecodeFirst(span, edition);
            if (!whole.IsSuccess)
            {
                if (whole.Error == PduErrorKind.Truncated && !endOfBurst)
                {
                    Compact();
                    return;
                }

                // Truncated at the burst end, or invalid after a good header CRC (an ACK-ONLY whose
                // header size does not match its form, C-1022): not a D_PDU to act on. Search on.
                _pos++;
                _headerDoneAt = -1;
                continue;
            }

            // An EXTENSION's count covers its header only; the search resumes after it (C-0365).
            _pos += whole.Consumed;
            _headerDoneAt = -1;
            Deliver(whole.Value!);
        }
    }

    /// <summary>The header effects of [Ed4 C.7.5.1, p.C-110], for every valid header (C-0832, C-0868).</summary>
    private void ApplyHeader(DpduHeaderInfo info)
    {
        DpduHeader h = info.Header;
        if (h.Eot > 0)
        {
            if (_blockMs is int blockMs)
            {
                (long blockStart, long share) = BlockAnchor(blockMs);
                long announced = blockStart + (h.Eot * 500L);
                long bound = announced + share;
                _latestAnnounced = Math.Max(_latestAnnounced ?? announced, announced);
                _earliestBound = Math.Min(_earliestBound ?? bound, bound);
                _mac.EotEstimate(Math.Max(_latestAnnounced.Value, _earliestBound.Value));
            }
            else
            {
                _mac.EotHeard(_context.Now + (h.Eot * 500L));
            }
        }

        _mac.DpduHeard(h.Src, h.Dst, info.GroupAddress);
        _onHeader?.Invoke(info);   // channel analysis (J-0043, J-0070)

        _heardThisBurst.Add(h.Src);
        if (!info.GroupAddress && h.Dst == _context.Config.LocalAddress)
        {
            _toUsThisBurst.Add(h.Src);
        }

        _countsThisBurst.TryGetValue(h.Src, out (long Headers, long Checked, long DataCrc) c);
        _countsThisBurst[h.Src] = (c.Headers + 1, c.Checked, c.DataCrc);
    }

    /// <summary>
    /// For the D_PDU at the search position (interp C-1289): the latest instant the block holding its
    /// first byte can have started on air, its arrival time less one block, and the share of that
    /// block before the byte, rounded up.
    /// </summary>
    private (long BlockStart, long Share) BlockAnchor(int blockMs)
    {
        long offset = _streamBase + _pos;
        foreach ((long start, int length, long t) in _blocks)
        {
            if (offset >= start && offset < start + length)
            {
                long into = offset - start;
                return (t - blockMs, ((into * blockMs) + length - 1) / length);
            }
        }

        throw new InvalidOperationException("a D_PDU starts outside every block received in this burst");
    }

    /// <summary>
    /// A whole D_PDU. A failed data CRC means discard (C-0836): we run no non-ARQ with errors
    /// service, so this applies to every type that carries one (C-0839), except a DROP PDU, which
    /// carries no data and is acknowledged whatever its data CRC (C-0204, C-0217, C-1125). A DATA-ACK
    /// (type 11, or type 2 on an 8-bit link) with a failed data CRC is counted as one, but still goes to the state machine, whose
    /// acknowledgement part rides in the checked header (C-0141, C-1133). The rest go to the state
    /// machine (C-0838).
    /// </summary>
    private void Deliver(Dpdu pdu)
    {
        bool dataOk = pdu switch
        {
            Ed3DataOnly p => p.SegmentCrcOk || p.Flags.DropCPdu,
            Ed3DataAck p => p.SegmentCrcOk || p.Flags.DropCPdu,
            ExpeditedDataOnly p => p.SegmentCrcOk,
            NonArqData p => p.SegmentCrcOk,
            ExpeditedNonArqData p => p.SegmentCrcOk,
            DataOnly p => p.SegmentCrcOk || p.Flags.DropCPdu,
            DataAck p => p.SegmentCrcOk || p.Flags.DropCPdu,
            AckOnly p => p.AckDataCrcOk != false,
            _ => true,
        };

        // The D_PDUs whose data part a CRC-32 checked (a segment, or the ACK data of a long type 10): the
        // denominator of the burst's error rate for rate selection, so that header-only D_PDUs, such as the
        // ACK-ONLY copies that fill a block, do not dilute it (interp C-1270 as revised in corpus 0.35.0).
        bool checkedData = pdu is Ed3DataOnly or Ed3DataAck or ExpeditedDataOnly or NonArqData or ExpeditedNonArqData or DataOnly or DataAck
            || (pdu is AckOnly a && a.AckDataCrcOk is not null);
        if (checkedData)
        {
            uint src = pdu.Header.Src;
            (long headers, long dataChecked, long dataCrc) = _countsThisBurst[src];
            _countsThisBurst[src] = (headers, dataChecked + 1, dataCrc);
        }

        if (!dataOk)
        {
            uint src = pdu.Header.Src;
            (long headers, long dataChecked, long dataCrc) = _countsThisBurst[src];
            _countsThisBurst[src] = (headers, dataChecked, dataCrc + 1);
            if (pdu is not (DataAck or Ed3DataAck))
            {
                return;
            }
        }

        _toStateMachine(pdu);
    }

    private int FindSync(int from)
    {
        for (int i = from; i + 1 < _length; i++)
        {
            if (_buffer[i] == Sync0 && _buffer[i + 1] == Sync1)
            {
                return i;
            }
        }

        return -1;
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        if (_length + bytes.Length > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + bytes.Length));
        }

        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
    }

    /// <summary>Drops the bytes before the search position.</summary>
    private void Compact()
    {
        if (_pos == 0)
        {
            return;
        }

        Array.Copy(_buffer, _pos, _buffer, 0, _length - _pos);
        _length -= _pos;
        _streamBase += _pos;
        if (_headerDoneAt >= 0)
        {
            _headerDoneAt -= _pos;
        }

        _pos = 0;
    }

    private void Reset()
    {
        _length = 0;
        _pos = 0;
        _headerDoneAt = -1;
        _streamBase = 0;
        _blocks.Clear();
    }
}
