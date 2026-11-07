using System.Collections.Concurrent;
using System.Text.Json;

[assembly: AssemblyFixture(typeof(M0LTE.S5066.Core.Tests.CorpusResults))]

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// Counts corpus results by the contract's four outcomes (harness-contract.md section 1): pass,
/// fail, unsupported and skipped. xunit only knows pass, fail and skip, so this keeps the
/// distinction, prints the summary line with the corpus version when the run ends, and writes
/// corpus-results.jsonl (one {"id", "result", "reason"} per case) next to the test binary.
/// </summary>
public sealed class CorpusResults : IDisposable
{
    private static readonly ConcurrentDictionary<string, (string Area, string Result, string Reason)> Results = new();

    private const string UnsupportedPrefix = "unsupported: ";
    private const string SkippedPrefix = "skipped: ";

    /// <summary>A reason for a case with a kind, op, dir or key this harness does not know.</summary>
    public static string Unsupported(string reason) => UnsupportedPrefix + reason;

    /// <summary>A reason for a known case the harness knowingly does not run yet.</summary>
    public static string Skipped(string reason) => SkippedPrefix + reason;

    /// <summary>
    /// Runs one case. <paramref name="body"/> returns null when the case passed, or a reason from
    /// <see cref="Unsupported"/> or <see cref="Skipped"/>; anything it throws is a failure. A case
    /// is recorded as pass only when the body returned null without throwing.
    /// </summary>
    public static void Run(string id, string area, Func<string?> body)
    {
        string? reason;
        try
        {
            reason = body();
        }
        catch (Exception ex)
        {
            Results[id] = (area, "fail", ex.Message.Split('\n')[0].Trim());
            throw;
        }

        if (reason is null)
        {
            Results[id] = (area, "pass", "");
            return;
        }

        string result = reason.StartsWith(UnsupportedPrefix, StringComparison.Ordinal) ? "unsupported" : "skipped";
        Results[id] = (area, result, reason);
        Assert.Skip(reason);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Results.IsEmpty)
        {
            return;
        }

        string version;
        try
        {
            version = Corpus.Version;
        }
        catch (Exception ex) when (ex is TypeInitializationException or InvalidOperationException or IOException)
        {
            version = "unreadable";
        }

        int Count(string result) => Results.Values.Count(r => r.Result == result);
        int prngFailures = Results.Values.Count(r => r.Area == "prng" && r.Result == "fail");

        string summary = $"corpus {version}: pass {Count("pass")}, fail {Count("fail")}, "
            + $"unsupported {Count("unsupported")}, skipped {Count("skipped")}";
        Console.WriteLine(summary);
        if (prngFailures > 0)
        {
            Console.WriteLine($"PRNG FAILED ({prngFailures} cases): every T2 result is invalid");
        }

        using var writer = new StreamWriter(Path.Combine(AppContext.BaseDirectory, "corpus-results.jsonl"));
        foreach (var (id, r) in Results.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            writer.WriteLine(JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["id"] = id,
                ["result"] = r.Result,
                ["reason"] = r.Reason,
            }));
        }
    }
}
