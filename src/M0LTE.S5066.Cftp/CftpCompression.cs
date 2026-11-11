using System.IO.Compression;

namespace M0LTE.S5066.Cftp;

/// <summary>
/// CFTP compression (V-0046, V-0063, V-0068, interp V-0082): we send RFC 1952 gzip, one
/// member; we accept gzip with any number of members, or an RFC 1950 zlib stream when the
/// data do not start with the gzip magic.
/// </summary>
public static class CftpCompression
{
    /// <summary>
    /// Compresses into one gzip member (RFC 1952). Deflate output is not unique, so other
    /// implementations may make other bytes for the same input (interp V-0082).
    /// </summary>
    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gz.Write(data);
        }

        return ms.ToArray();
    }

    /// <summary>Decompresses gzip (RFC 1952) or zlib (RFC 1950) data; any failure is <see cref="CftpErrorKind.BadCompression"/>.</summary>
    public static bool TryDecompress(ReadOnlyMemory<byte> data, out byte[]? result, out CftpErrorKind error)
    {
        result = null;
        error = CftpErrorKind.BadCompression;
        bool gzip = data.Length >= 2 && data.Span[0] == 0x1F && data.Span[1] == 0x8B;
        try
        {
            using var input = new MemoryStream(data.ToArray(), writable: false);
            using Stream inflate = gzip ? new GZipStream(input, CompressionMode.Decompress) : new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflate.CopyTo(output);
            result = output.ToArray();
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }
}
