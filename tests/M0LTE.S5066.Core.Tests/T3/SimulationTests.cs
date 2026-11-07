using System.Text;
using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests.T3;

/// <summary>
/// T3: several real nodes over the seeded channel model of <see cref="Simulation"/> (plan 7;
/// core-architecture.md 9, P3-7). Each test is deterministic from its seed.
/// </summary>
public class SimulationTests(ITestOutputHelper output)
{
    private const byte Sap = 5;
    private const uint Broadcast = DpduAddress.MaxValue;   // 15.255.255.255, the all-ones 28-bit address

    /// <summary>A realistic host: 100 ms receive to transmit, 50 ms reporting, 50 ms more at the end of a burst.</summary>
    private static readonly ChannelModel Realistic = new(HostLatencyMs: 50, TurnaroundMs: 100, EndOfBurstDelayMs: 50);

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    [InlineData(4UL)]
    [InlineData(5UL)]
    public void Broadcast_to_two_receivers_with_loss_never_overlaps_an_announced_eot(ulong seed)
    {
        // A-0081 (each U_PDU sent the requested number of times), C-0844 and C-0889 (no node
        // transmits before an end time announced by an EOT it has heard). Node 0 broadcasts 20
        // U_PDUs, each asking for 2 retransmissions; nodes 1 and 2 broadcast 4 each, so all three
        // contend. Every link loses 10% of blocks and misses 2% of bursts.
        var channel = Realistic with { BlockErrorPpm = 100_000, BurstMissPpm = 20_000 };
        var sim = new Simulation(seed, channel, [Node("0.0.1.1"), Node("0.0.1.2"), Node("0.0.1.3")]);
        var sent = new List<(int Node, string Text)>();
        for (int n = 0; n < 3; n++)
        {
            sim.Bind(n, 0, Sap);
        }

        for (int i = 0; i < 20; i++)
        {
            Send(sim, sent, 0, 1_000 + (i * 45_000L), $"n0-{i:00}", 2);
        }

        for (int i = 0; i < 4; i++)
        {
            Send(sim, sent, 1, 20_000 + (i * 210_000L), $"n1-{i:00}", 1);
            Send(sim, sent, 2, 20_000 + (i * 210_000L), $"n2-{i:00}", 1);
        }

        sim.Run(3_600_000);

        sim.Violations.Should().BeEmpty();
        AssertDeliveriesAreGenuine(sim, sent);

        // Most U_PDUs get through to both receivers. Not all: a U_PDU and its repeats fit one
        // block here, so one lost block or one missed burst loses every copy at once.
        int fromZero = sent.Count(s => s.Node == 0);
        for (int r = 1; r <= 2; r++)
        {
            int got = sim.Deliveries.Count(d => d.Receiver == r && d.Src == sim.AddressOf(0));
            got.Should().BeGreaterThanOrEqualTo(fromZero * 7 / 10, $"receiver {r} of node 0's broadcasts (seed {seed})");
        }

        output.WriteLine($"seed {seed}: {sim.Bursts.Count} bursts, {sim.Collisions} collisions, {sim.Deliveries.Count} of {sent.Count * 2} deliveries");
    }

    [Fact]
    public void Without_loss_one_sender_delivers_everything_once_and_sends_every_copy()
    {
        // A-0081, C-0848: each D_PDU goes 1 + 2 times, all in time; both receivers get each U_PDU once.
        var sim = new Simulation(7, Realistic, [Node("0.0.1.1"), Node("0.0.1.2"), Node("0.0.1.3")]);
        var sent = new List<(int Node, string Text)>();
        for (int n = 0; n < 3; n++)
        {
            sim.Bind(n, 0, Sap);
        }

        for (int i = 0; i < 10; i++)
        {
            Send(sim, sent, 0, 1_000 + (i * 20_000L), $"n0-{i:00}", 2);
        }

        sim.Run(3_600_000);

        sim.Violations.Should().BeEmpty();
        AssertDeliveriesAreGenuine(sim, sent);
        sim.Deliveries.Count(d => d.Receiver == 1).Should().Be(10);
        sim.Deliveries.Count(d => d.Receiver == 2).Should().Be(10);
        sim.Bursts.SelectMany(b => b.Dpdus).Count(d => d.Pdu is NonArqData).Should().Be(30);
        sim.Collisions.Should().Be(0);
    }

    [Fact]
    public void A_run_is_a_function_of_its_seed()
    {
        static IReadOnlyList<string> Run(ulong seed)
        {
            var sim = new Simulation(seed, Realistic with { BlockErrorPpm = 100_000, BurstMissPpm = 20_000 },
                [Node("0.0.1.1"), Node("0.0.1.2"), Node("0.0.1.3")]);
            var sent = new List<(int Node, string Text)>();
            for (int n = 0; n < 3; n++)
            {
                sim.Bind(n, 0, Sap);
                for (int i = 0; i < 5; i++)
                {
                    Send(sim, sent, n, 1_000 + (n * 3_000L) + (i * 20_000L), $"n{n}-{i:00}", 1);
                }
            }

            sim.Run(3_600_000);
            return sim.Log;
        }

        IReadOnlyList<string> a = Run(11);
        Run(11).Should().Equal(a);
        Run(12).Should().NotEqual(a);
    }

    [Fact]
    public void The_overlap_check_catches_a_node_that_ignores_an_announced_eot()
    {
        // The C-0844 and C-0889 check must be able to fail. Node 2 is a rogue: the first time its
        // core is given bytes holding a D_PDU, it transmits a copy of that D_PDU at once, while the
        // burst it came from is still on air.
        var sim = new Simulation(3, Realistic, [Node("0.0.1.1"), Node("0.0.1.2"), Node("0.0.1.3")], (config, seed, modem) =>
        {
            CoreDriver real = CoreDriver.ForNode(config, seed, modem);
            if (config.LocalAddress != 0x0103)
            {
                return real;
            }

            bool done = false;
            bool swallowDone = false;
            var extra = new List<CoreOutput>();
            return new CoreDriver(
                input =>
                {
                    if (swallowDone && input is TransmitDone)
                    {
                        swallowDone = false;
                        return;
                    }

                    real.Handle(input);
                    if (!done && input is BytesReceived b && DpduCodec.DecodeFirst(b.Bytes.Span, PeerEdition.Any) is { IsSuccess: true } r)
                    {
                        done = swallowDone = true;
                        extra.Add(new Transmit(b.Bytes[..r.Consumed].ToArray(), 2, Interleaver.Long, 7, true));
                    }
                },
                () =>
                {
                    List<CoreOutput> outputs = real.DrainOutputs();
                    outputs.AddRange(extra);
                    extra.Clear();
                    return outputs;
                });
        });
        for (int n = 0; n < 3; n++)
        {
            sim.Bind(n, 0, Sap);
        }

        // Three U_PDUs in one burst of three blocks, so the burst is still on air when its first
        // block, holding the first D_PDU whole, reaches node 2.
        for (int i = 0; i < 3; i++)
        {
            sim.SendUnidata(0, 1_000, Broadcast, true, Sap, 0, new byte[200]);
        }
        sim.Run(600_000);

        sim.Violations.Should().ContainSingle().Which.Should().StartWith("node 2: burst 1 starts at");
        sim.Collisions.Should().Be(1);
    }

    /// <summary>
    /// How the reception window commitment (C-1052) behaves when the host is slow. One sender, two
    /// receivers, no other traffic. Each U_PDU asks for 3 retransmissions and the transmission
    /// length is one block, so the four copies need four transmissions, and every copy after the
    /// first must fit inside the window stamped by the transmission before. The window allows each
    /// later transmission <c>dts.max_transmission_ms</c> plus the LBT wait after an own transmission
    /// plus <c>num_cont_slots</c> whole slots (C-1051). The time it does not cover is the delay
    /// between the <c>transmit</c> output and the core hearing <c>transmit_done</c>, beyond the
    /// planned duration: turnaround plus end-of-burst reporting. The slack that absorbs it is the
    /// part of the slot allowance the contention wait did not use, the transmission time not
    /// usable as whole blocks, and the half-second rounding of the window.
    /// </summary>
    [Fact]
    public void Reception_window_commitment_under_host_latency()
    {
        var results = new List<(string Mode, int LatencyMs, int Sent, int Owed)>();
        foreach ((string mode, string label, int? position) in new (string, string, int?)[]
                 {
                     ("jitter", "jitter, 16 slots", null), ("ed3", "ed3, 16 slots", null),
                     ("slotted", "slotted, position 1 of 3", 1), ("slotted", "slotted, position 3 of 3", 3),
                 })
        {
            foreach (int latency in new[] { 0, 50, 100, 200, 300 })
            {
                (int sent, int owed, _) = RepeatRun(mode, position, latency, blockErrorPpm: 0, seed: 21);
                results.Add((label, latency, sent, owed));
                output.WriteLine($"{label,-26} latency {latency,3} ms: {sent,2} of {owed} copies sent");
            }
        }

        // Jitter and ed3 never draw more than num_cont_slots - 1 slots (K-0080), so a whole slot
        // (3 s here) of the allowance is always spare: no copy is dropped at any of these latencies.
        // The same holds in slotted mode for position 1, which waits 1 slot of the 3 allowed after
        // its own transmission (K-0082).
        results.Where(r => !r.Mode.Contains("position 3", StringComparison.Ordinal)).Should().OnlyContain(r => r.Sent == r.Owed);

        // Slotted at the last position waits all num_cont_slots slots after its own transmission
        // (K-0082), so none of the slot allowance is spare. Here the transmission length is exactly
        // whole blocks and the window lands on a half second, so there is no slack at all: with no
        // latency every copy goes, with any latency the last copy of every U_PDU is dropped. Only
        // the last is lost: a window stamped while two or more transmissions are still owed covers
        // a whole extra transmission allowance, so the earlier copies always fit.
        var last = results.Where(r => r.Mode.Contains("position 3", StringComparison.Ordinal)).ToList();
        last.Single(r => r.LatencyMs == 0).Sent.Should().Be(last[0].Owed);
        last.Where(r => r.LatencyMs > 0).Should().OnlyContain(r => r.Sent == r.Owed - (r.Owed / 4));
    }

    [Fact]
    public void Dropped_repeats_cost_deliveries_under_loss()
    {
        // The effect of the drops above when the channel loses blocks: slotted at the last position,
        // 50% block loss, 40 U_PDUs, the same seed, with and without 100 ms of turnaround and
        // reporting delay. Losing the fourth copy turns a 1 in 16 chance of losing a U_PDU at one
        // receiver into 1 in 8.
        (int sentFast, int owed, int deliveredFast) = RepeatRun("slotted", 3, 0, blockErrorPpm: 500_000, seed: 5, messages: 40);
        (int sentSlow, _, int deliveredSlow) = RepeatRun("slotted", 3, 100, blockErrorPpm: 500_000, seed: 5, messages: 40);
        output.WriteLine($"no latency: {sentFast} of {owed} copies, {deliveredFast} of 80 deliveries");
        output.WriteLine($"100 ms:     {sentSlow} of {owed} copies, {deliveredSlow} of 80 deliveries");
        sentFast.Should().Be(owed);
        sentSlow.Should().Be(owed - (owed / 4));
        deliveredSlow.Should().BeLessThan(deliveredFast);
    }

    /// <summary>
    /// One sender (node 0) and two receivers in the given CSMA mode; U_PDUs of 150 bytes, each
    /// with 3 retransmissions, one block per transmission, spaced so each U_PDU's copies are done
    /// before the next arrives. Returns the copies sent, the copies owed and the deliveries.
    /// </summary>
    private static (int Sent, int Owed, int Delivered) RepeatRun(string mode, int? position, int latencyMs, int blockErrorPpm, ulong seed, int messages = 10)
    {
        // Latency split as turnaround (2/3) and end-of-burst reporting (1/3); only their sum
        // matters to the sender, which measures from its transmit output to transmit_done.
        var channel = new ChannelModel(BlockErrorPpm: blockErrorPpm, TurnaroundMs: latencyMs - (latencyMs / 3), EndOfBurstDelayMs: latencyMs / 3);
        int?[] positions = position is int p ? [p, p == 1 ? 2 : 1, p == 3 ? 2 : 3] : [null, null, null];
        var nodes = new List<JsonObject>();
        for (int n = 0; n < 3; n++)
        {
            JsonObject o = Node($"0.0.1.{n + 1}", mode, positions[n], numContSlots: mode == "slotted" ? 3 : 16);
            o["dts"] = new JsonObject { ["max_transmission_ms"] = 600 + 4800 + 150 };   // one 256-byte block
            nodes.Add(o);
        }

        var sim = new Simulation(seed, channel, nodes);
        var sent = new List<(int Node, string Text)>();
        for (int n = 0; n < 3; n++)
        {
            sim.Bind(n, 0, Sap);
        }

        for (int i = 0; i < messages; i++)
        {
            Send(sim, sent, 0, 1_000 + (i * 300_000L), $"n0-{i:00}".PadRight(150, '.'), 3);
        }

        sim.Run((messages + 1) * 300_000L);
        sim.Violations.Should().BeEmpty();
        AssertDeliveriesAreGenuine(sim, sent);
        int copies = sim.Bursts.SelectMany(b => b.Dpdus).Count(d => d.Pdu is NonArqData);
        return (copies, messages * 4, sim.Deliveries.Count);
    }

    /// <summary>Every delivery is a U_PDU its source really sent, intact, and no receiver gets one twice (C-0539).</summary>
    private static void AssertDeliveriesAreGenuine(Simulation sim, List<(int Node, string Text)> sent)
    {
        foreach (SimDelivery d in sim.Deliveries)
        {
            string text = Encoding.ASCII.GetString(d.UPdu);
            sent.Should().Contain(s => sim.AddressOf(s.Node) == d.Src && s.Text == text && s.Node != d.Receiver,
                $"node {d.Receiver} got '{text}' from {d.Src}");
        }

        sim.Deliveries.GroupBy(d => (d.Receiver, Encoding.ASCII.GetString(d.UPdu))).Should().OnlyContain(g => g.Count() == 1);
    }

    private static void Send(Simulation sim, List<(int Node, string Text)> sent, int node, long t, string text, int minRetx)
    {
        sent.Add((node, text));
        sim.SendUnidata(node, t, Broadcast, true, Sap, minRetx, Encoding.ASCII.GetBytes(text));
    }

    private static JsonObject Node(string address, string mode = "jitter", int? slotPosition = null, int numContSlots = 16)
    {
        var csma = new JsonObject { ["mode"] = mode, ["num_cont_slots"] = numContSlots };
        if (slotPosition is int p)
        {
            csma["node_slot_position"] = p;
        }

        return new JsonObject
        {
            ["local_address"] = address,
            ["address_size"] = 7,   // the broadcast address needs all 28 bits (C-0118, C-0123); a smaller size throws, issue #35
            ["csma"] = csma,
            ["modem"] = new JsonObject
            {
                ["preamble_ms"] = 600,
                ["flush_ms"] = 150,
                ["waveforms"] = new JsonArray(new JsonObject
                {
                    ["wn"] = 2, ["interleaver"] = "long", ["block_bytes"] = 256, ["block_ms"] = 4800,
                }),
            },
        };
    }
}
