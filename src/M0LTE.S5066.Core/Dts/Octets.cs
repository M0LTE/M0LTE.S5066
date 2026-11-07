namespace M0LTE.S5066.Core.Dts;

/// <summary>
/// An immutable byte string with value equality, so records that hold segments, acknowledgement
/// bytes or extension bytes compare by content.
/// </summary>
public sealed class Octets : IEquatable<Octets>
{
    private readonly byte[] _bytes;

    /// <summary>The empty byte string.</summary>
    public static Octets Empty { get; } = new(Array.Empty<byte>());

    /// <summary>Copies <paramref name="bytes"/>.</summary>
    public Octets(ReadOnlySpan<byte> bytes) => _bytes = bytes.ToArray();

    /// <summary>The bytes.</summary>
    public ReadOnlySpan<byte> Span => _bytes;

    /// <summary>Number of bytes.</summary>
    public int Length => _bytes.Length;

    /// <inheritdoc/>
    public bool Equals(Octets? other) => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Octets o && Equals(o);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.AddBytes(_bytes);
        return h.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() => Convert.ToHexStringLower(_bytes);
}
