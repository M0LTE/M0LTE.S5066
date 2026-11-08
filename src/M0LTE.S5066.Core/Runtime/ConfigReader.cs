using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// Builds a <see cref="NodeConfig"/> from a fully merged configuration object with the keys of
/// corpus-format.md section 6. Used by the T2 harness and by a host that keeps its configuration
/// in the same shape; <see cref="Node"/> itself never sees JSON (core-architecture.md 2).
/// </summary>
/// <remarks>
/// <c>local_address</c> is needed by every node, so a null there throws
/// <see cref="MissingSettingException"/> here. Settings needed only by some behaviour (for example
/// <c>csma.mode</c>, needed once the node contends) stay null in the record and throw when used.
/// </remarks>
public static class ConfigReader
{
    /// <summary>Every leaf key this reader knows, dotted, in the order of corpus-format.md section 6.</summary>
    public static IReadOnlyList<string> KnownKeys { get; } =
    [
        "local_address", "groups", "time.epoch_year", "time.epoch_day", "time.epoch_second", "prng_seed",
        "csma.mode", "csma.cont_slot_width_ms", "csma.num_cont_slots", "csma.lbt_wait_dcd_ms",
        "csma.lbt_wait_vdcd_ms", "csma.node_slot_position",
        "modem.preamble_ms", "modem.flush_ms", "modem.eom_bytes", "modem.waveforms",
        "sis.default_ttl", "sis.soft_link_idle_ms", "sis.soft_link_min_hold_ms", "sis.soft_link_max_ms",
        "sis.frag_size", "sis.confirm_wait_ms",
        "dts.max_segment_size", "dts.max_transmission_ms", "dts.max_window", "dts.ack_wait_ms",
        "drc.default_speed", "drc.table",
        "cas.max_links", "cas.link_request_timeout_ms", "cas.link_request_retries", "cas.first_data_timeout_ms",
        "cas.break_timeout_ms", "cas.break_retries", "cas.clean_close_timeout_ms",
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

        var fixture = new ModemFixture(Int(modem, "preamble_ms", "modem."), Int(modem, "flush_ms", "modem."), waveforms,
            Int(modem, "eom_bytes", "modem.") ?? 0);
        var result = new NodeConfig(local, csmaConfig, fixture)
        {
            Groups = ReadGroups(config),
            Time = ReadTime(config),
            Sis = ReadSis(config),
            Dts = ReadDts(config),
            Drc = ReadDrc(config),
            Cas = ReadCas(config),
        };

        // Issue #49 (b), interp C-1252: an acknowledgement wait shorter than the LBT wait of a peer
        // that heard only part of our transmission retransmits before that peer may answer.
        if (result.Dts.AckWaitMs is int ackWait && ackWait < result.Csma.LbtWaitDcdMs)
        {
            throw new ConfigException("dts.ack_wait_ms", $"{ackWait} is shorter than csma.lbt_wait_dcd_ms ({result.Csma.LbtWaitDcdMs}), the least time a peer needs to answer");
        }

        return result;
    }

    /// <summary>
    /// cas.*: a null stays null. The counts and the link limit throw where the CAS needs them; the
    /// timers are then derived (issue #49, interp B-0420). Limits: B-0193, B-0204, B-0216, B-0222,
    /// B-0283, B-0288, B-0322, B-0407.
    /// </summary>
    private static CasConfig ReadCas(JsonObject config)
    {
        if (config["cas"] is null)
        {
            return CasConfig.Unset;
        }

        JsonObject cas = Object(config, "cas");
        return new CasConfig(
            AtLeast(cas, "max_links", 1),
            AtLeast(cas, "link_request_timeout_ms", 1),
            AtLeast(cas, "link_request_retries", 0),
            AtLeast(cas, "first_data_timeout_ms", 1),
            AtLeast(cas, "break_timeout_ms", 1),
            AtLeast(cas, "break_retries", 0),
            AtLeast(cas, "clean_close_timeout_ms", 1));

        static int? AtLeast(JsonObject cas, string key, int min)
        {
            int? v = Int(cas, key, "cas.");
            return v is null || v >= min ? v : throw new ConfigException("cas." + key, $"must be at least {min}");
        }
    }

    /// <summary>groups: a list of dotted addresses (A-0120).</summary>
    private static List<uint> ReadGroups(JsonObject config)
    {
        if (config["groups"] is not JsonArray list)
        {
            throw new ConfigException("groups", "must be a list");
        }

        var groups = new List<uint>();
        foreach (JsonNode? entry in list)
        {
            if (entry is null || entry.GetValueKind() != JsonValueKind.String
                || !DpduAddress.TryParse(entry.GetValue<string>(), out uint g))
            {
                throw new ConfigException("groups", "entries must be dotted addresses w.x.y.z");
            }

            groups.Add(g);
        }

        return groups;
    }

    /// <summary>time.*: the UTC instant of core time 0 (A-0251).</summary>
    private static TimeConfig ReadTime(JsonObject config)
    {
        JsonObject time = Object(config, "time");
        int year = Required(time, "epoch_year", "time.", 1);
        int day = Required(time, "epoch_day", "time.", 1);
        int second = Int(time, "epoch_second", "time.") ?? throw new MissingSettingException("time.epoch_second");
        if (day > TtdCalendar.DaysInYear(year))
        {
            throw new ConfigException("time.epoch_day", "must be a day of the year, 1 to 365 or 366");
        }

        if (second >= TtdCalendar.SecondsPerDay)
        {
            throw new ConfigException("time.epoch_second", "must be 0 to 86399");
        }

        return new TimeConfig(year, day, second);
    }

    /// <summary>
    /// sis.*: the default TTL (A-0087); the soft link times, each null or at least 0 (idle, A-0013;
    /// minimum hold) or 1 (maximum session); the response fragment size, null or at least 0 (A-0102,
    /// A-0114); and the confirmation wait, null or at least 1 (A-0221, A-0226).
    /// </summary>
    private static SisConfig ReadSis(JsonObject config)
    {
        JsonObject sis = Object(config, "sis");
        int ttl = Required(sis, "default_ttl", "sis.", 1);
        if (ttl > SisConfig.MaxTtl)
        {
            throw new ConfigException("sis.default_ttl", $"must be at most {SisConfig.MaxTtl}");
        }

        return new SisConfig(ttl, AtLeast(sis, "soft_link_idle_ms", 0), AtLeast(sis, "soft_link_min_hold_ms", 0), AtLeast(sis, "soft_link_max_ms", 1),
                             AtLeast(sis, "frag_size", 0), AtLeast(sis, "confirm_wait_ms", 1));

        static int? AtLeast(JsonObject sis, string key, int min)
        {
            int? v = Int(sis, key, "sis.");
            return v is null || v >= min ? v : throw new ConfigException("sis." + key, $"must be at least {min}");
        }
    }

    /// <summary>
    /// dts.*: segment size 1 to 1023 (C-0503, C-0523), transmission length 1 to 127500 ms (K-0094),
    /// window 1 to 32768 (C-0772, C-0773), acknowledgement wait 1 or more ms or null (C-1120).
    /// </summary>
    private static DtsConfig ReadDts(JsonObject config)
    {
        JsonObject dts = Object(config, "dts");
        int segment = Required(dts, "max_segment_size", "dts.", 1);
        if (segment > DtsConfig.MaxSegmentSizeLimit)
        {
            throw new ConfigException("dts.max_segment_size", $"must be at most {DtsConfig.MaxSegmentSizeLimit}");
        }

        int transmission = Required(dts, "max_transmission_ms", "dts.", 1);
        if (transmission > DtsConfig.MaxTransmissionLimitMs)
        {
            throw new ConfigException("dts.max_transmission_ms", $"must be at most {DtsConfig.MaxTransmissionLimitMs}");
        }

        int window = Required(dts, "max_window", "dts.", 1);
        if (window > DtsConfig.MaxWindowLimit)
        {
            throw new ConfigException("dts.max_window", $"must be at most {DtsConfig.MaxWindowLimit}");   // C-0773
        }

        int? ackWait = Int(dts, "ack_wait_ms", "dts.");
        if (ackWait is 0)
        {
            throw new ConfigException("dts.ack_wait_ms", "must be at least 1");
        }

        return new DtsConfig(segment, transmission, window, ackWait);
    }

    /// <summary>drc.default_speed: an object with <c>wn</c> and <c>interleaver</c>.</summary>
    private static DrcConfig ReadDrc(JsonObject config)
    {
        JsonObject drc = Object(config, "drc");
        if (drc["default_speed"] is not JsonObject speed)
        {
            throw new ConfigException("drc.default_speed", "must be an object with wn and interleaver");
        }

        int wn = Required(speed, "wn", "drc.default_speed.", 0);
        string il = String(speed, "interleaver", "drc.default_speed.") ?? throw new MissingSettingException("drc.default_speed.interleaver");
        return new DrcConfig(new Speed(wn, InterleaverNames.Parse(il))) { Table = ReadTable(drc) };
    }

    private static readonly string[] TableKeys =
    [
        "ladder", "thresholds", "smoothing_weight", "clean_run", "rx_error_limit_ppt", "rx_ceiling_initial",
        "rx_ceiling_cap", "tx_step_down_waits", "tx_ceiling_initial", "tx_ceiling_cap",
    ];

    /// <summary>
    /// drc.table (corpus-format.md 6.1). Ladder rungs are WN 1 to 8 or 13 at Ultra Short, Short, Medium
    /// or Long (core-architecture.md 9, phase 7, design shape item 4); each list in <c>thresholds</c>
    /// is as long as the ladder. Absent from an older configuration means empty.
    /// </summary>
    private static DrcTable ReadTable(JsonObject drc)
    {
        if (drc["table"] is null)
        {
            return DrcTable.Empty;
        }

        if (drc["table"] is not JsonObject table)
        {
            throw new ConfigException("drc.table", "must be an object");
        }

        foreach (KeyValuePair<string, JsonNode?> kv in table)
        {
            if (!TableKeys.Contains(kv.Key, StringComparer.Ordinal))
            {
                throw new ConfigException("drc.table." + kv.Key, "is not a drc.table key");
            }
        }

        var ladder = new List<Speed>();
        foreach (JsonNode? e in List(table, "ladder"))
        {
            Speed rung = Rung(e, "drc.table.ladder");
            if (rung.Wn is not ((>= 1 and <= 8) or 13) || rung.Interleaver is Interleaver.VeryShort or Interleaver.VeryLong)
            {
                throw new ConfigException("drc.table.ladder", "rungs are WN 1 to 8 or 13 at ultra_short, short, medium or long");
            }

            if (ladder.Contains(rung))
            {
                throw new ConfigException("drc.table.ladder", "a rung appears twice");
            }

            ladder.Add(rung);
        }

        var thresholds = new List<DrcThresholds>();
        foreach (JsonNode? e in List(table, "thresholds"))
        {
            Speed rung = Rung(e, "drc.table.thresholds");
            if (thresholds.Exists(t => t.Rung == rung))
            {
                throw new ConfigException("drc.table.thresholds", "a rung appears twice");
            }

            JsonObject o = (JsonObject)e!;
            thresholds.Add(new DrcThresholds(rung, Tenths(o, "bulk", ladder.Count), Tenths(o, "latency", ladder.Count)));
        }

        int? weight = Int(table, "smoothing_weight", "drc.table.");
        if (weight is < 1 or > 16)
        {
            throw new ConfigException("drc.table.smoothing_weight", "must be 1 to 16");
        }

        int? limit = Int(table, "rx_error_limit_ppt", "drc.table.");
        if (limit is > 1000)
        {
            throw new ConfigException("drc.table.rx_error_limit_ppt", "must be 0 to 1000");
        }

        CapAtLeastInitial(table, "rx_ceiling_initial", "rx_ceiling_cap");
        CapAtLeastInitial(table, "tx_ceiling_initial", "tx_ceiling_cap");

        return new DrcTable(
            ladder,
            thresholds,
            weight,
            Positive(table, "clean_run"),
            limit,
            Positive(table, "rx_ceiling_initial"),
            Positive(table, "rx_ceiling_cap"),
            Positive(table, "tx_step_down_waits"),
            Positive(table, "tx_ceiling_initial"),
            Positive(table, "tx_ceiling_cap"));

        // corpus-format.md 6.1: a cap is at least its initial length.
        static void CapAtLeastInitial(JsonObject t, string initial, string cap)
        {
            if (Int(t, initial, "drc.table.") is int i && Int(t, cap, "drc.table.") is int c && c < i)
            {
                throw new ConfigException("drc.table." + cap, "must be at least " + initial);
            }
        }

        static int? Positive(JsonObject t, string key) =>
            Int(t, key, "drc.table.") is int v && v < 1 ? throw new ConfigException("drc.table." + key, "must be at least 1") : Int(t, key, "drc.table.");
    }

    private static JsonArray List(JsonObject obj, string key) =>
        obj[key] switch
        {
            null => [],
            JsonArray a => a,
            _ => throw new ConfigException("drc.table." + key, "must be a list"),
        };

    private static Speed Rung(JsonNode? e, string key)
    {
        if (e is not JsonObject o)
        {
            throw new ConfigException(key, "entries must be objects");
        }

        int wn = Required(o, "wn", key + ".", 0);
        string il = String(o, "interleaver", key + ".") ?? throw new ConfigException(key, "interleaver missing");
        return new Speed(wn, InterleaverNames.Parse(il));
    }

    /// <summary>A list of signed tenths of a dB or nulls, as long as the ladder.</summary>
    private static List<int?> Tenths(JsonObject o, string key, int length)
    {
        string where = "drc.table.thresholds." + key;
        if (o[key] is not JsonArray a || a.Count != length)
        {
            throw new ConfigException(where, "must be a list as long as the ladder");
        }

        var values = new List<int?>();
        foreach (JsonNode? n in a)
        {
            if (n is null)
            {
                values.Add(null);
                continue;
            }

            if (n.GetValueKind() != JsonValueKind.Number
                || !int.TryParse(n.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v))
            {
                throw new ConfigException(where, "entries are integers or null");
            }

            values.Add(v);
        }

        return values;
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
