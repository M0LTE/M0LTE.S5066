using System.Diagnostics;
using System.Security.Cryptography;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.TestClient;

/// <summary>Minimum, mean, median and maximum of some times, in milliseconds.</summary>
internal sealed record Summary(int Count, double Min, double Mean, double Median, double Max)
{
    public static Summary? Of(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        double[] v = [.. values.Order()];
        double median = v.Length % 2 == 1 ? v[v.Length / 2] : (v[(v.Length / 2) - 1] + v[v.Length / 2]) / 2;
        return new Summary(v.Length, Math.Round(v[0], 1), Math.Round(v.Average(), 1), Math.Round(median, 1), Math.Round(v[^1], 1));
    }
}

/// <summary>A rejection: which U_PDU and why (A.2.2.8 reason).</summary>
internal sealed record Rejection(uint Seq, int Reason);

/// <summary>The outcome of a chat send.</summary>
internal sealed record ChatSendResult(string To, string Text, string Status, int Reason, double ElapsedMs);

/// <summary>The outcome of waiting for a chat line.</summary>
internal sealed record ChatExpectResult(string? From, string? Text, double WaitedMs);

/// <summary>The outcome of a file send.</summary>
internal sealed record FileSendResult(
    string To, string Name, long Bytes, int Segments, string Confirm, string Sha256,
    int Sent, int Confirmed, IReadOnlyList<Rejection> Rejected, IReadOnlyList<string> Errors,
    double DeliveredMs, double? GoodputBytesPerSecond, bool? ReceiverHashMatches, string? ReceiverSha256, double ElapsedMs);

/// <summary>The outcome of a performance run.</summary>
internal sealed record PerfResult(
    string To, int Count, int Size, string Mode, string Confirm, bool Echo, int Window,
    int Sent, int Confirmed, IReadOnlyList<Rejection> Rejected, IReadOnlyList<string> Errors, int Echoed,
    Summary? ConfirmMs, Summary? RoundTripMs, double ElapsedMs, double? GoodputBytesPerSecond);

/// <summary>The settings of a file send.</summary>
internal sealed record FileOptions
{
    public byte Confirm { get; init; } = 2;

    public byte Priority { get; init; } = 4;

    public uint Ttl { get; init; }

    public int Window { get; init; } = 8;

    public int? SegmentBytes { get; init; }

    public bool WaitForVerdict { get; init; } = true;
}

/// <summary>The settings of a performance run.</summary>
internal sealed record PerfOptions
{
    public int Count { get; init; } = 10;

    public int Size { get; init; } = 200;

    public bool Arq { get; init; } = true;

    public byte Confirm { get; init; } = 1;

    public bool Echo { get; init; }

    public int Window { get; init; } = 8;

    public byte Priority { get; init; } = 4;

    public byte Repeats { get; init; }

    public uint Ttl { get; init; }
}

/// <summary>The activities of plan 11.6: chat, file and performance.</summary>
internal static class Activities
{
    /// <summary>Names of the delivery confirmation values (S-0081).</summary>
    public static string ConfirmName(byte c) => c switch { 0 => "none", 1 => "node", 2 => "client", _ => c.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    /// <summary>Parses a delivery confirmation name.</summary>
    public static byte ParseConfirm(string s) => s switch
    {
        "none" => 0,
        "node" => 1,
        "client" => 2,
        _ => throw new FormatException($"confirm must be none, node or client, not {s}"),
    };

    /// <summary>Sends one HFCHAT line (Annex O) and waits for its confirmation when it asks for one.</summary>
    public static async Task<ChatSendResult> ChatAsync(Station s, SisAddress to, string text, CancellationToken cancellationToken)
    {
        byte[] u = Station.EncodeChat(text, s.ChatMtu);
        long t0 = Stopwatch.GetTimestamp();
        SendResult r = await s.Chat.SendAsync(new UnidataSend(to, Station.ChatSap, u) { Priority = Station.ChatPriority, DeliveryMode = Station.ChatMode(to) }, cancellationToken).ConfigureAwait(false);
        return new ChatSendResult(Addresses.Show(to), Station.DecodeChat(u), r.Status.ToString().ToLowerInvariant(), r.Reason, Math.Round(Ms(t0), 1));
    }

    /// <summary>Waits for a chat line, from a node and with a text if given.</summary>
    public static async Task<ChatExpectResult> ExpectChatAsync(Station s, SisAddress? from, string? text, CancellationToken cancellationToken)
    {
        long t0 = Stopwatch.GetTimestamp();
        string? want = text is null ? null : Station.DecodeChat(Station.EncodeChat(text, int.MaxValue));
        ChatReceived got = await s.ChatLines.TakeAsync(
            l => (from is null || l.From.Value == from.Value.Value) && (want is null || l.Text == want), cancellationToken).ConfigureAwait(false);
        return new ChatExpectResult(Addresses.Show(got.From), got.Text, Math.Round(Ms(t0), 1));
    }

    /// <summary>
    /// Sends a file as ARQ U_PDUs on the data SAP: a header with the size and SHA-256, then the data
    /// in segments that fill the MTU. Up to <see cref="FileOptions.Window"/> U_PDUs wait for
    /// confirmation at once. Then waits for the receiver's verdict on the hash.
    /// </summary>
    public static async Task<FileSendResult> SendFileAsync(Station s, SisAddress to, byte[] data, string name, FileOptions o, CancellationToken cancellationToken)
    {
        if (to.Group)
        {
            throw new ArgumentException("a file goes ARQ, so to one node, not a group (S-0135, S-0136)");
        }

        long t0 = Stopwatch.GetTimestamp();
        uint id = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
        if (o.SegmentBytes is < 1)
        {
            throw new ArgumentException("segment bytes must be at least 1");
        }

        int segBytes = Math.Min(o.SegmentBytes ?? int.MaxValue, s.DataMtu - Wire.PrefixBytes);
        int segments = (data.Length + segBytes - 1) / segBytes;
        byte[] hash = SHA256.HashData(data);
        var uPdus = new List<byte[]> { Wire.FileHeader(id, data.Length, segments, hash, name) };
        for (int i = 0; i < segments; i++)
        {
            uPdus.Add(Wire.FileData(id, (uint)(i + 1), data.AsSpan(i * segBytes, Math.Min(segBytes, data.Length - (i * segBytes)))));
        }

        var mode = new ServiceType(1, Confirm: o.Confirm, Order: 0);
        Outcomes outs = await SendAllAsync(s, to, uPdus, mode, o.Priority, o.Ttl, o.Window, cancellationToken).ConfigureAwait(false);
        double deliveredMs = Ms(t0);
        bool allDelivered = outs.Rejected.Count == 0 && outs.Errors.Count == 0;

        FileVerdict? verdict = null;
        if (o.WaitForVerdict && allDelivered)
        {
            verdict = await s.Verdicts.TakeAsync(v => v.Id == id && v.From.Value == to.Value, cancellationToken).ConfigureAwait(false);
        }

        return new FileSendResult(Addresses.Show(to), name, data.Length, segments, ConfirmName(o.Confirm), Convert.ToHexStringLower(hash),
            outs.Sent, outs.Confirmed, outs.Rejected, outs.Errors, Math.Round(deliveredMs, 1),
            allDelivered && deliveredMs > 0 ? Math.Round(data.Length * 1000.0 / deliveredMs, 1) : null,
            // The receiver's flag and its hash must both agree with ours.
            verdict is null ? null : verdict.Match && verdict.Sha256 == Convert.ToHexStringLower(hash) && verdict.Received == data.Length,
            verdict?.Sha256, Math.Round(Ms(t0), 1));
    }

    /// <summary>
    /// Sends <see cref="PerfOptions.Count"/> messages of <see cref="PerfOptions.Size"/> bytes and
    /// measures the time to each confirmation and, with echo, the round trip to each echo.
    /// </summary>
    public static async Task<PerfResult> PerfAsync(Station s, SisAddress to, PerfOptions o, CancellationToken cancellationToken)
    {
        if (o.Arq && to.Group)
        {
            throw new ArgumentException("ARQ goes to one node, not a group (S-0135, S-0136)");
        }

        if (!o.Arq && o.Confirm != 0)
        {
            throw new ArgumentException("delivery confirmation needs ARQ (A-0040)");
        }

        if (o.Size < Wire.PerfMinBytes || o.Size > s.DataMtu)
        {
            throw new ArgumentException($"size must be {Wire.PerfMinBytes} to {s.DataMtu} bytes (the MTU)");
        }

        long t0 = Stopwatch.GetTimestamp();
        uint id = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
        var uPdus = Enumerable.Range(0, o.Count).Select(i => Wire.Perf(id, (uint)i, o.Size, o.Echo)).ToList();
        var mode = o.Arq ? new ServiceType(1, Confirm: o.Confirm, Order: 0) : new ServiceType(2, Confirm: 0, Order: 1, MinRetx: o.Repeats);
        var starts = new long[o.Count];
        Task<Outcomes> sending = SendAllAsync(s, to, uPdus, mode, o.Priority, o.Ttl, o.Window, cancellationToken, starts);

        var rtt = new List<double>();
        long lastEcho = 0;
        if (o.Echo)
        {
            var seen = new HashSet<uint>();
            try
            {
                while (seen.Count < o.Count)
                {
                    EchoReceived e = await s.Echoes.TakeAsync(x => x.Id == id && x.From.Value == to.Value, cancellationToken).ConfigureAwait(false);
                    if (e.Seq < o.Count && seen.Add(e.Seq))
                    {
                        // The start time is set before the send is begun, so before any echo of it.
                        rtt.Add(Stopwatch.GetElapsedTime(Volatile.Read(ref starts[e.Seq]), e.Timestamp).TotalMilliseconds);
                        lastEcho = e.Timestamp;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Report what came back before the step's time ran out.
            }
        }

        Outcomes outs = await sending.ConfigureAwait(false);
        int delivered = o.Echo ? rtt.Count : o.Confirm != 0 ? outs.Confirmed : outs.Sent;
        long end = o.Echo ? lastEcho : outs.LastDone;
        double spanMs = end == 0 ? 0 : Stopwatch.GetElapsedTime(t0, end).TotalMilliseconds;
        return new PerfResult(Addresses.Show(to), o.Count, o.Size, o.Arq ? "arq" : "nonarq", ConfirmName(o.Confirm), o.Echo, o.Window,
            outs.Sent, outs.Confirmed, outs.Rejected, outs.Errors, rtt.Count,
            Summary.Of(outs.ConfirmMs), Summary.Of(rtt), Math.Round(Ms(t0), 1),
            delivered > 0 && spanMs > 0 ? Math.Round(delivered * (double)o.Size * 1000.0 / spanMs, 1) : null);
    }

    private static double Ms(long t0) => Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

    /// <summary>What happened to a batch of sends.</summary>
    private sealed class Outcomes
    {
        public int Sent;
        public int Confirmed;
        public long LastDone;
        public List<Rejection> Rejected { get; } = [];
        public List<string> Errors { get; } = [];
        public List<double> ConfirmMs { get; } = [];
    }

    /// <summary>Sends U_PDUs in order, with at most <paramref name="window"/> waiting for an answer at once.</summary>
    private static async Task<Outcomes> SendAllAsync(Station s, SisAddress to, List<byte[]> uPdus, ServiceType mode, byte priority, uint ttl, int window, CancellationToken cancellationToken, long[]? starts = null)
    {
        var outs = new Outcomes();
        var gate = new object();
        // Not disposed: a send still running when the step is cancelled releases its slot later.
        var slots = new SemaphoreSlim(Math.Max(1, window));
        var tasks = new List<Task>(uPdus.Count);
        for (int i = 0; i < uPdus.Count; i++)
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            byte[] u = uPdus[i];
            Wire.TryReadPrefix(u, out _, out _, out uint seq);
            long start = Stopwatch.GetTimestamp();
            if (starts is not null)
            {
                Volatile.Write(ref starts[i], start);
            }

            Task<SendResult> send = s.Data.SendAsync(new UnidataSend(to, s.DataSap, u) { Priority = priority, DeliveryMode = mode, Ttl = ttl }, cancellationToken);
            tasks.Add(Finish(send, seq, start));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return outs;

        async Task Finish(Task<SendResult> send, uint seq, long start)
        {
            try
            {
                SendResult r = await send.ConfigureAwait(false);
                long now = Stopwatch.GetTimestamp();
                lock (gate)
                {
                    outs.Sent++;
                    outs.LastDone = Math.Max(outs.LastDone, now);
                    switch (r.Status)
                    {
                        case SendStatus.Confirmed:
                            outs.Confirmed++;
                            outs.ConfirmMs.Add(Stopwatch.GetElapsedTime(start, now).TotalMilliseconds);
                            break;
                        case SendStatus.Rejected:
                            outs.Rejected.Add(new Rejection(seq, r.Reason));
                            break;
                    }
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException)
            {
                lock (gate)
                {
                    outs.Errors.Add($"{seq}: {e.Message}");
                }
            }
            finally
            {
                slots.Release();
            }
        }
    }
}
