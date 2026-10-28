using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Backtester.Core;
using Backtester.Core.Analytics;
using Backtester.Core.Data;
using Backtester.Core.Engine;
using Backtester.Core.Optimization;
using Backtester.Core.Reporting;
using Backtester.Core.Research;
using Backtester.Core.Strategies;

namespace Backtester.Cli;

/// <summary>
/// Throughput benchmark: full default grids of the given strategies, each on the dev window and every walk-forward
/// test fold. Runs on seeded synthetic data by default so benchmarking never evaluates parameter sets on the
/// research data. Prints timings and a digest of every metric, so two engine versions can be checked for identical output.
/// </summary>
internal static class Bench
{
    private static readonly string[] DefaultStrategies =
        ["failed-breakout-fade", "volume-climax-reversal", "session-momentum", "impulse-bar-continuation", "vol-regime-switch"];

    public static int Run(CliArgs a)
    {
        var cfg = Commands.LoadConfig(a);
        string data = a.Get("data") ?? "synthetic";
        var engine = data switch
        {
            "synthetic" => SyntheticEngine(),
            "real" => Commands.LoadEngine(cfg),
            _ => throw new ArgumentException($"Unknown --data '{data}' (synthetic|real)"),
        };
        if (data == "real")
            Console.WriteLine("note: --data real evaluates these grids on the research data; only timings and the digest are printed.");

        var names = a.Get("strategies")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? DefaultStrategies;
        var rules = cfg.Hunt.WalkForward;
        var windows = new List<BacktestConfig> { cfg.ToBacktestConfig(cfg.Split.DevFrom, cfg.Split.DevTo) };
        windows.AddRange(WalkForward.Generate(cfg.Split.DevFrom, cfg.Split.DevTo, rules.TrainMonths, rules.TestMonths, rules.StepMonths)
            .Select(f => cfg.ToBacktestConfig(f.TestFrom, f.TestTo)));

        var jobs = new List<(StrategyDefinition Def, Dictionary<string, double> P, BacktestConfig Window)>();
        foreach (var name in names)
        {
            var def = StrategyRegistry.Get(name);
            foreach (var p in GridOptimizer.Enumerate(def.DefaultSpace, def.Defaults).Where(p => def.Create(p) is not null))
                foreach (var w in windows) jobs.Add((def, p, w));
        }
        int workers = a.Int("workers") ?? 1;
        int repeat = a.Int("repeat") ?? 1;
        // --runner: go through the experiment runner and a fresh on-disk store, as hunts do. The first repeat computes and
        // persists every result; later repeats are served from the store.
        using var store = !a.Has("runner") ? null
            : a.Has("no-cache") ? ExperimentStore.InMemory()
            : ExperimentStore.Open(Path.Combine("artifacts", "bench", $"{DateTime.UtcNow:yyyyMMddTHHmmssfff}Z_store.jsonl"));
        var runner = store is null ? null : new ExperimentRunner(engine, cfg.Symbol, store, workers: workers);
        var runnerJobs = jobs.Select(j => new ExperimentJob(j.Def, j.P, j.Window)).ToList();

        // JIT warm-up on one job, untimed.
        var (d0, p0, w0) = jobs[0];
        PerformanceMetrics.Compute(engine.Run(d0.Create(p0)!, w0, p0));

        var times = new List<double>();
        string digest = "";
        long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        var pauseBefore = GC.GetTotalPauseDuration();
        for (int r = 0; r < repeat; r++)
        {
            var results = new PerformanceMetrics[jobs.Count];
            var sw = Stopwatch.StartNew();
            if (runner is not null)
                results = runner.EvaluateMany(runnerJobs, new(0, "bench"))!;
            else if (workers <= 1)
                for (int i = 0; i < jobs.Count; i++) results[i] = Evaluate(engine, jobs[i]);
            else
                Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = workers }, i => results[i] = Evaluate(engine, jobs[i]));
            sw.Stop();
            times.Add(sw.Elapsed.TotalSeconds);
            digest = Digest(results);
        }
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;
        double best = times.Min(), median = times.OrderBy(t => t).ElementAt(times.Count / 2);
        double peakMb = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0;

        var summary = new
        {
            timestampUtc = DateTime.UtcNow,
            data,
            path = runner is null ? "engine" : "experiment runner + store",
            repeatSeconds = times,
            strategies = names,
            configurations = jobs.Count / windows.Count,
            windowsPerConfiguration = windows.Count,
            backtests = jobs.Count,
            workers,
            repeat,
            processorCount = Environment.ProcessorCount,
            bestSeconds = best,
            medianSeconds = median,
            backtestsPerSecond = jobs.Count / best,
            configurationsPerSecond = jobs.Count / windows.Count / best,
            allocatedMbPerRepeat = allocated / 1048576.0 / repeat,
            peakWorkingSetMb = peakMb,
            gcCollections = new { gen0 = GC.CollectionCount(0) - gen0, gen1 = GC.CollectionCount(1) - gen1, gen2 = GC.CollectionCount(2) - gen2 },
            gcPauseSecondsPerRepeat = (GC.GetTotalPauseDuration() - pauseBefore).TotalSeconds / repeat,
            serverGc = System.Runtime.GCSettings.IsServerGC,
            metricsDigest = digest,
        };
        Console.WriteLine($"{jobs.Count} backtests ({summary.configurations} configurations x {windows.Count} windows), {data} data, {workers} worker(s), {repeat} repeat(s), via {summary.path}");
        Console.WriteLine($"  per repeat: {string.Join(", ", times.Select(t => $"{t:F3}s"))}" +
                          (runner is not null ? " (first computes and stores; later ones read the store)" : " (shared indicator series are built during the first)"));
        Console.WriteLine($"  best {best:F3}s  median {median:F3}s  {summary.backtestsPerSecond:F0} backtests/s  {summary.configurationsPerSecond:F1} configurations/s");
        Console.WriteLine($"  allocated {summary.allocatedMbPerRepeat:F0} MB/repeat  peak working set {peakMb:F0} MB  GC gen0/1/2 {summary.gcCollections.gen0}/{summary.gcCollections.gen1}/{summary.gcCollections.gen2}, " +
                          $"pause {summary.gcPauseSecondsPerRepeat:F2}s/repeat ({(summary.serverGc ? "server" : "workstation")} GC)");
        Console.WriteLine($"  metrics digest {digest}");
        string path = Path.Combine("artifacts", "bench", $"{DateTime.UtcNow:yyyyMMddTHHmmssfff}Z_bench.json");
        Json.Write(path, summary);
        Console.WriteLine($"Saved: {path}");
        return 0;
    }

    private static PerformanceMetrics Evaluate(BacktestEngine engine, (StrategyDefinition Def, Dictionary<string, double> P, BacktestConfig Window) job) =>
        PerformanceMetrics.Compute(engine.Run(job.Def.Create(job.P)!, job.Window, job.P));

    /// <summary>The synthetic market spans the same timestamps as the research data, so the same windows apply.</summary>
    private static BacktestEngine SyntheticEngine()
    {
        var (bars, funding) = SyntheticData.Generate(new DateTime(2020, 3, 25, 10, 0, 0, DateTimeKind.Utc), 57_735, seed: 20261026);
        return new BacktestEngine(bars, funding);
    }

    private static string Digest(IEnumerable<PerformanceMetrics> metrics)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var m in metrics) sha.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(m, Json.Compact) + "\n"));
        return Convert.ToHexString(sha.GetHashAndReset())[..16];
    }
}
