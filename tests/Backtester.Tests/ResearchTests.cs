using System.Text.Json;
using Backtester.Core;
using Backtester.Core.Analytics;
using Backtester.Core.Engine;
using Backtester.Core.Optimization;
using Backtester.Core.Reporting;
using Backtester.Core.Research;
using Backtester.Core.Strategies;
using static Backtester.Tests.TestData;

namespace Backtester.Tests;

public class ResearchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bt_research_{Guid.NewGuid():N}");
    private readonly List<Bar> _bars = RandomOhlcv(24 * 60, seed: 11);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string StorePath => Path.Combine(_dir, "experiments.jsonl");
    private BacktestEngine Engine() => new(_bars, [new FundingRate(T0 + 8 * Hour, 0.0001)]);
    private BacktestConfig Cfg() => Config(_bars.Count, commission: 0.0004, slippage: 0.0002,
        sizing: new SizingConfig { Mode = SizingMode.RiskPerTrade, RiskPerTrade = 0.01 });

    /// <summary>impulse-bar-continuation's real rules, counting how often a strategy is created (i.e. actually run).</summary>
    private static StrategyDefinition Counting(Action onCreate, int version = 1)
    {
        var real = StrategyRegistry.Get("impulse-bar-continuation");
        return real with { Create = p => { onCreate(); return real.Create(p); }, Version = version };
    }

    private static readonly Dictionary<string, double> P = new() { ["rangeAtr"] = 2.0, ["bodyFrac"] = 0.5, ["holdBars"] = 6, ["atrMult"] = 2.0 };

    [Fact]
    public void Grid_enumeration_is_complete_distinct_and_in_optimizer_order()
    {
        ParamSpec[] space = [new("a", 1, 3, 1), new("b", 0.1, 0.3, 0.1), new("c", 10, 20, 10)];
        var points = GridOptimizer.Enumerate(space, new Dictionary<string, double> { ["fixed"] = 7 }).ToList();
        Assert.Equal(3 * 3 * 2, points.Count);
        Assert.Equal(points.Count, points.Select(p => string.Join(",", p.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value:R}"))).Distinct().Count());
        Assert.All(points, p => Assert.Equal(7, p["fixed"]));
        Assert.Equal((1.0, 0.1, 10.0), (points[0]["a"], points[0]["b"], points[0]["c"]));
        Assert.Equal((3.0, 0.3, 20.0), (points[^1]["a"], points[^1]["b"], points[^1]["c"]));

        var seen = new List<IReadOnlyDictionary<string, double>>();
        GridOptimizer.Run(space, p => { seen.Add(p); return new TrialOutcome(p, p["a"], 2, 1, 100, 0.05); },
            new OptimizerOptions { MaxEvaluations = 1000 });
        Assert.Equal(points.Select(p => (p["a"], p["b"], p["c"])), seen.Take(points.Count).Select(p => (p["a"], p["b"], p["c"])));
    }

    [Fact]
    public void Prefetching_grid_points_does_not_change_the_optimizer_report()
    {
        ParamSpec[] space = [new("x", 0, 10, 1), new("y", 0, 6, 1)];
        TrialOutcome Eval(IReadOnlyDictionary<string, double> p) =>
            new(p, -Math.Pow(p["x"] - 5, 2) - Math.Pow(p["y"] - 3, 2), 2, 1, 100, 0.05);
        var order1 = new List<string>();
        var order2 = new List<string>();
        int prefetched = 0;
        var plain = GridOptimizer.Run(space, p => { order1.Add($"{p["x"]},{p["y"]}"); return Eval(p); }, new OptimizerOptions { MaxEvaluations = 1000 });
        var withPrefetch = GridOptimizer.Run(space, p => { order2.Add($"{p["x"]},{p["y"]}"); return Eval(p); }, new OptimizerOptions { MaxEvaluations = 1000 },
            prefetch: pts => prefetched = pts.Count);
        Assert.Equal(77, prefetched);
        Assert.Equal(order1, order2);
        Assert.Equal(JsonSerializer.Serialize(plain, Json.Compact), JsonSerializer.Serialize(withPrefetch, Json.Compact));
    }

    [Fact]
    public void Identity_is_stable_and_changes_with_every_input()
    {
        var engine = Engine();
        var cfg = Cfg();
        ExperimentIdentity Id(IReadOnlyDictionary<string, double> p, BacktestConfig c, int version = 1, string symbol = "BTCUSDT", string? data = null) =>
            ExperimentIdentity.Create("s", version, p, symbol, data ?? engine.DataFingerprint, c);

        var a = new Dictionary<string, double> { ["x"] = 1, ["y"] = 2 };
        var reordered = new Dictionary<string, double> { ["y"] = 2, ["x"] = 1 };
        string id = Id(a, cfg).Id;
        Assert.Equal(id, Id(reordered, cfg).Id);
        Assert.Equal(32, id.Length);

        var roundTrip = JsonSerializer.Deserialize<ExperimentIdentity>(JsonSerializer.Serialize(Id(a, cfg), Json.Compact), Json.Compact)!;
        Assert.Equal(id, roundTrip.Id);

        string[] variants =
        [
            Id(new Dictionary<string, double> { ["x"] = 1, ["y"] = 2.0000000001 }, cfg).Id,
            Id(a, cfg, version: 2).Id,
            Id(a, cfg, symbol: "ETHUSDT").Id,
            Id(a, cfg, data: "0000").Id,
            Id(a, cfg with { FromMs = cfg.FromMs + Hour }).Id,
            Id(a, cfg with { ToMs = cfg.ToMs - Hour }).Id,
            Id(a, cfg with { Costs = cfg.Costs with { CommissionRate = 0.0005 } }).Id,
            Id(a, cfg with { Costs = cfg.Costs with { SlippageRate = 0.0003 } }).Id,
            Id(a, cfg with { FundingEnabled = false }).Id,
            Id(a, cfg with { Sizing = cfg.Sizing with { RiskPerTrade = 0.02 } }).Id,
            Id(a, cfg with { Risk = cfg.Risk with { MaxLeverage = 2 } }).Id,
            Id(a, cfg with { InitialCapital = 20_000 }).Id,
            (Id(a, cfg) with { EngineVersion = "2" }).Id,
            (Id(a, cfg) with { CodeFingerprint = "other-build" }).Id,
        ];
        Assert.DoesNotContain(id, variants);
        Assert.Equal(variants.Length, variants.Distinct().Count());
    }

    [Fact]
    public void Data_fingerprint_changes_when_one_bar_or_funding_rate_changes()
    {
        string baseline = Engine().DataFingerprint;
        Assert.Equal(baseline, Engine().DataFingerprint);
        var bars = _bars.ToList();
        bars[100] = bars[100] with { Close = bars[100].Close + 0.01 };
        Assert.NotEqual(baseline, new BacktestEngine(bars, [new FundingRate(T0 + 8 * Hour, 0.0001)]).DataFingerprint);
        Assert.NotEqual(baseline, new BacktestEngine(_bars, [new FundingRate(T0 + 8 * Hour, 0.0002)]).DataFingerprint);
    }

    [Fact]
    public void Stored_results_are_reused_bit_for_bit_and_survive_a_restart()
    {
        int created = 0;
        var def = Counting(() => created++);
        var engine = Engine();
        PerformanceMetrics first;
        using (var store = ExperimentStore.Open(StorePath))
        {
            var runner = new ExperimentRunner(engine, "BTCUSDT", store);
            first = runner.Evaluate(def, P, Cfg(), new(1, "t"))!;
            var again = runner.Evaluate(def, P, Cfg(), new(1, "t"))!;
            Assert.Equal(1, created);
            Assert.Same(first, again);
            Assert.Equal((1, 1), (runner.Budget.NewExperiments, runner.Budget.CacheHits));
        }

        using (var reopened = ExperimentStore.Open(StorePath))
        {
            var runner = new ExperimentRunner(engine, "BTCUSDT", reopened);
            var resumed = runner.Evaluate(def, P, Cfg(), new(1, "t"))!;
            Assert.Equal(1, created);
            Assert.Equal(JsonSerializer.Serialize(first, Json.Compact), JsonSerializer.Serialize(resumed, Json.Compact));
            Assert.Equal(first.Sharpe, resumed.Sharpe);
            Assert.Equal(first.DailySharpe, resumed.DailySharpe);
            Assert.Equal(0, runner.Budget.NewExperiments);
        }
    }

    [Fact]
    public void A_changed_input_or_strategy_version_is_never_served_from_the_store()
    {
        int created = 0;
        var engine = Engine();
        using var store = ExperimentStore.Open(StorePath);
        var runner = new ExperimentRunner(engine, "BTCUSDT", store);
        runner.Evaluate(Counting(() => created++), P, Cfg(), new(1, "t"));
        runner.Evaluate(Counting(() => created++), P, Cfg() with { Costs = new CostModel(0.0004, 0.0004) }, new(1, "t"));
        runner.Evaluate(Counting(() => created++), P, Cfg() with { FundingEnabled = false }, new(1, "t"));
        runner.Evaluate(Counting(() => created++, version: 2), P, Cfg(), new(1, "t"));
        runner.Evaluate(Counting(() => created++), new Dictionary<string, double>(P) { ["holdBars"] = 12 }, Cfg(), new(1, "t"));
        Assert.Equal(5, created);

        var otherData = new ExperimentRunner(new BacktestEngine(_bars.Take(_bars.Count - 1).ToList(), []), "BTCUSDT", store);
        otherData.Evaluate(Counting(() => created++), P, Config(_bars.Count - 1, 0.0004, 0.0002, new SizingConfig { Mode = SizingMode.RiskPerTrade }), new(1, "t"));
        Assert.Equal(6, created);
    }

    [Fact]
    public void A_torn_last_line_is_skipped_and_the_store_keeps_working()
    {
        var engine = Engine();
        using (var store = ExperimentStore.Open(StorePath))
            new ExperimentRunner(engine, "BTCUSDT", store).Evaluate(StrategyRegistry.Get("impulse-bar-continuation"), P, Cfg(), new(1, "t"));
        File.AppendAllText(StorePath, "{\"id\":\"half a record");   // crash mid-write

        int created = 0;
        using (var store = ExperimentStore.Open(StorePath))
        {
            Assert.Equal(1, store.SkippedLines);
            Assert.Equal(1, store.Count);
            var runner = new ExperimentRunner(engine, "BTCUSDT", store);
            runner.Evaluate(Counting(() => created++), P, Cfg(), new(1, "t"));
            runner.Evaluate(Counting(() => created++), P, Cfg() with { FundingEnabled = false }, new(1, "t"));
        }
        Assert.Equal(1, created);
        using var reread = ExperimentStore.Open(StorePath);
        Assert.Equal(2, reread.Count);
        Assert.Equal(1, reread.SkippedLines);
    }

    [Fact]
    public void A_record_whose_identity_no_longer_hashes_to_its_id_is_counted_stale_not_corrupt()
    {
        using (var store = ExperimentStore.Open(StorePath))
            new ExperimentRunner(Engine(), "BTCUSDT", store).Evaluate(StrategyRegistry.Get("impulse-bar-continuation"), P, Cfg(), new(1, "t"));
        // As if written by an older build whose identity lacked a field: the stored id no longer matches the identity.
        File.WriteAllText(StorePath, File.ReadAllText(StorePath).Replace("\"engineVersion\":\"1\"", "\"engineVersion\":\"0\""));
        using var reread = ExperimentStore.Open(StorePath);
        Assert.Equal(0, reread.SkippedLines);
        Assert.Equal(1, reread.StaleLines);
        Assert.Equal(0, reread.Count);
    }

    [Fact]
    public void A_failed_backtest_is_never_stored()
    {
        var real = StrategyRegistry.Get("impulse-bar-continuation");
        var broken = real with { Create = _ => new ThrowingStrategy() };
        using var store = ExperimentStore.Open(StorePath);
        var runner = new ExperimentRunner(Engine(), "BTCUSDT", store);
        Assert.Throws<InvalidOperationException>(() => runner.Evaluate(broken, P, Cfg(), new(1, "t")));
        Assert.Equal(0, store.Count);
        Assert.Throws<InvalidOperationException>(() => runner.Evaluate(broken, P, Cfg(), new(1, "t")));
    }

    [Fact]
    public void Invalid_parameters_are_rejected_before_any_backtest()
    {
        int created = 0;
        var def = StrategyRegistry.Get("ema-cross-atr");
        var counting = def with { Create = p => { var s = def.Create(p); if (s is not null) created++; return s; } };
        var runner = new ExperimentRunner(Engine(), "BTCUSDT", ExperimentStore.InMemory());
        var bad = new Dictionary<string, double>(def.Defaults) { ["fast"] = 300, ["slow"] = 100 };
        Assert.Null(runner.Evaluate(counting, bad, Cfg(), new(1, "t")));
        Assert.Equal((0, 0), (created, runner.Budget.NewExperiments));
    }

    [Fact]
    public void The_budget_stops_new_work_but_not_stored_results()
    {
        var def = StrategyRegistry.Get("impulse-bar-continuation");
        var engine = Engine();
        using var store = ExperimentStore.Open(StorePath);
        new ExperimentRunner(engine, "BTCUSDT", store).Evaluate(def, P, Cfg(), new(1, "t"));

        var budget = new ExperimentBudget { MaxNewExperiments = 2 };
        var runner = new ExperimentRunner(engine, "BTCUSDT", store, budget);
        Assert.NotNull(runner.Evaluate(def, P, Cfg(), new(1, "t")));                                    // stored: free
        runner.Evaluate(def, new Dictionary<string, double>(P) { ["holdBars"] = 12 }, Cfg(), new(1, "t"));
        runner.Evaluate(def, new Dictionary<string, double>(P) { ["holdBars"] = 18 }, Cfg(), new(1, "t"));
        var stop = Assert.Throws<ResearchStoppedException>(() => runner.Evaluate(def, new Dictionary<string, double>(P) { ["holdBars"] = 24 }, Cfg(), new(1, "t")));
        Assert.Contains("2 new experiments", stop.Message);
        Assert.Equal((2, 1), (budget.NewExperiments, budget.CacheHits));
        Assert.Equal(3, store.Count);
    }

    [Fact]
    public void Per_cycle_limit_stop_file_and_runtime_limit_stop_the_run()
    {
        var def = StrategyRegistry.Get("impulse-bar-continuation");
        Directory.CreateDirectory(_dir);
        string stopFile = Path.Combine(_dir, "STOP");
        var budget = new ExperimentBudget { StopFile = stopFile };
        var runner = new ExperimentRunner(Engine(), "BTCUSDT", ExperimentStore.InMemory(), budget);

        budget.BeginCycle(1);
        runner.Evaluate(def, P, Cfg(), new(1, "t"));
        Assert.Throws<ResearchStoppedException>(() => runner.Evaluate(def, new Dictionary<string, double>(P) { ["holdBars"] = 12 }, Cfg(), new(1, "t")));
        budget.BeginCycle(null);
        runner.Evaluate(def, new Dictionary<string, double>(P) { ["holdBars"] = 12 }, Cfg(), new(2, "t"));

        File.WriteAllText(stopFile, "stop");
        var e = Assert.Throws<ResearchStoppedException>(() => runner.Evaluate(def, new Dictionary<string, double>(P) { ["holdBars"] = 18 }, Cfg(), new(2, "t")));
        Assert.Contains("stop requested", e.Message);

        var expired = new ExperimentBudget { MaxRuntime = TimeSpan.Zero };
        Assert.Throws<ResearchStoppedException>(() =>
            new ExperimentRunner(Engine(), "BTCUSDT", ExperimentStore.InMemory(), expired).Evaluate(def, P, Cfg(), new(1, "t")));
    }

    [Fact]
    public void Parallel_batches_match_sequential_results_in_input_order()
    {
        var def = StrategyRegistry.Get("impulse-bar-continuation");
        var engine = Engine();
        var jobs = GridOptimizer.Enumerate(def.DefaultSpace, def.Defaults).Take(24)
            .Select(p => new ExperimentJob(def, p, Cfg())).ToList();
        jobs.Add(new ExperimentJob(StrategyRegistry.Get("ema-cross-atr"), new Dictionary<string, double>(StrategyRegistry.Get("ema-cross-atr").Defaults) { ["fast"] = 300 }, Cfg()));
        var sequential = new ExperimentRunner(engine, "BTCUSDT", ExperimentStore.InMemory(), workers: 1).EvaluateMany(jobs, new(1, "t"));
        var parallel = new ExperimentRunner(engine, "BTCUSDT", ExperimentStore.InMemory(), workers: 8).EvaluateMany(jobs, new(1, "t"));
        Assert.Null(parallel[^1]);
        Assert.Equal(sequential.Select(m => JsonSerializer.Serialize(m, Json.Compact)), parallel.Select(m => JsonSerializer.Serialize(m, Json.Compact)));
    }

    [Fact]
    public void A_parallel_batch_stopped_by_the_budget_keeps_what_finished()
    {
        var def = StrategyRegistry.Get("impulse-bar-continuation");
        using var store = ExperimentStore.Open(StorePath);
        var runner = new ExperimentRunner(Engine(), "BTCUSDT", store, new ExperimentBudget { MaxNewExperiments = 5 }, workers: 4);
        var jobs = GridOptimizer.Enumerate(def.DefaultSpace, def.Defaults).Take(20).Select(p => new ExperimentJob(def, p, Cfg())).ToList();
        Assert.Throws<ResearchStoppedException>(() => runner.EvaluateMany(jobs, new(1, "t")));
        Assert.Equal(5, store.Count);

        var resumed = new ExperimentRunner(Engine(), "BTCUSDT", store, workers: 4);
        resumed.EvaluateMany(jobs, new(1, "t"));
        Assert.Equal((15, 5), (resumed.Budget.NewExperiments, resumed.Budget.CacheHits));
    }

    [Fact]
    public void Feature_cache_series_match_bar_by_bar_indicators()
    {
        var engine = Engine();
        var atr = engine.Features.Atr(14);
        var median = engine.Features.AtrMedian(14, 50);
        Assert.Same(atr, engine.Features.Atr(14));
        var a = new Backtester.Core.Indicators.Atr(14);
        var m = new Backtester.Core.Indicators.RollingMedian(50);
        for (int i = 0; i < _bars.Count; i++)
        {
            a.Update(_bars[i]);
            if (a.IsReady) m.Add(a.Value);
            Assert.Equal(a.IsReady ? a.Value : double.NaN, atr[i]);
            Assert.Equal(m.IsReady ? m.Value : double.NaN, median[i]);
        }
        var parallel = new double[8][];
        Parallel.For(0, 8, i => parallel[i] = engine.Features.AtrMedian(14, 72));
        Assert.All(parallel, s => Assert.Same(parallel[0], s));
    }

    [Theory]
    [InlineData(360, 24, 0.10, 2.0)]
    [InlineData(48, 12, 0.20, 1.5)]
    [InlineData(100, 30, 0.05, 2.5)]
    public void Vol_regime_switch_on_the_feature_cache_trades_exactly_like_the_original(int regimeLen, int channelLen, double edgeFrac, double atrMult)
    {
        var bars = RandomOhlcv(24 * 90, seed: regimeLen);
        var engine = new BacktestEngine(bars, []);
        var cfg = Config(bars.Count, 0.0004, 0.0002, new SizingConfig { Mode = SizingMode.RiskPerTrade, RiskPerTrade = 0.01 }, fromMs: T0 + 24 * 20 * Hour);
        var cached = engine.Run(new VolRegimeSwitch(regimeLen, channelLen, edgeFrac, atrMult), cfg);
        var legacy = engine.Run(new LegacyVolRegimeSwitch(regimeLen, channelLen, edgeFrac, atrMult), cfg);
        Assert.NotEmpty(legacy.Trades);
        Assert.Equal(legacy.Trades, cached.Trades);
        Assert.Equal(legacy.Equity, cached.Equity);
        Assert.Equal(legacy.FinalEquity, cached.FinalEquity);
    }

    [Fact]
    public void Quarterly_returns_chain_quarter_ends_and_bucket_trades_by_exit()
    {
        long q2 = TimeUtil.ToMs(new DateTime(2022, 4, 1, 0, 0, 0, DateTimeKind.Utc));
        long q3 = TimeUtil.ToMs(new DateTime(2022, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        long q4 = TimeUtil.ToMs(new DateTime(2022, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var r = new BacktestResult
        {
            StrategyName = "x", Parameters = new Dictionary<string, double>(), Config = Config(1),
            Equity =
            [
                new(q2 - 2 * Hour, 10_100, 0), new(q2, 10_200, 0),            // a close at exactly 00:00 belongs to the quarter that ended
                new(q2 + Hour, 10_300, 0), new(q3, 9_270, 0),
                new(q3 + Hour, 9_270, 0), new(q4, 9_270, 0),                 // flat quarter: does not make money
            ],
            Trades =
            [
                new Trade(1, q2 - 5 * Hour, 1, q2, 2, 1, 150, 0, 0, ExitReason.Signal, 5),        // fill at the Q2 open: counts in Q2
                new Trade(1, q2 - 9 * Hour, 1, q2 - Hour, 2, 1, 200, 0, 0, ExitReason.Stop, 3),
                new Trade(-1, q3 - 9 * Hour, 1, q4, 2, 1, -10, 0, 0, ExitReason.EndOfTest, 3),  // window-end exit: counts in Q3
            ],
            StartMs = q2 - 3 * Hour, EndMs = q4,
        };
        var rows = Quarterly.Returns(r);
        Assert.Equal(["2022Q1", "2022Q2", "2022Q3"], rows.Select(x => x.Quarter));
        Assert.Equal((10_000.0, 10_200.0, 2.0), (rows[0].StartEquity, rows[0].EndEquity, Math.Round(rows[0].ReturnPct, 10)));
        Assert.Equal((10_200.0, 9_270.0), (rows[1].StartEquity, rows[1].EndEquity));
        Assert.Equal(-9.117647, rows[1].ReturnPct, 6);
        Assert.Equal((1, 200.0), (rows[0].TradesClosed, rows[0].ClosedTradesNetPnl));
        Assert.Equal((1, 150.0), (rows[1].TradesClosed, rows[1].ClosedTradesNetPnl));
        Assert.Equal((1, -10.0), (rows[2].TradesClosed, rows[2].ClosedTradesNetPnl));
        Assert.Equal([true, false, false], rows.Select(x => x.MadeMoney));
    }

    [Fact]
    public void Market_quarters_measure_return_trend_and_drawdown()
    {
        // Q1: straight-line rise; Q2: up then back down to where it started.
        long q1 = TimeUtil.ToMs(new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        long q2 = TimeUtil.ToMs(new DateTime(2022, 4, 1, 0, 0, 0, DateTimeKind.Utc));
        long q3 = TimeUtil.ToMs(new DateTime(2022, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var bars = new List<Bar>();
        double price = 100;
        for (long t = q1; t < q3; t += Hour)
        {
            double next = t < q2 ? price * 1.0001 : t < q2 + (q3 - q2) / 2 ? price * 1.0002 : price / 1.0002;
            bars.Add(new Bar(t, price, Math.Max(price, next), Math.Min(price, next), next, 1));
            price = next;
        }
        var rows = Quarterly.Market(bars, [new FundingRate(q1, 0.0001), new FundingRate(q2, 0.0003)], q1, q3);
        Assert.Equal(["2022Q1", "2022Q2"], rows.Select(r => r.Quarter));
        Assert.Equal((Math.Pow(1.0001, (q2 - q1) / Hour) - 1) * 100, rows[0].BuyHoldPct, 6);
        Assert.Equal(1.0, rows[0].EfficiencyRatio, 9);
        Assert.Equal(0, rows[0].MaxDrawdownPct, 9);
        Assert.True(rows[1].EfficiencyRatio < 0.01);
        Assert.True(rows[1].MaxDrawdownPct > 10);
        Assert.Equal((0.0001, 0.0003), (rows[0].MeanFundingRate, rows[1].MeanFundingRate));
    }

    private sealed class ThrowingStrategy : IStrategy
    {
        public string Name => "throws";
        public Signal OnBarClose(StrategyContext ctx) => ctx.Index == 100 ? throw new InvalidOperationException("strategy bug") : Signal.Flat;
    }
}

/// <summary>vol-regime-switch exactly as first implemented (bar-by-bar ATR and rolling median), kept to prove the cached version is equivalent.</summary>
internal sealed class LegacyVolRegimeSwitch(int regimeLen, int channelLen, double edgeFrac, double atrMult) : EntryExitStrategy
{
    private const int FadeMaxHold = 24;
    private const double FadeStopBufferAtr = 0.5;
    private readonly Backtester.Core.Indicators.Atr _atr = new(14);
    private readonly Backtester.Core.Indicators.RollingMedian _atrMedian = new(regimeLen);
    private readonly Backtester.Core.Indicators.RollingExtreme _hi = new(channelLen, max: true), _lo = new(channelLen, max: false);
    private readonly Backtester.Core.Indicators.RollingExtreme _exitHi = new(Math.Max(1, channelLen / 2), max: true), _exitLo = new(Math.Max(1, channelLen / 2), max: false);
    private bool _trendTrade;

    public override string Name => "legacy-vol-regime-switch";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        _atr.Update(b);
        if (_atr.IsReady) _atrMedian.Add(_atr.Value);
        var s = Hold;
        if (_atrMedian.IsReady && _hi.IsReady)
        {
            double atr = _atr.Value, hh = _hi.Value, ll = _lo.Value;
            if (Pos == 0) s = Enter(b, atr, hh, ll, quiet: atr < _atrMedian.Value);
            else if (_trendTrade) s = ManageTrend(ctx, b, atr);
            else s = ManageFade(b, hh, ll);
        }
        _hi.Add(b.High); _lo.Add(b.Low); _exitHi.Add(b.High); _exitLo.Add(b.Low);
        return s;
    }

    private Signal Enter(Bar b, double atr, double hh, double ll, bool quiet)
    {
        if (quiet)
        {
            double range = hh - ll;
            if (range <= 0) return Hold;
            double pos = (b.Close - ll) / range;
            _trendTrade = false;
            if (pos >= 0 && pos <= edgeFrac && b.Close > b.Open) return Signal.Long(Math.Min(ll, b.Low) - FadeStopBufferAtr * atr);
            if (pos <= 1 && pos >= 1 - edgeFrac && b.Close < b.Open) return Signal.Short(Math.Max(hh, b.High) + FadeStopBufferAtr * atr);
            return Hold;
        }
        _trendTrade = true;
        if (b.Close > hh) return LongWithStop(b, atr, atrMult);
        if (b.Close < ll) return ShortWithStop(b, atr, atrMult);
        return Hold;
    }

    private Signal ManageFade(Bar b, double hh, double ll)
    {
        double mid = (hh + ll) / 2;
        if (BarsHeld >= FadeMaxHold) return Signal.Flat;
        if (Pos > 0 && b.Close >= mid) return Signal.Flat;
        if (Pos < 0 && b.Close <= mid) return Signal.Flat;
        return Hold;
    }

    private Signal ManageTrend(StrategyContext ctx, Bar b, double atr)
    {
        if (Pos > 0 && b.Close < _exitLo.Value) return Signal.Flat;
        if (Pos < 0 && b.Close > _exitHi.Value) return Signal.Flat;
        double trail = b.Close - Pos * atrMult * atr;
        if (ctx.StopPrice is double stop && (Pos > 0 ? trail <= stop : trail >= stop)) return Hold;
        return new Signal(Pos, trail);
    }
}
