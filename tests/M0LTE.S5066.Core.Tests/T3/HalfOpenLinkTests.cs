using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Core.Tests.T3;

/// <summary>
/// T3 regression for issue #49 (P5-1): the half-open link that <see cref="ArqTransferTests"/> seed 349
/// left with the derived acknowledgement wait. Both nodes call at once (B-0405); node 1 accepts and
/// makes the link, but every accept it sends is lost or unheard while node 0, whose 30 s answer wait
/// is shorter than the time node 1 needs to answer, gives up its call (and its U_PDUs, A-0300). Node 1
/// then sends its data into a link node 0 does not hold, and node 0 answers each time with WARNING
/// reason 1. Before P5-1 node 1 kept that up until its first-data wait of 10 000 s ended the link.
/// Now node 1 still waits for the first DATA C_PDU, so the WARNINGs alone do not end the link (the
/// Caller might still be calling, B-0426), but its acknowledgement waits go unanswered, and once it
/// has awaited an answer for 16 answer waits the connection is lost (C-1250): both ends are unlinked well before the
/// first-data wait, and U_PDUs sent later make a new link and arrive once each, in order.
/// </summary>
public class HalfOpenLinkTests(ITestOutputHelper output)
{
    private const byte Sap = 5;

    [Fact]
    public void Seed_349_half_open_link_is_given_up_and_a_new_link_carries_later_data()
    {
        var channel = new ChannelModel(BlockErrorPpm: 150_000, BurstMissPpm: 50_000, HostLatencyMs: 50, TurnaroundMs: 100, EndOfBurstDelayMs: 50);
        var sim = new Simulation(349, channel, [ArqTransferTests.Node("0.0.1.1", true, false), ArqTransferTests.Node("0.0.1.2", true, false)]);
        uint a = sim.AddressOf(0);
        uint b = sim.AddressOf(1);
        sim.BindArq(0, 0, Sap);
        sim.BindArq(1, 0, Sap);

        // As ArqTransferTests seed 349 (derived acknowledgement wait, 16-bit family).
        for (int i = 0; i < 10; i++)
        {
            sim.SendArq(0, 1_000 + (i * 20_000), b, Sap, ArqTransferTests.Payload(0, i, 150 + (i * 37)));
        }

        for (int i = 0; i < 4; i++)
        {
            sim.SendArq(1, 150_000 + (i * 60_000), a, Sap, ArqTransferTests.Payload(1, i, 90 + (i * 53 % 160)));
        }

        // After the link has been given up, more traffic each way.
        const long Later = 3_000_000;
        var later = new List<byte[]>[] { [], [] };
        for (int i = 0; i < 3; i++)
        {
            for (int n = 0; n < 2; n++)
            {
                byte[] u = ArqTransferTests.Payload(n, 100 + i, 60 + (i * 41));
                later[n].Add(u);
                sim.SendArq(n, Later + (i * 30_000), n == 0 ? b : a, Sap, u);
            }
        }

        sim.Run(20_000_000);

        string trace = string.Join("\n", sim.LinkStates.Select(l => $"{l.T} n{l.Node} {l.Peer} {l.State}"));
        output.WriteLine(trace);
        sim.Violations.Should().BeEmpty();

        // The half-open link: node 1 linked, node 0 gave up its call.
        (long T, LinkStateName State) first1 = sim.LinkStates.Where(l => l.Node == 1).Select(l => (l.T, l.State)).First(l => l.State == LinkStateName.Linked);
        sim.LinkStates.Should().Contain(l => l.Node == 0 && l.State == LinkStateName.Unlinked && l.T > first1.T, "node 0's call failed after node 1 made the link\n" + trace);

        // Node 1 gives the link up through the DTS's silence bound, long before its first-data wait (10 000 000 ms).
        (int Node, long T, uint Peer, LinkStateName State) lost = sim.LinkStates.First(l => l.Node == 1 && l.State == LinkStateName.Unlinked);
        lost.T.Should().BeLessThan(first1.T + 2_000_000, trace);
        sim.Log.Should().NotContain(l => l.Contains("timer_expired cas.first_data", StringComparison.Ordinal));
        int expiries = sim.Log.Count(l => l.Contains(" n1 in timer_expired dts.ack_wait", StringComparison.Ordinal) && long.Parse(l.Split(' ')[0]) <= lost.T);
        expiries.Should().BeGreaterThan(0);

        // A new link carries the later U_PDUs, once each and in order.
        sim.LinkStates.Should().Contain(l => l.State == LinkStateName.Linked && l.T > Later, trace);
        for (int receiver = 0; receiver < 2; receiver++)
        {
            int sender = 1 - receiver;
            var got = sim.Deliveries.Where(d => d.Receiver == receiver && d.T > Later).Select(d => d.UPdu).ToList();
            got.Should().HaveCount(later[sender].Count, $"node {receiver} gets each later U_PDU of node {sender} once");
            for (int i = 0; i < got.Count; i++)
            {
                got[i].Should().Equal(later[sender][i]);
            }
        }
    }
}
