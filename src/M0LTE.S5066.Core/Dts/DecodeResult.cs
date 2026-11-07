namespace M0LTE.S5066.Core.Dts;

/// <summary>Which edition's D_PDU type table a decoder applies (corpus-format.md section 3.3).</summary>
public enum PeerEdition
{
    /// <summary>Types 0 to 8 and 15, which mean the same in both editions. Types 9 to 14 are read as Ed4.</summary>
    Any,

    /// <summary>An Edition 3 peer: types 9 to 14 are undefined (C-0059 to C-0062, C-1021).</summary>
    Ed3,

    /// <summary>An Edition 4 peer.</summary>
    Ed4,
}

/// <summary>The error kinds of corpus-format.md section 3.1, in the corpus vocabulary.</summary>
public enum DecodeErrorKind
{
    /// <summary>The input ends before the PDU does.</summary>
    Truncated,

    /// <summary>The PDU does not start with the sync bytes 90 EB (C-0083, C-0084).</summary>
    BadSync,

    /// <summary>The header CRC does not match (C-0137, C-0138).</summary>
    HeaderCrc,

    /// <summary>The size-of-header field is impossible for the type.</summary>
    BadHeaderSize,

    /// <summary>The size-of-address field is zero or otherwise impossible (C-0110).</summary>
    BadAddressSize,

    /// <summary>Bytes remain after the PDU in a whole-buffer decode.</summary>
    TrailingBytes,

    /// <summary>A field holds a value the codec cannot represent.</summary>
    InvalidField,
}

/// <summary>Either a decoded value or a typed error, plus how many bytes the PDU used.</summary>
/// <typeparam name="T">The decoded value type.</typeparam>
public sealed record DecodeResult<T> where T : class
{
    private DecodeResult(T? value, DecodeErrorKind? error, int consumed)
    {
        Value = value;
        Error = error;
        Consumed = consumed;
    }

    /// <summary>The decoded value, or null on error.</summary>
    public T? Value { get; }

    /// <summary>The error, or null on success.</summary>
    public DecodeErrorKind? Error { get; }

    /// <summary>Bytes the PDU occupies when decoding succeeded; 0 on error.</summary>
    public int Consumed { get; }

    /// <summary>True when <see cref="Value"/> is set.</summary>
    public bool IsSuccess => Value is not null;

    /// <summary>A successful result.</summary>
    public static DecodeResult<T> Ok(T value, int consumed) => new(value, null, consumed);

    /// <summary>A failed result.</summary>
    public static DecodeResult<T> Fail(DecodeErrorKind error) => new(null, error, 0);
}
