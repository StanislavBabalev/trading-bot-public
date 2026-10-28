using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Backtester.Core;
using Backtester.Core.Analytics;
using Backtester.Core.Engine;
using Backtester.Core.Optimization;
using Backtester.Core.Reporting;
using Backtester.Core.Strategies;
using Backtester.Core.Validation;

namespace Backtester.Cli;

/// <summary>Headline numbers of one run, as exported for the validation layer.</summary>
internal sealed record RunSummary(
    double NetProfitPct, double ProfitFactor, double Sharpe, double DailySharpe, double MaxDrawdownPct, double CagrPct, double Calmar,
    int Trades, double TradesPerYear, double FeesPaid, double FundingReceived, double SlippageCost)
{
    public static RunSummary From(PerformanceMetrics m) => new(m.NetProfitPct, m.ProfitFactor, m.Sharpe, m.DailySharpe, m.MaxDrawdownPct,
        m.CagrPct, m.Calmar, m.Trades, m.TradesPerYear, m.FeesPaid, m.FundingReceived, m.SlippageCost);
}

/// <summary>One execution-stress scenario: the candidate rerun on dev with one cost or funding assumption changed.</summary>
internal sealed record StressScenario(string Name, string Group, double Value, string Description, RunSummary Metrics);

/// <summary>How far one parameter moved: <see cref="Relative"/> for continuous parameters, <see cref="GridSteps"/> for discrete ones.</summary>
internal sealed record PerturbationStep(double Value, double? Relative, int? GridSteps);

internal sealed record PerturbationRun(string Id, string Kind, IReadOnlyDictionary<string, double> Parameters,
    IReadOnlyDictionary<string, PerturbationStep> Steps, bool Valid, string? Error, RunSummary? Metrics);

/// <summary>
/// <c>validate-export</c>: everything the Python validation layer (<c>python -m validation</c>) needs, computed by this engine
/// on the development window only; the locked OOS window is never touched. Per candidate (strategy + parameters) it writes
/// a bundle under artifacts/validation/&lt;strategy&gt;/&lt;identity&gt;/:
///   meta.json          identity (code, data, constants), search space, look-ahead audit, files
///   equity.csv         hourly mark-to-market equity of the base run      trades.csv  its trades
///   stress.json        slippage x0..4, commission x1..3, funding off / x3 / adverse at each spike month's mean |rate|
///   perturbations.json every 1D and 2D perturbation of the tunable parameters and its dev metrics
///   selection.*        the CPCV/local-PBO selection set: the strategy's registered grid if it has one, else the
///                      perturbation set; daily returns per config and their trades
/// Perturbation runs go to artifacts/validation/robustness_registry.jsonl, not to the hunt's trial registry: they can only
/// reject a candidate, never choose its parameters (AUDIT.md D3). It also replays every registered trial into
/// artifacts/validation/trials/ (daily returns, duplicates marked) for the effective number of trials and CSCV.
/// </summary>
internal static class ValidateExport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const long DayMs = 86_400_000L;

    public static int Run(CliArgs a)
    {
        var cfg = Commands.LoadConfig(a);
        var engine = Commands.LoadEngine(cfg);
        var dev = cfg.ToBacktestConfig(cfg.Split.DevFrom, cfg.Split.DevTo);
        if (dev.ToMs > TimeUtil.ToMs(cfg.Split.OosFrom))
            throw new InvalidOperationException("The development window overlaps the locked OOS period; refusing to export.");
        string outDir = a.Get("out") ?? cfg.Validation.OutputDir;
        int workers = a.Int("workers") ?? cfg.Research.ResolvedWorkers;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = workers };
        var registry = ReadRegistry(Path.Combine("artifacts", "hunt", "trials.jsonl"));

        var candidates = SelectCandidates(a, engine, dev, parallel);
        Console.WriteLine($"validate-export: {candidates.Count} candidate(s), dev {cfg.Split.DevFrom:yyyy-MM-dd}..{cfg.Split.DevTo:yyyy-MM-dd}, " +
                          $"code {Backtester.Core.Research.ExperimentIdentity.CurrentCodeFingerprint}, data {engine.DataFingerprint}");

        if (!a.Has("no-trials")) ExportTrials(Path.Combine(outDir, "trials"), registry, engine, Commands.DevEngines(cfg), parallel);

        var bundles = new List<object>();
        foreach (var (def, p) in candidates)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string dir = ExportCandidate(cfg, outDir, def, p, registry, engine, dev, parallel);
            bundles.Add(new { strategy = def.Name, parameters = p, bundle = dir.Replace('\\', '/') });
            Console.WriteLine($"  {def.Name,-28} {HoldoutGate.Describe(p),-50} -> {dir.Replace('\\', '/')} ({sw.Elapsed.TotalSeconds:F1}s)");
        }
        Json.Write(Path.Combine(outDir, "candidates.json"), new
        {
            exportedUtc = DateTime.UtcNow,
            codeFingerprint = Backtester.Core.Research.ExperimentIdentity.CurrentCodeFingerprint,
            dataFingerprint = engine.DataFingerprint,
            selection = a.Get("strategies") is not null ? "explicit" : a.Has("all") ? "all" : "profitable",
            candidates = bundles,
        });
        return 0;
    }

    // ------------------------------------------------------------------ candidates

    private static List<(StrategyDefinition Def, Dictionary<string, double> P)> SelectCandidates(
        CliArgs a, BacktestEngine engine, BacktestConfig dev, ParallelOptions parallel)
    {
        if (a.Get("strategies") is { } list)
        {
            var names = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var overrides = a.Params();
            if (overrides.Count > 0 && names.Count != 1) throw new ArgumentException("--param needs exactly one strategy in --strategies.");
            return names.Select(n => StrategyRegistry.Get(n)).Select(d => (d, StrategyRegistry.Resolve(d, overrides))).ToList();
        }
        if (a.Has("all")) return StrategyRegistry.All.Select(d => (d, new Dictionary<string, double>(d.Defaults))).ToList();

        // --profitable (default): the hunt's hypotheses whose a-priori defaults made money on dev.
        var tested = (Research.ReadLog(a.Get("log") ?? "hunt_log.json")["tested_strategies"] as System.Text.Json.Nodes.JsonArray ?? [])
            .Select(n => n!.GetValue<string>()).ToList();
        var net = new ConcurrentDictionary<string, double>();
        Parallel.ForEach(tested, parallel, name =>
        {
            var def = StrategyRegistry.Get(name);
            net[name] = PerformanceMetrics.Compute(engine.Run(def.Create(def.Defaults)!, dev, def.Defaults)).NetProfit;
        });
        return tested.Where(n => net[n] > 0).Select(n => StrategyRegistry.Get(n)).Select(d => (d, new Dictionary<string, double>(d.Defaults))).ToList();
    }

    private static string ExportCandidate(HuntConfig cfg, string outDir, StrategyDefinition def, Dictionary<string, double> p,
        List<RegisteredTrial> registry, BacktestEngine engine, BacktestConfig dev, ParallelOptions parallel)
    {
        var id = CandidateIdentity.Create(def, p, cfg, engine.DataFingerprint);
        string dir = id.BundleDir(outDir);
        Directory.CreateDirectory(dir);

        var baseRun = engine.Run(def.Create(p) ?? throw new ArgumentException($"Invalid parameters for {def.Name}: {HoldoutGate.Describe(p)}"), dev, p);
        var baseMetrics = PerformanceMetrics.Compute(baseRun);
        WriteEquity(Path.Combine(dir, "equity.csv"), baseRun);
        WriteTrades(Path.Combine(dir, "trades.csv"), baseRun.Trades);

        // Execution stress ladder.
        var spikes = cfg.Validation.FundingSpikeMonths.Select(m => (Month: m, Rate: MeanAbsFunding(engine, m, cfg))).ToList();
        var scenarios = new List<(string Name, string Group, double Value, string Description, BacktestConfig Cfg)>();
        foreach (var m in cfg.Validation.SlippageMultipliers)
            scenarios.Add(($"slippage_x{m.ToString(Inv)}", "slippage", m, $"slippage {m.ToString(Inv)} x base ({dev.Costs.SlippageRate * m:P3} per side)",
                dev with { Costs = dev.Costs with { SlippageRate = dev.Costs.SlippageRate * m } }));
        foreach (var m in cfg.Validation.CommissionMultipliers)
            scenarios.Add(($"commission_x{m.ToString(Inv)}", "commission", m, $"commission {m.ToString(Inv)} x base ({dev.Costs.CommissionRate * m:P3} per side)",
                dev with { Costs = dev.Costs with { CommissionRate = dev.Costs.CommissionRate * m } }));
        scenarios.Add(("funding_off", "funding", 0, "no funding (isolates the funding contribution)", dev with { FundingEnabled = false }));
        scenarios.Add(($"funding_x{cfg.Validation.FundingScale.ToString(Inv)}", "funding", cfg.Validation.FundingScale,
            $"every settled rate x {cfg.Validation.FundingScale.ToString(Inv)}", dev with { Costs = dev.Costs with { FundingScale = cfg.Validation.FundingScale } }));
        foreach (var (month, rate) in spikes)
            scenarios.Add(($"funding_spike_{month}", "funding-spike", rate,
                $"every settlement charged against the position at {month}'s mean |rate| ({rate:P4} per 8h)",
                dev with { Costs = dev.Costs with { AdverseFundingRate = rate } }));
        var stress = new StressScenario[scenarios.Count];
        Parallel.For(0, scenarios.Count, parallel, i =>
        {
            var s = scenarios[i];
            stress[i] = new StressScenario(s.Name, s.Group, s.Value, s.Description, RunSummary.From(PerformanceMetrics.Compute(engine.Run(def.Create(p)!, s.Cfg, p))));
        });
        Json.Write(Path.Combine(dir, "stress.json"), new { baseCosts = dev.Costs, scenarios = stress });

        // Parameter perturbations (1D and 2D) around the chosen point.
        var plans = Perturbations(def, p, cfg.Validation.PerturbationSteps);
        var perturbations = new PerturbationRun[plans.Count];
        var perturbationRuns = new BacktestResult?[plans.Count];
        Parallel.For(0, plans.Count, parallel, i =>
        {
            var (kind, prm, steps) = plans[i];
            string pid = $"p{i + 1:D3}";
            try
            {
                if (def.Create(prm) is not { } s) { perturbations[i] = new(pid, kind, prm, steps, false, "invalid parameter combination", null); return; }
                var r = engine.Run(s, dev, prm);
                perturbationRuns[i] = r;
                perturbations[i] = new(pid, kind, prm, steps, true, null, RunSummary.From(PerformanceMetrics.Compute(r)));
            }
            catch (ArgumentException e) { perturbations[i] = new(pid, kind, prm, steps, false, e.Message, null); }
        });
        Json.Write(Path.Combine(dir, "perturbations.json"), new { steps = cfg.Validation.PerturbationSteps, runs = perturbations });
        AppendRobustnessRegistry(Path.Combine(outDir, "robustness_registry.jsonl"), def, perturbations, engine.DataFingerprint);

        // Selection set for CPCV and the local PBO: the strategy's own registered grid, or the perturbation neighbourhood.
        var registered = registry.Where(t => t.Strategy == def.Name && t.IntervalMinutes == cfg.IntervalMinutes).Select(t => t.Parameters).DistinctBy(Key).ToList();
        bool useGrid = registered.Count >= cfg.Validation.MinRegisteredGridForSelection;
        var selection = new List<(string Id, string Source, IReadOnlyDictionary<string, double> P, BacktestResult? Run)> { ("c000", "chosen", p, baseRun) };
        var others = useGrid
            ? registered.Where(r => Key(r) != Key(p)).Select(r => ("registered-grid", (IReadOnlyDictionary<string, double>)r, (BacktestResult?)null))
            : perturbations.Select((x, i) => (x, i)).Where(t => t.x.Valid).Select(t => ("perturbation", t.x.Parameters, perturbationRuns[t.i]));
        foreach (var (source, prm, run) in others) selection.Add(($"c{selection.Count:D3}", source, prm, run));
        var selRuns = new BacktestResult[selection.Count];
        Parallel.For(0, selection.Count, parallel, i =>
        {
            var s = selection[i];
            selRuns[i] = s.Run ?? engine.Run(def.Create(s.P) ?? throw new ArgumentException($"Registered parameters no longer valid: {HoldoutGate.Describe(s.P)}"), dev, s.P);
        });
        var days = DaySeries(selRuns[0]).Days;
        WriteMatrix(Path.Combine(dir, "selection_daily.csv"), days, selection.Select(s => s.Id).ToList(), selRuns.Select(r => DaySeries(r).Returns).ToList());
        var st = new StringBuilder("config,entry_ms,exit_ms,direction,net_pnl,bars_held\n");
        for (int i = 0; i < selection.Count; i++)
            foreach (var t in selRuns[i].Trades)
                st.Append(Inv, $"{selection[i].Id},{t.EntryTimeMs},{t.ExitTimeMs},{t.Direction},{t.NetPnl:R},{t.BarsHeld}\n");
        File.WriteAllText(Path.Combine(dir, "selection_trades.csv"), st.ToString());
        Json.Write(Path.Combine(dir, "selection.json"), new
        {
            source = useGrid ? "registered-grid" : "perturbation",
            configs = selection.Select((s, i) => new { id = s.Id, source = s.Source, parameters = s.P, metrics = RunSummary.From(PerformanceMetrics.Compute(selRuns[i])) }),
        });

        var audit = LookAheadAudit.Run(def, p, intervalMinutes: cfg.IntervalMinutes);
        string? configPath = cfg.SourcePath;
        Json.Write(Path.Combine(dir, "meta.json"), new
        {
            candidate = new { id = id.Id, identity = id },
            strategy = new { def.Name, def.Family, def.Description, def.Version },
            parameters = p,
            intervalMinutes = cfg.IntervalMinutes,
            searchSpace = def.DefaultSpace.Select(s => new { s.Name, s.Min, s.Max, s.Step, values = s.Values() }),
            window = new { devFrom = TimeUtil.Iso(dev.FromMs), devTo = TimeUtil.Iso(dev.ToMs), devFromMs = dev.FromMs, devToMs = dev.ToMs },
            data = new
            {
                bars = new { path = cfg.BarsPath.Replace('\\', '/'), sha256 = HoldoutGate.FileSha256(cfg.BarsPath) },
                funding = new { path = cfg.FundingPath.Replace('\\', '/'), sha256 = HoldoutGate.FileSha256(cfg.FundingPath) },
            },
            config = new { path = configPath?.Replace('\\', '/'), sha256 = configPath is null ? null : HoldoutGate.TextSha256(configPath) },
            metrics = baseMetrics,
            attribution = new
            {
                grossPnl = baseRun.Trades.Sum(t => t.GrossPnl) + baseRun.SlippageCost,
                slippageCost = baseRun.SlippageCost,
                commission = baseRun.FeesPaid,
                fundingReceived = baseRun.FundingReceived,
                netPnl = baseRun.FinalEquity - baseRun.Config.InitialCapital,
            },
            maxBarsHeld = selRuns.SelectMany(r => r.Trades).Select(t => t.BarsHeld).DefaultIfEmpty(0).Max(),
            fundingSpikes = spikes.Select(s => new { month = s.Month, meanAbsRate = s.Rate }),
            registeredTrialsForStrategy = registered.Count,
            lookAheadAudit = audit,
            exportedUtc = DateTime.UtcNow,
            files = new
            {
                equity = "equity.csv", trades = "trades.csv", stress = "stress.json", perturbations = "perturbations.json",
                selection = "selection.json", selectionDaily = "selection_daily.csv", selectionTrades = "selection_trades.csv",
            },
        });
        return dir;
    }

    /// <summary>
    /// 1D: each tunable parameter moved alone; 2D: every pair moved together (all combinations of their moves). Continuous
    /// parameters move by ±each step as a fraction of their value (rounded to whole numbers when the parameter is one).
    /// Discrete ones (small whole numbers, or a value of 0) move to the adjacent values of their search grid.
    /// </summary>
    internal static List<(string Kind, Dictionary<string, double> P, Dictionary<string, PerturbationStep> Steps)> Perturbations(
        StrategyDefinition def, IReadOnlyDictionary<string, double> p, IReadOnlyList<double> steps)
    {
        var moves = new List<(string Name, List<PerturbationStep> Options)>();
        foreach (var spec in def.DefaultSpace)
        {
            double v = p[spec.Name];
            var grid = spec.Values();
            bool whole = v == Math.Round(v) && grid.All(g => g == Math.Round(g));
            var options = new List<PerturbationStep>();
            if (v == 0 || (whole && Math.Abs(v) < 10))
            {
                double? below = grid.Where(g => g < v - 1e-12).Select(g => (double?)g).LastOrDefault();
                double? above = grid.Where(g => g > v + 1e-12).Select(g => (double?)g).FirstOrDefault();
                if (below is double b) options.Add(new(b, null, -1));
                if (above is double u) options.Add(new(u, null, 1));
            }
            else
            {
                foreach (var s in steps.SelectMany(s => new[] { -s, s }).OrderBy(x => x))
                {
                    double x = whole ? Math.Round(v * (1 + s)) : Math.Round(v * (1 + s), 10);
                    if (x != v && options.All(o => o.Value != x)) options.Add(new(x, s, null));
                }
            }
            moves.Add((spec.Name, options));
        }

        var plans = new List<(string, Dictionary<string, double>, Dictionary<string, PerturbationStep>)>();
        Dictionary<string, double> With(params (string Name, double Value)[] changes)
        {
            var q = new Dictionary<string, double>(p);
            foreach (var (n, x) in changes) q[n] = x;
            return q;
        }
        foreach (var (name, options) in moves)
            foreach (var o in options)
                plans.Add(("1d", With((name, o.Value)), new() { [name] = o }));
        for (int i = 0; i < moves.Count; i++)
            for (int j = i + 1; j < moves.Count; j++)
                foreach (var oi in moves[i].Options)
                    foreach (var oj in moves[j].Options)
                        plans.Add(("2d", With((moves[i].Name, oi.Value), (moves[j].Name, oj.Value)), new() { [moves[i].Name] = oi, [moves[j].Name] = oj }));
        return plans;
    }

    /// <summary>Mean |rate| of the funding settled in one calendar month (yyyy-MM), which must lie inside dev.</summary>
    private static double MeanAbsFunding(BacktestEngine engine, string month, HuntConfig cfg)
    {
        var start = DateTime.ParseExact(month, "yyyy-MM", Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        var end = start.AddMonths(1);
        if (start < cfg.Split.DevFrom || end > cfg.Split.DevTo)
            throw new ArgumentException($"Funding spike month {month} is outside the development window; holdout data may not be used.");
        long from = TimeUtil.ToMs(start), to = TimeUtil.ToMs(end);
        var rates = engine.Funding.Where(f => f.TimeMs >= from && f.TimeMs < to).Select(f => Math.Abs(f.Rate)).ToList();
        if (rates.Count == 0) throw new InvalidDataException($"No funding rates in {month}.");
        return rates.Average();
    }

    // ------------------------------------------------------------------ registered trials

    internal sealed record RegisteredTrial(int Id, int Cycle, string Strategy, string Stage, Dictionary<string, double> Parameters, double DailySharpe,
        int? Interval = null)
    {
        public int IntervalMinutes => Interval ?? 60;
    }

    private static List<RegisteredTrial> ReadRegistry(string path) => File.Exists(path)
        ? File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => JsonSerializer.Deserialize<RegisteredTrial>(l, Json.Options)!).ToList()
        : [];

    /// <summary>
    /// Replays every registered trial on dev and writes their daily returns (one column per distinct return series; exact
    /// duplicates, e.g. the same rules registered under two names, point to the first) for the effective number of trials
    /// and CSCV. Replays are not new trials. Each replay's daily Sharpe is compared with the registered one. Every trial
    /// replays on the data of its own bar interval.
    /// </summary>
    private static void ExportTrials(string dir, List<RegisteredTrial> registry, BacktestEngine engine,
        Func<int, (BacktestEngine Engine, BacktestConfig Dev)> engines, ParallelOptions parallel)
    {
        Directory.CreateDirectory(dir);
        var intervals = registry.Select(t => t.IntervalMinutes).Distinct().Order().ToList();
        foreach (int m in intervals) engines(m);   // load each interval's data up front
        var dev = engines(60).Dev;
        var series = new (long[] Days, double[] Returns, double DailySharpe)?[registry.Count];
        var errors = new string?[registry.Count];
        Parallel.For(0, registry.Count, parallel, i =>
        {
            var t = registry[i];
            try
            {
                var def = StrategyRegistry.Get(t.Strategy);
                var (eng, tdev) = engines(t.IntervalMinutes);
                var r = eng.Run(def.Create(t.Parameters) ?? throw new ArgumentException("invalid parameters"), tdev, t.Parameters);
                var (d, ret) = DaySeries(r);
                series[i] = (d, ret, PerformanceMetrics.Compute(r).DailySharpe);
            }
            catch (ArgumentException e) { errors[i] = e.Message; }
        });

        var firstByHash = new Dictionary<string, int>();
        var rows = new StringBuilder("trial_id,cycle,strategy,stage,parameters,registry_daily_sharpe,replay_daily_sharpe,replay_matches,column,duplicate_of,error,interval\n");
        var columns = new List<(string Name, double[] Returns)>();
        long[]? days = null;
        int mismatches = 0;
        for (int i = 0; i < registry.Count; i++)
        {
            var t = registry[i];
            string column = "", duplicateOf = "";
            string replay = "", matches = "";
            if (series[i] is { } s)
            {
                days ??= s.Days;
                if (!s.Days.SequenceEqual(days)) throw new InvalidDataException($"Trial {t.Id} covers different days.");
                bool match = Math.Abs(s.DailySharpe - t.DailySharpe) <= 1e-12 * Math.Max(1, Math.Abs(t.DailySharpe));
                if (!match) mismatches++;
                replay = s.DailySharpe.ToString("R", Inv);
                matches = match ? "true" : "false";
                string hash = Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(s.Returns.AsSpan())));
                if (firstByHash.TryGetValue(hash, out int first)) duplicateOf = $"t{registry[first].Id}";
                else
                {
                    firstByHash[hash] = i;
                    column = $"t{t.Id}";
                    columns.Add((column, s.Returns));
                }
            }
            rows.Append(Inv, $"{t.Id},{t.Cycle},{t.Strategy},{t.Stage},{Csv(JsonSerializer.Serialize(t.Parameters, Json.Compact))},{t.DailySharpe:R},{replay},{matches},{column},{duplicateOf},{Csv(errors[i] ?? "")},{t.IntervalMinutes}\n");
        }
        File.WriteAllText(Path.Combine(dir, "index.csv"), rows.ToString());
        if (days is not null) WriteMatrix(Path.Combine(dir, "daily_returns.csv"), days, columns.Select(c => c.Name).ToList(), columns.Select(c => c.Returns).ToList());
        Json.Write(Path.Combine(dir, "meta.json"), new
        {
            exportedUtc = DateTime.UtcNow,
            codeFingerprint = Backtester.Core.Research.ExperimentIdentity.CurrentCodeFingerprint,
            dataFingerprint = engine.DataFingerprint,
            dataFingerprintsByInterval = intervals.ToDictionary(m => m.ToString(Inv), m => engines(m).Engine.DataFingerprint),
            window = new { devFrom = TimeUtil.Iso(dev.FromMs), devTo = TimeUtil.Iso(dev.ToMs) },
            registered = registry.Count,
            replayed = series.Count(s => s is not null),
            distinctReturnSeries = columns.Count,
            replayMismatches = mismatches,
            failed = errors.Count(e => e is not null),
        });
        Console.WriteLine($"  trials: {registry.Count} registered, {columns.Count} distinct return series, {mismatches} replay mismatches -> {dir.Replace('\\', '/')}");
    }

    // ------------------------------------------------------------------ files

    /// <summary>End-of-UTC-day equity returns (the first day from the initial capital), as in <see cref="PerformanceMetrics.DailyReturns"/>, with each day's start.</summary>
    internal static (long[] Days, double[] Returns) DaySeries(BacktestResult r)
    {
        var days = new List<long>();
        var ends = new List<double>();
        foreach (var p in r.Equity)
        {
            long day = TimeUtil.DayIndex(p.TimeMs - 1);
            if (days.Count == 0 || days[^1] != day) { days.Add(day); ends.Add(p.Equity); }
            else ends[^1] = p.Equity;
        }
        var returns = new double[ends.Count];
        double prev = r.Config.InitialCapital;
        for (int i = 0; i < ends.Count; i++)
        {
            returns[i] = prev > 0 ? ends[i] / prev - 1 : 0;
            prev = ends[i];
        }
        return (days.Select(d => d * DayMs).ToArray(), returns);
    }

    private static void WriteMatrix(string path, long[] days, List<string> names, List<double[]> columns)
    {
        var sb = new StringBuilder("day_ms," + string.Join(',', names) + "\n");
        for (int d = 0; d < days.Length; d++)
        {
            sb.Append(days[d].ToString(Inv));
            foreach (var c in columns) sb.Append(',').Append(c[d].ToString("R", Inv));
            sb.Append('\n');
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteEquity(string path, BacktestResult r)
    {
        var sb = new StringBuilder("time_ms,equity,position_qty\n");
        foreach (var p in r.Equity) sb.Append(Inv, $"{p.TimeMs},{p.Equity:R},{p.PositionQty:R}\n");
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteTrades(string path, IReadOnlyList<Trade> trades)
    {
        var sb = new StringBuilder("entry_ms,exit_ms,direction,entry_price,exit_price,quantity,gross_pnl,fees,funding,net_pnl,exit_reason,bars_held\n");
        foreach (var t in trades)
            sb.Append(Inv, $"{t.EntryTimeMs},{t.ExitTimeMs},{t.Direction},{t.EntryPrice:R},{t.ExitPrice:R},{t.Quantity:R},{t.GrossPnl:R},{t.Fees:R},{t.Funding:R},{t.NetPnl:R},{t.ExitReason},{t.BarsHeld}\n");
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>Records perturbation evaluations once per (strategy, parameters); separate from the hunt's trial registry.</summary>
    private static void AppendRobustnessRegistry(string path, StrategyDefinition def, IEnumerable<PerturbationRun> runs, string dataFingerprint)
    {
        var seen = File.Exists(path)
            ? File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => JsonDocument.Parse(l).RootElement)
                .Select(e => e.GetProperty("strategy").GetString() + "|" + Key(e.GetProperty("parameters").Deserialize<Dictionary<string, double>>(Json.Options)!)).ToHashSet()
            : [];
        var lines = new List<string>();
        foreach (var r in runs.Where(r => r.Valid && seen.Add(def.Name + "|" + Key(r.Parameters))))
            lines.Add(JsonSerializer.Serialize(new
            {
                strategy = def.Name, version = def.Version, parameters = r.Parameters, kind = r.Kind,
                codeFingerprint = Backtester.Core.Research.ExperimentIdentity.CurrentCodeFingerprint, dataFingerprint,
                netProfitPct = r.Metrics!.NetProfitPct, profitFactor = r.Metrics.ProfitFactor, sharpe = r.Metrics.Sharpe, trades = r.Metrics.Trades,
                recordedUtc = DateTime.UtcNow,
            }, Json.Compact));
        if (lines.Count > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllLines(path, lines);
        }
    }

    private static string Key(IReadOnlyDictionary<string, double> p) =>
        string.Join(",", p.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value.ToString("R", Inv)}"));

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
