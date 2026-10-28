using System.Globalization;
using Backtester.Cli;
using Backtester.Core;
using Backtester.Core.Analytics;
using Backtester.Core.Data;
using Backtester.Core.Engine;
using Backtester.Core.Optimization;
using Backtester.Core.Reporting;
using Backtester.Core.Strategies;
using Backtester.Core.Validation;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

try
{
    var args_ = CliArgs.Parse(args);
    return args_.Command switch
    {
        "strategies" => Commands.Strategies(),
        "fetch" => await Commands.Fetch(args_),
        "audit" => Commands.Audit(args_),
        "backtest" => Commands.Backtest(args_),
        "optimize" => Commands.Optimize(args_),
        "walkforward" => Commands.WalkForwardCmd(args_),
        "hunt" => Hunt.Run(args_),
        "report" => Commands.Report(args_),
        "export" => Export.Run(args_),
        "bench" => Bench.Run(args_),
        "research" => Research.Run(args_),
        "validate-export" => ValidateExport.Run(args_),
        _ => Commands.Usage(),
    };
}
catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException or IOException
                              or BybitApiException or HttpRequestException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

namespace Backtester.Cli
{
    internal static class Commands
    {
        public static int Usage()
        {
            Console.WriteLine("""
                Backtester for Bybit USDT linear perps (bar-close signals, next-bar-open fills, funding, costs).

                  strategies                              list strategies and their parameters
                  fetch     [--from 2020-03-25] [--to <now>] [--no-funding]   download klines + funding into data/
                  audit     [--window all|dev|oos | --from --to]      data audit -> artifacts/audit/local_data_audit.json
                  backtest  --strategy NAME [--param k=v ...] [--window dev|oos|all | --from --to]
                            [--slippage-sensitivity] [--unlock-oos]
                  optimize  --strategy NAME [--range k=min:max:step ...] [--param k=v ...] [--window dev]
                            [--objective sharpe|sortino|pf|calmar|net] [--max-evals 200] [--min-trades 0] [--total-trials N]
                  walkforward --strategy NAME [--param k=v ...] [--range k=min:max:step ...] [--window dev]
                            [--train 12] [--test 3] [--step 3] [--fold-min-pf 1.0] [--max-evals 100]
                  hunt      [--strategies a,b,c] [--log hunt_log.json]   one research cycle (rules in config "hunt");
                            defaults to the next untested strategies; runs locked OOS once per surviving candidate
                  research run|resume   cycles until the queue is empty or a limit/stop is hit, then writes the brief
                  research status | stop | brief | verify
                  research log-call --model M --input-tokens N --output-tokens N [--estimated] [--purpose TEXT]
                  research validate --file proposals.json   check proposed hypotheses against the hunt rules
                  report    --run DIR | --walkforward DIR | --cycle N | --hunt | --all    (re)write HTML reports + artifacts/index.html
                            (--hunt: only the all-cycles overview, artifacts/hunt/index.html, which every report refresh also rewrites)
                  export    [--out DIR]   every hunt cycle's results, parameters and settings: hunt_results.xlsx, hunt_strategies.csv,
                            hunt_results.json (default DIR artifacts/export; reads saved reports only)
                            (backtest, optimize, walkforward and hunt write their reports automatically)
                  bench     [--workers N] [--repeat R] [--strategies a,b] [--data synthetic|real]   throughput benchmark
                  validate-export [--profitable | --all | --strategies a,b [--param k=v ...]] [--no-trials]
                            dev-only inputs for python -m validation: per candidate a base run, the cost/funding stress
                            ladder, parameter perturbations, the CPCV selection set and a look-ahead audit; plus every
                            registered trial's daily returns. Default --profitable: hunt hypotheses with a profitable dev baseline

                Common: --config config/hunt.json   --interval M (bar minutes, dividing a day; default the config's
                intervalMinutes = 60): data from data/<symbol>_<M>.csv. Strategy parameters counted in bars stay bar
                counts; days, UTC hours, N-hour periods and funding stay clock time. Trials record their interval and
                replay on their own data. Windows overlapping the locked OOS period need --unlock-oos
                (backtest only, and only with a passing validation report for that exact candidate; every unlocked
                run is appended to artifacts/oos_access.log with the code and data state). optimize and
                walkforward refuse OOS windows outright.
                hunt / research run limits (defaults in config "research"): --workers N --max-experiments N
                --max-cycle-experiments N --max-cycles N --max-minutes M --max-memory-mb N --no-cache
                """);
            return 2;
        }

        public static int Strategies()
        {
            foreach (var d in StrategyRegistry.All)
            {
                Console.WriteLine($"{d.Name}  [{d.Family}]  {d.Description}");
                Console.WriteLine($"  defaults: {string.Join(" ", d.Defaults.Select(kv => $"{kv.Key}={kv.Value}"))}");
                Console.WriteLine($"  default search: {string.Join(" ", d.DefaultSpace.Select(s => $"{s.Name}={s.Min}:{s.Max}:{s.Step}"))}");
            }
            return 0;
        }

        public static async Task<int> Fetch(CliArgs a)
        {
            var cfg = LoadConfig(a);
            long from = TimeUtil.ToMs(a.Date("from") ?? new DateTime(2020, 3, 25, 0, 0, 0, DateTimeKind.Utc));
            long to = TimeUtil.ToMs(a.Date("to") ?? DateTime.UtcNow);
            var progress = new Progress<string>(m => Console.WriteLine($"  {m}"));
            using var client = new BybitClient();

            Console.WriteLine($"Fetching {cfg.Symbol} {cfg.IntervalMinutes}m klines {TimeUtil.Iso(from)} .. {TimeUtil.Iso(to)}");
            var bars = await client.FetchKlinesAsync(cfg.Symbol, cfg.IntervalMinutes, from, to, progress);
            if (File.Exists(cfg.BarsPath)) bars = CsvStore.Normalize(CsvStore.ReadBars(cfg.BarsPath).Concat(bars));
            CsvStore.WriteBars(cfg.BarsPath, bars);
            Console.WriteLine($"Wrote {bars.Count} bars to {cfg.BarsPath}");

            if (a.Has("no-funding")) return 0;   // e.g. other bar intervals: keep the funding file (and data fingerprints) as they are
            Console.WriteLine($"Fetching {cfg.Symbol} funding history");
            var funding = await client.FetchFundingAsync(cfg.Symbol, from, to, progress);
            if (File.Exists(cfg.FundingPath)) funding = CsvStore.Normalize(CsvStore.ReadFunding(cfg.FundingPath).Concat(funding));
            CsvStore.WriteFunding(cfg.FundingPath, funding);
            Console.WriteLine($"Wrote {funding.Count} funding rates to {cfg.FundingPath}");
            return 0;
        }

        public static int Audit(CliArgs a)
        {
            var cfg = LoadConfig(a);
            var (from, to) = ResolveWindow(a, cfg, defaultWindow: "all");
            RequireData(cfg);
            var bars = CsvStore.ReadBars(cfg.BarsPath);
            var funding = CsvStore.ReadFunding(cfg.FundingPath);
            long fromMs = TimeUtil.ToMs(from), toMs = TimeUtil.ToMs(to);

            var barReport = DataAudit.AuditBars(bars, cfg.IntervalMinutes * 60_000L, fromMs, toMs);
            var fundingReport = DataAudit.AuditFunding(funding, 8 * 3_600_000L, fromMs, toMs);
            var path = Path.Combine("artifacts", "audit", "local_data_audit.json");
            Json.Write(path, new
            {
                generatedUtc = DateTime.UtcNow,
                source = "Bybit v5 public API (category=linear)",
                symbol = cfg.Symbol,
                barsFile = cfg.BarsPath,
                fundingFile = cfg.FundingPath,
                bars = barReport,
                funding = fundingReport,
            });

            Console.WriteLine($"Bars     {cfg.BarsPath}  schema: {barReport.Schema}");
            Console.WriteLine($"  rows {barReport.Rows}, {barReport.FirstOpen} .. {barReport.LastOpen}, missing {barReport.MissingBars} of {barReport.ExpectedBars}, " +
                              $"dupes {barReport.DuplicateTimestamps}, out-of-order {barReport.OutOfOrderRows}, bad OHLC {barReport.InvalidOhlcRows}, misaligned {barReport.MisalignedTimestamps}");
            Console.WriteLine($"  window {barReport.WindowFrom} .. {barReport.WindowTo}: {barReport.WindowBars} of {barReport.WindowExpectedBars} bars, " +
                              $"missing {barReport.WindowMissingBars} in {barReport.WindowGaps} gaps");
            foreach (var g in barReport.Gaps.Take(20)) Console.WriteLine($"    gap after {g.After}: {g.MissingSteps} bar(s) missing, resumes {g.Next}");
            if (barReport.Gaps.Count > 20) Console.WriteLine($"    ... {barReport.Gaps.Count - 20} more (see JSON)");
            Console.WriteLine($"Funding  {cfg.FundingPath}  schema: {fundingReport.Schema}");
            Console.WriteLine($"  rows {fundingReport.Rows}, {fundingReport.FirstTime} .. {fundingReport.LastTime}, dupes {fundingReport.DuplicateTimestamps}, " +
                              $"irregular intervals {fundingReport.IrregularIntervals.Count}");
            Console.WriteLine($"  window: {fundingReport.WindowRows} rates, mean {fundingReport.MeanRate:E3}, min {fundingReport.MinRate:E3}, max {fundingReport.MaxRate:E3}");
            Console.WriteLine($"Audit written to {path}");
            return 0;
        }

        public static int Backtest(CliArgs a)
        {
            var cfg = LoadConfig(a);
            var def = StrategyRegistry.Get(a.Required("strategy"));
            var p = StrategyRegistry.Resolve(def, a.Params());
            var (from, to) = ResolveWindow(a, cfg, defaultWindow: "dev");
            var engine = LoadEngine(cfg);
            GuardOos(cfg, from, to, a.Has("unlock-oos"), allowUnlock: true, $"backtest {def.Name} {Fmt(p)}",
                () => CandidateIdentity.Create(def, p, cfg, engine.DataFingerprint));

            var bt = cfg.ToBacktestConfig(from, to);
            var strategy = def.Create(p) ?? throw new ArgumentException($"Invalid parameter combination: {Fmt(p)}");
            var result = engine.Run(strategy, bt, p);
            var metrics = PerformanceMetrics.Compute(result);
            var bench = PerformanceMetrics.Compute(Benchmark.BuyAndHold(engine.Bars, bt));

            List<SlippageRow>? sensitivity = null;
            if (a.Has("slippage-sensitivity"))
            {
                // -100%, -50%, base, +50%, +100% slippage; everything else unchanged
                sensitivity = new[] { 0.0, 0.5, 1.0, 1.5, 2.0 }.Select(mult =>
                {
                    var c = bt with { Costs = bt.Costs with { SlippageRate = bt.Costs.SlippageRate * mult } };
                    var m = PerformanceMetrics.Compute(engine.Run(def.Create(p)!, c, p));
                    return new SlippageRow(mult, c.Costs.SlippageRate, m.NetProfitPct, m.CagrPct, m.MaxDrawdownPct, m.ProfitFactor, m.Sharpe);
                }).ToList();
            }

            var dir = ArtifactWriter.NewRunDir(Path.Combine("artifacts", "runs"), def.Name);
            ArtifactWriter.WriteRun(dir, result, metrics, bench, new { slippageSensitivity = sensitivity });

            Console.WriteLine($"{result.StrategyName}  {metrics.From} .. {metrics.To}");
            PrintMetrics(metrics, bench);
            if (sensitivity is not null)
            {
                Console.WriteLine("\nSlippage sensitivity (x base slippage):");
                foreach (var s in sensitivity)
                    Console.WriteLine($"  x{s.SlippageMultiplier,-4} net {s.NetProfitPct,8:F2}%  CAGR {s.CagrPct,7:F2}%  maxDD {s.MaxDrawdownPct,6:F2}%  PF {s.ProfitFactor,5:F2}  Sharpe {s.Sharpe,5:F2}");
            }
            PrintDiagnostics(result);
            Console.WriteLine($"\nArtifacts: {dir}");
            Console.WriteLine($"Report:    {PublishReport(cfg, engine, w => w.WriteRun(dir))}");
            return 0;
        }

        public static int Optimize(CliArgs a)
        {
            var cfg = LoadConfig(a);
            var def = StrategyRegistry.Get(a.Required("strategy"));
            var fixedParams = StrategyRegistry.Resolve(def, a.Params());
            var space = a.All("range").Select(ParamSpec.Parse).ToList();
            if (space.Count == 0) space = def.DefaultSpace.ToList();
            foreach (var s in space)
                if (!def.Defaults.ContainsKey(s.Name)) throw new ArgumentException($"Strategy '{def.Name}' has no parameter '{s.Name}'.");
            var (from, to) = ResolveWindow(a, cfg, defaultWindow: "dev");
            GuardOos(cfg, from, to, unlock: false, allowUnlock: false, "optimize");

            var engine = LoadEngine(cfg);
            var bt = cfg.ToBacktestConfig(from, to);
            string objective = a.Get("objective") ?? "sharpe";
            var options = new OptimizerOptions { MaxEvaluations = a.Int("max-evals") ?? 200, MinTrades = a.Int("min-trades") ?? 0 };

            var trialLog = new List<object>();
            var report = GridOptimizer.Run(space, prm =>
            {
                var strategy = def.Create(prm);
                if (strategy is null) return null;
                var m = PerformanceMetrics.Compute(engine.Run(strategy, bt, prm));
                trialLog.Add(new { parameters = prm, metrics = m });
                return new TrialOutcome(prm, Objective(m, objective), m.ProfitFactor, m.Sharpe, m.Trades, m.DailySharpe);
            }, options, fixedParams);

            var dir = ArtifactWriter.NewRunDir(Path.Combine("artifacts", "optimizations"), def.Name);
            object? dsr = null;
            if (report.Best is { } best)
            {
                var bestParams = new Dictionary<string, double>(best.Parameters);
                var result = engine.Run(def.Create(bestParams)!, bt, bestParams);
                var m = PerformanceMetrics.Compute(result);
                var bench = PerformanceMetrics.Compute(Benchmark.BuyAndHold(engine.Bars, bt));
                int trials = a.Int("total-trials") ?? report.Evaluations;
                double variance = Statistics.Variance(report.Trials.Select(t => t.DailySharpe).ToList());
                dsr = new
                {
                    trials,
                    trialDailySharpeVariance = variance,
                    expectedMaxDailySharpe = DeflatedSharpe.ExpectedMaxSharpe(trials, variance),
                    deflatedSharpe = DeflatedSharpe.Deflated(m.DailySharpe, m.DailyReturnCount, m.DailySkewness, m.DailyKurtosis, trials, variance),
                    probabilisticSharpeVsZero = DeflatedSharpe.Probabilistic(m.DailySharpe, 0, m.DailyReturnCount, m.DailySkewness, m.DailyKurtosis),
                };
                ArtifactWriter.WriteRun(Path.Combine(dir, "best"), result, m, bench, new { dsr });
                Console.WriteLine($"Best by {objective}: {Fmt(best.Parameters)}");
                PrintMetrics(m, bench);
            }

            Json.Write(Path.Combine(dir, "optimization.json"), new
            {
                strategy = def.Name,
                objective,
                window = new { from, to },
                options,
                report.GridSize,
                report.Sampled,
                report.Evaluations,
                best = report.Best,
                report.BestAtEdgeOf,
                report.PlateauPassed,
                report.Verdict,
                report.Neighbours,
                deflatedSharpe = dsr,
                trials = trialLog,
            });

            Console.WriteLine($"\nGrid {report.GridSize} points, {(report.Sampled ? "sampled" : "exhaustive")}, {report.Evaluations} evaluations");
            Console.WriteLine($"Edge: {(report.BestAtEdgeOf.Count == 0 ? "no" : string.Join(", ", report.BestAtEdgeOf))}   Plateau: {(report.PlateauPassed ? "PASS" : "FAIL")}");
            foreach (var v in report.Verdict) Console.WriteLine($"  - {v}");
            if (dsr is not null) Console.WriteLine($"Deflated Sharpe: {System.Text.Json.JsonSerializer.Serialize(dsr)}");
            Console.WriteLine($"\nArtifacts: {dir}");
            if (report.Best is not null)
                Console.WriteLine($"Report:    {PublishReport(cfg, engine, w => w.WriteRun(Path.Combine(dir, "best")))}");
            return 0;
        }

        public static int WalkForwardCmd(CliArgs a)
        {
            var cfg = LoadConfig(a);
            var def = StrategyRegistry.Get(a.Required("strategy"));
            var fixedParams = StrategyRegistry.Resolve(def, a.Params());
            var space = a.All("range").Select(ParamSpec.Parse).ToList();
            var (from, to) = ResolveWindow(a, cfg, defaultWindow: "dev");
            GuardOos(cfg, from, to, unlock: false, allowUnlock: false, "walkforward");

            var engine = LoadEngine(cfg);
            int train = a.Int("train") ?? 12, test = a.Int("test") ?? 3;
            var folds = WalkForward.Generate(from, to, train, test, a.Int("step"));
            if (folds.Count == 0) throw new ArgumentException("Window too short for a single fold.");
            double foldMinPf = a.Double("fold-min-pf") ?? 1.0;
            var options = new OptimizerOptions { MaxEvaluations = a.Int("max-evals") ?? 100, MinTrades = a.Int("min-trades") ?? 0 };
            string objective = a.Get("objective") ?? "sharpe";

            int totalTrials = 0;
            var rows = new List<FoldRow>();
            Console.WriteLine($"{def.Name}: {folds.Count} folds, train {train}m / test {test}m, {(space.Count > 0 ? "re-optimized per fold" : "fixed parameters")}");
            Console.WriteLine("fold  test window              params                              net%     PF   Sharpe  maxDD%  trades  pass");
            foreach (var f in folds)
            {
                var prm = fixedParams;
                OptimizationReport? opt = null;
                if (space.Count > 0)
                {
                    var trainCfg = cfg.ToBacktestConfig(f.TrainFrom, f.TrainTo);
                    opt = GridOptimizer.Run(space, pp =>
                    {
                        var s = def.Create(pp);
                        if (s is null) return null;
                        var m = PerformanceMetrics.Compute(engine.Run(s, trainCfg, pp));
                        return new TrialOutcome(pp, Objective(m, objective), m.ProfitFactor, m.Sharpe, m.Trades, m.DailySharpe);
                    }, options, fixedParams);
                    totalTrials += opt.Evaluations;
                    if (opt.Best is null)
                    {
                        rows.Add(new FoldRow(f, null, null, false, null, opt.PlateauPassed, opt.BestAtEdgeOf, "no eligible train result"));
                        Console.WriteLine($"{f.Index,4}  {f.TestFrom:yyyy-MM-dd}..{f.TestTo:yyyy-MM-dd}  no eligible train result  NO");
                        continue;
                    }
                    prm = new Dictionary<string, double>(opt.Best.Parameters);
                }

                var testCfg = cfg.ToBacktestConfig(f.TestFrom, f.TestTo);
                var testMetrics = PerformanceMetrics.Compute(engine.Run(def.Create(prm)!, testCfg, prm));
                if (space.Count == 0) totalTrials++;
                bool pass = testMetrics.NetProfit > 0 && testMetrics.ProfitFactor >= foldMinPf;
                rows.Add(new FoldRow(f, prm, testMetrics, pass, opt?.Best, opt?.PlateauPassed, opt?.BestAtEdgeOf, null));
                Console.WriteLine($"{f.Index,4}  {f.TestFrom:yyyy-MM-dd}..{f.TestTo:yyyy-MM-dd}  {Fmt(prm),-34} {testMetrics.NetProfitPct,7:F2} {testMetrics.ProfitFactor,6:F2} {testMetrics.Sharpe,7:F2} {testMetrics.MaxDrawdownPct,7:F2} {testMetrics.Trades,7}  {(pass ? "yes" : "NO")}");
            }

            int failed = rows.Count(r => !r.Pass);
            var dir = ArtifactWriter.NewRunDir(Path.Combine("artifacts", "walkforward"), def.Name);
            Json.Write(Path.Combine(dir, "walkforward.json"), new
            {
                strategy = def.Name, window = new { from, to }, train, test, step = a.Int("step") ?? test, foldMinPf,
                reoptimized = space.Count > 0, space, totalTrials, foldsFailed = failed, folds = rows,
            });
            Console.WriteLine($"\nFolds failed: {failed} of {folds.Count}   trials run: {totalTrials}");
            Console.WriteLine($"Artifacts: {dir}");
            Console.WriteLine($"Report:    {PublishReport(cfg, engine, w => w.WriteWalkForward(dir))}");
            return 0;
        }

        public static int Report(CliArgs a)
        {
            var cfg = LoadConfig(a);
            var writer = new ReportWriter(cfg);
            if (a.Get("run") is { } run) Console.WriteLine(writer.WriteRun(run));
            else if (a.Get("walkforward") is { } wf) Console.WriteLine(writer.WriteWalkForward(wf));
            else if (a.Get("cycle") is { } cycle)
                Console.WriteLine(writer.WriteCycle(int.TryParse(cycle, out var n) ? Path.Combine("artifacts", "hunt", $"cycle_{n}") : cycle));
            else if (a.Has("all")) Console.WriteLine($"Wrote {writer.WriteAll()} reports.");
            else if (!a.Has("hunt")) throw new ArgumentException("Pass --run DIR, --walkforward DIR, --cycle N|DIR, --hunt or --all.");
            Console.WriteLine($"Index: {writer.RefreshIndex()}");
            if (File.Exists(ReportWriter.HuntOverviewPath)) Console.WriteLine($"Hunt:  {Path.GetFullPath(ReportWriter.HuntOverviewPath)}");
            return 0;
        }

        /// <summary>Writes an HTML report with <paramref name="write"/>, refreshes the index, and returns the page path.</summary>
        internal static string PublishReport(HuntConfig cfg, BacktestEngine engine, Func<ReportWriter, string> write)
        {
            var writer = new ReportWriter(cfg, engine.Bars);
            var path = write(writer);
            writer.RefreshIndex();
            return Path.GetFullPath(path);
        }

        // ---- helpers ----

        internal static double Objective(PerformanceMetrics m, string name) => name.ToLowerInvariant() switch
        {
            "sharpe" => m.Sharpe,
            "sortino" => m.Sortino,
            "pf" => m.ProfitFactor,
            "calmar" => m.Calmar,
            "net" => m.NetProfitPct,
            _ => throw new ArgumentException($"Unknown objective '{name}' (sharpe|sortino|pf|calmar|net)"),
        };

        internal static HuntConfig LoadConfig(CliArgs a)
        {
            var path = a.Get("config") ?? Path.Combine("config", "hunt.json");
            if (!File.Exists(path)) throw new FileNotFoundException($"Config not found: {path} (run from the repo root or pass --config)");
            var cfg = HuntConfig.Load(path);
            return a.Int("interval") is int m ? WithInterval(cfg, m) : cfg;
        }

        internal static HuntConfig WithInterval(HuntConfig cfg, int minutes) =>
            minutes > 0 && 1440 % minutes == 0 ? cfg with { IntervalMinutes = minutes }
                : throw new ArgumentException($"--interval {minutes}: bar minutes must divide a day (15, 30, 60, 240, ...).");

        /// <summary>Engines and dev configs per bar interval, loaded on first use (for replaying trials of mixed intervals).</summary>
        internal static Func<int, (BacktestEngine Engine, BacktestConfig Dev)> DevEngines(HuntConfig cfg)
        {
            var cache = new Dictionary<int, (BacktestEngine, BacktestConfig)>();
            return minutes =>
            {
                lock (cache)
                {
                    if (cache.TryGetValue(minutes, out var hit)) return hit;
                    var c = WithInterval(cfg, minutes);
                    return cache[minutes] = (LoadEngine(c), c.ToBacktestConfig(c.Split.DevFrom, c.Split.DevTo));
                }
            };
        }

        private static void RequireData(HuntConfig cfg)
        {
            if (!File.Exists(cfg.BarsPath) || !File.Exists(cfg.FundingPath))
                throw new FileNotFoundException($"Missing {cfg.BarsPath} or {cfg.FundingPath}; run 'fetch' first.");
        }

        internal static BacktestEngine LoadEngine(HuntConfig cfg)
        {
            RequireData(cfg);
            return new BacktestEngine(CsvStore.Normalize(CsvStore.ReadBars(cfg.BarsPath)), CsvStore.Normalize(CsvStore.ReadFunding(cfg.FundingPath)));
        }

        private static (DateTime From, DateTime To) ResolveWindow(CliArgs a, HuntConfig cfg, string defaultWindow)
        {
            var from = a.Date("from");
            var to = a.Date("to");
            if (from is not null || to is not null)
                return (from ?? throw new ArgumentException("--from needs --to"), to ?? throw new ArgumentException("--to needs --from"));
            return (a.Get("window") ?? defaultWindow) switch
            {
                "dev" => (cfg.Split.DevFrom, cfg.Split.DevTo),
                "oos" => (cfg.Split.OosFrom, cfg.Split.OosTo),
                "all" => (cfg.Split.DevFrom, cfg.Split.OosTo),
                var w => throw new ArgumentException($"Unknown window '{w}' (dev|oos|all)"),
            };
        }

        /// <summary>
        /// Refuses windows that touch the locked OOS period, except a <c>backtest --unlock-oos</c> of a candidate whose
        /// validation report passes the holdout gate (unless the gate is disabled in config). Every allowed run is logged.
        /// </summary>
        private static void GuardOos(HuntConfig cfg, DateTime from, DateTime to, bool unlock, bool allowUnlock, string what,
            Func<CandidateIdentity>? candidate = null)
        {
            bool overlaps = from < cfg.Split.OosTo && to > cfg.Split.OosFrom;
            if (!overlaps) return;
            if (!allowUnlock)
                throw new InvalidOperationException($"{what} may not touch the locked OOS period {cfg.Split.OosFrom:yyyy-MM-dd}..{cfg.Split.OosTo:yyyy-MM-dd}.");
            if (!unlock)
                throw new InvalidOperationException($"Window overlaps the locked OOS period {cfg.Split.OosFrom:yyyy-MM-dd}..{cfg.Split.OosTo:yyyy-MM-dd}. " +
                                                    "Pass --unlock-oos only for a candidate's single final validation.");
            GateDecision? decision = null;
            if (cfg.Validation.HoldoutGate)
            {
                decision = HoldoutGate.Check(cfg, (candidate ?? throw new InvalidOperationException($"{what} has no candidate to gate."))());
                if (decision.Status != GateStatus.Passed)
                    throw new InvalidOperationException("Holdout gate refused the locked OOS run:\n  " + string.Join("\n  ", decision.Reasons));
            }
            HoldoutGate.LogAccess(cfg, from, to, what, decision);
        }

        internal static string Fmt(IReadOnlyDictionary<string, double> p) =>
            string.Join(" ", p.Select(kv => $"{kv.Key}={kv.Value.ToString(CultureInfo.InvariantCulture)}"));

        internal static void PrintMetrics(PerformanceMetrics s, PerformanceMetrics b)
        {
            Console.WriteLine($"{"",-18}{"strategy",14}{"buy&hold",14}");
            void Row(string name, double x, double y, string fmt = "F2") => Console.WriteLine($"{name,-18}{x.ToString(fmt),14}{y.ToString(fmt),14}");
            Row("net profit %", s.NetProfitPct, b.NetProfitPct);
            Row("CAGR %", s.CagrPct, b.CagrPct);
            Row("max drawdown %", s.MaxDrawdownPct, b.MaxDrawdownPct);
            Row("profit factor", s.ProfitFactor, b.ProfitFactor);
            Row("Sharpe", s.Sharpe, b.Sharpe);
            Row("Sortino", s.Sortino, b.Sortino);
            Row("Calmar", s.Calmar, b.Calmar);
            Row("trades", s.Trades, b.Trades, "F0");
            Row("trades / yr", s.TradesPerYear, b.TradesPerYear, "F1");
            Row("win rate %", s.WinRatePct, b.WinRatePct, "F1");
            Row("time in market %", s.TimeInMarketPct, b.TimeInMarketPct, "F1");
            Row("turnover / yr (x)", s.TurnoverPerYear, b.TurnoverPerYear, "F1");
            Row("fees paid", s.FeesPaid, b.FeesPaid);
            Row("funding received", s.FundingReceived, b.FundingReceived);
            Row("slippage cost", s.SlippageCost, b.SlippageCost);
        }

        private static void PrintDiagnostics(BacktestResult r)
        {
            if (r.EntriesSkippedNoStop + r.EntriesSkippedStopBreached + r.EntriesSkippedNoVolEstimate + r.DailyLossStopsHit == 0 && !r.Halted) return;
            Console.WriteLine($"\nSkipped entries: no stop {r.EntriesSkippedNoStop}, stop already breached {r.EntriesSkippedStopBreached}, " +
                              $"no vol estimate {r.EntriesSkippedNoVolEstimate}. Daily loss stops: {r.DailyLossStopsHit}. " +
                              $"{(r.Halted ? $"HALTED: {r.HaltReason}" : "")}");
        }
    }

    internal sealed record SlippageRow(
        double SlippageMultiplier, double SlippageRate, double NetProfitPct, double CagrPct, double MaxDrawdownPct, double ProfitFactor, double Sharpe);

    internal sealed record FoldRow(
        Fold Fold,
        IReadOnlyDictionary<string, double>? Parameters,
        PerformanceMetrics? Test,
        bool Pass,
        TrialOutcome? TrainBest,
        bool? TrainPlateau,
        IReadOnlyList<string>? TrainEdge,
        string? Error);

    /// <summary>Minimal parser: a command, an optional subcommand, then --key value pairs; repeated keys collect; bare flags are true.</summary>
    internal sealed class CliArgs
    {
        private static readonly HashSet<string> Flags = ["unlock-oos", "slippage-sensitivity", "all", "hunt", "no-cache", "estimated", "runner", "profitable", "no-trials", "no-funding"];
        private readonly Dictionary<string, List<string>> _opts = new(StringComparer.OrdinalIgnoreCase);

        public string Command { get; private init; } = "";
        /// <summary>A bare word after the command (e.g. "run" in "research run"), or "".</summary>
        public string Sub { get; private init; } = "";

        public static CliArgs Parse(string[] argv)
        {
            bool hasSub = argv.Length > 1 && !argv[1].StartsWith("--");
            var a = new CliArgs { Command = argv.Length > 0 ? argv[0].ToLowerInvariant() : "", Sub = hasSub ? argv[1].ToLowerInvariant() : "" };
            for (int i = hasSub ? 2 : 1; i < argv.Length; i++)
            {
                if (!argv[i].StartsWith("--")) throw new FormatException($"Unexpected argument '{argv[i]}'");
                var key = argv[i][2..];
                string value = Flags.Contains(key) ? "true" : i + 1 < argv.Length ? argv[++i] : throw new FormatException($"--{key} needs a value");
                if (!a._opts.TryGetValue(key, out var list)) a._opts[key] = list = [];
                list.Add(value);
            }
            return a;
        }

        public bool Has(string key) => _opts.ContainsKey(key);
        public string? Get(string key) => _opts.TryGetValue(key, out var v) ? v[^1] : null;
        public string Required(string key) => Get(key) ?? throw new ArgumentException($"--{key} is required");
        public IEnumerable<string> All(string key) => _opts.TryGetValue(key, out var v) ? v : [];
        public int? Int(string key) => Get(key) is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : null;
        public double? Double(string key) => Get(key) is { } s ? double.Parse(s, CultureInfo.InvariantCulture) : null;

        public DateTime? Date(string key) => Get(key) is { } s
            ? DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            : null;

        public Dictionary<string, double> Params()
        {
            var p = new Dictionary<string, double>();
            foreach (var kv in All("param"))
            {
                var parts = kv.Split('=', 2);
                if (parts.Length != 2) throw new FormatException($"Expected --param name=value, got '{kv}'");
                p[parts[0]] = double.Parse(parts[1], CultureInfo.InvariantCulture);
            }
            return p;
        }
    }
}
