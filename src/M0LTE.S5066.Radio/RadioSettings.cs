using System.Text.Json;
using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Host.Audio;

namespace M0LTE.S5066.Radio;

/// <summary>
/// The <c>host.modem.radio</c> object of an s5066d configuration file (core-architecture.md
/// 12.11): a sound card for the modem's audio, a CM108 GPIO pin for PTT, and the timing and level
/// of each transmission, and optionally CAT control through Hamlib's rigctld (<see cref="CatSettings"/>).
/// </summary>
/// <param name="CaptureDevice">capture_device: the ALSA device the receiver's audio comes from, for example <c>plughw:CARD=Device,DEV=0</c>.</param>
/// <param name="PlaybackDevice">playback_device: the ALSA device that feeds the transmitter's audio input.</param>
/// <param name="SampleRate">sample_rate: the rate both devices are opened at, a multiple of 9600 Hz from 19200 (the resamplers need a factor of at least 2); 48000 by default.</param>
/// <param name="PttDevice">ptt_device: the CM108's hidraw node, for example <c>/dev/hidraw0</c>; null when PTT is by CAT.</param>
/// <param name="PttGpio">ptt_gpio: the CM108 GPIO pin wired to PTT, 1 to 8; 3 by default, as on most interfaces.</param>
/// <param name="TxDelayMs">txdelay_ms: silence sent after keying and before the burst, so a radio still switching to transmit does not clip the preamble; 250 by default.</param>
/// <param name="TxTailMs">txtail_ms: silence sent after the burst before unkeying; 20 by default.</param>
/// <param name="TxLevel">tx_level: the modulator's amplitude, a fraction of full scale above 0 and at most 1; 0.5 (the modulator's own default) by default.</param>
/// <param name="BusyDetect">busy_detect: whether in-band energy on the channel feeds the node's external busy input; off by default (plan A7).</param>
/// <param name="RxRecord">rx_record: a WAV file to which all received audio is written, at 9600 Hz, or null for none.</param>
/// <param name="Cat">cat: the radio's CAT control through rigctld, or null for none.</param>
/// <param name="PttByCat">ptt: true for <c>"cat"</c> (key through rigctld), false for <c>"cm108"</c>, the default (the CM108 GPIO pin).</param>
public sealed record RadioSettings(
    string CaptureDevice,
    string PlaybackDevice,
    int SampleRate,
    string? PttDevice,
    int PttGpio,
    int TxDelayMs,
    int TxTailMs,
    double TxLevel,
    bool BusyDetect,
    string? RxRecord,
    CatSettings? Cat = null,
    bool PttByCat = false)
{
    private const string Prefix = "host.modem.radio.";

    /// <summary>The default sample rate: 48 kHz, which every USB sound card offers.</summary>
    public const int DefaultSampleRate = 48000;

    /// <summary>Reads the object.</summary>
    /// <exception cref="ConfigException">A key is unknown, missing or wrong; the message names it.</exception>
    public static RadioSettings Parse(JsonObject radio)
    {
        ArgumentNullException.ThrowIfNull(radio);
        string[] known = ["capture_device", "playback_device", "sample_rate", "ptt_device", "ptt_gpio", "txdelay_ms", "txtail_ms", "tx_level", "busy_detect", "rx_record", "ptt", "cat"];
        foreach (string key in radio.Select(kv => kv.Key))
        {
            if (!known.Contains(key))
            {
                throw new ConfigException(Prefix + key, "unknown key");
            }
        }

        int rate = Int(radio, "sample_rate", DefaultSampleRate, 2 * IAudioChannel.SampleRate, 192000);
        if (rate % IAudioChannel.SampleRate != 0)
        {
            throw new ConfigException(Prefix + "sample_rate", $"must be a multiple of the modem's {IAudioChannel.SampleRate} Hz, for example 48000");
        }

        double level = radio["tx_level"] is JsonNode l ? Read<double>(l, "tx_level", "a number") : 0.5;
        if (!(level > 0 && level <= 1))
        {
            throw new ConfigException(Prefix + "tx_level", "must be above 0 and at most 1");
        }

        CatSettings? cat = radio["cat"] switch
        {
            null => null,
            JsonObject o => CatSettings.Parse(o),
            _ => throw new ConfigException(Prefix + "cat", "must be an object"),
        };
        bool pttByCat = Text(radio, "ptt") switch
        {
            null or "cm108" => false,
            "cat" => true,
            _ => throw new ConfigException(Prefix + "ptt", "must be \"cm108\" or \"cat\""),
        };
        string? pttDevice = Text(radio, "ptt_device");
        if (pttByCat)
        {
            if (cat is null)
            {
                throw new ConfigException(Prefix + "ptt", "\"cat\" needs a cat section");
            }

            foreach (string key in new[] { "ptt_device", "ptt_gpio" })
            {
                if (radio.ContainsKey(key))
                {
                    throw new ConfigException(Prefix + key, "is not used with ptt \"cat\"; remove it");
                }
            }
        }
        else if (pttDevice is null)
        {
            throw new ConfigException(Prefix + "ptt_device", "is required");
        }

        return new RadioSettings(
            Text(radio, "capture_device") ?? throw new ConfigException(Prefix + "capture_device", "is required"),
            Text(radio, "playback_device") ?? throw new ConfigException(Prefix + "playback_device", "is required"),
            rate,
            pttDevice,
            Int(radio, "ptt_gpio", 3, 1, 8),
            Int(radio, "txdelay_ms", 250, 0, 2000),
            Int(radio, "txtail_ms", 20, 0, 1000),
            level,
            radio["busy_detect"] is JsonNode b ? Read<bool>(b, "busy_detect", "true or false") : false,
            Text(radio, "rx_record"),
            cat,
            pttByCat);
    }

    private static string? Text(JsonObject o, string key)
    {
        if (o[key] is not JsonNode n)
        {
            return null;
        }

        string s = Read<string>(n, key, "a string");
        return s.Length == 0 ? throw new ConfigException(Prefix + key, "must not be empty") : s;
    }

    private static int Int(JsonObject o, string key, int fallback, int min, int max)
    {
        if (o[key] is not JsonNode n)
        {
            return fallback;
        }

        int v = Read<int>(n, key, "a whole number");
        return v < min || v > max ? throw new ConfigException(Prefix + key, $"must be from {min} to {max}") : v;
    }

    private static T Read<T>(JsonNode n, string key, string what)
    {
        try
        {
            return n.GetValue<T>();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or JsonException)
        {
            throw new ConfigException(Prefix + key, "must be " + what);
        }
    }
}
