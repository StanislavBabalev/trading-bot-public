using System.Diagnostics;
using System.Security.Cryptography;
using Backtester.Core.Analytics;
using Backtester.Core.Engine;
using Backtester.Core.Strategies;

namespace Backtester.Core.Research;

/// <summary>Thrown when a run reaches one of its limits or a stop was requested. Completed experiments stay stored.</summary>
public sealed class ResearchStoppedException(string reason) : Exception(reason);

/// <summary>
/// Limits for one research run, checked before every experiment that has to be computed (stored results are free).
/// Thread-safe.
/// </summary>
public sealed class ExperimentBudget
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _new, _hits, _cycleStart;
    private int? _cycleLimit;

    public int? MaxNewExperiments { get; init; }
    public TimeSpan? MaxRuntime { get; init; }
    public long? MaxManagedMemoryBytes { get; init; }
    /// <summary>A run stops at its next experiment once this file exists (see <c>research stop</c>).</summary>
    public string? StopFile { get; init; }

    public int NewExperiments => Volatile.Read(ref _new);
    public int CacheHits => Volatile.Read(ref _hits);
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Starts counting a cycle's new experiments against <paramref name="maxNewExperiments"/>.</summary>
    public void BeginCycle(int? maxNewExperiments)
    {
        _cycleStart = NewExperiments;
        _cycleLimit = maxNewExperiments;
    }

    /// <summary>Throws <see cref="ResearchStoppedException"/> if the run must stop now.</summary>
    public void Check()
    {
        if (StopFile is not null && File.Exists(StopFile)) throw new ResearchStoppedException($"stop requested ({StopFile} exists)");
        if (MaxRuntime is { } t && _clock.Elapsed > t) throw new ResearchStoppedException($"runtime limit of {t.TotalMinutes:F0} min reached");
        if (MaxManagedMemoryBytes is { } m && GC.GetTotalMemory(false) > m)
            throw new ResearchStoppedException($"managed memory above {m / 1048576} MB");
    }

    /// <summary>Claims one new experiment, or throws if a limit would be exceeded.</summary>
    public void Reserve()
    {
        Check();
        int n = Interlocked.Increment(ref _new);
        if (MaxNewExperiments is { } max && n > max)
        {
            Interlocked.Decrement(ref _new);
            throw new ResearchStoppedException($"limit of {max} new experiments per run reached");
        }
        if (_cycleLimit is { } c && n - _cycleStart > c)
        {
            Interlocked.Decrement(ref _new);
            throw new ResearchStoppedException($"limit of {c} new experiments per cycle reached");
        }
    }

    internal void Hit() => Interlocked.Increment(ref _hits);
}

/// <summary>Where an experiment was run from, for the record.</summary>
public readonly record struct ExperimentTag(int Cycle, string Stage);

public readonly record struct ExperimentJob(StrategyDefinition Definition, IReadOnlyDictionary<string, double> Parameters, BacktestConfig Config);

/// <summary>
/// Runs backtests through the experiment store: a configuration whose identity is already stored is not run again,
/// every new result is stored as soon as it completes, and batches run on a bounded number of worker threads with
/// results returned in input order. Failed runs throw and are never stored.
/// </summary>
public sealed class ExperimentRunner(BacktestEngine engine, string symbol, ExperimentStore store, ExperimentBudget? budget = null, int workers = 1)
{
    public BacktestEngine Engine => engine;
    public ExperimentStore Store => store;
    public ExperimentBudget Budget { get; } = budget ?? new ExperimentBudget();
    public int Workers { get; } = Math.Max(1, workers);

    public ExperimentIdentity Identify(StrategyDefinition def, IReadOnlyDictionary<string, double> p, BacktestConfig cfg) =>
        ExperimentIdentity.Create(def.Name, def.Version, p, symbol, engine.DataFingerprint, cfg);

    /// <summary>Metrics for one configuration, from the store when possible. Null if the parameters are invalid.</summary>
    public PerformanceMetrics? Evaluate(StrategyDefinition def, IReadOnlyDictionary<string, double> p, BacktestConfig cfg, ExperimentTag tag)
    {
        var identity = Identify(def, p, cfg);
        string id = identity.Id;
        if (store.TryGet(id, out var stored))
        {
            Budget.Hit();
            return stored.Metrics;
        }
        if (def.Create(p) is not { } strategy) return null;
        return Execute(strategy, identity, id, p, cfg, tag, reserve: true).Metrics;
    }

    /// <summary>
    /// Always runs (callers need the trades and equity curve) and stores the result. Pass <paramref name="budgetReserved"/>
    /// when the caller already claimed this experiment with <see cref="ExperimentBudget.Reserve"/>.
    /// </summary>
    public (BacktestResult Result, PerformanceMetrics Metrics) Run(StrategyDefinition def, IReadOnlyDictionary<string, double> p, BacktestConfig cfg,
        ExperimentTag tag, bool budgetReserved = false)
    {
        var strategy = def.Create(p) ?? throw new ArgumentException($"Invalid parameters for {def.Name}: {string.Join(" ", p.Select(kv => $"{kv.Key}={kv.Value}"))}");
        var identity = Identify(def, p, cfg);
        return Execute(strategy, identity, identity.Id, p, cfg, tag, reserve: !budgetReserved);
    }

    /// <summary>Evaluates every job on up to <see cref="Workers"/> threads. Result i belongs to job i (null = invalid parameters).</summary>
    public PerformanceMetrics?[] EvaluateMany(IReadOnlyList<ExperimentJob> jobs, ExperimentTag tag)
    {
        var results = new PerformanceMetrics?[jobs.Count];
        if (Workers == 1 || jobs.Count < 2)
        {
            for (int i = 0; i < jobs.Count; i++) results[i] = Evaluate(jobs[i].Definition, jobs[i].Parameters, jobs[i].Config, tag);
            return results;
        }
        try
        {
            Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = Workers },
                i => results[i] = Evaluate(jobs[i].Definition, jobs[i].Parameters, jobs[i].Config, tag));
        }
        catch (AggregateException e) when (e.InnerExceptions.OfType<ResearchStoppedException>().FirstOrDefault() is { } stop)
        {
            throw new ResearchStoppedException(stop.Message);
        }
        return results;
    }

    private (BacktestResult Result, PerformanceMetrics Metrics) Execute(IStrategy strategy, ExperimentIdentity identity, string id,
        IReadOnlyDictionary<string, double> p, BacktestConfig cfg, ExperimentTag tag, bool reserve)
    {
        if (reserve) Budget.Reserve();
        var sw = Stopwatch.StartNew();
        var result = engine.Run(strategy, cfg, p);
        var metrics = PerformanceMetrics.Compute(result);
        store.Add(new ExperimentRecord(id, identity, metrics, TradesHash(result.Trades), tag.Cycle, tag.Stage, DateTime.UtcNow, sw.Elapsed.TotalMilliseconds));
        return (result, metrics);
    }

    /// <summary>Behavioural fingerprint: identical trade lists hash the same, whatever the strategy or parameters.</summary>
    public static string TradesHash(IReadOnlyList<Trade> trades)
    {
        // One buffer and one hash call: per-field incremental hashing costs a native call each.
        var buf = new long[trades.Count * 5];
        for (int i = 0; i < trades.Count; i++)
        {
            var t = trades[i];
            buf[i * 5] = t.Direction;
            buf[i * 5 + 1] = t.EntryTimeMs;
            buf[i * 5 + 2] = t.ExitTimeMs;
            buf[i * 5 + 3] = BitConverter.DoubleToInt64Bits(t.EntryPrice);
            buf[i * 5 + 4] = BitConverter.DoubleToInt64Bits(t.ExitPrice);
        }
        return Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buf.AsSpan())))[..16];
    }
}
