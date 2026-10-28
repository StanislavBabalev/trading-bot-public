using Backtester.Core;
using Backtester.Core.Engine;
using Backtester.Core.Strategies;
using static Backtester.Tests.TestData;

namespace Backtester.Tests;

/// <summary>Cycle 12 rules (artifacts/hunt/cycle_12_ideas/research.md). Look-ahead: ValidationTests audits every registered strategy.</summary>
public class Cycle12StrategyTests
{
    private static readonly string[] Cycle12 = ["cascade-fade", "turmoil-reversal", "round-number-bounce"];

    [Fact]
    public void Grids_fit_the_optimizer_budget_and_defaults_sit_inside_them()
    {
        foreach (var name in Cycle12)
        {
            var def = StrategyRegistry.Get(name);
            Assert.NotNull(def.Create(def.Defaults));
            long size = def.DefaultSpace.Aggregate(1L, (acc, p) => acc * p.Values().Count);
            Assert.True(size <= 200 - 2 * def.DefaultSpace.Count, $"{name}: grid of {size} points would be sampled");
            foreach (var p in def.DefaultSpace)
            {
                var values = p.Values();
                Assert.Contains(values, v => Math.Abs(v - def.Defaults[p.Name]) < 1e-9);
                Assert.True(def.Defaults[p.Name] > values[0] && def.Defaults[p.Name] < values[^1], $"{name}.{p.Name} default on the grid edge");
            }
        }
    }

    private static Bar Flat(int i, double price, double halfRange, double volume = 1) =>
        new(T0 + i * Hour, price, price + halfRange, price - halfRange, price, volume);

    /// <summary>Funding settled every 8h from T0; rising rates put the latest 3-day mean at the top of its 90-day rank.</summary>
    private static List<FundingRate> Funding(int bars, bool rising) =>
        Enumerable.Range(0, bars / 8 + 1).Select(j => new FundingRate(T0 + j * 8 * Hour, (rising ? j : -j) * 1e-6)).ToList();

    private static BacktestResult Run(IStrategy s, List<Bar> bars, List<FundingRate> funding) =>
        new BacktestEngine(bars, funding).Run(s, Config(bars.Count));

    // ------------------------------------------------------------------ cascade-fade

    private static List<Bar> Cascade(int at, double close, Func<int, double> after, int total)
    {
        var bars = new List<Bar>();
        for (int i = 0; i < at; i++) bars.Add(Flat(i, 100, 0.5));
        bars.Add(new Bar(T0 + at * Hour, 100, 100.2, 96.5, close, 5));
        for (int i = at + 1; i < total; i++) bars.Add(Flat(i, after(i - at), 0.5));
        return bars;
    }

    [Fact]
    public void Cascade_fade_buys_a_high_volume_drop_when_longs_are_crowded_and_holds_for_holdBars()
    {
        int at = 24 * 96, total = at + 40;   // 96 days: the 3-day / 90-day funding rank is ready
        var bars = Cascade(at, 97, _ => 97.5, total);
        var r = Run(new CascadeFade(1.5, 1.5, 24), bars, Funding(total, rising: true));
        var t = Assert.Single(r.Trades);
        Assert.Equal(1, t.Direction);
        Assert.Equal(bars[at + 1].OpenTimeMs, t.EntryTimeMs);
        Assert.Equal(bars[at + 25].OpenTimeMs, t.ExitTimeMs);   // 24 bars held, exit at the next open
        Assert.Equal(ExitReason.Signal, t.ExitReason);
    }

    [Fact]
    public void Cascade_fade_exits_early_once_the_drop_has_fully_retraced()
    {
        int at = 24 * 96, total = at + 40;
        var bars = Cascade(at, 97, k => k < 5 ? 98 : 100.5, total);
        var t = Assert.Single(Run(new CascadeFade(1.5, 1.5, 24), bars, Funding(total, rising: true)).Trades);
        Assert.Equal(bars[at + 6].OpenTimeMs, t.ExitTimeMs);   // the close at k = 5 is back above the cascade open
    }

    [Fact]
    public void Cascade_fade_ignores_a_drop_when_shorts_are_the_crowded_side()
    {
        int at = 24 * 96, total = at + 40;
        Assert.Empty(Run(new CascadeFade(1.5, 1.5, 24), Cascade(at, 97, _ => 97.5, total), Funding(total, rising: false)).Trades);
    }

    // ------------------------------------------------------------------ turmoil-reversal

    /// <summary>100 calm days, then optionally 10 wide 4h periods, then a 4h period falling 100 -> 97, then flat at 97.</summary>
    private static (List<Bar> Bars, int DropEnd) Turmoil(bool turbulent)
    {
        var bars = new List<Bar>();
        int i = 0;
        for (; i < 24 * 100; i++) bars.Add(Flat(i, 100, 0.1));
        if (turbulent)
            for (int k = 0; k < 40; k++, i++) bars.Add(Flat(i, 100, 1.5));
        double[] drop = [100, 99.2, 98.4, 97.6, 97];
        for (int k = 0; k < 4; k++, i++) bars.Add(new Bar(T0 + i * Hour, drop[k], drop[k] + 0.1, drop[k + 1] - 0.1, drop[k + 1], 1));
        int dropEnd = i - 1;
        for (int k = 0; k < 60; k++, i++) bars.Add(Flat(i, 97, 0.1));
        return (bars, dropEnd);
    }

    [Fact]
    public void Turmoil_reversal_fades_a_large_4h_drop_only_in_high_volatility()
    {
        var (bars, dropEnd) = Turmoil(turbulent: true);
        var t = Assert.Single(Run(new TurmoilReversal(1, 0.7, 6), bars, []).Trades);
        Assert.Equal(1, t.Direction);
        Assert.Equal(bars[dropEnd + 1].OpenTimeMs, t.EntryTimeMs);   // signal on the 4h close, fill at the next hour's open
        Assert.Equal(bars[dropEnd + 1 + 24].OpenTimeMs, t.ExitTimeMs); // 6 periods of 4h, still below EMA(20)

        var (calm, _) = Turmoil(turbulent: false);
        Assert.Empty(Run(new TurmoilReversal(1, 0.7, 6), calm, []).Trades);
    }

    // ------------------------------------------------------------------ round-number-bounce

    private static List<Bar> Levels(params Bar[] tail)
    {
        var bars = Enumerable.Range(0, 48).Select(i => Flat(i, 30_500, 100)).ToList();
        bars.AddRange(tail.Select((b, k) => b with { OpenTimeMs = T0 + (48 + k) * Hour }));
        for (int k = 0; k < 30; k++) bars.Add(Flat(bars.Count, bars[^1].Close, 100));
        return bars;
    }

    [Fact]
    public void Round_number_bounce_buys_the_first_test_of_a_level_that_holds()
    {
        var bars = Levels(new Bar(0, 30_300, 30_350, 29_950, 30_100, 1));
        var t = Assert.Single(Run(new RoundNumberBounce(1000, 24, 24), bars, []).Trades);
        Assert.Equal(1, t.Direction);
        Assert.Equal(bars[49].OpenTimeMs, t.EntryTimeMs);
    }

    [Fact]
    public void Round_number_bounce_sells_a_rejected_first_test_from_below()
    {
        var bars = Levels(new Bar(0, 30_700, 31_050, 30_650, 30_900, 1));
        Assert.Equal(-1, Assert.Single(Run(new RoundNumberBounce(1000, 24, 24), bars, []).Trades).Direction);
    }

    [Fact]
    public void Round_number_bounce_skips_a_broken_level_a_two_sided_bar_and_a_retest()
    {
        Assert.Empty(Run(new RoundNumberBounce(1000, 24, 24), Levels(new Bar(0, 30_300, 30_350, 29_800, 29_900, 1)), []).Trades);   // closed below
        Assert.Empty(Run(new RoundNumberBounce(1000, 24, 24), Levels(new Bar(0, 30_500, 31_010, 29_990, 30_500, 1)), []).Trades);   // both levels
        // First test at bar 48 (traded, held 6 bars); the second test 11 hours later is not a first test.
        var tail = new List<Bar> { new(0, 30_200, 30_250, 29_990, 30_050, 1) };
        tail.AddRange(Enumerable.Range(0, 10).Select(_ => Flat(0, 30_300, 50)));
        tail.Add(new Bar(0, 30_200, 30_250, 29_980, 30_100, 1));
        var t = Assert.Single(Run(new RoundNumberBounce(1000, 24, 6), Levels(tail.ToArray()), []).Trades);
        Assert.Equal(T0 + 49 * Hour, t.EntryTimeMs);
    }
}
