using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Core.Tests.T2;

/// <summary>
/// Writes T2 scenario lines from field values (core-architecture.md section 8, authoring). Expected
/// bytes are encoded with the phase 1 codecs, which are byte-exact against T1, so a scenario's
/// D_PDUs, S_primitives and S_PDUs need no hand-written CRCs. Expected outputs come from the matrix
/// rows; this never records what the implementation does.
/// </summary>
internal sealed class ScenarioBuilder
{
    private readonly List<JsonObject> _lines = [];

    public ScenarioBuilder(string id, string[] rows, string source, string description, JsonObject config, string? note = null)
    {
        var header = new JsonObject
        {
            ["scenario"] = id,
            ["rows"] = new JsonArray(rows.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
            ["source"] = source,
            ["description"] = description,
            ["config"] = config,
        };
        if (note is not null)
        {
            header["note"] = note;
        }

        _lines.Add(header);
    }

    /// <summary>Replaces the header's note, for a note that states times the timeline works out.</summary>
    public ScenarioBuilder SetNote(string note)
    {
        _lines[0]["note"] = note;
        return this;
    }

    /// <summary>The encoded hex of a D_PDU.</summary>
    public static string DpduHex(Dpdu pdu) => Convert.ToHexStringLower(DpduCodec.Encode(pdu));

    /// <summary>The encoded hex of an Annex S primitive frame.</summary>
    public static string SisHex(SisPrimitive primitive) => Convert.ToHexStringLower(SisCodec.Encode(primitive));

    /// <summary>The encoded hex of an S_PDU.</summary>
    public static string SpduHex(SPdu pdu) => Convert.ToHexStringLower(SpduCodec.Encode(pdu));

    public ScenarioBuilder In(long t, string kind, JsonObject? keys = null)
    {
        var line = new JsonObject { ["t"] = t, ["in"] = kind };
        foreach (KeyValuePair<string, JsonNode?> kv in keys ?? [])
        {
            line[kv.Key] = kv.Value?.DeepClone();
        }

        _lines.Add(line);
        return this;
    }

    public ScenarioBuilder Out(string kind, JsonObject? keys = null)
    {
        var line = new JsonObject { ["out"] = kind };
        foreach (KeyValuePair<string, JsonNode?> kv in keys ?? [])
        {
            line[kv.Key] = kv.Value?.DeepClone();
        }

        _lines.Add(line);
        return this;
    }

    public ScenarioBuilder Config(long t, JsonObject set) => In(t, "config", new JsonObject { ["set"] = set });

    public ScenarioBuilder TimerExpired(long t, string name) => In(t, "timer_expired", new JsonObject { ["name"] = name });

    public ScenarioBuilder BurstStarted(long t, int? blockMs = null) =>
        In(t, "burst_started", blockMs is int ms ? new JsonObject { ["block_ms"] = ms } : null);

    public ScenarioBuilder BytesReceived(long t, string hex) => In(t, "bytes_received", new JsonObject { ["bytes"] = hex });

    public ScenarioBuilder BurstEnded(long t, string reason = "eom", int wn = 2, Interleaver il = Interleaver.Long, int k = 7, int? snrTenthDb = null, int blocks = 1) =>
        In(t, "burst_ended", new JsonObject
        {
            ["reason"] = reason, ["wn"] = wn, ["interleaver"] = InterleaverNames.ToName(il), ["k"] = k,
            ["snr_tenth_db"] = snrTenthDb, ["blocks"] = blocks,
        });

    public ScenarioBuilder TransmitDone(long t) => In(t, "transmit_done");

    public ScenarioBuilder ChannelBusy(long t, bool busy) => In(t, "channel_busy", new JsonObject { ["busy"] = busy });

    public ScenarioBuilder SisConnect(long t, int conn) => In(t, "sis_connect", new JsonObject { ["conn"] = conn });

    public ScenarioBuilder SisBytes(long t, int conn, string hex) => In(t, "sis_bytes", new JsonObject { ["conn"] = conn, ["bytes"] = hex });

    public ScenarioBuilder SisDisconnect(long t, int conn) => In(t, "sis_disconnect", new JsonObject { ["conn"] = conn });

    public ScenarioBuilder ExpectTimerStart(string name, long ms) => Out("timer_start", new JsonObject { ["name"] = name, ["ms"] = ms });

    public ScenarioBuilder ExpectTimerCancel(string name) => Out("timer_cancel", new JsonObject { ["name"] = name });

    public ScenarioBuilder ExpectTransmit(string hex, int wn, Interleaver il, int k = 7, bool eom = true) =>
        Out("transmit", new JsonObject { ["bytes"] = hex, ["wn"] = wn, ["interleaver"] = InterleaverNames.ToName(il), ["k"] = k, ["eom"] = eom });

    public ScenarioBuilder ExpectSisBytes(int conn, string hex) => Out("sis_bytes", new JsonObject { ["conn"] = conn, ["bytes"] = hex });

    public ScenarioBuilder ExpectSisClose(int conn) => Out("sis_close", new JsonObject { ["conn"] = conn });

    public ScenarioBuilder ExpectLinkState(string peer, LinkStateName state) =>
        Out("link_state", new JsonObject { ["peer"] = peer, ["state"] = TraceJson.LinkStateText(state) });

    /// <summary>The scenario as JSON Lines text, one object per line, ending with a newline.</summary>
    public IReadOnlyList<string> Lines() => _lines.Select(l => l.ToJsonString()).ToList();
}
