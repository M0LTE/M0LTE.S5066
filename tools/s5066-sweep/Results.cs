using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace M0LTE.S5066.Sweep;

/// <summary>A distribution of millisecond values: count, extremes, mean and a 100 ms histogram, mergeable across runs.</summary>
internal sealed class Dist
{
    public const int BinMs = 100;

    public long N { get; set; }

    public long Min { get; set; }

    public long Max { get; set; }

    public double Sum { get; set; }

    public double Mean => N == 0 ? 0 : Sum / N;

    /// <summary>Histogram: lower edge of each 100 ms bin (floor) to count.</summary>
    public SortedDictionary<long, long> Hist { get; set; } = [];

    public void Add(long v)
    {
        Min = N == 0 ? v : Math.Min(Min, v);
        Max = N == 0 ? v : Math.Max(Max, v);
        N++;
        Sum += v;
        long bin = (long)Math.Floor(v / (double)BinMs) * BinMs;
        Hist[bin] = Hist.GetValueOrDefault(bin) + 1;
    }

    public void Merge(Dist o)
    {
        if (o.N == 0)
        {
            return;
        }

        Min = N == 0 ? o.Min : Math.Min(Min, o.Min);
        Max = N == 0 ? o.Max : Math.Max(Max, o.Max);
        N += o.N;
        Sum += o.Sum;
        foreach ((long k, long c) in o.Hist)
        {
            Hist[k] = Hist.GetValueOrDefault(k) + c;
        }
    }

    /// <summary>The p-quantile (0 to 1), as the middle of its 100 ms bin; 0 when empty.</summary>
    public double Quantile(double p)
    {
        if (N == 0)
        {
            return 0;
        }

        long target = (long)Math.Ceiling(p * N);
        long seen = 0;
        foreach ((long k, long c) in Hist)
        {
            seen += c;
            if (seen >= Math.Max(1, target))
            {
                return k + (BinMs / 2.0);
            }
        }

        return Max;
    }

    public string Summary() => N == 0 ? "none" : string.Create(CultureInfo.InvariantCulture,
        $"n {N}, min {Min}, p10 {Quantile(0.1):0}, median {Quantile(0.5):0}, mean {Mean:0}, p90 {Quantile(0.9):0}, max {Max} ms");
}

/// <summary>Transmissions of one node on one rung, and what the far node made of them.</summary>
internal sealed class RungStats
{
    public long Count { get; set; }

    public long AirtimeMs { get; set; }

    public long Bytes { get; set; }

    public long Dpdus { get; set; }

    public long DpdusOk { get; set; }

    public long DataDpdus { get; set; }

    public long RetransmittedDpdus { get; set; }

    public long AckOnlyTransmissions { get; set; }

    /// <summary>clean, partial, failed, missed, collided.</summary>
    public SortedDictionary<string, long> Outcomes { get; set; } = new(StringComparer.Ordinal);

    public long SnrCount { get; set; }

    public long SnrSumTenths { get; set; }

    public void Merge(RungStats o)
    {
        Count += o.Count;
        AirtimeMs += o.AirtimeMs;
        Bytes += o.Bytes;
        Dpdus += o.Dpdus;
        DpdusOk += o.DpdusOk;
        DataDpdus += o.DataDpdus;
        RetransmittedDpdus += o.RetransmittedDpdus;
        AckOnlyTransmissions += o.AckOnlyTransmissions;
        SnrCount += o.SnrCount;
        SnrSumTenths += o.SnrSumTenths;
        foreach ((string k, long c) in o.Outcomes)
        {
            Outcomes[k] = Outcomes.GetValueOrDefault(k) + c;
        }
    }
}

internal sealed class NodeStats
{
    public SortedDictionary<string, RungStats> Rungs { get; set; } = new(StringComparer.Ordinal);

    public long Transmissions { get; set; }

    public long AckOnlyTransmissions { get; set; }

    public long DataDpdus { get; set; }

    public long RetransmittedDpdus { get; set; }

    public long LinksMade { get; set; }

    /// <summary>A link that went from linked to breaking or unlinked without a clean close.</summary>
    public long LinksLost { get; set; }

    public long LinksClosed { get; set; }

    public long SisRejected { get; set; }
}

internal sealed class ChatStats
{
    public long Sent { get; set; }

    public long Delivered { get; set; }

    public Dist LatencyMs { get; set; } = new();
}

/// <summary>One run's result (P7-8): one JSON file per run.</summary>
internal sealed class RunResult
{
    public string Id { get; set; } = "";

    public string Mode { get; set; } = "virtual";

    public string Channel { get; set; } = "";

    public int SnrTenthDb { get; set; }

    public string Config { get; set; } = "";

    public int Seed { get; set; }

    public long DurationMs { get; set; }

    public long WarmupMs { get; set; }

    public SortedDictionary<string, string> Versions { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The node settings both nodes ran with, as merged (drc.table summarised).</summary>
    public JsonElement? NodeConfig { get; set; }

    public long OfferedBytes { get; set; }

    public long DeliveredBytes { get; set; }

    public long DeliveredBytesWindow { get; set; }

    /// <summary>Bulk bytes delivered in the window (warm-up to end), in bit/s.</summary>
    public double GoodputBps { get; set; }

    public long BulkDuplicates { get; set; }

    public long BulkCorrupt { get; set; }

    public ChatStats Chat { get; set; } = new();

    /// <summary>a is the bulk sender (0.0.1.1), b the bulk receiver and chat sender (0.0.1.2).</summary>
    public SortedDictionary<string, NodeStats> Nodes { get; set; } = new(StringComparer.Ordinal);

    public double RetransmissionRate { get; set; }

    public long LinksLost { get; set; }

    /// <summary>The far node's predicted end (EOT) less the true end of the sender's transmission, per rung (issue #66).</summary>
    public SortedDictionary<string, Dist> EotOvershootMs { get; set; } = new(StringComparer.Ordinal);

    /// <summary>End of a node's transmission to the start of its next one (the gap P7-5 assumed to be 6 s), per rung of the first.</summary>
    public SortedDictionary<string, Dist> SameSenderGapMs { get; set; } = new(StringComparer.Ordinal);

    /// <summary>End of a transmission to the start of the other node's next one (negative: they overlapped).</summary>
    public Dist ExchangeGapMs { get; set; } = new();

    public double WallSeconds { get; set; }

    /// <summary>Wall seconds of one core per second of channel time.</summary>
    public double CoreSecondsPerSignalSecond { get; set; }

    public string? Error { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static RunResult FromJson(string json) => JsonSerializer.Deserialize<RunResult>(json, Json)
        ?? throw new InvalidDataException("empty result");
}
