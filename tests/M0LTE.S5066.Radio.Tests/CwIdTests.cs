using M0LTE.S5066.Host.Station;

namespace M0LTE.S5066.Radio.Tests;

/// <summary>
/// The CW identification on the radio (P8-2; core-architecture.md 10 item 13): keyed only inside a
/// keyup the node was granted, after TXDELAY and before the burst, on the schedule's terms.
/// </summary>
public class CwIdTests
{
    private const int CardRate = 48000;

    [Fact]
    public async Task The_id_goes_after_txdelay_and_before_the_burst_of_the_first_keyup_and_not_the_next()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var id = new CwIdSettings("DE M0XYZ", 20, 1800, 0.3, true, 10);
        var log = new EventLog();
        var lines = new List<string>();
        var output = new FakeOutput(CardRate, log);
        using RadioMedium medium = RadioMedium.Create(new RadioSettings("cap", "play", CardRate, "/dev/null", 3, 300, 20, 0.5, false, null),
            new FakeInput(CardRate), output, new FakePtt(log), lines.Add, cwId: id, clock: clock);
        medium.Audio.Start(_ => { });
        float[] burst = Signals.Tone(9600, 1000, 0.5, 0.5);

        await Play(medium, burst);
        float[] first = output.Written.ToArray();
        clock.Now += TimeSpan.FromSeconds(30);
        await Play(medium, burst);
        float[] second = output.Written.Skip(first.Length).ToArray();

        float[] cw = Morse.Generate("DE M0XYZ", 20, 1800, 0.3, CardRate);
        int lead = CardRate * 300 / 1000;
        int gap = CardRate * RadioAudioChannel.IdentificationGapMs / 1000;
        first.Length.Should().Be(second.Length + cw.Length + gap, "only the first keyup carries the ID");
        first.AsSpan(0, lead).ToArray().Should().OnlyContain(s => s == 0f, "TXDELAY is still silence, so the radio is on before the ID starts");
        first.AsSpan(lead, cw.Length).ToArray().Should().Equal(cw);
        first.AsSpan(lead + cw.Length, gap).ToArray().Should().OnlyContain(s => s == 0f);
        first.AsSpan(lead + cw.Length + gap).ToArray().Should().Equal(second.AsSpan(lead).ToArray(), "the burst and tail follow unchanged");
        log.Snapshot().Count(e => e == "key").Should().Be(2, "the ID never keys the radio by itself");
        lines.Should().ContainSingle(l => l.StartsWith("radio: cw id \"DE M0XYZ\"", StringComparison.Ordinal));
        medium.CwIdSchedule!.State().Sent.Should().Be(1);

        // Ten minutes after the ID, the next keyup carries it again.
        clock.Now += TimeSpan.FromMinutes(10);
        await Play(medium, burst);
        output.Written.Length.Should().Be((2 * first.Length) + second.Length);
    }

    [Fact]
    public async Task The_longest_burst_with_an_id_is_still_sent()
    {
        var log = new EventLog();
        var lines = new List<string>();
        var id = new CwIdSettings("DE M0XYZ", 20, 1800, null, true, null);
        using RadioMedium medium = RadioMedium.Create(new RadioSettings("cap", "play", CardRate, "/dev/null", 3, 0, 0, 0.5, false, null),
            new FakeInput(CardRate), new FakeOutput(CardRate, log), new FakePtt(log), lines.Add, cwId: id);
        medium.Audio.Start(_ => { });

        await Play(medium, new float[9600 * (RadioAudioChannel.MaxBurstMs / 1000)]);

        log.Snapshot().Should().Contain("key");
        lines.Should().NotContain(l => l.Contains("not sent", StringComparison.Ordinal));
    }

    private static async Task Play(RadioMedium medium, float[] burst)
    {
        var played = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        medium.Audio.Play(burst, () => played.TrySetResult());
        await played.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
