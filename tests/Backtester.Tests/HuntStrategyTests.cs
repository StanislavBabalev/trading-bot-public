using Backtester.Core;
using Backtester.Core.Engine;
using Backtester.Core.Indicators;
using Backtester.Core.Strategies;
using static Backtester.Tests.TestData;

namespace Backtester.Tests;

public class HuntStrategyTests
{
    private static List<Bar> FromCloses(IEnumerable<double> closes) =>
        closes.Select((p, i) => new Bar(T0 + i * Hour, p, p + 0.5, p - 0.5, p, 1)).ToList();

    private static BacktestResult Run(IStrategy s, List<Bar> bars) =>
        new BacktestEngine(bars, []).Run(s, Config(bars.Count, sizing: new SizingConfig { Mode = SizingMode.RiskPerTrade, RiskPerTrade = 0.01 }));

    [Fact]
    public void Rolling_extreme_matches_a_naive_window()
    {
        var rng = new Random(7);
        var xs = Enumerable.Range(0, 300).Select(_ => rng.NextDouble() * 100).ToList();
        var max = new RollingExtreme(17, max: true);
        var min = new RollingExtreme(17, max: false);
        for (int i = 0; i < xs.Count; i++)
        {
            max.Add(xs[i]);
            min.Add(xs[i]);
            var window = xs.Skip(Math.Max(0, i - 16)).Take(Math.Min(17, i + 1)).ToList();
            Assert.Equal(window.Max(), max.Value);
            Assert.Equal(window.Min(), min.Value);
        }
    }

    [Fact]
    public void Rsi_is_100_on_only_gains_and_0_on_only_losses()
    {
        var up = new Rsi(3);
        foreach (var c in new double[] { 1, 2, 3, 4, 5 }) up.Update(c);
        Assert.Equal(100, up.Value);
        var down = new Rsi(3);
        foreach (var c in new double[] { 5, 4, 3, 2, 1 }) down.Update(c);
        Assert.Equal(0, down.Value);
    }

    [Fact]
    public void Every_registered_strategy_builds_from_its_defaults_and_trades_with_stops()
    {
        // A choppy random walk with wicks, volume and shock bars gives every hypothesis something to do. 300 days and
        // 8-hourly funding cover the daily-bar, funding-rank and 200-day trend-filter warm-ups.
        var bars = RandomOhlcv(24 * 300, 11);
        var rng = new Random(5);
        var funding = Enumerable.Range(0, 900).Select(k => new FundingRate(T0 + k * 8 * Hour, 0.0001 + 0.0002 * (rng.NextDouble() - 0.5))).ToList();
        // round-number-bounce trades absolute USD levels (multiples of 1,000), so it needs BTC-like prices, not ~100.
        var btcLike = bars.Select(b => b with { Open = b.Open * 300, High = b.High * 300, Low = b.Low * 300, Close = b.Close * 300 }).ToList();
        foreach (var def in StrategyRegistry.All)
        {
            var s = def.Create(def.Defaults);
            Assert.NotNull(s);
            var input = def.Name == "round-number-bounce" ? btcLike : bars;
            var r = new BacktestEngine(input, funding).Run(s!, Config(input.Count, sizing: new SizingConfig { Mode = SizingMode.RiskPerTrade, RiskPerTrade = 0.01 }));
            Assert.True(r.Trades.Count > 0, $"{def.Name} made no trades");
            Assert.Equal(0, r.EntriesSkippedNoStop);
            Assert.True(def.DefaultSpace.Count <= 4, $"{def.Name} has more than 4 tunable parameters");
        }
    }

    [Fact]
    public void Donchian_enters_on_a_breakout_of_the_previous_bars_only()
    {
        // Flat at 100 for 30 bars, then a jump: the breakout bar itself sets the new high.
        var closes = Enumerable.Repeat(100.0, 30).Concat(Enumerable.Repeat(110.0, 10)).ToList();
        var r = Run(new DonchianBreakout(10, 5, 3), FromCloses(closes));
        var t = Assert.Single(r.Trades);
        Assert.Equal(1, t.Direction);
        Assert.Equal(T0 + 31 * Hour, t.EntryTimeMs);   // signal on bar 30's close, fill at bar 31's open
    }

    [Fact]
    public void Bollinger_reversion_exits_at_the_middle_band()
    {
        var closes = Enumerable.Repeat(100.0, 40).Concat([90.0, 92, 96, 100, 101, 101, 101]).ToList();
        var bars = closes.Select((c, i) => new Bar(T0 + i * Hour, c, c + 0.2, c - 0.2, c, 1)).ToList();
        var r = Run(new BollingerReversion(20, 2, 5), bars);
        var t = Assert.Single(r.Trades);
        Assert.Equal(1, t.Direction);
        Assert.Equal(ExitReason.Signal, t.ExitReason);
        Assert.True(t.ExitPrice > t.EntryPrice);
    }

    [Fact]
    public void Entry_exit_strategy_resyncs_after_a_stop_out()
    {
        // Momentum long, then a crash through the stop; the strategy must notice it is flat again.
        var closes = Enumerable.Range(0, 40).Select(i => 100.0 + i)
            .Concat(Enumerable.Repeat(60.0, 5))
            .Concat(Enumerable.Range(0, 40).Select(i => 60.0 - i * 0.5)).ToList();
        var r = Run(new TimeSeriesMomentum(10, 0.01, 2), FromCloses(closes));
        Assert.Equal(ExitReason.Stop, r.Trades[0].ExitReason);
        Assert.Contains(r.Trades.Skip(1), t => t.Direction == -1);
    }
}
