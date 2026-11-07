namespace M0LTE.S5066.Core;

/// <summary>
/// Modular sequence arithmetic for FSNs and window edges, through one helper so no code does ad hoc
/// <c>%</c> on sequence numbers (core-architecture.md 6 rule 2). The modulus is 256 for the 8-bit
/// family and 65536 for the 16-bit family. "Greater" and "less" are on the circular space: a pair of
/// bounds is an arc measured forward from its lower bound (C-0786).
/// </summary>
public readonly record struct SeqSpace
{
    private SeqSpace(int modulus) => Modulus = modulus;

    /// <summary>The 8-bit family, modulus 256.</summary>
    public static SeqSpace Mod256 { get; } = new(256);

    /// <summary>The 16-bit family, modulus 65536.</summary>
    public static SeqSpace Mod65536 { get; } = new(65536);

    /// <summary>256 or 65536.</summary>
    public int Modulus { get; }

    /// <summary><paramref name="value"/> + <paramref name="n"/> on the circle; <paramref name="n"/> may be negative.</summary>
    public int Add(int value, int n)
    {
        Check(value);
        // Non-negative operands only before the remainder, so C# and C++ truncation agree (rule 1).
        long r = n >= 0 ? n % Modulus : Modulus - ((-(long)n) % Modulus);
        return (int)((value + r) % Modulus);
    }

    /// <summary>The forward distance from <paramref name="from"/> to <paramref name="to"/>, 0 to Modulus - 1.</summary>
    public int Distance(int from, int to)
    {
        Check(from);
        Check(to);
        return to >= from ? to - from : to + Modulus - from;
    }

    /// <summary>
    /// The arc test of C-0786: true when <paramref name="value"/> lies on the arc from
    /// <paramref name="lower"/> forward to <paramref name="upper"/>, that is, its forward distance
    /// from <paramref name="lower"/> is no more than the arc length; with
    /// <paramref name="lowerExclusive"/> it must also be more than 0. Special cases of particular
    /// equations (Equation 5 with nothing outstanding) belong to the caller.
    /// </summary>
    public bool InArc(int value, int lower, int upper, bool lowerExclusive = false)
    {
        int d = Distance(lower, value);
        return d <= Distance(lower, upper) && (!lowerExclusive || d > 0);
    }

    private void Check(int v)
    {
        if (Modulus == 0)
        {
            throw new InvalidOperationException("use SeqSpace.Mod256 or SeqSpace.Mod65536");
        }

        if (v < 0 || v >= Modulus)
        {
            throw new ArgumentOutOfRangeException(nameof(v), v, $"sequence number outside 0 to {Modulus - 1}");
        }
    }
}
