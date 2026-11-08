using System.Collections.Concurrent;
using System.Text;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Host.Modem;

namespace M0LTE.S5066.Host.Tests;

/// <summary>Issue #86: the per-burst trace, off unless a sink is given.</summary>
public class HostTraceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_traced_arq_exchange_logs_each_dpdu_timer_link_change_and_burst_in_ascii()
    {
        var lines = new ConcurrentQueue<string>();
        var medium = new LoopbackMedium();
        await using NodeHost a = TestNodes.StartLoopbackHost(medium, "0.0.1.1", "0000000000000001", new NodeHostOptions { Trace = lines.Enqueue });
        await using NodeHost b = TestNodes.StartLoopbackHost(medium, "0.0.1.2", "0000000000000002");
        await using SisClient ca = await SisClient.ConnectAsync(TestNodes.Endpoint(a));
        await using SisClient cb = await SisClient.ConnectAsync(TestNodes.Endpoint(b));
        using var cts = new CancellationTokenSource(Timeout);
        await ca.BindAsync(5, new ServiceType(1), cts.Token);
        await cb.BindAsync(5, new ServiceType(1), cts.Token);

        SendResult r = await ca.SendAsync(
            new UnidataSend(SisAddress.FromParts(0, 0, 1, 2), 5, Encoding.ASCII.GetBytes("traced\r\n")) { DeliveryMode = new ServiceType(1, Confirm: 1) }, cts.Token);
        r.Status.Should().Be(SendStatus.Confirmed);

        string[] all = [.. lines];
        all.Should().OnlyContain(l => l.StartsWith("trace ", StringComparison.Ordinal) && l.All(ch => ch >= ' ' && ch <= '~'));
        all.Should().Contain(l => l.Contains("link 0.0.1.2 Calling", StringComparison.Ordinal));
        all.Should().Contain(l => l.Contains(" tx burst bytes=", StringComparison.Ordinal) && l.Contains("planned=", StringComparison.Ordinal));
        all.Should().Contain(l => l.Contains(" tx DATA-ONLY 0.0.1.1>0.0.1.2 ", StringComparison.Ordinal) && l.Contains("fsn=0", StringComparison.Ordinal));
        all.Should().Contain(l => l.Contains(" rx ACK-ONLY 0.0.1.2>0.0.1.1 ", StringComparison.Ordinal) && l.Contains("rx_lwe=1", StringComparison.Ordinal));
        all.Should().Contain(l => l.Contains(" tx done planned_end=", StringComparison.Ordinal));
        all.Should().Contain(l => l.Contains("timer start dts.ack_wait/0.0.1.2", StringComparison.Ordinal));
        all.Should().Contain(l => l.Contains("timer fired csma.", StringComparison.Ordinal));
        all.Should().Contain(l => l.Contains("timer cancel dts.ack_wait/0.0.1.2", StringComparison.Ordinal));
        all.Should().Contain(l => l.Contains(" mac ", StringComparison.Ordinal) && l.Contains(" -> ", StringComparison.Ordinal));
        all.Should().Contain(l => l.Contains(" prim DUnidataRequestConfirm", StringComparison.Ordinal));
        all.Should().Contain(l => l.Contains(" arq 0.0.1.2 ack tx lwe=1 ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stream_with_noise_between_dpdus_is_described_one_per_line()
    {
        var header = new DpduHeader(0, 3, 3, 0x102, 0x101);
        byte[] one = DpduCodec.Encode(new AckOnly(header, 7, 0, 0, Octets.Empty, null));
        byte[] two = DpduCodec.Encode(new DataOnly(header, new DataFlags(true, true, false, false, true, true), 7, new Octets([1, 2, 3]), true));
        byte[] stream = [.. one, 0x55, 0xaa, .. two, 0x90];

        List<string> d = HostTrace.DescribeAll(stream);

        d.Should().Equal(
            $"ACK-ONLY 0.0.1.1>0.0.1.2 eot=3 size={one.Length} rx_lwe=7 enc=0 off=0 acks=",
            "skipped 2 bytes",
            $"DATA-ONLY 0.0.1.1>0.0.1.2 eot=3 size={two.Length} fsn=7 seg=3 start end lwe uwe",
            "undecoded 1 bytes");
    }
}
