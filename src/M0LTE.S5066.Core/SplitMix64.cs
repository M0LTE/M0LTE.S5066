namespace M0LTE.S5066.Core;

/// <summary>
/// The one pseudo-random generator the core uses, SplitMix64, as specified in the conformance
/// repository's docs/prng.md so that the C# and C++ cores draw identical sequences from the same
/// seed. The standard names no generator, only the range of the CSMA jitter draw
/// [Ed4 K.3.2.3, p.K-10], so the choice is ours (an interpretation, not a requirement row).
/// </summary>
/// <remarks>
/// One instance per core, created with the core and never re-seeded. The core draws only at the
/// draw sites registered in prng.md section 6, and only through <see cref="Uniform"/>.
/// </remarks>
public sealed class SplitMix64
{
    private ulong _state;

    /// <summary>Creates a generator whose initial state is the seed itself; every seed, including 0, is valid.</summary>
    public SplitMix64(ulong seed) => _state = seed;

    /// <summary>The current state.</summary>
    public ulong State => _state;

    /// <summary>The number of <see cref="NextUInt64"/> calls made so far, rejected draws included.</summary>
    public long Draws { get; private set; }

    /// <summary>The next 64-bit value.</summary>
    public ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            Draws++;
            return z ^ (z >> 31);
        }
    }

    /// <summary>
    /// A uniform integer in [0, n) for 1 &lt;= n &lt;= 2^64 - 1, without modulo bias, by rejection.
    /// Always draws at least once, even for n = 1, and a rejected draw is consumed.
    /// </summary>
    public ulong Uniform(ulong n)
    {
        ArgumentOutOfRangeException.ThrowIfZero(n);
        ulong threshold = unchecked(0UL - n) % n;
        while (true)
        {
            ulong x = NextUInt64();
            if (x >= threshold)
            {
                return x % n;
            }
        }
    }
}
