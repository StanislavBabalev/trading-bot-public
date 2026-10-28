using Backtester.Core;
using Backtester.Core.Engine;
using static Backtester.Tests.TestData;

namespace Backtester.Tests;

public class EngineTests
{
    private static BacktestResult Run(List<Bar> bars, Func<int, Signal> script, BacktestConfig cfg, List<FundingRate>? funding = null) =>
        new BacktestEngine(bars, funding ?? []).Run(new ScriptedStrategy(script), cfg);

    [Fact]
    public void Signal_fills_at_next_bar_open_never_same_bar()
    {
        var bars = Bars((100, 101, 99, 100), (110, 112, 108, 111), (120, 121, 119, 120), (130, 131, 129, 130));
        var r = Run(bars, i => i <= 1 ? Signal.Long() : Signal.Flat, Config(4));

        var t = Assert.Single(r.Trades);
        Assert.Equal(bars[1].OpenTimeMs, t.EntryTimeMs);
        Assert.Equal(110, t.EntryPrice);
        Assert.Equal(bars[3].OpenTimeMs, t.ExitTimeMs);
        Assert.Equal(130, t.ExitPrice);
        Assert.Equal(ExitReason.Signal, t.ExitReason);
        Assert.Equal(10_000 * 130.0 / 110.0, r.FinalEquity, 6);
    }

    [Fact]
    public void Commission_and_slippage_are_charged_on_both_sides()
    {
        var bars = Bars((100, 101, 99, 100), (110, 112, 108, 111), (120, 121, 119, 120), (130, 131, 129, 130));
        var r = Run(bars, i => i <= 1 ? Signal.Long() : Signal.Flat, Config(4, commission: 0.0004, slippage: 0.0002));

        double entry = 110 * 1.0002, qty = 10_000 / entry, exit = 130 * (1 - 0.0002);
        double expected = 10_000 - 10_000 * 0.0004 + qty * (exit - entry) - qty * exit * 0.0004;
        var t = Assert.Single(r.Trades);
        Assert.Equal(entry, t.EntryPrice, 9);
        Assert.Equal(exit, t.ExitPrice, 9);
        Assert.Equal(expected, r.FinalEquity, 6);
        Assert.Equal(10_000 * 0.0004 + qty * exit * 0.0004, r.FeesPaid, 6);
        Assert.Equal(qty * 110 * 0.0002 + qty * 130 * 0.0002, r.SlippageCost, 6);
    }

    [Fact]
    public void Short_profits_when_price_falls()
    {
        var bars = Bars((100, 100, 100, 100), (100, 100, 100, 100), (80, 80, 80, 80), (80, 80, 80, 80));
        var r = Run(bars, i => i <= 1 ? Signal.Short() : Signal.Flat, Config(4));
        var t = Assert.Single(r.Trades);
        Assert.Equal(-1, t.Direction);
        Assert.Equal(100 * 20, t.GrossPnl, 6);
        Assert.Equal(12_000, r.FinalEquity, 6);
    }

    [Theory]
    [InlineData(1, -10)]   // long pays a positive rate
    [InlineData(-1, 10)]   // short receives it
    public void Funding_is_paid_by_longs_and_received_by_shorts(int dir, double expected)
    {
        var bars = Flat(4, 100);
        var funding = new List<FundingRate> { new(T0 + 2 * Hour, 0.001) };
        var r = Run(bars, i => i <= 1 ? new Signal(dir) : Signal.Flat, Config(4), funding);
        Assert.Equal(expected, r.FundingReceived, 9);
        Assert.Equal(expected, Assert.Single(r.Trades).Funding, 9);
        Assert.Equal(10_000 + expected, r.FinalEquity, 9);
    }

    [Fact]
    public void Funding_at_the_entry_bar_open_is_not_charged()
    {
        // The position fills at bar 1's open; funding stamped at that same instant settles on the
        // position held *into* the open, which was flat.
        var bars = Flat(4, 100);
        var funding = new List<FundingRate> { new(T0 + Hour, 0.001) };
        var r = Run(bars, i => i <= 1 ? Signal.Long() : Signal.Flat, Config(4), funding);
        Assert.Equal(0, r.FundingReceived);
    }

    [Fact]
    public void Funding_can_be_disabled()
    {
        var bars = Flat(4, 100);
        var funding = new List<FundingRate> { new(T0 + 2 * Hour, 0.001) };
        var r = Run(bars, i => i <= 1 ? Signal.Long() : Signal.Flat, Config(4, funding: false), funding);
        Assert.Equal(0, r.FundingReceived);
    }

    [Fact]
    public void Long_stop_fills_at_stop_price_when_touched()
    {
        var bars = Bars((100, 100, 100, 100), (100, 101, 99, 100), (99, 100, 94, 96), (96, 96, 96, 96));
        var r = Run(bars, _ => Signal.Long(95), Config(4, slippage: 0.001));
        var t = r.Trades[0];
        Assert.Equal(ExitReason.Stop, t.ExitReason);
        Assert.Equal(95 * (1 - 0.001), t.ExitPrice, 9);
        Assert.Equal(bars[2].OpenTimeMs, t.ExitTimeMs);
    }

    [Fact]
    public void Long_stop_fills_at_open_when_price_gaps_through_it()
    {
        var bars = Bars((100, 100, 100, 100), (100, 101, 99, 100), (90, 92, 88, 91), (91, 91, 91, 91));
        var r = Run(bars, _ => Signal.Long(95), Config(4));
        Assert.Equal(90, r.Trades[0].ExitPrice, 9);
    }

    [Fact]
    public void Short_stop_fills_when_high_reaches_it()
    {
        var bars = Bars((100, 100, 100, 100), (100, 101, 99, 100), (101, 106, 100, 104), (104, 104, 104, 104));
        var r = Run(bars, _ => Signal.Short(105), Config(4));
        Assert.Equal(ExitReason.Stop, r.Trades[0].ExitReason);
        Assert.Equal(105, r.Trades[0].ExitPrice, 9);
    }

    [Fact]
    public void After_a_stop_the_same_direction_waits_for_a_new_signal()
    {
        var bars = Bars((100, 100, 100, 100), (100, 101, 99, 100), (99, 100, 94, 96), (96, 97, 95, 96), (96, 97, 95, 96), (96, 97, 95, 96), (96, 97, 95, 96));
        // Long through index 3 (stopped out on bar 2), flat at 4, long again at 5 -> re-entry on bar 6's open.
        var r = Run(bars, i => i == 4 ? Signal.Flat : Signal.Long(95), Config(7));
        Assert.Equal(2, r.Trades.Count);
        Assert.Equal(ExitReason.Stop, r.Trades[0].ExitReason);
        Assert.Equal(bars[6].OpenTimeMs, r.Trades[1].EntryTimeMs);
    }

    [Fact]
    public void Entry_is_skipped_when_the_stop_is_already_breached_at_the_open()
    {
        var bars = Bars((100, 100, 100, 100), (94, 95, 93, 94), (94, 95, 93, 94));
        var r = Run(bars, _ => Signal.Long(95), Config(2));
        Assert.Empty(r.Trades);
        Assert.Equal(1, r.EntriesSkippedStopBreached);
    }

    [Fact]
    public void Run_starts_flat_and_ignores_a_direction_held_during_warmup()
    {
        var bars = Flat(10, 100);
        var cfg = Config(10, fromMs: T0 + 3 * Hour);
        var held = Run(bars, _ => Signal.Long(), cfg);
        Assert.Empty(held.Trades);

        var fresh = Run(bars, i => i == 5 ? Signal.Flat : Signal.Long(), cfg);
        Assert.Equal(bars[7].OpenTimeMs, Assert.Single(fresh.Trades).EntryTimeMs);
    }

    [Fact]
    public void Strategy_sees_every_bar_in_order_including_warmup()
    {
        var bars = Flat(6, 100);
        var strategy = new ScriptedStrategy(_ => Signal.Flat);
        new BacktestEngine(bars, []).Run(strategy, Config(6, fromMs: T0 + 2 * Hour));
        Assert.Equal([0, 1, 2, 3, 4, 5], strategy.SeenIndexes);
    }

    [Fact]
    public void Open_position_is_closed_at_the_open_of_the_first_bar_after_the_window()
    {
        var bars = Bars((100, 100, 100, 100), (100, 100, 100, 100), (110, 110, 110, 110), (120, 120, 120, 120));
        var r = Run(bars, _ => Signal.Long(), Config(3));
        var t = Assert.Single(r.Trades);
        Assert.Equal(ExitReason.EndOfTest, t.ExitReason);
        Assert.Equal(120, t.ExitPrice);
        Assert.Equal(r.FinalEquity, r.Equity[^1].Equity);
    }

    [Fact]
    public void Open_position_is_closed_at_the_last_close_when_data_ends()
    {
        var bars = Bars((100, 100, 100, 100), (100, 100, 100, 100), (110, 110, 110, 115));
        var r = Run(bars, _ => Signal.Long(), Config(10));
        Assert.Equal(115, Assert.Single(r.Trades).ExitPrice);
    }

    [Fact]
    public void Rejects_unsorted_bars()
    {
        var bars = Flat(3, 100);
        bars.Reverse();
        Assert.Throws<ArgumentException>(() => new BacktestEngine(bars, []));
    }
}
