using Backtester.Core;
using Backtester.Core.Data;
using Backtester.Core.Engine;
using Backtester.Core.Indicators;
using Backtester.Core.Strategies;
using static Backtester.Tests.TestData;

namespace Backtester.Tests;

public class Cycle11StrategyTests
{
    private static BacktestConfig Cfg(int bars, double commission = 0, double slippage = 0) =>
        Config(bars, commission, slippage, new SizingConfig { Mode = SizingMode.RiskPerTrade, RiskPerTrade = 0.01 });

    private static Bar B(int i, double o, double h, double l, double c) => new(T0 + i * Hour, o, h, l, c, 1);

    private static DcaGridBot Long(int orders = 5, double overlap = 0.10, double martingale = 0.15, double tp = 0.01) =>
        new("dca-test", 1, orders, overlap, martingale, tp, 0.05, 1.0, () => new NoDealFilter());

    /// <summary>The bot's planned quantities for a deal opened at <paramref name="p0"/> with <paramref name="equity"/>.</summary>
    private static double[] Planned(double p0, double equity, int dir = 1, int n = 5, double overlap = 0.10, double m = 0.15, double stopBuffer = 0.05)
    {
        var levels = Enumerable.Range(0, n).Select(k => p0 * (1 - dir * overlap * k / (n - 1))).ToArray();
        var w = Enumerable.Range(0, n).Select(k => Math.Pow(1 + m, k)).ToArray();
        double sum = w.Sum(), stop = p0 * (1 - dir * (overlap + stopBuffer));
        double lossPerUnit = Enumerable.Range(0, n).Sum(k => w[k] / sum / levels[k] * dir * (levels[k] - stop));
        double total = Math.Min(0.01 * equity / lossPerUnit, equity);
        return Enumerable.Range(0, n).Select(k => total * w[k] / sum / levels[k]).ToArray();
    }

    [Fact]
    public void A_long_deal_averages_down_and_takes_profit_above_the_new_average()
    {
        // Bar 0 decides; bar 1 opens the deal at 100 and dips to 97 (fills the 97.5 order); bar 2 rallies through the target.
        var bars = new List<Bar> { B(0, 100, 100.1, 99.9, 100), B(1, 100, 100.2, 97, 98), B(2, 98, 102, 97.9, 101.5), B(3, 101.5, 101.6, 101.4, 101.5) };
        var r = new BacktestEngine(bars, []).Run(Long(), Cfg(bars.Count));
        var t = r.Trades[0];
        var q = Planned(100, 10_000);
        double avg = (q[0] * 100 + q[1] * 97.5) / (q[0] + q[1]);
        Assert.Equal(ExitReason.TakeProfit, t.ExitReason);
        Assert.Equal(T0 + Hour, t.EntryTimeMs);
        Assert.Equal(T0 + 2 * Hour, t.ExitTimeMs);
        Assert.Equal(q[0] + q[1], t.Quantity, 9);
        Assert.Equal(avg, t.EntryPrice, 9);
        Assert.Equal(avg * 1.01, t.ExitPrice, 9);
        Assert.Equal((q[0] + q[1]) * avg * 0.01, t.NetPnl, 6);
    }

    [Fact]
    public void A_fully_filled_grid_that_hits_the_stop_loses_one_percent_of_equity()
    {
        // A crash bar fills every averaging order and the stop (85 = 100 x (1 - 0.10 - 0.05)) in one go.
        var bars = new List<Bar> { B(0, 100, 100.1, 99.9, 100), B(1, 100, 100.1, 80, 82), B(2, 82, 82.1, 81.9, 82) };
        var r = new BacktestEngine(bars, []).Run(Long(), Cfg(bars.Count));
        var t = r.Trades[0];
        Assert.Equal(ExitReason.Stop, t.ExitReason);
        Assert.Equal(Planned(100, 10_000).Sum(), t.Quantity, 9);
        Assert.Equal(85, t.ExitPrice, 9);
        Assert.Equal(-100, t.NetPnl, 6);   // 1% of 10,000, no costs
    }

    [Fact]
    public void A_short_deal_mirrors_the_long_one()
    {
        var bars = new List<Bar> { B(0, 100, 100.1, 99.9, 100), B(1, 100, 103, 99.8, 102), B(2, 102, 102.1, 98, 98.5), B(3, 98.5, 98.6, 98.4, 98.5) };
        var bot = new DcaGridBot("dca-test", -1, 5, 0.10, 0.15, 0.01, 0.05, 1.0, () => new NoDealFilter());
        var r = new BacktestEngine(bars, []).Run(bot, Cfg(bars.Count));
        var t = r.Trades[0];
        var q = Planned(100, 10_000, dir: -1);
        double avg = (q[0] * 100 + q[1] * 102.5) / (q[0] + q[1]);
        Assert.Equal((-1, ExitReason.TakeProfit), (t.Direction, t.ExitReason));
        Assert.Equal(avg * 0.99, t.ExitPrice, 9);
        Assert.True(t.NetPnl > 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Cash_fees_and_funding_reconcile_with_the_trades(int dir)
    {
        var (bars, funding) = SyntheticData.Generate(new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc), 24 * 200, 4);
        var bot = new DcaGridBot("dca-test", dir, 5, 0.10, 0.15, 0.01, 0.05, 1.0, () => new NoDealFilter());
        var cfg = Cfg(bars.Count, 0.0004, 0.0002) with { FromMs = bars[24 * 10].OpenTimeMs, ToMs = bars[^1].OpenTimeMs + Hour };
        var r = new BacktestEngine(bars, funding).Run(bot, cfg);
        Assert.True(r.Trades.Count > 50);
        Assert.Contains(r.Trades, t => t.ExitReason == ExitReason.TakeProfit);
        Assert.Equal(r.FinalEquity - cfg.InitialCapital, r.Trades.Sum(t => t.NetPnl), 6);
        Assert.Equal(r.FeesPaid, r.Trades.Sum(t => t.Fees), 6);
        Assert.Equal(r.FundingReceived, r.Trades.Sum(t => t.Funding), 6);
        Assert.Equal(r.FinalEquity, r.Equity[^1].Equity);
        Assert.All(r.Trades, t => Assert.True(t.NetPnl > -0.0125 * 12_000, "a deal lost more than its 1% risk budget plus costs"));
    }

    [Fact]
    public void The_rsi_filter_only_starts_deals_after_an_oversold_close()
    {
        var (bars, funding) = SyntheticData.Generate(new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc), 24 * 120, 6);
        var bot = new DcaGridBot("dca-test", 1, 5, 0.10, 0.15, 0.01, 0.05, 1.0, () => new RsiDealFilter(14, 30));
        var cfg = Cfg(bars.Count) with { FromMs = bars[0].OpenTimeMs, ToMs = bars[^1].OpenTimeMs + Hour };
        var r = new BacktestEngine(bars, funding).Run(bot, cfg);
        Assert.NotEmpty(r.Trades);
        var rsi = new Rsi(14);
        var values = bars.Select(b => { rsi.Update(b.Close); return rsi.IsReady ? rsi.Value : double.NaN; }).ToList();
        Assert.All(r.Trades, t => Assert.True(values[(int)((t.EntryTimeMs - bars[0].OpenTimeMs) / Hour) - 1] < 30));
    }
}
