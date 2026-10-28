using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backtester.Core.Analytics;

namespace Backtester.Core.Reporting;

/// <summary>Self-contained HTML pages (inline CSS + SVG, no scripts or network) for runs, walk-forwards and hunt cycles.</summary>
public static class HtmlReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed record IndexEntry(string Group, string Title, string Href, string Subtitle, DateTime Modified);

    // ------------------------------------------------------------------ run page

    /// <param name="benchmark">Buy-and-hold over the same window, used when the run did not save its own benchmark metrics.</param>
    public static string RunPage(RunArtifacts run, Engine.BacktestResult? benchmark, string? backHref = null)
    {
        var m = run.Metrics;
        var b = run.Benchmark ?? (benchmark is null ? null : PerformanceMetrics.Compute(benchmark));
        var benchmarkEquity = benchmark?.Equity;
        var sb = new StringBuilder();

        sb.Append(Tiles(m, b));
        sb.Append(Card("Equity", EquityChart(run.Equity, run.Config.InitialCapital, benchmarkEquity)));
        sb.Append(Card("Drawdown", DrawdownChart(run.Equity, run.Config.InitialCapital, benchmarkEquity)));
        sb.Append(Card("Monthly returns", MonthlyTable(run.Equity, run.Config.InitialCapital)));
        sb.Append(Card("Trades", TradeSection(run.Trades)));
        sb.Append(Card("All metrics", MetricsTable(m, b, "Strategy", "Buy & hold")));

        var extra = run.Result["extra"];
        if (extra?["slippageSensitivity"] is JsonArray slip && slip.Count > 0) sb.Append(Card("Slippage sensitivity", SlippageTable(slip)));
        if (extra?["dsr"] is JsonObject dsr) sb.Append(Card("Deflated Sharpe", KeyValues(dsr)));
        if (run.Result["diagnostics"] is JsonObject diag) sb.Append(Card("Diagnostics", KeyValues(diag)));
        sb.Append(Card("Settings", SettingsTable(run.Config, run.Parameters)));
        sb.Append(Details($"Trade list ({run.Trades.Count})", TradeTable(run.Trades, 2000)));
        sb.Append(Card("Files", $"<p class=\"muted mono\">{Enc(run.Dir.Replace('\\', '/'))}/result.json, equity.csv, trades.csv</p>"));

        string subtitle = $"{Day(m.From)} → {Day(m.To)} · {FormatParams(run.Parameters)}";
        return Page(run.Strategy, subtitle, sb.ToString(), backHref);
    }

    // ------------------------------------------------------------------ hunt cycle page

    public static string CyclePage(
        JsonObject report,
        Func<string, RunArtifacts?> loadRun,
        Func<BacktestConfig, Engine.BacktestResult?> benchmark,
        Func<string, string?> runHref,
        string? backHref = null)
    {
        int cycle = report["cycle"]?.GetValue<int>() ?? 0;
        var hyps = (report["hypotheses"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var trials = report["trials"] as JsonObject;
        int cumulative = trials?["cumulative"]?.GetValue<int>() ?? 0;
        double variance = D(trials?["dailySharpeVariance"]);
        var benchDev = Metrics(report["buyAndHold"]?["dev"]);
        var benchOos = Metrics(report["buyAndHold"]?["oos"]);
        int passed = hyps.Count(h => S(h["verdict"]) == "PASSED");
        int oosRuns = hyps.Count(h => h["lockedOos"] is JsonObject);

        var sb = new StringBuilder();

        // Champion
        if (report["champion"] is JsonObject champ)
            sb.Append(Card("Champion", $"<p><b>{Enc(S(champ["strategy"]))}</b> <span class=\"badge pass\">DSR {N(D(champ["deflatedSharpe"]), 3)}</span></p>" +
                                        $"<p class=\"muted\">{Enc(S(champ["rules"]))}</p>" + KeyValues(champ["parameters"] as JsonObject ?? [])));
        else
            sb.Append(Card("Champion", "<p><span class=\"badge fail\">None</span> Every hypothesis tested so far has been rejected.</p>"));

        double expMax = DeflatedSharpe.ExpectedMaxSharpe(cumulative, variance) * Math.Sqrt(365);
        sb.Append("<section class=\"tiles\">");
        sb.Append(Tile("Hypotheses", hyps.Count.ToString(Inv), $"{passed} passed"));
        sb.Append(Tile("Trials this cycle", (trials?["thisCycle"]?.GetValue<int>() ?? 0).ToString(Inv), $"{cumulative} cumulative"));
        sb.Append(Tile("Locked OOS runs", oosRuns.ToString(Inv), "one per surviving candidate"));
        sb.Append(Tile("Luck bar (Sharpe)", N(expMax, 2), $"best of {cumulative} random trials"));
        sb.Append("</section>");

        // Summary table
        var t = new StringBuilder("<div class=\"scroll\"><table><thead><tr><th>Hypothesis</th><th>Family</th><th>Verdict</th><th>Stopped at</th>" +
                                  "<th class=\"r\">Net</th><th class=\"r\">PF</th><th class=\"r\">Sharpe</th><th class=\"r\">Max DD</th><th class=\"r\">Trades/yr</th><th>Reasons</th></tr></thead><tbody>");
        foreach (var h in hyps)
        {
            var mm = Metrics(h["optimizedDev"]) ?? Metrics(h["baselineDev"]);
            string name = S(h["strategy"]);
            t.Append($"<tr><td><a href=\"#{Anchor(name)}\">{Enc(name)}</a></td><td>{Enc(S(h["family"]))}</td><td>{Verdict(S(h["verdict"]))}</td><td>{Enc(S(h["stage"]))}</td>");
            t.Append($"<td class=\"r {Sign(mm?.NetProfitPct)}\">{Pct(mm?.NetProfitPct)}</td><td class=\"r\">{N(mm?.ProfitFactor, 2)}</td><td class=\"r\">{N(mm?.Sharpe, 2)}</td>");
            t.Append($"<td class=\"r\">{Pct(mm?.MaxDrawdownPct, 1)}</td><td class=\"r\">{N(mm?.TradesPerYear, 1)}</td>");
            t.Append($"<td class=\"small\">{Enc(string.Join("; ", (h["killReasons"] as JsonArray ?? []).Select(S)))}</td></tr>");
        }
        if (benchDev is not null)
            t.Append($"<tr class=\"bench\"><td>Buy &amp; hold (dev)</td><td></td><td></td><td></td><td class=\"r {Sign(benchDev.NetProfitPct)}\">{Pct(benchDev.NetProfitPct)}</td>" +
                     $"<td class=\"r\">–</td><td class=\"r\">{N(benchDev.Sharpe, 2)}</td><td class=\"r\">{Pct(benchDev.MaxDrawdownPct, 1)}</td><td class=\"r\">–</td><td></td></tr>");
        t.Append("</tbody></table></div><p class=\"muted small\">Metrics are on development data: the optimized parameters where optimization ran, otherwise the defaults.</p>");
        sb.Append(Card($"Hypotheses ({hyps.Count})", t.ToString()));

        foreach (var h in hyps) sb.Append(HypothesisSection(h, benchDev, benchOos, loadRun, benchmark, runHref));

        if (report["warnings"] is JsonArray warnings)
            sb.Append(Card("Overfitting warnings", "<ul>" + string.Concat(warnings.Select(w => $"<li>{Enc(S(w))}</li>")) + "</ul>"));
        sb.Append(Details("Rules and constants", KeyValues(report["rules"] as JsonObject ?? []) + KeyValues(report["constants"] as JsonObject ?? [])));

        string ts = S(report["timestampUtc"]);
        string subtitle = $"{(ts.Length >= 19 ? ts[..19].Replace('T', ' ') : ts)} UTC · {cumulative} trials so far";
        return Page($"Hunt cycle {cycle}", subtitle, sb.ToString(), backHref);
    }

    private static string HypothesisSection(JsonObject h, PerformanceMetrics? benchDev, PerformanceMetrics? benchOos,
        Func<string, RunArtifacts?> loadRun, Func<BacktestConfig, Engine.BacktestResult?> benchmark, Func<string, string?> runHref)
    {
        string name = S(h["strategy"]);
        var art = h["artifacts"] as JsonObject ?? [];
        var sb = new StringBuilder();
        sb.Append($"<section class=\"card\" id=\"{Anchor(name)}\"><h2>{Enc(name)} {Verdict(S(h["verdict"]))}</h2>");
        sb.Append($"<p class=\"muted\">{Enc(S(h["family"]))} · {Enc(S(h["rules"]))}</p>");
        if (h["killReasons"] is JsonArray kr && kr.Count > 0)
            sb.Append("<div class=\"reasons\"><b>Rejected at " + Enc(S(h["stage"])) + ":</b><ul>" + string.Concat(kr.Select(r => $"<li>{Enc(S(r))}</li>")) + "</ul></div>");

        void RunBlock(string title, string artKey, PerformanceMetrics? metrics, PerformanceMetrics? bench, JsonNode? folds, string benchLabel)
        {
            sb.Append($"<h3>{Enc(title)}</h3>");
            if (metrics is not null) sb.Append(CompactMetrics(metrics, bench, benchLabel));
            string? path = S(art[artKey]);
            var run = string.IsNullOrEmpty(path) ? null : loadRun(path);
            if (run is not null)
            {
                var bc = benchmark(run.Config)?.Equity;
                sb.Append(EquityChart(run.Equity, run.Config.InitialCapital, bc, 230))
                  .Append(DrawdownChart(run.Equity, run.Config.InitialCapital, bc, 150));
                if (runHref(path!) is { } href) sb.Append($"<p class=\"small\"><a href=\"{Enc(href)}\">Full run report →</a></p>");
            }
            if (folds is JsonArray f && f.Count > 0) sb.Append(FoldSection(f));
        }

        RunBlock("Default parameters on development data", "baselineDev", Metrics(h["baselineDev"]), benchDev, h["baselineWalkForward"], "Buy & hold");
        if (h["optimization"] is JsonObject opt) sb.Append(OptimizationBlock(opt));
        if (h["optimizedDev"] is JsonNode)
        {
            RunBlock("Optimized parameters on development data", "optimizedDev", Metrics(h["optimizedDev"]), benchDev, h["optimizedWalkForward"], "Buy & hold");
            if (h["devSlippageSensitivity"] is JsonArray slip) sb.Append("<h4>Slippage sensitivity (dev)</h4>").Append(SlippageTable(slip));
        }
        if (h["lockedOos"] is JsonNode)
            RunBlock("Locked out-of-sample (single run)", "lockedOos", Metrics(h["lockedOos"]), benchOos, null, "Buy & hold (OOS)");
        if (h["dsr"] is JsonObject dsr) sb.Append("<h4>Deflated Sharpe</h4>").Append(KeyValues(dsr));
        sb.Append("</section>");
        return sb.ToString();
    }

    private static string OptimizationBlock(JsonObject opt)
    {
        var sb = new StringBuilder("<h3>Optimization</h3>");
        sb.Append($"<p>{N(D(opt["gridSize"]), 0)} grid points, {N(D(opt["evaluations"]), 0)} evaluated{(opt["sampled"]?.GetValue<bool>() == true ? " (sampled)" : "")}, " +
                  $"{N(D(opt["eligibleTrials"]), 0)} with enough trades. Edge: <b>{Enc(string.Join(", ", (opt["bestAtEdgeOf"] as JsonArray ?? []).Select(S)) is { Length: > 0 } e ? e : "none")}</b>. " +
                  $"Plateau: {(opt["plateauPassed"]?.GetValue<bool>() == true ? "<span class=\"badge pass\">pass</span>" : "<span class=\"badge fail\">fail</span>")}</p>");
        if (opt["top10"] is JsonArray top && top.Count > 0) sb.Append("<h4>Top parameter sets by Sharpe</h4>").Append(TrialTable(top));
        if (opt["neighbours"] is JsonArray nb && nb.Count > 0) sb.Append("<h4>Neighbours of the best point (±1 step)</h4>").Append(TrialTable(nb));
        return sb.ToString();
    }

    private static string TrialTable(JsonArray trials)
    {
        var rows = trials.OfType<JsonObject>().ToList();
        var keys = rows.SelectMany(r => (r["parameters"] as JsonObject ?? []).Select(kv => kv.Key)).Distinct().ToList();
        var sb = new StringBuilder("<div class=\"scroll\"><table><thead><tr>");
        foreach (var k in keys) sb.Append($"<th class=\"r\">{Enc(k)}</th>");
        sb.Append("<th class=\"r\">Sharpe</th><th class=\"r\">PF</th><th class=\"r\">Trades</th></tr></thead><tbody>");
        foreach (var r in rows)
        {
            sb.Append("<tr>");
            foreach (var k in keys) sb.Append($"<td class=\"r\">{N(D(r["parameters"]?[k]), 4, trim: true)}</td>");
            sb.Append($"<td class=\"r\">{N(D(r["sharpe"]), 2)}</td><td class=\"r\">{N(D(r["profitFactor"]), 2)}</td><td class=\"r\">{N(D(r["trades"]), 0)}</td></tr>");
        }
        return sb.Append("</tbody></table></div>").ToString();
    }

    private static string FoldSection(JsonArray folds)
    {
        var rows = folds.OfType<JsonObject>().ToList();
        var sb = new StringBuilder("<h4>Walk-forward test folds</h4><div class=\"grid2\"><div><table><thead><tr><th>Fold</th><th>Test window</th>" +
                                   "<th class=\"r\">Net</th><th class=\"r\">PF</th><th class=\"r\">Sharpe</th><th class=\"r\">Max DD</th><th class=\"r\">Trades</th><th>Pass</th></tr></thead><tbody>");
        foreach (var f in rows)
        {
            double net = D(f["netProfitPct"]);
            sb.Append($"<tr><td>{N(D(f["fold"]), 0)}</td><td>{Enc(S(f["testFrom"]))} → {Enc(S(f["testTo"]))}</td><td class=\"r {Sign(net)}\">{Pct(net)}</td>" +
                      $"<td class=\"r\">{N(D(f["profitFactor"]), 2)}</td><td class=\"r\">{N(D(f["sharpe"]), 2)}</td><td class=\"r\">{Pct(D(f["maxDrawdownPct"]), 1)}</td>" +
                      $"<td class=\"r\">{N(D(f["trades"]), 0)}</td><td>{(f["pass"]?.GetValue<bool>() == true ? "<span class=\"badge pass\">yes</span>" : "<span class=\"badge fail\">no</span>")}</td></tr>");
        }
        sb.Append("</tbody></table></div><div>");
        sb.Append(SvgCharts.Bars(rows.Select(f => ($"#{N(D(f["fold"]), 0)} {Month(S(f["testFrom"]))}", D(f["netProfitPct"]))).ToList(),
            v => Pct(v, 1), "Net return per walk-forward fold", 200));
        return sb.Append("</div></div>").ToString();
    }

    // ------------------------------------------------------------------ walk-forward page (walkforward command)

    public static string WalkForwardPage(JsonObject wf, string? backHref = null)
    {
        var rows = (wf["folds"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        int failed = wf["foldsFailed"]?.GetValue<int>() ?? rows.Count(r => r["pass"]?.GetValue<bool>() != true);
        var sb = new StringBuilder("<section class=\"tiles\">");
        sb.Append(Tile("Folds", rows.Count.ToString(Inv), $"train {S(wf["train"])}m / test {S(wf["test"])}m"));
        sb.Append(Tile("Failed", failed.ToString(Inv), $"min PF {S(wf["foldMinPf"])} and net > 0"));
        sb.Append(Tile("Mode", wf["reoptimized"]?.GetValue<bool>() == true ? "re-optimized" : "fixed", "parameters per fold"));
        sb.Append(Tile("Trials", S(wf["totalTrials"]), "backtests run"));
        sb.Append("</section>");

        var t = new StringBuilder("<div class=\"scroll\"><table><thead><tr><th>Fold</th><th>Test window</th><th>Parameters</th><th class=\"r\">Net</th><th class=\"r\">PF</th>" +
                                  "<th class=\"r\">Sharpe</th><th class=\"r\">Max DD</th><th class=\"r\">Trades</th><th>Pass</th></tr></thead><tbody>");
        var bars = new List<(string, double)>();
        foreach (var r in rows)
        {
            var fold = r["fold"];
            var m = Metrics(r["test"]);
            string from = S(fold?["testFrom"]), to = S(fold?["testTo"]);
            bars.Add(($"#{S(fold?["index"])} {Month(from)}", m?.NetProfitPct ?? double.NaN));
            var p = (r["parameters"] as JsonObject ?? []).Select(kv => $"{kv.Key}={N(D(kv.Value), 4, trim: true)}");
            t.Append($"<tr><td>{Enc(S(fold?["index"]))}</td><td>{Day(from)} → {Day(to)}</td><td class=\"small mono\">{Enc(string.Join(" ", p))}{(r["error"] is JsonNode err ? " · " + Enc(S(err)) : "")}</td>" +
                     $"<td class=\"r {Sign(m?.NetProfitPct)}\">{Pct(m?.NetProfitPct)}</td><td class=\"r\">{N(m?.ProfitFactor, 2)}</td><td class=\"r\">{N(m?.Sharpe, 2)}</td>" +
                     $"<td class=\"r\">{Pct(m?.MaxDrawdownPct, 1)}</td><td class=\"r\">{N(m?.Trades, 0)}</td><td>{(r["pass"]?.GetValue<bool>() == true ? "<span class=\"badge pass\">yes</span>" : "<span class=\"badge fail\">no</span>")}</td></tr>");
        }
        t.Append("</tbody></table></div>");
        sb.Append(Card("Net return per fold", SvgCharts.Bars(bars, v => Pct(v, 1), "Net return per fold")));
        sb.Append(Card("Folds", t.ToString()));
        string window = $"{Day(S(wf["window"]?["from"]))} → {Day(S(wf["window"]?["to"]))}";
        return Page($"Walk-forward · {S(wf["strategy"])}", window, sb.ToString(), backHref);
    }

    // ------------------------------------------------------------------ index

    public static string IndexPage(IEnumerable<IndexEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var g in entries.GroupBy(e => e.Group))
        {
            var list = new StringBuilder("<ul class=\"index\">");
            foreach (var e in g.OrderByDescending(e => e.Modified))
                list.Append($"<li><a href=\"{Enc(e.Href)}\">{Enc(e.Title)}</a> <span class=\"muted small\">{Enc(e.Subtitle)} · {e.Modified:yyyy-MM-dd HH:mm}</span></li>");
            sb.Append(Card($"{g.Key} ({g.Count()})", list.Append("</ul>").ToString()));
        }
        if (sb.Length == 0) sb.Append(Card("Nothing yet", "<p>Run a backtest, walk-forward or hunt cycle first.</p>"));
        return Page("Backtest reports", "Bybit BTCUSDT perpetual · local engine", sb.ToString(), null);
    }

    // ------------------------------------------------------------------ hunt overview (every cycle on one page)

    /// <param name="ReportHref">The cycle's report.html, relative to the overview page.</param>
    /// <param name="NotesHref">The research notes that cover the cycle, if any.</param>
    public sealed record CycleEntry(JsonObject Report, string ReportHref, string? NotesHref);

    public static string HuntOverviewPage(IReadOnlyList<CycleEntry> cycles, string? backHref = null)
    {
        var ordered = cycles.OrderBy(c => D(c.Report["cycle"])).ToList();
        var rows = ordered.SelectMany(c => (c.Report["hypotheses"] as JsonArray ?? []).OfType<JsonObject>()
                              .Select(h => (Cycle: c, H: h, M: Metrics(h["optimizedDev"]) ?? Metrics(h["baselineDev"]))))
                          .OrderByDescending(r => r.M?.Sharpe is double s && double.IsFinite(s) ? s : double.NegativeInfinity).ToList();
        static int Cycle(CycleEntry c) => (int)D(c.Report["cycle"]);
        static JsonArray Folds(JsonObject h) => h["optimizedWalkForward"] as JsonArray ?? h["baselineWalkForward"] as JsonArray ?? [];
        static string Quarter(string iso) => iso.Length >= 7 && int.TryParse(iso[5..7], out int m) ? $"{iso[2..4]}Q{(m - 1) / 3 + 1}" : iso;
        var latest = ordered.Count > 0 ? ordered[^1].Report : [];
        int cumulative = ordered.Select(c => (int)D(c.Report["trials"]?["cumulative"])).DefaultIfEmpty(0).Max();
        int oosRuns = rows.Count(r => r.H["lockedOos"] is JsonObject);
        string? champion = latest["champion"]?["strategy"]?.GetValue<string>();
        var best = rows.FirstOrDefault(r => r.M is not null);

        var sb = new StringBuilder("<section class=\"tiles\">");
        sb.Append(Tile("Cycles", ordered.Count.ToString(Inv), ordered.Count > 0 ? $"{Cycle(ordered[0])} → {Cycle(ordered[^1])}" : "none yet"));
        sb.Append(Tile("Hypotheses tested", rows.Count.ToString(Inv), $"{rows.Count(r => S(r.H["verdict"]) == "PASSED")} passed"));
        sb.Append(Tile("Trials", cumulative.ToString(Inv), "cumulative, deflates the Sharpe bar"));
        sb.Append(Tile("Locked OOS runs", oosRuns.ToString(Inv), "one per surviving candidate"));
        sb.Append(Tile("Champion", champion ?? "none", champion is null ? "every hypothesis rejected" : "see its cycle report", champion is null ? "neg" : "pos"));
        sb.Append(Tile("Best dev Sharpe", N(best.M?.Sharpe, 2), best.M is null ? "–" : S(best.H["strategy"])));
        sb.Append("</section>");

        sb.Append(Card("Best development Sharpe per cycle",
            SvgCharts.Bars(ordered.Select(c => ($"Cycle {Cycle(c)}", rows.Where(r => r.Cycle == c && r.M is not null).Select(r => r.M!.Sharpe).DefaultIfEmpty(double.NaN).Max())).ToList(),
                v => N(v, 2), "Best development Sharpe per cycle", 200) +
            "<p class=\"muted small\">The kill rule needs Sharpe ≥ 0.50 (and PF ≥ 1.10, max DD ≤ 30%, ≥ 50 trades/yr, at most one losing rule quarter).</p>"));

        var quarters = rows.Select(r => Folds(r.H)).FirstOrDefault(f => f.Count > 0)?.OfType<JsonObject>().Select(f => Quarter(S(f["testFrom"]))).ToList() ?? [];
        var t = new StringBuilder("<div class=\"scroll\"><table class=\"wide\"><thead><tr><th class=\"r\">#</th><th>Hypothesis</th><th class=\"r\">Cycle</th><th>Family</th><th>Verdict</th>" +
                                  "<th class=\"r\">Net</th><th class=\"r\">PF</th><th class=\"r\">Sharpe</th><th class=\"r\">Max DD</th><th class=\"r\">Trades/yr</th>" +
                                  $"<th>Rule quarters{(quarters.Count > 0 ? $"<br><span class=\"small\">{Enc(string.Join(" · ", quarters))}</span>" : "")}</th>" +
                                  "<th class=\"r\">Locked OOS</th><th>Reasons</th></tr></thead><tbody>");
        int rank = 0;
        foreach (var (c, h, mm) in rows)
        {
            string name = S(h["strategy"]);
            var folds = Folds(h).OfType<JsonObject>().ToList();
            string q = folds.Count == 0 ? "–" : string.Join(" ", folds.Select(f => D(f["netProfitPct"]) is var n && double.IsFinite(n)
                ? $"<span class=\"{Sign(n)}\">{n.ToString("+0.0;−0.0;0.0", Inv)}</span>" : "–")) +
                $" <span class=\"muted small\">({folds.Count(f => f["pass"]?.GetValue<bool>() == true)}/{folds.Count})</span>";
            var oos = Metrics(h["lockedOos"]);
            t.Append($"<tr><td class=\"r muted\">{++rank}</td><td class=\"nw\"><a href=\"{Enc(c.ReportHref)}#{Anchor(name)}\">{Enc(name)}</a></td><td class=\"r\">{Cycle(c)}</td>" +
                     $"<td>{Enc(S(h["family"]))}</td><td>{Verdict(S(h["verdict"]))} <span class=\"muted small nw\">{Enc(S(h["stage"]))}</span></td>");
            t.Append($"<td class=\"r {Sign(mm?.NetProfitPct)}\">{Pct(mm?.NetProfitPct)}</td><td class=\"r\">{N(mm?.ProfitFactor, 2)}</td><td class=\"r\">{N(mm?.Sharpe, 2)}</td>");
            t.Append($"<td class=\"r\">{Pct(mm?.MaxDrawdownPct, 1)}</td><td class=\"r\">{N(mm?.TradesPerYear, 1)}</td><td class=\"mono\">{q}</td>");
            t.Append(oos is null ? "<td class=\"r muted\">–</td>" : $"<td class=\"r {Sign(oos.NetProfitPct)}\">{Pct(oos.NetProfitPct)}<br><span class=\"muted small\">Sharpe {N(oos.Sharpe, 2)}</span></td>");
            t.Append($"<td class=\"small\">{Enc(string.Join("; ", (h["killReasons"] as JsonArray ?? []).Select(S)))}</td></tr>");
        }
        if (Metrics(latest["buyAndHold"]?["dev"]) is { } bh)
            t.Append($"<tr class=\"bench\"><td></td><td>Buy &amp; hold (dev)</td><td></td><td></td><td></td><td class=\"r {Sign(bh.NetProfitPct)}\">{Pct(bh.NetProfitPct)}</td>" +
                     $"<td class=\"r\">–</td><td class=\"r\">{N(bh.Sharpe, 2)}</td><td class=\"r\">{Pct(bh.MaxDrawdownPct, 1)}</td><td class=\"r\">–</td><td></td><td></td><td></td></tr>");
        t.Append("</tbody></table></div><p class=\"muted small\">Metrics are on development data: the optimized parameters where optimization ran, otherwise the defaults. " +
                 "Rule quarters are the net % of the walk-forward test quarters (passed / total); a quarter passes only if it makes money. " +
                 "Locked OOS is the single out-of-sample run, made only for candidates that survived development.</p>");
        sb.Append(Card($"Every hypothesis, best development Sharpe first ({rows.Count})", t.ToString()));

        var ct = new StringBuilder("<div class=\"scroll\"><table><thead><tr><th class=\"r\">Cycle</th><th>Run</th><th class=\"r\">Hypotheses</th><th>Best (dev Sharpe)</th>" +
                                   "<th class=\"r\">Trials</th><th class=\"r\">Cumulative</th><th class=\"r\">OOS runs</th><th>Champion</th><th>Pages</th></tr></thead><tbody>");
        foreach (var c in ordered)
        {
            var mine = rows.Where(r => r.Cycle == c).ToList();
            var top = mine.FirstOrDefault(r => r.M is not null);
            string ts = S(c.Report["timestampUtc"]);
            ct.Append($"<tr><td class=\"r\">{Cycle(c)}</td><td>{Enc(Day(ts))}</td><td class=\"r\">{mine.Count}</td>" +
                      $"<td>{(top.M is null ? "–" : $"{Enc(S(top.H["strategy"]))} <span class=\"muted\">{N(top.M.Sharpe, 2)}</span>")}</td>" +
                      $"<td class=\"r\">{N(D(c.Report["trials"]?["thisCycle"]), 0)}</td><td class=\"r\">{N(D(c.Report["trials"]?["cumulative"]), 0)}</td>" +
                      $"<td class=\"r\">{mine.Count(r => r.H["lockedOos"] is JsonObject)}</td><td>{Enc(c.Report["champion"]?["strategy"]?.GetValue<string>() ?? "none")}</td>" +
                      $"<td><a href=\"{Enc(c.ReportHref)}\">report</a>{(c.NotesHref is null ? "" : $" · <a href=\"{Enc(c.NotesHref)}\">research notes</a>")}</td></tr>");
        }
        sb.Append(Card("Cycles", ct.Append("</tbody></table></div>").ToString()));

        string window = best.M is null ? "" : $" · development window {Day(best.M.From)} → {Day(best.M.To)}";
        return Page("Hunt overview", $"Every hypothesis from every hunt cycle{window}", sb.ToString(), backHref);
    }

    // ------------------------------------------------------------------ building blocks

    private static string EquityChart(IReadOnlyList<EquityPoint> eq, double initial, IReadOnlyList<EquityPoint>? bench, int height = 300)
    {
        var series = new List<ChartSeries> { new("Strategy", "c1", eq.Select(p => (p.TimeMs, p.Equity)).ToList()) };
        if (bench is { Count: > 1 }) series.Add(new ChartSeries("Buy & hold", "c2", bench.Select(p => (p.TimeMs, p.Equity)).ToList()));
        return SvgCharts.Line(series, v => Money(v), "Equity curve", height);
    }

    private static string DrawdownChart(IReadOnlyList<EquityPoint> eq, double initial, IReadOnlyList<EquityPoint>? bench, int height = 220)
    {
        static List<(long, double)> Dd(IReadOnlyList<EquityPoint> pts, double start)
        {
            double peak = start;
            var list = new List<(long, double)>(pts.Count);
            foreach (var p in pts)
            {
                peak = Math.Max(peak, p.Equity);
                list.Add((p.TimeMs, peak > 0 ? (p.Equity / peak - 1) * 100 : 0));
            }
            return list;
        }
        var series = new List<ChartSeries> { new("Strategy drawdown", "neg", Dd(eq, initial), Area: true) };
        if (bench is { Count: > 1 }) series.Add(new ChartSeries("Buy & hold drawdown", "c2", Dd(bench, initial)));
        return SvgCharts.Line(series, v => Pct(v, 0), "Drawdown", height, includeZero: true);
    }

    private static string MonthlyTable(IReadOnlyList<EquityPoint> eq, double initial)
    {
        if (eq.Count == 0) return "<p class=\"muted\">No equity data.</p>";
        var monthEnd = new SortedDictionary<(int Y, int M), double>();
        foreach (var p in eq)
        {
            var t = TimeUtil.FromMs(p.TimeMs - 1);
            monthEnd[(t.Year, t.Month)] = p.Equity;
        }
        var ret = new Dictionary<(int, int), double>();
        double prev = initial;
        foreach (var (k, v) in monthEnd) { ret[k] = prev > 0 ? (v / prev - 1) * 100 : 0; prev = v; }

        var sb = new StringBuilder("<div class=\"scroll\"><table class=\"months\"><thead><tr><th>Year</th>");
        foreach (var mn in CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames.Take(12)) sb.Append($"<th class=\"r\">{mn}</th>");
        sb.Append("<th class=\"r\">Year</th></tr></thead><tbody>");
        foreach (var year in monthEnd.Keys.Select(k => k.Y).Distinct())
        {
            sb.Append($"<tr><td>{year}</td>");
            double compound = 1;
            for (int mth = 1; mth <= 12; mth++)
            {
                if (ret.TryGetValue((year, mth), out var r))
                {
                    compound *= 1 + r / 100;
                    sb.Append($"<td class=\"r heat\" style=\"{Heat(r)}\">{N(r, 1)}</td>");
                }
                else sb.Append("<td></td>");
            }
            double yr = (compound - 1) * 100;
            sb.Append($"<td class=\"r heat strong\" style=\"{Heat(yr / 3)}\">{N(yr, 1)}</td></tr>");
        }
        return sb.Append("</tbody></table></div><p class=\"muted small\">Percent change of end-of-month equity (UTC).</p>").ToString();
    }

    private static string TradeSection(IReadOnlyList<Trade> trades)
    {
        if (trades.Count == 0) return "<p class=\"muted\">No trades.</p>";
        var r = trades.Select(t => t.NetPnl / (t.EntryPrice * t.Quantity) * 100).ToList();
        var sorted = r.OrderBy(x => x).ToList();
        double Q(double q) => sorted[(int)Math.Round(q * (sorted.Count - 1))];

        double lo = Q(0.01), hi = Q(0.99);
        if (hi - lo < 1e-9) { lo -= 1; hi += 1; }
        int bins = 30;
        double w = (hi - lo) / bins;
        var counts = new int[bins];
        foreach (var x in r) counts[Math.Clamp((int)((x - lo) / w), 0, bins - 1)]++;
        var hist = counts.Select((c, i) => (N(lo + w * (i + 0.5), 1) + "%", (double)c)).ToList();
        var histSvg = SvgCharts.Bars(hist, v => N(v, 0), "Distribution of trade returns", 220, showValues: false,
            barClass: i => lo + w * (i + 0.5) >= 0 ? "pos" : "neg");

        var longs = trades.Where(t => t.Direction > 0).ToList();
        var shorts = trades.Where(t => t.Direction < 0).ToList();
        var kv = new List<(string, string)>
        {
            ("Trades", $"{trades.Count} ({longs.Count} long, {shorts.Count} short)"),
            ("Win rate", Pct(100.0 * r.Count(x => x > 0) / r.Count, 1)),
            ("Mean / median return", $"{Pct(r.Average())} / {Pct(Q(0.5))}"),
            ("5th / 95th percentile", $"{Pct(Q(0.05))} / {Pct(Q(0.95))}"),
            ("Worst / best", $"{Pct(sorted[0])} / {Pct(sorted[^1])}"),
            ("Avg bars held", N(trades.Average(t => t.BarsHeld), 1)),
            ("Net P&L long / short", $"{Money(longs.Sum(t => t.NetPnl))} / {Money(shorts.Sum(t => t.NetPnl))}"),
            ("Fees / funding", $"{Money(trades.Sum(t => t.Fees))} / {Money(trades.Sum(t => t.Funding))}"),
            ("Exits", string.Join(", ", trades.GroupBy(t => t.ExitReason).Select(g => $"{g.Key} {g.Count()}"))),
        };
        return "<div class=\"grid2\"><div>" + Kv(kv) + "</div><div>" + histSvg +
               "<p class=\"muted small\">Trade count by net return on entry notional (green: winners, red: losers; 1st–99th percentile range).</p></div></div>";
    }

    private static string TradeTable(IReadOnlyList<Trade> trades, int max)
    {
        var sb = new StringBuilder("<div class=\"scroll tall\"><table><thead><tr><th>#</th><th>Side</th><th>Entry</th><th class=\"r\">Price</th><th>Exit</th>" +
                                   "<th class=\"r\">Price</th><th class=\"r\">Qty</th><th class=\"r\">Net P&amp;L</th><th class=\"r\">Fees</th><th class=\"r\">Funding</th><th>Reason</th><th class=\"r\">Bars</th></tr></thead><tbody>");
        int i = 0;
        foreach (var t in trades.Take(max))
        {
            sb.Append($"<tr><td>{++i}</td><td>{(t.Direction > 0 ? "long" : "short")}</td><td class=\"mono\">{TimeUtil.Iso(t.EntryTimeMs)[..16]}</td><td class=\"r\">{N(t.EntryPrice, 1)}</td>" +
                      $"<td class=\"mono\">{TimeUtil.Iso(t.ExitTimeMs)[..16]}</td><td class=\"r\">{N(t.ExitPrice, 1)}</td><td class=\"r\">{N(t.Quantity, 4)}</td>" +
                      $"<td class=\"r {Sign(t.NetPnl)}\">{Money(t.NetPnl)}</td><td class=\"r\">{Money(t.Fees)}</td><td class=\"r\">{Money(t.Funding)}</td><td>{t.ExitReason}</td><td class=\"r\">{t.BarsHeld}</td></tr>");
        }
        if (trades.Count > max) sb.Append($"<tr><td colspan=\"12\" class=\"muted\">… {trades.Count - max} more in trades.csv</td></tr>");
        return sb.Append("</tbody></table></div>").ToString();
    }

    private static string Tiles(PerformanceMetrics m, PerformanceMetrics? b)
    {
        string Vs(Func<PerformanceMetrics, string> f) => b is null ? "" : "B&H " + f(b);
        var sb = new StringBuilder("<section class=\"tiles\">");
        sb.Append(Tile("Net profit", Pct(m.NetProfitPct), Vs(x => Pct(x.NetProfitPct)), Sign(m.NetProfitPct)));
        sb.Append(Tile("CAGR", Pct(m.CagrPct), Vs(x => Pct(x.CagrPct))));
        sb.Append(Tile("Max drawdown", Pct(m.MaxDrawdownPct, 1), Vs(x => Pct(x.MaxDrawdownPct, 1))));
        sb.Append(Tile("Sharpe", N(m.Sharpe, 2), Vs(x => N(x.Sharpe, 2))));
        sb.Append(Tile("Profit factor", N(m.ProfitFactor, 2), $"{N(m.WinRatePct, 1)}% winners"));
        sb.Append(Tile("Trades", N(m.Trades, 0), $"{N(m.TradesPerYear, 1)} / yr"));
        sb.Append(Tile("Time in market", Pct(m.TimeInMarketPct, 0), $"turnover {N(m.TurnoverPerYear, 1)}x / yr"));
        sb.Append(Tile("Costs", Money(m.FeesPaid + m.SlippageCost - m.FundingReceived), $"fees {Money(m.FeesPaid)} · funding {Money(-m.FundingReceived)}"));
        return sb.Append("</section>").ToString();
    }

    /// <summary>Profit factor of a single buy-and-hold trade is meaningless (∞ or 0), so it is shown as a dash.</summary>
    private static string Pf(PerformanceMetrics x) => x.Trades <= 1 ? "–" : N(x.ProfitFactor, 2);

    /// <summary>Two-row strategy vs benchmark summary for the cycle page.</summary>
    private static string CompactMetrics(PerformanceMetrics s, PerformanceMetrics? b, string bName)
    {
        var cols = new (string, Func<PerformanceMetrics, string>)[]
        {
            ("Net", x => Pct(x.NetProfitPct)), ("CAGR", x => Pct(x.CagrPct)), ("Max DD", x => Pct(x.MaxDrawdownPct, 1)), ("PF", Pf),
            ("Sharpe", x => N(x.Sharpe, 2)), ("Sortino", x => N(x.Sortino, 2)), ("Calmar", x => N(x.Calmar, 2)),
            ("Trades", x => N(x.Trades, 0)), ("/ yr", x => N(x.TradesPerYear, 1)), ("In market", x => Pct(x.TimeInMarketPct, 0)),
        };
        var sb = new StringBuilder("<div class=\"scroll\"><table class=\"compact\"><thead><tr><th></th>");
        foreach (var (h, _) in cols) sb.Append($"<th class=\"r\">{Enc(h)}</th>");
        sb.Append("</tr></thead><tbody><tr><td>Strategy</td>");
        foreach (var (_, f) in cols) sb.Append($"<td class=\"r\">{f(s)}</td>");
        sb.Append("</tr>");
        if (b is not null)
        {
            sb.Append($"<tr class=\"bench\"><td>{Enc(bName)}</td>");
            foreach (var (_, f) in cols) sb.Append($"<td class=\"r\">{f(b)}</td>");
            sb.Append("</tr>");
        }
        return sb.Append("</tbody></table></div>").ToString();
    }

    private static string MetricsTable(PerformanceMetrics s, PerformanceMetrics? b, string sName, string bName)
    {
        var rows = new List<(string, Func<PerformanceMetrics, string>)>
        {
            ("Net profit", x => Pct(x.NetProfitPct)), ("CAGR", x => Pct(x.CagrPct)), ("Max drawdown", x => Pct(x.MaxDrawdownPct, 1)),
            ("Profit factor", Pf), ("Sharpe", x => N(x.Sharpe, 2)), ("Sortino", x => N(x.Sortino, 2)),
            ("Calmar", x => N(x.Calmar, 2)), ("Trades", x => N(x.Trades, 0)), ("Trades / yr", x => N(x.TradesPerYear, 1)),
        };
        rows.AddRange(new (string, Func<PerformanceMetrics, string>)[]
            {
                ("Win rate", x => Pct(x.WinRatePct, 1)), ("Avg trade", x => Money(x.AvgTradeNetPnl)), ("Time in market", x => Pct(x.TimeInMarketPct, 1)),
                ("Turnover / yr", x => N(x.TurnoverPerYear, 1) + "x"), ("Fees paid", x => Money(x.FeesPaid)), ("Funding received", x => Money(x.FundingReceived)),
                ("Slippage cost", x => Money(x.SlippageCost)), ("Final equity", x => Money(x.FinalEquity)), ("Years", x => N(x.Years, 2)),
                ("Daily Sharpe / skew / kurtosis", x => $"{N(x.DailySharpe, 4)} / {N(x.DailySkewness, 2)} / {N(x.DailyKurtosis, 1)}"),
            });
        var sb = new StringBuilder($"<div class=\"scroll\"><table class=\"metrics\"><thead><tr><th></th><th class=\"r\">{Enc(sName)}</th>{(b is null ? "" : $"<th class=\"r\">{Enc(bName)}</th>")}</tr></thead><tbody>");
        foreach (var (label, f) in rows)
            sb.Append($"<tr><td>{label}</td><td class=\"r\">{f(s)}</td>{(b is null ? "" : $"<td class=\"r muted\">{f(b)}</td>")}</tr>");
        return sb.Append("</tbody></table></div>").ToString();
    }

    private static string SlippageTable(JsonArray rows)
    {
        var sb = new StringBuilder("<div class=\"scroll\"><table><thead><tr><th>Slippage</th><th class=\"r\">Rate / side</th><th class=\"r\">Net</th><th class=\"r\">CAGR</th>" +
                                   "<th class=\"r\">Max DD</th><th class=\"r\">PF</th><th class=\"r\">Sharpe</th></tr></thead><tbody>");
        foreach (var r in rows.OfType<JsonObject>())
        {
            double mult = D(r["slippageMultiplier"]);
            string label = mult switch { 0 => "−100%", 0.5 => "−50%", 1 => "base", 1.5 => "+50%", 2 => "+100%", _ => $"x{N(mult, 2)}" };
            sb.Append($"<tr><td>{label}</td><td class=\"r\">{Pct(D(r["slippageRate"]) * 100, 3)}</td><td class=\"r {Sign(D(r["netProfitPct"]))}\">{Pct(D(r["netProfitPct"]))}</td>" +
                      $"<td class=\"r\">{Pct(D(r["cagrPct"]))}</td><td class=\"r\">{Pct(D(r["maxDrawdownPct"]), 1)}</td><td class=\"r\">{N(D(r["profitFactor"]), 2)}</td><td class=\"r\">{N(D(r["sharpe"]), 2)}</td></tr>");
        }
        return sb.Append("</tbody></table></div>").ToString();
    }

    private static string SettingsTable(BacktestConfig c, IReadOnlyDictionary<string, double> p) => Kv(
    [
        ("Parameters", FormatParams(p)),
        ("Window", $"{TimeUtil.Iso(c.FromMs)} → {TimeUtil.Iso(c.ToMs)} (end exclusive)"),
        ("Initial capital", Money(c.InitialCapital)),
        ("Costs per side", $"commission {Pct(c.Costs.CommissionRate * 100, 3)} + slippage {Pct(c.Costs.SlippageRate * 100, 3)}"),
        ("Funding", c.FundingEnabled ? "on (8h, Bybit rates)" : "off"),
        ("Sizing", c.Sizing.Mode switch
        {
            SizingMode.FixedFraction => $"fixed fraction {N(c.Sizing.Fraction, 2)} of equity",
            SizingMode.RiskPerTrade => $"{Pct(c.Sizing.RiskPerTrade * 100, 2)} of equity at risk per trade",
            _ => $"vol target {Pct(c.Sizing.VolTargetAnnual * 100, 0)} ({c.Sizing.VolLookbackBars} bars)",
        }),
        ("Risk limits", $"max leverage {N(c.Risk.MaxLeverage, 2)}x · max-DD stop {(c.Risk.MaxDrawdownStop is double dd ? Pct(dd * 100, 0) : "off")} · daily-loss stop {(c.Risk.DailyLossStop is double dl ? Pct(dl * 100, 0) : "off")}"),
        ("Fills", "signal on bar close, fill at next bar open; stops fill intrabar"),
    ]);

    private static string KeyValues(JsonObject o) =>
        Kv(o.Select(kv => (kv.Key, kv.Value is JsonValue v ? Scalar(v) : kv.Value?.ToJsonString(new JsonSerializerOptions { WriteIndented = false, Encoder = Json.Options.Encoder }) ?? "–")).ToList());

    private static string Scalar(JsonValue v) => v.GetValueKind() switch
    {
        JsonValueKind.Number => Math.Abs(Json.Number(v)) >= 1000 ? N(Json.Number(v), 0) : N(Json.Number(v), 6, trim: true),
        JsonValueKind.True => "yes",
        JsonValueKind.False => "no",
        _ => v.ToString(),
    };

    private static string Kv(IEnumerable<(string Key, string Value)> rows) =>
        "<table class=\"kv\"><tbody>" + string.Concat(rows.Select(r => $"<tr><th>{Enc(r.Key)}</th><td>{Enc(r.Value)}</td></tr>")) + "</tbody></table>";

    private static string Tile(string label, string value, string sub, string cls = "") =>
        $"<div class=\"tile\"><div class=\"label\">{Enc(label)}</div><div class=\"value {cls}\">{Enc(value)}</div><div class=\"sub\">{Enc(sub)}</div></div>";

    private static string Card(string title, string body) => $"<section class=\"card\"><h2>{Enc(title)}</h2>{body}</section>";

    private static string Details(string title, string body) => $"<section class=\"card\"><details><summary>{Enc(title)}</summary>{body}</details></section>";

    private static string Verdict(string v) => v == "PASSED" ? "<span class=\"badge pass\">passed</span>" : "<span class=\"badge fail\">rejected</span>";

    private static string Page(string title, string subtitle, string body, string? backHref) => $$"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>{{Enc(title)}}</title><style>{{Css}}</style></head>
        <body><main>
        <header>{{(backHref is null ? "" : $"<a class=\"back\" href=\"{Enc(backHref)}\">← All reports</a>")}}<h1>{{Enc(title)}}</h1><p class="sub">{{Enc(subtitle)}}</p></header>
        {{body}}
        <footer>Generated {{DateTime.UtcNow:yyyy-MM-dd HH:mm}} UTC by the local backtester · self-contained file, no network access</footer>
        </main></body></html>
        """;

    private const string Css = """
        :root{--bg:#f6f6f3;--card:#fff;--fg:#1c1c1e;--muted:#6c6c72;--line:#e4e4e0;--grid:#ededea;--c1:#2563c9;--c2:#9b9ba3;--pos:#16834a;--neg:#c53030;--badge:#f0f0ec}
        @media (prefers-color-scheme:dark){:root:not([data-theme=light]){--bg:#121214;--card:#1b1b1e;--fg:#ececef;--muted:#9b9ba4;--line:#2b2b30;--grid:#242428;--c1:#6d9dff;--c2:#74747c;--pos:#40c07a;--neg:#ff6b6b;--badge:#26262b}}
        :root[data-theme=dark]{--bg:#121214;--card:#1b1b1e;--fg:#ececef;--muted:#9b9ba4;--line:#2b2b30;--grid:#242428;--c1:#6d9dff;--c2:#74747c;--pos:#40c07a;--neg:#ff6b6b;--badge:#26262b}
        *{box-sizing:border-box}
        body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.5 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif}
        main{max-width:1180px;margin:0 auto;padding:24px 16px 48px}
        header h1{margin:4px 0 2px;font-size:24px} .sub{margin:0 0 18px;color:var(--muted)} .back{font-size:13px}
        a{color:var(--c1);text-decoration:none} a:hover{text-decoration:underline}
        .card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px 18px;margin:0 0 16px}
        .card h2{font-size:16px;margin:0 0 12px} .card h3{font-size:14px;margin:18px 0 8px} .card h4{font-size:13px;margin:14px 0 6px;color:var(--muted)}
        .tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(128px,1fr));gap:12px;margin:0 0 16px}
        table.compact th,table.compact td{padding:5px 8px;font-size:13px;white-space:nowrap}
        .tile{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px 14px}
        .tile .label{font-size:12px;color:var(--muted)} .tile .value{font-size:22px;font-weight:600;font-variant-numeric:tabular-nums;margin:2px 0}
        .tile .sub{font-size:12px;color:var(--muted)}
        table{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums}
        th,td{padding:6px 8px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top}
        thead th{font-size:12px;color:var(--muted);font-weight:600} .r{text-align:right}
        table.kv th{width:240px;color:var(--muted);font-weight:500} table.metrics td:first-child{color:var(--muted)}
        tr.bench td{color:var(--muted);font-style:italic}
        .months td,.months th{padding:4px 6px;font-size:12px} .heat{border-radius:3px} .strong{font-weight:600}
        .scroll{overflow-x:auto} .tall{max-height:520px;overflow-y:auto} .nw{white-space:nowrap} table.wide{min-width:1040px}
        .pos{color:var(--pos)} .neg{color:var(--neg)} .muted{color:var(--muted)} .small{font-size:12px} .mono{font-family:ui-monospace,Consolas,monospace;font-size:12px}
        .badge{display:inline-block;padding:1px 8px;border-radius:999px;font-size:12px;font-weight:600;background:var(--badge)}
        .badge.pass{color:var(--pos)} .badge.fail{color:var(--neg)}
        .reasons{background:var(--badge);border-radius:8px;padding:8px 12px;margin:8px 0} .reasons ul{margin:4px 0 0;padding-left:18px}
        .grid2{display:grid;grid-template-columns:1fr 1fr;gap:16px;align-items:start} @media (max-width:820px){.grid2{grid-template-columns:1fr}}
        svg.chart{width:100%;height:auto;display:block}
        svg .grid{stroke:var(--grid);stroke-width:1} svg .zero{stroke:var(--muted);stroke-width:1;stroke-dasharray:3 3}
        svg .axis{fill:var(--muted);font-size:12px} svg .value{fill:var(--fg);font-size:11px}
        svg .series{fill:none;stroke-width:1.6;stroke-linejoin:round} svg .series.c1{stroke:var(--c1)} svg .series.c2{stroke:var(--c2)} svg .series.neg{stroke:var(--neg)}
        svg .area{stroke:none;opacity:.18} svg .area.neg{fill:var(--neg)} svg .area.c1{fill:var(--c1)}
        svg .bar.pos{fill:var(--pos)} svg .bar.neg{fill:var(--neg)}
        .legend{display:flex;gap:16px;font-size:12px;color:var(--muted);margin-top:4px}
        .key{display:inline-block;width:14px;height:3px;border-radius:2px;margin-right:6px;vertical-align:middle}
        .key.c1{background:var(--c1)} .key.c2{background:var(--c2)} .key.neg{background:var(--neg)}
        details summary{cursor:pointer;font-weight:600} details[open] summary{margin-bottom:10px}
        ul.index{margin:0;padding-left:18px} ul.index li{margin:4px 0}
        footer{color:var(--muted);font-size:12px;margin-top:24px}
        """;

    // ------------------------------------------------------------------ formatting

    private static PerformanceMetrics? Metrics(JsonNode? n) => n is JsonObject o ? o.Deserialize<PerformanceMetrics>(Json.Options) : null;

    private static double D(JsonNode? n) => Json.Number(n);

    private static string S(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => n.ToJsonString(),
    };

    private static string N(double? v, int digits, bool trim = false)
    {
        if (v is not double x || double.IsNaN(x)) return "–";
        if (double.IsPositiveInfinity(x)) return "∞";
        if (double.IsNegativeInfinity(x)) return "−∞";
        var s = x.ToString("N" + digits, Inv);
        return trim && s.Contains('.') ? s.TrimEnd('0').TrimEnd('.') : s;
    }

    private static string Pct(double? v, int digits = 2) => v is double x && double.IsFinite(x) ? N(x, digits) + "%" : N(v, digits);
    private static string Money(double v) => double.IsFinite(v) ? (v < 0 ? "−$" : "$") + Math.Abs(v).ToString("N0", Inv) : N(v, 0);
    private static string Sign(double? v) => v is double x && double.IsFinite(x) ? x > 0 ? "pos" : x < 0 ? "neg" : "" : "";
    private static string Day(string iso) => iso.Length >= 10 ? iso[..10] : iso;
    private static string Month(string iso) => iso.Length >= 7 ? iso[..7] : iso;
    private static string Anchor(string s) => "h-" + new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
    private static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);

    private static string FormatParams(IReadOnlyDictionary<string, double> p) =>
        p.Count == 0 ? "no parameters" : string.Join(" · ", p.Select(kv => $"{kv.Key} {N(kv.Value, 4, trim: true)}"));

    private static string Heat(double pct)
    {
        if (!double.IsFinite(pct) || pct == 0) return "";
        double a = Math.Min(60, Math.Abs(pct) * 6);
        return $"background:color-mix(in srgb,var({(pct > 0 ? "--pos" : "--neg")}) {a.ToString("F0", Inv)}%,transparent)";
    }
}
