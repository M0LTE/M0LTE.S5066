using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using M0LTE.HfChannel;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Sweep;

/// <summary>
/// The real-time cross-check of P7-8: the P6-4 T4 setup (two <c>s5066d</c> processes over UDP
/// audio) with the P7-4 channel inserted in the audio path by an in-process bridge, the same
/// workload over TCP SIS, then the same settings on the virtual clock, side by side. The
/// difference between the two turnarounds is the host latency (issue #38).
/// </summary>
internal static class Realtime
{
    public static int Run(RunSettings s, string? json, Action<string> log)
    {
        if (s.Config.DefaultSpeed is not { } rung || s.Config.Table?["ladder"]?.AsArray().Count > 0)
        {
            throw new ArgumentException("the real-time cross-check runs a fixed: config (the bridge scales the audio for one rung)");
        }

        RunResult real = RunRealtime(s, rung, log);
        log("[realtime] now the same settings on the virtual clock");
        RunResult virt = new Simulation(s).Run();
        if (json is not null)
        {
            File.WriteAllText(json, real.ToJson());
            File.WriteAllText(Path.ChangeExtension(json, ".virtual.json"), virt.ToJson());
        }

        Console.WriteLine("real time:");
        Cli.Print(real);
        Console.WriteLine("virtual clock:");
        Cli.Print(virt);
        Dist rs = Pool(real.SameSenderGapMs), vs = Pool(virt.SameSenderGapMs);
        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine(string.Create(inv,
            $"comparison: goodput {real.GoodputBps:0} real against {virt.GoodputBps:0} virtual bit/s; " +
            $"exchange gap median {real.ExchangeGapMs.Quantile(0.5):0} against {virt.ExchangeGapMs.Quantile(0.5):0} ms (mean {real.ExchangeGapMs.Mean:0} against {virt.ExchangeGapMs.Mean:0}); " +
            $"same-sender gap median {rs.Quantile(0.5):0} against {vs.Quantile(0.5):0} ms"));
        return real.Error is null && virt.Error is null ? 0 : 1;
    }

    private static Dist Pool(IEnumerable<KeyValuePair<string, Dist>> map)
    {
        var d = new Dist();
        foreach (var kv in map)
        {
            d.Merge(kv.Value);
        }

        return d;
    }

    private static RunResult RunRealtime(RunSettings s, Rung rung, Action<string> log)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "s5066d.dll");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException($"{dll} not found (s5066d is built beside s5066-sweep)");
        }

        string dir = Directory.CreateTempSubdirectory("s5066-sweep-rt-").FullName;
        int[] ports = FreeUdpPorts(4);   // node a listen, node b listen, bridge from a, bridge from b
        int sisA = FreeTcpPort(), sisB = FreeTcpPort();
        string cfgA = WriteConfig(dir, "a.json", s.NodeJson(true), sisA, ports[0], ports[2]);
        string cfgB = WriteConfig(dir, "b.json", s.NodeJson(false), sisB, ports[1], ports[3]);
        var result = new RunResult
        {
            Id = s.Id,
            Mode = "realtime",
            Channel = s.Channel.Name,
            SnrTenthDb = s.SnrTenthDb,
            Config = s.Config.Id,
            Seed = s.Seed,
            DurationMs = s.DurationMs,
            WarmupMs = s.WarmupMs,
            Versions = Simulation.VersionInfo(),
            NodeConfig = JsonSerializer.SerializeToElement(Simulation.Summarise(s.NodeJson(true))),
        };

        var logText = new StringBuilder();
        var processes = new List<Process>();
        var clock = Stopwatch.StartNew();
        float gain = RungPower.Gain(s.Channel.Ssb, rung);   // the nodes modulate at 0.5, as SimModem does before its gain
        using var bridge = new Bridge(s, gain, ports[2], ports[1], ports[3], ports[0], clock);
        var wall = Stopwatch.StartNew();
        try
        {
            processes.Add(Start(dll, cfgA, "a", logText));
            processes.Add(Start(dll, cfgB, "b", logText));
            WaitListen(sisA);
            WaitListen(sisB);
            clock.Restart();
            bridge.Start();
            Workload w = s.Workload;
            var bulk = new BulkSender(Workload.BulkSap, new SisAddress(RunSettings.AddressB), w.BulkBytes, w.BulkPriority);
            var bulkRx = new Receiver(Workload.BulkSap);
            var chat = new ChatSender(Workload.ChatSap, new SisAddress(RunSettings.AddressA), w.ChatBytes, w.ChatPriority, w.ChatStartMs, w.ChatIntervalMs);
            var chatRx = new Receiver(Workload.ChatSap);
            var gate = new object();
            using var c1 = new TcpApp(sisA, bulk, gate, clock);
            using var c2 = new TcpApp(sisA, chatRx, gate, clock);
            using var c3 = new TcpApp(sisB, bulkRx, gate, clock);
            using var c4 = new TcpApp(sisB, chat, gate, clock);
            TcpApp[] apps = [c1, c2, c3, c4];
            log(string.Create(CultureInfo.InvariantCulture, $"[realtime] {s.Id}: running {s.DurationMs / 1000} s in real time"));
            while (clock.ElapsedMilliseconds < s.DurationMs)
            {
                lock (gate)
                {
                    foreach (TcpApp a in apps)
                    {
                        a.Pump(clock.ElapsedMilliseconds);
                    }
                }

                Thread.Sleep(20);
            }

            lock (gate)
            {
                result.OfferedBytes = bulk.SentBytes;
                result.DeliveredBytes = bulkRx.Deliveries.Sum(d => (long)d.Bytes);
                result.DeliveredBytesWindow = bulkRx.Deliveries.Where(d => d.Time >= s.WarmupMs).Sum(d => (long)d.Bytes);
                result.GoodputBps = Math.Round(result.DeliveredBytesWindow * 8 / (Math.Max(1, s.DurationMs - s.WarmupMs) / 1000.0), 2);
                result.BulkDuplicates = bulkRx.Duplicates;
                result.BulkCorrupt = bulkRx.Corrupt;
                result.Chat.Sent = chat.Sent;
                result.Chat.Delivered = chatRx.Deliveries.Count;
                foreach (long l in chatRx.LatenciesMs)
                {
                    result.Chat.LatencyMs.Add(l);
                }
            }

            bridge.Collect(result, rung);
        }
        catch (Exception e)
        {
            result.Error = e.Message;
        }
        finally
        {
            foreach (Process p in processes)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
                catch (InvalidOperationException)
                {
                    // gone
                }

                p.Dispose();
            }

            lock (logText)
            {
                File.WriteAllText(Path.Combine(dir, "nodes.log"), logText.ToString());
            }

            log($"[realtime] node logs in {Path.Combine(dir, "nodes.log")}");
        }

        result.WallSeconds = Math.Round(wall.Elapsed.TotalSeconds, 2);
        return result;
    }

    private static string WriteConfig(string dir, string name, JsonObject node, int sis, int listen, int peer)
    {
        node["host"] = new JsonObject
        {
            ["sis_listen"] = $"127.0.0.1:{sis}",
            ["modem"] = new JsonObject { ["kind"] = "ms110d", ["audio_listen"] = $"127.0.0.1:{listen}", ["audio_peer"] = $"127.0.0.1:{peer}" },
        };
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static Process Start(string dll, string config, string tag, StringBuilder log)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", $"\"{dll}\" \"{config}\"")
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
                    log.AppendLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {tag}: {e.Data}");
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

    private static void WaitListen(int port)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            using var c = new TcpClient();
            try
            {
                c.Connect(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException) when (sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                Thread.Sleep(200);
            }
        }
    }

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static int[] FreeUdpPorts(int n)
    {
        var sockets = Enumerable.Range(0, n).Select(_ => new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))).ToList();
        int[] ports = [.. sockets.Select(u => ((IPEndPoint)u.Client.LocalEndPoint!).Port)];
        sockets.ForEach(u => u.Dispose());
        return ports;
    }

    /// <summary>One SIS client connection over TCP to a real node, driving a <see cref="SisApp"/> under a shared lock.</summary>
    private sealed class TcpApp : IDisposable
    {
        private readonly TcpClient _tcp = new() { NoDelay = true };
        private readonly NetworkStream _stream;
        private readonly SisApp _app;
        private readonly object _gate;
        private readonly Stopwatch _clock;
        private readonly Thread _reader;

        public TcpApp(int port, SisApp app, object gate, Stopwatch clock)
        {
            _tcp.Connect(IPAddress.Loopback, port);
            _stream = _tcp.GetStream();
            _app = app;
            _gate = gate;
            _clock = clock;
            lock (gate)
            {
                app.Connected();
                Flush();
            }

            _reader = new Thread(Read) { IsBackground = true };
            _reader.Start();
        }

        public void Pump(long now)
        {
            while (_app.Poll(now))
            {
            }

            Flush();
        }

        private void Flush()
        {
            while (_app.TryTakeOutgoing(out byte[] bytes))
            {
                _stream.Write(bytes);
            }
        }

        private void Read()
        {
            var buffer = new byte[65536];
            try
            {
                while (true)
                {
                    int n = _stream.Read(buffer);
                    if (n <= 0)
                    {
                        return;
                    }

                    lock (_gate)
                    {
                        _app.Receive(buffer.AsSpan(0, n), _clock.ElapsedMilliseconds);
                        Flush();
                    }
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
            {
                // closed
            }
        }

        public void Dispose() => _tcp.Dispose();
    }

    /// <summary>
    /// The channel in the audio path: datagrams of 20 ms of 32-bit floats from each node pass through
    /// that direction's continuous channel to the other node. A node's UdpAudioChannel sends exact
    /// zeros while idle, so a datagram with any non-zero sample marks it transmitting; the bridge
    /// records each burst's arrival times on its own clock.
    /// </summary>
    private sealed class Bridge : IDisposable
    {
        private readonly UdpClient _fromA, _fromB;
        private readonly IPEndPoint _toB, _toA;
        private readonly ContinuousWattersonChannel _ab, _ba;
        private readonly float _gain;
        private readonly Stopwatch _clock;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<(int Node, long Start, long End)> _bursts = [];
        private readonly (long Start, long Last)?[] _current = new (long, long)?[2];
        private readonly object _gate = new();
        private Task[] _tasks = [];

        public Bridge(RunSettings s, float gain, int fromA, int toB, int fromB, int toA, Stopwatch clock)
        {
            _fromA = new UdpClient(new IPEndPoint(IPAddress.Loopback, fromA));
            _fromB = new UdpClient(new IPEndPoint(IPAddress.Loopback, fromB));
            _toB = new IPEndPoint(IPAddress.Loopback, toB);
            _toA = new IPEndPoint(IPAddress.Loopback, toA);
            _ab = s.Channel.Create(Seeds.Int("channel", s.Channel.Name, s.SnrTenthDb, s.Seed, "a->b"), s.SnrTenthDb / 10.0);
            _ba = s.Channel.Create(Seeds.Int("channel", s.Channel.Name, s.SnrTenthDb, s.Seed, "b->a"), s.SnrTenthDb / 10.0);
            _gain = gain;
            _clock = clock;
        }

        public void Start() => _tasks = [Task.Run(() => Pump(0, _fromA, _ab, _toB)), Task.Run(() => Pump(1, _fromB, _ba, _toA))];

        private async Task Pump(int node, UdpClient from, ContinuousWattersonChannel channel, IPEndPoint to)
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try
                {
                    r = await from.ReceiveAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    if (_stop.IsCancellationRequested)
                    {
                        return;
                    }

                    continue;
                }

                float[] samples = MemoryMarshal.Cast<byte, float>(r.Buffer.AsSpan(0, r.Buffer.Length & ~3)).ToArray();
                bool active = false;
                for (int i = 0; i < samples.Length; i++)
                {
                    active |= samples[i] != 0;
                    samples[i] *= _gain;
                }

                Note(node, active);
                float[] output = channel.Process(samples);
                try
                {
                    from.Send(MemoryMarshal.AsBytes(output.AsSpan()), to);
                }
                catch (SocketException)
                {
                    // the far node is not up yet
                }
            }
        }

        private void Note(int node, bool active)
        {
            long now = _clock.ElapsedMilliseconds;
            lock (_gate)
            {
                if (active)
                {
                    _current[node] = _current[node] is { } c ? (c.Start, now) : (now, now);
                }
                else if (_current[node] is { } c)
                {
                    _bursts.Add((node, c.Start, c.Last + SimModem.ChunkMs));
                    _current[node] = null;
                }
            }
        }

        public void Collect(RunResult result, Rung rung)
        {
            List<(int Node, long Start, long End)> bursts;
            lock (_gate)
            {
                bursts = [.. _bursts.OrderBy(b => b.Start)];
            }

            var stats = new[] { new NodeStats(), new NodeStats() };
            (int Node, long Start, long End)? prev = null;
            var lastOwn = new (int Node, long Start, long End)?[2];
            foreach (var b in bursts)
            {
                NodeStats n = stats[b.Node];
                n.Transmissions++;
                if (!n.Rungs.TryGetValue(rung.Name, out RungStats? rs))
                {
                    n.Rungs[rung.Name] = rs = new RungStats();
                }

                rs.Count++;
                rs.AirtimeMs += b.End - b.Start;
                if (lastOwn[b.Node] is { } own)
                {
                    (result.SameSenderGapMs.TryGetValue(rung.Name, out Dist? d) ? d : result.SameSenderGapMs[rung.Name] = new Dist()).Add(b.Start - own.End);
                }

                if (prev is { } p && p.Node != b.Node)
                {
                    result.ExchangeGapMs.Add(b.Start - p.End);
                }

                lastOwn[b.Node] = b;
                prev = b;
            }

            result.Nodes["a"] = stats[0];
            result.Nodes["b"] = stats[1];
        }

        public void Dispose()
        {
            _stop.Cancel();
            _fromA.Dispose();
            _fromB.Dispose();
            try
            {
                Task.WaitAll(_tasks, TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }

            _stop.Dispose();
        }
    }
}
