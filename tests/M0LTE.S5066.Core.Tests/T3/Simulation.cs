using System.Globalization;
using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests.T3;

/// <summary>
/// The channel and host model of a T3 simulation (plan 7; core-architecture.md 9, P3-7). Loss is in
/// parts per million so every draw is an integer. Latencies are in ms.
/// </summary>
/// <param name="BlockErrorPpm">Chance that one modem block reaches one receiver corrupted (its bytes replaced by noise).</param>
/// <param name="BurstMissPpm">Chance that one receiver misses a whole burst: no preamble lock, so no DCD and no bytes.</param>
/// <param name="HostLatencyMs">Delay from a modem event to the core input that reports it (burst start, each block, burst end, transmit done).</param>
/// <param name="TurnaroundMs">Delay from the core's <c>transmit</c> output to the start of the burst on air (host to modem, receive to transmit switch).</param>
/// <param name="EndOfBurstDelayMs">Extra delay on the end-of-burst reports, <c>transmit_done</c> and <c>burst_ended</c>, on top of the host latency.</param>
/// <param name="FadePpm">Chance, after each block but the last, that one receiver loses the signal for the rest of the burst (<c>burst_ended</c> signal lost while the burst is still on air).</param>
internal sealed record ChannelModel(
    int BlockErrorPpm = 0,
    int BurstMissPpm = 0,
    int HostLatencyMs = 0,
    int TurnaroundMs = 0,
    int EndOfBurstDelayMs = 0,
    int FadePpm = 0);

/// <summary>One burst on air: who sent it, when, its plan and the D_PDUs it holds.</summary>
internal sealed class SimBurst
{
    public required int Id { get; init; }
    public required int Sender { get; init; }
    public required long EmitT { get; init; }
    public required long Start { get; init; }
    public required BurstPlan Plan { get; init; }
    public required byte[] Padded { get; init; }
    public required Transmit Output { get; init; }
    public required IReadOnlyList<SimDpdu> Dpdus { get; init; }

    public long End => Start + Plan.DurationMs;
}

/// <summary>
/// One D_PDU of a burst, as laid out: its byte offset and header length, its decoded value, and the
/// time its EOT announces as the end of the burst, from the start of its block (C-0104, C-1043).
/// </summary>
internal sealed record SimDpdu(int Offset, int HeaderLength, Dpdu Pdu, long AnnouncedEnd);

/// <summary>A U_PDU delivered to a client: receiver, core time, source address and bytes.</summary>
internal sealed record SimDelivery(int Receiver, long T, uint Src, byte[] UPdu);

/// <summary>
/// Two or more <see cref="Node"/>s on one simulated HF channel (core-architecture.md 9, P3-7). The
/// harness of harness-contract.md 2 is applied to each node, with the modem replaced by a channel
/// model: every burst a node transmits is heard by the others through per-receiver loss, half
/// duplex, and one burst at a time per receiver. Everything is a function of the seed: nodes get
/// seeds drawn from it and the channel has its own generator drawn from it, and events run in a
/// total order (time, phase, sequence), so a run is reproducible exactly.
/// </summary>
/// <remarks>
/// Timing at a receiver: DCD (<c>burst_started</c>) after the preamble, each block's bytes at the
/// end of that block, <c>burst_ended</c> at the end of the burst (after the flush), each plus the
/// host latency, the end report also plus the end-of-burst delay. A receiver that keys up mid-burst
/// stops receiving it (<c>signal_lost</c>). A burst that arrives while a receiver is locked on
/// another is not received, and corrupts the blocks of the locked one that it overlaps.
/// </remarks>
internal sealed class Simulation
{
    // Event phases at one millisecond: channel events first (they only schedule), then core inputs
    // in the class order of corpus-format.md 4.5 (timer, modem, SIS; no config inputs here).
    private const int PhaseChannel = 0;
    private const int PhaseTimer = 1;
    private const int PhaseModem = 2;
    private const int PhaseSis = 3;

    private const int Conn = 1;

    private readonly ChannelModel _channel;
    private readonly SplitMix64 _prng;
    private readonly List<SimNode> _nodes = [];
    private readonly PriorityQueue<Action<long>, (long T, int Phase, long Seq)> _events = new();
    private readonly List<SimBurst> _bursts = [];
    private readonly List<string> _log = [];
    private readonly List<string> _violations = [];
    private readonly List<SimDelivery> _deliveries = [];
    private readonly List<(int Node, long T, uint Peer, LinkStateName State)> _linkStates = [];
    private long _seq;
    private long _now;

    /// <summary>Builds the nodes, each from <c>defaults.json</c> merged with its overlay.</summary>
    /// <param name="seed">The simulation seed.</param>
    /// <param name="channel">The channel model.</param>
    /// <param name="overlays">One configuration overlay per node, as in a T2 scenario header.</param>
    /// <param name="core">Builds each node's core; null for a real <see cref="Node"/>. The tests use it to check the checks.</param>
    public Simulation(ulong seed, ChannelModel channel, IReadOnlyList<JsonObject> overlays, CoreFactory? core = null)
    {
        _channel = channel;
        var master = new SplitMix64(seed);
        foreach (JsonObject overlay in overlays)
        {
            NodeConfig config = ConfigReader.Read(ConfigMerge.Merge(Corpus.T2Defaults, overlay));
            var modem = new FixtureModem(config.Modem);
            _nodes.Add(new SimNode(_nodes.Count, config, modem, (core ?? CoreDriver.ForNode)(config, master.NextUInt64(), modem)));

            // The end report of one burst must reach the core before the start report of the next
            // one can (DCD comes after the preamble), or a receiver would see them out of order.
            if (channel.EndOfBurstDelayMs >= config.Modem.PreambleMs)
            {
                throw new ArgumentException("the end-of-burst delay must be shorter than every preamble", nameof(channel));
            }
        }

        _prng = new SplitMix64(master.NextUInt64());
    }

    public IReadOnlyList<SimBurst> Bursts => _bursts;

    public IReadOnlyList<SimDelivery> Deliveries => _deliveries;

    /// <summary>Every <c>link_state</c> output: node, time, peer and state, in the order they happened.</summary>
    public IReadOnlyList<(int Node, long T, uint Peer, LinkStateName State)> LinkStates => _linkStates;

    /// <summary>Breaches of the assertions the simulation checks itself (C-0844, C-0889, the harness rules).</summary>
    public IReadOnlyList<string> Violations => _violations;

    /// <summary>Every core input and output, one line each, in the order they happened.</summary>
    public IReadOnlyList<string> Log => _log;

    /// <summary>Times one node started a burst while another node's burst was on air (true collisions, which CSMA allows).</summary>
    public int Collisions { get; private set; }

    /// <summary>
    /// Fades that left a receiver with no signal while an EOT it had been given still announced
    /// the burst on air: the cases where only the EOT keeps it quiet (C-0844, "even if it hears no signal").
    /// </summary>
    public int FadesUnderEot { get; private set; }

    /// <summary>Requests the client of a node had rejected.</summary>
    public int Rejections { get; private set; }

    public uint AddressOf(int node) => _nodes[node].Config.LocalAddress;

    /// <summary>Connects a client to the node at <paramref name="t"/> and binds <paramref name="sap"/> for non-ARQ.</summary>
    public void Bind(int node, long t, byte sap)
    {
        Sis(node, t, new SisConnect(t, Conn));
        Sis(node, t, new SisBytes(t, Conn, SisCodec.Encode(new SisBindRequest(sap, new ServiceType(2)))));
    }

    /// <summary>Connects a client to the node at <paramref name="t"/> and binds <paramref name="sap"/> for ARQ, no confirmation.</summary>
    public void BindArq(int node, long t, byte sap)
    {
        Sis(node, t, new SisConnect(t, Conn));
        Sis(node, t, new SisBytes(t, Conn, SisCodec.Encode(new SisBindRequest(sap, new ServiceType(1)))));
    }

    /// <summary>Queues an ARQ S_UNIDATA_REQUEST (the bound service) from the node's client at <paramref name="t"/>, TTL default.</summary>
    public void SendArq(int node, long t, uint dst, byte dstSap, byte[] uPdu)
    {
        var request = new SisUnidataRequest(0, dstSap, new SisAddress(dst), ServiceType.Default, 0, uPdu);
        Sis(node, t, new SisBytes(t, Conn, SisCodec.Encode(request)));
    }

    /// <summary>Queues an S_UNIDATA_REQUEST from the node's client at <paramref name="t"/>, non-ARQ, TTL default.</summary>
    public void SendUnidata(int node, long t, uint dst, bool group, byte dstSap, int minRetx, byte[] uPdu)
    {
        var request = new SisUnidataRequest(0, dstSap, new SisAddress(dst, group), new ServiceType(2, MinRetx: (byte)minRetx), 0, uPdu);
        Sis(node, t, new SisBytes(t, Conn, SisCodec.Encode(request)));
    }

    /// <summary>Runs until nothing is left to happen or the next event is after <paramref name="untilMs"/>.</summary>
    public void Run(long untilMs)
    {
        while (_events.TryPeek(out _, out (long T, int Phase, long Seq) key) && key.T <= untilMs)
        {
            Action<long> action = _events.Dequeue();
            if (key.T < _now)
            {
                throw new InvalidOperationException("simulation time went backwards");
            }

            _now = key.T;
            action(key.T);
        }
    }

    private void At(long t, int phase, Action<long> action) => _events.Enqueue(action, (t, phase, _seq++));

    private void Sis(int node, long t, CoreInput input) => At(t, PhaseSis, _ => Feed(_nodes[node], input));

    private void Modem(SimNode node, CoreInput input) => At(input.T, PhaseModem, _ => Feed(node, input));

    /// <summary>One input to one core, then its outputs, as harness-contract.md 2 step 4 does.</summary>
    private void Feed(SimNode n, CoreInput input)
    {
        // Harness time checks: no running timer of this node is due before this input, nor at its
        // time for a modem or SIS input (corpus-format.md 4.5). The event order guarantees it; a
        // breach is a bug in the simulation, so it throws.
        foreach (KeyValuePair<TimerName, (long Deadline, long Gen)> kv in n.Timers)
        {
            if (kv.Value.Deadline < input.T || (kv.Value.Deadline == input.T && input is not TimerExpired))
            {
                throw new InvalidOperationException($"node {n.Index}: timer {kv.Key.ToTraceString()} due at {kv.Value.Deadline} missed by input at {input.T}");
            }
        }

        _log.Add(string.Create(CultureInfo.InvariantCulture, $"{input.T} n{n.Index} in {Describe(input)}"));
        n.Node.Handle(input);
        foreach (CoreOutput output in n.Node.DrainOutputs())
        {
            _log.Add(string.Create(CultureInfo.InvariantCulture, $"{input.T} n{n.Index} out {Describe(output)}"));
            Output(n, input.T, output);
        }
    }

    private void Output(SimNode n, long t, CoreOutput output)
    {
        switch (output)
        {
            case TimerStart s:
                long gen = ++n.TimerGen;
                long deadline = t + s.Ms;
                n.Timers[s.Name] = (deadline, gen);
                At(deadline, PhaseTimer, now =>
                {
                    // A timer restarted or cancelled since is stale: the harness never fires it.
                    if (n.Timers.TryGetValue(s.Name, out (long Deadline, long Gen) cur) && cur.Gen == gen)
                    {
                        n.Timers.Remove(s.Name);
                        Feed(n, new TimerExpired(now, s.Name));
                    }
                });
                break;
            case TimerCancel c:
                n.Timers.Remove(c.Name);
                break;
            case LinkState l:
                _linkStates.Add((n.Index, t, l.Peer, l.State));
                break;
            case Transmit tx:
                int given = n.Heard.Count;   // the headers this core had been given when it decided
                At(t + _channel.TurnaroundMs, PhaseChannel, now => AirStart(n, t, now, tx, given));
                break;
            case SisBytesOut b:
                SisPrimitive? p = SisCodec.DecodeFrame(b.Bytes.Span).Primitive;
                switch (p)
                {
                    case SisUnidataIndication ind:
                        _deliveries.Add(new SimDelivery(n.Index, t, ind.SrcAddress.Value, ind.UPdu.ToArray()));
                        break;
                    case SisBindRejected or SisUnbindIndication:
                        _violations.Add($"node {n.Index}: client got {p.GetType().Name} at {t}");
                        break;
                    case SisUnidataRequestRejected:
                        Rejections++;
                        break;
                }

                break;
        }
    }

    /// <summary>A burst goes on air: the C-0844 and C-0889 check, half duplex, then each receiver.</summary>
    private void AirStart(SimNode sender, long emitT, long start, Transmit tx, int given)
    {
        if (sender.OnAirUntil > start)
        {
            _violations.Add($"node {sender.Index}: transmit at {start} while still on air to {sender.OnAirUntil}");
        }

        BurstPlan plan = sender.Modem.PlanBurst(tx.Wn, tx.Interleaver, tx.Bytes.Length);
        var padded = new byte[plan.Blocks * plan.BytesPerBlock];
        tx.Bytes.Span.CopyTo(padded);   // the modem's zero fill (core-architecture.md 5.5)
        var burst = new SimBurst
        {
            Id = _bursts.Count, Sender = sender.Index, EmitT = emitT, Start = start, Plan = plan, Padded = padded, Output = tx,
            Dpdus = Layout(tx.Bytes.Span, plan, start),
        };
        _bursts.Add(burst);

        // C-0891: every EOT announces an end no earlier than the true end of the burst; otherwise
        // the check below would be weaker than it looks.
        foreach (SimDpdu d in burst.Dpdus)
        {
            if (d.Pdu.Header.Eot > 0 && d.AnnouncedEnd < burst.End)
            {
                _violations.Add($"node {sender.Index}: burst {burst.Id} announces an end at {d.AnnouncedEnd} before its true end {burst.End}");
            }
        }

        // C-0844, C-0889: a node never starts a transmission before the end time announced by the
        // EOT of any D_PDU of another node whose header its core had been given by the time it
        // decided to transmit (fed before or with the input that produced the transmit output).
        // The announced end is taken from the true start of the D_PDU's block, which is never
        // later than what the receiving core computes (it uses the arrival time).
        for (int i = 0; i < given; i++)
        {
            (long end, int burstId) = sender.Heard[i];
            if (start < end)
            {
                _violations.Add($"node {sender.Index}: burst {burst.Id} starts at {start} before {end}, announced by burst {burstId}");
            }
        }

        foreach (SimBurst other in _bursts)
        {
            if (other.Sender != sender.Index && other.Start <= start && other.End > start)
            {
                Collisions++;
            }
        }

        sender.OnAirUntil = burst.End;

        // Half duplex: keying up ends any reception in progress.
        if (sender.Rx is { } cut)
        {
            sender.Rx = null;
            Modem(sender, new BurstEnded(Math.Max(start + _channel.HostLatencyMs, cut.StartedInputT), BurstEndReason.SignalLost,
                tx.Wn, tx.Interleaver, tx.K, null, 0));
        }

        Modem(sender, new TransmitDone(burst.End + _channel.EndOfBurstDelayMs + _channel.HostLatencyMs));

        foreach (SimNode r in _nodes)
        {
            if (r == sender || r.OnAirUntil > start)
            {
                continue;   // the sender itself, or a receiver that is transmitting (half duplex)
            }

            if (r.Rx is { } locked)
            {
                locked.Corrupt.Add((start, burst.End));   // collision: neither is received cleanly
                continue;
            }

            if (Draw(_channel.BurstMissPpm))
            {
                continue;
            }

            var rx = new RxState(burst, start + plan.PreambleMs + _channel.HostLatencyMs);

            // A burst already on air that this receiver is not locked on (it missed it, or was
            // transmitting when it began) still corrupts the blocks of this one that it overlaps.
            foreach (SimBurst other in _bursts)
            {
                if (other != burst && other.Sender != r.Index && other.End > start)
                {
                    rx.Corrupt.Add((start, other.End));
                }
            }

            r.Rx = rx;
            Modem(r, new BurstStarted(rx.StartedInputT));
            for (int k = 0; k < plan.Blocks; k++)
            {
                int block = k;
                At(start + plan.PreambleMs + ((k + 1L) * plan.BlockMs), PhaseChannel, now => BlockEnd(r, rx, block, now));
            }

            At(burst.End, PhaseChannel, now =>
            {
                if (r.Rx == rx)
                {
                    r.Rx = null;
                    Modem(r, new BurstEnded(now + _channel.EndOfBurstDelayMs + _channel.HostLatencyMs, BurstEndReason.Eom,
                        tx.Wn, tx.Interleaver, tx.K, null, plan.Blocks));
                }
            });
        }
    }

    /// <summary>One block has arrived at a receiver: loss and collision decide whether its bytes are clean.</summary>
    private void BlockEnd(SimNode r, RxState rx, int k, long now)
    {
        if (r.Rx != rx)
        {
            return;   // reception was cut
        }

        SimBurst burst = rx.Burst;
        BurstPlan plan = burst.Plan;
        long blockStart = now - plan.BlockMs;
        bool collided = rx.Corrupt.Any(c => c.From < now && c.To > blockStart);
        bool lost = Draw(_channel.BlockErrorPpm);
        byte[] bytes = burst.Padded.AsSpan(k * plan.BytesPerBlock, plan.BytesPerBlock).ToArray();
        if (collided || lost)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)_prng.Uniform(256);
            }
        }

        rx.Clean.Add(!(collided || lost));

        // Headers these bytes complete intact: each lies wholly in delivered clean blocks.
        var heard = new List<(long End, int BurstId)>();
        foreach (SimDpdu d in burst.Dpdus)
        {
            int first = d.Offset / plan.BytesPerBlock;
            int last = (d.Offset + d.HeaderLength - 1) / plan.BytesPerBlock;
            if (last == k && Enumerable.Range(first, last - first + 1).All(b => rx.Clean[b]) && d.Pdu.Header.Eot > 0)
            {
                heard.Add((d.AnnouncedEnd, burst.Id));
            }
        }

        // Recorded as the core is given them, so a transmit decided earlier in the same ms does not count them.
        var input = new BytesReceived(now + _channel.HostLatencyMs, bytes);
        At(input.T, PhaseModem, _ =>
        {
            r.Heard.AddRange(heard);
            Feed(r, input);
        });
        rx.HeardEot |= heard.Count > 0;

        // A fade: the receiver loses the signal for the rest of the burst.
        if (k < plan.Blocks - 1 && Draw(_channel.FadePpm))
        {
            r.Rx = null;
            if (rx.HeardEot)
            {
                FadesUnderEot++;
            }

            Modem(r, new BurstEnded(now + _channel.EndOfBurstDelayMs + _channel.HostLatencyMs, BurstEndReason.SignalLost,
                burst.Output.Wn, burst.Output.Interleaver, burst.Output.K, null, k + 1));
        }
    }

    private bool Draw(int ppm) => ppm > 0 && (long)_prng.Uniform(1_000_000) < ppm;

    /// <summary>Steps through the D_PDUs of a transmission with the phase 1 codec (back to back, D-0003).</summary>
    private static List<SimDpdu> Layout(ReadOnlySpan<byte> bytes, BurstPlan plan, long start)
    {
        var list = new List<SimDpdu>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            DecodeResult<DpduHeaderInfo> h = DpduCodec.DecodeHeader(bytes[offset..], PeerEdition.Any);
            DecodeResult<Dpdu> r = DpduCodec.DecodeFirst(bytes[offset..], PeerEdition.Any);
            if (!h.IsSuccess || !r.IsSuccess)
            {
                throw new InvalidOperationException($"a transmission does not decode at offset {offset}: {r.Error}");
            }

            long blockStart = start + plan.PreambleMs + ((long)(offset / plan.BytesPerBlock) * plan.BlockMs);
            list.Add(new SimDpdu(offset, h.Consumed, r.Value!, blockStart + (r.Value!.Header.Eot * 500L)));
            offset += r.Consumed;
        }

        return list;
    }

    private static string Describe(CoreInput input) => input switch
    {
        TimerExpired e => "timer_expired " + e.Name.ToTraceString(),
        BytesReceived b => "bytes_received " + Convert.ToHexStringLower(b.Bytes.Span),
        BurstEnded e => "burst_ended " + e.Reason,
        SisBytes s => "sis_bytes " + Convert.ToHexStringLower(s.Bytes.Span),
        _ => input.GetType().Name,
    };

    private static string Describe(CoreOutput output) => output switch
    {
        TimerStart s => string.Create(CultureInfo.InvariantCulture, $"timer_start {s.Name.ToTraceString()} {s.Ms}"),
        TimerCancel c => "timer_cancel " + c.Name.ToTraceString(),
        Transmit t => "transmit " + Convert.ToHexStringLower(t.Bytes.Span),
        SisBytesOut b => "sis_bytes " + Convert.ToHexStringLower(b.Bytes.Span),
        LinkState l => string.Create(CultureInfo.InvariantCulture, $"link_state {l.Peer} {l.State}"),
        _ => output.GetType().Name,
    };

    private sealed class SimNode(int index, NodeConfig config, FixtureModem modem, CoreDriver node)
    {
        public int Index { get; } = index;
        public NodeConfig Config { get; } = config;
        public FixtureModem Modem { get; } = modem;
        public CoreDriver Node { get; } = node;
        public SortedDictionary<TimerName, (long Deadline, long Gen)> Timers { get; } = [];
        public long TimerGen { get; set; }
        public long OnAirUntil { get; set; }
        public RxState? Rx { get; set; }
        public List<(long End, int BurstId)> Heard { get; } = [];
    }

    private sealed class RxState(SimBurst burst, long startedInputT)
    {
        public SimBurst Burst { get; } = burst;
        public long StartedInputT { get; } = startedInputT;
        public List<(long From, long To)> Corrupt { get; } = [];
        public List<bool> Clean { get; } = [];
        public bool HeardEot { get; set; }
    }
}
