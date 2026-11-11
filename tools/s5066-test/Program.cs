using System.Globalization;
using System.Text.Json;
using M0LTE.S5066.TestClient;

// s5066-test: the 5066 test client (plan 11.6). See docs/s5066-test.md.
const string Usage = """
    usage:
      s5066-test [--node host:port] [--sap n] [--save-dir dir]       interactive (line mode)
      s5066-test serve [--node host:port] [--sap n] [--save-dir dir] [--for seconds]
      s5066-test run <scenario.json> [--json result.json] [--quiet]
      s5066-test chat --to addr --text text [--node host:port]
      s5066-test file --to addr (--path file | --bytes n) [--confirm none|node|client] [--window n] [--no-verdict]
      s5066-test cftp --to addr (--path file | --bytes n) [--rcpt mailbox] [--return-path mailbox]
      s5066-test perf --to addr [--count n] [--size bytes] [--mode arq|nonarq] [--confirm none|node|client] [--echo] [--window n]
      s5066-test info [--node host:port]
      s5066-test monitor (--wav file | --udp host:port | --card alsa-device [--rate 48000]) [--jsonl file] [--quiet]
    monitor is receive only: it decodes every D_PDU heard and never transmits.
    Every command but the interactive one, serve and monitor prints a JSON result and exits 0 on pass, 1 on fail.
    Common: --node (default 127.0.0.1:5066), --sap (data SAP, default 13), --priority, --ttl, --timeout seconds.
    """;

string command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "ui";
string[] rest = command == "ui" && (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal)) ? args : args[1..];
string? positional = null;
var opts = new Dictionary<string, string>();
var flags = new HashSet<string>();
string[] flagNames = ["--echo", "--no-verdict", "--quiet"];
for (int i = 0; i < rest.Length; i++)
{
    if (flagNames.Contains(rest[i]))
    {
        flags.Add(rest[i]);
    }
    else if (rest[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < rest.Length)
    {
        opts[rest[i]] = rest[++i];
    }
    else if (!rest[i].StartsWith("--", StringComparison.Ordinal) && positional is null)
    {
        positional = rest[i];
    }
    else
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }
}

string node = opts.GetValueOrDefault("--node", "127.0.0.1:5066");
try
{
    byte sap = byte.Parse(opts.GetValueOrDefault("--sap", "13"), CultureInfo.InvariantCulture);
    switch (command)
    {
        case "ui":
            return await Interactive.RunAsync(node, sap, opts.GetValueOrDefault("--save-dir"));
        case "serve":
            return await ServeAsync(node, sap, opts.GetValueOrDefault("--save-dir"), opts.TryGetValue("--for", out string? f) ? double.Parse(f, CultureInfo.InvariantCulture) : null);
        case "run":
        {
            if (positional is null)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            Scenario scenario = ScenarioRunner.Load(positional);
            return await RunAndReportAsync(scenario, Path.GetDirectoryName(Path.GetFullPath(positional))!, opts.GetValueOrDefault("--json"), flags.Contains("--quiet"));
        }

        case "chat" or "file" or "cftp" or "perf" or "info":
        {
            var step = new ScenarioStep
            {
                Do = command,
                Node = "node",
                To = opts.GetValueOrDefault("--to"),
                Text = opts.GetValueOrDefault("--text"),
                Path = opts.GetValueOrDefault("--path"),
                Bytes = Int(opts, "--bytes"),
                Confirm = opts.GetValueOrDefault("--confirm"),
                Count = Int(opts, "--count"),
                Size = Int(opts, "--size"),
                Mode = opts.GetValueOrDefault("--mode"),
                Echo = flags.Contains("--echo") ? true : null,
                Window = Int(opts, "--window"),
                Priority = Int(opts, "--priority"),
                Repeats = Int(opts, "--repeats"),
                Ttl = Int(opts, "--ttl") is { } ttl ? (uint)ttl : null,
                SegmentBytes = Int(opts, "--segment-bytes"),
                Verdict = flags.Contains("--no-verdict") ? false : null,
                ReturnPath = opts.GetValueOrDefault("--return-path"),
                Recipients = opts.TryGetValue("--rcpt", out string? rc) ? [.. rc.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : null,
                TimeoutS = opts.TryGetValue("--timeout", out string? t) ? double.Parse(t, CultureInfo.InvariantCulture) : null,
            };
            var scenario = new Scenario { Nodes = new() { ["node"] = node }, Sap = sap, Steps = [step] };
            return await RunAndReportAsync(scenario, Directory.GetCurrentDirectory(), opts.GetValueOrDefault("--json"), flags.Contains("--quiet"));
        }

        case "monitor":
        {
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };
            return await M0LTE.S5066.TestClient.Monitor.MonitorCommand.RunAsync(
                opts.GetValueOrDefault("--wav"),
                opts.GetValueOrDefault("--udp"),
                opts.GetValueOrDefault("--card"),
                Int(opts, "--rate") ?? 48000,
                opts.GetValueOrDefault("--jsonl"),
                flags.Contains("--quiet"),
                stop.Token);
        }

        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (Exception e) when (e is FormatException or OverflowException or JsonException or IOException or System.Net.Sockets.SocketException
    || M0LTE.S5066.TestClient.Monitor.MonitorCommand.IsUserError(e))
{
    Console.Error.WriteLine($"s5066-test: {e.Message}");
    return 2;
}

static int? Int(Dictionary<string, string> opts, string key) =>
    opts.TryGetValue(key, out string? v) ? int.Parse(v, CultureInfo.InvariantCulture) : null;

// Runs a scenario, logs progress to stderr, writes the JSON result to stdout or a file.
static async Task<int> RunAndReportAsync(Scenario scenario, string baseDir, string? jsonPath, bool quiet)
{
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Cancel();
    };
    ScenarioResult result = await ScenarioRunner.RunAsync(scenario, baseDir, quiet ? null : line => Console.Error.WriteLine(line), stop.Token);
    string json = JsonSerializer.Serialize(result, ScenarioRunner.Json);
    if (jsonPath is null)
    {
        Console.WriteLine(json);
    }
    else
    {
        await File.WriteAllTextAsync(jsonPath, json + "\n");
    }

    return result.Pass ? 0 : 1;
}

// Binds and answers (echoes, receives files) until stopped or for a while; prints what happens.
static async Task<int> ServeAsync(string node, byte sap, string? saveDir, double? seconds)
{
    await using Station s = await Station.ConnectAsync("serve", node, sap, saveDir);
    s.Note += line => Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {line}");
    Console.WriteLine($"s5066-test serving on {node}: chat on SAP {Station.ChatSap}, data on SAP {sap}{(s.Cftp is null ? "" : $", CFTP on SAP {M0LTE.S5066.Cftp.CftpClient.Sap}")}");
    var stop = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.TrySetResult();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
    Task timer = seconds is { } sec ? Task.Delay(TimeSpan.FromSeconds(sec)) : Task.Delay(Timeout.Infinite);
    await Task.WhenAny(stop.Task, timer, s.Chat.Closed, s.Data.Closed);
    return s.Chat.Closed.IsCompleted || s.Data.Closed.IsCompleted ? 1 : 0;
}
