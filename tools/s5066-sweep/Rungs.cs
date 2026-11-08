using System.Globalization;
using M0LTE.HfChannel;
using M0LTE.Ms110d;
using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Sweep;

/// <summary>A (WN, interleaver) pair, named as P7-5 names it: <c>wn6-long</c>.</summary>
internal readonly record struct Rung(int Wn, Interleaver Il) : IComparable<Rung>
{
    public string Name => string.Create(CultureInfo.InvariantCulture, $"wn{Wn}-{InterleaverNames.ToName(Il).Replace("_", "", StringComparison.Ordinal)}");

    public static Rung Parse(string text)
    {
        // wn<N>-<interleaver> or wn<N>:<interleaver>, interleaver as ultrashort, ultra_short, short, medium or long.
        string t = text.Trim().ToLowerInvariant();
        int sep = t.IndexOfAny(['-', ':']);
        if (!t.StartsWith("wn", StringComparison.Ordinal) || sep < 3)
        {
            throw new FormatException($"rung '{text}' is not wn<N>-<interleaver>");
        }

        int wn = int.Parse(t[2..sep], CultureInfo.InvariantCulture);
        string il = t[(sep + 1)..] switch
        {
            "ultrashort" or "ultra_short" or "us" => "ultra_short",
            "short" => "short",
            "medium" => "medium",
            "long" => "long",
            var other => throw new FormatException($"interleaver '{other}' is not ultrashort, short, medium or long"),
        };
        return new Rung(wn, InterleaverNames.Parse(il));
    }

    public int CompareTo(Rung other) => Wn != other.Wn ? Wn.CompareTo(other.Wn) : Il.CompareTo(other.Il);

    public override string ToString() => Name;

    /// <summary>The waveforms M0LTE.Ms110d 0.6.x sends on the 5066 ladder (design shape item 4).</summary>
    public static readonly int[] LadderWns = [1, 2, 3, 4, 5, 6, 7, 8, 13];

    public static readonly Interleaver[] LadderIls = [Interleaver.UltraShort, Interleaver.Short, Interleaver.Medium, Interleaver.Long];

    public static IEnumerable<Rung> All36 => LadderWns.SelectMany(w => LadderIls.Select(i => new Rung(w, i)));

    public static Ms110dTxSettings Settings(int wn, Interleaver il, int k, bool eom) => new()
    {
        WaveformNumber = wn,
        Interleaver = il switch
        {
            Interleaver.UltraShort => Ms110dInterleaverKind.UltraShort,
            Interleaver.Short => Ms110dInterleaverKind.Short,
            Interleaver.Medium => Ms110dInterleaverKind.Medium,
            Interleaver.Long => Ms110dInterleaverKind.Long,
            _ => throw new ArgumentException($"{InterleaverNames.ToName(il)} is not a 110D interleaver (plan 5.3)", nameof(il)),
        },
        ConstraintLength = k,
        AppendEom = eom,
        AppendEot = true,
    };
}

/// <summary>
/// The SNR reference, the same as P7-5's (M0LTE.Ms110d docs/calibration-sweep.md): the set SNR is
/// the on-air signal power over the noise in 3 kHz at the receiving antenna. On air means the
/// transmitter output, or with the SSB band-pass the signal after the sending radio's passband.
/// The reference power is the mean over all 36 rungs of each rung's burst power, and every burst
/// is scaled to it, so every rung meets the set SNR (WN 8's QAM would otherwise sit about 0.8 dB lower).
/// </summary>
internal static class RungPower
{
    public const float Amplitude = 0.5f;

    private static readonly Lazy<(Dictionary<Rung, double> Plain, Dictionary<Rung, double> Ssb)> Powers = new(Measure);

    public static double SignalPower(bool ssb) => (ssb ? Powers.Value.Ssb : Powers.Value.Plain).Values.Average();

    /// <summary>The amplitude scale that brings a burst on <paramref name="r"/> to <see cref="SignalPower"/> on air; 1 for a rung not measured.</summary>
    public static float Gain(bool ssb, Rung r)
    {
        var map = ssb ? Powers.Value.Ssb : Powers.Value.Plain;
        return map.TryGetValue(r, out double p) ? (float)Math.Sqrt(SignalPower(ssb) / p) : 1f;
    }

    private static (Dictionary<Rung, double>, Dictionary<Rung, double>) Measure()
    {
        var plain = new Dictionary<Rung, double>();
        var ssb = new Dictionary<Rung, double>();
        var random = new Random(12345);
        foreach (Rung r in Rung.All36)
        {
            Ms110dTxSettings s = Rung.Settings(r.Wn, r.Il, 7, true) with { Amplitude = Amplitude };
            Ms110dBurstPlan one = Ms110dBurstPlan.ForBlocks(s, 1);
            int blocks = Math.Max(1, (int)Math.Ceiling(4.0 / one.BlockSeconds));
            var bits = new byte[(blocks * one.InputBitsPerBlock) - 32];
            for (int i = 0; i < bits.Length; i++)
            {
                bits[i] = (byte)random.Next(2);
            }

            float[] audio = new Ms110dModulator(s).Modulate(bits);
            plain[r] = MeanSquare(audio);
            ssb[r] = MeanSquare(new SsbFilter(ChannelSpec.SampleRate, ChannelSpec.DefaultPassband).Apply(audio));
        }

        return (plain, ssb);
    }

    private static double MeanSquare(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (float v in x)
        {
            sum += (double)v * v;
        }

        return sum / x.Length;
    }
}

/// <summary>A named channel: AWGN, Poor or Moderate, each with or without the SSB band-pass on both radios.</summary>
internal sealed record ChannelSpec(string Name, WattersonPath[] Paths, bool Ssb)
{
    public const int SampleRate = 9600;

    /// <summary>The research note's transmit figures, -6 dB edges (P7-4 default; Tom's bench sweep replaces it later).</summary>
    public static readonly SsbPassband DefaultPassband = new(400, 2600);

    public static readonly string[] Names = ["awgn", "poor", "moderate", "awgn-ssb", "poor-ssb", "moderate-ssb"];

    public static ChannelSpec Parse(string name)
    {
        string n = name.Trim().ToLowerInvariant();
        bool ssb = n.EndsWith("-ssb", StringComparison.Ordinal);
        string b = ssb ? n[..^4] : n;
        WattersonPath[] paths = b switch
        {
            "awgn" => [],
            "poor" => WattersonChannel.Poor,
            "moderate" => WattersonChannel.Moderate,
            _ => throw new FormatException($"channel '{name}' is not one of {string.Join(", ", Names)}"),
        };
        return new ChannelSpec(n, paths, ssb);
    }

    /// <summary>One direction's continuous realisation at <paramref name="snrDb"/>.</summary>
    public ContinuousWattersonChannel Create(int seed, double snrDb) =>
        new(SampleRate, seed, snrDb, RungPower.SignalPower(Ssb), Paths)
        {
            TransmitPassband = Ssb ? DefaultPassband : null,
            ReceivePassband = Ssb ? DefaultPassband : null,
        };
}

/// <summary>Stable seeds: string.GetHashCode is randomised per process, so it never feeds a seed.</summary>
internal static class Seeds
{
    public static ulong Mix(params object[] parts)
    {
        ulong h = 14695981039346656037UL;
        foreach (object p in parts)
        {
            foreach (char c in Convert.ToString(p, CultureInfo.InvariantCulture) + "|")
            {
                h ^= c;
                h *= 1099511628211UL;
            }
        }

        // One SplitMix64 step to spread the FNV-1a bits.
        h += 0x9E3779B97F4A7C15UL;
        h = (h ^ (h >> 30)) * 0xBF58476D1CE4E5B9UL;
        h = (h ^ (h >> 27)) * 0x94D049BB133111EBUL;
        return h ^ (h >> 31);
    }

    public static int Int(params object[] parts) => (int)(Mix(parts) & 0x7FFFFFFF);
}
