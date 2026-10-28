using Backtester.Core;
using Backtester.Core.Data;
using Backtester.Core.Engine;
using Backtester.Core.Indicators;
using Backtester.Core.Strategies;
using static Backtester.Tests.TestData;

namespace Backtester.Tests;

public class Cycle5StrategyTests
{
    private static readonly string[] Sprint =
    [
        "donchian-ensemble-trend", "funding-crowding-fade", "noise-area-momentum", "nr-compression-breakout", "tsmom-vote",
        "max-min-daily", "mtf-trend-pullback", "efficiency-regime-switch", "volume-conditioned-reversal", "crowding-filtered-breakout",
        "funding-tilt", "ema-cross-control", "funding-carry-tilt", "funding-fade-wide",
        "cipher-b-dots", "cipher-b-sommi-flag", "cipher-b-divergence", "cipher-a-ribbon", "cipher-b-confluence", "supertrend", "squeeze-momentum", "ichimoku-cloud",
        "dca-grid-long", "dca-grid-short", "dca-rsi-long", "dca-trend-long",
    ];

    private static BacktestConfig RiskCfg(List<Bar> bars, long? fromMs = null) =>
        Config(bars.Count, 0.0004, 0.0002, new SizingConfig { Mode = SizingMode.RiskPerTrade, RiskPerTrade = 0.01 }, fromMs: fromMs);

    private static Bar B(int i, double o, double h, double l, double c, double v = 1) => new(T0 + i * Hour, o, h, l, c, v);

    [Fact]
    public void Sprint_grids_fit_the_optimizer_budget_and_defaults_sit_on_the_grid()
    {
        foreach (var name in Sprint)
        {
            var def = StrategyRegistry.Get(name);
            Assert.NotNull(def.Create(def.Defaults));
            long size = def.DefaultSpace.Aggregate(1L, (acc, p) => acc * p.Values().Count);
            Assert.True(size <= 200 - 2 * def.DefaultSpace.Count, $"{name}: grid of {size} points would be sampled, not searched");
            foreach (var p in def.DefaultSpace)
                Assert.Contains(p.Values(), v => Math.Abs(v - def.Defaults[p.Name]) < 1e-9);
        }
    }

    // The splice look-ahead check now runs for every registered strategy: ValidationTests.Every_registered_strategy_passes_the_look_ahead_audit.

    [Fact]
    public void Daily_bars_complete_only_full_utc_days_on_the_23_00_bar()
    {
        // Starts at 20:00: day 0 is partial (4 bars) and never completes; day 1 completes on its 23:00 bar.
        var bars = Enumerable.Range(0, 4 + 24 + 2).Select(i => new Bar(T0 + (20 + i) * Hour, 100 + i, 101 + i, 99 + i, 100.5 + i, 2)).ToList();
        var d = new DailyBars();
        var closedAt = new List<int>();
        for (int i = 0; i < bars.Count; i++)
        {
            d.Update(bars[i]);
            if (d.DayClosed) closedAt.Add(i);
        }
        Assert.Equal([27], closedAt);
        var day = Assert.Single(d.Days);
        Assert.Equal(bars[4].OpenTimeMs, day.OpenTimeMs);
        Assert.Equal((bars[4].Open, bars[27].High, bars[4].Low, bars[27].Close, 48.0), (day.Open, day.High, day.Low, day.Close, day.Volume));
    }

    [Fact]
    public void Efficiency_ratio_is_one_on_a_line_and_matches_a_naive_window()
    {
        var line = new EfficiencyRatio(5);
        for (int i = 0; i < 10; i++) line.Add(100 + i);
        Assert.Equal(1.0, line.Value, 12);

        var rng = new Random(4);
        var xs = Enumerable.Range(0, 300).Select(_ => 100 + rng.Next(-20, 20) * 0.5).ToList();
        var er = new EfficiencyRatio(12);
        for (int i = 0; i < xs.Count; i++)
        {
            er.Add(xs[i]);
            Assert.Equal(i >= 12, er.IsReady);
            if (i < 12) continue;
            double path = Enumerable.Range(i - 11, 12).Sum(j => Math.Abs(xs[j] - xs[j - 1]));
            double naive = path > 0 ? Math.Abs(xs[i] - xs[i - 12]) / path : 0;
            Assert.Equal(naive, er.Value, 9);
        }
    }

    [Fact]
    public void Funding_rank_uses_only_rates_settled_by_each_bar_open()
    {
        var bars = Enumerable.Range(0, 24 * 6).Select(i => new Bar(T0 + i * Hour, 100, 101, 99, 100, 1)).ToList();
        // One rate every 8 hours, rising: each new 2-rate mean is the highest so far (rank 1) once 3 earlier means exist.
        var funding = Enumerable.Range(0, 18).Select(k => new FundingRate(T0 + k * 8 * Hour, 0.0001 * k)).ToList();
        var f = new BacktestEngine(bars, funding).Features;
        var count = f.FundingCount();
        var last = f.Funding();
        var rank = f.FundingRank(2, 3);
        Assert.Equal(1, count[0]);                        // the T0 rate settles at the first bar's open
        Assert.Equal(1, count[7]);
        Assert.Equal(2, count[8]);
        Assert.Equal(0.0001, last[8], 12);
        Assert.True(double.IsNaN(rank[8 * 4 - 1]));       // 4 rates: means exist for k = 2..4, need k - 3 >= 2
        Assert.Equal(1.0, rank[8 * 4]);                   // 5 rates settled
        // Falling rates later must not change earlier ranks.
        var changed = funding.Take(10).Concat(Enumerable.Range(10, 8).Select(k => new FundingRate(T0 + k * 8 * Hour, -0.01))).ToList();
        var rank2 = new BacktestEngine(bars, changed).Features.FundingRank(2, 3);
        Assert.Equal(rank.Take(8 * 10), rank2.Take(8 * 10));
        Assert.Equal(0.0, rank2[8 * 11]);
    }

    [Fact]
    public void Nr_breakout_buys_the_first_close_above_a_narrowest_day_and_stops_at_its_low()
    {
        // Days 0-3: ranges 10, 8, 6, 2 (day 3 is NR4). Day 4: closes above day 3's high at 10:00.
        var bars = new List<Bar>();
        double[] half = [5, 4, 3, 1];
        for (int d = 0; d < 4; d++)
            for (int h = 0; h < 24; h++)
            {
                int i = d * 24 + h;
                bars.Add(h == 12 ? B(i, 100, 100 + half[d], 100 - half[d], 100) : B(i, 100, 100.5, 99.5, 100));
            }
        for (int h = 0; h < 24; h++) bars.Add(h == 10 ? B(96 + h, 100, 101.6, 99.9, 101.5) : B(96 + h, h > 10 ? 101.5 : 100, 101.6, 99.6, h > 10 ? 101.5 : 100));
        var r = new BacktestEngine(bars, []).Run(new NrCompressionBreakout(4, 6, 1.0), RiskCfg(bars));
        var t = Assert.Single(r.Trades);
        Assert.Equal(1, t.Direction);
        Assert.Equal(T0 + (96 + 11) * Hour, t.EntryTimeMs);
        Assert.Equal(T0 + (96 + 17) * Hour, t.ExitTimeMs);   // 6 bars held
        Assert.Equal(ExitReason.Signal, t.ExitReason);
    }

    [Fact]
    public void Noise_area_momentum_enters_once_a_day_and_is_flat_by_the_next_day()
    {
        var (bars, funding) = SyntheticData.Generate(new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc), 24 * 120, 9);
        var r = new BacktestEngine(bars, funding).Run(new NoiseAreaMomentum(14, 1.0, 2), RiskCfg(bars, bars[24 * 20].OpenTimeMs));
        Assert.NotEmpty(r.Trades);
        Assert.All(r.Trades, t =>
        {
            Assert.True(TimeUtil.UtcHour(t.EntryTimeMs) >= 3, "entry fills no earlier than the 03:00 open");
            Assert.True(t.ExitTimeMs - TimeUtil.DayIndex(t.EntryTimeMs) * 86_400_000L <= 86_400_000L, "held past the day end");
        });
        Assert.Equal(r.Trades.Count, r.Trades.Select(t => TimeUtil.DayIndex(t.EntryTimeMs)).Distinct().Count());
    }

    [Fact]
    public void Tsmom_vote_follows_the_majority_and_flips_on_a_reversal()
    {
        // 40 days up 1%/day, then 40 days down 1%/day (hourly bars interpolate the daily path).
        var bars = new List<Bar>();
        double p = 100;
        for (int d = 0; d < 80; d++)
            for (int h = 0; h < 24; h++)
            {
                double next = p * Math.Pow(d < 40 ? 1.01 : 0.99, 1.0 / 24);
                bars.Add(B(d * 24 + h, p, Math.Max(p, next) * 1.0005, Math.Min(p, next) * 0.9995, next));
                p = next;
            }
        var r = new BacktestEngine(bars, []).Run(new TsmomVote(3, false, 3.0), RiskCfg(bars));
        Assert.Equal(1, r.Trades[0].Direction);
        Assert.Contains(r.Trades, t => t.Direction == -1);
        Assert.True(r.Trades.First(t => t.Direction == -1).EntryTimeMs > T0 + 40 * 24 * Hour);
    }
}
