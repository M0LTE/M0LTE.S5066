using System.Globalization;
using System.Text;

namespace M0LTE.S5066.Sweep;

/// <summary>
/// Merges the per-run JSON files into the small aggregates that come back from a spot instance:
/// <c>curve-&lt;channel&gt;.tsv</c> per channel (one line per set SNR and configuration, over the seeds),
/// <c>baseline.tsv</c> (per channel, the fixed rung with the highest goodput summed over the sweep,
/// open question 14, and each point's best fixed rung), <c>turnaround.tsv</c> and <c>overshoot.tsv</c>
/// (the gap between bursts and the EOT overshoot per channel and rung, all runs pooled), and
/// <c>merge.json</c> (counts, missing runs, failures).
/// </summary>
internal static class Merge
{
    public static int Run(SweepSettings settings, string outDir, Action<string> log)
    {
        string runs = Path.Combine(outDir, "runs");
        string aggDir = Path.Combine(outDir, "aggregate");
        Directory.CreateDirectory(aggDir);
        List<RunSettings> jobs = settings.Jobs();
        var results = new List<RunResult>();
        var missing = new List<string>();
        foreach (RunSettings j in jobs)
        {
            string p = Path.Combine(runs, j.Id + ".json");
            if (File.Exists(p))
            {
                results.Add(RunResult.FromJson(File.ReadAllText(p)));
            }
            else
            {
                missing.Add(j.Id);
            }
        }

        string[] failed = Directory.Exists(runs) ? [.. Directory.GetFiles(runs, "*.failed").Select(Path.GetFileNameWithoutExtension).OfType<string>()] : [];
        Write(aggDir, results);
        string summary = string.Create(CultureInfo.InvariantCulture,
            $"{{\n  \"planned\": {jobs.Count},\n  \"merged\": {results.Count},\n  \"complete\": {(missing.Count == 0 ? "true" : "false")},\n" +
            $"  \"missing\": [{string.Join(", ", missing.Select(m => "\"" + m + "\""))}],\n  \"failed\": [{string.Join(", ", failed.Select(m => "\"" + m + "\""))}]\n}}\n");
        File.WriteAllText(Path.Combine(aggDir, "merge.json"), summary);
        File.Copy(Path.Combine(outDir, "plan.json"), Path.Combine(aggDir, "plan.json"), overwrite: true);
        log($"[merge] {results.Count} of {jobs.Count} runs merged into {aggDir}{(missing.Count > 0 ? $", {missing.Count} missing" : "")}{(failed.Length > 0 ? $", {failed.Length} failed" : "")}");
        return missing.Count == 0 && failed.Length == 0 ? 0 : 1;
    }

    internal static void Write(string aggDir, IReadOnlyList<RunResult> results)
    {
        var inv = CultureInfo.InvariantCulture;
        foreach (IGrouping<string, RunResult> ch in results.GroupBy(r => r.Channel).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var sb = new StringBuilder("channel\tset_snr_tenth_db\tconfig\tseeds\tgoodput_mean_bps\tgoodput_min_bps\tgoodput_max_bps\tretransmission_pct\tlinks_lost\tchat_delivered\tchat_sent\tchat_latency_median_ms\ttransmissions_per_rung\n");
            foreach (IGrouping<(int Snr, string Config), RunResult> pt in ch.GroupBy(r => (r.SnrTenthDb, r.Config)).OrderBy(g => g.Key.SnrTenthDb).ThenBy(g => g.Key.Config, StringComparer.Ordinal))
            {
                var g = pt.ToList();
                var rungs = new SortedDictionary<string, long>(StringComparer.Ordinal);
                var chat = new Dist();
                foreach (RunResult r in g)
                {
                    foreach (NodeStats n in r.Nodes.Values)
                    {
                        foreach ((string rung, RungStats s) in n.Rungs)
                        {
                            rungs[rung] = rungs.GetValueOrDefault(rung) + s.Count;
                        }
                    }

                    chat.Merge(r.Chat.LatencyMs);
                }

                long data = g.Sum(r => r.Nodes.Values.Sum(n => n.DataDpdus));
                long retx = g.Sum(r => r.Nodes.Values.Sum(n => n.RetransmittedDpdus));
                sb.Append(inv, $"{ch.Key}\t{pt.Key.Snr}\t{pt.Key.Config}\t{string.Join(",", g.Select(r => r.Seed).Order())}\t")
                  .Append(inv, $"{g.Average(r => r.GoodputBps):F1}\t{g.Min(r => r.GoodputBps):F1}\t{g.Max(r => r.GoodputBps):F1}\t")
                  .Append(inv, $"{(data == 0 ? 0 : 100.0 * retx / data):F2}\t{g.Sum(r => r.LinksLost)}\t{g.Sum(r => r.Chat.Delivered)}\t{g.Sum(r => r.Chat.Sent)}\t{(chat.N == 0 ? "-" : chat.Quantile(0.5).ToString("F0", inv))}\t")
                  .Append(string.Join(",", rungs.Select(kv => $"{kv.Key}:{kv.Value}")))
                  .Append('\n');
            }

            File.WriteAllText(Path.Combine(aggDir, $"curve-{ch.Key}.tsv"), sb.ToString());
        }

        // Open question 14: per channel the single fixed rung with the highest goodput summed over the sweep.
        var bl = new StringBuilder("channel\tkind\tset_snr_tenth_db\tconfig\tgoodput_mean_bps\n");
        foreach (IGrouping<string, RunResult> ch in results.Where(r => r.Config.StartsWith("fixed-", StringComparison.Ordinal)).GroupBy(r => r.Channel).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var means = ch.GroupBy(r => (r.Config, r.SnrTenthDb)).ToDictionary(g => g.Key, g => g.Average(r => r.GoodputBps));
            var best = means.GroupBy(kv => kv.Key.Config).Select(g => (Config: g.Key, Sum: g.Sum(kv => kv.Value))).OrderByDescending(x => x.Sum).ThenBy(x => x.Config, StringComparer.Ordinal).First();
            bl.Append(inv, $"{ch.Key}\tbaseline\t-\t{best.Config}\t{best.Sum / means.Count(kv => kv.Key.Config == best.Config):F1}\n");
            foreach (IGrouping<int, KeyValuePair<(string Config, int SnrTenthDb), double>> pt in means.GroupBy(kv => kv.Key.SnrTenthDb).OrderBy(g => g.Key))
            {
                var top = pt.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Config, StringComparer.Ordinal).First();
                bl.Append(inv, $"{ch.Key}\tbest_at_point\t{pt.Key}\t{top.Key.Config}\t{top.Value:F1}\n");
            }
        }

        File.WriteAllText(Path.Combine(aggDir, "baseline.tsv"), bl.ToString());

        var ta = new StringBuilder("channel\tconfig_kind\trung\tkind\tn\tmin_ms\tp10_ms\tmedian_ms\tmean_ms\tp90_ms\tmax_ms\n");
        var os = new StringBuilder("channel\tconfig_kind\trung\tn\tmin_ms\tp10_ms\tmedian_ms\tmean_ms\tp90_ms\tmax_ms\n");
        foreach (IGrouping<(string Channel, string Kind), RunResult> g in results.GroupBy(r => (r.Channel, Kind: r.Config.Split('-')[0])).OrderBy(g => g.Key.Channel, StringComparer.Ordinal).ThenBy(g => g.Key.Kind, StringComparer.Ordinal))
        {
            var same = new SortedDictionary<string, Dist>(StringComparer.Ordinal);
            var over = new SortedDictionary<string, Dist>(StringComparer.Ordinal);
            var exchange = new Dist();
            var sameAll = new Dist();
            foreach (RunResult r in g)
            {
                exchange.Merge(r.ExchangeGapMs);
                foreach ((string k, Dist d) in r.SameSenderGapMs)
                {
                    (same.TryGetValue(k, out Dist? x) ? x : same[k] = new Dist()).Merge(d);
                    sameAll.Merge(d);
                }

                foreach ((string k, Dist d) in r.EotOvershootMs)
                {
                    (over.TryGetValue(k, out Dist? x) ? x : over[k] = new Dist()).Merge(d);
                }
            }

            void Line(StringBuilder sb, string rung, string? kind, Dist d) =>
                sb.Append(inv, $"{g.Key.Channel}\t{g.Key.Kind}\t{rung}\t{(kind is null ? "" : kind + "\t")}{d.N}\t{d.Min}\t{d.Quantile(0.1):F0}\t{d.Quantile(0.5):F0}\t{d.Mean:F0}\t{d.Quantile(0.9):F0}\t{d.Max}\n");
            Line(ta, "all", "exchange", exchange);
            Line(ta, "all", "same_sender", sameAll);
            foreach ((string k, Dist d) in same)
            {
                Line(ta, k, "same_sender", d);
            }

            foreach ((string k, Dist d) in over)
            {
                Line(os, k, null, d);
            }
        }

        File.WriteAllText(Path.Combine(aggDir, "turnaround.tsv"), ta.ToString());
        File.WriteAllText(Path.Combine(aggDir, "overshoot.tsv"), os.ToString());
    }
}
