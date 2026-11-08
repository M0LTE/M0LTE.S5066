using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace M0LTE.S5066.T4.Tests;

/// <summary>
/// P6-4, the phase 6 done-when (plan 12): two s5066d processes on the bench configurations, joined
/// by UDP audio through the real M0LTE.Ms110d modem, exchange chat both ways, driven by s5066-test
/// headless. The short test runs in CI; the long one only when S5066_LONG_TESTS=1.
/// </summary>
public class TwoNodeAudioTests
{
    private readonly ITestOutputHelper _output;

    public TwoNodeAudioTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Two_nodes_over_audio_exchange_chat_both_ways_and_a_confirmed_file()
    {
        // About 25 s: two confirmed chat lines (link set-up included) and a 500-byte file with client
        // delivery confirmation and the receiver's hash verdict.
        JsonElement result = await RunAsync("""
            [
              { "do": "chat", "node": "a", "to": "0.0.1.2", "text": "hello from 0.0.1.1" },
              { "do": "expect_chat", "node": "b", "from": "0.0.1.1", "text": "hello from 0.0.1.1" },
              { "do": "chat", "node": "b", "to": "0.0.1.1", "text": "hello from 0.0.1.2" },
              { "do": "expect_chat", "node": "a", "from": "0.0.1.2", "text": "hello from 0.0.1.2" },
              { "do": "file", "node": "a", "to": "0.0.1.2", "bytes": 500, "confirm": "client" },
              { "do": "expect_file", "node": "b", "from": "0.0.1.1" }
            ]
            """, TimeSpan.FromMinutes(6));

        JsonElement[] steps = [.. result.GetProperty("steps").EnumerateArray()];
        steps.Should().HaveCount(6);
        steps[0].GetProperty("detail").GetProperty("status").GetString().Should().Be("confirmed");
        steps[1].GetProperty("detail").GetProperty("text").GetString().Should().Be("hello from 0.0.1.1");
        steps[2].GetProperty("detail").GetProperty("status").GetString().Should().Be("confirmed");
        steps[3].GetProperty("detail").GetProperty("from").GetString().Should().Be("0.0.1.2");
        steps[4].GetProperty("detail").GetProperty("receiver_hash_matches").GetBoolean().Should().BeTrue();
        steps[5].GetProperty("detail").GetProperty("sha256").GetString().Should().Be(steps[4].GetProperty("detail").GetProperty("sha256").GetString());
        result.GetProperty("pass").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Long_two_nodes_over_audio_files_both_ways_and_perf_with_echo()
    {
        // Several minutes; run locally with S5066_LONG_TESTS=1. As of 2026-11-08 it fails: a 10 kB file
        // (five U_PDUs near the MTU) stalls and its last U_PDUs are rejected with reason 3 (issue #86).
        Assert.SkipUnless(Environment.GetEnvironmentVariable("S5066_LONG_TESTS") == "1", "long T4 test: set S5066_LONG_TESTS=1 to run");
        JsonElement result = await RunAsync("""
            [
              { "do": "chat", "node": "a", "to": "0.0.1.2", "text": "long run" },
              { "do": "expect_chat", "node": "b", "text": "long run" },
              { "do": "file", "node": "a", "to": "0.0.1.2", "bytes": 10000, "confirm": "client" },
              { "do": "file", "node": "b", "to": "0.0.1.1", "bytes": 10000, "confirm": "node" },
              { "do": "perf", "node": "a", "to": "0.0.1.2", "count": 10, "size": 200, "confirm": "node", "echo": true },
              { "do": "perf", "node": "b", "to": "0.0.1.1", "count": 10, "size": 200, "confirm": "client" },
              { "do": "info", "node": "a" }
            ]
            """, TimeSpan.FromMinutes(30), trace: true);
        result.GetProperty("pass").GetBoolean().Should().BeTrue();
    }

    /// <summary>
    /// Runs one scenario on two fresh nodes. With <paramref name="trace"/> the nodes run with
    /// <c>--trace</c> (issue #86), and the whole log goes to the test output as always; set
    /// S5066_T4_LOG to a file path to keep it there too.
    /// </summary>
    private async Task<JsonElement> RunAsync(string steps, TimeSpan timeout, bool trace = false)
    {
        string dir = Directory.CreateTempSubdirectory("s5066-t4-").FullName;
        (int sisA, int sisB, int audioA, int audioB) = FreePorts();
        string configA = WriteConfig(dir, "bench-node1.json", sisA, audioA, audioB);
        string configB = WriteConfig(dir, "bench-node2.json", sisB, audioB, audioA);
        string scenario = Path.Combine(dir, "scenario.json");
        await File.WriteAllTextAsync(scenario, $$"""
            { "nodes": { "a": "127.0.0.1:{{sisA}}", "b": "127.0.0.1:{{sisB}}" }, "steps": {{steps}} }
            """);

        var log = new StringBuilder();
        var processes = new List<Process>();
        string flag = trace ? "--trace " : string.Empty;
        try
        {
            processes.Add(Start("s5066d.dll", flag + configA, "a", log));
            processes.Add(Start("s5066d.dll", flag + configB, "b", log));
            await WaitForListenAsync(sisA, processes[0]);
            await WaitForListenAsync(sisB, processes[1]);
            string json = Path.Combine(dir, "result.json");
            Process run = Start("s5066-test.dll", $"run \"{scenario}\" --json \"{json}\"", "test", log);
            processes.Add(run);
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await run.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"s5066-test did not finish within {timeout}");
            }

            File.Exists(json).Should().BeTrue($"s5066-test writes its result (exit code {run.ExitCode})");
            string text = await File.ReadAllTextAsync(json);
            _output.WriteLine(text);
            JsonElement result = JsonDocument.Parse(text).RootElement.Clone();
            run.ExitCode.Should().Be(result.GetProperty("pass").GetBoolean() ? 0 : 1, "the exit code agrees with the JSON result");
            return result;
        }
        finally
        {
            // Every process this test started goes, whatever happened, so no s5066d is left behind.
            foreach (Process p in processes)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    await p.WaitForExitAsync();
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }

                p.Dispose();
            }

            lock (log)
            {
                _output.WriteLine(log.ToString());
                if (Environment.GetEnvironmentVariable("S5066_T4_LOG") is { Length: > 0 } keep)
                {
                    File.WriteAllText(keep, log.ToString());
                }
            }

            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A copy of a bench configuration with its fixed ports swapped for free ones, so tests and a running bench never collide.</summary>
    private static string WriteConfig(string dir, string name, int sis, int listen, int peer)
    {
        string text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "bench", name));
        string[] fixedPorts = name == "bench-node1.json" ? ["5066", "7300", "7301"] : ["5067", "7301", "7300"];
        text = text.Replace($"\"sis_listen\": \"127.0.0.1:{fixedPorts[0]}\"", $"\"sis_listen\": \"127.0.0.1:{sis}\"", StringComparison.Ordinal)
            .Replace($"\"audio_listen\": \"127.0.0.1:{fixedPorts[1]}\"", $"\"audio_listen\": \"127.0.0.1:{listen}\"", StringComparison.Ordinal)
            .Replace($"\"audio_peer\": \"127.0.0.1:{fixedPorts[2]}\"", $"\"audio_peer\": \"127.0.0.1:{peer}\"", StringComparison.Ordinal);
        text.Should().Contain($":{sis}\"").And.Contain($":{listen}\"").And.Contain($":{peer}\"", "the bench file's ports were found and replaced");
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static Process Start(string dll, string arguments, string tag, StringBuilder log)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", $"\"{Path.Combine(AppContext.BaseDirectory, dll)}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var p = new Process { StartInfo = psi };
        DataReceivedEventHandler h = (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (log)
                {
                    log.AppendLine($"[{DateTime.UtcNow:HH:mm:ss.f}] {tag}: {e.Data}");
                }
            }
        };
        p.OutputDataReceived += h;
        p.ErrorDataReceived += h;
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    private static async Task WaitForListenAsync(int port, Process node)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            using var c = new TcpClient();
            try
            {
                await c.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException) when (sw.Elapsed < TimeSpan.FromSeconds(30) && !node.HasExited)
            {
                await Task.Delay(200);
            }
        }
    }

    /// <summary>Two TCP and two UDP ports, all held open until all four are chosen, so none repeats.</summary>
    private static (int SisA, int SisB, int AudioA, int AudioB) FreePorts()
    {
        using var ta = new TcpListener(IPAddress.Loopback, 0);
        using var tb = new TcpListener(IPAddress.Loopback, 0);
        ta.Start();
        tb.Start();
        using var ua = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var ub = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int Port(EndPoint e) => ((IPEndPoint)e).Port;
        (int, int, int, int) ports = (Port(ta.LocalEndpoint), Port(tb.LocalEndpoint), Port(ua.Client.LocalEndPoint!), Port(ub.Client.LocalEndPoint!));
        ta.Stop();
        tb.Stop();
        return ports;
    }
}
