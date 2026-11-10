using M0LTE.Ms110d;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Sweep;

/// <summary>One D_PDU as laid in a transmission, from the sender's bytes.</summary>
internal readonly record struct SentDpdu(int Offset, int Length, int HeaderLength, int Eot, bool Data, bool Retransmission, bool AckOnly, bool Padding, string Tag = "");

/// <summary>What happened to one transmission: the sender's view and the far node's.</summary>
internal sealed class TxRecord
{
    public required int Node { get; init; }

    public required Rung Rung { get; init; }

    public required byte[] Bytes { get; init; }

    public required long StartMs { get; init; }

    /// <summary>The sending node had set a failed-trial ceiling before this transmission (open question 14).</summary>
    public bool AfterFirstCeiling { get; init; }

    /// <summary>When the last sample went into the channel, ms (exact, not chunk-rounded).</summary>
    public required long EndMs { get; init; }

    /// <summary>Every D_PDU laid in the transmission, PADDING included.</summary>
    public required List<SentDpdu> AllDpdus { get; init; }

    /// <summary>The D_PDUs that count in the statistics: all but PADDING, which only fills a block (C-0872).</summary>
    public IEnumerable<SentDpdu> Dpdus => AllDpdus.Where(d => !d.Padding);

    public int DpduCount => AllDpdus.Count(d => !d.Padding);

    public bool HasData => AllDpdus.Exists(d => d.Data);

    public bool AckOnlyTransmission => !HasData && AllDpdus.Exists(d => d.AckOnly);

    // The far node's view.
    public bool Collided { get; set; }

    public bool Locked { get; set; }

    public int ExtraLocks { get; set; }

    public List<(long Time, byte[] Bytes)> Blocks { get; } = [];

    public BurstEndReason? EndReason { get; set; }

    public int? SnrTenthDb { get; set; }

    public int DpdusOk { get; private set; }

    /// <summary>Per D_PDU (PADDING excluded), its tag and whether the far node got it right, for the detailed timeline.</summary>
    public string Detail { get; private set; } = "";

    public long? PredictedEndMs { get; private set; }

    /// <summary>clean, partial, failed (locked, no D_PDU right), missed (never locked) or collided (the far node keyed during it).</summary>
    public string Outcome { get; private set; } = "missed";

    /// <summary>
    /// Scores the far node's reception against the sent bytes: a D_PDU is right when every one of
    /// its bytes arrived right. The far node's predicted end is computed as its core computes it with
    /// the block duration the modem gives (DtsReceiver, interp C-1289): each header's block started by
    /// its arrival less one block; the estimate is the later of the latest end so announced and the
    /// earliest bound that adds the D_PDU's share of its block.
    /// </summary>
    public void Score()
    {
        byte[] rx = Blocks.SelectMany(b => b.Bytes).ToArray();
        int ok = 0;
        var detail = new List<string>();
        long? predicted = null;
        long? latest = null;
        long? earliest = null;
        foreach (SentDpdu d in AllDpdus)
        {
            if (!d.Padding && rx.Length >= d.Offset + d.Length && rx.AsSpan(d.Offset, d.Length).SequenceEqual(Bytes.AsSpan(d.Offset, d.Length)))
            {
                ok++;
                if (!d.Padding)
                {
                    detail.Add(d.Tag);
                }
            }
            else if (!d.Padding)
            {
                detail.Add(d.Tag + "x");
            }

            int headerEnd = d.Offset + d.HeaderLength;
            if (d.Eot > 0 && rx.Length >= headerEnd && rx.AsSpan(d.Offset, d.HeaderLength).SequenceEqual(Bytes.AsSpan(d.Offset, d.HeaderLength)))
            {
                (long blockStart, long share) = Anchor(d.Offset);
                long announced = blockStart + (d.Eot * 500L);
                latest = Math.Max(latest ?? announced, announced);
                earliest = Math.Min(earliest ?? (announced + share), announced + share);
                predicted = Math.Max(latest.Value, earliest.Value);   // as DtsReceiver with block_ms (interp C-1289)
            }
        }

        DpdusOk = ok;
        Detail = string.Join(' ', detail);
        PredictedEndMs = predicted;
        Outcome = Collided ? "collided"
            : !Locked ? "missed"
            : ok == DpduCount && DpduCount > 0 ? "clean"
            : ok > 0 ? "partial"
            : "failed";
    }

    /// <summary>The receiver's EOT anchor for a D_PDU starting at this byte, as DtsReceiver computes it with block_ms (interp C-1289).</summary>
    private (long BlockStart, long Share) Anchor(int byteIndex)
    {
        Ms110dInterleaverKind kind = Rung.Il switch
        {
            Interleaver.UltraShort => Ms110dInterleaverKind.UltraShort,
            Interleaver.Short => Ms110dInterleaverKind.Short,
            Interleaver.Medium => Ms110dInterleaverKind.Medium,
            _ => Ms110dInterleaverKind.Long,
        };
        int blockMs = M0LTE.S5066.Host.Modem.Ms110dHostModem.ReceivedBlockMs(new Ms110dLockInfo(Rung.Wn, kind, 7, 0))!.Value;
        int seen = 0;
        foreach ((long time, byte[] bytes) in Blocks)
        {
            if (byteIndex < seen + bytes.Length)
            {
                long into = byteIndex - seen;
                return (time - blockMs, ((into * blockMs) + bytes.Length - 1) / bytes.Length);
            }

            seen += bytes.Length;
        }

        return (Blocks[^1].Time, 0);
    }

    /// <summary>
    /// The rate selection EOWs in the transmission's headers, for the timeline: the last MAX-SPEED and
    /// LOW-SPEED it carried, as " max wn8-long low wn13-long", or empty when it carried neither.
    /// </summary>
    public string Recommendations()
    {
        string? max = null;
        string? low = null;
        foreach (SentDpdu d in AllDpdus)
        {
            DecodeResult<DpduHeaderInfo> h = DpduCodec.DecodeHeader(Bytes.AsSpan(d.Offset, d.HeaderLength), PeerEdition.Ed4);
            if (!h.IsSuccess)
            {
                continue;
            }

            switch (EowCodec.Decode(h.Value!.Header.Eow))
            {
                case MaxSpeedEow m:
                    max = Name(m.Value);
                    break;
                case LowSpeedEow l:
                    low = Name(l.Value);
                    break;
            }
        }

        return (max is null ? "" : " max " + max) + (low is null ? "" : " low " + low);

        static string Name(SpeedInterleaver v) => "wn" + v.Speed.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-"
            + (v.Interleaver switch { 1 => "ultrashort", 2 => "veryshort", 3 => "short", 4 => "medium", 5 => "long", _ => "il" + v.Interleaver.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }

    /// <summary>Lays the sender's bytes out as D_PDUs, as a receiver would find them from the start.</summary>
    public static List<SentDpdu> Parse(ReadOnlySpan<byte> bytes, Func<Dpdu, bool> isRetransmission)
    {
        var list = new List<SentDpdu>();
        int pos = 0;
        while (pos < bytes.Length)
        {
            DecodeResult<Dpdu> whole = DpduCodec.DecodeFirst(bytes[pos..], PeerEdition.Ed4);
            DecodeResult<DpduHeaderInfo> header = DpduCodec.DecodeHeader(bytes[pos..], PeerEdition.Ed4);
            if (!whole.IsSuccess || !header.IsSuccess || whole.Consumed <= 0)
            {
                break;   // fill after the last D_PDU, or something we do not lay out
            }

            Dpdu p = whole.Value!;
            bool data = p is DataOnly or DataAck or ExpeditedDataOnly or NonArqData or ExpeditedNonArqData;
            string tag = p switch
            {
                DataOnly d => $"D{d.TxFsn}:{d.Segment.Length}{(d.Flags.CPduStart ? "s" : "")}{(d.Flags.CPduEnd ? "e" : "")}",
                DataAck d => $"DA{d.TxFsn}:{d.Segment.Length}{(d.Flags.CPduStart ? "s" : "")}{(d.Flags.CPduEnd ? "e" : "")}",
                AckOnly a => $"A{a.RxLwe}",
                Padding => "P",
                _ => p.GetType().Name,
            };
            list.Add(new SentDpdu(pos, whole.Consumed, header.Consumed, p.Header.Eot, data, data && isRetransmission(p), p is AckOnly or ExpeditedAckOnly, p is Padding, tag));
            pos += whole.Consumed;
        }

        return list;
    }
}

/// <summary>Tells retransmitted data D_PDUs from new ones: the same link, sequence number and segment bytes as one sent before.</summary>
internal sealed class RetransmissionTracker
{
    private readonly Dictionary<(uint Src, uint Dst, int Kind, int Fsn), ulong> _last = [];

    public bool IsRetransmission(Dpdu p)
    {
        (int kind, int fsn, Octets seg) = p switch
        {
            DataOnly d => (1, d.TxFsn, d.Segment),
            DataAck d => (1, d.TxFsn, d.Segment),
            ExpeditedDataOnly d => (2, d.TxFsn, d.Segment),
            _ => (0, 0, Octets.Empty),
        };
        if (kind == 0)
        {
            return false;
        }

        ulong h = 14695981039346656037UL;
        foreach (byte b in seg.Span)
        {
            h = (h ^ b) * 1099511628211UL;
        }

        var key = (p.Header.Src, p.Header.Dst, kind, fsn);
        bool again = _last.TryGetValue(key, out ulong prev) && prev == h;
        _last[key] = h;
        return again;
    }
}
