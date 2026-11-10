using M0LTE.Ms110d;
using M0LTE.S5066.Core;
using M0LTE.S5066.Core.Dts;

namespace M0LTE.S5066.TestClient.Monitor;

/// <summary>A burst the demodulator locked to (plan 11.6 part 2).</summary>
/// <param name="Number">1 for the first burst heard, then counting up.</param>
/// <param name="StartSample">The first preamble sample matched, 9600 Hz samples from the start of the audio.</param>
/// <param name="Wn">Waveform number from the preamble.</param>
/// <param name="Interleaver">Interleaver from the preamble: US, S, M or L.</param>
/// <param name="K">Constraint length from the preamble.</param>
/// <param name="CfoHz">Carrier frequency offset at lock.</param>
/// <param name="PreambleSnrDb">SNR from the preamble, or null.</param>
internal sealed record MonitorBurst(int Number, long StartSample, int Wn, string Interleaver, int K, double CfoHz, double? PreambleSnrDb)
{
    /// <summary>The waveform as an operator reads it, for example <c>WN6/S</c>.</summary>
    public string Waveform => $"WN{Wn}/{Interleaver}";
}

/// <summary>One D_PDU heard, whole or with a good header only.</summary>
/// <param name="Burst">The burst it was in.</param>
/// <param name="Sample">End of the block that completed it, in 9600 Hz samples from the start of the audio.</param>
/// <param name="SnrDb">The modem's SNR over that block, or null.</param>
/// <param name="Header">The header, which passed its CRC.</param>
/// <param name="Pdu">The whole D_PDU, or null when only its header could be read (<see cref="Status"/> says why).</param>
/// <param name="Bytes">Bytes on the air, sync to the last CRC; for a D_PDU cut short, the bytes there were.</param>
/// <param name="Status">ok, bad (data CRC failed), cut (the burst ended inside it) or invalid (a good header CRC over an impossible D_PDU).</param>
/// <param name="Raw">The bytes, for a test to compare with what was sent.</param>
internal sealed record MonitorDpdu(MonitorBurst Burst, long Sample, double? SnrDb, DpduHeaderInfo Header, Dpdu? Pdu, int Bytes, string Status, byte[] Raw);

/// <summary>What a burst held, once it ended.</summary>
/// <param name="Burst">The burst.</param>
/// <param name="EndSample">The end of its last decoded block, or where the receiver let go of it.</param>
/// <param name="Reason">eom, blocks, terminated, lost, absent or new-preamble.</param>
/// <param name="Blocks">Blocks decoded.</param>
/// <param name="SnrDb">The modem's SNR over the burst, or null.</param>
/// <param name="Dpdus">D_PDUs with a good header.</param>
/// <param name="DataChecked">D_PDUs among them whose data a CRC-32 checked.</param>
/// <param name="DataBad">Those whose data CRC failed.</param>
/// <param name="Cut">D_PDUs cut short by the burst end, or invalid after a good header.</param>
/// <param name="BadHeaders">Syncs whose header failed its checks: noise, or a D_PDU lost to errors.</param>
internal sealed record MonitorBurstEnd(MonitorBurst Burst, long EndSample, string Reason, int Blocks, double? SnrDb, int Dpdus, int DataChecked, int DataBad, int Cut, int BadHeaders);

/// <summary>Everything heard from one source to one destination.</summary>
/// <param name="Src">Source address.</param>
/// <param name="Dst">Destination address.</param>
/// <param name="Group">The destination is a group (types 7 and 8 only).</param>
internal sealed record MonitorLinkKey(uint Src, uint Dst, bool Group);

/// <summary>Running totals for one <see cref="MonitorLinkKey"/>.</summary>
internal sealed class MonitorLink
{
    public int Bursts { get; set; }

    public int Dpdus { get; set; }

    public int Data { get; set; }

    public int DataBad { get; set; }

    public int Cut { get; set; }

    public long DataBytes { get; set; }

    public int Acks { get; set; }

    public long FirstSample { get; set; }

    public long LastSample { get; set; }

    public string Waveform { get; set; } = "";

    public double? SnrDb { get; set; }
}

/// <summary>Where the monitor's records go.</summary>
internal interface IMonitorSink
{
    void BurstStarted(MonitorBurst burst);

    void Dpdu(MonitorDpdu dpdu);

    void BurstEnded(MonitorBurstEnd end);
}

/// <summary>
/// The receive-only 5066 monitor (plan 11.6 part 2): audio in, through its own M0LTE.Ms110d
/// demodulator, and every D_PDU out, decoded by the core's codecs. It never transmits and shares
/// nothing with any node: no peer table, so every D_PDU is read with <see cref="PeerEdition.Any"/>
/// (types 9 to 14 as Ed4).
/// </summary>
/// <remarks>
/// The D_PDU search follows the node's receiver (core-architecture.md 4.3): the bytes of one burst
/// are one stream, searched for the sync <c>90 EB</c> across block boundaries; a candidate whose
/// header fails a check moves the search on by one byte; one that needs more bytes waits for the
/// next block, and at the burst end it is reported as cut and the search moves on by one byte.
/// Not thread safe: feed it from one thread.
/// </remarks>
internal sealed class MonitorDecoder
{
    private const byte Sync0 = 0x90;
    private const byte Sync1 = 0xEB;

    private readonly IMonitorSink _sink;
    private readonly Ms110dDemodulator _demod;
    private readonly SortedDictionary<MonitorLinkKey, MonitorLink> _links = new(Comparer<MonitorLinkKey>.Create(Compare));
    private readonly HashSet<MonitorLinkKey> _linksThisBurst = [];

    private MonitorBurst? _burst;
    private int _bursts;
    private byte[] _buffer = new byte[512];
    private int _length;
    private int _pos;
    private long _blockSample;
    private double? _blockSnr;
    private int _dpdus;
    private int _dataChecked;
    private int _dataBad;
    private int _cut;
    private int _badHeaders;

    /// <summary>Creates the monitor.</summary>
    /// <param name="sink">Where records go; called on the thread that calls <see cref="Process"/>.</param>
    public MonitorDecoder(IMonitorSink sink)
    {
        _sink = sink;
        _demod = new Ms110dDemodulator(new Ms110dDemodOptions());
        _demod.BurstStarted += OnBurstStarted;
        _demod.BlockDecoded += OnBlockDecoded;
        _demod.BurstCompleted += OnBurstCompleted;
    }

    /// <summary>Audio samples at 9600 Hz taken so far: the clock every sample number counts in.</summary>
    public long SamplesProcessed => _demod.SamplesProcessed;

    /// <summary>Totals per source and destination, in address order.</summary>
    public IReadOnlyDictionary<MonitorLinkKey, MonitorLink> Links => _links;

    /// <summary>Bursts heard so far.</summary>
    public int Bursts => _bursts;

    /// <summary>Feeds received audio at 9600 Hz.</summary>
    public void Process(ReadOnlySpan<float> samples) => _demod.Process(samples);

    /// <summary>
    /// The end of the audio: two seconds of silence let a burst still being received end by
    /// itself, and one still locked after that is ended as terminated.
    /// </summary>
    public void Finish()
    {
        var silence = new float[192];
        for (int i = 0; i < 100 && _burst is not null; i++)
        {
            _demod.Process(silence);
        }

        if (_burst is not null)
        {
            _demod.Terminate();
        }
    }

    /// <summary>Packs 0/1 bits into bytes, least significant bit first, as the node's modem port does (C-0150).</summary>
    internal static byte[] ToBytes(ReadOnlySpan<byte> bits)
    {
        byte[] bytes = new byte[bits.Length / 8];
        for (int i = 0; i < bytes.Length; i++)
        {
            int v = 0;
            for (int b = 0; b < 8; b++)
            {
                v |= (bits[(i * 8) + b] & 1) << b;
            }

            bytes[i] = (byte)v;
        }

        return bytes;
    }

    /// <summary>The 110D interleaver as one or two letters.</summary>
    internal static string InterleaverName(Ms110dInterleaverKind kind) => kind switch
    {
        Ms110dInterleaverKind.UltraShort => "US",
        Ms110dInterleaverKind.Short => "S",
        Ms110dInterleaverKind.Medium => "M",
        _ => "L",
    };

    private static int Compare(MonitorLinkKey a, MonitorLinkKey b)
    {
        int c = a.Src.CompareTo(b.Src);
        if (c == 0)
        {
            c = a.Dst.CompareTo(b.Dst);
        }

        return c != 0 ? c : a.Group.CompareTo(b.Group);
    }

    private void OnBurstStarted(Ms110dBurstStart start)
    {
        if (_burst is not null)
        {
            // Should not happen (a new preamble ends the old burst first), but never lose one.
            EndBurst("new-preamble", 0, null, start.FirstSeenSample);
        }

        Ms110dLockInfo l = start.Lock;
        _burst = new MonitorBurst(++_bursts, start.FirstSeenSample, l.WaveformNumber, InterleaverName(l.Interleaver), l.ConstraintLength, l.CfoHz, start.PreambleSnrDb);
        _length = 0;
        _pos = 0;
        _dpdus = _dataChecked = _dataBad = _cut = _badHeaders = 0;
        _blockSample = start.LockSample;
        _blockSnr = start.PreambleSnrDb;
        _linksThisBurst.Clear();
        _sink.BurstStarted(_burst);
    }

    private void OnBlockDecoded(Ms110dRxBlock block)
    {
        if (_burst is null)
        {
            return;
        }

        _blockSample = block.EndSample;
        _blockSnr = block.SnrDb ?? _blockSnr;
        byte[] bytes = ToBytes(block.Bits);
        if (_length + bytes.Length > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + bytes.Length));
        }

        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
        Scan(endOfBurst: false);
    }

    private void OnBurstCompleted(Ms110dBurst burst)
    {
        if (_burst is null)
        {
            return;
        }

        string reason = burst.Reason switch
        {
            Ms110dBurstEndReason.Eom => "eom",
            Ms110dBurstEndReason.MaxInputDataBlocks => "blocks",
            Ms110dBurstEndReason.Terminated => "terminated",
            Ms110dBurstEndReason.SignalAbsent => "absent",
            Ms110dBurstEndReason.NewPreamble => "new-preamble",
            _ => "lost",
        };
        EndBurst(reason, burst.Blocks, burst.SnrDb, burst.LastFrameEndSample ?? burst.EndSample);
    }

    private void EndBurst(string reason, int blocks, double? snrDb, long endSample)
    {
        Scan(endOfBurst: true);
        MonitorBurst b = _burst!;
        foreach (MonitorLinkKey key in _linksThisBurst)
        {
            MonitorLink link = _links[key];
            link.Bursts++;
            link.SnrDb = snrDb ?? link.SnrDb;
        }

        _burst = null;
        _length = 0;
        _pos = 0;
        _sink.BurstEnded(new MonitorBurstEnd(b, endSample, reason, blocks, snrDb, _dpdus, _dataChecked, _dataBad, _cut, _badHeaders));
    }

    private void Scan(bool endOfBurst)
    {
        while (true)
        {
            int sync = FindSync(_pos);
            if (sync < 0)
            {
                // Keep a trailing 90: it may be the first byte of a sync split across blocks.
                _pos = _length > _pos && _buffer[_length - 1] == Sync0 ? _length - 1 : _length;
                Compact();
                return;
            }

            _pos = sync;
            ReadOnlySpan<byte> span = _buffer.AsSpan(_pos, _length - _pos);
            DecodeResult<DpduHeaderInfo> header = DpduCodec.DecodeHeader(span, PeerEdition.Any);
            if (!header.IsSuccess)
            {
                if (header.Error == PduErrorKind.Truncated && !endOfBurst)
                {
                    Compact();
                    return;
                }

                if (header.Error != PduErrorKind.Truncated)
                {
                    _badHeaders++;
                }

                _pos++;
                continue;
            }

            DecodeResult<Dpdu> whole = DpduCodec.DecodeFirst(span, PeerEdition.Any);
            if (!whole.IsSuccess)
            {
                if (whole.Error == PduErrorKind.Truncated && !endOfBurst)
                {
                    Compact();
                    return;
                }

                bool cut = whole.Error == PduErrorKind.Truncated;
                int have = cut ? span.Length : header.Consumed;
                Emit(header.Value!, null, have, cut ? "cut" : "invalid", span[..have]);
                _pos++;
                continue;
            }

            // An EXTENSION's count covers its header only; the search resumes after it (C-0365).
            Dpdu pdu = whole.Value!;
            bool? dataOk = DataCrc(pdu);
            Emit(header.Value!, pdu, whole.Consumed, dataOk == false ? "bad" : "ok", span[..whole.Consumed]);
            _pos += whole.Consumed;
        }
    }

    /// <summary>Whether a D_PDU's data CRC-32 passed, or null for a D_PDU without one.</summary>
    internal static bool? DataCrc(Dpdu pdu) => pdu switch
    {
        Ed3DataOnly p => p.SegmentCrcOk,
        Ed3DataAck p => p.SegmentCrcOk,
        ExpeditedDataOnly p => p.SegmentCrcOk,
        NonArqData p => p.SegmentCrcOk,
        ExpeditedNonArqData p => p.SegmentCrcOk,
        DataOnly p => p.SegmentCrcOk,
        DataAck p => p.SegmentCrcOk,
        AckOnly p => p.AckDataCrcOk,
        _ => null,
    };

    /// <summary>Data bytes a D_PDU carries: a segment, long ACK-ONLY data, or extended management bytes.</summary>
    internal static int DataLength(Dpdu pdu) => pdu switch
    {
        Ed3DataOnly p => p.Segment.Length,
        Ed3DataAck p => p.Segment.Length,
        ExpeditedDataOnly p => p.Segment.Length,
        NonArqData p => p.Segment.Length,
        ExpeditedNonArqData p => p.Segment.Length,
        DataOnly p => p.Segment.Length,
        DataAck p => p.Segment.Length,
        AckOnly p => p.AckDataCrcOk is null ? 0 : p.AckData.Length,
        Management p => p.ExtendedData.Length,
        _ => 0,
    };

    private void Emit(DpduHeaderInfo info, Dpdu? pdu, int bytes, string status, ReadOnlySpan<byte> raw)
    {
        MonitorBurst b = _burst!;
        _dpdus++;
        bool? dataOk = pdu is null ? null : DataCrc(pdu);
        if (dataOk is not null)
        {
            _dataChecked++;
            if (dataOk == false)
            {
                _dataBad++;
            }
        }

        if (pdu is null)
        {
            _cut++;
        }

        var key = new MonitorLinkKey(info.Header.Src, info.Header.Dst, info.GroupAddress);
        if (!_links.TryGetValue(key, out MonitorLink? link))
        {
            link = new MonitorLink { FirstSample = _blockSample };
            _links[key] = link;
        }

        _linksThisBurst.Add(key);
        link.Dpdus++;
        link.LastSample = _blockSample;
        link.Waveform = b.Waveform;
        if (pdu is null)
        {
            link.Cut++;
        }
        else if (dataOk is not null && pdu is not AckOnly)
        {
            link.Data++;
            if (dataOk == false)
            {
                link.DataBad++;
            }
            else
            {
                link.DataBytes += DataLength(pdu);
            }
        }

        if (pdu is Ed3AckOnly or ExpeditedAckOnly or AckOnly or DataAck or Ed3DataAck)
        {
            link.Acks++;
        }

        _sink.Dpdu(new MonitorDpdu(b, _blockSample, _blockSnr, info, pdu, bytes, status, raw.ToArray()));
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

    private void Compact()
    {
        if (_pos == 0)
        {
            return;
        }

        Array.Copy(_buffer, _pos, _buffer, 0, _length - _pos);
        _length -= _pos;
        _pos = 0;
    }
}
