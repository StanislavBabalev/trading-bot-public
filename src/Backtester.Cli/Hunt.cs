using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backtester.Core;
using Backtester.Core.Analytics;
using Backtester.Core.Engine;
using Backtester.Core.Optimization;
using Backtester.Core.Reporting;
using Backtester.Core.Research;
using Backtester.Core.Strategies;
using Backtester.Core.Validation;

namespace Backtester.Cli;

/// <summary>One registered trial. <see cref="Interval"/> is the bar length in minutes; null means the default 60.</summary>
internal sealed record TrialRecord(
    int Id, int Cycle, string Strategy, string Stage, IReadOnlyDictionary<string, double> Parameters,
    double DailySharpe, double Sharpe, double ProfitFactor, int Trades, string Source,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    int? Interval = null)
{
    [System.Text.Json.Serialization.JsonIgnore] public int IntervalMinutes => Interval ?? 60;
}

internal sealed record FoldResult(
    int Fold, string TestFrom, string TestTo, double NetProfitPct, double ProfitFactor, double Sharpe, double MaxDrawdownPct, int Trades, bool Pass);

/// <summary>Per-trade net return on entry notional, in percent.</summary>
internal sealed record TradeDistribution(
    int Count, double MeanPct, double MedianPct, double P05Pct, double P25Pct, double P75Pct, double P95Pct,
    double WorstPct, double BestPct, double AvgBarsHeld, double WinRatePct)
{
    public static TradeDistribution From(IReadOnlyList<Trade> trades)
    {
        if (trades.Count == 0) return new(0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        var r = trades.Select(t => t.NetPnl / (t.EntryPrice * t.Quantity) * 100).OrderBy(x => x).ToList();
        double P(double q)
        {
            double pos = q * (r.Count - 1);
            int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
            return r[lo] + (r[hi] - r[lo]) * (pos - lo);
        }
        return new(r.Count, r.Average(), P(0.5), P(0.05), P(0.25), P(0.75), P(0.95), r[0], r[^1],
            trades.Average(t => t.BarsHeld), 100.0 * r.Count(x => x > 0) / r.Count);
    }
}

internal sealed record DsrResult(
    int Trials, double TrialDailySharpeVariance, double ExpectedMaxDailySharpe, double DailySharpe,
    int Observations, double Skewness, double Kurtosis, double DeflatedSharpe);

internal sealed class HypothesisReport
{
    public required string Strategy { get; init; }
    public required string Family { get; init; }
    public required string Rules { get; init; }
    public required IReadOnlyDictionary<string, double> DefaultParameters { get; init; }
    public string Verdict { get; set; } = "REJECTED";
    public string Stage { get; set; } = "";
    public List<string> KillReasons { get; } = [];
    public PerformanceMetrics? BaselineDev { get; set; }
    public List<FoldResult>? BaselineWalkForward { get; set; }
    public object? Optimization { get; set; }
    public IReadOnlyDictionary<string, double>? OptimizedParameters { get; set; }
    public PerformanceMetrics? OptimizedDev { get; set; }
    public List<FoldResult>? OptimizedWalkForward { get; set; }
    public List<SlippageRow>? DevSlippageSensitivity { get; set; }
    public TradeDistribution? DevTradeDistribution { get; set; }
    public PerformanceMetrics? LockedOos { get; set; }
    public TradeDistribution? OosTradeDistribution { get; set; }
    public DsrResult? Dsr { get; set; }
    public Dictionary<string, string> Artifacts { get; } = [];
}

/// <summary>Every parameter set ever evaluated on development data, for the multiple-testing correction.</summary>
internal sealed class TrialRegistry
{
    private readonly string _path;
    private readonly int _interval;
    private readonly List<TrialRecord> _trials;
    private readonly HashSet<string> _keys;

    private TrialRegistry(string path, int interval, List<TrialRecord> trials)
    {
        _path = path;
        _interval = interval;
        _trials = trials;
        _keys = trials.Select(t => Key(t.Strategy, t.IntervalMinutes, t.Parameters)).ToHashSet();
    }

    public IReadOnlyList<TrialRecord> All => _trials;
    public int Count => _trials.Count;

    /// <summary>Loads the registry; trials added afterwards are recorded at <paramref name="intervalMinutes"/>.</summary>
    public static TrialRegistry Load(string path, JsonObject log, int intervalMinutes = 60)
    {
        if (File.Exists(path))
            return new TrialRegistry(path, intervalMinutes, File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonSerializer.Deserialize<TrialRecord>(l, Json.Options)!).ToList());

        // First hunt cycle: import pre-hunt trials from their saved optimization artifacts.
        var seeded = new List<TrialRecord>();
        foreach (var pre in (log["pre_hunt_trials"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string strategy = pre["strategy"]!.GetValue<string>();
            int expected = pre["distinct_parameter_sets"]!.GetValue<int>();
            int before = seeded.Count;
            foreach (var art in (pre["artifacts"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).Where(p => p.Contains("optimizations")))
            {
                var file = Path.Combine(art, "optimization.json");
                if (!File.Exists(file)) throw new FileNotFoundException($"Pre-hunt artifact missing: {file}");
                var opt = JsonNode.Parse(File.ReadAllText(file))!;
                foreach (var t in opt["trials"]!.AsArray().OfType<JsonObject>())
                {
                    var p = t["parameters"]!.AsObject().ToDictionary(kv => kv.Key, kv => Json.Number(kv.Value));
                    var m = t["metrics"]!;
                    seeded.Add(new TrialRecord(seeded.Count + 1, 0, strategy, "pre-hunt", p,
                        Json.Number(m["dailySharpe"]), Json.Number(m["sharpe"]), Json.Number(m["profitFactor"]), (int)Json.Number(m["trades"]), file));
                }
            }
            if (seeded.Count - before != expected)
                throw new InvalidDataException($"hunt_log says {expected} pre-hunt trials for {strategy}, artifacts hold {seeded.Count - before}.");
        }
        return new TrialRegistry(path, intervalMinutes, seeded);
    }

    /// <summary>Adds a trial unless this exact parameter set was already counted at this bar interval.</summary>
    public void Add(int cycle, string strategy, string stage, IReadOnlyDictionary<string, double> p, PerformanceMetrics m, string source)
    {
        if (!_keys.Add(Key(strategy, _interval, p))) return;
        _trials.Add(new TrialRecord(_trials.Count + 1, cycle, strategy, stage, new Dictionary<string, double>(p),
            m.DailySharpe, m.Sharpe, m.ProfitFactor, m.Trades, source, _interval == 60 ? null : _interval));
    }

    public void Save() => AtomicFile.WriteAllLines(_path, _trials.Select(t => JsonSerializer.Serialize(t, Json.Compact)));

    private static string Key(string strategy, int interval, IReadOnlyDictionary<string, double> p) =>
        strategy + (interval == 60 ? "" : $"@{interval}m") + "|" +
        string.Join(",", p.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value.ToString("R", CultureInfo.InvariantCulture)}"));
}

/// <summary>How a cycle ended. A paused cycle recorded nothing in the hunt log; running it again resumes it.</summary>
internal sealed record CycleOutcome(int Cycle, bool Completed, string? PauseReason, IReadOnlyList<string> Strategies,
    int TrialsBefore, int TrialsAfter, int LockedOosRuns, string? Champion, string? ReportPath);

/// <summary>
/// One research cycle (hunt steps 1-9). Only development data is used until a candidate has passed the kill
/// checks, the optimizer's edge/plateau checks and walk-forward; then it gets exactly one locked-OOS run.
/// Every backtest goes through the experiment store, so a cycle stopped by a limit (or a crash) resumes from its
/// completed experiments, and the hunt log and trial registry are only written once the whole cycle has finished.
/// </summary>
internal static class Hunt
{
    private static readonly JsonSerializerOptions LogJson = new(Json.Options) { PropertyNamingPolicy = null };

    public static int Run(CliArgs a)
    {
        var cfg = Commands.LoadConfig(a);
        string logPath = a.Get("log") ?? "hunt_log.json";
        RequireAuditedData(Research.ReadLog(logPath)["cycles"]!.AsArray(), cfg);
        using var session = ResearchSession.Open(cfg, a);
        var names = a.Get("strategies")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var outcome = RunCycle(cfg, logPath, names, session.Runner, session.Limits.MaxNewExperimentsPerCycle);
        Console.WriteLine(session.Summary());
        return outcome.Completed ? 0 : 3;
    }

    /// <summary>Strategies in the registry that no cycle has tested yet, in registry order.</summary>
    public static List<string> Untested(JsonObject log)
    {
        var tested = (log["tested_strategies"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToHashSet();
        var preHunt = (log["pre_hunt_trials"] as JsonArray ?? []).Select(n => n!["strategy"]!.GetValue<string>()).ToHashSet();
        return StrategyRegistry.All.Select(d => d.Name).Where(n => !tested.Contains(n) && !preHunt.Contains(n)).ToList();
    }

    public static CycleOutcome RunCycle(HuntConfig cfg, string logPath, List<string>? requested, ExperimentRunner runner, int? maxNewExperimentsPerCycle)
    {
        var rules = cfg.Hunt;
        var log = JsonNode.Parse(File.ReadAllText(logPath))!.AsObject();
        var cycles = log["cycles"]!.AsArray();
        RequireAuditedData(cycles, cfg);

        var engine = runner.Engine;
        int cycleNo = cycles.Count + 1;
        string huntDir = Path.Combine("artifacts", "hunt", $"cycle_{cycleNo}");
        string runsDir = Path.Combine("artifacts", "runs", $"hunt_cycle_{cycleNo}");
        Directory.CreateDirectory(huntDir);

        var registry = TrialRegistry.Load(Path.Combine("artifacts", "hunt", "trials.jsonl"), log, cfg.IntervalMinutes);
        int trialsBefore = registry.Count;

        var tested = (log["tested_strategies"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToHashSet();
        var names = requested ?? Untested(log).Take(rules.HypothesesPerCycle).ToList();
        if (names.Count == 0) throw new InvalidOperationException("No untested hypotheses left in the strategy registry.");
        if (names.Count > rules.HypothesesPerCycle) throw new ArgumentException($"At most {rules.HypothesesPerCycle} hypotheses per cycle.");
        foreach (var n in names.Where(tested.Contains)) throw new ArgumentException($"'{n}' was already tested in an earlier cycle.");

        var dev = cfg.ToBacktestConfig(cfg.Split.DevFrom, cfg.Split.DevTo);
        var oos = cfg.ToBacktestConfig(cfg.Split.OosFrom, cfg.Split.OosTo);
        var folds = WalkForward.Generate(cfg.Split.DevFrom, cfg.Split.DevTo, rules.WalkForward.TrainMonths, rules.WalkForward.TestMonths, rules.WalkForward.StepMonths);
        double devYears = (dev.ToMs - dev.FromMs) / (365.25 * 86_400_000.0);
        int minTrades = (int)Math.Ceiling(rules.MinTradesPerYear * devYears);

        Console.WriteLine($"Hunt cycle {cycleNo}: {names.Count} hypotheses, {folds.Count} walk-forward folds, optimizer cap {rules.OptimizerMaxEvaluations}, " +
                          $"min {minTrades} dev trades. Trials so far: {trialsBefore}.");

        var reports = new List<HypothesisReport>();
        int oosRuns = 0;
        runner.Budget.BeginCycle(maxNewExperimentsPerCycle);
        try
        {
            foreach (var name in names)
            {
                var def = StrategyRegistry.Get(name);
                var rep = new HypothesisReport { Strategy = def.Name, Family = def.Family, Rules = def.Description, DefaultParameters = def.Defaults };
                reports.Add(rep);
                if (Evaluate(def, rep, runner, cfg, rules, dev, oos, folds, minTrades, registry, cycleNo, huntDir, runsDir)) oosRuns++;
                Console.WriteLine($"  {rep.Strategy,-20} {rep.Verdict,-8} at {rep.Stage,-14} {string.Join("; ", rep.KillReasons)}");
            }
        }
        catch (ResearchStoppedException e)
        {
            Console.WriteLine($"Cycle {cycleNo} paused: {e.Message}. Nothing was written to {logPath} or the trial registry; " +
                              "completed experiments are stored, so running the same command again resumes this cycle.");
            return new CycleOutcome(cycleNo, false, e.Message, names, trialsBefore, trialsBefore, oosRuns, null, null);
        }

        // Step 8: rank everything that ever passed by Deflated Sharpe, penalized by the cumulative trial count.
        var daily = registry.All.Select(t => t.DailySharpe).Where(double.IsFinite).ToList();
        double trialVariance = Statistics.Variance(daily);
        int trials = registry.Count;
        foreach (var rep in reports.Where(r => r.Verdict == "PASSED"))
            rep.Dsr = Dsr(rep.OptimizedDev!.DailySharpe, rep.OptimizedDev.DailyReturnCount, rep.OptimizedDev.DailySkewness, rep.OptimizedDev.DailyKurtosis, trials, trialVariance);

        var candidates = new List<JsonObject>();
        if (log["champion"] is JsonObject oldChampion) candidates.Add(Rescore(oldChampion, trials, trialVariance));
        foreach (var r in (log["runner_ups"] as JsonArray ?? []).OfType<JsonObject>()) candidates.Add(Rescore(r, trials, trialVariance));
        foreach (var rep in reports.Where(r => r.Verdict == "PASSED")) candidates.Add(ChampionRecord(rep, cfg, cycleNo, huntDir));
        static double Rank(double v) => double.IsNaN(v) ? double.NegativeInfinity : v;
        var ranked = candidates
            .OrderByDescending(c => Rank(Json.Number(c["deflatedSharpe"])))
            .ThenByDescending(c => Rank(Json.Number(c["calmarDev"])))
            .ToList();

        PerformanceMetrics benchDev = PerformanceMetrics.Compute(Benchmark.BuyAndHold(engine.Bars, dev));
        PerformanceMetrics? benchOos = oosRuns > 0 ? PerformanceMetrics.Compute(Benchmark.BuyAndHold(engine.Bars, oos)) : null;
        registry.Save();

        var warnings = new List<string>
        {
            $"{trials} parameter sets have been evaluated on development data so far; the best of that many lucky trials " +
            $"would show a daily Sharpe near {DeflatedSharpe.ExpectedMaxSharpe(trials, trialVariance):F4} " +
            $"(annualized {DeflatedSharpe.ExpectedMaxSharpe(trials, trialVariance) * Math.Sqrt(365):F2}) with no real edge.",
            "Walk-forward folds use fixed parameters (no per-fold re-optimization, to stay within the optimizer cap); " +
            "for optimized candidates the test folds lie inside the window the parameters were tuned on, so only the locked OOS run is truly out of sample.",
            "Max drawdown is measured on hourly bar-close equity; intrabar drawdowns are deeper.",
        };

        string reportPath = Path.Combine(huntDir, "report.json");
        Json.Write(reportPath, new
        {
            cycle = cycleNo,
            timestampUtc = DateTime.UtcNow,
            rules,
            constants = new { cfg.Symbol, cfg.IntervalMinutes, cfg.InitialCapital, cfg.Costs, cfg.FundingEnabled, cfg.Sizing, cfg.Risk, cfg.Split },
            walkForwardFolds = folds,
            minDevTrades = minTrades,
            trials = new { before = trialsBefore, thisCycle = trials - trialsBefore, cumulative = trials, dailySharpeVariance = trialVariance, registry = "artifacts/hunt/trials.jsonl" },
            buyAndHold = new { dev = benchDev, oos = benchOos },
            hypotheses = reports,
            champion = ranked.FirstOrDefault(),
            runnerUps = ranked.Skip(1).ToList(),
            warnings,
        });

        // Step 9: persist state.
        log["cumulative_trial_count"] = trials;
        if (log["tested_strategies"] is not JsonArray testedArr) log["tested_strategies"] = testedArr = new JsonArray();
        foreach (var n in names) testedArr.Add(n);
        if (log["rejected"] is not JsonArray rejected) log["rejected"] = rejected = new JsonArray();
        foreach (var rep in reports.Where(r => r.Verdict != "PASSED"))
            rejected.Add(new JsonObject
            {
                ["cycle"] = cycleNo,
                ["strategy"] = rep.Strategy,
                ["family"] = rep.Family,
                ["stage"] = rep.Stage,
                ["reasons"] = new JsonArray(rep.KillReasons.Select(r => (JsonNode)r).ToArray()),
                ["parameters"] = JsonSerializer.SerializeToNode(rep.OptimizedParameters ?? rep.DefaultParameters, LogJson),
                ["report"] = reportPath.Replace('\\', '/'),
            });
        log["champion"] = ranked.FirstOrDefault()?.DeepClone();
        log["runner_ups"] = new JsonArray(ranked.Skip(1).Select(c => (JsonNode)c.DeepClone()).ToArray());
        cycles.Add(new JsonObject
        {
            ["cycle"] = cycleNo,
            ["timestamp_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["step"] = "hunt cycle (steps 1-9)",
            ["hypotheses"] = new JsonArray(reports.Select(r => (JsonNode)new JsonObject
            {
                ["strategy"] = r.Strategy,
                ["family"] = r.Family,
                ["verdict"] = r.Verdict,
                ["stage"] = r.Stage,
                ["reasons"] = new JsonArray(r.KillReasons.Select(x => (JsonNode)x).ToArray()),
            }).ToArray()),
            ["trials_this_cycle"] = trials - trialsBefore,
            ["cumulative_trial_count"] = trials,
            ["locked_oos_runs"] = oosRuns,
            ["champion"] = ranked.FirstOrDefault()?["strategy"]?.GetValue<string>(),
            ["report"] = reportPath.Replace('\\', '/'),
        });
        AtomicFile.WriteAllText(logPath, log.ToJsonString(LogJson));

        Console.WriteLine($"\nTrials: {trialsBefore} -> {trials}. Locked OOS runs this cycle: {oosRuns}.");
        Console.WriteLine(ranked.Count == 0
            ? "No champion: every hypothesis so far has been rejected."
            : $"Champion: {ranked[0]["strategy"]} DSR {ranked[0]["deflatedSharpe"]}");
        Console.WriteLine($"Report: {reportPath}");
        Console.WriteLine($"HTML:   {Commands.PublishReport(cfg, engine, w => w.WriteCycle(huntDir))}");
        return new CycleOutcome(cycleNo, true, null, names, trialsBefore, trials, oosRuns,
            ranked.FirstOrDefault()?["strategy"]?.GetValue<string>(), reportPath.Replace('\\', '/'));
    }

    /// <summary>Runs steps 3-7 for one hypothesis. Returns true if it used its locked-OOS run.</summary>
    private static bool Evaluate(
        StrategyDefinition def, HypothesisReport rep, ExperimentRunner runner, HuntConfig cfg, HuntRules rules,
        BacktestConfig dev, BacktestConfig oos, IReadOnlyList<Fold> folds, int minTrades, TrialRegistry registry,
        int cycleNo, string huntDir, string runsDir)
    {
        string runRoot = Path.Combine(runsDir, def.Name);
        var engine = runner.Engine;

        // Step 3: development backtest with the hypothesis' a-priori defaults.
        var defaults = new Dictionary<string, double>(def.Defaults);
        var (baseRes, baseM) = runner.Run(def, defaults, dev, new(cycleNo, "baseline-dev"));
        string baseDir = Path.Combine(runRoot, "baseline_dev");
        ArtifactWriter.WriteRun(baseDir, baseRes, baseM, null);
        rep.Artifacts["baselineDev"] = baseDir.Replace('\\', '/');
        registry.Add(cycleNo, def.Name, "baseline", defaults, baseM, baseDir);
        rep.BaselineDev = baseM;
        rep.BaselineWalkForward = RunFolds(runner, def, defaults, folds, cfg, rules, new(cycleNo, "baseline-folds"));

        // Step 4: numeric kill.
        if (Reject(rep, "dev-baseline", Kill(baseM, rep.BaselineWalkForward, rules))) return false;

        // Step 5: optimize on development data only.
        string optFile = Path.Combine(huntDir, $"{def.Name}_optimization.json");
        var trialLog = new List<object>();
        var options = new OptimizerOptions
        {
            MaxEvaluations = rules.OptimizerMaxEvaluations,
            MinTrades = minTrades,
            PlateauMinProfitFactor = rules.PlateauMinProfitFactor,
            PlateauMinSharpe = rules.PlateauMinSharpe,
        };
        // The search points are backtested in parallel first; the callback then reads them back in grid order, so the
        // registry and trial log come out in the same order as a single-threaded run.
        var optTag = new ExperimentTag(cycleNo, "optimizer");
        var opt = GridOptimizer.Run(def.DefaultSpace, p =>
        {
            if (runner.Evaluate(def, p, dev, optTag) is not { } m) return null;
            registry.Add(cycleNo, def.Name, "optimizer", p, m, optFile);
            trialLog.Add(new { parameters = p, metrics = m });
            return new TrialOutcome(p, m.Sharpe, m.ProfitFactor, m.Sharpe, m.Trades, m.DailySharpe);
        }, options, defaults, prefetch: points => runner.EvaluateMany(points.Select(p => new ExperimentJob(def, p, dev)).ToList(), optTag));

        rep.Optimization = new
        {
            objective = "sharpe",
            space = def.DefaultSpace,
            opt.GridSize,
            opt.Sampled,
            opt.Evaluations,
            best = opt.Best,
            opt.BestAtEdgeOf,
            opt.PlateauPassed,
            opt.Verdict,
            neighbours = opt.Neighbours,
            top10 = opt.Trials.Where(t => t.Trades >= minTrades).OrderByDescending(t => t.Objective).Take(10).ToList(),
            eligibleTrials = opt.Trials.Count(t => t.Trades >= minTrades),
        };
        Json.Write(optFile, new { strategy = def.Name, window = "dev", options, report = rep.Optimization, trials = trialLog });
        rep.Artifacts["optimization"] = optFile.Replace('\\', '/');

        var optReasons = new List<string>();
        if (opt.Best is null) optReasons.Add($"no parameter set reached {minTrades} trades");
        else
        {
            if (opt.BestAtEdgeOf.Count > 0) optReasons.Add($"optimum on the search-range edge of {string.Join(", ", opt.BestAtEdgeOf)}");
            if (!opt.PlateauPassed) optReasons.AddRange(opt.Verdict.Where(v => !v.StartsWith("best point is on the edge")).Select(v => "plateau: " + v));
        }
        if (Reject(rep, "optimization", optReasons)) return false;

        // Step 6: optimized parameters on dev, numeric kill again, fold-by-fold walk-forward.
        var best = new Dictionary<string, double>(opt.Best!.Parameters);
        rep.OptimizedParameters = best;
        var (optRes, optM) = runner.Run(def, best, dev, new(cycleNo, "optimized-dev"));
        string optDir = Path.Combine(runRoot, "optimized_dev");
        ArtifactWriter.WriteRun(optDir, optRes, optM, null);
        rep.Artifacts["optimizedDev"] = optDir.Replace('\\', '/');
        rep.OptimizedDev = optM;
        rep.DevTradeDistribution = TradeDistribution.From(optRes.Trades);
        rep.OptimizedWalkForward = RunFolds(runner, def, best, folds, cfg, rules, new(cycleNo, "optimized-folds"));
        double[] multipliers = [0.0, 0.5, 1.0, 1.5, 2.0];
        var slipCfgs = multipliers.Select(mult => dev with { Costs = dev.Costs with { SlippageRate = dev.Costs.SlippageRate * mult } }).ToList();
        var slipM = runner.EvaluateMany(slipCfgs.Select(c => new ExperimentJob(def, best, c)).ToList(), new(cycleNo, "slippage-sensitivity"));
        rep.DevSlippageSensitivity = multipliers.Select((mult, i) =>
        {
            var m = slipM[i]!;
            return new SlippageRow(mult, slipCfgs[i].Costs.SlippageRate, m.NetProfitPct, m.CagrPct, m.MaxDrawdownPct, m.ProfitFactor, m.Sharpe);
        }).ToList();
        if (Reject(rep, "optimized-dev", Kill(optM, rep.OptimizedWalkForward, rules))) return false;

        // Step 7a: the holdout gate. The locked OOS run needs a passing validation report (python -m validation) for exactly
        // these parameters, this code and this data. Without one the cycle pauses here (nothing is recorded; rerun after
        // validating); with a failing one the candidate is rejected.
        GateDecision? gate = null;
        if (cfg.Validation.HoldoutGate)
        {
            gate = HoldoutGate.Check(cfg, CandidateIdentity.Create(def, best, cfg, engine.DataFingerprint));
            if (gate.Status == GateStatus.Missing)
                throw new ResearchStoppedException($"{def.Name} passed the dev checks and needs a validation report before its locked-OOS run " +
                                                   $"({gate.Reasons[0]}); run: python -m validation run --strategies {def.Name} " +
                                                   string.Join(" ", best.Select(kv => $"--param {kv.Key}={kv.Value.ToString(CultureInfo.InvariantCulture)}")));
            if (Reject(rep, "validation-gate", gate.Reasons.ToList())) return false;
        }

        // Step 7b: the single locked-OOS validation. Logged before it runs. A cycle that was interrupted after this point
        // runs the identical configuration again when resumed (deterministic, same observation); the log says so.
        runner.Budget.Reserve();
        string what = $"hunt cycle {cycleNo} {def.Name} {Commands.Fmt(best)}";
        bool repeat = File.Exists(HoldoutGate.AccessLog) && File.ReadLines(HoldoutGate.AccessLog).Any(l => l.Split('\t') is [_, _, var w, ..] && w.StartsWith(what, StringComparison.Ordinal));
        HoldoutGate.LogAccess(cfg, cfg.Split.OosFrom, cfg.Split.OosTo,
            what + (repeat ? " (repeat of the identical configuration after an interrupted cycle)" : ""), gate);
        var (oosRes, oosM) = runner.Run(def, best, oos, new(cycleNo, "locked-oos"), budgetReserved: true);
        string oosDir = Path.Combine(runRoot, "locked_oos");
        ArtifactWriter.WriteRun(oosDir, oosRes, oosM, PerformanceMetrics.Compute(Benchmark.BuyAndHold(engine.Bars, oos)));
        rep.Artifacts["lockedOos"] = oosDir.Replace('\\', '/');
        rep.LockedOos = oosM;
        rep.OosTradeDistribution = TradeDistribution.From(oosRes.Trades);
        if (Reject(rep, "locked-oos", Kill(oosM, null, rules))) return true;

        rep.Verdict = "PASSED";
        rep.Stage = "passed";
        return true;
    }

    private static bool Reject(HypothesisReport rep, string stage, List<string> reasons)
    {
        rep.Stage = stage;
        if (reasons.Count == 0) return false;
        rep.Verdict = "REJECTED";
        rep.KillReasons.AddRange(reasons);
        return true;
    }

    private static List<string> Kill(PerformanceMetrics m, IReadOnlyList<FoldResult>? folds, HuntRules r)
    {
        var k = new List<string>();
        if (!(m.ProfitFactor >= r.MinProfitFactor)) k.Add($"PF {m.ProfitFactor:F2} < {r.MinProfitFactor:F2}");
        if (!(m.Sharpe >= r.MinSharpe)) k.Add($"Sharpe {m.Sharpe:F2} < {r.MinSharpe:F2}");
        if (!(m.MaxDrawdownPct <= r.MaxDrawdownPct)) k.Add($"max DD {m.MaxDrawdownPct:F1}% > {r.MaxDrawdownPct:F0}%");
        if (!(m.TradesPerYear >= r.MinTradesPerYear)) k.Add($"{m.TradesPerYear:F1} trades/yr < {r.MinTradesPerYear:F0} ({m.Trades} trades)");
        if (folds is not null)
        {
            int failed = folds.Count(f => !f.Pass);
            if (failed > r.WalkForward.MaxFoldFailures) k.Add($"walk-forward: {failed} of {folds.Count} folds failed");
        }
        return k;
    }

    private static List<FoldResult> RunFolds(ExperimentRunner runner, StrategyDefinition def, IReadOnlyDictionary<string, double> p,
        IReadOnlyList<Fold> folds, HuntConfig cfg, HuntRules rules, ExperimentTag tag)
    {
        var metrics = runner.EvaluateMany(folds.Select(f => new ExperimentJob(def, p, cfg.ToBacktestConfig(f.TestFrom, f.TestTo))).ToList(), tag);
        return folds.Select((f, i) =>
        {
            var m = metrics[i] ?? throw new ArgumentException($"Invalid parameters for {def.Name}: {Commands.Fmt(p)}");
            bool pass = m.NetProfit > 0 && m.ProfitFactor >= rules.WalkForward.FoldMinProfitFactor;
            return new FoldResult(f.Index, f.TestFrom.ToString("yyyy-MM-dd"), f.TestTo.ToString("yyyy-MM-dd"),
                m.NetProfitPct, m.ProfitFactor, m.Sharpe, m.MaxDrawdownPct, m.Trades, pass);
        }).ToList();
    }

    private static DsrResult Dsr(double dailySharpe, int observations, double skew, double kurt, int trials, double variance) =>
        new(trials, variance, DeflatedSharpe.ExpectedMaxSharpe(trials, variance), dailySharpe, observations, skew, kurt,
            DeflatedSharpe.Deflated(dailySharpe, observations, skew, kurt, trials, variance));

    private static JsonObject ChampionRecord(HypothesisReport rep, HuntConfig cfg, int cycleNo, string huntDir)
    {
        var d = rep.OptimizedDev!;
        return new JsonObject
        {
            ["strategy"] = rep.Strategy,
            ["family"] = rep.Family,
            ["rules"] = rep.Rules,
            ["parameters"] = JsonSerializer.SerializeToNode(rep.OptimizedParameters, LogJson),
            ["constants"] = JsonSerializer.SerializeToNode(new { cfg.Symbol, cfg.IntervalMinutes, cfg.Costs, cfg.FundingEnabled, cfg.Sizing, cfg.Risk }, Json.Options),
            ["cycle"] = cycleNo,
            ["deflatedSharpe"] = rep.Dsr!.DeflatedSharpe,
            ["calmarDev"] = d.Calmar,
            ["selection"] = new JsonObject
            {
                ["dailySharpe"] = d.DailySharpe,
                ["observations"] = d.DailyReturnCount,
                ["skewness"] = d.DailySkewness,
                ["kurtosis"] = d.DailyKurtosis,
            },
            ["dev"] = JsonSerializer.SerializeToNode(rep.OptimizedDev, Json.Options),
            ["oos"] = JsonSerializer.SerializeToNode(rep.LockedOos, Json.Options),
            ["report"] = Path.Combine(huntDir, "report.json").Replace('\\', '/'),
            ["artifacts"] = JsonSerializer.SerializeToNode(rep.Artifacts, LogJson),
        };
    }

    /// <summary>Recomputes an earlier winner's DSR against today's (larger) trial count.</summary>
    private static JsonObject Rescore(JsonObject record, int trials, double variance)
    {
        var c = record.DeepClone().AsObject();
        var s = c["selection"]!;
        var dsr = DeflatedSharpe.Deflated(Json.Number(s["dailySharpe"]), (int)Json.Number(s["observations"]),
            Json.Number(s["skewness"]), Json.Number(s["kurtosis"]), trials, variance);
        c["deflatedSharpe"] = dsr;
        return c;
    }

    /// <summary>STEP 0 gate: an audit must have passed and the data files must be the ones it hashed.</summary>
    internal static void RequireAuditedData(JsonArray cycles, HuntConfig cfg)
    {
        var audit = cycles.OfType<JsonObject>().LastOrDefault(c => c["result"]?.GetValue<string>() == "AUDIT PASSED")
                    ?? throw new InvalidOperationException("STEP 0 has not passed; run the audit first.");
        string? barsHash = audit["checks"]?["data_btcusdt_1h"]?["file_sha256"]?.GetValue<string>();
        string? fundHash = audit["checks"]?["funding_rate_data"]?["file_sha256"]?.GetValue<string>();
        if (barsHash is null || fundHash is null) throw new InvalidOperationException("The passing audit did not record data hashes.");
        if (!Sha256(cfg.BarsPath).Equals(barsHash, StringComparison.OrdinalIgnoreCase) ||
            !Sha256(cfg.FundingPath).Equals(fundHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Data files changed since the STEP 0 audit; re-run the audit before hunting.");
    }

    private static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f));
    }
}
