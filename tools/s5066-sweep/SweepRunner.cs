using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace M0LTE.S5066.Sweep;

/// <summary>
/// Runs a sweep's jobs into an output directory: one JSON file per run under <c>runs/</c>, written
/// whole when the run finishes (a killed run just runs again), <c>plan.json</c> fixing the grid so a
/// resume or another shard cannot mix grids, and <c>progress.log</c> with the speed and time to go.
/// </summary>
internal static class SweepRunner
{
    public static int Run(SweepSettings settings, string outDir, int workers, (int Index, int Count) shard, Action<string> log, Func<RunSettings, RunResult>? runOne = null)
    {
        runOne ??= job => new Simulation(job).Run();
        string runs = Path.Combine(outDir, "runs");
        Directory.CreateDirectory(runs);
        string planPath = Path.Combine(outDir, "plan.json");
        string plan = settings.Describe().ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (File.Exists(planPath))
        {
            if (File.ReadAllText(planPath) != plan)
            {
                log($"{planPath} was written by a different plan; use a new --out, or the same settings, node file, table and build");
                return 1;
            }
        }
        else
        {
            File.WriteAllText(planPath, plan);
        }

        List<RunSettings> all = settings.Jobs();
        var todo = all.Where((j, i) => i % shard.Count == shard.Index)
            .Where(j => !File.Exists(Path.Combine(runs, j.Id + ".json")))
            .ToList();
        // Slow rungs at low SNR are no slower to simulate than fast ones (the channel time is fixed),
        // so the order is the plan's; fading channels cost a little more, so they go first.
        todo = [.. todo.OrderByDescending(j => j.Channel.Paths.Length).ThenBy(j => j.Channel.Ssb ? 0 : 1)];
        double signalHours = todo.Count * settings.DurationMs / 3_600_000.0;
        log(string.Create(CultureInfo.InvariantCulture,
            $"[sweep] {settings.Profile}: shard {shard.Index}/{shard.Count}, {todo.Count} runs to do ({all.Count} planned), {signalHours:F1} channel-hours, {workers} workers, out {outDir}"));

        var progressPath = Path.Combine(outDir, "progress.log");
        var gate = new object();
        var wall = Stopwatch.StartNew();
        int done = 0, failed = 0;
        double coreSeconds = 0;
        Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = workers }, job =>
        {
            RunResult r;
            try
            {
                r = runOne(job);
            }
            catch (Exception e)
            {
                r = new RunResult { Id = job.Id, Channel = job.Channel.Name, SnrTenthDb = job.SnrTenthDb, Config = job.Config.Id, Seed = job.Seed, Error = e.ToString() };
            }

            string path = Path.Combine(runs, job.Id + ".json");
            if (r.Error is null)
            {
                File.WriteAllText(path + ".tmp", r.ToJson());
                File.Move(path + ".tmp", path, overwrite: true);
                File.Delete(Path.Combine(runs, job.Id + ".failed"));   // a retry that worked clears the earlier failure
            }
            else
            {
                File.WriteAllText(Path.Combine(runs, job.Id + ".failed"), r.Error);
            }

            lock (gate)
            {
                done++;
                failed += r.Error is null ? 0 : 1;
                coreSeconds += r.WallSeconds;
                double perSignal = coreSeconds / (done * settings.DurationMs / 1000.0);
                double left = (todo.Count - done) * settings.DurationMs / 1000.0 * perSignal / workers / 3600;
                string line = string.Create(CultureInfo.InvariantCulture,
                    $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} {done}/{todo.Count} {job.Id} goodput {r.GoodputBps:0} bit/s{(r.Error is null ? "" : " FAILED")}; {perSignal:F3} core-s per channel-s, about {left:F2} h to go");
                File.AppendAllText(progressPath, line + Environment.NewLine);
                log(line);
            }
        });

        log(string.Create(CultureInfo.InvariantCulture, $"[sweep] done: {done} runs, {failed} failed, {wall.Elapsed.TotalMinutes:F1} min"));
        return failed == 0 ? 0 : 1;
    }
}
