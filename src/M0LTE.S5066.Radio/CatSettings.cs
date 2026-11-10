using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Radio;

/// <summary>
/// The <c>host.modem.radio.cat</c> object (core-architecture.md 12.13): where Hamlib's rigctld
/// listens and the frequency to set. The mode (<see cref="Mode"/>) and passband
/// (<see cref="PassbandHz"/>) are fixed: rigctld's data USB, as wide as the radio allows.
/// </summary>
/// <param name="Host">The host part of rigctld: <c>127.0.0.1</c> by default.</param>
/// <param name="Port">The port of rigctld: 4532, rigctld's own default, by default.</param>
/// <param name="FrequencyHz">frequency_hz: the radio's dial frequency in Hz, set at start and on each reconnection.</param>
public sealed record CatSettings(string Host, int Port, long FrequencyHz)
{
    private const string Prefix = "host.modem.radio.cat.";

    /// <summary>rigctld's own default port.</summary>
    public const int DefaultPort = 4532;

    /// <summary>
    /// The mode set: Hamlib's name for data on upper sideband, which each Hamlib backend maps to
    /// its radio's own data mode (Hamlib's FT-450 backend sends mode C, data USB; its FT-818 backend
    /// selects DIG and sets DIG MODE to USER-U).
    /// </summary>
    public const string Mode = "PKTUSB";

    /// <summary>The receive passband asked for, in Hz: the waveforms' 3 kHz. The radio may set less.</summary>
    public const int PassbandHz = 3000;

    /// <summary>rigctld's address as <c>host:port</c>.</summary>
    public string Address => (Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]" : Host) + ":" + Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads the object.</summary>
    /// <exception cref="ConfigException">A key is unknown, missing or wrong; the message names it.</exception>
    public static CatSettings Parse(JsonObject cat)
    {
        ArgumentNullException.ThrowIfNull(cat);
        foreach (string key in cat.Select(kv => kv.Key))
        {
            if (key is not ("rigctld" or "frequency_hz"))
            {
                throw new ConfigException(Prefix + key, "unknown key");
            }
        }

        (string host, int port) = ("127.0.0.1", DefaultPort);
        if (cat["rigctld"] is JsonNode r)
        {
            (host, port) = ParseAddress(Read<string>(r, "rigctld", "a string"));
        }

        if (cat["frequency_hz"] is not JsonNode f)
        {
            throw new ConfigException(Prefix + "frequency_hz", "is required");
        }

        long hz = Read<long>(f, "frequency_hz", "a whole number of Hz");
        if (hz <= 0)
        {
            throw new ConfigException(Prefix + "frequency_hz", "must be above 0");
        }

        return new CatSettings(host, port, hz);
    }

    private static (string Host, int Port) ParseAddress(string text)
    {
        const string Wanted = "must be host:port, for example 127.0.0.1:4532";
        int colon = text.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
        {
            throw new ConfigException(Prefix + "rigctld", Wanted);
        }

        string host = text[..colon];
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
        }

        return host.Length == 0 ? throw new ConfigException(Prefix + "rigctld", Wanted) : (host, port);
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
