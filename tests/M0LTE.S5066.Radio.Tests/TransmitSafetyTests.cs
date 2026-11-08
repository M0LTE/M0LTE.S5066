using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Host.Audio;
using M0LTE.S5066.Host.Modem;

namespace M0LTE.S5066.Radio.Tests;

/// <summary>The radio is never left keyed (core-architecture.md 12.11): the transmit guard, every way out of a keyup, and the stop.</summary>
public class TransmitSafetyTests
{
    private const int CardRate = 48000;

    [Fact]
    public async Task A_hung_playback_device_is_unkeyed_by_the_guard_and_the_channel_stops()
    {
        var log = new EventLog();
        var lines = new List<string>();
        using var hang = new ManualResetEventSlim();
        var output = new FakeOutput(CardRate, log) { Hang = hang };
        var channel = new RadioAudioChannel(new FakeInput(CardRate), output, new FakePtt(log), 0, 0, Lines(lines), TimeSpan.FromMilliseconds(200));
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Failed += e => failed.TrySetResult(e);
        channel.Start(_ => { });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        channel.Play(new float[960], () => { });
        Exception e = await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "a 0.1 s keyup with a 0.2 s margin is cut off well inside 3 s");
        e.Message.Should().StartWith("transmit guard");
        log.Snapshot().Should().Equal("key", "hung write", "unkey");
        Lines(lines, null).Should().Contain(l => l.StartsWith("radio: transmit guard", StringComparison.Ordinal));

        // The write comes back late: nothing more is written and the radio is not keyed again.
        hang.Set();
        channel.Play(new float[960], () => { });
        await Task.Delay(300);
        log.Snapshot().Count(x => x.StartsWith("write ", StringComparison.Ordinal)).Should().BeLessThanOrEqualTo(1, "only the hung write itself completes");
        log.Snapshot().Count(x => x == "key").Should().Be(1);
        channel.Dispose();
    }

    [Fact]
    public async Task Stopping_unkeys_at_once_even_when_the_transmit_thread_is_stuck()
    {
        var log = new EventLog();
        using var hang = new ManualResetEventSlim();
        var output = new FakeOutput(CardRate, log) { Hang = hang };
        // A guard far off, so it is the stop that unkeys.
        var channel = new RadioAudioChannel(new FakeInput(CardRate), output, new FakePtt(log), 0, 0, null, TimeSpan.FromMinutes(5));
        channel.Start(_ => { });
        channel.Play(new float[960], () => { });
        SpinWait.SpinUntil(() => log.Snapshot().Contains("hung write"), TimeSpan.FromSeconds(10)).Should().BeTrue();

        var stopping = Task.Run(channel.Dispose);
        SpinWait.SpinUntil(() => log.Snapshot().Contains("unkey"), TimeSpan.FromSeconds(1)).Should().BeTrue("the stop unkeys before it waits for the transmit thread");
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        hang.Set();
    }

    [Fact]
    public async Task An_unexpected_exception_while_keyed_unkeys_and_stops_the_channel_not_the_process()
    {
        var log = new EventLog();
        var output = new FakeOutput(CardRate, log) { Throw = new NotSupportedException("something odd") };
        using var channel = new RadioAudioChannel(new FakeInput(CardRate), output, new FakePtt(log), 0, 0);
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Failed += e => failed.TrySetResult(e);
        channel.Start(_ => { });

        channel.Play(new float[960], () => { });

        (await failed.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeOfType<NotSupportedException>();
        log.Snapshot().First().Should().Be("key");
        log.Snapshot().Skip(1).Should().NotBeEmpty().And.OnlyContain(x => x == "unkey");
    }

    [Fact]
    public async Task An_exception_in_the_receive_path_stops_the_channel_not_the_process()
    {
        var log = new EventLog();
        var input = new FakeInput(CardRate);
        using var channel = new RadioAudioChannel(input, new FakeOutput(CardRate, log), new FakePtt(log), 0, 0);
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Failed += e => failed.TrySetResult(e);
        channel.Start(_ => throw new NullReferenceException("a demodulator bug"));

        input.Queue(new float[CardRate / 10]);

        (await failed.Task.WaitAsync(TimeSpan.FromSeconds(10))).Message.Should().Be("a demodulator bug");
    }

    [Fact]
    public async Task A_keyup_longer_than_any_plannable_burst_is_never_keyed()
    {
        var log = new EventLog();
        var lines = new List<string>();
        var output = new FakeOutput(CardRate, log);
        using var channel = new RadioAudioChannel(new FakeInput(CardRate), output, new FakePtt(log), 0, 0, Lines(lines));
        channel.Start(_ => { });
        var played = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        channel.Play(new float[(IAudioChannel.SampleRate * (RadioAudioChannel.MaxBurstMs / 1000)) + IAudioChannel.SampleRate], () => played.TrySetResult());
        await played.Task.WaitAsync(TimeSpan.FromSeconds(10));

        log.Snapshot().Should().BeEmpty();
        Lines(lines, null).Should().ContainSingle().Which.Should().StartWith("radio: burst not sent");
    }

    [Fact]
    public void The_longest_burst_the_modem_can_plan_fits_under_the_cap()
    {
        // The largest D_PDU: a 1023-byte segment with the longest header and its CRCs, rounded up.
        const int largestDpdu = 1100;
        var modem = new Ms110dHostModem(new NullAudio());
        long longest = 0;
        foreach (int wn in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 13 })
        {
            foreach (Interleaver il in new[] { Interleaver.UltraShort, Interleaver.Short, Interleaver.Medium, Interleaver.Long })
            {
                try
                {
                    longest = Math.Max(longest, modem.PlanBurst(wn, il, largestDpdu).DurationMs);
                }
                catch (ArgumentException)
                {
                    // Not a combination the waveform has (WN 0 has no ultra short interleaver).
                }
            }
        }

        longest.Should().BeLessThan(RadioAudioChannel.MaxBurstMs, "the guard must never refuse a legal burst");
        longest.Should().BeGreaterThan(100_000, "the slowest waveform's largest D_PDU is close to two minutes, so the cap is not loose");
    }

    [Fact]
    public async Task EmergencyUnkey_unkeys_a_keyed_radio_and_keeps_it_unkeyed()
    {
        var log = new EventLog();
        var output = new FakeOutput(CardRate, log) { RealTime = true };
        using var channel = new RadioAudioChannel(new FakeInput(CardRate), output, new FakePtt(log), 0, 0);
        channel.Start(_ => { });
        var played = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Play(new float[9600 * 2], () => played.TrySetResult());
        SpinWait.SpinUntil(() => log.Snapshot().Contains("key"), TimeSpan.FromSeconds(10)).Should().BeTrue();

        channel.EmergencyUnkey();

        log.Snapshot().Should().Contain("unkey");
        await played.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var again = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Play(new float[960], () => again.TrySetResult());
        await again.Task.WaitAsync(TimeSpan.FromSeconds(10));
        log.Snapshot().Count(x => x == "key").Should().Be(1, "nothing is keyed after an emergency unkey");
    }

    [Fact]
    public async Task A_burst_that_underran_says_so()
    {
        var log = new EventLog();
        var lines = new List<string>();
        int underruns = 0;
        var output = new FakeOutput(CardRate, log);
        using var channel = new RadioAudioChannel(new FakeInput(CardRate), output, new FakePtt(log), 0, 0, Lines(lines))
        {
            PlaybackXruns = () => Interlocked.Increment(ref underruns) - 1,
        };
        channel.Start(_ => { });
        var played = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        channel.Play(new float[960], () => played.TrySetResult());
        await played.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Lines(lines, null).Should().ContainSingle().Which.Should().StartWith("radio: the playback device underran 1 time(s)");
    }

    private static Action<string> Lines(List<string> lines) => line =>
    {
        lock (lines)
        {
            lines.Add(line);
        }
    };

    private static string[] Lines(List<string> lines, object? _)
    {
        lock (lines)
        {
            return [.. lines];
        }
    }

    private sealed class NullAudio : IAudioChannel
    {
        public void Start(Action<ReadOnlyMemory<float>> received)
        {
        }

        public void Play(float[] samples, Action played) => played();
    }
}
