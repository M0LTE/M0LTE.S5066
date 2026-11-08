using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Host.Audio;
using M0LTE.S5066.Host.Modem;

namespace M0LTE.S5066.Host.Tests;

/// <summary>
/// P7-9a: the host loads the calibrated drc.table for M0LTE.Ms110d and overwrites the configured
/// one, and refuses a calibration of another modem (core-architecture.md 9, phase 7, design shape
/// item 6). The corpus default stays empty, so no T2 trace changes.
/// </summary>
public class CalibratedTableTests
{
    [Fact]
    public void The_embedded_calibration_is_for_the_modem_version_this_host_references()
    {
        // A modem bump without a new calibration fails here: recalibrate, or show the estimate matches.
        CalibratedTable c = CalibratedTable.Default;

        c.ModemPackage.Should().Be(Ms110dHostModem.ModemPackage);
        c.ModemVersion.Should().Be(Ms110dHostModem.ModemVersion);
        c.Calibration.Should().Be("ms110d-cs-" + Ms110dHostModem.ModemVersion);
    }

    [Fact]
    public void The_embedded_table_reads_as_a_full_drc_table()
    {
        DrcTable t = CalibratedTable.Default.Table;

        t.IsEmpty.Should().BeFalse();
        t.Ladder[0].Should().Be(new Speed(1, Interleaver.Long));
        t.Thresholds.Should().HaveCount(36);
        t.Thresholds.Should().OnlyContain(e => e.Bulk.Count == t.Ladder.Count && e.Latency.Count == t.Ladder.Count);
        new int?[] { t.SmoothingWeight, t.CleanRun, t.RxErrorLimitPpt, t.RxCeilingInitial, t.RxCeilingCap, t.TxStepDownWaits, t.TxCeilingInitial, t.TxCeilingCap }
            .Should().OnlyContain(v => v != null);
    }

    [Fact]
    public void The_corpus_default_table_stays_empty()
    {
        HostConfig.Parse(File()).Node.Drc.Table.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_host_on_the_ms110d_modem_overwrites_the_configured_table_with_the_calibration()
    {
        var lines = new List<string>();
        HostConfig c = HostConfig.Parse(File());
        var host = new NodeHost(c.Node, c.PrngSeed, new Ms110dHostModem(new AudioMedium().CreateChannel()), new NodeHostOptions { SisEndpoint = null, Log = lines.Add });

        host.Calibration.Should().BeSameAs(CalibratedTable.Default);
        host.Config.Drc.Table.Should().BeSameAs(CalibratedTable.Default.Table);
        host.Config.Drc.DefaultSpeed.Should().Be(c.Node.Drc.DefaultSpeed);
        lines.Should().BeEmpty();

        host.UpdateConfig(c.Node);
        host.Config.Drc.Table.Should().BeSameAs(CalibratedTable.Default.Table);
    }

    [Fact]
    public void A_host_on_another_modem_loads_no_calibration()
    {
        var medium = new LoopbackMedium();
        IHostModem modem = medium.CreateModem(new ModemFixture(720, 0, [new ModemWaveform(2, Interleaver.Long, 96, 4800)]));

        NodeHost.CalibrationFor(modem).Should().BeNull();
    }

    [Fact]
    public void A_calibration_of_another_modem_version_does_not_match()
    {
        CalibratedTable c = CalibratedTable.Default;

        c.Matches(c.ModemPackage, c.ModemVersion).Should().BeTrue();
        c.Matches(c.ModemPackage, c.ModemVersion + ".1").Should().BeFalse();
        c.Matches("ms110d-cpp", c.ModemVersion).Should().BeFalse();
    }

    [Fact]
    public void A_file_without_its_modem_is_refused()
    {
        Action parse = () => CalibratedTable.Parse("""{"source": {"calibration": "x"}, "table": {"ladder": [], "thresholds": []}}""");

        parse.Should().Throw<ConfigException>().Where(e => e.Setting == "source.modem");
    }

    private static string File() => """
        {
          "local_address": "0.0.1.1",
          "prng_seed": "0000000000000001",
          "csma": { "mode": "jitter", "num_cont_slots": 4, "cont_slot_width_ms": 1000, "lbt_wait_dcd_ms": 2000, "lbt_wait_vdcd_ms": 500 },
          "drc": { "default_speed": { "wn": 6, "interleaver": "short" } },
          "cas": { "max_links": 4, "link_request_retries": 5, "break_retries": 3 }
        }
        """;
}
