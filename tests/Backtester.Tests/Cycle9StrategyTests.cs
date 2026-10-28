using Backtester.Core;
using Backtester.Core.Engine;
using Backtester.Core.Indicators;
using Backtester.Core.Strategies;
using static Backtester.Tests.TestData;

namespace Backtester.Tests;

public class Cycle9StrategyTests
{
    [Fact]
    public void Period_bars_aggregate_aligned_complete_periods_only()
    {
        // Starts at 02:00, so the 00:00-04:00 block is partial; 04:00-08:00 completes on the 07:00 bar.
        var bars = Enumerable.Range(0, 10).Select(i => new Bar(T0 + (2 + i) * Hour, 100 + i, 102 + i, 99 + i, 101 + i, 1)).ToList();
        var p = new PeriodBars(4);
        var closedAt = new List<int>();
        for (int i = 0; i < bars.Count; i++)
        {
            p.Update(bars[i]);
            if (p.Closed) closedAt.Add(i);
        }
        Assert.Equal([5, 9], closedAt);
        Assert.Equal(new Bar(T0 + 8 * Hour, bars[6].Open, bars[9].High, bars[6].Low, bars[9].Close, 4), p.Last);
        Assert.Throws<ArgumentException>(() => new PeriodBars(5));
    }

    [Fact]
    public void Wave_trend_matches_the_lazybear_formula()
    {
        var rng = new Random(8);
        var ap = Enumerable.Range(0, 200).Select(i => 100 + 10 * Math.Sin(i / 7.0) + rng.NextDouble()).ToList();
        var wt = new WaveTrend(9, 12, 3);
        // Naive: EMA seeded with the first value (ta.ema), SMA(3) of WT1.
        static List<double> Ema(IReadOnlyList<double> x, int n)
        {
            double a = 2.0 / (n + 1);
            var o = new List<double> { x[0] };
            for (int i = 1; i < x.Count; i++) o.Add(a * x[i] + (1 - a) * o[^1]);
            return o;
        }
        var esa = Ema(ap, 9);
        var d = Ema(ap.Select((v, i) => Math.Abs(v - esa[i])).ToList(), 9);
        var ci = ap.Select((v, i) => d[i] > 0 ? (v - esa[i]) / (0.015 * d[i]) : 0).ToList();   // 0/0 on the first value counts as 0
        var wt1 = Ema(ci, 12);
        for (int i = 0; i < ap.Count; i++)
        {
            wt.Update(ap[i]);
            Assert.Equal(wt1[i], wt.Wt1, 9);
            if (i >= 2) Assert.Equal((wt1[i] + wt1[i - 1] + wt1[i - 2]) / 3, wt.Wt2, 9);
        }
        Assert.True(wt.IsReady);
    }

    [Fact]
    public void Supertrend_follows_a_rally_and_flips_on_a_crash()
    {
        var st = new SuperTrend(3, 2);
        var dirs = new List<int>();
        for (int i = 0; i < 40; i++)
        {
            double c = i < 30 ? 100 + i : 130 - 6 * (i - 29);
            st.Update(new Bar(T0 + i * Hour, c - 0.5, c + 1, c - 1, c, 1));
            dirs.Add(st.Direction);
        }
        Assert.Equal(1, dirs[29]);
        Assert.Equal(-1, dirs[^1]);
        Assert.True(dirs.Skip(3).Take(27).All(x => x == 1), "the trend must not flip during a steady rally");
    }

    [Fact]
    public void Linreg_returns_the_last_point_of_a_line_and_the_fit_otherwise()
    {
        Assert.Equal(19.5, SqueezeMomentum.LinRegLast(Enumerable.Range(0, 20).Select(i => 0.5 + i)), 9);
        Assert.Equal(2.6, SqueezeMomentum.LinRegLast([1, 3, 1, 3]), 9);   // slope 0.4, intercept 1.4 → 1.4 + 3 × 0.4
    }

    [Fact]
    public void Cipher_b_dots_buys_an_oversold_cross_up_and_exits_on_the_next_cross_down()
    {
        // 2h periods: a steady decline (WaveTrend deeply oversold), a sharp turn up, then a roll-over.
        var bars = new List<Bar>();
        double price = 100;
        for (int i = 0; i < 2 * 120; i++)
        {
            int period = i / 2;
            double step = period < 80 ? 0.15 : period < 95 ? -0.6 : period < 105 ? 0.8 : -0.8;
            double next = price + step;
            bars.Add(new Bar(T0 + i * Hour, price, Math.Max(price, next) + 0.05, Math.Min(price, next) - 0.05, next, 1));
            price = next;
        }
        var r = new BacktestEngine(bars, []).Run(new CipherBDots(2, 53, 2.5),
            Config(bars.Count, 0, 0, new SizingConfig { Mode = SizingMode.RiskPerTrade, RiskPerTrade = 0.01 }));
        var longs = r.Trades.Where(t => t.Direction == 1).ToList();
        Assert.NotEmpty(longs);
        var first = longs[0];
        Assert.InRange(first.EntryTimeMs, T0 + 2 * 95 * Hour, T0 + 2 * 105 * Hour);   // after the low, during the rebound
        Assert.True(first.ExitTimeMs > first.EntryTimeMs);
        Assert.Equal(0, (first.EntryTimeMs - T0) / Hour % 2);                        // fills at a period open
    }
}
