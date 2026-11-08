using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Host.Tests;

/// <summary>The host's input order (core-architecture.md 12.3, 12.4; corpus-format.md 4.4, 4.5). No sockets, no clock.</summary>
public class InputSchedulerTests
{
    [Fact]
    public void Same_millisecond_inputs_go_config_then_timers_then_modem_then_sis()
    {
        var s = new InputScheduler();
        Start(s, 0, TimerName.Of(TimerKind.CsmaLbtWait), 100);
        s.Post(InputClass.Sis, t => new SisConnect(t, 1), 100);
        s.Post(InputClass.Modem, t => new BurstStarted(t), 100);
        s.Post(InputClass.Config, t => new ConfigInput(t, null!), 100);

        Kinds(s, 100).Should().Equal("ConfigInput", "TimerExpired", "BurstStarted", "SisConnect");
    }

    [Fact]
    public void Timers_due_at_one_time_expire_in_the_order_they_were_last_started()
    {
        var s = new InputScheduler();
        TimerName x = TimerName.Of(TimerKind.CsmaLbtWait);
        TimerName y = TimerName.Of(TimerKind.SisConfirmWait);
        Start(s, 0, x, 50);
        Start(s, 0, y, 50);
        Start(s, 0, x, 50);   // a restart takes a new place in the order

        var taken = new List<TimerName>();
        while (s.TakeNext(50, out _) is TimerExpired e)
        {
            taken.Add(e.Name);
        }

        taken.Should().Equal(y, x);
    }

    [Fact]
    public void A_late_timer_expires_at_its_deadline_before_later_events()
    {
        var s = new InputScheduler();
        TimerName x = TimerName.Of(TimerKind.CsmaLbtWait);
        Start(s, 0, x, 30);
        s.Post(InputClass.Modem, t => new BurstStarted(t), 80);   // the loop wakes late, at 80

        CoreInput first = s.TakeNext(80, out _)!;
        CoreInput second = s.TakeNext(80, out _)!;

        first.Should().Be(new TimerExpired(30, x));
        second.T.Should().Be(80);
    }

    [Fact]
    public void A_timer_not_yet_due_is_not_taken_and_its_deadline_is_given()
    {
        var s = new InputScheduler();
        Start(s, 0, TimerName.Of(TimerKind.CsmaLbtWait), 30);

        s.TakeNext(29, out long? next).Should().BeNull();
        next.Should().Be(30);
    }

    [Fact]
    public void A_cancelled_timer_never_expires()
    {
        var s = new InputScheduler();
        TimerName x = TimerName.Of(TimerKind.CsmaLbtWait);
        Start(s, 0, x, 30);
        s.Apply(new TimerCancel(x));

        s.TakeNext(1000, out long? next).Should().BeNull();
        next.Should().BeNull();
    }

    [Fact]
    public void Time_never_goes_backwards_even_when_the_clock_does()
    {
        var s = new InputScheduler();
        s.Post(InputClass.Sis, t => new SisConnect(t, 1), 100);
        s.TakeNext(100, out _);
        s.Post(InputClass.Sis, t => new SisConnect(t, 2), 90);

        s.TakeNext(100, out _)!.T.Should().Be(100);
    }

    private static void Start(InputScheduler s, long at, TimerName name, long ms)
    {
        s.Post(InputClass.Config, t => new ConfigInput(t, null!), at);
        s.TakeNext(at, out _);
        s.Apply(new TimerStart(name, ms));
    }

    private static List<string> Kinds(InputScheduler s, long now)
    {
        var kinds = new List<string>();
        while (s.TakeNext(now, out _) is { } i)
        {
            kinds.Add(i.GetType().Name);
        }

        return kinds;
    }
}
