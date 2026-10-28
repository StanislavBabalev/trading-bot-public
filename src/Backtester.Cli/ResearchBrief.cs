using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backtester.Core;
using Backtester.Core.Analytics;
using Backtester.Core.Engine;
using Backtester.Core.Reporting;
using Backtester.Core.Research;
using Backtester.Core.Strategies;

namespace Backtester.Cli;

/// <summary>One tested hypothesis, re-measured from its registered baseline run on the development window.</summary>
internal sealed record HypothesisDiagnosis(
    string Strategy, string Family, string Description, int Cycle, string Stage, IReadOnlyList<string> KillReasons,
    double NetProfitPct, double ProfitFactor, double Sharpe, double MaxDrawdownPct, double TradesPerYear, double TurnoverPerYear,
    double TimeInMarketPct, double EdgeBeforeCostsPct, double CostsPct, double FundingPct, double CostDragPctPerYear,
    string Cause, IReadOnlyList<string> FailedRuleQuarters, IReadOnlyList<QuarterReturn> Quarters);

/// <summary>
/// Deterministic post-cycle analysis and the sanitized brief for whoever proposes the next hypotheses. Only the
/// development window is used; the locked OOS period never appears. The brief holds aggregate numbers and the
/// hypotheses' rule descriptions: no code, no price or trade rows, no credentials, no file paths.
/// </summary>
internal static class ResearchBrief
{
    // Broad idea families for the "not tried yet" list; the tested ones are subtracted.
    private static readonly string[] FamilyIdeas =
    [
        "trend", "momentum", "mean-reversion", "vol-breakout", "range-structure", "volume", "time-of-day", "regime-switch",
        "funding-carry", "day-of-week-seasonality", "multi-timeframe-trend", "volatility-targeted-trend",
    ];

    public static (string Brief, string Diagnostics, int EstimatedTokens) Write(HuntConfig cfg, BacktestEngine engine, JsonObject log, ExperimentStore store)
    {
        var rules = cfg.Hunt;
        var dev = cfg.ToBacktestConfig(cfg.Split.DevFrom, cfg.Split.DevTo);
        // Unbudgeted: it only re-runs the registered baseline of each tested hypothesis (no new parameter sets).
        var runner = new ExperimentRunner(engine, cfg.Symbol, store);
        var ruleFolds = WalkForward.Generate(cfg.Split.DevFrom, cfg.Split.DevTo, rules.WalkForward.TrainMonths, rules.WalkForward.TestMonths, rules.WalkForward.StepMonths);
        var ruleQuarters = ruleFolds.Select(f => Quarterly.Label(TimeUtil.ToMs(f.TestFrom))).ToList();
        var market = Quarterly.Market(engine.Bars, engine.Funding, dev.FromMs, dev.ToMs);

        var rejected = (log["rejected"] as JsonArray ?? []).OfType<JsonObject>()
            .GroupBy(r => r["strategy"]!.GetValue<string>()).ToDictionary(g => g.Key, g => g.Last());
        var rows = new List<HypothesisDiagnosis>();
        foreach (var name in (log["tested_strategies"] as JsonArray ?? []).Select(n => n!.GetValue<string>()))
        {
            var def = StrategyRegistry.All.FirstOrDefault(d => d.Name == name);
            if (def is null) continue;
            var p = new Dictionary<string, double>(def.Defaults);
            var (res, m) = runner.Run(def, p, dev, new(0, "brief-baseline"));
            var rej = rejected.GetValueOrDefault(name);
            int cycle = (int)Json.Number(rej?["cycle"]);
            double cap = cfg.InitialCapital;
            double costs = m.FeesPaid + m.SlippageCost;
            double edge = m.NetProfit + costs - m.FundingReceived;   // P&L with zero commission, slippage and funding (approximate)
            bool failsDev = !(m.ProfitFactor >= rules.MinProfitFactor) || !(m.Sharpe >= rules.MinSharpe) || m.NetProfit <= 0;
            string cause = edge <= 0 ? "no edge before costs"
                : failsDev && costs >= 0.5 * edge ? "edge consumed by costs"
                : failsDev ? "weak edge"
                : "meets the dev PF/Sharpe floors";
            rows.Add(new HypothesisDiagnosis(def.Name, def.Family, def.Description, cycle, rej?["stage"]?.GetValue<string>() ?? "?",
                (rej?["reasons"] as JsonArray ?? []).Select(x => x!.GetValue<string>()).ToList(),
                m.NetProfitPct, m.ProfitFactor, m.Sharpe, m.MaxDrawdownPct, m.TradesPerYear, m.TurnoverPerYear, m.TimeInMarketPct,
                edge / cap * 100, costs / cap * 100, m.FundingReceived / cap * 100, costs / cap * 100 / m.Years, cause,
                FailedRuleQuarters(cycle, name, ruleQuarters), Quarterly.Returns(res)));
        }

        var trials = File.Exists(Path.Combine("artifacts", "hunt", "trials.jsonl"))
            ? File.ReadLines(Path.Combine("artifacts", "hunt", "trials.jsonl")).Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonSerializer.Deserialize<TrialRecord>(l, Json.Options)!).ToList()
            : [];
        double variance = Statistics.Variance(trials.Select(t => t.DailySharpe).Where(double.IsFinite).ToList());
        double hurdle = DeflatedSharpe.ExpectedMaxSharpe(trials.Count, variance) * Math.Sqrt(365);

        var findings = Findings(rows, market, ruleQuarters, rules, hurdle, trials.Count);
        var recommendations = Recommendations(rows, market, ruleQuarters, cfg);

        string diagPath = Path.Combine(Research.Dir, "diagnostics.json");
        Json.Write(diagPath, new
        {
            generatedUtc = DateTime.UtcNow,
            window = new { from = cfg.Split.DevFrom, to = cfg.Split.DevTo, note = "development window only; the locked OOS period is excluded" },
            ruleQuarters,
            trials = new { count = trials.Count, dailySharpeVariance = variance, expectedMaxAnnualSharpeOfZeroEdgeTrials = hurdle },
            market,
            hypotheses = rows,
            findings,
            recommendations,
        });

        string md = Markdown(cfg, rows, market, ruleQuarters, findings, recommendations, trials.Count, hurdle);
        int tokens = (int)Math.Ceiling(md.Length / 4.0);
        md = md.Replace("{{TOKENS}}", tokens.ToString(CultureInfo.InvariantCulture));
        string briefPath = Path.Combine(Research.Dir, "brief.md");
        AtomicFile.WriteAllText(briefPath, md);
        return (briefPath, diagPath, tokens);
    }

    /// <summary>The rule quarters a hypothesis failed, from its cycle report's baseline walk-forward.</summary>
    private static List<string> FailedRuleQuarters(int cycle, string strategy, List<string> ruleQuarters)
    {
        string path = Path.Combine("artifacts", "hunt", $"cycle_{cycle}", "report.json");
        if (!File.Exists(path)) return [];
        var h = JsonNode.Parse(File.ReadAllText(path))!["hypotheses"]!.AsArray().OfType<JsonObject>()
            .FirstOrDefault(x => x["strategy"]?.GetValue<string>() == strategy);
        return (h?["baselineWalkForward"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(f => f["pass"]?.GetValue<bool>() == false)
            .Select(f => Quarterly.Label(TimeUtil.ToMs(DateTime.Parse(f["testFrom"]!.GetValue<string>(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal))))
            .ToList();
    }

    private static double Median(IEnumerable<double> xs)
    {
        var s = xs.Where(double.IsFinite).OrderBy(x => x).ToList();
        if (s.Count == 0) return double.NaN;
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    private static List<string> Findings(List<HypothesisDiagnosis> rows, List<MarketQuarter> market, List<string> ruleQuarters, HuntRules rules, double hurdle, int trials)
    {
        var f = new List<string>();
        if (rows.Count == 0) return ["No tested hypotheses to analyse."];
        foreach (var g in rows.GroupBy(r => r.Cause).OrderByDescending(g => g.Count()))
            f.Add($"{g.Count()} of {rows.Count} hypotheses: {g.Key} ({string.Join(", ", g.Select(r => r.Strategy))}).");
        f.Add($"Median turnover {Median(rows.Select(r => r.TurnoverPerYear)):F0}x equity a year, median cost drag {Median(rows.Select(r => r.CostDragPctPerYear)):F1}% of starting equity a year " +
              $"(commission + slippage); median edge before costs {Median(rows.Select(r => r.EdgeBeforeCostsPct)):F1}% over the whole window.");

        double volMed = Median(market.Select(m => m.RealizedVolPct)), effMed = Median(market.Select(m => m.EfficiencyRatio));
        foreach (var q in ruleQuarters)
        {
            int failed = rows.Count(r => r.FailedRuleQuarters.Contains(q));
            var mq = market.FirstOrDefault(m => m.Quarter == q);
            if (mq is null) continue;
            f.Add($"Rule quarter {q}: failed by {failed} of {rows.Count}. BTC {mq.BuyHoldPct:+0.0;-0.0}%, realized vol {mq.RealizedVolPct:F0}% (dev median {volMed:F0}%), " +
                  $"efficiency {mq.EfficiencyRatio:F2} (median {effMed:F2}), daily range {mq.MeanDailyRangePct:F1}%.");
        }
        var losers = market.Select(m => (m.Quarter, Losers: rows.Count(r => r.Quarters.FirstOrDefault(x => x.Quarter == m.Quarter) is { } x && !x.MadeMoney)))
            .OrderByDescending(x => x.Losers).Take(3).ToList();
        f.Add($"Quarters where the most continuous dev runs lost money: {string.Join(", ", losers.Select(l => $"{l.Quarter} ({l.Losers} of {rows.Count})"))}.");
        f.Add($"{trials} parameter sets have been evaluated on the development data; the best of that many zero-edge trials would show an annualized Sharpe near {hurdle:F2}. " +
              $"A candidate's dev Sharpe needs to clear that, not just the {rules.MinSharpe:F1} floor, to mean much.");
        return f;
    }

    private static List<string> Recommendations(List<HypothesisDiagnosis> rows, List<MarketQuarter> market, List<string> ruleQuarters, HuntConfig cfg)
    {
        var r = new List<string>();
        if (rows.Count == 0) return r;
        double perRoundTripPct = 2 * (cfg.Costs.CommissionRate + cfg.Costs.SlippageRate) * 100;
        int costBound = rows.Count(x => x.Cause == "edge consumed by costs");
        if (costBound * 3 >= rows.Count)
            r.Add($"Costs decide many outcomes ({costBound} of {rows.Count}): each round trip costs {perRoundTripPct:F2}% of notional. Prefer fewer, longer trades " +
                  "(signals on 4h/daily structure, longer holds) or edges per trade well above that; avoid more 1h fade/continuation variants.");
        if (rows.Count(x => x.Cause == "no edge before costs") * 3 >= rows.Count)
            r.Add("Several ideas lose money even before costs: they are not cost problems, so tuning will not save them; drop the idea rather than its parameters.");

        // Label the rule quarters that at least half the hypotheses failed: quiet (vol < 80% of the dev median) and/or
        // directionless (efficiency < half the median).
        double volMed = Median(market.Select(m => m.RealizedVolPct)), effMed = Median(market.Select(m => m.EfficiencyRatio));
        var labelled = ruleQuarters
            .Where(q => rows.Count(x => x.FailedRuleQuarters.Contains(q)) * 2 >= rows.Count)
            .Select(q => market.FirstOrDefault(m => m.Quarter == q))
            .OfType<MarketQuarter>()
            .Select(m => (m.Quarter, Tags: new[]
            {
                m.RealizedVolPct < 0.8 * volMed ? $"quiet (vol {m.RealizedVolPct:F0}% vs median {volMed:F0}%)" : null,
                m.EfficiencyRatio < 0.5 * effMed ? $"directionless (efficiency {m.EfficiencyRatio:F2} vs median {effMed:F2}, BTC {m.BuyHoldPct:+0;-0}%)" : null,
            }.OfType<string>().ToList()))
            .Where(x => x.Tags.Count > 0)
            .ToList();
        if (labelled.Count > 0)
            r.Add($"The most-failed rule quarters were {string.Join("; ", labelled.Select(x => $"{x.Quarter} {string.Join(" and ", x.Tags)}"))}. " +
                  "A quarter spent flat still fails the rule, so a filter that stands aside is not enough: an idea has to earn something in quiet or range-bound markets too.");

        var tried = rows.Select(x => x.Family).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var untried = FamilyIdeas.Where(fam => !tried.Contains(fam)).ToList();
        if (untried.Count > 0) r.Add($"Families not tried yet: {string.Join(", ", untried)}. Funding-rate history is already available locally.");
        r.Add("Keep the locked OOS window out of idea generation; propose ideas from the development evidence only.");
        return r;
    }

    private static string Markdown(HuntConfig cfg, List<HypothesisDiagnosis> rows, List<MarketQuarter> market, List<string> ruleQuarters,
        List<string> findings, List<string> recommendations, int trials, double hurdle)
    {
        var inv = CultureInfo.InvariantCulture;
        var rules = cfg.Hunt;
        var sb = new StringBuilder();
        sb.AppendLine("# Research brief (sanitized)");
        sb.AppendLine();
        sb.AppendLine($"Generated {DateTime.UtcNow:yyyy-MM-dd} by the local research controller. Aggregate metrics only: no code, price or trade rows, credentials or file paths. " +
                      "Review it before sharing it with anyone. Size: ~{{TOKENS}} tokens (estimated as characters / 4).");
        sb.AppendLine();
        sb.AppendLine("## Setup");
        sb.AppendLine($"- Instrument: {cfg.Symbol} perpetual, {cfg.IntervalMinutes}-minute bars. Development window {cfg.Split.DevFrom:yyyy-MM} to {cfg.Split.DevTo.AddDays(-1):yyyy-MM}; the final holdout is excluded from this brief.");
        sb.AppendLine(string.Create(inv, $"- Costs per side: {cfg.Costs.CommissionRate * 100:0.###}% commission + {cfg.Costs.SlippageRate * 100:0.###}% slippage; funding {(cfg.FundingEnabled ? "on" : "off")}. " +
                      $"Sizing: {cfg.Sizing.RiskPerTrade * 100:0.#}% equity risk per trade, max {cfg.Risk.MaxLeverage:0.#}x leverage, every entry needs a stop."));
        sb.AppendLine(string.Create(inv, $"- Kill rules: PF < {rules.MinProfitFactor}, Sharpe < {rules.MinSharpe}, max DD > {rules.MaxDrawdownPct}%, < {rules.MinTradesPerYear} trades/yr, or more than " +
                      $"{rules.WalkForward.MaxFoldFailures} of {ruleQuarters.Count} rule quarters ({string.Join(", ", ruleQuarters)}) failing. A rule quarter passes only if it makes money; sitting it out fails."));
        sb.AppendLine($"- Survivors are tuned over at most {rules.OptimizerMaxEvaluations} grid points (≤ 4 parameters) and must sit on a plateau, not a range edge.");
        sb.AppendLine(string.Create(inv, $"- Parameter sets evaluated so far: {trials}. Expected best annualized Sharpe from that many zero-edge trials: {hurdle:F2}."));
        sb.AppendLine();
        sb.AppendLine($"## Hypotheses tested ({rows.Count}), default settings on the development window");
        sb.AppendLine("| name | family | net % | PF | Sharpe | max DD % | trades/yr | turnover x/yr | edge before costs % | costs % | cause | failed rule quarters |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
            sb.AppendLine(string.Create(inv, $"| {r.Strategy} ({Short(r.Strategy)}) | {r.Family} | {r.NetProfitPct:F1} | {r.ProfitFactor:F2} | {r.Sharpe:F2} | {r.MaxDrawdownPct:F1} | {r.TradesPerYear:F0} | " +
                                             $"{r.TurnoverPerYear:F0} | {r.EdgeBeforeCostsPct:F1} | {r.CostsPct:F1} | {r.Cause} | {(r.FailedRuleQuarters.Count == 0 ? "-" : string.Join(" ", r.FailedRuleQuarters))} |"));
        sb.AppendLine();
        sb.AppendLine("Rules of each (so new ideas do not repeat them):");
        foreach (var r in rows) sb.AppendLine($"- {r.Strategy}: {r.Description}");
        sb.AppendLine();
        sb.AppendLine("## Quarter by quarter (net % of each continuous dev run; rule quarters marked *)");
        sb.Append("| quarter | BTC % | vol % | efficiency |");
        foreach (var r in rows) sb.Append($" {Short(r.Strategy)} |");
        sb.AppendLine(" losing runs |");
        sb.Append("|---|---|---|---|");
        foreach (var _ in rows) sb.Append("---|");
        sb.AppendLine("---|");
        foreach (var m in market)
        {
            sb.Append(string.Create(inv, $"| {m.Quarter}{(ruleQuarters.Contains(m.Quarter) ? "*" : "")} | {m.BuyHoldPct:F0} | {m.RealizedVolPct:F0} | {m.EfficiencyRatio:F2} |"));
            int losing = 0;
            foreach (var r in rows)
            {
                var q = r.Quarters.FirstOrDefault(x => x.Quarter == m.Quarter);
                if (q is not null && !q.MadeMoney) losing++;
                sb.Append(q is null ? " - |" : string.Create(inv, $" {q.ReturnPct:F1} |"));
            }
            sb.AppendLine($" {losing} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Findings");
        foreach (var f in findings) sb.AppendLine($"- {f}");
        sb.AppendLine();
        sb.AppendLine("## Directions");
        foreach (var r in recommendations) sb.AppendLine($"- {r}");
        sb.AppendLine();
        sb.AppendLine("## Requested output");
        sb.AppendLine($"Propose at most {rules.HypothesesPerCycle} new, distinct hypotheses as JSON only, in this shape (it is validated locally before anything is built):");
        sb.AppendLine("```json");
        sb.AppendLine("{ \"hypotheses\": [ { \"name\": \"kebab-case-name\", \"family\": \"...\", \"entry\": \"...\", \"exit\": \"...\", \"stop\": \"...\", \"rationale\": \"...\",");
        sb.AppendLine("    \"parameters\": [ { \"name\": \"lookback\", \"default\": 48, \"min\": 24, \"max\": 96, \"step\": 24 } ] } ] }");
        sb.AppendLine("```");
        sb.AppendLine($"Each: 1-4 parameters, defaults on their grid, grid ≤ {rules.OptimizerMaxEvaluations - 8} points, a stop on every entry, rules computable from {cfg.IntervalMinutes}-minute OHLCV and funding without look-ahead.");
        return sb.ToString();
    }

    private static string Short(string name) => string.Concat(name.Split('-').Select(w => w[0])).ToUpperInvariant();
}
