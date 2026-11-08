using System.Globalization;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.TestClient;

/// <summary>
/// The line-mode terminal UI: works over ssh with nothing but a terminal. A plain line is a chat
/// message; commands start with '/'. The station answers for the far end throughout.
/// </summary>
internal static class Interactive
{
    private const string Help = """
        Commands:
          text                  send a chat line to the current destination
          /to w.x.y.z           set the destination (16 to 31 for w is a group); /all for broadcast
          /file path [confirm]  send a file (confirm: none, node or client; default client)
          /perf count size [nonarq] [none|node|client] [echo] [window n]
          /info                 show the node information (speed, interleaver, SNR per peer)
          /help, /quit
        """;

    public static async Task<int> RunAsync(string node, byte sap, string? saveDir)
    {
        await using Station s = await Station.ConnectAsync("node", node, sap, saveDir);
        s.Note += Print;
        Print($"s5066-test on {node}: chat SAP {Station.ChatSap} (MTU {s.ChatMtu}), data SAP {sap} (MTU {s.DataMtu}). /help for commands.");
        SisAddress to = Addresses.Broadcast;
        while (Console.ReadLine() is { } line)
        {
            string[] w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            try
            {
                switch (w.FirstOrDefault())
                {
                    case null:
                        break;
                    case "/quit":
                        return 0;
                    case "/help":
                        Print(Help);
                        break;
                    case "/all":
                        to = Addresses.Broadcast;
                        Print("to all");
                        break;
                    case "/to" when w.Length == 2:
                        to = Addresses.Parse(w[1]);
                        Print($"to {Addresses.Show(to)}");
                        break;
                    case "/info":
                        _ = Background(async () => Show(await s.Data.GetNodeInformationAsync()));
                        break;
                    case "/file" when w.Length is 2 or 3:
                    {
                        SisAddress dest = to;
                        byte[] data = await File.ReadAllBytesAsync(w[1]);
                        var o = new FileOptions { Confirm = w.Length == 3 ? Activities.ParseConfirm(w[2]) : (byte)2 };
                        Print($"sending {w[1]} ({data.Length} bytes) to {Addresses.Show(dest)}");
                        _ = Background(async () =>
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                            Show(await Activities.SendFileAsync(s, dest, data, Path.GetFileName(w[1]), o, cts.Token));
                        });
                        break;
                    }

                    case "/perf" when w.Length >= 3:
                    {
                        SisAddress dest = to;
                        PerfOptions o = ParsePerf(w);
                        Print($"perf: {o.Count} x {o.Size} bytes to {Addresses.Show(dest)}");
                        _ = Background(async () =>
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                            Show(await Activities.PerfAsync(s, dest, o, cts.Token));
                        });
                        break;
                    }

                    default:
                        if (line.StartsWith('/'))
                        {
                            Print("unknown command; /help");
                            break;
                        }

                        SisAddress chatTo = to;
                        _ = Background(async () =>
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                            ChatSendResult r = await Activities.ChatAsync(s, chatTo, line, cts.Token);
                            Print(r.Status switch
                            {
                                "confirmed" => $"delivered to {r.To} in {r.ElapsedMs / 1000:F1} s",
                                "rejected" => $"NOT delivered to {r.To} (reason {r.Reason})",
                                _ => $"sent to {r.To}",
                            });
                        });
                        break;
                }
            }
            catch (Exception e) when (e is FormatException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                Print(e.Message);
            }
        }

        return 0;
    }

    private static PerfOptions ParsePerf(string[] w)
    {
        var o = new PerfOptions
        {
            Count = int.Parse(w[1], CultureInfo.InvariantCulture),
            Size = int.Parse(w[2], CultureInfo.InvariantCulture),
        };
        for (int i = 3; i < w.Length; i++)
        {
            o = w[i] switch
            {
                "nonarq" => o with { Arq = false, Confirm = 0 },
                "arq" => o with { Arq = true },
                "none" or "node" or "client" => o with { Confirm = Activities.ParseConfirm(w[i]) },
                "echo" => o with { Echo = true },
                "window" when i + 1 < w.Length => o with { Window = int.Parse(w[++i], CultureInfo.InvariantCulture) },
                _ => throw new FormatException($"perf: unknown option {w[i]}"),
            };
        }

        return o;
    }

    private static async Task Background(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            Print(e is OperationCanceledException ? "timed out" : e.Message);
        }
    }

    private static void Show(NodeInformation n)
    {
        NodeView v = NodeView.Of(n);
        Print($"node {v.Address}: sends WN {v.TxWn} {v.TxInterleaver}; {v.Peers.Count} peer(s)");
        foreach (PeerView p in v.Peers)
        {
            string heard = p.HeardWn is null ? "never heard" : $"heard WN {p.HeardWn} {p.HeardInterleaver}, SNR {p.SnrDb?.ToString("F1", CultureInfo.InvariantCulture) ?? "?"} dB, {p.SecondsSinceHeard} s ago";
            Print($"  {p.Address}: soft link {p.SoftLink}; we send WN {p.TxWn} {p.TxInterleaver}; {heard}");
        }
    }

    private static void Show(FileSendResult r)
    {
        string verdict = r.ReceiverHashMatches switch { true => "receiver's hash matches", false => "RECEIVER'S HASH DOES NOT MATCH", null => "no verdict" };
        string rej = r.Rejected.Count == 0 ? "" : $", {r.Rejected.Count} rejected (reasons {string.Join(",", r.Rejected.Select(x => x.Reason).Distinct())})";
        Print($"file {r.Name} to {r.To}: {r.Confirmed}/{r.Segments + 1} U_PDUs confirmed{rej}, {r.DeliveredMs / 1000:F1} s, {r.GoodputBytesPerSecond?.ToString("F0", CultureInfo.InvariantCulture) ?? "?"} bytes/s; {verdict}");
    }

    private static void Show(PerfResult r)
    {
        string rej = r.Rejected.Count == 0 ? "" : $", {r.Rejected.Count} rejected";
        string c = r.ConfirmMs is { } cm ? $"; confirm ms min/median/max {cm.Min:F0}/{cm.Median:F0}/{cm.Max:F0}" : "";
        string e = r.RoundTripMs is { } rt ? $"; round trip ms min/median/max {rt.Min:F0}/{rt.Median:F0}/{rt.Max:F0}" : "";
        Print($"perf to {r.To}: sent {r.Sent}/{r.Count}, confirmed {r.Confirmed}, echoed {r.Echoed}{rej}{c}{e}; {r.GoodputBytesPerSecond?.ToString("F0", CultureInfo.InvariantCulture) ?? "?"} bytes/s");
    }

    private static void Print(string text) => Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {text}");
}
