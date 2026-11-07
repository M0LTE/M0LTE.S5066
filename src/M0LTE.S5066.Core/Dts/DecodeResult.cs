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

/// <summary>Either a decoded value or a typed error, plus how many bytes the PDU used.</summary>
/// <typeparam name="T">The decoded value type.</typeparam>
public sealed record DecodeResult<T> where T : class
{
    private DecodeResult(T? value, PduErrorKind? error, int consumed)
    {
        Value = value;
        Error = error;
        Consumed = consumed;
    }

    /// <summary>The decoded value, or null on error.</summary>
    public T? Value { get; }

    /// <summary>The error, or null on success.</summary>
    public PduErrorKind? Error { get; }

    /// <summary>Bytes the PDU occupies when decoding succeeded; 0 on error.</summary>
    public int Consumed { get; }

    /// <summary>True when <see cref="Value"/> is set.</summary>
    public bool IsSuccess => Value is not null;

    /// <summary>A successful result.</summary>
    public static DecodeResult<T> Ok(T value, int consumed) => new(value, null, consumed);

    /// <summary>A failed result.</summary>
    public static DecodeResult<T> Fail(PduErrorKind error) => new(null, error, 0);
}
