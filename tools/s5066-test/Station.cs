using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.TestClient;

/// <summary>A chat line that arrived.</summary>
internal sealed record ChatReceived(SisAddress From, SisAddress To, string Text, DateTimeOffset Time);

/// <summary>A file this station received in full.</summary>
internal sealed record FileReceived(SisAddress From, uint Id, string Name, long Size, bool Match, string Sha256, double ElapsedMs, string? SavedTo);

/// <summary>The far end's verdict on a file this station sent.</summary>
internal sealed record FileVerdict(SisAddress From, uint Id, bool Match, long Received, string Sha256);

/// <summary>An echo of a performance message.</summary>
internal sealed record EchoReceived(SisAddress From, uint Id, uint Seq, long Timestamp);

/// <summary>
/// The tool's view of one node: a chat client bound to SAP 5 and a data client bound to the tool's
/// data SAP, both over the SIS. It also answers for the far end of every activity: it echoes
/// performance messages that ask for it and receives files, checks their hash and tells the
/// sender the result.
/// </summary>
internal sealed class Station : IAsyncDisposable
{
    /// <summary>The HFCHAT SAP (O-0012).</summary>
    public const byte ChatSap = 5;

    /// <summary>The HFCHAT default priority (O-0014).</summary>
    public const byte ChatPriority = 12;

    /// <summary>
    /// The tool's default data SAP. 13 is unallocated in [Ed4 F Table F-1, p.F-1], and a deployment
    /// gives unallocated SAPs preference for its own applications [Ed4 F.2, p.F-2].
    /// </summary>
    public const byte DefaultDataSap = 13;

    private readonly Dictionary<(uint Src, uint Id), PartialFile> _partial = [];
    private readonly object _gate = new();

    private Station(string name, SisClient chat, SisClient data, ushort chatMtu, ushort dataMtu, byte dataSap, string? saveDir)
    {
        Name = name;
        Chat = chat;
        Data = data;
        ChatMtu = chatMtu;
        DataMtu = dataMtu;
        DataSap = dataSap;
        SaveDir = saveDir;
    }

    /// <summary>A name for logs.</summary>
    public string Name { get; }

    /// <summary>The SAP 5 connection.</summary>
    public SisClient Chat { get; }

    /// <summary>The data SAP connection.</summary>
    public SisClient Data { get; }

    /// <summary>MTU of the chat bind.</summary>
    public ushort ChatMtu { get; }

    /// <summary>MTU of the data bind.</summary>
    public ushort DataMtu { get; }

    /// <summary>The data SAP.</summary>
    public byte DataSap { get; }

    /// <summary>Where received files are written, or null.</summary>
    public string? SaveDir { get; }

    /// <summary>Chat lines received.</summary>
    public Inbox<ChatReceived> ChatLines { get; } = new();

    /// <summary>Files received in full.</summary>
    public Inbox<FileReceived> Files { get; } = new();

    /// <summary>Verdicts on files sent.</summary>
    public Inbox<FileVerdict> Verdicts { get; } = new();

    /// <summary>Echoes received.</summary>
    public Inbox<EchoReceived> Echoes { get; } = new();

    /// <summary>One line of news for a person: something arrived or went wrong.</summary>
    public event Action<string>? Note;

    /// <summary>Connects twice to a node and binds SAP 5 (ARQ default, interp O-0026) and the data SAP (ARQ default).</summary>
    public static async Task<Station> ConnectAsync(string name, string endpoint, byte dataSap = DefaultDataSap, string? saveDir = null, CancellationToken cancellationToken = default)
    {
        (string host, int port) = Addresses.ParseEndpoint(endpoint);
        SisClient chat = await SisClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        SisClient? data = null;
        try
        {
            data = await SisClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            SisBindAccepted c = await chat.BindAsync(ChatSap, new ServiceType(1), cancellationToken).ConfigureAwait(false);
            SisBindAccepted d = await data.BindAsync(dataSap, new ServiceType(1), cancellationToken).ConfigureAwait(false);
            var s = new Station(name, chat, data, c.Mtu, d.Mtu, dataSap, saveDir);
            s.Hook(chat);
            s.Hook(data);
            _ = s.ChatLoopAsync();
            _ = s.DataLoopAsync();
            return s;
        }
        catch
        {
            await chat.DisposeAsync().ConfigureAwait(false);
            if (data is not null)
            {
                await data.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Encodes a chat line: ASCII (O-0002), eighth bit zero (O-0021, O-0022), ending CR LF (O-0003,
    /// O-0023), at most the MTU (O-0004).
    /// </summary>
    public static byte[] EncodeChat(string text, int mtu)
    {
        string body = text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2] : text;
        if (body.Any(c => c > 0x7F) || body.Contains('\r', StringComparison.Ordinal) || body.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("chat lines are one line of ASCII", nameof(text));
        }

        byte[] u = Encoding.ASCII.GetBytes(body + "\r\n");
        return u.Length <= mtu ? u : throw new ArgumentException($"chat line of {u.Length} bytes is over the MTU of {mtu}", nameof(text));
    }

    /// <summary>Decodes a chat line for display: CR LF dropped, anything not printable ASCII as '?' (interp O-0025).</summary>
    public static string DecodeChat(ReadOnlySpan<byte> u)
    {
        if (u.EndsWith("\r\n"u8))
        {
            u = u[..^2];
        }

        var sb = new StringBuilder(u.Length);
        foreach (byte b in u)
        {
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '?');
        }

        return sb.ToString();
    }

    /// <summary>
    /// The HFCHAT delivery mode: to one node ARQ with node confirmation, in order (O-0016); to a
    /// group non-ARQ, as they arrive, with the given repeats (O-0018).
    /// </summary>
    public static ServiceType ChatMode(SisAddress to, byte groupRepeats = 1) => to.Group
        ? new ServiceType(2, Confirm: 0, Order: 1, MinRetx: groupRepeats)
        : new ServiceType(1, Confirm: 1, Order: 0);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await Chat.DisposeAsync().ConfigureAwait(false);
        await Data.DisposeAsync().ConfigureAwait(false);
    }

    private void Hook(SisClient c)
    {
        c.Unbound += reason => Say($"unbound by the node, reason {reason}");
        c.UnmatchedRejection += r => Say($"rejection matching no send: reason {r.Reason} to {Addresses.Show(r.DestAddress)}");
        c.HandlerFailed += e => Say($"handler failed: {e.Message}");
    }

    private void Say(string text)
    {
        try
        {
            Note?.Invoke($"{Name}: {text}");
        }
        catch (Exception)
        {
            // A display problem must not stop the station.
        }
    }

    private async Task ChatLoopAsync()
    {
        await foreach (SisUnidataIndication u in Chat.Received.ReadAllAsync().ConfigureAwait(false))
        {
            // Source and time shown with every message (O-0008, O-0009).
            var line = new ChatReceived(u.SrcAddress, u.DestAddress, DecodeChat(u.UPdu.Span), DateTimeOffset.UtcNow);
            ChatLines.Add(line);
            string to = !u.DestAddress.Group ? "" : u.DestAddress.Value == Addresses.Broadcast.Value ? " (to all)" : $" (to {Addresses.Show(u.DestAddress)})";
            Say($"chat {Addresses.Show(u.SrcAddress)}{to}: {line.Text}");
        }

        Say("chat connection closed");
    }

    private async Task DataLoopAsync()
    {
        await foreach (SisUnidataIndication u in Data.Received.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                HandleData(u);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Say($"could not handle a U_PDU from {Addresses.Show(u.SrcAddress)}: {e.Message}");
            }
        }

        Say("data connection closed");
    }

    private void HandleData(SisUnidataIndication u)
    {
        long now = Stopwatch.GetTimestamp();
        ReadOnlySpan<byte> b = u.UPdu.Span;
        if (!Wire.TryReadPrefix(b, out WireKind kind, out uint id, out uint seq))
        {
            Say($"{u.UPdu.Length} bytes from {Addresses.Show(u.SrcAddress)} that are not s5066-test's");
            return;
        }

        switch (kind)
        {
            case WireKind.Perf when Wire.PerfWantsEcho(b):
                Reply(u, Wire.Echo(b));
                break;
            case WireKind.Perf:
                break;
            case WireKind.Echo:
                Echoes.Add(new EchoReceived(u.SrcAddress, id, seq, now));
                break;
            case WireKind.FileResult when Wire.TryReadFileResult(b, out bool match, out long received, out byte[] hash):
                Verdicts.Add(new FileVerdict(u.SrcAddress, id, match, received, Convert.ToHexStringLower(hash)));
                break;
            case WireKind.FileHeader when Wire.TryReadFileHeader(b, out long size, out int segments, out byte[] hash, out string name):
                Part(u.SrcAddress, id, now).SetHeader(size, segments, hash, name);
                Complete(u, id);
                break;
            case WireKind.FileData:
                Part(u.SrcAddress, id, now).Segments[seq] = b[Wire.PrefixBytes..].ToArray();
                Complete(u, id);
                break;
            default:
                Say($"malformed {kind} from {Addresses.Show(u.SrcAddress)}");
                break;
        }
    }

    private PartialFile Part(SisAddress src, uint id, long now)
    {
        lock (_gate)
        {
            if (!_partial.TryGetValue((src.Value, id), out PartialFile? p))
            {
                p = new PartialFile(now);
                _partial[(src.Value, id)] = p;
            }

            return p;
        }
    }

    private void Complete(SisUnidataIndication u, uint id)
    {
        PartialFile p;
        lock (_gate)
        {
            p = _partial[(u.SrcAddress.Value, id)];
            if (p.Sha256 is null || p.Segments.Count < p.SegmentCount)
            {
                return;
            }

            _partial.Remove((u.SrcAddress.Value, id));
        }

        // Segments are numbered 1 to n; data that arrive out of order are put back in order here.
        using var ms = new MemoryStream();
        for (uint i = 1; i <= p.SegmentCount; i++)
        {
            if (!p.Segments.TryGetValue(i, out byte[]? seg))
            {
                Say($"file {id:x8} from {Addresses.Show(u.SrcAddress)} is missing segment {i}");
                return;
            }

            ms.Write(seg);
        }

        byte[] data = ms.ToArray();
        byte[] hash = SHA256.HashData(data);
        bool match = data.LongLength == p.Size && hash.AsSpan().SequenceEqual(p.Sha256);
        string? saved = null;
        if (SaveDir is not null)
        {
            Directory.CreateDirectory(SaveDir);
            string baseName = Path.GetFileName(p.Name) is { Length: > 0 } n ? n : "file";
            saved = Path.Combine(SaveDir, $"{baseName}.{id:x8}");
            File.WriteAllBytes(saved, data);
        }

        double ms2 = Stopwatch.GetElapsedTime(p.FirstSeen).TotalMilliseconds;
        Files.Add(new FileReceived(u.SrcAddress, id, p.Name, data.LongLength, match, Convert.ToHexStringLower(hash), ms2, saved));
        Say($"file \"{p.Name}\" ({data.Length} bytes) from {Addresses.Show(u.SrcAddress)}: {(match ? "hash matches" : "HASH DOES NOT MATCH")}{(saved is null ? "" : $", saved as {saved}")}");
        Reply(u, Wire.FileResult(id, match, data.LongLength, hash));
    }

    /// <summary>Answers a U_PDU on the data connection, in the mode it came (ARQ or not), with no confirmation.</summary>
    private void Reply(SisUnidataIndication u, byte[] reply)
    {
        var send = new UnidataSend(u.SrcAddress, u.SrcSap, reply)
        {
            Priority = u.Priority,
            DeliveryMode = new ServiceType(u.TxMode == 1 ? (byte)1 : (byte)2, Confirm: 0, Order: (byte)(u.TxMode == 1 ? 0 : 1)),
        };
        _ = Task.Run(async () =>
        {
            try
            {
                await Data.SendAsync(send).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or ObjectDisposedException)
            {
                Say($"could not answer {Addresses.Show(u.SrcAddress)}: {e.Message}");
            }
        });
    }

    private sealed class PartialFile(long firstSeen)
    {
        public long FirstSeen { get; } = firstSeen;

        public Dictionary<uint, byte[]> Segments { get; } = [];

        public long Size { get; private set; }

        public int SegmentCount { get; private set; }

        public byte[]? Sha256 { get; private set; }

        public string Name { get; private set; } = "";

        public void SetHeader(long size, int segments, byte[] sha256, string name)
        {
            Size = size;
            SegmentCount = segments;
            Sha256 = sha256;
            Name = name;
        }
    }
}
