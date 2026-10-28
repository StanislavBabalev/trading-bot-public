using Backtester.Core;
using Backtester.Core.Engine;
using Backtester.Core.Indicators;
using Backtester.Core.Strategies;
using static Backtester.Tests.TestData;

namespace Backtester.Tests;

public class Cycle4StrategyTests
{
    private static readonly string[] Cycle4 =
        ["failed-breakout-fade", "volume-climax-reversal", "session-momentum", "impulse-bar-continuation", "vol-regime-switch"];

    private static BacktestResult Run(IStrategy s, List<Bar> bars) =>
        new BacktestEngine(bars, []).Run(s, Config(bars.Count, sizing: new SizingConfig { Mode = SizingMode.RiskPerTrade, RiskPerTrade = 0.01 }));

    private static Bar B(int i, double o, double h, double l, double c, double v = 1) => new(T0 + i * Hour, o, h, l, c, v);

    /// <summary>A doji bar at <paramref name="c"/> with the given half-range.</summary>
    private static Bar Doji(int i, double c, double half) => B(i, c, c + half, c - half, c);

    [Fact]
    public void Cycle_4_grids_fit_the_optimizer_budget_and_defaults_sit_on_the_grid()
    {
        foreach (var name in Cycle4)
        {
            var def = StrategyRegistry.Get(name);
            Assert.NotNull(def.Create(def.Defaults));
            long size = def.DefaultSpace.Aggregate(1L, (acc, p) => acc * p.Values().Count);
            Assert.True(size <= 200 - 2 * def.DefaultSpace.Count, $"{name}: grid of {size} points would be sampled, not searched");
            foreach (var p in def.DefaultSpace)
                Assert.Contains(p.Values(), v => Math.Abs(v - def.Defaults[p.Name]) < 1e-9);
        }
    }

    [Fact]
    public void Rolling_median_matches_a_naive_window()
    {
        var rng = new Random(3);
        var xs = Enumerable.Range(0, 200).Select(_ => (double)rng.Next(20)).ToList();   // duplicates on purpose
        foreach (int len in new[] { 7, 8 })
        {
            var m = new RollingMedian(len);
            for (int i = 0; i < xs.Count; i++)
            {
                m.Add(xs[i]);
                var w = xs.Skip(Math.Max(0, i + 1 - len)).Take(Math.Min(len, i + 1)).OrderBy(x => x).ToList();
                double naive = w.Count % 2 == 1 ? w[w.Count / 2] : (w[w.Count / 2 - 1] + w[w.Count / 2]) / 2;
                Assert.Equal(naive, m.Value);
                Assert.Equal(i + 1 >= len, m.IsReady);
            }
        }
    }

    [Fact]
    public void Utc_day_levels_skip_a_partial_first_day()
    {
        // Starts at 20:00, so day 0 is partial; day 1 is complete; day 2 reads day 1's levels.
        var bars = Enumerable.Range(0, 30).Select(i =>
        {
            double o = 100 + i;
            return new Bar(T0 + (20 + i) * Hour, o, o + 1 + i % 3, o - 1 - i % 5, o + 0.5, 1);
        }).ToList();
        var day = new UtcDayLevels();
        for (int i = 0; i < 28; i++)
        {
            day.Update(bars[i]);
            Assert.False(day.HasPrev);
        }
        day.Update(bars[28]);
        Assert.True(day.HasPrev);
        Assert.Equal(bars.Skip(4).Take(24).Max(b => b.High), day.PrevHigh);
        Assert.Equal(bars.Skip(4).Take(24).Min(b => b.Low), day.PrevLow);
        Assert.Equal(bars[28].Open, day.DayOpen);
        Assert.Equal(0, TimeUtil.UtcHour(bars[28].OpenTimeMs));
        Assert.Equal(23, TimeUtil.UtcHour(bars[27].OpenTimeMs));
    }

    /// <summary>
    /// Day 0: range 100–110 (PDH 110, PDL 100, midpoint 105). Day 1: two closes below 100 (low 98), then a close back
    /// at 101 on bar 30, then a rally to 105.5 on bar 32.
    /// </summary>
    private static List<Bar> FailedBreakDays()
    {
        var bars = new List<Bar>();
        for (int i = 0; i < 24; i++)
            bars.Add(i == 5 ? B(i, 105, 110, 104.5, 105) : i == 10 ? B(i, 105, 105.5, 100, 105) : Doji(i, 105, 0.5));
        for (int i = 24; i < 28; i++) bars.Add(Doji(i, 104, 0.5));
        bars.Add(B(28, 104, 104, 98.5, 99));
        bars.Add(B(29, 99, 99.5, 98, 99.2));
        bars.Add(B(30, 99.2, 101.2, 99, 101));
        bars.Add(B(31, 101, 103.2, 100.8, 103));
        bars.Add(B(32, 103, 105.7, 102.8, 105.5));
        for (int i = 33; i < 36; i++) bars.Add(Doji(i, 105.5, 0.2));
        return bars;
    }

    [Fact]
    public void Failed_breakout_fade_buys_a_reclaimed_prior_day_low_and_exits_at_the_midpoint()
    {
        var r = Run(new FailedBreakoutFade(6, 0.25, 0.5, 24), FailedBreakDays());
        var t = Assert.Single(r.Trades);
        Assert.Equal(1, t.Direction);
        Assert.Equal(T0 + 31 * Hour, t.EntryTimeMs);   // signal on bar 30's close
        Assert.Equal(T0 + 33 * Hour, t.ExitTimeMs);    // bar 32 closed at/above the midpoint
        Assert.Equal(ExitReason.Signal, t.ExitReason);
        Assert.Equal(105.5, t.ExitPrice);
    }

    [Fact]
    public void Failed_breakout_fade_skips_a_break_that_lasted_too_long()
    {
        var r = Run(new FailedBreakoutFade(1, 0.25, 0.5, 24), FailedBreakDays());
        Assert.Empty(r.Trades);
    }

    private static List<Bar> ClimaxBars(double climaxVolume)
    {
        var bars = Enumerable.Range(0, 60).Select(i => Doji(i, 100, 0.5)).ToList();
        bars.Add(B(60, 100, 100.5, 95, 99.5, climaxVolume));   // long lower wick at a 24-bar low
        bars.AddRange(Enumerable.Range(61, 12).Select(i => Doji(i, 99.5, 0.5)));
        return bars;
    }

    [Fact]
    public void Volume_climax_reversal_fades_a_high_volume_wick_and_exits_on_time()
    {
        var r = Run(new VolumeClimaxReversal(2.0, 1.5, 6), ClimaxBars(5));
        var t = Assert.Single(r.Trades);
        Assert.Equal(1, t.Direction);
        Assert.Equal(T0 + 61 * Hour, t.EntryTimeMs);
        Assert.Equal(T0 + 67 * Hour, t.ExitTimeMs);    // 6 bars held (61–66), out at bar 67's open
        Assert.Equal(ExitReason.Signal, t.ExitReason);
    }

    [Fact]
    public void Volume_climax_reversal_needs_the_volume_spike()
    {
        Assert.Empty(Run(new VolumeClimaxReversal(2.0, 1.5, 6), ClimaxBars(1)).Trades);
    }

    [Fact]
    public void Session_momentum_trades_the_morning_move_from_the_12_00_open()
    {
        // Day 0 flat at 100; day 1 rises 0.5/bar until 11:00 then flat at 106; day 2 falls back to 100 by 11:00.
        var bars = new List<Bar>();
        for (int i = 0; i < 24; i++) bars.Add(Doji(i, 100, 0.25));
        for (int k = 0; k < 12; k++) bars.Add(B(24 + k, 100 + 0.5 * k, 100.6 + 0.5 * k, 99.9 + 0.5 * k, 100.5 + 0.5 * k));
        for (int i = 36; i < 48; i++) bars.Add(Doji(i, 106, 0.25));
        for (int k = 0; k < 12; k++) bars.Add(B(48 + k, 106 - 0.5 * k, 106.1 - 0.5 * k, 105.4 - 0.5 * k, 105.5 - 0.5 * k));
        for (int i = 60; i < 72; i++) bars.Add(Doji(i, 100, 0.25));

        var r = Run(new SessionMomentum(1.0, 4, 3.0), bars);
        Assert.Equal(2, r.Trades.Count);
        Assert.Equal((1, T0 + 36 * Hour, T0 + 40 * Hour), (r.Trades[0].Direction, r.Trades[0].EntryTimeMs, r.Trades[0].ExitTimeMs));
        Assert.Equal((-1, T0 + 60 * Hour, T0 + 64 * Hour), (r.Trades[1].Direction, r.Trades[1].EntryTimeMs, r.Trades[1].ExitTimeMs));
    }

    [Fact]
    public void Impulse_bar_continuation_follows_a_full_body_bar_but_not_a_wick()
    {
        var bars = Enumerable.Range(0, 30).Select(i => Doji(i, 100, 0.25)).ToList();
        bars.Add(B(30, 100, 100.2, 97, 99.9));                 // big range, but a wick: no trade
        bars.AddRange(Enumerable.Range(31, 14).Select(i => Doji(i, 99.9, 0.25)));
        bars.Add(B(45, 99.9, 103.2, 99.8, 103));               // full-body impulse up
        bars.AddRange(Enumerable.Range(46, 10).Select(i => Doji(i, 103, 0.25)));

        var r = Run(new ImpulseBarContinuation(2.5, 0.6, 6, 2.0), bars);
        var t = Assert.Single(r.Trades);
        Assert.Equal(1, t.Direction);
        Assert.Equal(T0 + 46 * Hour, t.EntryTimeMs);
        Assert.Equal(T0 + 52 * Hour, t.ExitTimeMs);
    }

    [Fact]
    public void Vol_regime_switch_fades_the_range_edge_when_quiet()
    {
        // 80 volatile bars (ATR ~4), 30 quiet bars (ATR falls below its median), then a green close at the range bottom.
        var bars = Enumerable.Range(0, 80).Select(i => Doji(i, i % 2 == 0 ? 102 : 98, 0.2)).ToList();
        bars.AddRange(Enumerable.Range(80, 30).Select(i => Doji(i, i % 2 == 0 ? 100.5 : 99.5, 0.2)));
        bars.Add(B(110, 99.35, 99.5, 99.3, 99.45));
        bars.Add(B(111, 99.45, 100.2, 99.4, 100.1));            // back to the channel midpoint (100.0)
        bars.AddRange(Enumerable.Range(112, 4).Select(i => Doji(i, 100.1, 0.2)));

        var r = Run(new VolRegimeSwitch(50, 10, 0.2, 2.0), bars);
        var t = Assert.Single(r.Trades);
        Assert.Equal(1, t.Direction);
        Assert.Equal(T0 + 111 * Hour, t.EntryTimeMs);
        Assert.Equal(T0 + 112 * Hour, t.ExitTimeMs);
        Assert.Equal(ExitReason.Signal, t.ExitReason);
        Assert.True(t.ExitPrice > t.EntryPrice);
    }

    [Fact]
    public void Vol_regime_switch_breaks_out_and_trails_its_stop_when_volatility_expands()
    {
        // Quiet range, then a wide breakout bar, ten up bars (the stop trails), then a crash through the trailed stop.
        var bars = Enumerable.Range(0, 100).Select(i => Doji(i, i % 2 == 0 ? 100.5 : 99.5, 0.2)).ToList();
        bars.Add(B(100, 99.5, 104.2, 99.4, 104));
        for (int k = 1; k <= 10; k++) bars.Add(B(100 + k, 103 + k, 104.2 + k, 102.8 + k, 104 + k));
        bars.Add(B(111, 114, 114.2, 105, 106));

        var r = Run(new VolRegimeSwitch(50, 10, 0.2, 2.0), bars);
        var t = Assert.Single(r.Trades);
        Assert.Equal(1, t.Direction);
        Assert.Equal(T0 + 101 * Hour, t.EntryTimeMs);
        Assert.Equal(ExitReason.Stop, t.ExitReason);
        Assert.True(t.ExitPrice > 110, $"stop should have trailed above 110, exited at {t.ExitPrice}");
    }
}
