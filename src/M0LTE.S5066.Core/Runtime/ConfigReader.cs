using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;

namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// Builds a <see cref="NodeConfig"/> from a fully merged configuration object with the keys of
/// corpus-format.md section 6. Used by the T2 harness and by a host that keeps its configuration
/// in the same shape; <see cref="Node"/> itself never sees JSON (core-architecture.md 2).
/// </summary>
/// <remarks>
/// <c>local_address</c> and <c>address_size</c> are needed by every node, so a null there throws
/// <see cref="MissingSettingException"/> here. Settings needed only by some behaviour (for example
/// <c>csma.mode</c>, needed once the node contends) stay null in the record and throw when used.
/// </remarks>
public static class ConfigReader
{
    /// <summary>Every leaf key this reader knows, dotted, in the order of corpus-format.md section 6.</summary>
    public static IReadOnlyList<string> KnownKeys { get; } =
    [
        "local_address", "address_size", "prng_seed",
        "csma.mode", "csma.cont_slot_width_ms", "csma.num_cont_slots", "csma.lbt_wait_dcd_ms",
        "csma.lbt_wait_vdcd_ms", "csma.node_slot_position",
        "modem.preamble_ms", "modem.flush_ms", "modem.waveforms",
    ];

    /// <summary>
    /// The first leaf key of <paramref name="config"/>, in document order, that this reader does not
    /// know, or null. A harness reports a scenario with one as unsupported (harness-contract.md 1).
    /// </summary>
    public static string? UnknownKey(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return FirstUnknown(config, "");
    }

    /// <summary>Reads <c>prng_seed</c>: 16 lower-case hex digits (prng.md 3).</summary>
    public static ulong ReadSeed(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        string s = String(config, "prng_seed") ?? throw new MissingSettingException("prng_seed");
        if (s.Length != 16 || !ulong.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong seed))
        {
            throw new ConfigException("prng_seed", "must be 16 hex digits");
        }

        return seed;
    }

    /// <summary>Builds the record. Throws <see cref="ConfigException"/> or <see cref="MissingSettingException"/>.</summary>
    public static NodeConfig Read(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        string addr = String(config, "local_address") ?? throw new MissingSettingException("local_address");
        if (!DpduAddress.TryParse(addr, out uint local))
        {
            throw new ConfigException("local_address", "must be a dotted address w.x.y.z");
        }

        int addressSize = Int(config, "address_size") ?? throw new MissingSettingException("address_size");
        if (addressSize is < 1 or > 7)
        {
            throw new ConfigException("address_size", "must be 1 to 7");
        }

        JsonObject csma = Object(config, "csma");
        CsmaMode? mode = String(csma, "mode", "csma.") switch
        {
            null => null,
            "jitter" => CsmaMode.Jitter,
            "slotted" => CsmaMode.Slotted,
            "ed3" => CsmaMode.Ed3,
            _ => throw new ConfigException("csma.mode", "must be jitter, slotted or ed3"),
        };
        var csmaConfig = new CsmaConfig(
            mode,
            Required(csma, "cont_slot_width_ms", "csma.", 1),
            Required(csma, "num_cont_slots", "csma.", 1),
            Required(csma, "lbt_wait_dcd_ms", "csma.", 0),
            Required(csma, "lbt_wait_vdcd_ms", "csma.", 0),
            Int(csma, "node_slot_position", "csma."));

        JsonObject modem = Object(config, "modem");
        var waveforms = new List<ModemWaveform>();
        if (modem["waveforms"] is not JsonArray list)
        {
            throw new ConfigException("modem.waveforms", "must be a list");
        }

        foreach (JsonNode? entry in list)
        {
            if (entry is not JsonObject e)
            {
                throw new ConfigException("modem.waveforms", "entries must be objects");
            }

            string il = String(e, "interleaver", "modem.waveforms.") ?? throw new ConfigException("modem.waveforms", "interleaver missing");
            waveforms.Add(new ModemWaveform(
                Required(e, "wn", "modem.waveforms.", 0),
                InterleaverNames.Parse(il),
                Required(e, "block_bytes", "modem.waveforms.", 1),
                Required(e, "block_ms", "modem.waveforms.", 1)));
        }

        var fixture = new ModemFixture(Int(modem, "preamble_ms", "modem."), Int(modem, "flush_ms", "modem."), waveforms);
        return new NodeConfig(local, addressSize, csmaConfig, fixture);
    }

    private static string? FirstUnknown(JsonObject obj, string prefix)
    {
        foreach (KeyValuePair<string, JsonNode?> kv in obj)
        {
            string key = prefix + kv.Key;
            if (kv.Value is JsonObject child && !KnownKeys.Contains(key, StringComparer.Ordinal))
            {
                if (FirstUnknown(child, key + ".") is { } inner)
                {
                    return inner;
                }
            }
            else if (!KnownKeys.Contains(key, StringComparer.Ordinal))
            {
                return key;
            }
        }

        return null;
    }

    private static JsonObject Object(JsonObject parent, string key) =>
        parent[key] as JsonObject ?? throw new ConfigException(key, "must be an object");

    private static string? String(JsonObject obj, string key, string prefix = "")
    {
        JsonNode? n = obj[key];
        if (n is null)
        {
            return null;
        }

        return n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : throw new ConfigException(prefix + key, "must be a string");
    }

    private static int? Int(JsonObject obj, string key, string prefix = "")
    {
        JsonNode? n = obj[key];
        if (n is null)
        {
            return null;
        }

        // Parse the JSON text, so a value read from a file and one built in code behave alike;
        // a fraction or an exponent does not parse, as corpus-format.md section 2 requires.
        if (n.GetValueKind() != JsonValueKind.Number
            || !long.TryParse(n.ToJsonString(), NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            || value > int.MaxValue)
        {
            throw new ConfigException(prefix + key, "must be a non-negative integer");
        }

        return (int)value;
    }

    private static int Required(JsonObject obj, string key, string prefix, int min)
    {
        int value = Int(obj, key, prefix) ?? throw new MissingSettingException(prefix + key);
        return value >= min ? value : throw new ConfigException(prefix + key, $"must be at least {min}");
    }
}

/// <summary>The T2 spellings of <see cref="Interleaver"/> (corpus-format.md 4.2). For traces and configuration only.</summary>
public static class InterleaverNames
{
    /// <summary>The trace spelling, for example <c>very_long</c>.</summary>
    public static string ToName(Interleaver il) => il switch
    {
        Interleaver.UltraShort => "ultra_short",
        Interleaver.VeryShort => "very_short",
        Interleaver.Short => "short",
        Interleaver.Medium => "medium",
        Interleaver.Long => "long",
        Interleaver.VeryLong => "very_long",
        _ => throw new ArgumentOutOfRangeException(nameof(il)),
    };

    /// <summary>Parses a trace spelling. Throws <see cref="ConfigException"/> on anything else.</summary>
    public static Interleaver Parse(string name) => name switch
    {
        "ultra_short" => Interleaver.UltraShort,
        "very_short" => Interleaver.VeryShort,
        "short" => Interleaver.Short,
        "medium" => Interleaver.Medium,
        "long" => Interleaver.Long,
        "very_long" => Interleaver.VeryLong,
        _ => throw new ConfigException("interleaver", $"'{name}' is not an interleaver name"),
    };
}
