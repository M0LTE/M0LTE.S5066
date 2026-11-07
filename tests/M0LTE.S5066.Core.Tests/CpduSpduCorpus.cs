using System.Text.Json;
using System.Text.Json.Nodes;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// Runs the T1 kinds <c>cpdu</c> and <c>spdu</c> (conformance docs/corpus-format.md sections 3.5
/// and 3.6) against the codecs. The conversion between the corpus JSON shape and the core's
/// records lives here, not in the core (harness-contract.md section 2).
/// </summary>
internal static class CpduSpduCorpus
{
    private static readonly HashSet<string> CaseKeys = ["id", "kind", "dir", "rows", "source", "value", "bytes", "error", "note"];

    public static string? Run(CorpusLine line, string kind)
    {
        JsonElement root = line.Root;
        if (root.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => !CaseKeys.Contains(n)) is { } key)
        {
            return CorpusResults.Unsupported($"unknown key '{key}'");
        }

        string dir = root.GetProperty("dir").GetString()!;
        byte[] bytes = Convert.FromHexString(root.GetProperty("bytes").GetString()!);
        bool cpdu = kind == "cpdu";

        if (root.TryGetProperty("error", out JsonElement error))
        {
            string actual = cpdu ? ErrorOf(CpduCodec.Decode(bytes)) : ErrorOf(SpduCodec.Decode(bytes));
            actual.Should().Be(error.GetString(), $"{line.Where}, error kind");
            return null;
        }

        JsonNode expected = JsonNode.Parse(root.GetProperty("value").GetRawText())!;
        if (dir is "decode" or "both")
        {
            JsonNode? actual;
            if (cpdu)
            {
                var r = CpduCodec.Decode(bytes);
                r.IsOk.Should().BeTrue($"{line.Where}, decode gave {r.Error}");
                actual = CpduJson.ToJson(r.Value!);
            }
            else
            {
                var r = SpduCodec.Decode(bytes);
                r.IsOk.Should().BeTrue($"{line.Where}, decode gave {r.Error}");
                actual = SpduJson.ToJson(r.Value!);
            }

            JsonNode.DeepEquals(actual, expected).Should().BeTrue($"{line.Where}: decoded {actual} but expected {expected}");
        }

        if (dir is "encode" or "both")
        {
            byte[] encoded = cpdu ? CpduCodec.Encode(CpduJson.FromJson(expected)) : SpduCodec.Encode(SpduJson.FromJson(expected));
            Convert.ToHexStringLower(encoded).Should().Be(root.GetProperty("bytes").GetString(), $"{line.Where}, encoded bytes");
        }

        return null;
    }

    private static string ErrorOf<T>(CodecResult<T> r)
        where T : class
    {
        r.IsOk.Should().BeFalse("the case expects an error");
        return r.Error switch
        {
            PduErrorKind.Truncated => "truncated",
            PduErrorKind.BadSync => "bad_sync",
            PduErrorKind.HeaderCrc => "header_crc",
            PduErrorKind.BadHeaderSize => "bad_header_size",
            PduErrorKind.BadAddressSize => "bad_address_size",
            PduErrorKind.TrailingBytes => "trailing_bytes",
            _ => "invalid_field",
        };
    }

    private static class CpduJson
    {
        public static JsonNode ToJson(CPdu p) => p switch
        {
            CpduData d => new JsonObject { ["type"] = 0, ["s_pdu"] = Convert.ToHexStringLower(d.SPdu.Span) },
            PhysicalLinkRequest r => Link(1, r.Short, r.Ed4, r.Link),
            PhysicalLinkAccepted a => Link(2, a.Short, a.Ed4, a.Link),
            PhysicalLinkRejected j => new JsonObject { ["type"] = 3, ["reason"] = j.Reason },
            PhysicalLinkBreak b => new JsonObject { ["type"] = 4, ["reason"] = b.Reason },
            PhysicalLinkBreakConfirm => new JsonObject { ["type"] = 5 },
            PhysicalLinkBreakRequest q => new JsonObject { ["type"] = 6, ["reason"] = q.Reason, ["priority"] = q.Priority },
            PhysicalLinkResume => new JsonObject { ["type"] = 7 },
            PhysicalLinkResumeAccept => new JsonObject { ["type"] = 8 },
            PhysicalLinkResumeReject => new JsonObject { ["type"] = 9 },
            AleRelink l => new JsonObject { ["type"] = 10, ["reason"] = l.Reason },
            _ => throw new InvalidOperationException(p.GetType().Name),
        };

        public static CPdu FromJson(JsonNode n)
        {
            JsonObject o = n.AsObject();
            return (int)o["type"]! switch
            {
                0 => new CpduData(Convert.FromHexString((string)o["s_pdu"]!)),
                1 => new PhysicalLinkRequest((bool)o["short"]!, (bool)o["ed4"]!, (bool)o["link"]!),
                2 => new PhysicalLinkAccepted((bool)o["short"]!, (bool)o["ed4"]!, (bool)o["link"]!),
                3 => new PhysicalLinkRejected((byte)o["reason"]!),
                4 => new PhysicalLinkBreak((byte)o["reason"]!),
                5 => new PhysicalLinkBreakConfirm(),
                6 => new PhysicalLinkBreakRequest((byte)o["reason"]!, (byte)o["priority"]!),
                var t => throw new InvalidOperationException($"type {t} is decode only"),
            };
        }

        private static JsonObject Link(int type, bool s, bool e, bool l) =>
            new() { ["type"] = type, ["short"] = s, ["ed4"] = e, ["link"] = l };
    }

    private static class SpduJson
    {
        public static JsonNode ToJson(SPdu p)
        {
            switch (p)
            {
                case SpduData d:
                {
                    var o = new JsonObject
                    {
                        ["type"] = 0,
                        ["priority"] = d.Priority,
                        ["src_sap"] = d.SrcSap,
                        ["dst_sap"] = d.DstSap,
                        ["client_confirm"] = d.ClientConfirm,
                    };
                    if (d.Ttd is { } t)
                    {
                        o["ttd"] = new JsonObject { ["day_mod16"] = t.DayMod16, ["half_seconds"] = t.HalfSeconds };
                    }

                    o["u_pdu"] = Convert.ToHexStringLower(d.UPdu.Span);
                    return o;
                }

                case SpduDeliveryConfirm c:
                    return new JsonObject
                    {
                        ["type"] = 1, ["priority"] = c.Priority, ["src_sap"] = c.SrcSap, ["dst_sap"] = c.DstSap,
                        ["u_pdu"] = Convert.ToHexStringLower(c.UPdu.Span),
                    };
                case SpduDeliveryFail f:
                    return new JsonObject
                    {
                        ["type"] = 2, ["reason"] = f.Reason, ["src_sap"] = f.SrcSap, ["dst_sap"] = f.DstSap,
                        ["u_pdu"] = Convert.ToHexStringLower(f.UPdu.Span),
                    };
                default:
                    throw new InvalidOperationException(p.GetType().Name);
            }
        }

        public static SPdu FromJson(JsonNode n)
        {
            JsonObject o = n.AsObject();
            byte u(string k) => (byte)o[k]!;
            byte[] data = Convert.FromHexString((string)o["u_pdu"]!);
            switch ((int)o["type"]!)
            {
                case 0:
                {
                    SpduTtd? ttd = o["ttd"] is JsonObject t ? new SpduTtd((byte)t["day_mod16"]!, (ushort)t["half_seconds"]!) : null;
                    return new SpduData(u("priority"), u("src_sap"), u("dst_sap"), (bool)o["client_confirm"]!, ttd, data);
                }

                case 1:
                    return new SpduDeliveryConfirm(u("priority"), u("src_sap"), u("dst_sap"), data);
                default:
                    return new SpduDeliveryFail(u("reason"), u("src_sap"), u("dst_sap"), data);
            }
        }
    }
}
