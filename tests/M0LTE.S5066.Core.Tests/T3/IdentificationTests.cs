using M0LTE.S5066.Host.Station;

namespace M0LTE.S5066.Core.Tests.T3;

/// <summary>
/// T3: the CW identification hook on the virtual clock (P8-2; plan 9.1, decision D4;
/// core-architecture.md 10 item 13). One node keys an identification ahead of its bursts on the
/// host's own schedule, at the start and every minute. The ID is only ever keyed inside a keyup
/// the core was granted by Annex K, so it starts no earlier than the burst would have: the
/// simulation's C-0844 and C-0889 check, made at the key, never fails, it is never keyed on its
/// own, every ID is followed at once by its burst, and the ARQ transfer still completes with each
/// U_PDU delivered once and in order. The ID lengthens our keyup but not the EOT we announce, which
/// is counted from each D_PDU on air (C-0104), so every EOT stays true (C-0891).
/// </summary>
public class IdentificationTests(ITestOutputHelper output)
{
    private const byte Sap = 5;

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    [InlineData(4UL)]
    public void An_id_ahead_of_granted_keyups_never_breaks_channel_access_and_the_transfer_completes(ulong seed)
    {
        var channel = new ChannelModel(BlockErrorPpm: 100_000, BurstMissPpm: 30_000, HostLatencyMs: 50, TurnaroundMs: 100, EndOfBurstDelayMs: 50);
        var sim = new Simulation(seed, channel, [ArqTransferTests.Node("0.0.1.1", true, false), ArqTransferTests.Node("0.0.1.2", true, false)]);
        const int IdMs = 5_000;   // about "DE M0XYZ" at 20 wpm
        var schedule = new CwIdSchedule(atStart: true, everyMinutes: 1);
        var keyups = new List<long>();
        sim.Identification = (node, t) =>
        {
            if (node != 0)
            {
                return 0;
            }

            keyups.Add(t);
            return schedule.Keyup(t) ? IdMs : 0;
        };

        uint a = sim.AddressOf(0);
        uint b = sim.AddressOf(1);
        sim.BindArq(0, 0, Sap);
        sim.BindArq(1, 0, Sap);
        var sent = new List<byte[]>[] { [], [] };
        for (int i = 0; i < 10; i++)
        {
            byte[] u = ArqTransferTests.Payload(0, i, 150 + (i * 37));
            sent[0].Add(u);
            sim.SendArq(0, 1_000 + (i * 20_000), b, Sap, u);
        }

        for (int i = 0; i < 4; i++)
        {
            byte[] u = ArqTransferTests.Payload(1, i, 90 + (i * 53 % 160));
            sent[1].Add(u);
            sim.SendArq(1, 150_000 + (i * 60_000), a, Sap, u);
        }

        sim.Run(40_000_000);

        output.WriteLine($"seed {seed}: {sim.Bursts.Count} bursts, {sim.Identifications.Count} IDs over {keyups.Count} keyups of node 0, {sim.Collisions} collisions, last delivery at {sim.Deliveries.Max(d => d.T)}");
        sim.Violations.Should().BeEmpty();
        sim.Identifications.Should().HaveCountGreaterThan(1, "the run lasts several minutes of node 0 transmitting");
        sim.Identifications[0].Start.Should().Be(keyups[0], "the pre-session ID goes with the first keyup");
        foreach ((int node, long start, long end) in sim.Identifications)
        {
            node.Should().Be(0);
            keyups.Should().Contain(start, "an ID is keyed only at a keyup the core was granted");
            sim.Bursts.Should().ContainSingle(x => x.Sender == 0 && x.Start == end, "its burst follows it at once, in the same keyup");
        }

        for (int i = 1; i < sim.Identifications.Count; i++)
        {
            (sim.Identifications[i].Start - sim.Identifications[i - 1].Start).Should().BeGreaterThanOrEqualTo(60_000, "at most one periodic ID a minute");
        }

        for (int receiver = 0; receiver < 2; receiver++)
        {
            int sender = 1 - receiver;
            var got = sim.Deliveries.Where(d => d.Receiver == receiver && d.Src == sim.AddressOf(sender)).Select(d => d.UPdu).ToList();
            got.Should().HaveCount(sent[sender].Count, $"node {receiver} gets each U_PDU of node {sender} once");
            for (int i = 0; i < got.Count; i++)
            {
                got[i].Should().Equal(sent[sender][i]);
            }
        }
    }
}
