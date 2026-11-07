using System.Text.Json;
using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// Runs the sis_frame and sis_stream corpus kinds (corpus-format.md section 3.7). Converts between
/// the corpus JSON shape and the core's types; the core knows nothing about JSON.
/// </summary>
internal static class SisCorpus
{
    public static string? Run(CorpusLine line)
    {
        JsonElement root = line.Root;
        string kind = root.GetProperty("kind").GetString()!;
        string dir = root.GetProperty("dir").GetString()!;
        byte[] bytes = Convert.FromHexString(root.GetProperty("bytes").GetString()!);

        if (kind == "sis_stream")
        {
            if (dir != "decode")
            {
                return CorpusResults.Unsupported($"dir '{dir}' for kind 'sis_stream'");
            }

            RunStream(line, root.GetProperty("value"), bytes);
            return null;
        }

        root.TryGetProperty("value", out JsonElement value);
        if (dir is "encode" or "both" && value.ValueKind == JsonValueKind.Object)
        {
            SisPrimitive p = FromJson(value);
            Convert.ToHexStringLower(SisCodec.Encode(p)).Should().Be(root.GetProperty("bytes").GetString(), $"{line.Where}, encode");
        }

        if (dir is "decode" or "both")
        {
            SisDecodeResult r = SisCodec.DecodeFrame(bytes);
            if (root.TryGetProperty("error", out JsonElement error))
            {
                ErrorName(r).Should().Be(error.GetString(), $"{line.Where}, error kind");
            }
            else
            {
                r.Primitive.Should().NotBeNull($"{line.Where}: got error {ErrorName(r)}");
                Same(ToJson(r.Primitive!), value, line.Where);
            }
        }

        return null;
    }

    private static void RunStream(CorpusLine line, JsonElement value, byte[] all)
    {
        var chunks = value.GetProperty("chunks").EnumerateArray().Select(c => Convert.FromHexString(c.GetString()!)).ToList();
        JsonElement events = value.GetProperty("events");

        // The result must not depend on chunking (corpus-format.md section 3.7).
        foreach (var (name, split) in new (string, List<byte[]>)[]
        {
            ("given chunks", chunks),
            ("one chunk", [all]),
            ("one byte at a time", all.Select(b => new[] { b }).ToList()),
        })
        {
            var decoder = new SisFrameDecoder();
            var got = new List<JsonNode?>();
            foreach (byte[] chunk in split)
            {
                decoder.Push(chunk);
                while (decoder.TryRead(out SisDecodeResult r))
                {
                    got.Add(r.Primitive is { } p ? ToJson(p) : new JsonObject { ["error"] = ErrorName(r) });
                }
            }

            got.Count.Should().Be(events.GetArrayLength(), $"{line.Where}, event count, {name}");
            int i = 0;
            foreach (JsonElement expected in events.EnumerateArray())
            {
                Same(got[i], expected, $"{line.Where}, event {i}, {name}");
                i++;
            }
        }
    }

    private static void Same(JsonNode? actual, JsonElement expected, string where)
    {
        JsonNode? exp = JsonNode.Parse(expected.GetRawText());
        JsonNode.DeepEquals(actual, exp).Should().BeTrue($"{where}: expected {exp?.ToJsonString()} but got {actual?.ToJsonString()}");
    }

    internal static string ErrorName(SisDecodeResult r) => r.Error switch
    {
        SisErrorKind.Truncated => "truncated",
        SisErrorKind.BadSync => "bad_sync",
        SisErrorKind.BadVersion => "bad_version",
        SisErrorKind.BadSize => "bad_size",
        SisErrorKind.UnknownType => "unknown_type",
        SisErrorKind.DeprecatedType => "deprecated_type",
        SisErrorKind.BadLength => "bad_length",
        SisErrorKind.TrailingBytes => "trailing_bytes",
        _ => "(primitive)",
    };

    private static JsonObject ServiceTypeJson(ServiceType s) => new()
    {
        ["tx_mode"] = s.TxMode,
        ["confirm"] = s.Confirm,
        ["order"] = s.Order,
        ["extended"] = s.Extended,
        ["min_retx"] = s.MinRetx,
    };

    private static JsonObject AddrJson(SisAddress a) => new() { ["address"] = a.Dotted, ["group"] = a.Group, ["size"] = a.Size };

    private static string Hex(ReadOnlyMemory<byte> m) => Convert.ToHexStringLower(m.Span);

    internal static JsonNode ToJson(SisPrimitive p)
    {
        var o = new JsonObject();
        switch (p)
        {
            case SisBindRequest b:
                o["primitive"] = "S_BIND_REQUEST";
                o["sap"] = b.Sap;
                o["service_type"] = ServiceTypeJson(b.ServiceType);
                break;
            case SisUnbindRequest:
                o["primitive"] = "S_UNBIND_REQUEST";
                break;
            case SisBindAccepted a:
                o["primitive"] = "S_BIND_ACCEPTED";
                o["sap"] = a.Sap;
                o["mtu"] = a.Mtu;
                break;
            case SisBindRejected r:
                o["primitive"] = "S_BIND_REJECTED";
                o["reason"] = r.Reason;
                break;
            case SisUnbindIndication r:
                o["primitive"] = "S_UNBIND_INDICATION";
                o["reason"] = r.Reason;
                break;
            case SisSubnetAvailability s:
                o["primitive"] = "S_SUBNET_AVAILABILITY";
                o["node_status"] = s.NodeStatus;
                o["reason"] = s.Reason;
                break;
            case SisDataFlowOn:
                o["primitive"] = "S_DATA_FLOW_ON";
                break;
            case SisDataFlowOff:
                o["primitive"] = "S_DATA_FLOW_OFF";
                break;
            case SisKeepAlive:
                o["primitive"] = "S_KEEP_ALIVE";
                break;
            case SisManagementMsgRequest m:
                o["primitive"] = "S_MANAGEMENT_MSG_REQUEST";
                o["msg_type"] = m.MsgType;
                o["body"] = Hex(m.Body);
                break;
            case SisManagementMsgIndication m:
                o["primitive"] = "S_MANAGEMENT_MSG_INDICATION";
                o["msg_type"] = m.MsgType;
                o["body"] = Hex(m.Body);
                break;
            case SisUnidataRequest u:
                o["primitive"] = "S_UNIDATA_REQUEST";
                o["priority"] = u.Priority;
                o["dest_sap"] = u.DestSap;
                o["dest_address"] = AddrJson(u.DestAddress);
                o["delivery_mode"] = ServiceTypeJson(u.DeliveryMode);
                o["ttl"] = u.Ttl;
                o["u_pdu"] = Hex(u.UPdu);
                break;
            case SisUnidataIndication u:
                o["primitive"] = "S_UNIDATA_INDICATION";
                o["priority"] = u.Priority;
                o["dest_sap"] = u.DestSap;
                o["dest_address"] = AddrJson(u.DestAddress);
                o["tx_mode"] = u.TxMode;
                o["src_sap"] = u.SrcSap;
                o["src_address"] = AddrJson(u.SrcAddress);
                o["u_pdu"] = Hex(u.UPdu);
                break;
            case SisUnidataRequestConfirm u:
                o["primitive"] = "S_UNIDATA_REQUEST_CONFIRM";
                o["dest_sap"] = u.DestSap;
                o["dest_address"] = AddrJson(u.DestAddress);
                o["u_pdu"] = Hex(u.UPdu);
                break;
            case SisUnidataRequestRejected u:
                o["primitive"] = "S_UNIDATA_REQUEST_REJECTED";
                o["reason"] = u.Reason;
                o["dest_sap"] = u.DestSap;
                o["dest_address"] = AddrJson(u.DestAddress);
                o["u_pdu"] = Hex(u.UPdu);
                break;
            default:
                throw new InvalidOperationException("unknown primitive " + p.GetType().Name);
        }

        return o;
    }

    private static ServiceType ServiceTypeFrom(JsonElement e) =>
        new(e.GetProperty("tx_mode").GetByte(), e.GetProperty("confirm").GetByte(), e.GetProperty("order").GetByte(),
            e.GetProperty("extended").GetByte(), e.GetProperty("min_retx").GetByte());

    private static SisAddress AddrFrom(JsonElement e)
    {
        SisAddress.TryParse(e.GetProperty("address").GetString()!, e.GetProperty("group").GetBoolean(), e.GetProperty("size").GetByte(), out var a)
            .Should().BeTrue("address text parses");
        return a;
    }

    private static byte[] BytesFrom(JsonElement e, string name) => Convert.FromHexString(e.GetProperty(name).GetString()!);

    internal static SisPrimitive FromJson(JsonElement v)
    {
        byte B(string name) => v.GetProperty(name).GetByte();
        return v.GetProperty("primitive").GetString() switch
        {
            "S_BIND_REQUEST" => new SisBindRequest(B("sap"), ServiceTypeFrom(v.GetProperty("service_type"))),
            "S_UNBIND_REQUEST" => new SisUnbindRequest(),
            "S_BIND_ACCEPTED" => new SisBindAccepted(B("sap"), v.GetProperty("mtu").GetUInt16()),
            "S_BIND_REJECTED" => new SisBindRejected(B("reason")),
            "S_UNBIND_INDICATION" => new SisUnbindIndication(B("reason")),
            "S_SUBNET_AVAILABILITY" => new SisSubnetAvailability(B("node_status"), B("reason")),
            "S_DATA_FLOW_ON" => new SisDataFlowOn(),
            "S_DATA_FLOW_OFF" => new SisDataFlowOff(),
            "S_KEEP_ALIVE" => new SisKeepAlive(),
            "S_MANAGEMENT_MSG_REQUEST" => new SisManagementMsgRequest(B("msg_type"), BytesFrom(v, "body")),
            "S_MANAGEMENT_MSG_INDICATION" => new SisManagementMsgIndication(B("msg_type"), BytesFrom(v, "body")),
            "S_UNIDATA_REQUEST" => new SisUnidataRequest(B("priority"), B("dest_sap"), AddrFrom(v.GetProperty("dest_address")),
                ServiceTypeFrom(v.GetProperty("delivery_mode")), v.GetProperty("ttl").GetUInt32(), BytesFrom(v, "u_pdu")),
            "S_UNIDATA_INDICATION" => new SisUnidataIndication(B("priority"), B("dest_sap"), AddrFrom(v.GetProperty("dest_address")),
                B("tx_mode"), B("src_sap"), AddrFrom(v.GetProperty("src_address")), BytesFrom(v, "u_pdu")),
            "S_UNIDATA_REQUEST_CONFIRM" => new SisUnidataRequestConfirm(B("dest_sap"), AddrFrom(v.GetProperty("dest_address")), BytesFrom(v, "u_pdu")),
            "S_UNIDATA_REQUEST_REJECTED" => new SisUnidataRequestRejected(B("reason"), B("dest_sap"), AddrFrom(v.GetProperty("dest_address")), BytesFrom(v, "u_pdu")),
            var other => throw new InvalidOperationException("unknown primitive " + other),
        };
    }
}
