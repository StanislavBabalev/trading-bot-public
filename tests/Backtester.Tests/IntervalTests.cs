using Backtester.Core;
using Backtester.Core.Data;
using Backtester.Core.Engine;
using Backtester.Core.Indicators;
using Backtester.Core.Strategies;
using Backtester.Core.Validation;
using static Backtester.Tests.TestData;

namespace Backtester.Tests;

/// <summary>Bar intervals other than 1h: clock-time builders, clock-time strategy rules and the look-ahead audit.</summary>
public class IntervalTests
{
    private const long Minute = 60_000L;

    private static List<Bar> Bars(int count, int minutes) =>
        Enumerable.Range(0, count).Select(i => new Bar(T0 + i * minutes * Minute, 100 + i, 101 + i, 99 + i, 100.5 + i, 1)).ToList();

    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(240)]
    public void Daily_bars_close_on_the_last_bar_of_each_utc_day(int minutes)
    {
        int perDay = 1440 / minutes;
        var bars = Bars(3 * perDay, minutes);
        var d = new DailyBars();
        var closedAt = new List<int>();
        for (int i = 0; i < bars.Count; i++)
        {
            d.Update(bars[i]);
            if (d.DayClosed) closedAt.Add(i);
        }
        Assert.Equal([perDay - 1, 2 * perDay - 1, 3 * perDay - 1], closedAt);
        var day = d.Back(0);
        Assert.Equal(T0 + 2 * 86_400_000L, day.OpenTimeMs);
        Assert.Equal(bars[2 * perDay].Open, day.Open);
        Assert.Equal(bars[^1].Close, day.Close);
        Assert.Equal(perDay, day.Volume);
    }

    [Fact]
    public void Period_bars_build_from_any_bar_that_divides_the_period_and_refuse_longer_ones()
    {
        var p = new PeriodBars(4);
        var closedAt = new List<int>();
        var quarter = Bars(32, 15);
        for (int i = 0; i < quarter.Count; i++)
        {
            p.Update(quarter[i]);
            if (p.Closed) closedAt.Add(i);
        }
        Assert.Equal([15, 31], closedAt);   // the 03:45 and 07:45 bars

        var eight = new PeriodBars(8);
        var fourHour = Bars(6, 240);
        closedAt.Clear();
        for (int i = 0; i < fourHour.Count; i++)
        {
            eight.Update(fourHour[i]);
            if (eight.Closed) closedAt.Add(i);
        }
        Assert.Equal([1, 3, 5], closedAt);

        foreach (int hours in new[] { 2, 6 })
        {
            var tooShort = new PeriodBars(hours);
            tooShort.Update(fourHour[0]);
            Assert.Throws<ArgumentException>(() => tooShort.Update(fourHour[1]));
        }
    }

    [Theory]
    [InlineData(15)]
    [InlineData(240)]
    public void Session_momentum_decides_on_the_bar_that_ends_at_noon(int minutes)
    {
        // Flat for 20 days (ATR warm-up), then a day that rallies from midnight: the decision bar is the one closing at 12:00.
        int perDay = 1440 / minutes, n = 22 * perDay;
        var bars = Enumerable.Range(0, n).Select(i =>
        {
            double c = i < 21 * perDay ? 100 : 100 + 3.0 * (i - 21 * perDay + 1);
            double o = i < 21 * perDay || i == 21 * perDay ? 100 : c - 3.0;
            return new Bar(T0 + i * minutes * Minute, o, Math.Max(o, c) + 0.5, Math.Min(o, c) - 0.5, c, 1);
        }).ToList();
        var cfg = Config(n, sizing: new SizingConfig { Mode = SizingMode.RiskPerTrade, RiskPerTrade = 0.01 }) with
        {
            ToMs = T0 + n * minutes * Minute,
            BarIntervalMinutes = minutes,
        };
        var r = new BacktestEngine(bars, []).Run(new SessionMomentum(1, 1, 3), cfg);
        var t = Assert.Single(r.Trades);
        Assert.Equal(1, t.Direction);
        Assert.Equal(T0 + 21 * 86_400_000L + 12 * 3_600_000L, t.EntryTimeMs);
    }

    public static TheoryData<string, int> StrategiesAndIntervals()
    {
        var data = new TheoryData<string, int>();
        foreach (var d in StrategyRegistry.All)
            foreach (int minutes in new[] { 15, 240 })
                data.Add(d.Name, minutes);
        return data;
    }

    /// <summary>
    /// The audit at 15m and 4h bars. Period strategies whose N-hour period a 4h bar cannot build (2h, 6h) refuse to run
    /// there instead of trading on wrong periods.
    /// </summary>
    [Theory]
    [MemberData(nameof(StrategiesAndIntervals))]
    public void Every_registered_strategy_passes_the_look_ahead_audit_at_other_intervals(string name, int minutes)
    {
        var def = StrategyRegistry.Get(name);
        var p = name == "dca-trend-long" ? new Dictionary<string, double>(def.Defaults) { ["maDays"] = 50 } : def.Defaults;
        if (p.TryGetValue("tfHours", out double tf) && tf * 60 % minutes != 0)
        {
            Assert.Throws<ArgumentException>(() => LookAheadAudit.Run(def, p, intervalMinutes: minutes));
            return;
        }
        var audit = LookAheadAudit.Run(def, p, intervalMinutes: minutes);
        Assert.True(audit.Problems.Count == 0, $"{name} at {minutes}m: {string.Join("; ", audit.Problems)}");
        Assert.True(audit.Verdict == "pass", $"{name} at {minutes}m: {audit.Verdict} ({audit.TradesBeforeCut} trades)");
    }

    [Fact]
    public void Synthetic_data_at_other_intervals_settles_funding_every_8_hours()
    {
        foreach (int minutes in new[] { 15, 240 })
        {
            var (bars, funding) = SyntheticData.Generate(new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc), 3 * 1440 / minutes, 1, minutes);
            Assert.Equal(minutes * Minute, bars[1].OpenTimeMs - bars[0].OpenTimeMs);
            Assert.Equal(9, funding.Count);
            Assert.All(funding.Zip(funding.Skip(1)), f => Assert.Equal(8 * 3_600_000L, f.Second.TimeMs - f.First.TimeMs));
        }
    }
}
