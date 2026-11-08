using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.TestClient;

/// <summary>A scenario: the nodes to drive and the steps to run, in order (docs/s5066-test.md).</summary>
internal sealed class Scenario
{
    /// <summary>Node names to SIS endpoints, "host:port".</summary>
    public Dictionary<string, string> Nodes { get; set; } = [];

    /// <summary>The data SAP; default 13.</summary>
    public byte? Sap { get; set; }

    /// <summary>Where every node writes the files it receives; default none.</summary>
    public string? SaveDir { get; set; }

    /// <summary>Go on after a step fails; default false.</summary>
    public bool ContinueOnFailure { get; set; }

    /// <summary>The steps.</summary>
    public List<ScenarioStep> Steps { get; set; } = [];
}

/// <summary>One step. Which fields matter depends on <see cref="Do"/>.</summary>
internal sealed class ScenarioStep
{
    /// <summary>chat, expect_chat, file, expect_file, perf, info or sleep.</summary>
    public string Do { get; set; } = "";

    /// <summary>The node that acts (not for sleep).</summary>
    public string? Node { get; set; }

    /// <summary>The destination address (chat, file, perf).</summary>
    public string? To { get; set; }

    /// <summary>The expected source (expect_chat, expect_file); any if unset.</summary>
    public string? From { get; set; }

    /// <summary>The chat text (chat, and expect_chat when a particular text is wanted).</summary>
    public string? Text { get; set; }

    /// <summary>The file to send (file); relative to the scenario file.</summary>
    public string? Path { get; set; }

    /// <summary>Instead of a path, send this many random bytes (file).</summary>
    public int? Bytes { get; set; }

    /// <summary>none, node or client.</summary>
    public string? Confirm { get; set; }

    /// <summary>Messages to send (perf).</summary>
    public int? Count { get; set; }

    /// <summary>Bytes per message (perf).</summary>
    public int? Size { get; set; }

    /// <summary>arq or nonarq (perf).</summary>
    public string? Mode { get; set; }

    /// <summary>Ask the far end to echo each message (perf).</summary>
    public bool? Echo { get; set; }

    /// <summary>U_PDUs waiting for an answer at once (file, perf).</summary>
    public int? Window { get; set; }

    /// <summary>SIS priority, 0 to 15 (file, perf).</summary>
    public int? Priority { get; set; }

    /// <summary>Non-ARQ repeats (perf).</summary>
    public int? Repeats { get; set; }

    /// <summary>TTL in units of 2 s as sent, 0 for the node's default (file, perf).</summary>
    public uint? Ttl { get; set; }

    /// <summary>Data bytes per file segment; default the MTU less the 9-byte prefix (file).</summary>
    public int? SegmentBytes { get; set; }

    /// <summary>Wait for the receiver's hash verdict; default true (file).</summary>
    public bool? Verdict { get; set; }

    /// <summary>Seconds to sleep (sleep).</summary>
    public double? Seconds { get; set; }

    /// <summary>Seconds the step may take.</summary>
    public double? TimeoutS { get; set; }
}

/// <summary>The outcome of one step.</summary>
internal sealed record StepResult(int Index, string Do, string? Node, bool Pass, double ElapsedMs, object? Detail, string? Error);

/// <summary>The outcome of a scenario: the machine-readable result of a headless run.</summary>
internal sealed record ScenarioResult(bool Pass, double ElapsedMs, IReadOnlyList<StepResult> Steps, string? Error);

/// <summary>Runs scenarios headless.</summary>
internal static class ScenarioRunner
{
    /// <summary>JSON settings for scenarios and results: snake_case, comments and trailing commas allowed.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Reads a scenario file.</summary>
    public static Scenario Load(string path) =>
        JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path), Json) ?? throw new FormatException("empty scenario");

    /// <summary>
    /// Connects to every node (binding SAP 5 and the data SAP on each, so each also answers for the far
    /// end of every activity), then runs the steps in order.
    /// </summary>
    public static async Task<ScenarioResult> RunAsync(Scenario scenario, string baseDir, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        long t0 = Stopwatch.GetTimestamp();
        var stations = new Dictionary<string, Station>();
        var results = new List<StepResult>();
        try
        {
            foreach ((string name, string endpoint) in scenario.Nodes)
            {
                Station st = await Station.ConnectAsync(name, endpoint, scenario.Sap ?? Station.DefaultDataSap, scenario.SaveDir, cancellationToken).ConfigureAwait(false);
                if (log is not null)
                {
                    st.Note += log;
                }

                stations[name] = st;
            }

            bool pass = true;
            for (int i = 0; i < scenario.Steps.Count; i++)
            {
                ScenarioStep step = scenario.Steps[i];
                StepResult r = await RunStepAsync(i, step, stations, baseDir, cancellationToken).ConfigureAwait(false);
                results.Add(r);
                log?.Invoke($"step {i} {step.Do}{(step.Node is null ? "" : $" on {step.Node}")}: {(r.Pass ? "pass" : "FAIL")} in {r.ElapsedMs / 1000:F1} s{(r.Error is null ? "" : $" ({r.Error})")}");
                pass &= r.Pass;
                if (!r.Pass && !scenario.ContinueOnFailure)
                {
                    break;
                }
            }

            return new ScenarioResult(pass, Math.Round(Stopwatch.GetElapsedTime(t0).TotalMilliseconds, 1), results, null);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or SisBindRejectedException or FormatException or OperationCanceledException)
        {
            return new ScenarioResult(false, Math.Round(Stopwatch.GetElapsedTime(t0).TotalMilliseconds, 1), results, e.Message);
        }
        finally
        {
            foreach (Station st in stations.Values)
            {
                await st.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<StepResult> RunStepAsync(int index, ScenarioStep step, Dictionary<string, Station> stations, string baseDir, CancellationToken outer)
    {
        long t0 = Stopwatch.GetTimestamp();
        double defaultTimeout = step.Do is "file" or "perf" or "expect_file" ? 900 : 120;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(TimeSpan.FromSeconds(step.TimeoutS ?? defaultTimeout));
        CancellationToken ct = cts.Token;
        StepResult Done(bool pass, object? detail, string? error = null) =>
            new(index, step.Do, step.Node, pass, Math.Round(Stopwatch.GetElapsedTime(t0).TotalMilliseconds, 1), detail, error);

        try
        {
            if (step.Do == "sleep")
            {
                await Task.Delay(TimeSpan.FromSeconds(step.Seconds ?? 1), ct).ConfigureAwait(false);
                return Done(true, null);
            }

            if (step.Node is null || !stations.TryGetValue(step.Node, out Station? s))
            {
                return Done(false, null, $"no node named {step.Node}");
            }

            switch (step.Do)
            {
                case "chat":
                {
                    ChatSendResult r = await Activities.ChatAsync(s, Addresses.Parse(Need(step.To, "to")), Need(step.Text, "text"), ct).ConfigureAwait(false);
                    return Done(r.Status is "confirmed" or "sent" && (r.Status == "sent") == Addresses.Parse(step.To!).Group, r);
                }

                case "expect_chat":
                {
                    ChatExpectResult r = await Activities.ExpectChatAsync(s, step.From is null ? null : Addresses.Parse(step.From), step.Text, ct).ConfigureAwait(false);
                    return Done(true, r);
                }

                case "file":
                {
                    byte[] data;
                    string name;
                    if (step.Path is not null)
                    {
                        string p = System.IO.Path.Combine(baseDir, step.Path);
                        data = await File.ReadAllBytesAsync(p, ct).ConfigureAwait(false);
                        name = System.IO.Path.GetFileName(p);
                    }
                    else
                    {
                        data = new byte[step.Bytes ?? throw new FormatException("file needs path or bytes")];
                        Random.Shared.NextBytes(data);
                        name = $"random-{data.Length}.bin";
                    }

                    var o = new FileOptions
                    {
                        Confirm = step.Confirm is null ? (byte)2 : Activities.ParseConfirm(step.Confirm),
                        Priority = (byte)(step.Priority ?? 4),
                        Ttl = step.Ttl ?? 0,
                        Window = step.Window ?? 8,
                        SegmentBytes = step.SegmentBytes,
                        WaitForVerdict = step.Verdict ?? true,
                    };
                    FileSendResult r = await Activities.SendFileAsync(s, Addresses.Parse(Need(step.To, "to")), data, name, o, ct).ConfigureAwait(false);
                    bool delivered = r.Rejected.Count == 0 && r.Errors.Count == 0 && (o.Confirm == 0 || r.Confirmed == r.Segments + 1);
                    return Done(delivered && (!o.WaitForVerdict || r.ReceiverHashMatches == true), r);
                }

                case "expect_file":
                {
                    SisAddress? from = step.From is null ? null : Addresses.Parse(step.From);
                    FileReceived r = await s.Files.TakeAsync(f => from is null || f.From.Value == from.Value.Value, ct).ConfigureAwait(false);
                    return Done(r.Match, new { From = Addresses.Show(r.From), r.Id, r.Name, r.Size, r.Match, r.Sha256, ElapsedMs = Math.Round(r.ElapsedMs, 1), r.SavedTo });
                }

                case "perf":
                {
                    bool arq = (step.Mode ?? "arq") switch { "arq" => true, "nonarq" => false, _ => throw new FormatException("mode must be arq or nonarq") };
                    var o = new PerfOptions
                    {
                        Count = step.Count ?? 10,
                        Size = step.Size ?? 200,
                        Arq = arq,
                        Confirm = step.Confirm is null ? (arq ? (byte)1 : (byte)0) : Activities.ParseConfirm(step.Confirm),
                        Echo = step.Echo ?? false,
                        Window = step.Window ?? 8,
                        Priority = (byte)(step.Priority ?? 4),
                        Repeats = (byte)(step.Repeats ?? 0),
                        Ttl = step.Ttl ?? 0,
                    };
                    PerfResult r = await Activities.PerfAsync(s, Addresses.Parse(Need(step.To, "to")), o, ct).ConfigureAwait(false);
                    bool pass = r.Rejected.Count == 0 && r.Errors.Count == 0 && r.Sent == r.Count
                        && (o.Confirm == 0 || r.Confirmed == r.Count) && (!o.Echo || r.Echoed == r.Count);
                    return Done(pass, r);
                }

                case "info":
                {
                    NodeInformation info = await s.Data.GetNodeInformationAsync(ct).ConfigureAwait(false);
                    return Done(true, NodeView.Of(info));
                }

                default:
                    return Done(false, null, $"unknown step \"{step.Do}\"");
            }
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            return Done(false, null, "timed out");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or FormatException or UnauthorizedAccessException)
        {
            return Done(false, null, e.Message);
        }
    }

    private static string Need(string? value, string field) => value ?? throw new FormatException($"the step needs \"{field}\"");
}

/// <summary>The node information message as the tool shows it (plan 11.6, node view).</summary>
internal sealed record NodeView(string Address, int TxWn, string TxInterleaver, IReadOnlyList<PeerView> Peers)
{
    private static readonly string[] Interleavers = ["ultra short", "very short", "short", "medium", "long", "very long"];

    public static string Interleaver(int code) => code >= 0 && code < Interleavers.Length ? Interleavers[code] : $"code {code}";

    public static NodeView Of(NodeInformation n) => new(Addresses.Show(n.Address), n.TxWn, Interleaver(n.TxInterleaver),
        [.. n.Peers.Select(p => new PeerView(Addresses.Show(p.Address), p.SoftLink switch { 0 => "none", 1 => "making", 2 => "established", 3 => "closing", _ => $"{p.SoftLink}" },
            p.TxWn, Interleaver(p.TxInterleaver), p.HeardWn, p.HeardInterleaver is { } h ? Interleaver(h) : null,
            p.SnrTenthDb is { } snr ? snr / 10.0 : null, p.SecondsSinceHeard))]);
}

/// <summary>One peer of <see cref="NodeView"/>.</summary>
internal sealed record PeerView(string Address, string SoftLink, int TxWn, string TxInterleaver, int? HeardWn, string? HeardInterleaver, double? SnrDb, int? SecondsSinceHeard);
