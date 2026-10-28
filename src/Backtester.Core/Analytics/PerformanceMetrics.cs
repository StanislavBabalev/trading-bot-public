using Backtester.Core.Engine;

namespace Backtester.Core.Analytics;

/// <summary>
/// Headline metrics for a run. Sharpe and Sortino use daily (UTC) equity returns annualized with √365.
/// Max drawdown is measured on bar-close equity, so intrabar drawdowns are not seen.
/// </summary>
public sealed record PerformanceMetrics(
    string From,
    string To,
    double Years,
    double InitialCapital,
    double FinalEquity,
    double NetProfit,
    double NetProfitPct,
    double CagrPct,
    double MaxDrawdownPct,
    double ProfitFactor,
    double Sharpe,
    double Sortino,
    double Calmar,
    int Trades,
    double TradesPerYear,
    double WinRatePct,
    double AvgTradeNetPnl,
    double TimeInMarketPct,
    double TurnoverPerYear,
    double FeesPaid,
    double FundingReceived,
    double SlippageCost,
    int DailyReturnCount,
    double DailySharpe,
    double DailySkewness,
    double DailyKurtosis)
{
    public static PerformanceMetrics Compute(BacktestResult r)
    {
        double initial = r.Config.InitialCapital;
        double years = (r.EndMs - r.StartMs) / (365.25 * 86_400_000.0);
        double final = r.FinalEquity;

        // Span loops over the equity curve (tens of thousands of points); same arithmetic, in the same order, as LINQ.
        var equity = AsSpan(r.Equity);
        double peak = initial, maxDd = 0, equitySum = 0;
        foreach (ref readonly var p in equity)
        {
            peak = Math.Max(peak, p.Equity);
            if (peak > 0) maxDd = Math.Max(maxDd, (peak - p.Equity) / peak);
            equitySum += p.Equity;
        }

        var daily = DailyReturns(r.Equity, initial);
        double mean = Statistics.Mean(daily), sd = Statistics.StdDev(daily);
        double dailySharpe = sd > 0 ? mean / sd : 0;
        double downside = daily.Count > 0 ? Math.Sqrt(daily.Sum(x => Math.Min(x, 0) * Math.Min(x, 0)) / daily.Count) : double.NaN;

        double cagr = final <= 0 ? -1 : years > 0 ? Math.Pow(final / initial, 1 / years) - 1 : double.NaN;
        double grossWin = r.Trades.Where(t => t.NetPnl > 0).Sum(t => t.NetPnl);
        double grossLoss = -r.Trades.Where(t => t.NetPnl < 0).Sum(t => t.NetPnl);
        double pf = grossLoss > 0 ? grossWin / grossLoss : grossWin > 0 ? double.PositiveInfinity : double.NaN;
        double avgEquity = equity.Length > 0 ? equitySum / equity.Length : initial;

        return new PerformanceMetrics(
            TimeUtil.Iso(r.StartMs), TimeUtil.Iso(r.EndMs), years, initial, final,
            final - initial, (final / initial - 1) * 100, cagr * 100, maxDd * 100, pf,
            dailySharpe * Math.Sqrt(365),
            downside > 0 ? mean / downside * Math.Sqrt(365) : double.NaN,
            maxDd > 0 ? cagr / maxDd : double.NaN,
            r.Trades.Count,
            years > 0 ? r.Trades.Count / years : double.NaN,
            r.Trades.Count > 0 ? 100.0 * r.Trades.Count(t => t.NetPnl > 0) / r.Trades.Count : double.NaN,
            r.Trades.Count > 0 ? r.Trades.Average(t => t.NetPnl) : double.NaN,
            r.BarsInWindow > 0 ? 100.0 * r.BarsInMarket / r.BarsInWindow : 0,
            years > 0 && avgEquity > 0 ? r.TradedNotional / avgEquity / years : double.NaN,
            r.FeesPaid, r.FundingReceived, r.SlippageCost,
            daily.Count, dailySharpe, Statistics.Skewness(daily), Statistics.Kurtosis(daily));
    }

    /// <summary>Simple returns of end-of-UTC-day equity; the first day is measured from the initial capital.</summary>
    public static List<double> DailyReturns(IReadOnlyList<EquityPoint> equity, double initial)
    {
        var dayEnds = new List<double>();
        long currentDay = long.MinValue;
        foreach (ref readonly var p in AsSpan(equity))
        {
            // A close at exactly 00:00 belongs to the day that just ended.
            long day = TimeUtil.DayIndex(p.TimeMs - 1);
            if (day != currentDay) { dayEnds.Add(p.Equity); currentDay = day; }
            else dayEnds[^1] = p.Equity;
        }
        var returns = new List<double>(dayEnds.Count);
        double prev = initial;
        foreach (var e in dayEnds)
        {
            returns.Add(prev > 0 ? e / prev - 1 : 0);
            prev = e;
        }
        return returns;
    }

    private static ReadOnlySpan<EquityPoint> AsSpan(IReadOnlyList<EquityPoint> equity) => equity switch
    {
        List<EquityPoint> list => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list),
        EquityPoint[] array => array,
        _ => equity.ToArray(),
    };
}
