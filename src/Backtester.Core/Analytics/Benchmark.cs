using Backtester.Core.Engine;

namespace Backtester.Core.Analytics;

public static class Benchmark
{
    /// <summary>
    /// Buy at the first window bar's open with all capital (1x, spot-style: no funding), hold, sell at the
    /// last window bar's close. Same commission and slippage as the strategy.
    /// </summary>
    public static BacktestResult BuyAndHold(IReadOnlyList<Bar> bars, BacktestConfig cfg)
    {
        var window = bars.Where(b => b.OpenTimeMs >= cfg.FromMs && b.OpenTimeMs < cfg.ToMs).ToList();
        if (window.Count == 0) throw new InvalidOperationException("No bars inside the benchmark window.");

        double comm = cfg.Costs.CommissionRate, slip = cfg.Costs.SlippageRate;
        var first = window[0];
        double entry = first.Open * (1 + slip);
        double notional = cfg.InitialCapital / (1 + comm);
        double qty = notional / entry;
        double entryFee = notional * comm;

        var equity = window
            .Select(b => new EquityPoint(b.OpenTimeMs + cfg.BarIntervalMs, qty * b.Close, qty))
            .ToList();

        var last = window[^1];
        double exit = last.Close * (1 - slip);
        double exitFee = qty * exit * comm;
        double gross = qty * (exit - entry);
        double final = cfg.InitialCapital + gross - entryFee - exitFee;
        equity[^1] = equity[^1] with { Equity = final, PositionQty = 0 };

        long endMs = last.OpenTimeMs + cfg.BarIntervalMs;
        return new BacktestResult
        {
            StrategyName = "buy-and-hold",
            Parameters = new Dictionary<string, double>(),
            Config = cfg with { FundingEnabled = false },
            Trades = [new Trade(1, first.OpenTimeMs, entry, endMs, exit, qty, gross, entryFee + exitFee, 0, ExitReason.EndOfTest, window.Count)],
            Equity = equity,
            StartMs = first.OpenTimeMs,
            EndMs = endMs,
            FinalEquity = final,
            FeesPaid = entryFee + exitFee,
            SlippageCost = qty * (first.Open + last.Close) * slip,
            TradedNotional = notional + qty * exit,
            BarsInWindow = window.Count,
            BarsInMarket = window.Count,
        };
    }
}
