using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Host.Audio;
using M0LTE.S5066.Host.Modem;

namespace M0LTE.S5066.Host.Tests;

/// <summary>The configuration files shipped with s5066d (core-architecture.md 12.9; issue #80).</summary>
public class ConfigFileTests
{
    [Fact]
    public void The_example_keeps_the_standard_csma_defaults()
    {
        // [Ed4 K Table 7, p.K-12]: K-0098, K-0099, K-0102, K-0103.
        HostConfig c = Load("example.json");
        c.Node.Csma.ContSlotWidthMs.Should().Be(3000);
        c.Node.Csma.NumContSlots.Should().Be(16);
        c.Node.Csma.LbtWaitDcdMs.Should().Be(30000);
        c.Node.Csma.LbtWaitVdcdMs.Should().Be(3000);
        File.ReadAllText(Path("example.json")).Should().Contain("bench-node1.json", "the slow defaults are explained and the bench pair named");
    }

    [Fact]
    public void The_bench_pair_is_two_nodes_wired_to_each_other_with_short_csma()
    {
        HostConfig a = Load("bench-node1.json");
        HostConfig b = Load("bench-node2.json");

        a.Node.LocalAddress.Should().NotBe(b.Node.LocalAddress);
        a.SisEndpoint.Should().NotBe(b.SisEndpoint);
        a.Modem!.AudioPeer!.Port.Should().Be(b.Modem!.AudioListen!.Port);
        b.Modem.AudioPeer!.Port.Should().Be(a.Modem.AudioListen!.Port);
        foreach (HostConfig c in new[] { a, b })
        {
            c.Node.Csma.LbtWaitDcdMs.Should().BeLessThan(30000);
            c.Node.Csma.NumContSlots.Should().BeLessThan(16);
        }

        (a.Node with { LocalAddress = 0 }).Should().BeEquivalentTo(b.Node with { LocalAddress = 0 }, "the two differ only in address and endpoints");
    }

    [Theory]
    [InlineData("example.json")]
    [InlineData("bench-node1.json")]
    [InlineData("bench-node2.json")]
    public void Each_shipped_file_is_complete_for_a_node_on_the_110d_modem(string name)
    {
        // Issue #83: every setting a node needs in some exchange is set (interp B-0454), and the
        // modem can send at the default speed.
        HostConfig c = Load(name);
        c.Node.Cas.LinkRequestRetries.Should().NotBeNull();
        c.Node.Cas.BreakRetries.Should().NotBeNull();
        NodeHost.CheckConfig(c.Node, new Ms110dHostModem(new AudioMedium().CreateChannel()));
    }

    private static HostConfig Load(string name) => HostConfig.Load(Path(name));

    private static string Path(string name)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir, "M0LTE.S5066.slnx")))
        {
            dir = System.IO.Path.GetDirectoryName(dir);
        }

        return System.IO.Path.Combine(dir ?? throw new InvalidOperationException("repository root not found"), "src", "s5066d", name);
    }
}
