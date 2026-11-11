using System.Text.Json;
using System.Text.Json.Nodes;
using M0LTE.S5066.Cftp;
using M0LTE.S5066.Core.Tests;

namespace M0LTE.S5066.Cftp.Tests;

/// <summary>
/// Runs the corpus's CFTP T1 kinds (conformance docs/corpus-format.md 3.10) against the CFTP
/// codecs. The core's harness skips these kinds and points here.
/// </summary>
public class CftpCorpusTests
{
    private static readonly HashSet<string> Kinds = ["rcop1", "bftp1", "cftp_mail", "cftp_recipients", "cftp_gzip"];

    private static readonly HashSet<string> Keys = ["id", "kind", "dir", "rows", "source", "value", "bytes", "error", "note"];

    public static IEnumerable<TheoryDataRow<string>> Cases() =>
        Corpus.T1.Where(c => Kinds.Contains(c.Root.GetProperty("kind").GetString()!))
            .Select(c => new TheoryDataRow<string>(c.Id) { TestDisplayName = c.Id });

    [Fact]
    public void Every_cftp_kind_has_cases()
    {
        Corpus.T1.Select(c => c.Root.GetProperty("kind").GetString()!).Where(Kinds.Contains).Distinct()
            .Should().BeEquivalentTo(Kinds);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case(string id)
    {
        CorpusLine line = Corpus.T1.Single(c => c.Id == id);
        JsonElement root = line.Root;
        foreach (JsonProperty p in root.EnumerateObject())
        {
            Keys.Should().Contain(p.Name, $"{line.Where}: unknown key");
        }

        string kind = root.GetProperty("kind").GetString()!;
        string dir = root.GetProperty("dir").GetString()!;
        byte[] bytes = Convert.FromHexString(root.GetProperty("bytes").GetString()!);
        bool hasError = root.TryGetProperty("error", out JsonElement errorEl);

        if (dir is "decode" or "both")
        {
            (JsonNode? value, CftpErrorKind? error) = Decode(kind, bytes);
            if (hasError)
            {
                error.Should().NotBeNull($"{line.Where}: expected error {errorEl.GetString()}");
                Name(error!.Value).Should().Be(errorEl.GetString(), $"{line.Where}, error kind");
            }
            else
            {
                error.Should().BeNull($"{line.Where}: decode failed");
                JsonNode.DeepEquals(value, JsonNode.Parse(root.GetProperty("value").GetRawText())).Should()
                    .BeTrue($"{line.Where}: decoded {value?.ToJsonString()}");
            }
        }

        if (dir is "encode" or "both")
        {
            Convert.ToHexStringLower(Encode(kind, JsonNode.Parse(root.GetProperty("value").GetRawText())!))
                .Should().Be(root.GetProperty("bytes").GetString(), $"{line.Where}, encoded bytes");
        }
    }

    private static string Name(CftpErrorKind e) => e switch
    {
        CftpErrorKind.Truncated => "truncated",
        CftpErrorKind.BadSync => "bad_sync",
        CftpErrorKind.TrailingBytes => "trailing_bytes",
        CftpErrorKind.InvalidField => "invalid_field",
        CftpErrorKind.BadCompression => "bad_compression",
        _ => e.ToString(),
    };

    private static string Hex(ReadOnlyMemory<byte> b) => Convert.ToHexStringLower(b.Span);

    private static (JsonNode? Value, CftpErrorKind? Error) Decode(string kind, byte[] b)
    {
        CftpErrorKind e;
        switch (kind)
        {
            case "rcop1":
                return Rcop1Pdu.TryDecode(b, out Rcop1Pdu? r, out e)
                    ? (new JsonObject { ["connection_id"] = r!.ConnectionId, ["u_pdu_id"] = r.UPduId, ["segment"] = r.Segment, ["data"] = Hex(r.Data) }, null)
                    : (null, e);
            case "bftp1":
                return Bftp1Pdu.TryDecode(b, out Bftp1Pdu? f, out e)
                    ? (new JsonObject { ["file_name"] = Hex(f!.FileName), ["file_data"] = Hex(f.FileData) }, null)
                    : (null, e);
            case "cftp_mail":
                return CftpMailFile.TryDecode(b, out CftpMailFile? m, out e)
                    ? (new JsonObject { ["message_id"] = m!.MessageId, ["recipient_list"] = m.RecipientList, ["message"] = Hex(m.Message) }, null)
                    : (null, e);
            case "cftp_recipients":
                return CftpRecipients.TryDecode(System.Text.Encoding.Latin1.GetString(b), out CftpRecipients? rc, out e)
                    ? (new JsonObject { ["return_path"] = rc!.ReturnPath, ["recipients"] = new JsonArray([.. rc.Recipients.Select(x => (JsonNode)x)]) }, null)
                    : (null, e);
            case "cftp_gzip":
                return CftpCompression.TryDecompress(b, out byte[]? d, out e)
                    ? (new JsonObject { ["data"] = Convert.ToHexStringLower(d!) }, null)
                    : (null, e);
            default:
                throw new InvalidOperationException(kind);
        }
    }

    private static byte[] Encode(string kind, JsonNode v)
    {
        byte[] H(string k) => Convert.FromHexString(v[k]!.GetValue<string>());
        return kind switch
        {
            "rcop1" => new Rcop1Pdu(v["connection_id"]!.GetValue<byte>(), v["u_pdu_id"]!.GetValue<byte>(), v["segment"]!.GetValue<ushort>(), H("data")).Encode(),
            "bftp1" => new Bftp1Pdu(H("file_name"), H("file_data")).Encode(),
            "cftp_mail" => new CftpMailFile(v["message_id"]!.GetValue<string>(), v["recipient_list"]!.GetValue<string>(), H("message")).Encode(),
            "cftp_recipients" => System.Text.Encoding.Latin1.GetBytes(new CftpRecipients(v["return_path"]!.GetValue<string>(),
                [.. v["recipients"]!.AsArray().Select(x => x!.GetValue<string>())]).Encode()),
            _ => throw new InvalidOperationException($"{kind} is decode only"),
        };
    }
}
