using System.Text.Json;
using Xunit.Sdk;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// Runs the shared conformance corpus against the core, following the conformance repository's
/// docs/harness-contract.md. One xunit row per case id. A kind, direction or operation the harness
/// does not support yet is reported as skipped with a reason, never as passed.
/// </summary>
public class CorpusHarnessTests
{
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
        Corpus.T1.Select(c => c.Id).Concat(Corpus.Prng.Select(c => c.Id))
            .Should().OnlyHaveUniqueItems("case ids are unique across the corpus");
    }

    [Theory]
    [MemberData(nameof(PrngCases))]
    public void Prng(string id)
    {
        CorpusLine line = Corpus.Prng.Single(c => c.Id == id);
        JsonElement root = line.Root;
        ulong seed = Hex64(root.GetProperty("seed").GetString()!);
        string op = root.GetProperty("op").GetString()!;
        var rng = new SplitMix64(seed);

        switch (op)
        {
            case "next_u64":
                int i = 0;
                foreach (JsonElement expected in root.GetProperty("results").EnumerateArray())
                {
                    Hex(rng.NextUInt64()).Should().Be(expected.GetString(), $"{line.Where}, draw {i++}");
                }

                break;

            case "uniform":
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
                break;

            default:
                Assert.Skip($"unsupported PRNG op '{op}'");
                break;
        }
    }

    [Theory]
    [MemberData(nameof(T1Cases))]
    public void T1(string id)
    {
        CorpusLine line = Corpus.T1.Single(c => c.Id == id);
        JsonElement root = line.Root;
        string kind = root.GetProperty("kind").GetString()!;
        string dir = root.GetProperty("dir").GetString()!;

        switch (kind)
        {
            case "crc16":
            case "crc32":
                if (dir is not ("encode" or "both"))
                {
                    Assert.Skip($"unsupported dir '{dir}' for kind '{kind}'");
                }

                RunCrc(line, kind);
                break;

            case "dpdu":
                Assert.Skip("D_PDU codec not implemented yet (phase 1b)");
                break;

            default:
                Assert.Skip($"unsupported T1 kind '{kind}'");
                break;
        }
    }

    [Theory]
    [MemberData(nameof(T2Cases))]
    public void T2(string scenario)
    {
        Assert.Skip($"T2 scenario runner not implemented yet (phase 3): {scenario}");
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
            ((ulong)crc).Should().Be(register, $"{line.Where}, register");
            Span<byte> wire = stackalloc byte[2];
            Crc16S5066.Write(crc, wire);
            Convert.ToHexStringLower(wire).Should().Be(expectedBytes, $"{line.Where}, bytes");
        }
        else
        {
            uint crc = Crc32S5066.Compute(data);
            ((ulong)crc).Should().Be(register, $"{line.Where}, register");
            Span<byte> wire = stackalloc byte[4];
            Crc32S5066.Write(crc, wire);
            Convert.ToHexStringLower(wire).Should().Be(expectedBytes, $"{line.Where}, bytes");
        }
    }

    private static ulong Hex64(string s) => Convert.ToUInt64(s, 16);

    private static string Hex(ulong v) => v.ToString("x16");
}
