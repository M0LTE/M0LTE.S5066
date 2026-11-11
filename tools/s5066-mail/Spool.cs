using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace M0LTE.S5066.Mail;

/// <summary>Which way a spooled mail goes.</summary>
internal enum SpoolQueue
{
    /// <summary>Out over HF by CFTP to one node.</summary>
    Hf,

    /// <summary>Out to the SMTP relay (V-0069).</summary>
    Smtp,
}

/// <summary>
/// What the spool keeps about one mail besides its octets.
/// </summary>
/// <param name="Id">The spool name, also used in log lines and SMTP replies.</param>
/// <param name="Queue">Which way it goes.</param>
/// <param name="Created">When it was taken in; the retry limit counts from here.</param>
/// <param name="ReturnPath">The SMTP return path, empty for none (a bounce).</param>
/// <param name="Recipients">The recipients still to deliver.</param>
/// <param name="Node">For <see cref="SpoolQueue.Hf"/>, the node address as w.x.y.z.</param>
/// <param name="MessageId">The CFTP MessageID: chosen when spooled for HF and kept across tries; for mail from HF, the one it came with.</param>
/// <param name="From">For mail from HF, the node it came from.</param>
internal sealed record SpoolItem(
    string Id,
    SpoolQueue Queue,
    DateTimeOffset Created,
    string ReturnPath,
    IReadOnlyList<string> Recipients,
    string? Node,
    string MessageId,
    string? From)
{
    public int Failures { get; init; }

    public DateTimeOffset NextAttempt { get; init; }

    public string? LastError { get; init; }
}

/// <summary>
/// A directory of mail waiting to go, so nothing taken in is lost across a restart (interp V-0092).
/// Each mail is two files in hf/ or smtp/: id.eml (the message octets, not dot-stuffed) and
/// id.json (<see cref="SpoolItem"/>). Both are written to tmp/ first and renamed in, the message
/// before the record, so a mail is in the spool only once both are whole. received.txt keeps the
/// MessageIDs of mail taken from HF for a week, so a file sent again is not forwarded twice.
/// </summary>
internal sealed partial class Spool
{
    /// <summary>How long a received MessageID is remembered.</summary>
    public static readonly TimeSpan RememberReceived = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private readonly string _root;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _received = new(StringComparer.Ordinal);

    public Spool(string root, DateTimeOffset now)
    {
        _root = root;
        foreach (string d in new[] { "tmp", "hf", "smtp" })
        {
            Directory.CreateDirectory(Path.Combine(root, d));
        }

        foreach (string f in Directory.EnumerateFiles(Path.Combine(root, "tmp")))
        {
            File.Delete(f);
        }

        foreach (SpoolQueue q in Enum.GetValues<SpoolQueue>())
        {
            foreach (string eml in Directory.EnumerateFiles(Dir(q), "*.eml"))
            {
                if (!File.Exists(Path.ChangeExtension(eml, ".json")))
                {
                    File.Delete(eml);
                }
            }
        }

        string seen = Path.Combine(root, "received.txt");
        if (File.Exists(seen))
        {
            foreach (string line in File.ReadLines(seen))
            {
                string[] p = line.Split(' ', 2);
                if (p.Length == 2 && long.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out long t)
                    && DateTimeOffset.FromUnixTimeSeconds(t) is var at && now - at < RememberReceived)
                {
                    _received[p[1]] = at;
                }
            }

            RewriteReceived();
        }
    }

    /// <summary>A new spool name: the time, then random letters, so names sort by age.</summary>
    public static string NewId(DateTimeOffset now) =>
        now.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 8);

    /// <summary>Adds a mail. Returns once both files are on disk.</summary>
    public void Add(SpoolItem item, ReadOnlySpan<byte> message)
    {
        string tmpEml = Path.Combine(_root, "tmp", item.Id + ".eml");
        WriteDurably(tmpEml, message);
        File.Move(tmpEml, Path.Combine(Dir(item.Queue), item.Id + ".eml"), overwrite: true);
        Save(item);
    }

    /// <summary>Rewrites a mail's record, after a try.</summary>
    public void Save(SpoolItem item)
    {
        string tmp = Path.Combine(_root, "tmp", item.Id + ".json");
        WriteDurably(tmp, JsonSerializer.SerializeToUtf8Bytes(item, Json));
        File.Move(tmp, Path.Combine(Dir(item.Queue), item.Id + ".json"), overwrite: true);
        SyncDirectory(Dir(item.Queue));
    }

    /// <summary>Removes a mail that has gone or was bounced.</summary>
    public void Remove(SpoolItem item)
    {
        File.Delete(Path.Combine(Dir(item.Queue), item.Id + ".json"));
        File.Delete(Path.Combine(Dir(item.Queue), item.Id + ".eml"));
    }

    /// <summary>Every mail in one queue, oldest first. A record that does not read is skipped and named.</summary>
    public List<SpoolItem> List(SpoolQueue queue, Action<string>? complain = null)
    {
        var items = new List<SpoolItem>();
        foreach (string f in Directory.EnumerateFiles(Dir(queue), "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                if (JsonSerializer.Deserialize<SpoolItem>(File.ReadAllBytes(f), Json) is { } item && File.Exists(Path.ChangeExtension(f, ".eml")))
                {
                    items.Add(item);
                }
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                complain?.Invoke($"s5066-mail: spool file {f} does not read ({e.Message}); left where it is");
            }
        }

        return items;
    }

    /// <summary>The message octets of a mail.</summary>
    public byte[] Message(SpoolItem item) => File.ReadAllBytes(Path.Combine(Dir(item.Queue), item.Id + ".eml"));

    /// <summary>True when a mail from this node with this MessageID was taken in during the last week (interp V-0092).</summary>
    public bool Seen(string fromNode, string messageId, DateTimeOffset now)
    {
        lock (_gate)
        {
            return _received.TryGetValue(fromNode + " " + messageId, out DateTimeOffset at) && now - at < RememberReceived;
        }
    }

    /// <summary>Remembers a mail taken in from HF, after it is spooled.</summary>
    public void Remember(string fromNode, string messageId, DateTimeOffset now)
    {
        string key = fromNode + " " + messageId;
        lock (_gate)
        {
            _received[key] = now;
            File.AppendAllText(Path.Combine(_root, "received.txt"), string.Create(CultureInfo.InvariantCulture, $"{now.ToUnixTimeSeconds()} {key}\n"));
        }
    }

    private string Dir(SpoolQueue q) => Path.Combine(_root, q == SpoolQueue.Hf ? "hf" : "smtp");

    private void RewriteReceived()
    {
        string tmp = Path.Combine(_root, "tmp", "received.txt");
        File.WriteAllLines(tmp, _received.Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Value.ToUnixTimeSeconds()} {kv.Key}")));
        File.Move(tmp, Path.Combine(_root, "received.txt"), overwrite: true);
    }

    /// <summary>
    /// Flushes a directory, so a rename into it survives a power cut as well as the file's octets
    /// do (fsync of the file alone does not make its new name durable on Linux).
    /// </summary>
    private static void SyncDirectory(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        int fd = Native.open(dir, 0);
        if (fd < 0)
        {
            throw new IOException($"cannot open {dir} to flush it (errno {Marshal.GetLastPInvokeError()})");
        }

        try
        {
            if (Native.fsync(fd) != 0)
            {
                throw new IOException($"cannot flush {dir} (errno {Marshal.GetLastPInvokeError()})");
            }
        }
        finally
        {
            _ = Native.close(fd);
        }
    }

    private static partial class Native
    {
        // open(2) with O_RDONLY (0), which opens a directory for fsync(2).
        [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        public static partial int open(string path, int flags);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int fsync(int fd);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int close(int fd);
    }

    private static void WriteDurably(string path, ReadOnlySpan<byte> data)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(data);
        fs.Flush(flushToDisk: true);
    }
}
