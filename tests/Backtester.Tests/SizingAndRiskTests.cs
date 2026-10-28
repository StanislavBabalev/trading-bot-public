using Backtester.Core;
using Backtester.Core.Engine;
using static Backtester.Tests.TestData;

namespace Backtester.Tests;

public class SizingAndRiskTests
{
    private static BacktestResult Run(List<Bar> bars, Func<int, Signal> script, BacktestConfig cfg) =>
        new BacktestEngine(bars, []).Run(new ScriptedStrategy(script), cfg);

    [Fact]
    public void Fixed_fraction_uses_that_share_of_equity()
    {
        var r = Run(Flat(4, 100), i => i <= 1 ? Signal.Long() : Signal.Flat,
            Config(4, sizing: new SizingConfig { Mode = SizingMode.FixedFraction, Fraction = 0.5 }));
        Assert.Equal(50, r.Trades[0].Quantity, 9);
    }

    [Fact]
    public void Risk_per_trade_sizes_so_the_stop_loses_the_risk_amount()
    {
        var r = Run(Flat(4, 100), i => i <= 1 ? Signal.Long(95) : Signal.Flat,
            Config(4, sizing: new SizingConfig { Mode = SizingMode.RiskPerTrade, RiskPerTrade = 0.01 }));
        // 1% of 10,000 = 100 at risk over a 5-point stop -> 20 units
        Assert.Equal(20, r.Trades[0].Quantity, 9);
    }

    [Fact]
    public void Risk_per_trade_is_capped_by_max_leverage()
    {
        var r = Run(Flat(4, 100), i => i <= 1 ? Signal.Long(99.9) : Signal.Flat,
            Config(4, sizing: new SizingConfig { Mode = SizingMode.RiskPerTrade, RiskPerTrade = 0.01 }, risk: new RiskLimits { MaxLeverage = 1 }));
        Assert.Equal(100, r.Trades[0].Quantity, 9);
    }

    [Fact]
    public void Risk_per_trade_without_a_stop_skips_the_entry()
    {
        var r = Run(Flat(4, 100), i => i == 0 ? Signal.Long() : Signal.Flat,
            Config(4, sizing: new SizingConfig { Mode = SizingMode.RiskPerTrade }));
        Assert.Empty(r.Trades);
        Assert.Equal(1, r.EntriesSkippedNoStop);
    }

    [Fact]
    public void Vol_target_scales_notional_by_target_over_realized_vol()
    {
        var bars = Bars((100, 100, 100, 100), (110, 110, 110, 110), (100, 100, 100, 100), (100, 100, 100, 110), (110, 110, 110, 110), (110, 110, 110, 110));
        var sizing = new SizingConfig { Mode = SizingMode.VolTarget, VolTargetAnnual = 0.2, VolLookbackBars = 2 };
        var r = Run(bars, i => i == 3 ? Signal.Long() : i == 4 ? Signal.Long() : Signal.Flat, Config(6, sizing: sizing, risk: new RiskLimits { MaxLeverage = 10 }));

        // Signal at bar 3's close: closes 100,110,100,110 -> the last two log returns are ln(100/110) and ln(110/100).
        double r1 = Math.Log(100.0 / 110), r2 = Math.Log(110.0 / 100), mean = (r1 + r2) / 2;
        double sd = Math.Sqrt(((r1 - mean) * (r1 - mean) + (r2 - mean) * (r2 - mean)) / 1);
        double annual = sd * Math.Sqrt(365 * 24);
        double notional = 10_000 * 0.2 / annual;
        Assert.Equal(notional / 110, r.Trades[0].Quantity, 9);
    }

    [Fact]
    public void Vol_target_without_enough_history_skips_the_entry()
    {
        var sizing = new SizingConfig { Mode = SizingMode.VolTarget, VolLookbackBars = 50 };
        var r = Run(Flat(4, 100), i => i == 0 ? Signal.Long() : Signal.Flat, Config(4, sizing: sizing));
        Assert.Empty(r.Trades);
        Assert.Equal(1, r.EntriesSkippedNoVolEstimate);
    }

    [Fact]
    public void Max_drawdown_stop_goes_flat_and_halts_for_good()
    {
        var bars = Bars((100, 100, 100, 100), (100, 100, 100, 100), (100, 100, 60, 60), (60, 60, 60, 60), (60, 60, 60, 60), (60, 60, 60, 60));
        var r = Run(bars, i => i == 3 ? Signal.Flat : Signal.Long(),
            Config(6, risk: new RiskLimits { MaxDrawdownStop = 0.30 }));
        Assert.True(r.Halted);
        var t = Assert.Single(r.Trades);
        Assert.Equal(ExitReason.RiskHalt, t.ExitReason);
        Assert.Equal(bars[3].OpenTimeMs, t.ExitTimeMs);
    }

    [Fact]
    public void Daily_loss_stop_blocks_entries_until_the_next_utc_day()
    {
        // 30 hourly bars from midnight UTC. Long from bar 2's open at 100; the 10% drop at bar 3 trips a 5% daily
        // stop, so the position is closed at bar 4's open and not re-entered until midnight, although the
        // strategy keeps asking for a long.
        var bars = Enumerable.Range(0, 30).Select(i =>
        {
            double p = i < 3 ? 100 : 90;
            return new Bar(T0 + i * Hour, p, p, p, p, 1);
        }).ToList();
        var r = Run(bars, i => i == 0 ? Signal.Flat : Signal.Long(), Config(30, risk: new RiskLimits { DailyLossStop = 0.05 }));

        Assert.Equal(1, r.DailyLossStopsHit);
        Assert.Equal(ExitReason.RiskHalt, r.Trades[0].ExitReason);
        Assert.Equal(bars[4].OpenTimeMs, r.Trades[0].ExitTimeMs);
        Assert.Equal(T0 + 24 * Hour, r.Trades[1].EntryTimeMs);
        Assert.False(r.Halted);
    }
}
