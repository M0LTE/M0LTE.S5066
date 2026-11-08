using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using M0LTE.HfChannel;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Host;

namespace M0LTE.S5066.Sweep;

/// <summary>The traffic both runs carry: a bulk ARQ transfer a to b, chat-sized ARQ messages b to a.</summary>
internal sealed record Workload
{
    public const byte BulkSap = 10;
    public const byte ChatSap = 11;

    /// <summary>Bulk U_PDU size, capped at the bind's MTU (2048, A-0047).</summary>
    public int BulkBytes { get; init; } = 2048;

    public byte BulkPriority { get; init; } = 6;

    public int ChatBytes { get; init; } = 100;

    public byte ChatPriority { get; init; } = 12;

    public long ChatStartMs { get; init; } = 30_000;

    public long ChatIntervalMs { get; init; } = 60_000;
}

/// <summary>One stack sweep run's settings.</summary>
internal sealed record RunSettings(ChannelSpec Channel, int SnrTenthDb, SweepConfig Config, int Seed, long DurationMs, long WarmupMs, JsonObject NodeOverlay)
{
    public Workload Workload { get; init; } = new();

    public string Id => string.Create(CultureInfo.InvariantCulture, $"{Channel.Name}_snr{SnrTenthDb:D3}_{Config.Id}_s{Seed}");

    public static readonly uint AddressA = 0x0000_0101;   // 0.0.1.1
    public static readonly uint AddressB = 0x0000_0102;   // 0.0.1.2

    /// <summary>The merged node configuration for node a or b (corpus defaults, then the sweep overlay, then the run's speed and table).</summary>
    public JsonObject NodeJson(bool a)
    {
        JsonObject merged = HostConfig.Merge(HostConfig.Defaults(), NodeOverlay);
        merged.Remove("host");
        merged["local_address"] = a ? "0.0.1.1" : "0.0.1.2";
        merged["prng_seed"] = Seeds.Mix("node", a ? "a" : "b", Seed).ToString("X16", CultureInfo.InvariantCulture);
        JsonObject drc = merged["drc"]!.AsObject();
        if (Config.DefaultSpeed is { } speed)
        {
            drc["default_speed"] = new JsonObject { ["wn"] = speed.Wn, ["interleaver"] = InterleaverNames.ToName(speed.Il) };
        }

        if (Config.Table is { } table)
        {
            drc["table"] = table.DeepClone();
        }

        return merged;
    }
}

/// <summary>
/// One simulated node: the sans-IO core, the host's input scheduler (core-architecture.md 12.3,
/// 12.4, so inputs reach the core in the order a real host gives them), the synchronous modem and
/// two in-process SIS client connections.
/// </summary>
internal sealed class SimNode
{
    private readonly InputScheduler _scheduler = new();
    private readonly Dictionary<int, SisApp> _apps = [];
    private readonly Action<SimNode, Transmit, long> _onTransmit;
    private readonly Action<SimNode, LinkState> _onLink;

    public SimNode(int index, NodeConfig config, ulong seed, bool ssb, Action<SimNode, Transmit, long> onTransmit, Action<SimNode, LinkState> onLink)
    {
        Index = index;
        Modem = new SimModem(ssb);
        NodeHost.CheckConfig(config, Modem);
        Core = new Node(config, seed, Modem);
        _onTransmit = onTransmit;
        _onLink = onLink;
    }

    public int Index { get; }

    public Node Core { get; }

    public SimModem Modem { get; }

    public void Attach(int conn, SisApp app, long now)
    {
        _apps[conn] = app;
        _scheduler.Post(InputClass.Sis, t => new SisConnect(t, conn), now);
        app.Connected();
        Flush(conn, app, now);
    }

    public void PostModem(List<Func<long, CoreInput>> events, long now)
    {
        foreach (Func<long, CoreInput> e in events)
        {
            _scheduler.Post(InputClass.Modem, e, now);
        }
    }

    /// <summary>Hands the core every input due at or before <paramref name="now"/>, in order, letting the apps send between inputs.</summary>
    public void Run(long now)
    {
        while (true)
        {
            CoreInput? input = _scheduler.TakeNext(now, out _);
            if (input is null)
            {
                bool sent = false;
                foreach ((int conn, SisApp app) in _apps)
                {
                    if (app.Poll(now))
                    {
                        Flush(conn, app, now);
                        sent = true;
                    }
                }

                if (!sent)
                {
                    return;
                }

                continue;
            }

            Core.Handle(input);
            foreach (CoreOutput o in Core.DrainOutputs())
            {
                Dispatch(o, now);
            }
        }
    }

    private void Flush(int conn, SisApp app, long now)
    {
        while (app.TryTakeOutgoing(out byte[] bytes))
        {
            _scheduler.Post(InputClass.Sis, t => new SisBytes(t, conn, bytes), now);
        }
    }

    private void Dispatch(CoreOutput o, long now)
    {
        switch (o)
        {
            case TimerStart or TimerCancel:
                _scheduler.Apply(o);
                break;
            case Transmit t:
                _onTransmit(this, t, now);
                // A burst cut short by our own transmission ends now, on the loop (12.6).
                PostModem(Modem.TakeEvents(), now);
                break;
            case TerminateReceive:
                Modem.TerminateReceive();
                break;
            case SetMaxInputBlocks m:
                Modem.SetMaxInputBlocks(m.N);
                break;
            case SisBytesOut b:
                if (_apps.TryGetValue(b.Conn, out SisApp? app))
                {
                    app.Receive(b.Bytes.Span, now);
                    Flush(b.Conn, app, now);
                }

                break;
            case SisClose c:
                _apps.Remove(c.Conn);
                break;
            case LinkState l:
                _onLink(this, l);
                break;
        }
    }
}

/// <summary>
/// One stack sweep run on the virtual clock (core-architecture.md 9, P7-8): two nodes joined by one
/// continuous channel realisation per direction, advanced in 20 ms steps. Each step: every node
/// handles the inputs due (timers by deadline, modem and SIS events by stamp, in the host's order);
/// each node's transmitter fills its 20 ms chunk; each chunk passes through its direction's channel;
/// each receiver demodulates what reached it, and its events are stamped with the step's end. So a
/// run goes as fast as the two demodulators and repeats exactly for a seed.
/// </summary>
internal sealed class Simulation
{
    private readonly RunSettings _s;
    private readonly SimNode[] _nodes = new SimNode[2];
    private readonly List<TxRecord> _txs = [];
    private readonly TxRecord?[] _onAir = new TxRecord?[2];
    private readonly TxRecord?[] _lastHeard = new TxRecord?[2];
    private readonly RetransmissionTracker[] _retx = [new(), new()];
    private readonly NodeStats[] _stats = [new(), new()];
    private readonly LinkStateName?[] _linkState = new LinkStateName?[2];
    private long _now;

    public Simulation(RunSettings settings) => _s = settings;

    /// <summary>A sink for one line per transmission and link change, for looking at a single run; null for none.</summary>
    public Action<string>? Timeline { get; init; }

    public RunResult Run()
    {
        var wall = Stopwatch.StartNew();
        var result = new RunResult
        {
            Id = _s.Id,
            Channel = _s.Channel.Name,
            SnrTenthDb = _s.SnrTenthDb,
            Config = _s.Config.Id,
            Seed = _s.Seed,
            DurationMs = _s.DurationMs,
            WarmupMs = _s.WarmupMs,
            Versions = VersionInfo(),
        };

        JsonObject jsonA = _s.NodeJson(true), jsonB = _s.NodeJson(false);
        result.NodeConfig = JsonSerializer.SerializeToElement(Summarise(jsonA));
        NodeConfig configA = ConfigReader.Read(jsonA), configB = ConfigReader.Read(jsonB);
        _nodes[0] = new SimNode(0, configA, ConfigReader.ReadSeed(jsonA), _s.Channel.Ssb, OnTransmit, OnLink);
        _nodes[1] = new SimNode(1, configB, ConfigReader.ReadSeed(jsonB), _s.Channel.Ssb, OnTransmit, OnLink);
        for (int i = 0; i < 2; i++)
        {
            int me = i;
            SimModem m = _nodes[i].Modem;
            m.RxStarted = () => OnRxStarted(me);
            m.RxBlock = b => OnRxBlock(me, b);
            m.RxEnded = (reason, _, _, snr) => OnRxEnded(me, reason, snr);
        }

        Workload w = _s.Workload;
        var toB = new SisAddress(RunSettings.AddressB);
        var toA = new SisAddress(RunSettings.AddressA);
        var bulk = new BulkSender(Workload.BulkSap, toB, w.BulkBytes, w.BulkPriority);
        var bulkRx = new Receiver(Workload.BulkSap);
        var chat = new ChatSender(Workload.ChatSap, toA, w.ChatBytes, w.ChatPriority, w.ChatStartMs, w.ChatIntervalMs);
        var chatRx = new Receiver(Workload.ChatSap);
        _nodes[0].Attach(1, bulk, 0);
        _nodes[0].Attach(2, chatRx, 0);
        _nodes[1].Attach(1, bulkRx, 0);
        _nodes[1].Attach(2, chat, 0);

        ContinuousWattersonChannel ab = _s.Channel.Create(Seeds.Int("channel", _s.Channel.Name, _s.SnrTenthDb, _s.Seed, "a->b"), _s.SnrTenthDb / 10.0);
        ContinuousWattersonChannel ba = _s.Channel.Create(Seeds.Int("channel", _s.Channel.Name, _s.SnrTenthDb, _s.Seed, "b->a"), _s.SnrTenthDb / 10.0);
        var txA = new float[SimModem.ChunkSamples];
        var txB = new float[SimModem.ChunkSamples];
        var rxA = new float[SimModem.ChunkSamples];
        var rxB = new float[SimModem.ChunkSamples];
        try
        {
            for (_now = 0; _now < _s.DurationMs; _now += SimModem.ChunkMs)
            {
                _nodes[0].Run(_now);
                _nodes[1].Run(_now);
                int doneA = _nodes[0].Modem.FillTx(txA);
                int doneB = _nodes[1].Modem.FillTx(txB);
                ab.Process(txA, rxB);
                ba.Process(txB, rxA);
                long end = _now + SimModem.ChunkMs;
                Finish(0, doneA, end);
                Finish(1, doneB, end);
                _nodes[0].Modem.Receive(rxA);
                _nodes[1].Modem.Receive(rxB);
                _nodes[0].PostModem(_nodes[0].Modem.TakeEvents(), end);
                _nodes[1].PostModem(_nodes[1].Modem.TakeEvents(), end);
            }
        }
        catch (Exception e)
        {
            result.Error = $"{e.GetType().Name} at {_now} ms: {e.Message}";
        }

        Collect(result, bulk, bulkRx, chat, chatRx);
        if (Timeline is { } tl)
        {
            foreach (TxRecord t in _txs)
            {
                tl(string.Create(CultureInfo.InvariantCulture,
                    $"{t.StartMs,9} to {t.EndMs,9} {(t.Node == 0 ? "a" : "b")} {t.Rung.Name,-14} {t.Bytes.Length,6} bytes {t.Dpdus.Count(d => d.Data),3} data ({t.Dpdus.Count(d => d.Retransmission)} again) {t.Dpdus.Count(d => d.AckOnly)} ack: {t.Outcome} {t.DpdusOk}/{t.DpduCount}, end {t.EndReason?.ToString() ?? "-"}, snr {t.SnrTenthDb?.ToString(CultureInfo.InvariantCulture) ?? "-"}, eot +{(t.PredictedEndMs is long p ? p - t.EndMs : 0)}"));
            }
        }

        result.WallSeconds = Math.Round(wall.Elapsed.TotalSeconds, 2);
        result.CoreSecondsPerSignalSecond = Math.Round(wall.Elapsed.TotalSeconds / (_s.DurationMs / 1000.0), 4);
        return result;
    }

    private void Finish(int node, int samplesInChunk, long chunkEnd)
    {
        if (samplesInChunk < 0)
        {
            return;
        }

        _nodes[node].Modem.TxFinished();
        _onAir[node] = null;
        _nodes[node].PostModem([t => new TransmitDone(t)], chunkEnd);
    }

    private void OnTransmit(SimNode node, Transmit t, long now)
    {
        int samples = node.Modem.Transmit(t.Bytes, t.Wn, t.Interleaver, t.K, t.Eom);
        byte[] bytes = t.Bytes.ToArray();
        // Transmit comes while the step at `now` handles its inputs, before the transmitters fill that
        // step's chunk, so the burst goes on air at `now`.
        long start = now;
        var rec = new TxRecord
        {
            Node = node.Index,
            Rung = new Rung(t.Wn, t.Interleaver),
            Bytes = bytes,
            StartMs = start,
            EndMs = start + (long)Math.Ceiling(samples * 1000.0 / ChannelSpec.SampleRate),
            AllDpdus = TxRecord.Parse(bytes, _retx[node.Index].IsRetransmission),
        };
        int other = 1 - node.Index;
        if (_onAir[other] is { } heard)
        {
            heard.Collided = true;   // the far node keys during it, so it is deaf to the rest
            rec.Collided = true;
        }

        _onAir[node.Index] = rec;
        _txs.Add(rec);
    }

    private void OnLink(SimNode node, LinkState l)
    {
        Timeline?.Invoke(string.Create(CultureInfo.InvariantCulture, $"{_now,9} {(node.Index == 0 ? "a" : "b")} link {Core.Dts.DpduAddress.ToDotted(l.Peer)} {l.State}"));
        NodeStats s = _stats[node.Index];
        LinkStateName? was = _linkState[node.Index];
        if (l.State == LinkStateName.Linked && was != LinkStateName.Linked)
        {
            s.LinksMade++;
        }
        else if (was == LinkStateName.Linked && l.State is LinkStateName.Breaking or LinkStateName.Unlinked)
        {
            s.LinksLost++;
        }
        else if (was == LinkStateName.Linked && l.State == LinkStateName.Closing)
        {
            s.LinksClosed++;
        }

        _linkState[node.Index] = l.State;
    }

    // Receiver side: a burst at node `me` comes from the other node's transmission on air, or the one just ended.
    private TxRecord? Source(int me) => _onAir[1 - me] ?? _txs.FindLast(x => x.Node == 1 - me);

    private void OnRxStarted(int me)
    {
        TxRecord? src = Source(me);
        if (src is null)
        {
            _lastHeard[me] = null;
            return;
        }

        if (src.Locked)
        {
            src.ExtraLocks++;   // a second lock on one transmission (after a lost signal): its blocks are not mixed in
            _lastHeard[me] = null;
            return;
        }

        src.Locked = true;
        _lastHeard[me] = src;
    }

    private void OnRxBlock(int me, byte[] bytes) => _lastHeard[me]?.Blocks.Add((_now + SimModem.ChunkMs, bytes));

    private void OnRxEnded(int me, BurstEndReason reason, int? snr)
    {
        if (_lastHeard[me] is { } src)
        {
            src.EndReason = reason;
            src.SnrTenthDb = snr;
        }

        _lastHeard[me] = null;
    }

    private void Collect(RunResult result, BulkSender bulk, Receiver bulkRx, ChatSender chat, Receiver chatRx)
    {
        result.OfferedBytes = bulk.SentBytes;
        result.DeliveredBytes = bulkRx.Deliveries.Sum(d => (long)d.Bytes);
        result.DeliveredBytesWindow = bulkRx.Deliveries.Where(d => d.Time >= _s.WarmupMs).Sum(d => (long)d.Bytes);
        double windowS = Math.Max(1, _s.DurationMs - _s.WarmupMs) / 1000.0;
        result.GoodputBps = Math.Round(result.DeliveredBytesWindow * 8 / windowS, 2);
        result.BulkDuplicates = bulkRx.Duplicates;
        result.BulkCorrupt = bulkRx.Corrupt;
        result.Chat.Sent = chat.Sent;
        result.Chat.Delivered = chatRx.Deliveries.Count;
        foreach (long l in chatRx.LatenciesMs)
        {
            result.Chat.LatencyMs.Add(l);
        }

        _stats[0].SisRejected = bulk.Rejected + chatRx.Rejected;
        _stats[1].SisRejected = bulkRx.Rejected + chat.Rejected;

        foreach (TxRecord t in _txs)
        {
            t.Score();
            NodeStats ns = _stats[t.Node];
            if (!ns.Rungs.TryGetValue(t.Rung.Name, out RungStats? rs))
            {
                ns.Rungs[t.Rung.Name] = rs = new RungStats();
            }

            int data = t.Dpdus.Count(d => d.Data), retx = t.Dpdus.Count(d => d.Retransmission);
            ns.Transmissions++;
            ns.DataDpdus += data;
            ns.RetransmittedDpdus += retx;
            rs.Count++;
            rs.AirtimeMs += t.EndMs - t.StartMs;
            rs.Bytes += t.Bytes.Length;
            rs.Dpdus += t.DpduCount;
            rs.DpdusOk += t.DpdusOk;
            rs.DataDpdus += data;
            rs.RetransmittedDpdus += retx;
            if (t.AckOnlyTransmission)
            {
                rs.AckOnlyTransmissions++;
                ns.AckOnlyTransmissions++;
            }

            rs.Outcomes[t.Outcome] = rs.Outcomes.GetValueOrDefault(t.Outcome) + 1;
            if (t.SnrTenthDb is int snr)
            {
                rs.SnrCount++;
                rs.SnrSumTenths += snr;
            }

            if (t.PredictedEndMs is long p)
            {
                Add(result.EotOvershootMs, t.Rung.Name, p - t.EndMs);
            }
        }

        // Gaps, in on-air order.
        TxRecord? prev = null;
        var lastOwn = new TxRecord?[2];
        foreach (TxRecord t in _txs.OrderBy(x => x.StartMs))
        {
            if (lastOwn[t.Node] is { } own)
            {
                Add(result.SameSenderGapMs, own.Rung.Name, t.StartMs - own.EndMs);
            }

            if (prev is not null && prev.Node != t.Node)
            {
                result.ExchangeGapMs.Add(t.StartMs - prev.EndMs);
            }

            lastOwn[t.Node] = t;
            prev = t;
        }

        result.Nodes["a"] = _stats[0];
        result.Nodes["b"] = _stats[1];
        long dataDpdus = _stats.Sum(s => s.DataDpdus);
        result.RetransmissionRate = dataDpdus == 0 ? 0 : Math.Round(_stats.Sum(s => s.RetransmittedDpdus) / (double)dataDpdus, 4);
        result.LinksLost = _stats.Sum(s => s.LinksLost);
    }

    private static void Add(SortedDictionary<string, Dist> map, string key, long v)
    {
        if (!map.TryGetValue(key, out Dist? d))
        {
            map[key] = d = new Dist();
        }

        d.Add(v);
    }

    /// <summary>The settings that shape a run, for the record; the drc.table as its ladder length and a hash.</summary>
    internal static JsonObject Summarise(JsonObject node)
    {
        var copy = (JsonObject)node.DeepClone();
        copy.Remove("local_address");
        copy.Remove("prng_seed");
        copy.Remove("time");
        copy.Remove("modem");
        if (copy["drc"]?["table"] is JsonObject table)
        {
            string text = table.ToJsonString();
            copy["drc"]!["table"] = new JsonObject
            {
                ["ladder_rungs"] = table["ladder"]?.AsArray().Count ?? 0,
                ["fnv1a64"] = Seeds.Mix(text).ToString("x16", CultureInfo.InvariantCulture),
            };
        }

        return copy;
    }

    internal static SortedDictionary<string, string> VersionInfo()
    {
        static string V(Type t) => t.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? t.Assembly.GetName().Version?.ToString() ?? "?";
        return new(StringComparer.Ordinal)
        {
            ["s5066_core"] = V(typeof(Node)),
            ["ms110d"] = V(typeof(M0LTE.Ms110d.Ms110dDemodulator)),
            ["hfchannel"] = V(typeof(ContinuousWattersonChannel)),
            ["ssb_passband"] = string.Create(CultureInfo.InvariantCulture, $"{ChannelSpec.DefaultPassband.LowHz}-{ChannelSpec.DefaultPassband.HighHz} Hz"),
        };
    }
}
