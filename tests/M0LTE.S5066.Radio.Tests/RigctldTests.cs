using System.Net;
using System.Net.Sockets;
using System.Text;

namespace M0LTE.S5066.Radio.Tests;

/// <summary>CAT control through rigctld (core-architecture.md 12.13), against a fake rigctld on a local TCP port.</summary>
public class RigctldTests
{
    private const int CardRate = 48000;

    [Fact]
    public void At_start_it_sets_frequency_mode_and_passband_reads_them_back_and_logs_one_line()
    {
        using var fake = new FakeRigctld();
        var lines = new Lines();
        using var rig = new Rigctld(Cat(fake), pttByCat: false, lines.Add, Timeout, Retry);
        rig.Start();

        rig.Connected.Should().BeTrue();
        fake.Commands.Should().Equal("F 7052300", "M PKTUSB 3000", "f", "m");
        lines.All.Should().Equal($"radio: cat: rigctld {Cat(fake).Address}: 7052300 Hz, PKTUSB, passband 3000 Hz");
    }

    [Fact]
    public void A_radio_that_sets_a_narrower_passband_is_logged_with_what_it_set()
    {
        using var fake = new FakeRigctld { WidestPassband = 2400 };
        var lines = new Lines();
        using var rig = new Rigctld(Cat(fake), pttByCat: false, lines.Add, Timeout, Retry);
        rig.Start();

        lines.All.Should().ContainSingle().Which.Should().EndWith("7052300 Hz, PKTUSB, passband 2400 Hz (asked for 3000; the radio set this)");
    }

    [Fact]
    public void A_refused_passband_is_retried_with_the_normal_one_and_said()
    {
        using var fake = new FakeRigctld { RefuseWidth = true, WidestPassband = 2400 };
        var lines = new Lines();
        using var rig = new Rigctld(Cat(fake), pttByCat: false, lines.Add, Timeout, Retry);
        rig.Start();

        fake.Commands.Should().Equal("F 7052300", "M PKTUSB 3000", "M PKTUSB 0", "f", "m");
        lines.All.Should().ContainSingle().Which.Should().EndWith("passband 2400 Hz (asked for 3000; the radio set this); 3000 Hz passband refused (RPRT -1)");
    }

    [Fact]
    public void A_refused_mode_and_frequency_are_said_and_the_rest_carries_on()
    {
        using var fake = new FakeRigctld { RefuseMode = true, RefuseFrequency = true, Mode = "USB" };
        var lines = new Lines();
        using var rig = new Rigctld(Cat(fake) with { FrequencyHz = 99 }, pttByCat: false, lines.Add, Timeout, Retry);
        rig.Start();

        rig.Connected.Should().BeTrue();
        lines.All.Should().ContainSingle().Which.Should().Be(
            $"radio: cat: rigctld {Cat(fake).Address}: 7052300 Hz (asked for 99), USB (asked for PKTUSB), passband 3000 Hz; frequency refused (RPRT -1); mode PKTUSB refused (RPRT -1)");
    }

    [Fact]
    public void With_ptt_by_cat_it_unkeys_first_then_keys_and_unkeys_with_T()
    {
        using var fake = new FakeRigctld();
        using var rig = new Rigctld(Cat(fake), pttByCat: true, null, Timeout, Retry);
        rig.Start();
        rig.Key();
        fake.Ptt.Should().BeTrue();
        rig.Unkey();

        fake.Ptt.Should().BeFalse();
        fake.Commands.Should().Equal("T 0", "F 7052300", "M PKTUSB 3000", "f", "m", "T 1", "T 0");
    }

    [Fact]
    public void With_no_rigctld_it_says_so_once_refuses_to_key_and_connects_when_it_appears()
    {
        int port = FreePort();
        var lines = new Lines();
        using var rig = new Rigctld(new CatSettings("127.0.0.1", port, 7052300), pttByCat: true, lines.Add, Timeout, Retry);
        rig.Start();

        rig.Connected.Should().BeFalse();
        FluentActions.Invoking(rig.Key).Should().Throw<IOException>().WithMessage($"CAT PTT: no answer from rigctld at 127.0.0.1:{port}, so not transmitting");
        Thread.Sleep(Retry * 3);
        lines.All.Should().ContainSingle().Which.Should().StartWith($"radio: cat: no rigctld at 127.0.0.1:{port} (").And.EndWith("trying again every 0.1 s, and not transmitting until it answers");

        using var fake = new FakeRigctld(port);
        SpinWait.SpinUntil(() => rig.Connected, TimeSpan.FromSeconds(10)).Should().BeTrue();
        lines.All.Should().HaveCount(2);
        lines.All[1].Should().Be($"radio: cat: rigctld 127.0.0.1:{port}: 7052300 Hz, PKTUSB, passband 3000 Hz, PTT by CAT");
        rig.Key();
        fake.Ptt.Should().BeTrue();
    }

    [Fact]
    public void A_lost_rigctld_is_one_line_and_is_reconnected_and_set_up_again()
    {
        int port;
        var lines = new Lines();
        Rigctld rig;
        using (var first = new FakeRigctld())
        {
            port = first.Port;
            rig = new Rigctld(new CatSettings("127.0.0.1", port, 7052300), pttByCat: true, lines.Add, Timeout, Retry);
            rig.Start();
            rig.Connected.Should().BeTrue();
        }

        using (rig)
        {
            // Gone while idle: noticed without a key.
            SpinWait.SpinUntil(() => !rig.Connected, TimeSpan.FromSeconds(10)).Should().BeTrue();
            FluentActions.Invoking(rig.Key).Should().Throw<IOException>().WithMessage("*so not transmitting");
            Thread.Sleep(Retry * 3);
            lines.All.Should().HaveCount(2);
            lines.All[1].Should().StartWith($"radio: cat: no rigctld at 127.0.0.1:{port}");

            using var second = new FakeRigctld(port);
            SpinWait.SpinUntil(() => rig.Connected, TimeSpan.FromSeconds(10)).Should().BeTrue();
            second.Commands.Should().StartWith(["T 0", "F 7052300", "M PKTUSB 3000"]);
            lines.All.Should().HaveCount(3);
        }
    }

    [Fact]
    public void A_rigctld_that_stops_answering_refuses_the_key_within_the_timeout()
    {
        using var fake = new FakeRigctld();
        var lines = new Lines();
        using var rig = new Rigctld(Cat(fake), pttByCat: true, lines.Add, Timeout, TimeSpan.FromMinutes(5));
        rig.Start();
        fake.Mute = true;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        FluentActions.Invoking(rig.Key).Should().Throw<IOException>().WithMessage("CAT PTT: lost rigctld at *, so not transmitting");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        rig.Connected.Should().BeFalse();
        fake.Ptt.Should().BeTrue("the fake took the key but never answered; the unkey that follows goes on a fresh connection");

        fake.Mute = false;
        rig.Unkey();
        fake.Ptt.Should().BeFalse();
    }

    [Fact]
    public void A_refused_key_is_an_error_but_keeps_the_connection()
    {
        using var fake = new FakeRigctld { RefusePtt = true };
        using var rig = new Rigctld(Cat(fake), pttByCat: true, null, Timeout, Retry);
        rig.Start();

        FluentActions.Invoking(rig.Key).Should().Throw<IOException>().WithMessage("CAT PTT: rigctld refused to key the radio (RPRT -1)");
        rig.Connected.Should().BeTrue();
    }

    [Fact]
    public void An_unkey_after_a_key_with_rigctld_gone_says_so()
    {
        var fake = new FakeRigctld();
        int port = fake.Port;
        using var rig = new Rigctld(Cat(fake), pttByCat: true, null, Timeout, TimeSpan.FromMinutes(5));
        rig.Start();
        rig.Key();
        fake.Dispose();

        FluentActions.Invoking(rig.Unkey).Should().Throw<IOException>().WithMessage($"CAT PTT: cannot reach rigctld at 127.0.0.1:{port} to unkey (*); check the radio is not left transmitting");
    }

    [Fact]
    public void An_unkey_with_rigctld_gone_and_no_key_sent_since_the_last_unkey_sends_nothing()
    {
        int port = FreePort();
        using var rig = new Rigctld(new CatSettings("127.0.0.1", port, 7052300), pttByCat: true, null, Timeout, Retry);
        FluentActions.Invoking(rig.Unkey).Should().NotThrow("nothing was keyed, so there is nothing to undo");
    }

    [Fact]
    public async Task On_the_radio_a_burst_is_keyed_and_unkeyed_by_CAT_around_the_audio()
    {
        using var fake = new FakeRigctld();
        var log = new EventLog();
        var output = new FakeOutput(CardRate, log);
        fake.OnPtt = on => log.Add(on ? "key" : "unkey");
        var rig = new Rigctld(Cat(fake), pttByCat: true, null, Timeout, Retry);
        rig.Start();
        using var radio = RadioMedium.Create(Settings(), new FakeInput(CardRate), output, new CatPtt(rig), null, cat: rig);
        var played = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        radio.Audio.Start(_ => { });

        radio.Audio.Play(new float[960], played.SetResult);
        await played.Task.WaitAsync(TimeSpan.FromSeconds(10));

        log.Snapshot().Should().Equal("unkey", "key", $"write {(960 * 5) + (CardRate / 100 * 3)}", "drain", "unkey");
    }

    [Fact]
    public async Task On_the_radio_with_rigctld_gone_a_burst_is_not_sent_and_the_log_says_why()
    {
        int port = FreePort();
        var log = new EventLog();
        var lines = new Lines();
        var rig = new Rigctld(new CatSettings("127.0.0.1", port, 7052300), pttByCat: true, lines.Add, Timeout, Retry);
        rig.Start();
        using var radio = RadioMedium.Create(Settings(), new FakeInput(CardRate), new FakeOutput(CardRate, log), new CatPtt(rig), lines.Add, cat: rig);
        var played = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        radio.Audio.Start(_ => { });

        radio.Audio.Play(new float[960], played.SetResult);
        await played.Task.WaitAsync(TimeSpan.FromSeconds(10));

        log.Snapshot().Should().BeEmpty("nothing is played unkeyed");
        // One line for the lost rigctld and one for the burst: no unkey was sent, as no key was.
        lines.All.Should().HaveCount(2);
        lines.All[0].Should().StartWith($"radio: cat: no rigctld at 127.0.0.1:{port} (");
        lines.All[1].Should().Be($"radio: burst not sent, PTT failed: CAT PTT: no answer from rigctld at 127.0.0.1:{port}, so not transmitting");
    }

    [Fact]
    public async Task On_the_radio_by_CAT_a_hung_playback_device_is_unkeyed_by_the_transmit_guard()
    {
        using var fake = new FakeRigctld();
        var log = new EventLog();
        using var hang = new ManualResetEventSlim();
        var output = new FakeOutput(CardRate, log) { Hang = hang };
        using var rig = new Rigctld(Cat(fake), pttByCat: true, null, Timeout, Retry);
        rig.Start();
        var channel = new RadioAudioChannel(new FakeInput(CardRate), output, new CatPtt(rig), 0, 0, null, TimeSpan.FromMilliseconds(200));
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Failed += e => failed.TrySetResult(e);
        channel.Start(_ => { });

        channel.Play(new float[960], () => { });
        Exception e = await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        e.Message.Should().StartWith("transmit guard");
        fake.Ptt.Should().BeFalse();
        hang.Set();
        channel.Dispose();
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(100);

    private static CatSettings Cat(FakeRigctld fake) => new("127.0.0.1", fake.Port, 7052300);

    private static RadioSettings Settings() => new("c", "p", CardRate, null, 3, 10, 20, 0.5, false, null, null, true);

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class Lines
    {
        private readonly List<string> _lines = [];

        public string[] All
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public void Add(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        }
    }
}

/// <summary>
/// A fake rigctld: Hamlib's plain text protocol on a local TCP port, one command a line, set
/// commands answered <c>RPRT 0</c> (or <c>RPRT -1</c> when told to refuse), <c>f</c> with the
/// frequency and <c>m</c> with the mode and passband, as rigctld does.
/// </summary>
internal sealed class FakeRigctld : IDisposable
{
    private readonly TcpListener _listener;
    private readonly List<string> _commands = [];
    private readonly List<TcpClient> _clients = [];
    private readonly CancellationTokenSource _stop = new();

    public FakeRigctld(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    public long Frequency { get; private set; } = 7052300;

    public string Mode { get; set; } = "PKTUSB";

    public int Passband { get; private set; } = 3000;

    public int WidestPassband { get; set; } = 3000;

    public bool RefuseWidth { get; set; }

    public bool RefuseMode { get; set; }

    public bool RefuseFrequency { get; set; }

    public bool RefusePtt { get; set; }

    /// <summary>Takes commands but answers none.</summary>
    public volatile bool Mute;

    public volatile bool Ptt;

    public Action<bool>? OnPtt { get; set; }

    public string[] Commands
    {
        get
        {
            lock (_commands)
            {
                return [.. _commands];
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        lock (_clients)
        {
            foreach (TcpClient c in _clients)
            {
                c.Dispose();
            }
        }
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                lock (_clients)
                {
                    _clients.Add(client);
                }

                _ = Task.Run(() => Serve(client));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task Serve(TcpClient client)
    {
        try
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
            NetworkStream stream = client.GetStream();
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                lock (_commands)
                {
                    _commands.Add(line);
                }

                string reply = Answer(line.Split(' '));
                if (!Mute)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), _stop.Token);
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private string Answer(string[] words)
    {
        const string Ok = "RPRT 0\n";
        const string No = "RPRT -1\n";
        switch (words)
        {
            case ["F", var hz]:
                if (RefuseFrequency)
                {
                    return No;
                }

                Frequency = long.Parse(hz, System.Globalization.CultureInfo.InvariantCulture);
                return Ok;
            case ["f"]:
                return $"{Frequency}\n";
            case ["M", var mode, var width]:
                if (RefuseMode)
                {
                    return No;
                }

                int w = int.Parse(width, System.Globalization.CultureInfo.InvariantCulture);
                if (RefuseWidth && w != 0)
                {
                    return No;
                }

                Mode = mode;
                Passband = w == 0 ? WidestPassband : Math.Min(w, WidestPassband);
                return Ok;
            case ["m"]:
                return $"{Mode}\n{Passband}\n";
            case ["T", var on]:
                if (RefusePtt)
                {
                    return No;
                }

                Ptt = on == "1";
                OnPtt?.Invoke(Ptt);
                return Ok;
            case ["t"]:
                return Ptt ? "1\n" : "0\n";
            default:
                return "RPRT -11\n";
        }
    }
}
