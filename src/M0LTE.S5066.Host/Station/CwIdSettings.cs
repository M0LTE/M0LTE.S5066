using System.Text.Json;
using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Host.Station;

/// <summary>
/// The optional <c>host.station.cw_id</c> object (plan 9.1, decision D4; core-architecture.md 10
/// item 13): a Morse identification the operator sets, keyed ahead of the node's own transmissions.
/// Nothing in it is fixed by the node: the text, and whether to send one at all, are the
/// deployment's choice.
/// </summary>
/// <param name="Text">text: what to send, already normalised (<see cref="Morse.Normalise"/>).</param>
/// <param name="Wpm">wpm: speed in words a minute, PARIS timing; 20 by default, 5 to 40.</param>
/// <param name="ToneHz">tone_hz: the tone; 1800 Hz by default, the modem's audio centre (plan 9.1), 300 to 3000.</param>
/// <param name="Level">level: peak amplitude as a fraction of full scale, or null for the radio's tx_level.</param>
/// <param name="AtStart">at_start: identify before the first transmission after a quiet spell (<see cref="CwIdSchedule.QuietMs"/>); true by default.</param>
/// <param name="EveryMinutes">every_minutes: identify again before the first transmission once this many minutes have passed since the last ID, or null for no periodic ID; 1 to 60.</param>
public sealed record CwIdSettings(string Text, int Wpm, double ToneHz, double? Level, bool AtStart, int? EveryMinutes)
{
    private const string Prefix = "host.station.cw_id.";

    /// <summary>
    /// The longest ID accepted, in ms: 20 s. Every second of it is added to a transmission, and the
    /// far node's answer waits (C-1120, C-1051) have only a few seconds of slack.
    /// </summary>
    public const int MaxDurationMs = 20_000;

    /// <summary>How long the ID takes to send, in ms.</summary>
    public int DurationMs => Morse.DurationMs(Text, Wpm);

    /// <summary>Reads the object.</summary>
    /// <exception cref="ConfigException">A key is unknown, missing or wrong; the message names it.</exception>
    public static CwIdSettings Parse(JsonObject o)
    {
        ArgumentNullException.ThrowIfNull(o);
        string[] known = ["text", "wpm", "tone_hz", "level", "at_start", "every_minutes"];
        foreach (string key in o.Select(kv => kv.Key))
        {
            if (!known.Contains(key))
            {
                throw new ConfigException(Prefix + key, "unknown key");
            }
        }

        if (o["text"] is not JsonNode t)
        {
            throw new ConfigException(Prefix + "text", "is required");
        }

        string text;
        try
        {
            text = Morse.Normalise(Read<string>(t, "text", "a string"));
        }
        catch (ArgumentException e)
        {
            throw new ConfigException(Prefix + "text", e.Message.Split(" (Parameter", 2)[0]);
        }

        if (text.Length == 0)
        {
            throw new ConfigException(Prefix + "text", "must not be empty");
        }

        int wpm = o["wpm"] is JsonNode w ? Read<int>(w, "wpm", "a whole number") : 20;
        if (wpm is < 5 or > 40)
        {
            throw new ConfigException(Prefix + "wpm", "must be from 5 to 40");
        }

        double tone = o["tone_hz"] is JsonNode h ? Read<double>(h, "tone_hz", "a number") : 1800;
        if (tone is < 300 or > 3000)
        {
            throw new ConfigException(Prefix + "tone_hz", "must be from 300 to 3000");
        }

        double? level = o["level"] is JsonNode l ? Read<double>(l, "level", "a number") : null;
        if (level is double v && !(v > 0 && v <= 1))
        {
            throw new ConfigException(Prefix + "level", "must be above 0 and at most 1");
        }

        bool atStart = o["at_start"] is JsonNode s ? Read<bool>(s, "at_start", "true or false") : true;
        int? every = o["every_minutes"] is JsonNode e2 ? Read<int>(e2, "every_minutes", "a whole number") : null;
        if (every is < 1 or > 60)
        {
            throw new ConfigException(Prefix + "every_minutes", "must be from 1 to 60");
        }

        if (!atStart && every is null)
        {
            throw new ConfigException(Prefix + "at_start", "is false and every_minutes is not set, so the ID would never be sent; remove cw_id instead");
        }

        var settings = new CwIdSettings(text, wpm, tone, level, atStart, every);
        if (settings.DurationMs > MaxDurationMs)
        {
            throw new ConfigException(Prefix + "text", $"takes {settings.DurationMs / 1000.0:0.0} s at {wpm} wpm, longer than {MaxDurationMs / 1000} s; shorten it or raise wpm");
        }

        return settings;
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
