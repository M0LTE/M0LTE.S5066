using System.Buffers.Binary;

namespace M0LTE.S5066.Cftp;

/// <summary>
/// One BFTPv1 PDU (Figures V-3 and V-4, V-0026 to V-0038): DLE STX, SIZE_OF_FILENAME, the
/// file name, SIZE_OF_FILE (four octets, most significant first), then the file data.
/// </summary>
/// <param name="FileName">The file name, 0 to 255 octets, first octet first (V-0030 to V-0034); no meaning is given to it (V-0035).</param>
/// <param name="FileData">The file, its size given by SIZE_OF_FILE (V-0036, V-0037).</param>
public sealed record Bftp1Pdu(ReadOnlyMemory<byte> FileName, ReadOnlyMemory<byte> FileData)
{
    /// <summary>DLE, the first synchronisation octet (V-0027, V-0029).</summary>
    public const byte Dle = 0x10;

    /// <summary>STX, the second synchronisation octet (V-0027, V-0029).</summary>
    public const byte Stx = 0x02;

    /// <summary>Header octets other than the name: sync (2), name size (1) and file size (4).</summary>
    public const int FixedHeaderBytes = 7;

    /// <summary>The encoded length.</summary>
    public long Length => FixedHeaderBytes + (long)FileName.Length + FileData.Length;

    /// <summary>Encodes.</summary>
    public byte[] Encode()
    {
        if (FileName.Length > 255)
        {
            throw new ArgumentException("a BFTPv1 file name is at most 255 octets (V-0030, V-0031)", nameof(FileName));
        }

        byte[] b = new byte[Length];
        b[0] = Dle;
        b[1] = Stx;
        b[2] = (byte)FileName.Length;
        FileName.Span.CopyTo(b.AsSpan(3));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(3 + FileName.Length), (uint)FileData.Length);
        FileData.Span.CopyTo(b.AsSpan(FixedHeaderBytes + FileName.Length));
        return b;
    }

    /// <summary>
    /// The length of the BFTPv1 PDU that starts <paramref name="b"/>, read from its header, or
    /// null when the header is not all there yet. Used to find the end of a segmented PDU
    /// (interp V-0073). Throws <see cref="CftpFormatException"/> on a bad sync.
    /// </summary>
    public static long? DeclaredLength(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 2 && (b[0] != Dle || b[1] != Stx))
        {
            throw new CftpFormatException(CftpErrorKind.BadSync, "not a BFTPv1 PDU: it does not start with DLE STX");
        }

        if (b.Length < 3 || b.Length < FixedHeaderBytes + b[2])
        {
            return null;
        }

        int n = b[2];
        return FixedHeaderBytes + n + (long)BinaryPrimitives.ReadUInt32BigEndian(b[(3 + n)..]);
    }

    /// <summary>
    /// Decodes one whole PDU. Check order: fewer than 2 octets truncated; not DLE STX bad sync;
    /// too short for the name size, then for the name and SIZE_OF_FILE, then for the data,
    /// truncated; octets after the data trailing (corpus-format.md 3.10).
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> b, out Bftp1Pdu? pdu, out CftpErrorKind error)
    {
        pdu = null;
        error = CftpErrorKind.Truncated;
        if (b.Length < 2)
        {
            return false;
        }

        if (b[0] != Dle || b[1] != Stx)
        {
            error = CftpErrorKind.BadSync;
            return false;
        }

        if (b.Length < 3)
        {
            return false;
        }

        int n = b[2];
        if (b.Length < FixedHeaderBytes + n)
        {
            return false;
        }

        long p = BinaryPrimitives.ReadUInt32BigEndian(b[(3 + n)..]);
        long end = FixedHeaderBytes + n + p;
        if (b.Length < end)
        {
            return false;
        }

        if (b.Length > end)
        {
            error = CftpErrorKind.TrailingBytes;
            return false;
        }

        pdu = new Bftp1Pdu(b.Slice(3, n).ToArray(), b[(FixedHeaderBytes + n)..].ToArray());
        return true;
    }
}
