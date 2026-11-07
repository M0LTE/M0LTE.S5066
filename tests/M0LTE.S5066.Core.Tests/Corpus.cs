using System.Text.Json;
using System.Text.Json.Nodes;

namespace M0LTE.S5066.Core.Tests;

/// <summary>One line of a corpus file: its case or scenario id, where it came from, and the parsed JSON.</summary>
internal sealed record CorpusLine(string Id, string File, int LineNumber, JsonElement Root)
{
    public string Where => $"{File}:{LineNumber}";
}

/// <summary>
/// Locates and reads the pinned conformance corpus (conformance/corpus, a git submodule), as
/// docs/harness-contract.md section 1 requires: refuse a corpus version the harness was not
/// written for, and discover files rather than hard-coding their names.
/// </summary>
internal static class Corpus
{
    /// <summary>The corpus MAJOR.MINOR this harness was written for. Before 1.0.0 a different MINOR is refused.</summary>
    public const string SupportedVersionPrefix = "0.21.";

    public static string Root { get; } = Locate();

    public static string Version { get; } = ReadVersion();

    public static IReadOnlyList<CorpusLine> T1 { get; } = ReadJsonLines(Directory.GetFiles(Path.Combine(Root, "t1"), "*.jsonl"));

    public static IReadOnlyList<CorpusLine> Prng { get; } = ReadJsonLines(Directory.GetFiles(Path.Combine(Root, "prng"), "*.jsonl"));

    /// <summary>T2 scenario files, one scenario per file; each is represented by its first line.</summary>
    public static IReadOnlyList<string> T2Files { get; } = Directory.Exists(Path.Combine(Root, "t2"))
        ? Directory.GetFiles(Path.Combine(Root, "t2"), "*.jsonl", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToArray()
        : Array.Empty<string>();

    /// <summary>The scenario ids from the header line of each T2 file.</summary>
    public static IReadOnlyList<string> T2Ids { get; } = T2Files
        .Select(f => JsonNode.Parse(File.ReadLines(f).First())!["scenario"]!.GetValue<string>()).ToArray();

    /// <summary>corpus/t2/defaults.json, the base configuration of every T2 scenario.</summary>
    public static JsonObject T2Defaults => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "t2", "defaults.json")))!.AsObject();

    private static string Locate()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "conformance", "corpus");
            if (File.Exists(Path.Combine(candidate, "VERSION")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            "conformance/corpus/VERSION not found above " + AppContext.BaseDirectory
            + "; run 'git submodule update --init'.");
    }

    private static string ReadVersion()
    {
        string version = File.ReadAllText(Path.Combine(Root, "VERSION")).Trim();
        if (!version.StartsWith(SupportedVersionPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"corpus version {version} is not supported by this harness (written for {SupportedVersionPrefix}x).");
        }

        return version;
    }

    private static List<CorpusLine> ReadJsonLines(IEnumerable<string> files)
    {
        var lines = new List<CorpusLine>();
        foreach (string path in files.Order(StringComparer.Ordinal))
        {
            string name = Path.GetRelativePath(Root, path).Replace('\\', '/');
            int n = 0;
            foreach (string text in File.ReadLines(path))
            {
                n++;
                JsonElement root = JsonDocument.Parse(text).RootElement.Clone();
                lines.Add(new CorpusLine(root.GetProperty("id").GetString()!, name, n, root));
            }
        }

        return lines;
    }
}
