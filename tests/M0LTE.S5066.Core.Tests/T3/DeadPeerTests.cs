using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests.T3;

/// <summary>
/// T3 for issue #61: a peer that dies mid-transfer while this node still has a deep queue of ARQ
/// data for it. Counting unanswered waits let such a sender take hours to notice (14.7 h with 400
/// U_PDUs queued, the P5-1 review), because each retransmission round restarted its waits. Link
/// loss is now time-based (C-1250): the connection is lost once the peer has given no answer for 16
/// answer waits, checked at each wait expiry and at the end of each of our transmissions to it, so
/// the link goes within that bound plus one round of waiting and transmitting.
/// </summary>
public class DeadPeerTests(ITestOutputHelper output)
{
    private const byte Sap = 5;

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    public void A_dead_peer_with_a_deep_queue_is_declared_lost_within_the_bound(ulong seed)
    {
        var channel = new ChannelModel(BlockErrorPpm: 150_000, BurstMissPpm: 50_000, HostLatencyMs: 50, TurnaroundMs: 100, EndOfBurstDelayMs: 50);
        var overlays = new[] { ArqTransferTests.Node("0.0.1.1", true, false), ArqTransferTests.Node("0.0.1.2", true, false) };
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

        const long Death = 600_000;
        sim.Kill(1, Death);
        sim.Run(Death + (40 * wait));

        string trace = string.Join("\n", sim.LinkStates.Select(l => $"{l.T} n{l.Node} {l.Peer} {l.State}"));
        output.WriteLine($"seed {seed}: answer wait {wait} ms, {sim.Deliveries.Count(d => d.Receiver == 1)} U_PDUs delivered before the death at {Death}\n{trace}");
        sim.Violations.Should().BeEmpty();
        sim.Deliveries.Should().Contain(d => d.Receiver == 1, "the link carried data before the peer died\n" + trace);

        // Node 0's first unlink after the death. The peer's last answer came before it, so the bound
        // is reached by Death + 16 waits; the next check follows within one wait plus one round of LBT,
        // contention and a transmission (12 s at most here).
        (int Node, long T, uint Peer, LinkStateName State) lost = sim.LinkStates.First(l => l.Node == 0 && l.T >= Death && l.State == LinkStateName.Unlinked);
        output.WriteLine($"lost at {lost.T}, {lost.T - Death} ms after the death, bound {DtsPeers.SilenceWaits * wait} ms");
        lost.T.Should().BeLessThanOrEqualTo(Death + ((DtsPeers.SilenceWaits + 1) * wait) + 60_000, trace);
    }
}
