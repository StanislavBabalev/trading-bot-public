using System.Text.Json.Nodes;
using Backtester.Core;
using Backtester.Core.Analytics;
using Backtester.Core.Engine;
using Backtester.Core.Reporting;
using static Backtester.Tests.TestData;

namespace Backtester.Tests;

public class ReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bt_report_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private (BacktestResult Result, List<Bar> Bars) SampleRun()
    {
        var rng = new Random(3);
        double p = 100;
        var bars = Enumerable.Range(0, 24 * 120).Select(i =>
        {
            double o = p;
            p *= Math.Exp((rng.NextDouble() - 0.49) * 0.02);
            return new Bar(T0 + i * Hour, o, Math.Max(o, p) + 0.2, Math.Min(o, p) - 0.2, p, 1);
        }).ToList();
        var r = new BacktestEngine(bars, [new FundingRate(T0 + 8 * Hour, 0.0001)])
            .Run(new ScriptedStrategy(i => ((i / 48) % 3) switch { 0 => Signal.Long(), 1 => Signal.Flat, _ => Signal.Short() }),
                Config(bars.Count, commission: 0.0004, slippage: 0.0002), new Dictionary<string, double> { ["x"] = 1 });
        return (r, bars);
    }

    [Fact]
    public void Run_artifacts_round_trip_through_csv()
    {
        var (r, _) = SampleRun();
        var m = PerformanceMetrics.Compute(r);
        ArtifactWriter.WriteRun(_dir, r, m, null);

        var loaded = RunArtifacts.Load(_dir);
        Assert.Equal(r.Trades.Count, loaded.Trades.Count);
        Assert.Equal(r.Equity.Count, loaded.Equity.Count);
        Assert.Equal(r.Trades[0].NetPnl, loaded.Trades[0].NetPnl, 9);
        Assert.Equal(r.Trades[^1].ExitReason, loaded.Trades[^1].ExitReason);
        Assert.Equal(m.NetProfitPct, loaded.Metrics.NetProfitPct, 9);
        Assert.Equal(r.Config.Costs, loaded.Config.Costs);
        Assert.Equal(1, loaded.Parameters["x"]);
    }

    [Fact]
    public void Run_page_is_self_contained_html_with_charts_and_tables()
    {
        var (r, bars) = SampleRun();
        var bench = Benchmark.BuyAndHold(bars, r.Config);
        ArtifactWriter.WriteRun(_dir, r, PerformanceMetrics.Compute(r), PerformanceMetrics.Compute(bench));

        var html = HtmlReport.RunPage(RunArtifacts.Load(_dir), bench, "../index.html");
        Assert.StartsWith("<!doctype html>", html.TrimStart());
        Assert.Contains("<svg", html);
        Assert.Contains("Monthly returns", html);
        Assert.Contains("Trade list", html);
        Assert.Contains("Buy &amp; hold", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
    }

    [Fact]
    public void Cycle_page_renders_rejections_folds_and_missing_runs()
    {
        var m = PerformanceMetrics.Compute(SampleRun().Result);
        var metrics = System.Text.Json.JsonSerializer.SerializeToNode(m, Json.Options);
        var report = new JsonObject
        {
            ["cycle"] = 7,
            ["timestampUtc"] = "2026-10-26T15:00:00Z",
            ["trials"] = new JsonObject { ["thisCycle"] = 1, ["cumulative"] = 31, ["dailySharpeVariance"] = 0.0004 },
            ["buyAndHold"] = new JsonObject { ["dev"] = metrics!.DeepClone() },
            ["hypotheses"] = new JsonArray(new JsonObject
            {
                ["strategy"] = "toy <strategy>",
                ["family"] = "trend",
                ["rules"] = "buy low & sell high",
                ["verdict"] = "REJECTED",
                ["stage"] = "dev-baseline",
                ["killReasons"] = new JsonArray("PF 0.90 < 1.10"),
                ["baselineDev"] = metrics.DeepClone(),
                ["baselineWalkForward"] = new JsonArray(new JsonObject
                {
                    ["fold"] = 1, ["testFrom"] = "2022-01-01", ["testTo"] = "2022-04-01", ["netProfitPct"] = -2.5,
                    ["profitFactor"] = 0.8, ["sharpe"] = -1.0, ["maxDrawdownPct"] = 4.0, ["trades"] = 20, ["pass"] = false,
                }),
                ["artifacts"] = new JsonObject { ["baselineDev"] = Path.Combine(_dir, "missing") },
            }),
            ["warnings"] = new JsonArray("be careful"),
        };

        var html = HtmlReport.CyclePage(report, _ => null, _ => null, _ => null);
        Assert.Contains("Hunt cycle 7", html);
        Assert.Contains("toy &lt;strategy&gt;", html);
        Assert.Contains("PF 0.90 &lt; 1.10", html);
        Assert.Contains("Walk-forward test folds", html);
        Assert.Contains("None", html);
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public void Hunt_overview_ranks_every_hypothesis_across_cycles_and_links_to_its_cycle_page()
    {
        var m = System.Text.Json.JsonSerializer.SerializeToNode(PerformanceMetrics.Compute(SampleRun().Result), Json.Options)!;
        JsonNode Dev(double sharpe) { var d = m.DeepClone(); d["sharpe"] = sharpe; return d; }
        JsonObject Hyp(string name, double sharpe, JsonNode? oos = null) => new()
        {
            ["strategy"] = name, ["family"] = "trend", ["verdict"] = "REJECTED", ["stage"] = oos is null ? "dev-baseline" : "locked-oos",
            ["killReasons"] = new JsonArray("Sharpe < 0.50"), ["baselineDev"] = Dev(sharpe), ["lockedOos"] = oos,
            ["baselineWalkForward"] = new JsonArray(
                new JsonObject { ["fold"] = 1, ["testFrom"] = "2022-01-01", ["netProfitPct"] = 1.5, ["pass"] = true },
                new JsonObject { ["fold"] = 2, ["testFrom"] = "2022-07-01", ["netProfitPct"] = -0.4, ["pass"] = false }),
        };
        JsonObject Cycle(int n, int cumulative, params JsonObject[] hyps) => new()
        {
            ["cycle"] = n, ["timestampUtc"] = $"2026-10-2{n}T10:00:00Z",
            ["trials"] = new JsonObject { ["thisCycle"] = hyps.Length, ["cumulative"] = cumulative },
            ["buyAndHold"] = new JsonObject { ["dev"] = m.DeepClone() }, ["hypotheses"] = new JsonArray(hyps),
        };

        var html = HtmlReport.HuntOverviewPage(
        [
            new(Cycle(4, 7, Hyp("late <winner>", 0.9, Dev(-0.3))), "cycle_4/report.html", null),
            new(Cycle(3, 5, Hyp("middle", 0.2), Hyp("worst", -1.1)), "cycle_3/report.html", "cycle_3_ideas/research.md"),
        ]);

        Assert.Contains("Hunt overview", html);
        int a = html.IndexOf("late &lt;winner&gt;", StringComparison.Ordinal), b = html.IndexOf(">middle<", StringComparison.Ordinal), c = html.IndexOf(">worst<", StringComparison.Ordinal);
        Assert.True(a >= 0 && a < b && b < c, "rows are not ordered by development Sharpe");
        Assert.Contains("href=\"cycle_4/report.html#h-late--winner-\"", html);
        Assert.Contains("href=\"cycle_3_ideas/research.md\"", html);
        Assert.Contains("22Q1 &#183; 22Q3", html);      // HtmlEncode writes the middle dot as an entity
        Assert.Contains("(1/2)", html);
        Assert.Contains("Sharpe -0.30", html);           // the locked OOS column
        Assert.Contains("Buy &amp; hold (dev)", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
    }

    [Fact]
    public void Decimation_keeps_extremes()
    {
        var pts = Enumerable.Range(0, 10_000).Select(i => ((long)i, i == 4321 ? -50.0 : i == 777 ? 99.0 : Math.Sin(i / 100.0))).ToList();
        var d = SvgCharts.Decimate(pts, 400);
        Assert.True(d.Count <= 404);
        Assert.Contains(d, p => p.V == -50.0);
        Assert.Contains(d, p => p.V == 99.0);
        Assert.Equal(pts[0], d[0]);
        Assert.Equal(pts[^1], d[^1]);
    }

    [Fact]
    public void Nice_ticks_cover_the_range()
    {
        var t = SvgCharts.NiceTicks(9_430, 14_870, 5);
        Assert.True(t[0] <= 9_430 && t[^1] >= 14_870);
        Assert.InRange(t.Length, 3, 9);
    }
}
