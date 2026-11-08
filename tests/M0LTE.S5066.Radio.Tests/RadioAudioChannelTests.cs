namespace M0LTE.S5066.Radio.Tests;

/// <summary>The audio path over a sound card and PTT (core-architecture.md 12.11), on fake devices.</summary>
public class RadioAudioChannelTests
{
    private const int CardRate = 48000;

    [Fact]
    public void A_keyup_is_txdelay_of_silence_then_the_burst_at_the_card_rate_then_the_tail()
    {
        var log = new EventLog();
        using var channel = new RadioAudioChannel(new FakeInput(CardRate), new FakeOutput(CardRate, log), new FakePtt(log), txDelayMs: 300, txTailMs: 20);
        float[] burst = Signals.Tone(9600, 1000, 1.0, 0.5);

        float[] keyup = channel.Keyup(burst);

        int lead = CardRate * 300 / 1000;
        int tail = CardRate * 20 / 1000;
        keyup.Length.Should().Be(lead + (burst.Length * 5) + tail);
        keyup.AsSpan(0, lead).ToArray().Should().OnlyContain(s => s == 0f, "TXDELAY is silence");
        keyup.AsSpan(keyup.Length - tail).ToArray().Should().OnlyContain(s => s == 0f, "the tail is silence");
        float[] body = keyup.AsSpan(lead, burst.Length * 5).ToArray();
        // Away from the filter's start-up, the tone is still 1000 Hz and keeps its level.
        Signals.Frequency(body.AsSpan(4800, 38400), CardRate).Should().BeApproximately(1000, 2);
        Signals.Rms(body.AsSpan(4800, 38400)).Should().BeApproximately(0.5 / Math.Sqrt(2), 0.02);
    }

    [Fact]
    public async Task A_burst_is_keyed_written_whole_drained_and_unkeyed_and_only_then_reported_played()
    {
        var log = new EventLog();
        var output = new FakeOutput(CardRate, log);
        using var channel = new RadioAudioChannel(new FakeInput(CardRate), output, new FakePtt(log), 100, 20);
        channel.Keyed += keyed => log.Add(keyed ? "keyed event" : "unkeyed event");
        channel.Start(_ => { });
        var played = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        float[] burst = Signals.Tone(9600, 1500, 0.5, 0.5);
        channel.Play(burst, () =>
        {
            log.Add("played");
            played.TrySetResult();
        });
        await played.Task.WaitAsync(TimeSpan.FromSeconds(10));

        int total = (CardRate / 10) + (burst.Length * 5) + (CardRate / 50);
        log.Snapshot().Should().Equal("keyed event", "key", $"write {total}", "drain", "unkey", "unkeyed event", "played");
        output.Written.Should().Equal(channel.Keyup(burst), "what is written is the keyup Play prepared");
    }

    [Fact]
    public async Task The_burst_is_upsampled_in_Play_before_the_radio_is_keyed()
    {
        // The transmit thread is held at the key for the first burst. A second burst's upsampling
        // must still be done by the time Play returns, so it is never dead air after a key.
        var log = new EventLog();
        using var gate = new ManualResetEventSlim();
        var ptt = new FakePtt(log) { Gate = gate };
        var output = new FakeOutput(CardRate, log);
        using var channel = new RadioAudioChannel(new FakeInput(CardRate), output, ptt, 0, 0);
        channel.Start(_ => { });
        int played = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Played()
        {
            if (Interlocked.Increment(ref played) == 2)
            {
                both.TrySetResult();
            }
        }

        channel.Play(Signals.Tone(9600, 1000, 0.2, 0.5), Played);
        float[] second = Signals.Tone(9600, 2000, 3.0, 0.5);
        channel.Play(second, Played);
        log.Snapshot().Should().BeEmpty("nothing is keyed yet");

        gate.Set();
        await both.Task.WaitAsync(TimeSpan.FromSeconds(10));
        log.Snapshot().Where(e => e.StartsWith("write", StringComparison.Ordinal)).Last().Should().Be($"write {second.Length * 5}");
    }

    [Fact]
    public async Task Received_audio_is_decimated_to_the_modems_rate_and_keeps_its_frequency()
    {
        var log = new EventLog();
        var input = new FakeInput(CardRate);
        using var channel = new RadioAudioChannel(input, new FakeOutput(CardRate, log), new FakePtt(log), 0, 0);
        var got = new List<float>();
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Start(chunk =>
        {
            got.AddRange(chunk.ToArray());
            if (got.Count >= 9600)
            {
                enough.TrySetResult();
            }
        });

        input.Queue(Signals.Tone(CardRate, 1800, 1.0, 0.5));
        await enough.Task.WaitAsync(TimeSpan.FromSeconds(10));

        got.Count.Should().Be(9600);
        Signals.Frequency(got.ToArray().AsSpan(960, 7680), 9600).Should().BeApproximately(1800, 3);
        Signals.Rms(got.ToArray().AsSpan(960, 7680)).Should().BeApproximately(0.5 / Math.Sqrt(2), 0.02);
    }

    [Fact]
    public async Task A_failed_key_is_logged_and_the_burst_still_ends()
    {
        var log = new EventLog();
        var lines = new List<string>();
        var output = new FakeOutput(CardRate, log);
        using var channel = new RadioAudioChannel(new FakeInput(CardRate), output, new FakePtt(log) { Fail = true }, 100, 20, lines.Add);
        channel.Start(_ => { });
        var played = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        channel.Play(new float[960], () => played.TrySetResult());
        await played.Task.WaitAsync(TimeSpan.FromSeconds(10));

        output.Written.Should().BeEmpty("nothing is played unkeyed");
        lines.Should().ContainSingle().Which.Should().StartWith("radio: burst not sent, PTT failed");
    }

    [Fact]
    public async Task A_playback_device_that_fails_unkeys_ends_the_burst_and_stops_the_channel()
    {
        var log = new EventLog();
        var output = new FakeOutput(CardRate, log) { Fail = true };
        using var channel = new RadioAudioChannel(new FakeInput(CardRate), output, new FakePtt(log), 100, 20);
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Failed += e => failed.TrySetResult(e);
        channel.Start(_ => { });
        var played = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        channel.Play(new float[960], () => played.TrySetResult());
        await played.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        log.Snapshot().Should().Equal("key", "unkey");
    }

    [Fact]
    public async Task A_capture_device_that_fails_stops_the_channel()
    {
        var log = new EventLog();
        var input = new FakeInput(CardRate) { ThrowOnRead = true };
        using var channel = new RadioAudioChannel(input, new FakeOutput(CardRate, log), new FakePtt(log), 0, 0);
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Failed += e => failed.TrySetResult(e);

        channel.Start(_ => { });

        (await failed.Task.WaitAsync(TimeSpan.FromSeconds(10))).Message.Should().Contain("No such device");
    }

    [Fact]
    public void Mismatched_or_unusable_rates_are_refused()
    {
        var log = new EventLog();
        FluentActions.Invoking(() => new RadioAudioChannel(new FakeInput(48000), new FakeOutput(44100, log), new FakePtt(log), 0, 0))
            .Should().Throw<ArgumentException>().WithMessage("*must match*");
        FluentActions.Invoking(() => new RadioAudioChannel(new FakeInput(44100), new FakeOutput(44100, log), new FakePtt(log), 0, 0))
            .Should().Throw<ArgumentException>().WithMessage("*not a multiple*");
    }
}
