using System.Buffers.Binary;
using System.Text;

namespace M0LTE.S5066.TestClient;

/// <summary>The kinds of the tool's own U_PDUs on its data SAP.</summary>
internal enum WireKind : byte
{
    /// <summary>File header: size, segment count, SHA-256 and name.</summary>
    FileHeader = (byte)'H',

    /// <summary>File data segment.</summary>
    FileData = (byte)'D',

    /// <summary>The receiver's verdict on a file: its SHA-256 of what arrived.</summary>
    FileResult = (byte)'R',

    /// <summary>Performance message, padded to the size asked for.</summary>
    Perf = (byte)'P',

    /// <summary>Echo of a performance message: its 10-byte prefix only.</summary>
    Echo = (byte)'E',
}

/// <summary>
/// The tool's U_PDU formats (docs/s5066-test.md). These are the tool's own, not a STANAG 5066
/// protocol: plain ARQ unidata until CFTP (Annex V) exists (plan 11.6).
/// </summary>
/// <remarks>
/// Every U_PDU starts with kind (1 byte), a transfer ID (4 bytes, random per transfer) and a
/// sequence number (4 bytes), big-endian. So the start of every U_PDU is unique, which is what
/// the client library's confirmation matching needs: the node returns a copy that may be only the
/// first bytes [Ed4 A.2.2.7, p.A-10].
/// </remarks>
internal static class Wire
{
    /// <summary>Bytes of the common prefix.</summary>
    public const int PrefixBytes = 9;

    /// <summary>Bytes of a file header before the name.</summary>
    public const int FileHeaderFixedBytes = PrefixBytes + 8 + 4 + 32 + 1;

    /// <summary>Bytes of a file result.</summary>
    public const int FileResultBytes = PrefixBytes + 1 + 8 + 32;

    /// <summary>The smallest performance message: the prefix and the flags byte.</summary>
    public const int PerfMinBytes = PrefixBytes + 1;

    /// <summary>Reads the common prefix, or returns false for a U_PDU that is not the tool's.</summary>
    public static bool TryReadPrefix(ReadOnlySpan<byte> u, out WireKind kind, out uint id, out uint seq)
    {
        kind = default;
        id = seq = 0;
        if (u.Length < PrefixBytes || !Enum.IsDefined((WireKind)u[0]))
        {
            return false;
        }

        kind = (WireKind)u[0];
        id = BinaryPrimitives.ReadUInt32BigEndian(u[1..]);
        seq = BinaryPrimitives.ReadUInt32BigEndian(u[5..]);
        return true;
    }

    private static void WritePrefix(Span<byte> u, WireKind kind, uint id, uint seq)
    {
        u[0] = (byte)kind;
        BinaryPrimitives.WriteUInt32BigEndian(u[1..], id);
        BinaryPrimitives.WriteUInt32BigEndian(u[5..], seq);
    }

    /// <summary>A file header, sequence 0. The name is ASCII, at most 255 bytes; anything else becomes '_'.</summary>
    public static byte[] FileHeader(uint id, long size, int segments, ReadOnlySpan<byte> sha256, string name)
    {
        byte[] n = Encoding.ASCII.GetBytes(new string([.. name.Take(255).Select(c => c is >= ' ' and < (char)0x7F ? c : '_')]));
        var u = new byte[FileHeaderFixedBytes + n.Length];
        WritePrefix(u, WireKind.FileHeader, id, 0);
        BinaryPrimitives.WriteInt64BigEndian(u.AsSpan(9), size);
        BinaryPrimitives.WriteInt32BigEndian(u.AsSpan(17), segments);
        sha256.CopyTo(u.AsSpan(21));
        u[53] = (byte)n.Length;
        n.CopyTo(u, 54);
        return u;
    }

    /// <summary>Parses a file header.</summary>
    public static bool TryReadFileHeader(ReadOnlySpan<byte> u, out long size, out int segments, out byte[] sha256, out string name)
    {
        size = segments = 0;
        sha256 = [];
        name = "";
        if (u.Length < FileHeaderFixedBytes || u.Length < FileHeaderFixedBytes + u[53])
        {
            return false;
        }

        size = BinaryPrimitives.ReadInt64BigEndian(u[9..]);
        segments = BinaryPrimitives.ReadInt32BigEndian(u[17..]);
        sha256 = u.Slice(21, 32).ToArray();
        name = Encoding.ASCII.GetString(u.Slice(54, u[53]));
        return size >= 0 && segments >= 0;
    }

    /// <summary>A file data segment, sequence 1 onwards.</summary>
    public static byte[] FileData(uint id, uint seq, ReadOnlySpan<byte> data)
    {
        var u = new byte[PrefixBytes + data.Length];
        WritePrefix(u, WireKind.FileData, id, seq);
        data.CopyTo(u.AsSpan(PrefixBytes));
        return u;
    }

    /// <summary>A file result: 0 the hash matched, 1 it did not.</summary>
    public static byte[] FileResult(uint id, bool match, long received, ReadOnlySpan<byte> sha256)
    {
        var u = new byte[FileResultBytes];
        WritePrefix(u, WireKind.FileResult, id, 0);
        u[9] = match ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteInt64BigEndian(u.AsSpan(10), received);
        sha256.CopyTo(u.AsSpan(18));
        return u;
    }

    /// <summary>Parses a file result.</summary>
    public static bool TryReadFileResult(ReadOnlySpan<byte> u, out bool match, out long received, out byte[] sha256)
    {
        match = false;
        received = 0;
        sha256 = [];
        if (u.Length < FileResultBytes)
        {
            return false;
        }

        match = u[9] == 0;
        received = BinaryPrimitives.ReadInt64BigEndian(u[10..]);
        sha256 = u.Slice(18, 32).ToArray();
        return true;
    }

    /// <summary>A performance message of <paramref name="size"/> bytes; bit 0 of the flags asks for an echo.</summary>
    public static byte[] Perf(uint id, uint seq, int size, bool echo)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, PerfMinBytes);
        var u = new byte[size];
        WritePrefix(u, WireKind.Perf, id, seq);
        u[9] = echo ? (byte)1 : (byte)0;
        for (int i = PerfMinBytes; i < size; i++)
        {
            u[i] = (byte)i;
        }

        return u;
    }

    /// <summary>True when a performance message asks for an echo.</summary>
    public static bool PerfWantsEcho(ReadOnlySpan<byte> u) => u.Length >= PerfMinBytes && (u[9] & 1) != 0;

    /// <summary>The echo of a performance message: its prefix with the kind changed, and its flags byte.</summary>
    public static byte[] Echo(ReadOnlySpan<byte> perf)
    {
        byte[] u = perf[..PerfMinBytes].ToArray();
        u[0] = (byte)WireKind.Echo;
        return u;
    }
}
