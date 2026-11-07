namespace M0LTE.S5066.Core.Sis;

/// <summary>Why a frame was rejected. These match the corpus error kinds (corpus-format.md sections 3.1 and 3.5).</summary>
public enum SisErrorKind
{
    /// <summary>The input ends before the frame does (whole-frame decode only; a stream waits instead).</summary>
    Truncated,

    /// <summary>The input is not at a 90 EB preamble (S-0061, S-0075).</summary>
    BadSync,

    /// <summary>The version byte is not 0 (S-0066, S-0075).</summary>
    BadVersion,

    /// <summary>The size field is 0 (S-0069, S-0075).</summary>
    BadSize,

    /// <summary>The type byte is not a primitive of the profile, deprecated or otherwise (S-0071, S-0159).</summary>
    UnknownType,

    /// <summary>A hard link (6 to 13) or expedited data (24 to 27) primitive: recognised, reported, not decoded (S-0155, S-0156, S-0157).</summary>
    DeprecatedType,

    /// <summary>The body length does not fit the type, or an inner size field disagrees with it (S-0076).</summary>
    BadLength,

    /// <summary>Whole-frame decode only: bytes remain after the frame.</summary>
    TrailingBytes,
}

/// <summary>
/// One outcome of decoding: a primitive, or an error. For <see cref="SisErrorKind.DeprecatedType"/>
/// and <see cref="SisErrorKind.UnknownType"/> the offending type byte is in <see cref="TypeByte"/>.
/// </summary>
/// <param name="Primitive">The decoded primitive, or null when this is an error.</param>
/// <param name="Error">The error kind, or null when this is a primitive.</param>
/// <param name="TypeByte">The type byte for a deprecated or unknown type, otherwise null.</param>
public sealed record SisDecodeResult(SisPrimitive? Primitive, SisErrorKind? Error, byte? TypeByte = null)
{
    /// <summary>True when this result holds a primitive.</summary>
    public bool IsPrimitive => Primitive is not null;

    internal static SisDecodeResult Ok(SisPrimitive p) => new(p, null);

    internal static SisDecodeResult Fail(SisErrorKind kind, byte? typeByte = null) => new(null, kind, typeByte);
}
