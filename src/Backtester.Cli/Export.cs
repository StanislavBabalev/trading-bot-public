using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backtester.Core.Analytics;
using Backtester.Core.Optimization;
using Backtester.Core.Reporting;
using Backtester.Core.Strategies;
using static Backtester.Core.Reporting.Xlsx;

namespace Backtester.Cli;

/// <summary>
/// <c>export</c>: every hunt cycle's results, parameters and settings in one workbook (hunt_results.xlsx), the strategy
/// table as CSV and the full cycle reports as one JSON file. Reads saved artifacts only: no backtest, no network.
/// Audit cycles without a report contribute their step and result only, never their evidence (it can name accounts).
/// </summary>
internal static class Export
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string HuntRoot = "artifacts/hunt";
    private const string StrategiesSheet = "Strategies";

    private sealed class Hyp(JsonObject report, JsonObject h)
    {
        public JsonObject Report { get; } = report;
        public JsonObject H { get; } = h;
        public int Cycle { get; } = (int)Json.Number(report["cycle"]);
        public string Name { get; } = Str(h["strategy"]);
        public bool Optimized { get; } = h["optimizedDev"] is JsonObject;
        public PerformanceMetrics? Baseline { get; } = Metrics(h["baselineDev"]);
        public PerformanceMetrics? Oos { get; } = Metrics(h["lockedOos"]);
        public PerformanceMetrics? Dev { get; } = Metrics(h["optimizedDev"]) ?? Metrics(h["baselineDev"]);
        public List<JsonObject> Folds { get; } = (h["optimizedWalkForward"] as JsonArray ?? h["baselineWalkForward"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        public StrategyDefinition? Registered { get; } = StrategyRegistry.All.FirstOrDefault(d => d.Name == Str(h["strategy"]));
        public double SortKey => Dev?.Sharpe is double s && double.IsFinite(s) ? s : double.NegativeInfinity;
    }

    private sealed record Col(string Header, double Width, Func<Hyp, Cell> Value);

    public static int Run(CliArgs a)
    {
        string outDir = a.Get("out") ?? Path.Combine("artifacts", "export");
        var reports = Directory.Exists(HuntRoot)
            ? Directory.EnumerateFiles(HuntRoot, "report.json", SearchOption.AllDirectories)
                .Select(f => JsonNode.Parse(File.ReadAllText(f))!.AsObject())
                .OrderBy(r => Json.Number(r["cycle"])).ToList()
            : [];
        if (reports.Count == 0)
        {
            Console.Error.WriteLine($"No hunt cycle reports under {HuntRoot}; run a hunt cycle first.");
            return 1;
        }
        var audits = AuditCycles(reports);
        var notes = ReportWriter.ResearchNotes(HuntRoot);
        var hyps = reports.SelectMany(r => (r["hypotheses"] as JsonArray ?? []).OfType<JsonObject>().Select(h => new Hyp(r, h))).ToList();
        var ranked = hyps.OrderByDescending(h => h.SortKey).ToList();
        var exported = DateTime.UtcNow;

        var columns = StrategyColumns(reports[^1], ranked);
        var sheets = new List<Sheet>
        {
            SummarySheet(reports, ranked, columns, exported),
            StrategySheet(ranked, columns),
            ParameterSheet(hyps),
            QuarterSheet(hyps),
            OptimizationSheet(hyps),
            TopTrialsSheet(hyps),
            SlippageSheet(hyps),
            OosSheet(hyps),
            CycleSheet(reports, hyps, audits, notes),
            SettingsSheet(reports),
        };

        Directory.CreateDirectory(outDir);
        string xlsx = Path.Combine(outDir, "hunt_results.xlsx"), csv = Path.Combine(outDir, "hunt_strategies.csv"), json = Path.Combine(outDir, "hunt_results.json");
        Xlsx.Write(xlsx, sheets);
        AtomicFile.WriteAllText(csv, Csv(ranked, columns));
        AtomicFile.WriteAllText(json, FullJson(reports, ranked, audits, notes, exported).ToJsonString(Json.Options));
        Console.WriteLine($"Exported {hyps.Count} hypotheses from {reports.Count} hunt cycles ({string.Join(", ", reports.Select(r => Json.Number(r["cycle"])))}).");
        foreach (var f in new[] { xlsx, csv, json }) Console.WriteLine($"  {Path.GetFullPath(f)}");
        return 0;
    }

    // ------------------------------------------------------------------ sheets

    private static List<Col> StrategyColumns(JsonObject latest, List<Hyp> ranked)
    {
        var rank = ranked.Select((h, i) => (h, i)).ToDictionary(x => x.h, x => x.i + 1);
        var quarters = (latest["walkForwardFolds"] as JsonArray ?? []).OfType<JsonObject>().Select(f => Quarter(Str(f["testFrom"]))).ToList();
        var cols = new List<Col>
        {
            new("Rank (dev Sharpe)", 8, h => Int(rank[h])),
            new("Cycle", 7, h => Int(h.Cycle)),
            new("Strategy", 28, h => Text(h.Name)),
            new("Family", 20, h => Text(Str(h.H["family"]))),
            new("Strategy version", 9, h => Int(h.Registered?.Version)),
            new("Verdict", 11, h => Text(Str(h.H["verdict"]))),
            new("Stopped at", 14, h => Text(Str(h.H["stage"]))),
            new("Kill reasons", 60, h => Text(Join(h.H["killReasons"]))),
            new("Metrics from", 11, h => Text(h.Optimized ? "optimized" : "defaults")),
            new("Parameters used", 40, h => Text(Params(h.Optimized ? h.H["optimizedParameters"] : h.H["defaultParameters"]))),
            new("Default parameters", 40, h => Text(Params(h.H["defaultParameters"]))),
            new("Optimized parameters", 40, h => Text(Params(h.H["optimizedParameters"]))),
            new("Net %", 10, h => Pct(h.Dev?.NetProfitPct)),
            new("CAGR %", 10, h => Pct(h.Dev?.CagrPct)),
            new("Profit factor", 9, h => Num(h.Dev?.ProfitFactor)),
            new("Sharpe", 9, h => Num(h.Dev?.Sharpe)),
            new("Sortino", 9, h => Num(h.Dev?.Sortino)),
            new("Calmar", 9, h => Num(h.Dev?.Calmar)),
            new("Max DD %", 10, h => Pct(h.Dev?.MaxDrawdownPct)),
            new("Trades", 8, h => Int(h.Dev?.Trades)),
            new("Trades / yr", 9, h => Num(h.Dev?.TradesPerYear)),
            new("Win rate %", 10, h => Pct(h.Dev?.WinRatePct)),
            new("Avg trade $", 11, h => Money(h.Dev?.AvgTradeNetPnl)),
            new("Time in market %", 10, h => Pct(h.Dev?.TimeInMarketPct)),
            new("Turnover / yr", 9, h => Num(h.Dev?.TurnoverPerYear)),
            new("Fees $", 11, h => Money(h.Dev?.FeesPaid)),
            new("Funding received $", 11, h => Money(h.Dev?.FundingReceived)),
            new("Slippage cost $", 11, h => Money(h.Dev?.SlippageCost)),
            new("Final equity $", 12, h => Money(h.Dev?.FinalEquity)),
        };
        for (int k = 0; k < quarters.Count; k++)
        {
            int fold = k;
            cols.Add(new($"{quarters[k]} net %", 10, h => Pct(fold < h.Folds.Count ? Json.Number(h.Folds[fold]["netProfitPct"]) : null)));
        }
        cols.AddRange(
        [
            new($"Quarters passed (of {quarters.Count})", 10, h => Int(h.Folds.Count == 0 ? null : h.Folds.Count(f => f["pass"]?.GetValue<bool>() == true))),
            new("Defaults: net %", 10, h => Pct(h.Baseline?.NetProfitPct)),
            new("Defaults: PF", 9, h => Num(h.Baseline?.ProfitFactor)),
            new("Defaults: Sharpe", 9, h => Num(h.Baseline?.Sharpe)),
            new("OOS net %", 10, h => Pct(h.Oos?.NetProfitPct)),
            new("OOS PF", 9, h => Num(h.Oos?.ProfitFactor)),
            new("OOS Sharpe", 9, h => Num(h.Oos?.Sharpe)),
            new("OOS max DD %", 10, h => Pct(h.Oos?.MaxDrawdownPct)),
            new("OOS trades", 8, h => Int(h.Oos?.Trades)),
            new("OOS win rate %", 10, h => Pct(h.Oos?.WinRatePct)),
            new("Rules", 100, h => Text(Str(h.H["rules"]))),
            new("Dev run folder", 52, h => Text(Str(h.H["artifacts"]?[h.Optimized ? "optimizedDev" : "baselineDev"]))),
            new("OOS run folder", 52, h => Text(Str(h.H["artifacts"]?["lockedOos"]))),
            new("Cycle report", 34, h => Text($"{HuntRoot}/cycle_{h.Cycle}/report.html")),
        ]);
        return cols;
    }

    private static Sheet StrategySheet(List<Hyp> ranked, List<Col> cols)
    {
        var s = new Sheet(StrategiesSheet) { FreezeRows = 1, FreezeCols = 3, FilterRow = 0 };
        for (int c = 0; c < cols.Count; c++) s.Widths[c] = cols[c].Width;
        s.Add(cols.Select(c => Header(c.Header)).ToArray());
        foreach (var h in ranked) s.Add(cols.Select(c => c.Value(h)).ToArray());
        return s;
    }

    private static Sheet SummarySheet(List<JsonObject> reports, List<Hyp> ranked, List<Col> cols, DateTime exported)
    {
        var s = new Sheet("Summary");
        double[] widths = [34, 24, 8, 11, 9, 9, 11, 10, 11, 11, 70];
        for (int c = 0; c < widths.Length; c++) s.Widths[c] = widths[c];
        var latest = reports[^1];
        int first = (int)Json.Number(reports[0]["cycle"]), last = (int)Json.Number(latest["cycle"]);
        int cumulative = reports.Max(r => (int)Json.Number(r["trials"]?["cumulative"]));
        double variance = Json.Number(latest["trials"]?["dailySharpeVariance"]);
        double luck = DeflatedSharpe.ExpectedMaxSharpe(cumulative, variance) * Math.Sqrt(365);
        var bhDev = Metrics(latest["buyAndHold"]?["dev"]);
        // Only a cycle that made a locked OOS run reports buy & hold over that window.
        var bhOos = reports.Select(r => Metrics(r["buyAndHold"]?["oos"])).LastOrDefault(m => m is not null);

        // Live formulas over the Strategies sheet, with their values cached for viewers that do not recalculate.
        string Range(string header)
        {
            int c = cols.FindIndex(x => x.Header == header);
            return $"{SheetRef(StrategiesSheet)}${Column(c)}$2:${Column(c)}${ranked.Count + 1}";
        }
        string sharpe = Range("Sharpe"), name = Range("Strategy"), cycle = Range("Cycle");
        var best = ranked.FirstOrDefault(h => double.IsFinite(h.SortKey));

        s.Add(new Cell("Hunt results export", Style.Title));
        s.Add($"Exported {exported:yyyy-MM-dd HH:mm} UTC from {HuntRoot}/cycle_*/report.json. A snapshot of saved results: re-run `dotnet run --project src/Backtester.Cli -- export` to refresh.");
        s.Add();
        s.Add(Header("Headline"), Header("Value"), Header("Notes"));
        s.Add("Hunt cycles with results", Text($"{first} → {last} ({reports.Count} cycles)"), "Cycles 1–2 were the data and engine audits (see Cycles).");
        s.Add("Hypotheses tested", new Cell(ranked.Count, Style.Integer, $"COUNTA({name})"), "One row each on the Strategies sheet.");
        s.Add("Passed every rule", new Cell(ranked.Count(h => Str(h.H["verdict"]) == "PASSED"), Style.Integer, $"COUNTIF({Range("Verdict")},\"PASSED\")"),
              "A pass needs every kill rule, a plateau optimum and the single locked out-of-sample run.");
        s.Add("Reached the locked OOS run", new Cell(ranked.Count(h => h.Oos is not null), Style.Integer, $"COUNT({Range("OOS net %")})"), "Out-of-sample window used once per surviving candidate.");
        s.Add("Champion", Text(latest["champion"]?["strategy"]?.GetValue<string>() ?? "none"), "From the latest cycle report.");
        s.Add("Parameter sets evaluated (cumulative)", Int(cumulative), "Every optimizer evaluation counts; it deflates the Sharpe bar.");
        s.Add("Luck bar (annualized Sharpe)", Num(luck), $"Best Sharpe expected from {cumulative} trials with no real edge (deflated-Sharpe expected maximum).");
        s.Add("Best development Sharpe", new Cell(best?.SortKey, Style.Number2, $"MAX({sharpe})"), "On 2021–2023 data; kill bar is 0.50.");
        s.Add("  achieved by", new Cell(best?.Name, Style.Default, $"INDEX({name},MATCH(MAX({sharpe}),{sharpe},0))"));
        s.Add("  in cycle", new Cell(best?.Cycle, Style.Integer, $"INDEX({cycle},MATCH(MAX({sharpe}),{sharpe},0))"));
        if (bhDev is not null)
            s.Add("Buy & hold, development window", Pct(bhDev.NetProfitPct), Text($"net · Sharpe {bhDev.Sharpe.ToString("0.00", Inv)} · max DD {bhDev.MaxDrawdownPct.ToString("0.0", Inv)}% · {Day(bhDev.From)} → {Day(bhDev.To)}"));
        if (bhOos is not null)
            s.Add("Buy & hold, locked OOS window", Pct(bhOos.NetProfitPct), Text($"net · Sharpe {bhOos.Sharpe.ToString("0.00", Inv)} · max DD {bhOos.MaxDrawdownPct.ToString("0.0", Inv)}% · {Day(bhOos.From)} → {Day(bhOos.To)}"));
        s.Add();

        s.Add(new Cell("Top 10 by development Sharpe", Style.Bold));
        string[] top = ["Strategy", "Family", "Cycle", "Net %", "Profit factor", "Sharpe", "Max DD %", "Trades / yr", cols.First(c => c.Header.StartsWith("Quarters passed")).Header, "OOS net %", "Kill reasons"];
        s.Add(top.Select(Header).ToArray());
        for (int i = 0; i < Math.Min(10, ranked.Count); i++)
        {
            int row = i + 2;
            s.Add(top.Select(header =>
            {
                int c = cols.FindIndex(x => x.Header == header);
                var v = cols[c].Value(ranked[i]);
                return v with { Formula = $"IF({SheetRef(StrategiesSheet)}{Column(c)}{row}=\"\",\"\",{SheetRef(StrategiesSheet)}{Column(c)}{row})" };
            }).ToArray());
        }
        s.Add();

        s.Add(new Cell("Sheets", Style.Bold));
        foreach (var (sheet, what) in new[]
        {
            (StrategiesSheet, "One row per hypothesis, ranked by development Sharpe: verdict, kill reasons, parameters, every dev metric, the walk-forward quarters, the locked OOS result, rules and artifact folders. Filter and sort freely."),
            ("Parameters", "Every parameter of every hypothesis: default, optimized value, and its search grid (searched in the cycle, or the registry grid when rejected before optimization)."),
            ("Quarters", "The four walk-forward test quarters per hypothesis, for default and optimized parameters, with train and test windows."),
            ("Optimization", "Grid search summary for hypotheses that were optimized: grid size, evaluations, edge and plateau checks, best point."),
            ("Top trials", "The ten best parameter sets and the neighbours of the optimum for each optimized hypothesis."),
            ("Slippage", "Slippage sensitivity of optimized candidates (0x to 2x the base slippage)."),
            ("Locked OOS", "Development vs locked out-of-sample vs buy & hold for every candidate that reached the OOS run, plus trade distributions."),
            ("Cycles", "Per cycle: date, hypotheses, trial counts, luck bar, champion, buy & hold, warnings, report and research-notes paths."),
            ("Settings", "Data, costs, sizing, risk, windows, engine conventions and the hunt rules every cycle used."),
        })
            s.Add(Text(sheet), Text(what));
        s.Add();

        s.Add(new Cell("How to read", Style.Bold));
        s.Add("Development metrics use the optimized parameters where optimization ran, otherwise the defaults (column 'Metrics from').");
        s.Add("A walk-forward quarter passes only if it makes money and its profit factor is at least the fold minimum; more than one failed quarter kills a hypothesis.");
        s.Add("Percent columns are stored as fractions and formatted as %. Money is in USD on a $10,000 starting balance.");
        s.Add("Max drawdown is measured on hourly bar-close equity; intrabar drawdowns are deeper.");
        s.Add("Full trade lists and equity curves are in each run folder (trades.csv, equity.csv, result.json).");
        return s;
    }

    private static Sheet ParameterSheet(List<Hyp> hyps)
    {
        var s = new Sheet("Parameters") { FreezeRows = 1, FreezeCols = 3, FilterRow = 0 };
        double[] widths = [7, 28, 16, 10, 10, 12, 9, 9, 9, 8, 60, 44];
        for (int c = 0; c < widths.Length; c++) s.Widths[c] = widths[c];
        s.Add(new[] { "Cycle", "Strategy", "Parameter", "Default", "Optimized", "Used for metrics", "Grid min", "Grid max", "Grid step", "Grid points", "Grid values", "Grid source" }.Select(Header).ToArray());
        foreach (var h in hyps)
        {
            var defaults = h.H["defaultParameters"] as JsonObject ?? [];
            var optimized = h.H["optimizedParameters"] as JsonObject;
            var searched = (h.H["optimization"]?["space"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(p => new ParamSpec(Str(p["name"]), Json.Number(p["min"]), Json.Number(p["max"]), Json.Number(p["step"]))).ToList();
            var registry = h.Registered?.DefaultSpace ?? [];
            var names = defaults.Select(kv => kv.Key).Concat(optimized?.Select(kv => kv.Key) ?? []).Concat(searched.Select(p => p.Name)).Distinct().ToList();
            foreach (var p in names)
            {
                var spec = searched.FirstOrDefault(x => x.Name == p) ?? registry.FirstOrDefault(x => x.Name == p);
                string source = searched.Any(x => x.Name == p) ? "searched in this cycle"
                    : spec is not null ? "registry grid (not searched: rejected before optimization)"
                    : "fixed (not tuned)";
                double? def = defaults[p] is JsonNode d ? Json.Number(d) : null;
                double? opt = optimized?[p] is JsonNode o ? Json.Number(o) : null;
                var values = spec?.Values();
                s.Add(Int(h.Cycle), Text(h.Name), Text(p), Raw(def), Raw(opt), Raw(opt ?? def),
                      Raw(spec?.Min), Raw(spec?.Max), Raw(spec?.Step), Int(values?.Count),
                      Text(values is null ? null : string.Join(", ", values.Select(Fmt))), Text(source));
            }
        }
        return s;
    }

    private static Sheet QuarterSheet(List<Hyp> hyps)
    {
        var s = new Sheet("Quarters") { FreezeRows = 1, FreezeCols = 2, FilterRow = 0 };
        double[] widths = [7, 28, 12, 6, 11, 11, 11, 11, 10, 9, 9, 10, 8, 8];
        for (int c = 0; c < widths.Length; c++) s.Widths[c] = widths[c];
        s.Add(new[] { "Cycle", "Strategy", "Parameters", "Fold", "Train from", "Train to (excl.)", "Test from", "Test to (excl.)", "Net %", "Profit factor", "Sharpe", "Max DD %", "Trades", "Passed" }.Select(Header).ToArray());
        foreach (var h in hyps)
        {
            var windows = (h.Report["walkForwardFolds"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(f => (int)Json.Number(f["index"]));
            foreach (var (key, label) in new[] { ("baselineWalkForward", "defaults"), ("optimizedWalkForward", "optimized") })
                foreach (var f in (h.H[key] as JsonArray ?? []).OfType<JsonObject>())
                {
                    int fold = (int)Json.Number(f["fold"]);
                    var w = windows.GetValueOrDefault(fold);
                    s.Add(Int(h.Cycle), Text(h.Name), Text(label), Int(fold), Text(Day(Str(w?["trainFrom"]))), Text(Day(Str(w?["trainTo"]))),
                          Text(Day(Str(f["testFrom"]))), Text(Day(Str(f["testTo"]))), Pct(Json.Number(f["netProfitPct"])), Num(Json.Number(f["profitFactor"])),
                          Num(Json.Number(f["sharpe"])), Pct(Json.Number(f["maxDrawdownPct"])), Int(Json.Number(f["trades"])),
                          Text(f["pass"]?.GetValue<bool>() == true ? "yes" : "no"));
                }
        }
        return s;
    }

    private static Sheet OptimizationSheet(List<Hyp> hyps)
    {
        var s = new Sheet("Optimization") { FreezeRows = 1, FreezeCols = 2, FilterRow = 0 };
        double[] widths = [7, 28, 9, 9, 9, 8, 9, 16, 9, 30, 40, 9, 9, 8, 10, 14, 50];
        for (int c = 0; c < widths.Length; c++) s.Widths[c] = widths[c];
        s.Add(new[] { "Cycle", "Strategy", "Objective", "Grid points", "Evaluated", "Sampled", "Enough trades", "Best at edge of", "Plateau passed",
                      "Optimizer verdict", "Best parameters", "Best Sharpe", "Best PF", "Best trades", "Best daily Sharpe", "Stopped at", "Kill reasons" }.Select(Header).ToArray());
        foreach (var h in hyps)
        {
            if (h.H["optimization"] is not JsonObject o) continue;
            var b = o["best"];
            s.Add(Int(h.Cycle), Text(h.Name), Text(Str(o["objective"])), Int(Json.Number(o["gridSize"])), Int(Json.Number(o["evaluations"])),
                  Text(o["sampled"]?.GetValue<bool>() == true ? "yes" : "no"), Int(Json.Number(o["eligibleTrials"])), Text(Join(o["bestAtEdgeOf"])),
                  Text(o["plateauPassed"]?.GetValue<bool>() == true ? "yes" : "no"), Text(Join(o["verdict"])), Text(Params(b?["parameters"])),
                  Num(Json.Number(b?["sharpe"])), Num(Json.Number(b?["profitFactor"])), Int(Json.Number(b?["trades"])), new Cell(Json.Number(b?["dailySharpe"]), Style.General),
                  Text(Str(h.H["stage"])), Text(Join(h.H["killReasons"])));
        }
        return s;
    }

    private static Sheet TopTrialsSheet(List<Hyp> hyps)
    {
        var s = new Sheet("Top trials") { FreezeRows = 1, FreezeCols = 2, FilterRow = 0 };
        double[] widths = [7, 28, 12, 5, 44, 9, 9, 8, 10];
        for (int c = 0; c < widths.Length; c++) s.Widths[c] = widths[c];
        s.Add(new[] { "Cycle", "Strategy", "Set", "#", "Parameters", "Sharpe", "Profit factor", "Trades", "Daily Sharpe" }.Select(Header).ToArray());
        foreach (var h in hyps)
        {
            if (h.H["optimization"] is not JsonObject o) continue;
            foreach (var (key, label) in new[] { ("top10", "top 10"), ("neighbours", "neighbour") })
            {
                int n = 0;
                foreach (var t in (o[key] as JsonArray ?? []).OfType<JsonObject>())
                    s.Add(Int(h.Cycle), Text(h.Name), Text(label), Int(++n), Text(Params(t["parameters"])), Num(Json.Number(t["sharpe"])),
                          Num(Json.Number(t["profitFactor"])), Int(Json.Number(t["trades"])), new Cell(Json.Number(t["dailySharpe"]), Style.General));
            }
        }
        return s;
    }

    private static Sheet SlippageSheet(List<Hyp> hyps)
    {
        var s = new Sheet("Slippage") { FreezeRows = 1, FreezeCols = 2, FilterRow = 0 };
        double[] widths = [7, 28, 12, 12, 10, 10, 10, 9, 9];
        for (int c = 0; c < widths.Length; c++) s.Widths[c] = widths[c];
        s.Add(new[] { "Cycle", "Strategy", "x base slippage", "Slippage / side %", "Net %", "CAGR %", "Max DD %", "Profit factor", "Sharpe" }.Select(Header).ToArray());
        foreach (var h in hyps)
            foreach (var r in (h.H["devSlippageSensitivity"] as JsonArray ?? []).OfType<JsonObject>())
                s.Add(Int(h.Cycle), Text(h.Name), Num(Json.Number(r["slippageMultiplier"])), new Cell(Json.Number(r["slippageRate"]), Style.Percent),
                      Pct(Json.Number(r["netProfitPct"])), Pct(Json.Number(r["cagrPct"])), Pct(Json.Number(r["maxDrawdownPct"])),
                      Num(Json.Number(r["profitFactor"])), Num(Json.Number(r["sharpe"])));
        return s;
    }

    private static Sheet OosSheet(List<Hyp> hyps)
    {
        var s = new Sheet("Locked OOS") { FreezeRows = 1, FilterRow = 0 };
        double[] widths = [7, 28, 18, 26, 18, 18, 18];
        for (int c = 0; c < widths.Length; c++) s.Widths[c] = widths[c];
        s.Add(new[] { "Cycle", "Strategy", "Section", "Metric", "Development", "Locked OOS", "Buy & hold (OOS)" }.Select(Header).ToArray());
        foreach (var h in hyps.Where(x => x.Oos is not null))
        {
            var dev = h.Dev!;
            var oos = h.Oos!;
            var bh = Metrics(h.Report["buyAndHold"]?["oos"]);
            void Row(string section, string metric, Cell d, Cell o, Cell b) => s.Add(Int(h.Cycle), Text(h.Name), Text(section), Text(metric), d, o, b);
            Row("Run", "Parameters", Text(Params(h.H["optimizedParameters"] ?? h.H["defaultParameters"])), Text(Params(h.H["optimizedParameters"] ?? h.H["defaultParameters"])), Text(null));
            Row("Run", "Window", Text($"{Day(dev.From)} → {Day(dev.To)}"), Text($"{Day(oos.From)} → {Day(oos.To)}"), Text(bh is null ? null : $"{Day(bh.From)} → {Day(bh.To)}"));
            Row("Run", "Outcome", Text(Str(h.H["verdict"])), Text(Join(h.H["killReasons"])), Text(null));
            foreach (var (metric, f) in new (string, Func<PerformanceMetrics, Cell>)[]
            {
                ("Net %", m => Pct(m.NetProfitPct)), ("CAGR %", m => Pct(m.CagrPct)), ("Profit factor", m => Num(m.ProfitFactor)),
                ("Sharpe", m => Num(m.Sharpe)), ("Sortino", m => Num(m.Sortino)), ("Max DD %", m => Pct(m.MaxDrawdownPct)),
                ("Trades", m => Int(m.Trades)), ("Trades / yr", m => Num(m.TradesPerYear)), ("Win rate %", m => Pct(m.WinRatePct)),
                ("Avg trade $", m => Money(m.AvgTradeNetPnl)), ("Time in market %", m => Pct(m.TimeInMarketPct)), ("Fees $", m => Money(m.FeesPaid)),
                ("Funding received $", m => Money(m.FundingReceived)), ("Final equity $", m => Money(m.FinalEquity)),
            })
                Row("Performance", metric, f(dev), f(oos), bh is null ? Text(null) : f(bh));
            var dd = h.H["devTradeDistribution"] as JsonObject;
            var od = h.H["oosTradeDistribution"] as JsonObject;
            foreach (var (key, label) in new[]
            {
                ("count", "Trades"), ("meanPct", "Mean trade %"), ("medianPct", "Median trade %"), ("p05Pct", "5th percentile %"), ("p25Pct", "25th percentile %"),
                ("p75Pct", "75th percentile %"), ("p95Pct", "95th percentile %"), ("worstPct", "Worst trade %"), ("bestPct", "Best trade %"),
                ("avgBarsHeld", "Avg bars held"), ("winRatePct", "Win rate %"),
            })
            {
                Cell Dist(JsonObject? d) => d is null ? Text(null) : key.EndsWith("Pct") ? Pct(Json.Number(d[key])) : key == "count" ? Int(Json.Number(d[key])) : Num(Json.Number(d[key]));
                Row("Trade distribution", label, Dist(dd), Dist(od), Text(null));
            }
            if (h.H["dsr"] is JsonObject dsr)
                foreach (var kv in dsr) Row("Deflated Sharpe", kv.Key, Text(null), kv.Value is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? new Cell(Json.Number(v), Style.General) : Text(Str(kv.Value)), Text(null));
        }
        if (s.Rows.Count == 1) s.Add(Text("No hypothesis has reached the locked out-of-sample run yet."));
        return s;
    }

    private static Sheet CycleSheet(List<JsonObject> reports, List<Hyp> hyps, List<JsonObject> audits, Func<int, string?> notes)
    {
        var s = new Sheet("Cycles") { FreezeRows = 1, FreezeCols = 1, FilterRow = 0 };
        double[] widths = [7, 18, 8, 34, 11, 70, 9, 9, 11, 12, 12, 10, 12, 10, 10, 10, 34, 40, 100];
        for (int c = 0; c < widths.Length; c++) s.Widths[c] = widths[c];
        s.Add(new[] { "Cycle", "Run (UTC)", "Kind", "Step / result", "Hypotheses", "Strategies", "Trials before", "Trials this cycle", "Cumulative trials",
                      "Daily Sharpe variance", "Luck bar (ann. Sharpe)", "Reached OOS", "Champion", "B&H dev net %", "B&H dev Sharpe", "B&H dev max DD %",
                      "Report", "Research notes", "Warnings" }.Select(Header).ToArray());
        foreach (var a in audits)
            s.Add(Int(Json.Number(a["cycle"])), Text(Time(Str(a["timestampUtc"]))), Text("audit"), Text($"{Str(a["step"])}: {Str(a["result"])}"));
        foreach (var r in reports)
        {
            int cycle = (int)Json.Number(r["cycle"]);
            var mine = hyps.Where(h => h.Cycle == cycle).ToList();
            int cumulative = (int)Json.Number(r["trials"]?["cumulative"]);
            double variance = Json.Number(r["trials"]?["dailySharpeVariance"]);
            var bh = Metrics(r["buyAndHold"]?["dev"]);
            s.Add(Int(cycle), Text(Time(Str(r["timestampUtc"]))), Text("hunt"), Text($"{mine.Count} hypotheses, {mine.Count(h => Str(h.H["verdict"]) == "PASSED")} passed"),
                  Int(mine.Count), Text(string.Join(", ", mine.Select(h => h.Name))), Int(Json.Number(r["trials"]?["before"])), Int(Json.Number(r["trials"]?["thisCycle"])),
                  Int(cumulative), new Cell(variance, Style.General), Num(DeflatedSharpe.ExpectedMaxSharpe(cumulative, variance) * Math.Sqrt(365)),
                  Int(mine.Count(h => h.Oos is not null)), Text(r["champion"]?["strategy"]?.GetValue<string>() ?? "none"),
                  Pct(bh?.NetProfitPct), Num(bh?.Sharpe), Pct(bh?.MaxDrawdownPct), Text($"{HuntRoot}/cycle_{cycle}/report.html"),
                  Text(notes(cycle)?.Replace('\\', '/')), Text(Join(r["warnings"])));
        }
        return s;
    }

    private static Sheet SettingsSheet(List<JsonObject> reports)
    {
        var s = new Sheet("Settings") { FreezeRows = 1 };
        double[] widths = [16, 34, 28, 100];
        for (int c = 0; c < widths.Length; c++) s.Widths[c] = widths[c];
        var latest = reports[^1];
        var k = latest["constants"] as JsonObject ?? [];
        var rules = latest["rules"] as JsonObject ?? [];
        var wf = rules["walkForward"] as JsonObject ?? [];
        string Signature(JsonObject r) => (r["constants"]?.ToJsonString() ?? "") + (r["rules"]?.ToJsonString() ?? "") + (r["walkForwardFolds"]?.ToJsonString() ?? "") + Str(r["minDevTrades"]);
        var differs = reports.Where(r => Signature(r) != Signature(latest)).Select(r => (int)Json.Number(r["cycle"])).ToList();

        s.Add(new[] { "Section", "Setting", "Value", "Notes" }.Select(Header).ToArray());
        void Row(string section, string setting, Cell value, string? note = null) => s.Add(Text(section), Text(setting), value, Text(note));
        Row("Consistency", "Same settings in every cycle", Text(differs.Count == 0 ? "yes" : "no"),
            differs.Count == 0 ? $"Constants, rules and walk-forward folds are identical in cycles {Json.Number(reports[0]["cycle"])}–{Json.Number(latest["cycle"])}; values below are from cycle {Json.Number(latest["cycle"])}."
                               : $"Cycles {string.Join(", ", differs)} differ from cycle {Json.Number(latest["cycle"])}; see each cycle's report.json.");
        Row("Data", "Symbol", Text(Str(k["symbol"])), "Bybit USDT perpetual, public market data fetched locally by `fetch`.");
        Row("Data", "Bar interval (minutes)", Int(Json.Number(k["intervalMinutes"])));
        Row("Windows", "Development from", Text(Day(Str(k["split"]?["devFrom"]))), "Optimization and all kill rules use this window only.");
        Row("Windows", "Development to (exclusive)", Text(Day(Str(k["split"]?["devTo"]))));
        Row("Windows", "Locked OOS from", Text(Day(Str(k["split"]?["oosFrom"]))), "Used once per surviving candidate; every access is logged in artifacts/oos_access.log.");
        Row("Windows", "Locked OOS to (exclusive)", Text(Day(Str(k["split"]?["oosTo"]))));
        Row("Account", "Initial capital $", Money(Json.Number(k["initialCapital"])), "Every run starts flat with this balance.");
        Row("Costs", "Commission per side", new Cell(Json.Number(k["costs"]?["commissionRate"]), Style.Percent), "Fraction of notional, charged on entry and exit.");
        Row("Costs", "Slippage per side", new Cell(Json.Number(k["costs"]?["slippageRate"]), Style.Percent), "Applied against the trade on every fill.");
        Row("Costs", "Funding", Text(k["fundingEnabled"]?.GetValue<bool>() == true ? "on" : "off"), "8-hourly Bybit funding rates, settled at bar open on the position held.");
        Row("Sizing", "Mode", Text(Str(k["sizing"]?["mode"])), "Every entry needs a stop; size is set so the stop loses this fraction of equity.");
        Row("Sizing", "Risk per trade", new Cell(Json.Number(k["sizing"]?["riskPerTrade"]), Style.Percent));
        Row("Risk", "Max leverage", Num(Json.Number(k["risk"]?["maxLeverage"])), "Position notional is capped at this multiple of equity.");
        Row("Risk", "Max-drawdown stop", Text(k["risk"]?["maxDrawdownStop"] is JsonNode dd ? Str(dd) : "off"));
        Row("Risk", "Daily-loss stop", Text(k["risk"]?["dailyLossStop"] is JsonNode dl ? Str(dl) : "off"));
        Row("Engine", "Fills", Text("next bar open"), "Signals are taken on a bar's close and filled at the next bar's open; never on the same bar.");
        Row("Engine", "Stops", Text("intrabar"), "Stops rest inside the bar and fill when price trades through them.");
        Row("Engine", "Max drawdown", Text("bar-close equity"), "Measured on hourly bar-close equity; intrabar drawdowns are deeper.");
        Row("Hunt rules", "Hypotheses per cycle", Int(Json.Number(rules["hypothesesPerCycle"])));
        Row("Hunt rules", "Optimizer evaluation cap", Int(Json.Number(rules["optimizerMaxEvaluations"])), "Grids that fit under the cap (minus 2 neighbours per parameter) are searched exhaustively.");
        Row("Kill rules", "Min profit factor", Num(Json.Number(rules["minProfitFactor"])));
        Row("Kill rules", "Min Sharpe (annualized)", Num(Json.Number(rules["minSharpe"])));
        Row("Kill rules", "Max drawdown", new Cell(Json.Number(rules["maxDrawdownPct"]) / 100, Style.Percent));
        Row("Kill rules", "Min trades per year", Int(Json.Number(rules["minTradesPerYear"])), $"{Json.Number(latest["minDevTrades"])} trades on the development window.");
        Row("Kill rules", "Max failed walk-forward quarters", Int(Json.Number(wf["maxFoldFailures"])),
            $"A quarter fails when it loses money or its profit factor is below {Json.Number(wf["foldMinProfitFactor"]).ToString("0.##", Inv)}.");
        Row("Kill rules", "Optimum on a grid edge", Text("rejected"), "The best point must be interior so its neighbours can be checked.");
        Row("Kill rules", "Plateau min profit factor", Num(Json.Number(rules["plateauMinProfitFactor"])), "Neighbours of the optimum (±1 grid step) must keep at least this.");
        Row("Kill rules", "Plateau min Sharpe", Num(Json.Number(rules["plateauMinSharpe"])));
        Row("Walk-forward", "Train / test / step (months)", Text($"{Json.Number(wf["trainMonths"])} / {Json.Number(wf["testMonths"])} / {Json.Number(wf["stepMonths"])}"),
            "Fixed parameters per fold (no re-optimization); only the locked OOS run is truly out of sample for optimized candidates.");
        foreach (var f in (latest["walkForwardFolds"] as JsonArray ?? []).OfType<JsonObject>())
            Row("Walk-forward", $"Fold {Json.Number(f["index"])} ({Quarter(Str(f["testFrom"]))})", Text($"test {Day(Str(f["testFrom"]))} → {Day(Str(f["testTo"]))}"),
                $"train {Day(Str(f["trainFrom"]))} → {Day(Str(f["trainTo"]))} (end dates exclusive)");
        Row("Selection", "Deflated Sharpe", Text("cumulative trials"), "Every parameter set ever evaluated counts toward the luck bar a champion must beat.");
        return s;
    }

    // ------------------------------------------------------------------ CSV and JSON

    private static string Csv(List<Hyp> ranked, List<Col> cols)
    {
        static string Field(Cell c)
        {
            string s = c.Value switch
            {
                null => "",
                double d when double.IsNaN(d) => "",
                double d when double.IsInfinity(d) => d > 0 ? "inf" : "-inf",
                double d => (c.Style == Style.Percent ? Math.Round(d * 100, 9) : d).ToString("R", Inv),
                int i => i.ToString(Inv),
                IFormattable f => f.ToString(null, Inv),
                _ => c.Value.ToString() ?? "",
            };
            return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        }
        var sb = new StringBuilder("﻿");   // BOM so Excel reads UTF-8
        sb.AppendLine(string.Join(",", cols.Select(c => Field(c.Header))));
        foreach (var h in ranked) sb.AppendLine(string.Join(",", cols.Select(c => Field(c.Value(h)))));
        return sb.ToString();
    }

    private static JsonObject FullJson(List<JsonObject> reports, List<Hyp> ranked, List<JsonObject> audits, Func<int, string?> notes, DateTime exported)
    {
        var latest = reports[^1];
        var tested = ranked.Select(h => h.Name).Distinct().ToList();
        return new JsonObject
        {
            ["exportedUtc"] = exported.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv),
            ["source"] = $"{HuntRoot}/cycle_*/report.json",
            ["note"] = "Percent fields are in percent units (21.3 = 21.3%). Each cycles[] entry is that cycle's full report.json plus its report and research-notes paths.",
            ["summary"] = new JsonObject
            {
                ["cycles"] = new JsonArray(reports.Select(r => (JsonNode)(int)Json.Number(r["cycle"])).ToArray()),
                ["hypotheses"] = ranked.Count,
                ["passed"] = ranked.Count(h => Str(h.H["verdict"]) == "PASSED"),
                ["reachedLockedOos"] = ranked.Count(h => h.Oos is not null),
                ["champion"] = latest["champion"]?.DeepClone(),
                ["cumulativeTrials"] = reports.Max(r => (int)Json.Number(r["trials"]?["cumulative"])),
                ["rankedByDevSharpe"] = new JsonArray(ranked.Select(h => (JsonNode)new JsonObject
                {
                    ["cycle"] = h.Cycle, ["strategy"] = h.Name, ["verdict"] = Str(h.H["verdict"]), ["stage"] = Str(h.H["stage"]),
                    ["devSharpe"] = Finite(h.Dev?.Sharpe), ["devNetProfitPct"] = Finite(h.Dev?.NetProfitPct), ["oosNetProfitPct"] = Finite(h.Oos?.NetProfitPct),
                }).ToArray()),
            },
            ["settings"] = latest["constants"]?.DeepClone(),
            ["rules"] = latest["rules"]?.DeepClone(),
            ["walkForwardFolds"] = latest["walkForwardFolds"]?.DeepClone(),
            ["minDevTrades"] = latest["minDevTrades"]?.DeepClone(),
            ["auditCycles"] = new JsonArray(audits.Select(a => (JsonNode)a.DeepClone()).ToArray()),
            ["strategies"] = new JsonArray(tested.Select(n => StrategyRegistry.All.FirstOrDefault(d => d.Name == n)).Where(d => d is not null).Select(d => (JsonNode)new JsonObject
            {
                ["name"] = d!.Name, ["family"] = d.Family, ["version"] = d.Version, ["description"] = d.Description,
                ["defaults"] = new JsonObject(d.Defaults.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                ["searchGrid"] = new JsonArray(d.DefaultSpace.Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["min"] = p.Min, ["max"] = p.Max, ["step"] = p.Step }).ToArray()),
            }).ToArray()),
            ["cycles"] = new JsonArray(reports.Select(r =>
            {
                var c = r.DeepClone().AsObject();
                int cycle = (int)Json.Number(r["cycle"]);
                c["reportPath"] = $"{HuntRoot}/cycle_{cycle}/report.html";
                c["researchNotes"] = notes(cycle)?.Replace('\\', '/');
                return (JsonNode)c;
            }).ToArray()),
        };
    }

    /// <summary>Audit cycles from hunt_log.json that have no cycle report: number, time, step and result only.</summary>
    private static List<JsonObject> AuditCycles(List<JsonObject> reports)
    {
        if (!File.Exists("hunt_log.json")) return [];
        var reported = reports.Select(r => (int)Json.Number(r["cycle"])).ToHashSet();
        return (JsonNode.Parse(File.ReadAllText("hunt_log.json"))?["cycles"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(c => !reported.Contains((int)Json.Number(c["cycle"])))
            .Select(c => new JsonObject
            {
                ["cycle"] = (int)Json.Number(c["cycle"]), ["timestampUtc"] = Str(c["timestamp_utc"]), ["step"] = Str(c["step"]), ["result"] = Str(c["result"]),
            }).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private static PerformanceMetrics? Metrics(JsonNode? n) => n is JsonObject o ? o.Deserialize<PerformanceMetrics>(Json.Options) : null;

    private static string Str(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => n.ToJsonString(),
    };

    private static string Join(JsonNode? n) => n is JsonArray a ? string.Join("; ", a.Select(Str)) : "";
    private static string Fmt(double v) => v.ToString("0.##########", Inv);
    private static string Params(JsonNode? p) => p is JsonObject o && o.Count > 0 ? string.Join(", ", o.Select(kv => $"{kv.Key}={Fmt(Json.Number(kv.Value))}")) : "";
    private static string Day(string iso) => iso.Length >= 10 ? iso[..10] : iso;
    private static string Time(string iso) => (iso.Length >= 19 ? iso[..19] : iso).Replace('T', ' ');
    private static string Quarter(string iso) => iso.Length >= 7 && int.TryParse(iso[5..7], out int m) ? $"{iso[..4]}Q{(m - 1) / 3 + 1}" : iso;
    private static double? Finite(double? v) => v is double x && double.IsFinite(x) ? x : null;

    private static Cell Header(string s) => new(s, Style.Header);
    private static Cell Text(string? s) => new(string.IsNullOrEmpty(s) ? null : s);
    private static Cell Pct(double? percent) => new(percent is double p && double.IsFinite(p) ? p / 100 : null, Style.Percent);
    private static Cell Num(double? v) => new(v, Style.Number2);
    private static Cell Int(double? v) => new(v, Style.Integer);
    private static Cell Money(double? v) => new(v, Style.Money);
    private static Cell Raw(double? v) => new(v, Style.General);
}
