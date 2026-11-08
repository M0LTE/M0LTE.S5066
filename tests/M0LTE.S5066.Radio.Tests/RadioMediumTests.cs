using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Host;
using M0LTE.S5066.Host.Audio;
using M0LTE.S5066.Host.Modem;

namespace M0LTE.S5066.Radio.Tests;

/// <summary>The radio medium put together on fake devices, and the shipped bench pair (core-architecture.md 12.11).</summary>
public class RadioMediumTests
{
    private const int CardRate = 48000;

    [Fact]
    public async Task A_burst_sent_through_one_radio_decodes_through_another()
    {
        // The whole chain without hardware: modulate, upsample, TXDELAY, onto a "wire", then
        // decimate and demodulate on the far side, so the resamplers are proved to pass 110D.
        var settings = Settings() with { TxDelayMs = 200, TxTailMs = 20, TxLevel = 0.5 };
        var log = new EventLog();
        var txOutput = new FakeOutput(CardRate, log);
        using var tx = RadioMedium.Create(settings, new FakeInput(CardRate), txOutput, new FakePtt(log), null);
        var txEvents = new RecordingEvents();
        tx.Modem.Start(txEvents);
        byte[] payload = [.. Enumerable.Range(0, 120).Select(i => (byte)(i * 7))];

        tx.Modem.Transmit(payload, 6, Interleaver.Short, 7, eom: true);
        await txEvents.Done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        float[] wire = txOutput.Written;
        wire.AsSpan(0, CardRate / 5).ToArray().Should().OnlyContain(s => s == 0f, "200 ms of TXDELAY silence leads");
        var rxInput = new FakeInput(CardRate);
        var lines = new List<string>();
        using var rx = RadioMedium.Create(settings, rxInput, new FakeOutput(CardRate, log), new FakePtt(log), lines.Add);
        var rxEvents = new RecordingEvents();
        rx.Modem.Start(rxEvents);
        rxInput.Queue(Mix(new float[CardRate / 2].Concat(wire).Concat(new float[CardRate]).ToArray(), Signals.Noise(CardRate, 0.5 + (wire.Length / (double)CardRate) + 1, 0.001, 9)));
        await rxEvents.Ended.Task.WaitAsync(TimeSpan.FromSeconds(30));

        rxEvents.Events.First().Should().Be("started");
        rxEvents.Events.Last().Should().Be("ended Eom wn6 Short");
        rxEvents.Bytes.Take(payload.Length).Should().Equal(payload);
        lines.Should().ContainSingle(l => l.StartsWith("radio: rx wn6 short ", StringComparison.Ordinal) && l.Contains(", SNR "));
    }

    [Fact]
    public async Task A_sent_burst_is_one_log_line_once_it_has_gone_out()
    {
        var log = new EventLog();
        var lines = new List<string>();
        using var medium = RadioMedium.Create(Settings(), new FakeInput(CardRate), new FakeOutput(CardRate, log), new FakePtt(log), lines.Add);
        var events = new RecordingEvents();
        medium.Modem.Start(events);

        medium.Modem.Transmit(new byte[40], 6, Interleaver.Short, 7, eom: true);
        await events.Done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        lines.Should().Equal("radio: tx wn6 short 40 bytes");
    }

    [Fact]
    public async Task With_busy_detect_on_energy_on_the_channel_is_the_nodes_busy_input()
    {
        var log = new EventLog();
        var input = new FakeInput(CardRate);
        using var medium = RadioMedium.Create(Settings() with { BusyDetect = true }, input, new FakeOutput(CardRate, log), new FakePtt(log), null);
        var events = new RecordingEvents();
        medium.Modem.Start(events);

        input.Queue(Signals.Noise(CardRate, 3, 0.01, 5));
        input.Queue(Signals.Tone(CardRate, 1500, 1, 0.3));
        await WaitFor(() => events.Events.Contains("busy True"));
        input.Queue(Signals.Noise(CardRate, 3, 0.01, 6));
        await WaitFor(() => events.Events.Contains("busy False"));
    }

    [Fact]
    public void The_host_looks_through_the_radio_modem_to_the_110d_adapter()
    {
        var log = new EventLog();
        using var medium = RadioMedium.Create(Settings(), new FakeInput(CardRate), new FakeOutput(CardRate, log), new FakePtt(log), null);
        medium.Modem.Should().BeAssignableTo<IHostModemWrapper>().Which.Inner.Should().BeOfType<Ms110dHostModem>();
    }

    [Fact]
    public void A_slot_no_longer_than_txdelay_and_the_preamble_is_warned_of_without_busy_detect()
    {
        var log = new EventLog();
        HostConfig c = HostConfig.Load(Path("radio-ft450.json"));
        RadioSettings s = RadioSettings.Parse(c.Modem!.Radio!);
        using var medium = RadioMedium.Create(s, new FakeInput(CardRate), new FakeOutput(CardRate, log), new FakePtt(log), null);

        RadioMedium.Warnings(s, c.Node, medium.Modem).Should().BeEmpty("busy_detect senses the burst soon after its first audio");
        RadioMedium.Warnings(s with { BusyDetect = false }, c.Node, medium.Modem).Should().ContainSingle().Which.Should().Contain("txdelay_ms 300");
    }

    [Fact]
    public void The_bench_pair_is_the_udp_bench_pair_on_two_radios()
    {
        HostConfig a = HostConfig.Load(Path("radio-ft450.json"));
        HostConfig b = HostConfig.Load(Path("radio-ft818.json"));
        HostConfig udpBench = HostConfig.Load(Path("bench-node1.json"));

        a.Node.LocalAddress.Should().NotBe(b.Node.LocalAddress);
        (a.Node with { LocalAddress = 0 }).Should().BeEquivalentTo(udpBench.Node with { LocalAddress = 0 }, "the CSMA and timer values are the bench ones");
        (b.Node with { LocalAddress = 0 }).Should().BeEquivalentTo(udpBench.Node with { LocalAddress = 0 });

        RadioSettings ra = RadioSettings.Parse(a.Modem!.Radio!);
        RadioSettings rb = RadioSettings.Parse(b.Modem!.Radio!);
        ra.TxLevel.Should().BeLessThan(rb.TxLevel, "the FT-450 data input wants only 60 mVp-p");
        ra.TxLevel.Should().BeLessThanOrEqualTo(0.05);
        (ra with { TxLevel = 0 }).Should().Be(rb with { TxLevel = 0 }, "otherwise the two radios are set up alike");
        ra.SampleRate.Should().Be(48000);
        ra.BusyDetect.Should().BeTrue();
        ra.CaptureDevice.Should().Contain("REPLACE_ME", "device names are placeholders");
        NodeHost.CheckConfig(a.Node, new Ms110dHostModem(new AudioMedium().CreateChannel()));
    }

    private static RadioSettings Settings() => new("cap", "play", CardRate, "/dev/null", 3, 0, 0, 0.5, false, null);

    private static float[] Mix(float[] a, float[] b)
    {
        var m = new float[a.Length];
        for (int i = 0; i < m.Length; i++)
        {
            m[i] = a[i] + (i < b.Length ? b[i] : 0f);
        }

        return m;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline);
            await Task.Delay(20);
        }
    }

    private static string Path(string name)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir, "M0LTE.S5066.slnx")))
        {
            dir = System.IO.Path.GetDirectoryName(dir);
        }

        return System.IO.Path.Combine(dir ?? throw new InvalidOperationException("repo root not found"), "src", "s5066d", name);
    }
}
