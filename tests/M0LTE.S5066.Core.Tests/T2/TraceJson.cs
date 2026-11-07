using System.Text.Json;
using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Core.Tests.T2;

/// <summary>A scenario uses an input, output or key this harness does not know (harness-contract.md 1).</summary>
internal sealed class UnsupportedTraceException(string message) : Exception(message);

/// <summary>
/// The JSON shapes of corpus-format.md 4.2 and 4.3, to and from the records of
/// core-architecture.md section 2. The core never sees JSON.
/// </summary>
internal static class TraceJson
{
    private static readonly Dictionary<string, (int Class, string[] Keys)> Inputs = new(StringComparer.Ordinal)
    {
        ["config"] = (0, ["set"]),
        ["timer_expired"] = (1, ["name"]),
        ["burst_started"] = (2, []),
        ["bytes_received"] = (2, ["bytes"]),
        ["burst_ended"] = (2, ["reason", "wn", "interleaver", "k", "snr_tenth_db", "blocks"]),
        ["transmit_done"] = (2, []),
        ["channel_busy"] = (2, ["busy"]),
        ["sis_connect"] = (3, ["conn"]),
        ["sis_bytes"] = (3, ["conn", "bytes"]),
        ["sis_disconnect"] = (3, ["conn"]),
    };

    private static readonly Dictionary<string, string[]> Outputs = new(StringComparer.Ordinal)
    {
        ["transmit"] = ["bytes", "wn", "interleaver", "k", "eom"],
        ["terminate_receive"] = [],
        ["set_max_input_blocks"] = ["n"],
        ["timer_start"] = ["name", "ms"],
        ["timer_cancel"] = ["name"],
        ["sis_bytes"] = ["conn", "bytes"],
        ["sis_close"] = ["conn"],
        ["link_state"] = ["peer", "state"],
    };

    /// <summary>The input class of corpus-format.md 4.5; throws for an unknown input or key.</summary>
    public static int InputClass(JsonObject line)
    {
        string kind = Str(line, "in");
        if (!Inputs.TryGetValue(kind, out var spec))
        {
            throw new UnsupportedTraceException($"unknown input '{kind}'");
        }

        foreach (KeyValuePair<string, JsonNode?> kv in line)
        {
            if (kv.Key is not ("t" or "in") && !spec.Keys.Contains(kv.Key, StringComparer.Ordinal))
            {
                throw new UnsupportedTraceException($"unknown key '{kv.Key}' on input '{kind}'");
            }
        }

        return spec.Class;
    }

    /// <summary>Checks an expected output line names a known kind with known keys.</summary>
    public static void CheckOutput(JsonObject line)
    {
        string kind = Str(line, "out");
        if (!Outputs.TryGetValue(kind, out string[]? keys))
        {
            throw new UnsupportedTraceException($"unknown output '{kind}'");
        }

        foreach (KeyValuePair<string, JsonNode?> kv in line)
        {
            if (kv.Key != "out" && !keys.Contains(kv.Key, StringComparer.Ordinal))
            {
                throw new UnsupportedTraceException($"unknown key '{kv.Key}' on output '{kind}'");
            }
        }
    }

    /// <summary>Converts an input line, other than config (the runner merges and reads those), to its record.</summary>
    public static CoreInput ToInput(JsonObject line)
    {
        long t = Long(line, "t");
        return Str(line, "in") switch
        {
            "timer_expired" => new TimerExpired(t, TimerName.Parse(Str(line, "name"))),
            "burst_started" => new BurstStarted(t),
            "bytes_received" => new BytesReceived(t, Hex(line, "bytes")),
            "burst_ended" => new BurstEnded(t, Reason(Str(line, "reason")), Int(line, "wn"),
                InterleaverNames.Parse(Str(line, "interleaver")), Int(line, "k"),
                Snr(line), Int(line, "blocks")),
            "transmit_done" => new TransmitDone(t),
            "channel_busy" => new ChannelBusy(t, line["busy"]!.GetValue<bool>()),
            "sis_connect" => new SisConnect(t, Int(line, "conn")),
            "sis_bytes" => new SisBytes(t, Int(line, "conn"), Hex(line, "bytes")),
            "sis_disconnect" => new SisDisconnect(t, Int(line, "conn")),
            string other => throw new UnsupportedTraceException($"input '{other}' is not converted here"),
        };
    }

    /// <summary>The corpus JSON form of an output.</summary>
    public static JsonObject ToJson(CoreOutput output) => output switch
    {
        Transmit x => new JsonObject
        {
            ["out"] = "transmit", ["bytes"] = Convert.ToHexStringLower(x.Bytes.Span), ["wn"] = x.Wn,
            ["interleaver"] = InterleaverNames.ToName(x.Interleaver), ["k"] = x.K, ["eom"] = x.Eom,
        },
        TerminateReceive => new JsonObject { ["out"] = "terminate_receive" },
        SetMaxInputBlocks x => new JsonObject { ["out"] = "set_max_input_blocks", ["n"] = x.N },
        TimerStart x => new JsonObject { ["out"] = "timer_start", ["name"] = x.Name.ToTraceString(), ["ms"] = x.Ms },
        TimerCancel x => new JsonObject { ["out"] = "timer_cancel", ["name"] = x.Name.ToTraceString() },
        SisBytesOut x => new JsonObject { ["out"] = "sis_bytes", ["conn"] = x.Conn, ["bytes"] = Convert.ToHexStringLower(x.Bytes.Span) },
        SisClose x => new JsonObject { ["out"] = "sis_close", ["conn"] = x.Conn },
        LinkState x => new JsonObject { ["out"] = "link_state", ["peer"] = DpduAddress.ToDotted(x.Peer), ["state"] = LinkStateText(x.State) },
        _ => throw new InvalidOperationException($"unknown output {output.GetType().Name}"),
    };

    /// <summary>
    /// A canonical text form for comparison: byte strings lower-cased (the format compares bytes,
    /// not hex case), then serialised and re-parsed so both sides are the same kind of node.
    /// </summary>
    public static JsonNode Normalise(JsonObject line)
    {
        var copy = (JsonObject)line.DeepClone();
        if (copy["bytes"] is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            copy["bytes"] = v.GetValue<string>().ToLowerInvariant();
        }

        return JsonNode.Parse(copy.ToJsonString())!;
    }

    public static string LinkStateText(LinkStateName s) => s switch
    {
        LinkStateName.Unlinked => "unlinked",
        LinkStateName.Calling => "calling",
        LinkStateName.Linked => "linked",
        LinkStateName.Closing => "closing",
        LinkStateName.Breaking => "breaking",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String
            ? v.GetValue<string>()
            : throw new InvalidDataException($"'{key}' must be a string");

    public static long Long(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            && long.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long n)
            ? n
            : throw new InvalidDataException($"'{key}' must be a non-negative integer");

    private static int Int(JsonObject o, string key) => checked((int)Long(o, key));

    private static int? Snr(JsonObject o) =>
        o["snr_tenth_db"] is not JsonValue v ? null
        : v.GetValueKind() == JsonValueKind.Number
            && int.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int n)
            ? n
            : throw new InvalidDataException("'snr_tenth_db' must be an integer or null");

    private static byte[] Hex(JsonObject o, string key) => Convert.FromHexString(Str(o, key));

    private static BurstEndReason Reason(string s) => s switch
    {
        "eom" => BurstEndReason.Eom,
        "signal_lost" => BurstEndReason.SignalLost,
        "terminated" => BurstEndReason.Terminated,
        _ => throw new InvalidDataException($"unknown burst_ended reason '{s}'"),
    };
}
