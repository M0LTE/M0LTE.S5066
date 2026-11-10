using System.Text;
using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Host.Station;

namespace M0LTE.S5066.Host.Tests;

/// <summary>
/// The CW identification hook (P8-2; plan 9.1, decision D4; core-architecture.md 10 item 13): the
/// Morse audio, checked by a Goertzel CW decoder that knows nothing of how it was made, the
/// schedule on a virtual clock, and the <c>host.station.cw_id</c> settings.
/// </summary>
public class StationIdTests
{
    [Theory]
    [InlineData("DE M0XYZ", 20, 1800, 48000)]
    [InlineData("VVV DE G4ABC/P", 25, 1000, 9600)]
    [InlineData("QRL? 73 = TEST, 1.2 -+", 12, 700, 8000)]
    [InlineData("A1B2 C3", 40, 2400, 48000)]
    public void The_audio_decodes_as_the_configured_text(string text, int wpm, double tone, int rate)
    {
        float[] audio = Morse.Generate(text, wpm, tone, 0.5, rate);

        Decode(audio, tone, rate).Should().Be(Morse.Normalise(text));
    }

    [Fact]
    public void A_decoder_tuned_away_from_the_tone_hears_nothing()
    {
        float[] audio = Morse.Generate("DE M0XYZ", 20, 1800, 0.5, 48000);

        Power(audio, 800, 48000).Max().Should().BeLessThan(Power(audio, 1800, 48000).Max() / 100, "the keyed tone stays at its frequency (20 dB down 1 kHz away)");
    }

    [Fact]
    public void Paris_timing_one_dit_is_1200_over_wpm_ms_and_PARIS_is_43_dits_without_its_word_gap()
    {
        Morse.DitMs(20).Should().Be(60);
        Morse.Units("PARIS").Should().HaveCount(43);
        Morse.Units("PARIS PARIS").Should().HaveCount(43 + 7 + 43);
        Morse.DurationMs("PARIS", 20).Should().Be(43 * 60);
        Morse.DurationMs("DE M0XYZ", 20).Should().Be(91 * 60, "the example in the docs, 5.5 s");
        Morse.Generate("PARIS", 20, 1800, 0.5, 48000).Should().HaveCount(43 * 60 * 48);
    }

    [Fact]
    public void The_keying_is_shaped_so_it_makes_no_clicks()
    {
        const int rate = 48000;
        const double tone = 1800;
        float[] audio = Morse.Generate("EEEE TTTT", 30, tone, 0.8, rate);

        // No step: sample to sample, the signal never moves more than a pure tone of that amplitude can.
        double maxStep = 0.8 * 2 * Math.PI * tone / rate;
        for (int i = 1; i < audio.Length; i++)
        {
            ((double)Math.Abs(audio[i] - audio[i - 1])).Should().BeLessThanOrEqualTo(maxStep * 1.01, $"sample {i}");
        }

        // Starts and ends at silence, and each element rises over about 5 ms rather than at once.
        audio[0].Should().Be(0);
        Math.Abs(audio[^1]).Should().BeLessThan(0.01f);
        int rise = rate * 5 / 1000;
        audio.AsSpan(0, rise / 4).ToArray().Max(Math.Abs).Should().BeLessThan(0.8f * 0.2f, "the first quarter of the rise stays low");
    }

    [Fact]
    public void Characters_without_a_code_are_refused_and_text_is_normalised()
    {
        Morse.Normalise("  de  m0xyz ").Should().Be("DE M0XYZ");
        FluentActions.Invoking(() => Morse.Normalise("DE M0XYZ!")).Should().Throw<ArgumentException>().WithMessage("*'!'*");
    }

    [Fact]
    public void At_start_identifies_before_the_first_keyup_and_again_only_after_ten_quiet_minutes()
    {
        var s = new CwIdSchedule(atStart: true, everyMinutes: null);
        const long Min = 60_000;

        s.Keyup(5 * Min).Should().BeTrue("the first keyup since start-up");
        s.Keyup((5 * Min) + 2_000).Should().BeFalse();
        s.Keyup(14 * Min).Should().BeFalse("9 minutes after the last keyup is not a quiet spell");
        s.Keyup(24 * Min).Should().BeTrue("10 minutes with no keyup");
        s.Keyup(25 * Min).Should().BeFalse();
        s.State().Sent.Should().Be(2);
    }

    [Fact]
    public void Every_n_minutes_goes_with_the_first_keyup_once_due_so_an_idle_node_stays_silent()
    {
        var s = new CwIdSchedule(atStart: false, everyMinutes: 10);
        const long Min = 60_000;

        s.Keyup(1 * Min).Should().BeFalse("no pre-session ID; the first keyup starts the clock");
        s.State().PeriodicDueFromMs.Should().Be(11 * Min);
        s.Keyup(10 * Min).Should().BeFalse();
        s.Keyup(11 * Min).Should().BeTrue("ten minutes since the first keyup");
        s.Keyup(15 * Min).Should().BeFalse();

        // Idle for an hour: no ID is sent while idle (nothing calls Keyup), and the next keyup carries it.
        s.Keyup(80 * Min).Should().BeTrue();
        s.Keyup(81 * Min).Should().BeFalse();
        s.State().Should().Be(new CwIdState(2, 80 * Min, 81 * Min, 90 * Min));
    }

    [Fact]
    public void Both_together_never_send_two_ids_for_one_keyup_and_restart_the_period()
    {
        var s = new CwIdSchedule(atStart: true, everyMinutes: 5);
        const long Min = 60_000;
        var ids = new List<long>();

        // A keyup every 30 s for 12 minutes, a quiet spell of 20 minutes, then 3 more minutes.
        for (long t = 0; t <= 12 * Min; t += 30_000)
        {
            if (s.Keyup(t))
            {
                ids.Add(t);
            }
        }

        for (long t = 32 * Min; t <= 35 * Min; t += 30_000)
        {
            if (s.Keyup(t))
            {
                ids.Add(t);
            }
        }

        ids.Should().Equal(0, 5 * Min, 10 * Min, 32 * Min);
    }

    [Fact]
    public void The_settings_read_with_defaults_and_refuse_what_cannot_be_sent()
    {
        CwIdSettings s = CwIdSettings.Parse(new JsonObject { ["text"] = "de m0xyz" });
        s.Should().Be(new CwIdSettings("DE M0XYZ", 20, 1800, null, true, null));

        CwIdSettings full = CwIdSettings.Parse(JsonNode.Parse("""{"text":"DE M0XYZ","wpm":25,"tone_hz":1000,"level":0.3,"at_start":false,"every_minutes":10}""")!.AsObject());
        full.Should().Be(new CwIdSettings("DE M0XYZ", 25, 1000, 0.3, false, 10));

        Refused("""{"wpm":20}""", "host.station.cw_id.text: is required");
        Refused("""{"text":"DE M0XYZ!"}""", "host.station.cw_id.text: '!' has no Morse code*");
        Refused("""{"text":"DE M0XYZ","wpm":60}""", "host.station.cw_id.wpm: must be from 5 to 40");
        Refused("""{"text":"DE M0XYZ","tone_hz":5000}""", "host.station.cw_id.tone_hz: must be from 300 to 3000");
        Refused("""{"text":"DE M0XYZ","level":1.5}""", "host.station.cw_id.level: must be above 0 and at most 1");
        Refused("""{"text":"DE M0XYZ","at_start":false}""", "host.station.cw_id.at_start: is false and every_minutes is not set*");
        Refused("""{"text":"DE M0XYZ","every_minutes":0}""", "host.station.cw_id.every_minutes: must be from 1 to 60");
        Refused("""{"text":"DE M0XYZ","when":"end"}""", "host.station.cw_id.when: unknown key");
        Refused("""{"text":"THIS IS A VERY LONG IDENTIFICATION TEXT INDEED","wpm":10}""", "host.station.cw_id.text: takes * s at 10 wpm, longer than 20 s*");
    }

    [Fact]
    public void A_host_file_carries_the_cw_id_and_the_status_address()
    {
        HostConfig c = HostConfig.Parse("""
            {"local_address":"0.0.1.1","csma":{"mode":"jitter"},"cas":{"max_links":4,"link_request_retries":3,"break_retries":3},
             "host":{"status_listen":"127.0.0.1:8066","station":{"cw_id":{"text":"DE M0XYZ","every_minutes":15}}}}
            """);
        c.StatusEndpoint!.ToString().Should().Be("127.0.0.1:8066");
        c.CwId.Should().Be(new CwIdSettings("DE M0XYZ", 20, 1800, null, true, 15));

        HostConfig plain = HostConfig.Parse("""{"local_address":"0.0.1.1","csma":{"mode":"jitter"},"cas":{"max_links":4,"link_request_retries":3,"break_retries":3}}""");
        plain.CwId.Should().BeNull();
        plain.StatusEndpoint.Should().BeNull("the page is off unless asked for");

        FluentActions.Invoking(() => HostConfig.Parse("""{"local_address":"0.0.1.1","csma":{"mode":"jitter"},"cas":{"max_links":4,"link_request_retries":3,"break_retries":3},"host":{"station":{"beacon":1}}}"""))
            .Should().Throw<ConfigException>().WithMessage("host.station.beacon: unknown key");
    }

    private static void Refused(string json, string message) =>
        FluentActions.Invoking(() => CwIdSettings.Parse(JsonNode.Parse(json)!.AsObject())).Should().Throw<ConfigException>().WithMessage(message);

    /// <summary>
    /// The ITU-R M.1677-1 codes, typed here independently of <see cref="Morse"/> so the decoder
    /// checks the code table too, not only the timing.
    /// </summary>
    private static readonly Dictionary<string, char> Itu = new()
    {
        [".-"] = 'A', ["-..."] = 'B', ["-.-."] = 'C', ["-.."] = 'D', ["."] = 'E', ["..-."] = 'F', ["--."] = 'G',
        ["...."] = 'H', [".."] = 'I', [".---"] = 'J', ["-.-"] = 'K', [".-.."] = 'L', ["--"] = 'M', ["-."] = 'N',
        ["---"] = 'O', [".--."] = 'P', ["--.-"] = 'Q', [".-."] = 'R', ["..."] = 'S', ["-"] = 'T', ["..-"] = 'U',
        ["...-"] = 'V', [".--"] = 'W', ["-..-"] = 'X', ["-.--"] = 'Y', ["--.."] = 'Z',
        ["-----"] = '0', [".----"] = '1', ["..---"] = '2', ["...--"] = '3', ["....-"] = '4',
        ["....."] = '5', ["-...."] = '6', ["--..."] = '7', ["---.."] = '8', ["----."] = '9',
        ["-..-."] = '/', ["..--.."] = '?', [".-.-.-"] = '.', ["--..--"] = ',', ["-...-"] = '=', ["-....-"] = '-', [".-.-."] = '+',
    };

    [Fact]
    public void Every_character_of_the_alphabet_decodes_by_the_itu_table()
    {
        string all = "ABCDEFGHIJ KLMNOPQRST UVWXYZ0123 456789/?., =-+";
        Decode(Morse.Generate(all, 20, 1800, 0.5, 8000), 1800, 8000).Should().Be(all);
    }

    /// <summary>Goertzel power at <paramref name="tone"/> over consecutive 2 ms frames.</summary>
    private static double[] Power(float[] audio, double tone, int rate)
    {
        int frame = rate * 2 / 1000;
        double coeff = 2 * Math.Cos(2 * Math.PI * tone / rate);
        var power = new double[audio.Length / frame];
        for (int f = 0; f < power.Length; f++)
        {
            double s1 = 0, s2 = 0;
            for (int i = f * frame; i < (f + 1) * frame; i++)
            {
                double s0 = audio[i] + (coeff * s1) - s2;
                s2 = s1;
                s1 = s0;
            }

            power[f] = (s1 * s1) + (s2 * s2) - (coeff * s1 * s2);
        }

        return power;
    }

    /// <summary>
    /// A plain CW decoder: tone power per frame, key down above a quarter of the peak, then run
    /// lengths measured in units of the shortest key-down run (a dit): under two units is a dit,
    /// else a dah; a gap under two units is inside a character, under five ends the character, and
    /// longer ends the word too.
    /// </summary>
    private static string Decode(float[] audio, double tone, int rate)
    {
        double[] power = Power(audio, tone, rate);
        double threshold = power.Max() / 4;
        var runs = new List<(bool On, int Frames)>();
        foreach (double p in power)
        {
            bool on = p > threshold;
            if (runs.Count > 0 && runs[^1].On == on)
            {
                runs[^1] = (on, runs[^1].Frames + 1);
            }
            else
            {
                runs.Add((on, 1));
            }
        }

        double dit = runs.Where(r => r.On).Min(r => r.Frames);
        var text = new StringBuilder();
        var code = new StringBuilder();
        void EndCharacter()
        {
            if (code.Length > 0)
            {
                text.Append(Itu.TryGetValue(code.ToString(), out char c) ? c : '#');
                code.Clear();
            }
        }

        foreach ((bool on, int frames) in runs)
        {
            double units = frames / dit;
            if (on)
            {
                code.Append(units < 2 ? '.' : '-');
            }
            else if (units >= 5)
            {
                EndCharacter();
                if (text.Length > 0)
                {
                    text.Append(' ');
                }
            }
            else if (units >= 2)
            {
                EndCharacter();
            }
        }

        EndCharacter();
        return text.ToString().Trim();
    }
}
