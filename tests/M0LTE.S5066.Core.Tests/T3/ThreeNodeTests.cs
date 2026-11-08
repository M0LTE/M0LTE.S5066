using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests.T3;

/// <summary>
/// T3, three nodes (P5-5, issue #59): the whole stack on one lossy channel. Node 0 may hold one
/// physical link at a time (<c>cas.max_links</c> 1); nodes 1 and 2 may hold several. Node 0 sends ARQ
/// U_PDUs to both other nodes, so one peer is always held off while the other has the link (B-0028),
/// and its soft links are closed to share the channel once past <c>sis.soft_link_max_ms</c>
/// (A-0014, interp A-0322) and for higher priority traffic (A-0198, interp A-0323). Nodes 1 and 2 keep
/// a second link between themselves, and send ARQ to node 0 too. Every node broadcasts non-ARQ U_PDUs
/// meanwhile. Links are made, closed when idle (A-0011) and made again, and every soft link close is a
/// clean close (B-0239). A client whose U_PDU is rejected (A-0207, A-0223) sends it again, as a client
/// would, from the S_UNIDATA_REQUEST_REJECTED its SIS reports (P6-1).
/// </summary>
/// <remarks>
/// <para>
/// What is checked, at every seed: no node transmits before an end time an EOT it holds announced
/// (C-0844, C-0889) and nothing is on air twice from one node (the <see cref="Simulation"/> checks);
/// every ARQ U_PDU arrives exactly once (C-0764), and those never rejected arrive in the order sent; no
/// broadcast arrives twice or corrupted, and most arrive; node 0 never has more than one link active
/// or being made (B-0402); and whenever both ends of a link say linked, the receiver's RX LWE lies
/// between the sender's TX LWE and TX UWE + 1 (C-0764, C-0790). After the traffic every link has been
/// closed when idle, both ends agree, and nothing is left held anywhere.
/// </para>
/// <para>
/// Channel access is run in jitter mode and in slotted mode, each node in its own slot (K-0080 to
/// K-0082, K-0089, K-0090).
/// </para>
/// <para>
/// The simulation found four protocol issues in P5-5, since fixed and now checked strictly: a
/// half-open link made again kept stale ARQ state at the end that held it (#70, interp B-0452); a
/// confirmed in-order C_PDU held behind a gap was lost when the receiver's link ended (#71, interp
/// C-1254); in-order C_PDUs to one peer were delivered in priority order (#72, interp C-1253); and a
/// node held off by a peer's reason 4 with no other link never asked again (#73, interp B-0451).
/// In-order delivery is checked across priorities. Broadcast repeats dropped for want of reception
/// window (#38) are counted, not failed. In a local sweep of 300 seeds of each variant
/// (S5066_T3_SEEDS=300) 1199 of 1200 runs passed; seed 229 slotted any order failed only the end
/// check, a link still in use when the run ended. S5066_T3_DUMP=seed-mode-inOrder writes one run's
/// trace to the temporary directory.
/// </para>
/// </remarks>
public class ThreeNodeTests(ITestOutputHelper output)
{
    private const byte Sap = 5;
    private const uint Broadcast = DpduAddress.MaxValue;
    private const byte ArqMark = 0xA0;
    private const byte BroadcastMark = 0xB0;
    private const int MaxAttempts = 30;
    private static readonly object SummaryLock = new();

    /// <summary>Seeds 1 to 10, or to S5066_T3_SEEDS for a local sweep.</summary>
    private static ulong SeedCount => ulong.TryParse(Environment.GetEnvironmentVariable("S5066_T3_SEEDS"), out ulong n) ? n : 10;

    public static TheoryData<ulong, string, bool> Cases()
    {
        var data = new TheoryData<ulong, string, bool>();
        for (ulong seed = 1; seed <= SeedCount; seed++)
        {
            data.Add(seed, "jitter", false);
            data.Add(seed, "slotted", false);
            data.Add(seed, "jitter", true);
            data.Add(seed, "slotted", true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Three_nodes_with_links_sessions_and_broadcast_under_loss(ulong seed, string mode, bool inOrder)
    {
        Run run = Execute(seed, mode, inOrder, Lossy);
        output.WriteLine(run.Summary);
        if (Environment.GetEnvironmentVariable("S5066_T3_SUMMARY") is { } file)
        {
            lock (SummaryLock)
            {
                File.AppendAllLines(file, [run.Summary.Split('\n')[0]]);
            }
        }
        run.Failures.Should().BeEmpty(run.Summary);
    }

    /// <summary>
    /// The runs above must exercise what they claim to: across the seeds, soft links held off for
    /// want of a link (B-0028), closed when idle (A-0011), to share the channel (A-0014, interp
    /// A-0322) and for higher priority (A-0198, interp A-0323), links made again and again, and
    /// collisions that CSMA allows, and contention rounds won (K-0090) and lost to a busy channel (K-0089).
    /// </summary>
    [Fact]
    public void The_runs_exercise_every_feature()
    {
        var totals = new Dictionary<string, int>();
        foreach (string mode in new[] { "jitter", "slotted" })
        {
            for (ulong seed = 1; seed <= 10; seed++)
            {
                foreach ((string k, int v) in Execute(seed, mode, false, Lossy).Counts)
                {
                    totals[k] = totals.GetValueOrDefault(k) + v;
                }
            }
        }

        output.WriteLine(string.Join(", ", totals.Select(kv => $"{kv.Value} {kv.Key}")));
        foreach (string k in new[] { "links made", "held off", "idle closes", "share channel closes", "higher priority closes", "collisions", "contention rounds won", "contention rounds lost", "resends" })
        {
            totals[k].Should().BePositive(k);
        }
    }

    /// <summary>The result of one run: the checks that failed and a summary of what happened.</summary>
    internal sealed record Run(List<string> Failures, string Summary, Dictionary<string, int> Counts);

    /// <summary>A U_PDU a client asked for: who from, to whom (-1 for broadcast), its bytes and priority, and how often it was sent.</summary>
    private sealed class Msg(int id, int src, int dst, byte[] payload, byte priority)
    {
        public int Id { get; } = id;
        public int Src { get; } = src;
        public int Dst { get; } = dst;
        public byte[] Payload { get; } = payload;
        public byte Priority { get; } = priority;
        public int Attempts { get; set; } = 1;
        public List<string> Rejections { get; } = [];
    }

    /// <summary>10% of blocks lost, 3% of bursts missed, 1% fades per block, a realistic host.</summary>
    internal static readonly ChannelModel Lossy = new(BlockErrorPpm: 100_000, BurstMissPpm: 30_000, HostLatencyMs: 50, TurnaroundMs: 100, EndOfBurstDelayMs: 50, FadePpm: 10_000);

    internal static Run Execute(ulong seed, string mode, bool inOrder, ChannelModel channel)
    {
        var parts = new List<Parts>();
        CoreDriver Factory(NodeConfig config, ulong s, IModemPort modem)
        {
            var context = new CoreContext(config, modem);
            var mac = new MacSublayer(context, new SplitMix64(s));
            var dts = new DtsSublayer(context, mac);
            mac.Transmitter = dts.Transmitter;
            var sis = new SisSublayer(context);
            var p = new Parts(context, sis, dts);
            var dispatcher = new Dispatcher(context, new SisTap(sis, p), new CasTap(new CasSublayer(context), p), dts, mac);
            parts.Add(p);
            return new CoreDriver(dispatcher.Handle, context.TakeOutputs);
        }

        var sim = new Simulation(seed, channel, [Node("0.0.1.1", mode, 1, 1), Node("0.0.1.2", mode, 2, 4), Node("0.0.1.3", mode, 3, 4)], Factory);
        var failures = new List<string>();
        var msgs = new List<Msg>();
        var byKey = new Dictionary<string, Msg>();

        void Arq(int src, int dst, long t, int length, byte priority)
        {
            int id = msgs.Count;
            var u = new byte[length];
            u[0] = ArqMark;
            u[1] = (byte)src;
            u[2] = (byte)dst;
            u[3] = (byte)(id >> 8);
            u[4] = (byte)id;
            for (int i = 5; i < length; i++)
            {
                u[i] = (byte)((id * 7) + i);
            }

            var m = new Msg(id, src, dst, u, priority);
            msgs.Add(m);
            byKey[Convert.ToHexString(u)] = m;
            sim.SendArq(src, t, sim.AddressOf(dst), Sap, u, priority, confirm: true, inOrder);
        }

        void Bcast(int src, long t, int length)
        {
            int id = msgs.Count;
            var u = new byte[length];
            u[0] = BroadcastMark;
            u[1] = (byte)src;
            u[3] = (byte)(id >> 8);
            u[4] = (byte)id;
            var m = new Msg(id, src, -1, u, 0);
            msgs.Add(m);
            byKey[Convert.ToHexString(u)] = m;
            sim.SendUnidata(src, t, Broadcast, true, Sap, 1, u);
        }

        // A client sends a rejected U_PDU again a second later (A-0105, A-0107).
        var rejectReasons = new Dictionary<string, int>();
        void Resend(int node, long t, string key, string reason)
        {
            rejectReasons[reason] = rejectReasons.GetValueOrDefault(reason) + 1;
            if (!byKey.TryGetValue(key, out Msg? m) || m.Src != node || m.Dst < 0)
            {
                failures.Add($"node {node} got a rejection for a U_PDU it did not send ARQ, reason {reason} at {t}");
                return;
            }

            m.Rejections.Add(reason);
            if (m.Attempts >= MaxAttempts)
            {
                failures.Add($"U_PDU {m.Id} from node {m.Src} rejected {m.Attempts} times");
                return;
            }

            m.Attempts++;
            sim.SendArq(node, t + 1_000, sim.AddressOf(m.Dst), Sap, m.Payload, m.Priority, confirm: true, inOrder);
        }

        sim.Rejected = (node, t, rej) => Resend(node, t, Convert.ToHexString(rej.UPdu.Span), rej.Reason.ToString(System.Globalization.CultureInfo.InvariantCulture));

        for (int n = 0; n < 3; n++)
        {
            sim.BindArq(n, 0, Sap);
        }

        // Phase 1, 0 to 420 s: node 0 streams to node 1 at priority 2, and to node 2 at the same
        // priority, so node 2 is held off until the link to node 1 runs past its maximum and is
        // closed to share the channel (or goes idle).
        for (int i = 0; i < 18; i++)
        {
            Arq(0, 1, 1_000 + (i * 20_000L), 60 + (i * 29 % 140), 2);
        }

        for (int i = 0; i < 6; i++)
        {
            Arq(0, 2, 30_000 + (i * 50_000L), 50 + (i * 31 % 120), 2);
        }

        // Phase 2, 600 to 900 s: node 0 streams to node 1 at priority 1, and higher priority
        // traffic for node 2 arrives in the middle of it.
        for (int i = 0; i < 12; i++)
        {
            Arq(0, 1, 600_000 + (i * 22_000L), 70 + (i * 23 % 120), 1);
        }

        for (int i = 0; i < 3; i++)
        {
            Arq(0, 2, 680_000 + (i * 70_000L), 40 + (i * 17), 12);
        }

        // Throughout: the second link, nodes 1 and 2 both ways, and some ARQ to node 0.
        for (int i = 0; i < 10; i++)
        {
            Arq(1, 2, 15_000 + (i * 85_000L), 50 + (i * 37 % 150), (byte)(i % 4));
            Arq(2, 1, 60_000 + (i * 90_000L), 40 + (i * 41 % 130), (byte)(i % 3));
        }

        for (int i = 0; i < 3; i++)
        {
            Arq(1, 0, 200_000 + (i * 300_000L), 60 + (i * 20), 3);
            Arq(2, 0, 350_000 + (i * 280_000L), 50 + (i * 25), 3);
        }

        // Broadcasts from every node, each sent twice (A-0081).
        for (int i = 0; i < 12; i++)
        {
            for (int n = 0; n < 3; n++)
            {
                Bcast(n, 5_000 + (n * 9_000L) + (i * 80_000L), 30 + (i * 7 % 40));
            }
        }

        // Probes: link limits at node 0 (B-0402) and window consistency on every link both ends hold.
        int probes = 0;
        var windowFaults = new List<(int A, int B, long T, string Text)>();
        for (long t = 5_000; t < RunMs; t += 5_000)
        {
            sim.Probe(t, now =>
            {
                probes++;
                for (int a = 0; a < 3; a++)
                {
                    int active = Enumerable.Range(0, 3).Count(b => b != a && LastState(sim, a, sim.AddressOf(b)) is not (null or LinkStateName.Unlinked));
                    if (a == 0 && active > 1)
                    {
                        failures.Add($"node 0 has {active} links active or being made at {now}");
                    }

                    for (int b = 0; b < 3; b++)
                    {
                        if (b == a || LastState(sim, a, sim.AddressOf(b)) != LinkStateName.Linked || LastState(sim, b, sim.AddressOf(a)) != LinkStateName.Linked)
                        {
                            continue;
                        }

                        ArqLink? tx = parts[a].Queue.LinkOf(sim.AddressOf(b));
                        ArqLink? rx = parts[b].Queue.LinkOf(sim.AddressOf(a));
                        if (tx is null || rx is null || tx.Suspended || rx.Suspended || tx.ShortFamily != rx.ShortFamily)
                        {
                            continue;
                        }

                        int sent = tx.Seq.Distance(tx.TxLwe, tx.Seq.Add(tx.TxUwe, 1));
                        int heard = tx.Seq.Distance(tx.TxLwe, rx.RxLwe);
                        if (heard > sent)
                        {
                            windowFaults.Add((a, b, now, $"windows out of step at {now}: node {a} TX LWE {tx.TxLwe} UWE {tx.TxUwe}, node {b} RX LWE {rx.RxLwe}"));
                        }
                    }
                }
            });
        }

        sim.Run(RunMs);
        if (Environment.GetEnvironmentVariable("S5066_T3_DUMP") is { } dump && dump == $"{seed}-{mode}-{inOrder}")
        {
            File.WriteAllLines($"{Path.GetTempPath()}t3-{seed}-{mode}.log", sim.Log);
            File.WriteAllLines($"{Path.GetTempPath()}t3-{seed}-{mode}.bursts", sim.Bursts.SelectMany(b => new[] { $"burst {b.Id} n{b.Sender} {b.Start}-{b.End}" }
                .Concat(b.Dpdus.Select(d => "  " + d.Pdu.ToString()))));
            var rx = new List<string>();
            var buffers = new Dictionary<string, (long T, List<byte> Bytes)>();
            void Flush(string node)
            {
                if (buffers.Remove(node, out (long T, List<byte> Bytes) buf))
                {
                    byte[] bytes = [.. buf.Bytes];
                    for (int i = 0; i + 1 < bytes.Length; i++)
                    {
                        if (bytes[i] == 0x90 && bytes[i + 1] == 0xEB && DpduCodec.DecodeFirst(bytes.AsSpan(i), PeerEdition.Any) is { IsSuccess: true } r)
                        {
                            rx.Add($"{buf.T} {node} @{i} {r.Value}");
                        }
                    }
                }
            }

            foreach (string line in sim.Log)
            {
                string[] f = line.Split(' ');
                if (f.Length >= 4 && f[2] == "in" && (f[3] == "BurstStarted" || f[3] == "burst_ended"))
                {
                    Flush(f[1]);
                }
                else if (f.Length >= 5 && f[2] == "in" && f[3] == "bytes_received")
                {
                    if (!buffers.TryGetValue(f[1], out (long T, List<byte> Bytes) buf))
                    {
                        buf = (long.Parse(f[0], System.Globalization.CultureInfo.InvariantCulture), []);
                        buffers[f[1]] = buf;
                    }

                    buf.Bytes.AddRange(Convert.FromHexString(f[4]));
                }
            }

            File.WriteAllLines($"{Path.GetTempPath()}t3-{seed}-{mode}.rx", rx);
            File.WriteAllLines($"{Path.GetTempPath()}t3-{seed}-{mode}.deliveries", sim.Deliveries.Select(d => $"{d.T} n{d.Receiver} from {d.Src} {Convert.ToHexString(d.UPdu)}"));
        }

        failures.AddRange(sim.Violations);

        failures.AddRange(windowFaults.Select(w => w.Text));

        // ARQ: each U_PDU at its destination exactly once. A U_PDU rejected and sent again may have
        // arrived the first time (its acknowledgement lost before the link ended), so it may arrive twice.
        var delivered = new Dictionary<int, int>();
        foreach (SimDelivery d in sim.Deliveries)
        {
            if (!byKey.TryGetValue(Convert.ToHexString(d.UPdu), out Msg? m) || sim.AddressOf(m.Src) != d.Src || (m.Dst >= 0 && m.Dst != d.Receiver) || m.Src == d.Receiver)
            {
                failures.Add($"node {d.Receiver} got a U_PDU nobody sent it at {d.T}");
                continue;
            }

            if (m.Dst >= 0)
            {
                delivered[m.Id] = delivered.GetValueOrDefault(m.Id) + 1;
                if (delivered[m.Id] > 1 && m.Rejections.Count == 0)
                {
                    failures.Add($"ARQ U_PDU {m.Id} ({m.Src} to {m.Dst}) delivered twice, the second at {d.T}, never rejected");
                }
            }
        }

        foreach (Msg m in msgs.Where(m => m.Dst >= 0 && !delivered.ContainsKey(m.Id)))
        {
            string key = Convert.ToHexString(m.Payload);
            string silent = string.Join(",", parts[m.Src].Rejected.Where(r => r.UPdu == key).Select(r => $"{r.Reason}@{r.T}"));
            string confirmed = string.Join(",", parts[m.Src].Confirmed.Where(r => r.UPdu == key).Select(r => r.T));
            bool arrived = parts[m.Dst].Arrived.Any(r => r.UPdu == key);
            string ends = string.Join(",", parts[m.Dst].CasTimers.Where(x => x.Name.Peer == sim.AddressOf(m.Src)).Select(x => $"{x.Name.ToTraceString()}@{x.T}")
                .Concat(parts[m.Dst].LostAt.Where(x => x.Peer == sim.AddressOf(m.Src)).Select(x => $"lost@{x.T}")));
            failures.Add($"ARQ U_PDU {m.Id} ({m.Src} to {m.Dst}, priority {m.Priority}, {m.Attempts} attempts, rejections [{string.Join(",", m.Rejections)}], core rejections [{silent}], confirmed [{confirmed}], arrived {arrived}, receiver CAS [{ends}]) never delivered");
        }

        // In order (C-0017, C-0178): U_PDUs sent once, from one node to another, arrive in the order sent.
        if (inOrder)
        {
            for (int s = 0; s < 3; s++)
            {
                for (int r = 0; r < 3; r++)
                {
                    var order = sim.Deliveries
                        .Where(d => d.Receiver == r && d.Src == sim.AddressOf(s) && d.UPdu[0] == ArqMark)
                        .Select(d => byKey.GetValueOrDefault(Convert.ToHexString(d.UPdu)))
                        .OfType<Msg>()
                        .Where(m => m.Attempts == 1)
                        .ToList();
                    List<int> ids = order.Select(m => m.Id).ToList();   // across priorities (C-0017, interp C-1253)
                    if (!ids.SequenceEqual(ids.Order()))
                    {
                        failures.Add($"ARQ U_PDUs from node {s} to node {r} out of order: {string.Join(",", order.Select(m => $"{m.Id}p{m.Priority}"))}");
                    }
                }
            }
        }

        // Broadcast: never twice, and most of each sender's at each receiver.
        foreach (var g in sim.Deliveries.Where(d => d.UPdu[0] == BroadcastMark).GroupBy(d => (d.Receiver, Convert.ToHexString(d.UPdu))))
        {
            if (g.Count() > 1)
            {
                failures.Add($"node {g.Key.Receiver} got a broadcast {g.Count()} times");
            }
        }

        // Every broadcast went on air, each segment twice unless the repeat was dropped (A-0081,
        // C-0848, C-1052); and a fair share arrived. Two copies in one burst die together when the burst is
        // lost or collides, so the share is modest.
        int broadcasts = 0;
        int repeatsDropped = 0;
        for (int sender = 0; sender < 3; sender++)
        {
            int sent = msgs.Count(m => m.Dst < 0 && m.Src == sender);
            var copies = sim.Bursts.Where(x => x.Sender == sender).SelectMany(x => x.Dpdus)
                .Select(d => d.Pdu).OfType<NonArqData>().Where(d => d.Fields.GroupAddress)
                .GroupBy(d => (d.Fields.CPduId, d.Fields.SegmentOffset)).ToList();
            int cpdus = copies.Select(g => g.Key.CPduId).Distinct().Count();
            if (cpdus != sent)
            {
                failures.Add($"node {sender} put {cpdus} of its {sent} broadcasts on air");
            }

            // A repeat is dropped when it cannot go within the reception window already announced
            // (C-1052), which a node that keeps losing contention can run out of: issue #38.
            repeatsDropped += copies.Count(g => g.Count() < 2);
        }

        for (int r = 0; r < 3; r++)
        {
            int got = sim.Deliveries.Count(d => d.Receiver == r && d.UPdu[0] == BroadcastMark);
            int owed = msgs.Count(m => m.Dst < 0 && m.Src != r);
            broadcasts += got;
            if (got < owed / 6)
            {
                failures.Add($"node {r} got only {got} of the {owed} broadcasts of the others");
            }
        }

        // The end: every link closed when idle, both ends agreeing, nothing held anywhere.
        for (int a = 0; a < 3; a++)
        {
            for (int b = 0; b < 3; b++)
            {
                if (a != b && LastState(sim, a, sim.AddressOf(b)) is LinkStateName s and not LinkStateName.Unlinked)
                {
                    failures.Add($"node {a} ends {s} with node {b}");
                }
            }

            if (parts[a].Sis.Outstanding.Count > 0 || parts[a].Sis.AwaitingSoftLink.Count > 0)
            {
                failures.Add($"node {a} SIS ends with {parts[a].Sis.Outstanding.Count} outstanding and {parts[a].Sis.AwaitingSoftLink.Count} awaiting a soft link");
            }

            if (parts[a].Dts.HeldArq.Count > 0)
            {
                failures.Add($"node {a} DTS ends holding {parts[a].Dts.HeldArq.Count} ARQ C_PDUs");
            }
        }

        var counts = new Dictionary<string, int>
        {
            ["links made"] = parts.Sum(p => p.Made),
            ["held off"] = parts.Sum(p => p.HeldOff),
            ["idle closes"] = parts.Sum(p => p.Breaks.GetValueOrDefault(SisBreakReason.Idle)),
            ["share channel closes"] = parts.Sum(p => p.Breaks.GetValueOrDefault(SisBreakReason.ShareChannel)),
            ["higher priority closes"] = parts.Sum(p => p.Breaks.GetValueOrDefault(SisBreakReason.HigherPriorityLink)),
            ["connections lost"] = parts.Sum(p => p.Lost),
            ["calls failed"] = parts.Sum(p => p.Failed),
            ["collisions"] = sim.Collisions,
            ["contention rounds won"] = sim.Log.Count(l => l.EndsWith(" in timer_expired csma.cont_wait", StringComparison.Ordinal)),
            ["contention rounds lost"] = sim.Log.Count(l => l.EndsWith(" out timer_cancel csma.cont_wait", StringComparison.Ordinal)),
            ["resends"] = msgs.Sum(m => m.Attempts - 1),
            ["resets"] = sim.Bursts.Sum(x => x.Dpdus.Count(d => d.Pdu is ResetWinResync or Ed3ResetWinResync)),
            ["probes"] = probes,
            ["broadcast repeats dropped (#38)"] = repeatsDropped,
            ["duplicates after a resend"] = delivered.Values.Sum(v => v - 1),
        };
        string summary = $"seed {seed} {mode} {(inOrder ? "in order" : "any order")}: {sim.Bursts.Count} bursts, {delivered.Count} of {msgs.Count(m => m.Dst >= 0)} ARQ, {broadcasts} broadcast deliveries, "
            + string.Join(", ", counts.Select(kv => $"{kv.Value} {kv.Key}"))
            + ", rejections " + string.Join(" ", rejectReasons.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"))
            + (failures.Count > 0 ? "\n" + string.Join("\n", failures) + "\n" + string.Join("\n", sim.LinkStates.Select(l => $"{l.T} n{l.Node} {DpduAddress.ToDotted(l.Peer)} {l.State}")) : "");
        return new Run(failures, summary, counts);
    }

    private const long RunMs = 4_000_000;


    private static LinkStateName? LastState(Simulation sim, int node, uint peer)
    {
        for (int i = sim.LinkStates.Count - 1; i >= 0; i--)
        {
            if (sim.LinkStates[i].Node == node && sim.LinkStates[i].Peer == peer)
            {
                return sim.LinkStates[i].State;
            }
        }

        return null;
    }

    internal static JsonObject Node(string address, string mode, int slot, int maxLinks)
    {
        var csma = new JsonObject { ["mode"] = mode, ["num_cont_slots"] = mode == "slotted" ? 3 : 4, ["cont_slot_width_ms"] = 1000, ["lbt_wait_vdcd_ms"] = 1000, ["lbt_wait_dcd_ms"] = 5000 };
        if (mode == "slotted")
        {
            csma["node_slot_position"] = slot;
        }

        return new JsonObject
        {
            ["local_address"] = address,
            ["csma"] = csma,
            ["modem"] = new JsonObject
            {
                ["preamble_ms"] = 600,
                ["flush_ms"] = 150,
                ["waveforms"] = new JsonArray(new JsonObject
                {
                    ["wn"] = 2, ["interleaver"] = "long", ["block_bytes"] = 64, ["block_ms"] = 1200,
                }),
            },
            ["cas"] = new JsonObject
            {
                ["max_links"] = maxLinks,
                ["link_request_timeout_ms"] = 30_000,
                ["link_request_retries"] = 10,
                ["break_retries"] = 3,
                // Test departure: the first-data wait (B-0215, B-0406) is made longer than the run. A
                // Called node that aborts on it does so locally, so the Caller holds a half-open link until
                // WARNINGs reach it (B-0426) or 16 answer waits pass (C-1250); for node 0, which may hold
                // one link, that can stop all its traffic for minutes. Since #70 and #71 that is a delay,
                // not a fault: with the derived wait a 300-seed sweep showed no safety failure, and four
                // runs in 1200 still had traffic in flight when the run ended.
                ["first_data_timeout_ms"] = 10_000_000,
            },
            ["sis"] = new JsonObject { ["soft_link_idle_ms"] = 60_000, ["soft_link_min_hold_ms"] = 30_000, ["soft_link_max_ms"] = 150_000 },
            ["dts"] = new JsonObject { ["max_segment_size"] = 40, ["max_window"] = 6, ["max_transmission_ms"] = 12_000, ["ack_wait_ms"] = 20_000 },
        };
    }

    /// <summary>The parts of one node the checks look at, and what the taps saw pass between its sublayers.</summary>
    private sealed class Parts(CoreContext context, SisSublayer sis, DtsSublayer dts)
    {
        public CoreContext Context { get; } = context;
        public SisSublayer Sis { get; } = sis;
        public DtsSublayer Dts { get; } = dts;
        public TransmitQueue Queue => (TransmitQueue)Dts.Transmitter;
        public Dictionary<SisBreakReason, int> Breaks { get; } = [];
        public int Made { get; set; }
        public int HeldOff { get; set; }
        public int Failed { get; set; }
        public int Lost { get; set; }

        /// <summary>The U_PDU (as hex) of each ARQ C_UNIDATA_REQUEST handed to the CAS, by handle.</summary>
        public Dictionary<int, string> Handles { get; } = [];

        /// <summary>Every C_UNIDATA_REQUEST_REJECTED: the time, the U_PDU (as hex) and the reason.</summary>
        public List<(long T, string UPdu, CReason Reason)> Rejected { get; } = [];

        /// <summary>Every link made or refused, in order: the peer, made, and refused with the peer's reason 4.</summary>
        public List<(uint Peer, bool Made, bool PeerTooMany)> LinkEvents { get; } = [];

        /// <summary>Every CAS timer expiry: the time and the name.</summary>
        public List<(long T, TimerName Name)> CasTimers { get; } = [];

        /// <summary>Every D_CONNECTION_LOST: the time and the peer.</summary>
        public List<(long T, uint Peer)> LostAt { get; } = [];

        /// <summary>Every C_UNIDATA_REQUEST_CONFIRM: the time and the U_PDU (as hex).</summary>
        public List<(long T, string UPdu)> Confirmed { get; } = [];

        /// <summary>Every ARQ DATA C_PDU the DTS passed up to the CAS: the time, the source and the U_PDU (as hex).</summary>
        public List<(long T, uint Src, string UPdu)> Arrived { get; } = [];
    }

    /// <summary>Passes everything to the real CAS, noting the soft link closes (C_PHYSICAL_LINK_BREAK) and lost connections.</summary>
    private sealed class CasTap(ICasSublayer inner, Parts parts) : ICasSublayer
    {
        public void OnConfigChanged(NodeConfig oldConfig, NodeConfig newConfig) => inner.OnConfigChanged(oldConfig, newConfig);

        public void OnTimer(TimerName name)
        {
            parts.CasTimers.Add((parts.Context.Now, name));
            inner.OnTimer(name);
        }

        public void On(ToCas primitive)
        {
            switch (primitive)
            {
                case CPhysicalLinkBreak b:
                    parts.Breaks[b.Reason] = parts.Breaks.GetValueOrDefault(b.Reason) + 1;
                    break;
                case DConnectionLost l:
                    parts.Lost++;
                    parts.LostAt.Add((parts.Context.Now, l.Peer));
                    break;
                case DUnidataIndication { Arq: true } ind when CpduCodec.Decode(ind.CPdu.Span).Value is CpduData cd && SpduCodec.Decode(cd.SPdu.Span).Value is SpduData sd:
                    parts.Arrived.Add((parts.Context.Now, ind.Src, Convert.ToHexString(sd.UPdu.Span)));
                    break;
                case CUnidataRequest { Arq: true } r when SpduCodec.Decode(r.SPdu.Span).Value is SpduData d:
                    parts.Handles[r.Handle] = Convert.ToHexString(d.UPdu.Span);
                    break;
            }

            inner.On(primitive);
        }
    }

    /// <summary>Passes everything to the real SIS, noting links made, held off and failed.</summary>
    private sealed class SisTap(ISisSublayer inner, Parts parts) : ISisSublayer
    {
        public void OnConfigChanged(NodeConfig oldConfig, NodeConfig newConfig) => inner.OnConfigChanged(oldConfig, newConfig);

        public void OnTimer(TimerName name) => inner.OnTimer(name);

        public void OnConnect(int conn) => inner.OnConnect(conn);

        public void OnBytes(int conn, ReadOnlyMemory<byte> bytes) => inner.OnBytes(conn, bytes);

        public void OnDisconnect(int conn) => inner.OnDisconnect(conn);

        public void OnShutdown() => inner.OnShutdown();

        public void OnReceiveStarted() => inner.OnReceiveStarted();

        public void OnReceiveEnded() => inner.OnReceiveEnded();

        public void On(ToSis primitive)
        {
            switch (primitive)
            {
                case CPhysicalLinkMade m:
                    parts.Made++;
                    parts.LinkEvents.Add((m.Peer, true, false));
                    break;
                case CPhysicalLinkRejected { Reason: LinkReason.MaxLinksInUse or LinkReason.PeerTooManyLinks } h:
                    parts.HeldOff++;
                    parts.LinkEvents.Add((h.Peer, false, h.Reason == LinkReason.PeerTooManyLinks));
                    break;
                case CPhysicalLinkRejected f:
                    parts.Failed++;
                    parts.LinkEvents.Add((f.Peer, false, false));
                    break;
                case CUnidataRequestConfirm c:
                    parts.Confirmed.Add((parts.Context.Now, parts.Handles.GetValueOrDefault(c.Handle, "?")));
                    break;
                case CUnidataRequestRejected j:
                    string key = parts.Handles.GetValueOrDefault(j.Handle, "?");
                    parts.Rejected.Add((parts.Context.Now, key, j.Reason));
                    break;
            }

            inner.On(primitive);
        }
    }
}
