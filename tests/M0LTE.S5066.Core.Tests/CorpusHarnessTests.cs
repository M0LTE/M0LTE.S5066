using System.Text.Json;
using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// Runs the shared conformance corpus against the core, following the conformance repository's
/// docs/harness-contract.md. One xunit row per case id. A kind, direction, operation or key the
/// harness does not know is reported as unsupported, and a known case it does not run yet as
/// skipped; both are xunit skips with a reason, never passes, and <see cref="CorpusResults"/>
/// counts them separately in the summary.
/// </summary>
public class CorpusHarnessTests
{
    // Keys from corpus-format.md section 3 and prng.md section 5. Anything else is unsupported.
    private static readonly HashSet<string> T1Keys =
        ["id", "kind", "dir", "rows", "source", "value", "bytes", "error", "peer_edition", "note"];

    private static readonly HashSet<string> CrcKeys = ["id", "kind", "dir", "rows", "source", "value", "bytes", "note"];

    private static readonly HashSet<string> CrcValueKeys = ["data", "register"];

    private static readonly HashSet<string> NextU64Keys = ["id", "seed", "op", "results"];

    private static readonly HashSet<string> UniformKeys = ["id", "seed", "op", "n", "results", "state_after"];

    private static readonly HashSet<string> UniformResultKeys = ["value", "draws"];

    public static IEnumerable<TheoryDataRow<string>> T1Cases() =>
        Corpus.T1.Select(c => new TheoryDataRow<string>(c.Id) { TestDisplayName = c.Id });

    public static IEnumerable<TheoryDataRow<string>> PrngCases() =>
        Corpus.Prng.Select(c => new TheoryDataRow<string>(c.Id) { TestDisplayName = c.Id });

    public static IEnumerable<TheoryDataRow<string>> T2Cases()
    {
        // xunit rejects a theory with no rows, so an empty T2 directory shows as one skipped row.
        var files = Corpus.T2Files.Select(f => Path.GetRelativePath(Corpus.Root, f).Replace('\\', '/')).ToList();
        if (files.Count == 0)
        {
            files.Add("t2/none");
        }

        return files.Select(f => new TheoryDataRow<string>(f) { TestDisplayName = f });
    }

    [Fact]
    public void Corpus_is_discovered_at_a_supported_version()
    {
        Corpus.Version.Should().StartWith(Corpus.SupportedVersionPrefix);
        Corpus.T1.Should().NotBeEmpty();
        Corpus.Prng.Should().NotBeEmpty();
        Corpus.T1.Select(c => c.Id).Concat(Corpus.Prng.Select(c => c.Id)).Concat(Corpus.T2Ids)
            .Should().OnlyHaveUniqueItems("case ids are unique across the corpus");
    }

    [Theory]
    [MemberData(nameof(PrngCases))]
    public void Prng(string id) => CorpusResults.Run(id, "prng", () => RunPrng(Corpus.Prng.Single(c => c.Id == id)));

    [Theory]
    [MemberData(nameof(T1Cases))]
    public void T1(string id) => CorpusResults.Run(id, "t1", () => RunT1(Corpus.T1.Single(c => c.Id == id)));

    [Theory]
    [MemberData(nameof(T2Cases))]
    public void T2(string scenario)
    {
        if (scenario == "t2/none")
        {
            CorpusResults.Run(scenario, "t2", () => CorpusResults.Skipped("no T2 scenarios in the corpus"));
            return;
        }

        string path = Path.Combine(Corpus.Root, scenario);
        string[] lines = File.ReadAllLines(path);
        string id = JsonNode.Parse(lines[0])!["scenario"]!.GetValue<string>();
        CorpusResults.Run(id, "t2", () =>
        {
            if (AwaitingSis.Applies(scenario))
            {
                return CorpusResults.Skipped("needs the P3-1 SIS to queue data; CsmaScenarioTests runs it with a stand-in SIS until then");
            }

            T2Result result = T2Runner.Run(scenario, lines, Corpus.T2Defaults);
            return result.Unsupported is null ? null : CorpusResults.Unsupported(result.Unsupported);
        });
    }

    private static string? RunPrng(CorpusLine line)
    {
        JsonElement root = line.Root;
        string op = root.GetProperty("op").GetString()!;
        switch (op)
        {
            case "next_u64":
            {
                if (UnknownKey(root, NextU64Keys) is { } key)
                {
                    return CorpusResults.Unsupported($"unknown key '{key}'");
                }

                var rng = new SplitMix64(Hex64(root.GetProperty("seed").GetString()!));
                int i = 0;
                foreach (JsonElement expected in root.GetProperty("results").EnumerateArray())
                {
                    Hex(rng.NextUInt64()).Should().Be(expected.GetString(), $"{line.Where}, draw {i++}");
                }

                return null;
            }

            case "uniform":
            {
                if (UnknownKey(root, UniformKeys) is { } key)
                {
                    return CorpusResults.Unsupported($"unknown key '{key}'");
                }

                foreach (JsonElement r in root.GetProperty("results").EnumerateArray())
                {
                    if (UnknownKey(r, UniformResultKeys) is { } rkey)
                    {
                        return CorpusResults.Unsupported($"unknown key 'results.{rkey}'");
                    }
                }

                var rng = new SplitMix64(Hex64(root.GetProperty("seed").GetString()!));
                ulong n = Hex64(root.GetProperty("n").GetString()!);
                int call = 0;
                foreach (JsonElement expected in root.GetProperty("results").EnumerateArray())
                {
                    long before = rng.Draws;
                    ulong value = rng.Uniform(n);
                    Hex(value).Should().Be(expected.GetProperty("value").GetString(), $"{line.Where}, call {call}");
                    (rng.Draws - before).Should().Be(expected.GetProperty("draws").GetInt32(), $"{line.Where}, call {call} draw count");
                    call++;
                }

                Hex(rng.State).Should().Be(root.GetProperty("state_after").GetString(), $"{line.Where}, state_after");
                return null;
            }

            default:
                return CorpusResults.Unsupported($"unknown PRNG op '{op}'");
        }
    }

    private static string? RunT1(CorpusLine line)
    {
        JsonElement root = line.Root;
        string kind = root.GetProperty("kind").GetString()!;
        string dir = root.GetProperty("dir").GetString()!;

        switch (kind)
        {
            case "crc16":
            case "crc32":
                // These kinds are encode only (corpus-format.md section 3.2). Any other dir, or an
                // error case, is something this harness does not know how to run.
                if (dir != "encode")
                {
                    return CorpusResults.Unsupported($"dir '{dir}' for kind '{kind}'");
                }

                if (UnknownKey(root, CrcKeys) is { } key)
                {
                    return CorpusResults.Unsupported($"unknown key '{key}'");
                }

                if (UnknownKey(root.GetProperty("value"), CrcValueKeys) is { } vkey)
                {
                    return CorpusResults.Unsupported($"unknown key 'value.{vkey}'");
                }

                RunCrc(line, kind);
                return null;

            case "dpdu":
            case "eow":
            case "address":
            case "speed_interleaver":
                if (UnknownKey(root, T1Keys) is { } dkey)
                {
                    return CorpusResults.Unsupported($"unknown key '{dkey}'");
                }

                return RunCodec(line, kind, dir);

            case "sis_frame":
            case "sis_stream":
                return UnknownKey(root, T1Keys) is { } skey
                    ? CorpusResults.Unsupported($"unknown key '{skey}'")
                    : SisCorpus.Run(line);

            case "cpdu":
            case "spdu":
                return CpduSpduCorpus.Run(line, kind);

            default:
                return CorpusResults.Unsupported($"unknown T1 kind '{kind}'");
        }
    }

    private static string? RunCodec(CorpusLine line, string kind, string dir)
    {
        JsonElement root = line.Root;
        byte[] bytes = Convert.FromHexString(root.GetProperty("bytes").GetString()!);
        bool hasError = root.TryGetProperty("error", out JsonElement errorEl);
        JsonNode? expected = root.TryGetProperty("value", out JsonElement valueEl) ? JsonNode.Parse(valueEl.GetRawText()) : null;
        bool decode = dir is "decode" or "both";
        bool encode = dir is "encode" or "both";
        if (kind == "eow" && hasError)
        {
            return CorpusResults.Unsupported("error cases for kind 'eow'");
        }

        if (decode)
        {
            (JsonNode? actual, string? error) = DecodeToJson(kind, root, bytes);
            if (hasError)
            {
                error.Should().Be(errorEl.GetString(), $"{line.Where}, error kind");
            }
            else
            {
                error.Should().BeNull($"{line.Where}, decode error");
                JsonNode.DeepEquals(actual, expected).Should().BeTrue($"{line.Where}: decoded {actual?.ToJsonString()} but expected {expected?.ToJsonString()}");
            }
        }

        if (encode)
        {
            Convert.ToHexStringLower(EncodeFromJson(kind, expected!)).Should().Be(root.GetProperty("bytes").GetString(), $"{line.Where}, encoded bytes");
        }

        return null;
    }

    private static (JsonNode? Value, string? Error) DecodeToJson(string kind, JsonElement root, byte[] bytes)
    {
        switch (kind)
        {
            case "dpdu":
            {
                var peer = DpduCorpusAdapter.Peer(root.GetProperty("peer_edition").GetString()!);
                DecodeResult<Dpdu> r = DpduCodec.Decode(bytes, peer);
                return r.IsSuccess ? (DpduCorpusAdapter.ToJson(r.Value!), null) : (null, DpduCorpusAdapter.ErrorName(r.Error!.Value));
            }

            case "address":
            {
                if (!DpduAddress.TryRead(bytes, out uint dst, out uint src))
                {
                    return (null, "bad_address_size");
                }

                return (new JsonObject
                {
                    ["address_size"] = bytes.Length,
                    ["dst"] = DpduAddress.ToDotted(dst),
                    ["src"] = DpduAddress.ToDotted(src),
                }, null);
            }

            case "speed_interleaver":
            {
                if (bytes.Length != 1)
                {
                    return (null, bytes.Length < 1 ? "truncated" : "trailing_bytes");
                }

                SpeedInterleaver s = SpeedInterleaver.FromByte(bytes[0]);
                return (new JsonObject { ["interleaver"] = s.Interleaver, ["speed"] = s.Speed }, null);
            }

            default: // eow: 2 bytes, type then contents
                if (bytes.Length != 2)
                {
                    return (null, bytes.Length < 2 ? "truncated" : "trailing_bytes");
                }

                return bytes[0] > 15
                    ? (null, "invalid_field")
                    : (DpduCorpusAdapter.EowToJson(EowCodec.Decode((bytes[0] << 8) | bytes[1])), null);
        }
    }

    private static byte[] EncodeFromJson(string kind, JsonNode value)
    {
        switch (kind)
        {
            case "dpdu":
                return DpduCodec.Encode(DpduCorpusAdapter.FromJson(value));
            case "address":
            {
                int m = value["address_size"]!.GetValue<int>();
                DpduAddress.TryParse(value["dst"]!.GetValue<string>(), out uint dst).Should().BeTrue();
                DpduAddress.TryParse(value["src"]!.GetValue<string>(), out uint src).Should().BeTrue();
                var field = new byte[m];
                DpduAddress.Write(m, dst, src, field);
                return field;
            }

            case "speed_interleaver":
                return [new SpeedInterleaver(value["interleaver"]!.GetValue<int>(), value["speed"]!.GetValue<int>()).ToByte()];
            default:
            {
                int v = EowCodec.Encode(DpduCorpusAdapter.EowFromJson(value));
                return [(byte)(v >> 8), (byte)v];
            }
        }
    }

    private static void RunCrc(CorpusLine line, string kind)
    {
        JsonElement value = line.Root.GetProperty("value");
        byte[] data = Convert.FromHexString(value.GetProperty("data").GetString()!);
        ulong register = value.GetProperty("register").GetUInt64();
        string expectedBytes = line.Root.GetProperty("bytes").GetString()!;

        if (kind == "crc16")
        {
            ushort crc = Crc16S5066.Compute(data);
            HexRegister(crc, 4).Should().Be(HexRegister(register, 4), $"{line.Where}, register");
            Span<byte> wire = stackalloc byte[2];
            Crc16S5066.Write(crc, wire);
            Convert.ToHexStringLower(wire).Should().Be(expectedBytes, $"{line.Where}, bytes");
        }
        else
        {
            uint crc = Crc32S5066.Compute(data);
            HexRegister(crc, 8).Should().Be(HexRegister(register, 8), $"{line.Where}, register");
            Span<byte> wire = stackalloc byte[4];
            Crc32S5066.Write(crc, wire);
            Convert.ToHexStringLower(wire).Should().Be(expectedBytes, $"{line.Where}, bytes");
        }
    }

    private static string? UnknownKey(JsonElement obj, HashSet<string> known) =>
        obj.EnumerateObject().Select(p => p.Name).FirstOrDefault(name => !known.Contains(name));

    private static ulong Hex64(string s) => Convert.ToUInt64(s, 16);

    private static string Hex(ulong v) => v.ToString("x16");

    private static string HexRegister(ulong v, int digits) => "0x" + v.ToString("x" + digits);
}
