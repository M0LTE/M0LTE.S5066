using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests.T3;

/// <summary>
/// T3 for issues #61 and #64: a node with a deep queue of ARQ data for one peer, whose window is
/// large enough that it always has something new to send. Issue #61: when the peer dies, link loss
/// is time-based (C-1250), so the connection goes within 16 answer waits of the death plus one round
/// of waiting and transmitting. Issue #64: when the peer is alive, the sender must not capture the
/// channel. It returns to air after <c>lbt_wait_vdcd_ms</c> plus a contention wait (K-0074), and the
/// receiver, having lost one round, was held in LBT_WAIT with <c>lbt_wait_dcd_ms</c> (30 s) at each
/// idle, so it never got to answer and the live link was lost after about 13 min (40 of 40 seeds).
/// With K-0120 the held timer takes the value the LBT rules give for that idle, so the receiver's
/// zero wait under rule 1 (K-0067) lets it contend for every turn.
/// </summary>
public class DeadPeerTests(ITestOutputHelper output)
{
    private const byte Sap = 5;
    private const long Death = 600_000;

    [Theory]
    [InlineData(1UL, 6)]
    [InlineData(2UL, 6)]
    [InlineData(3UL, 6)]
    [InlineData(1UL, 4096)]
    [InlineData(2UL, 4096)]
    [InlineData(3UL, 4096)]
    public void A_dead_peer_with_a_deep_queue_is_declared_lost_within_the_bound(ulong seed, int window)
    {
        (Simulation sim, long wait) = Run(seed, window, kill: true);

        string trace = Trace(sim);
        output.WriteLine($"seed {seed}, window {window}: answer wait {wait} ms, {sim.Deliveries.Count(d => d.Receiver == 1)} U_PDUs delivered before the death at {Death}\n{trace}");
        sim.Violations.Should().BeEmpty();
        sim.LinkStates.Should().Contain(l => l.Node == 1 && l.State == LinkStateName.Linked && l.T < Death, "the link was made before the peer died\n" + trace);

        // Node 0's first unlink after the death. The silence is timed from the end of node 0's first
        // transmission after the peer's last answer that leaves a wait running: with a deep queue that
        // is at most one wait (window full) plus one round of LBT, contention and a transmission after
        // the answer, so in practice within about a wait of the death. The bound is reached 16 waits
        // later and the next check follows within one wait or one round (12 s transmissions here).
        (int Node, long T, uint Peer, LinkStateName State) lost = sim.LinkStates.First(l => l.Node == 0 && l.T >= Death && l.State == LinkStateName.Unlinked);
        output.WriteLine($"lost at {lost.T}, {lost.T - Death} ms after the death, bound {DtsPeers.SilenceWaits * wait} ms");
        lost.T.Should().BeLessThanOrEqualTo(Death + ((DtsPeers.SilenceWaits + 1) * wait) + 60_000, trace);
    }

    /// <summary>
    /// Issue #64 (K-0120): the same run with the peer alive keeps the link for the whole 40 answer
    /// waits (over 30 min here) and keeps delivering, at the window of the issue and at the default.
    /// Before the fix every seed lost the link at about 13 min and delivered one U_PDU.
    /// </summary>
    [Theory]
    [InlineData(4096)]
    [InlineData(32768)]
    public void A_live_peer_with_a_deep_queue_keeps_the_link(int window)
    {
        const int Seeds = 40;
        int fewest = int.MaxValue;
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            (Simulation sim, long wait) = Run(seed, window, kill: false);
            string trace = Trace(sim);
            int delivered = sim.Deliveries.Count(d => d.Receiver == 1);
            fewest = Math.Min(fewest, delivered);
            output.WriteLine($"seed {seed}, window {window}: {delivered} U_PDUs delivered in {Death + (40 * wait)} ms");
            sim.Violations.Should().BeEmpty();
            sim.LinkStates.Should().Contain(l => l.Node == 1 && l.State == LinkStateName.Linked, "the link was made\n" + trace);
            sim.LinkStates.Should().NotContain(l => l.State == LinkStateName.Unlinked, $"seed {seed}: a live link must be kept\n" + trace);
            delivered.Should().BeGreaterThan(20, $"seed {seed}: the receiver must get turns to acknowledge\n" + trace);
        }

        output.WriteLine($"fewest delivered over {Seeds} seeds: {fewest}");
    }

    private static (Simulation Sim, long Wait) Run(ulong seed, int window, bool kill)
    {
        var channel = new ChannelModel(BlockErrorPpm: 150_000, BurstMissPpm: 50_000, HostLatencyMs: 50, TurnaroundMs: 100, EndOfBurstDelayMs: 50);
        var overlays = new[] { ArqTransferTests.Node("0.0.1.1", true, false), ArqTransferTests.Node("0.0.1.2", true, false) };
        foreach (System.Text.Json.Nodes.JsonObject o in overlays)
        {
            o["dts"]!["max_window"] = window;
        }

        NodeConfig config = ConfigReader.Read(ConfigMerge.Merge(Corpus.T2Defaults, overlays[0]));
        long wait = config.AnswerWaitMs();
        var sim = new Simulation(seed, channel, overlays);
        uint b = sim.AddressOf(1);
        sim.BindArq(0, 0, Sap);
        sim.BindArq(1, 0, Sap);
        for (int i = 0; i < 400; i++)
        {
            sim.SendArq(0, 1_000 + (i * 10), b, Sap, ArqTransferTests.Payload(0, i, 100 + (i * 37 % 120)));
        }

        if (kill)
        {
            sim.Kill(1, Death);
        }

        sim.Run(Death + (40 * wait));
        return (sim, wait);
    }

    private static string Trace(Simulation sim) => string.Join("\n", sim.LinkStates.Select(l => $"{l.T} n{l.Node} {l.Peer} {l.State}"));
}
