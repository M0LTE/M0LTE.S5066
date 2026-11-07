namespace M0LTE.S5066.Core;

/// <summary>
/// Why a decoder rejected its input. These are the corpus error kinds (conformance
/// docs/corpus-format.md section 3.1); a harness compares the kind only. Each codec documents
/// which kinds it can report and in what order it checks.
/// </summary>
public enum PduErrorKind
{
    /// <summary>The input ends before the PDU does.</summary>
    Truncated,

    /// <summary>The PDU does not start with the expected sync or preamble bytes.</summary>
    BadSync,

    /// <summary>The header CRC does not match.</summary>
    HeaderCrc,

    /// <summary>The size-of-header field is impossible for the PDU type.</summary>
    BadHeaderSize,

    /// <summary>The size-of-address field is zero or otherwise impossible.</summary>
    BadAddressSize,

    /// <summary>Bytes remain after the PDU.</summary>
    TrailingBytes,

    /// <summary>A field holds a value the codec cannot represent.</summary>
    InvalidField,
}

/// <summary>The outcome of a decode: either a value or a <see cref="PduErrorKind"/>, never both.</summary>
/// <typeparam name="T">The decoded type.</typeparam>
public readonly struct CodecResult<T> : IEquatable<CodecResult<T>>
    where T : class
{
    private CodecResult(T? value, PduErrorKind error)
    {
        Value = value;
        Error = error;
    }

    /// <summary>True when decoding succeeded and <see cref="Value"/> is set.</summary>
    public bool IsOk => Value is not null;

    /// <summary>The decoded value, or null when decoding failed.</summary>
    public T? Value { get; }

    /// <summary>The error kind; meaningful only when <see cref="IsOk"/> is false.</summary>
    public PduErrorKind Error { get; }

    /// <summary>A successful result.</summary>
    public static CodecResult<T> Ok(T value) => new(value ?? throw new ArgumentNullException(nameof(value)), default);

    /// <summary>A failed result.</summary>
    public static CodecResult<T> Fail(PduErrorKind error) => new(null, error);

    /// <inheritdoc/>
    public bool Equals(CodecResult<T> other) => Equals(Value, other.Value) && (IsOk || Error == other.Error);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CodecResult<T> other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => IsOk ? Value!.GetHashCode() : (int)Error;

    /// <summary>Equality operator.</summary>
    public static bool operator ==(CodecResult<T> left, CodecResult<T> right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(CodecResult<T> left, CodecResult<T> right) => !left.Equals(right);
}
