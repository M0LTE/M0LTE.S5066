using System.Buffers.Binary;
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

    /// <summary>The CRC-32 of RFC 1952 (reflected polynomial 0xEDB88320).</summary>
    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
        }

        return ~c;
    }

    /// <summary>The Adler-32 of RFC 1950.</summary>
    internal static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (byte x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
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
            byte[] plain = output.ToArray();

            // The inflater stops quietly at the end of input that is cut short, so check the
            // trailer: for gzip, the last member's CRC-32 and ISIZE over its output (RFC 1952
            // 2.3.1); for zlib, the Adler-32 of the whole output (RFC 1950 2.2).
            ReadOnlySpan<byte> d = data.Span;
            if (gzip)
            {
                if (d.Length < 18)
                {
                    return false;
                }

                uint crc = BinaryPrimitives.ReadUInt32LittleEndian(d[^8..]);
                uint isize = BinaryPrimitives.ReadUInt32LittleEndian(d[^4..]);
                if (isize > plain.Length || Crc32(plain.AsSpan(plain.Length - (int)isize)) != crc)
                {
                    return false;
                }
            }
            else if (d.Length < 6 || Adler32(plain) != BinaryPrimitives.ReadUInt32BigEndian(d[^4..]))
            {
                return false;
            }

            result = plain;
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
