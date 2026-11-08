namespace M0LTE.S5066.Radio.Tests;

/// <summary>The energy busy input (core-architecture.md 12.11).</summary>
public class RadioBusyTests
{
    private const int Rate = 9600;

    [Fact]
    public void A_signal_well_above_the_noise_is_busy_and_noise_alone_is_not()
    {
        var busy = new RadioBusy(Rate);
        var changes = new List<bool>();
        busy.Changed += changes.Add;

        Feed(busy, Signals.Noise(Rate, 3, 0.01, 1));
        busy.Busy.Should().BeFalse();
        changes.Should().BeEmpty();

        Feed(busy, Mix(Signals.Noise(Rate, 1, 0.01, 2), Signals.Tone(Rate, 1500, 1, 0.2)));
        busy.Busy.Should().BeTrue();
        changes.Should().Equal(true);
    }

    [Fact]
    public void Our_own_transmission_and_the_settling_tail_after_it_are_left_out()
    {
        var busy = new RadioBusy(Rate);
        var changes = new List<bool>();
        busy.Changed += changes.Add;
        Feed(busy, Signals.Noise(Rate, 3, 0.01, 3));
        float[] loud = Mix(Signals.Noise(Rate, 1, 0.01, 4), Signals.Tone(Rate, 1500, 1, 0.2));
        Feed(busy, loud);
        busy.Busy.Should().BeTrue();

        busy.SetKeyed(true);
        busy.Busy.Should().BeFalse("keying is never somebody else");
        Feed(busy, loud);
        busy.Busy.Should().BeFalse("our own audio heard back is not fed");

        busy.SetKeyed(false);
        Feed(busy, loud.AsSpan(0, Rate * 200 / 1000).ToArray());
        busy.Busy.Should().BeFalse("within the 250 ms after our unkey");
        Feed(busy, loud);
        busy.Busy.Should().BeTrue("after the tail, the channel counts again");

        changes.Should().Equal(true, false, true);
    }

    private static void Feed(RadioBusy busy, float[] samples)
    {
        for (int i = 0; i < samples.Length; i += 192)
        {
            busy.Process(samples.AsSpan(i, Math.Min(192, samples.Length - i)));
        }
    }

    private static float[] Mix(float[] a, float[] b)
    {
        var m = new float[a.Length];
        for (int i = 0; i < m.Length; i++)
        {
            m[i] = a[i] + b[i];
        }

        return m;
    }
}
