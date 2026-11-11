namespace M0LTE.S5066.Cftp;

/// <summary>
/// Why a CFTP decoder rejected its input: the corpus error kinds its kinds use (conformance
/// docs/corpus-format.md sections 3.1 and 3.10).
/// </summary>
public enum CftpErrorKind
{
    /// <summary>The input ends before the PDU or field does.</summary>
    Truncated,

    /// <summary>A BFTPv1 PDU does not start with DLE STX (0x10 0x02).</summary>
    BadSync,

    /// <summary>Bytes remain after the PDU.</summary>
    TrailingBytes,

    /// <summary>A field holds a value the codec cannot accept.</summary>
    InvalidField,

    /// <summary>Compressed data do not decompress.</summary>
    BadCompression,
}

/// <summary>A CFTP decoder rejected its input.</summary>
/// <param name="kind">Why.</param>
/// <param name="message">What, for a person.</param>
public sealed class CftpFormatException(CftpErrorKind kind, string message) : FormatException(message)
{
    /// <summary>Why.</summary>
    public CftpErrorKind Kind { get; } = kind;
}
