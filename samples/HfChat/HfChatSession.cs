using System.Text;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Samples.HfChat;

/// <summary>One line of the chat history.</summary>
/// <param name="Time">When it was sent or received (UTC).</param>
/// <param name="Text">What to show.</param>
public sealed record ChatLine(DateTimeOffset Time, string Text);

/// <summary>
/// HF Operator Chat (Annex O) on <see cref="SisClient"/>: the message rules and the default services,
/// with no console in the way, so a test can drive it.
/// </summary>
public sealed class HfChatSession
{
    /// <summary>The HFCHAT SAP (O-0012).</summary>
    public const byte Sap = 5;

    /// <summary>The default priority (O-0014).</summary>
    public const byte DefaultPriority = 12;

    /// <summary>The broadcast address, group 31.255.255.255 (O-0020, C-0123).</summary>
    public static readonly SisAddress Broadcast = new(0x0FFFFFFF, Group: true);

    private readonly SisClient _client;
    private readonly Queue<ChatLine> _history = new();
    private readonly object _gate = new();

    private HfChatSession(SisClient client, ushort mtu, int historySize)
    {
        _client = client;
        Mtu = mtu;
        HistorySize = historySize;
    }

    /// <summary>The bind's MTU: the longest message, CR LF included (O-0004).</summary>
    public ushort Mtu { get; }

    /// <summary>How many lines of history are kept, 10 to 100 (O-0007).</summary>
    public int HistorySize { get; }

    /// <summary>Message priority, configurable (O-0013), default 12 (O-0014).</summary>
    public byte Priority { get; set; } = DefaultPriority;

    /// <summary>Repeats for group messages, as the non-ARQ minimum retransmissions (O-0018); default 1 (interp O-0024).</summary>
    public byte GroupRepeats { get; set; } = 1;

    /// <summary>A line was added to the history.</summary>
    public event Action<ChatLine>? LineAdded;

    /// <summary>The history, oldest first.</summary>
    public IReadOnlyList<ChatLine> History
    {
        get
        {
            lock (_gate)
            {
                return [.. _history];
            }
        }
    }

    /// <summary>
    /// Binds the client to SAP 5 (O-0012) with ARQ as its default service (interp O-0026), and starts
    /// showing what arrives.
    /// </summary>
    public static async Task<HfChatSession> StartAsync(SisClient client, int historySize = 50, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfLessThan(historySize, 10);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(historySize, 100);
        SisBindAccepted bound = await client.BindAsync(Sap, new ServiceType(1), cancellationToken).ConfigureAwait(false);
        var session = new HfChatSession(client, bound.Mtu, historySize);
        _ = session.ReceiveLoopAsync();
        return session;
    }

    /// <summary>
    /// Encodes a message: ASCII (O-0002), LSB aligned with the eighth bit zero (O-0021, O-0022),
    /// ending CR LF (O-0003, O-0023), at most the MTU (O-0004). A CR LF is added when missing.
    /// </summary>
    /// <exception cref="ArgumentException">A character is not ASCII, the text holds a CR or LF before its end, or it is too long.</exception>
    public static byte[] Encode(string text, int mtu)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            text += "\r\n";
        }

        string body = text[..^2];
        if (body.Any(c => c > 0x7F))
        {
            throw new ArgumentException("HFCHAT messages are ASCII only", nameof(text));
        }

        if (body.Contains('\r') || body.Contains('\n'))
        {
            throw new ArgumentException("one line per message", nameof(text));
        }

        byte[] bytes = Encoding.ASCII.GetBytes(text);
        return bytes.Length <= mtu ? bytes : throw new ArgumentException($"message of {bytes.Length} bytes is over the MTU of {mtu}", nameof(text));
    }

    /// <summary>Decodes a received U_PDU for display: the CR LF is dropped and anything not printable ASCII shows as '?' (interp O-0025).</summary>
    public static string Decode(ReadOnlySpan<byte> uPdu)
    {
        if (uPdu.EndsWith("\r\n"u8))
        {
            uPdu = uPdu[..^2];
        }

        var sb = new StringBuilder(uPdu.Length);
        foreach (byte b in uPdu)
        {
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '?');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Sends a message. To one node: ARQ, node delivery confirmation, in order (O-0016), and the
    /// confirmation is shown (O-0010). To a group: non-ARQ with <see cref="GroupRepeats"/>, no
    /// confirmation, not in order (O-0018).
    /// </summary>
    public async Task<SendResult> SendAsync(SisAddress to, string text, CancellationToken cancellationToken = default)
    {
        byte[] u = Encode(text, Mtu);
        ServiceType mode = to.Group
            ? new ServiceType(2, Confirm: 0, Order: 1, MinRetx: GroupRepeats)
            : new ServiceType(1, Confirm: 1, Order: 0);
        Add($"me -> {Show(to)}: {Decode(u)}");
        SendResult r = await _client.SendAsync(new UnidataSend(to, Sap, u) { Priority = Priority, DeliveryMode = mode }, cancellationToken).ConfigureAwait(false);
        switch (r.Status)
        {
            case SendStatus.Confirmed:
                Add($"delivered to {Show(to)}");
                break;
            case SendStatus.Rejected:
                Add($"NOT delivered to {Show(to)} (reason {r.Reason})");
                break;
        }

        return r;
    }

    private async Task ReceiveLoopAsync()
    {
        await foreach (SisUnidataIndication u in _client.Received.ReadAllAsync().ConfigureAwait(false))
        {
            // Source and time of receipt shown with every message (O-0008, O-0009).
            string to = !u.DestAddress.Group ? "" : u.DestAddress.Value == Broadcast.Value ? " (to all)" : $" (to {Show(u.DestAddress)})";
            Add($"{Show(u.SrcAddress)}{to}: {Decode(u.UPdu.Span)}");
        }
    }

    /// <summary>
    /// An address as an operator reads it: a group address with the group flag as a 29th bit, so
    /// 16.0.0.0 to 31.255.255.255 and the broadcast address 31.255.255.255 (C-0122, C-0123).
    /// </summary>
    public static string Show(SisAddress a) => a.Group
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{16 + ((a.Value >> 24) & 15)}.{(a.Value >> 16) & 255}.{(a.Value >> 8) & 255}.{a.Value & 255}")
        : a.Dotted;

    private void Add(string text)
    {
        var line = new ChatLine(DateTimeOffset.UtcNow, text);
        lock (_gate)
        {
            _history.Enqueue(line);
            while (_history.Count > HistorySize)
            {
                _history.Dequeue();
            }
        }

        LineAdded?.Invoke(line);
    }
}
