using Backtester.Core.Engine;

namespace Backtester.Core.Analytics;

/// <summary>A strategy's result in one calendar quarter of a continuous run (mark-to-market, costs and funding included).</summary>
public sealed record QuarterReturn(string Quarter, double StartEquity, double EndEquity, double ReturnPct, int TradesClosed, double ClosedTradesNetPnl)
{
    /// <summary>The hunt's quarter rule: the quarter made money. A quarter without trades or exposure does not pass.</summary>
    public bool MadeMoney => EndEquity > StartEquity;
}

/// <summary>What the market did in one calendar quarter.</summary>
public sealed record MarketQuarter(
    string Quarter,
    int Bars,
    double BuyHoldPct,
    double RealizedVolPct,
    double EfficiencyRatio,
    double MeanDailyRangePct,
    double MaxDrawdownPct,
    double MeanFundingRate);

public static class Quarterly
{
    /// <summary>"2022Q3" for a UTC timestamp.</summary>
    public static string Label(long ms)
    {
        var t = TimeUtil.FromMs(ms);
        return $"{t.Year}Q{(t.Month - 1) / 3 + 1}";
    }

    /// <summary>
    /// Splits a run into calendar quarters. Equity points are bar closes, so a close at exactly a quarter boundary belongs
    /// to the quarter that just ended. Trades count in the quarter their exit filled (an end-of-test exit at the window
    /// boundary counts in the last quarter). The first quarter starts from the initial capital.
    /// </summary>
    public static List<QuarterReturn> Returns(BacktestResult r)
    {
        var rows = new List<QuarterReturn>();
        var trades = r.Trades.GroupBy(t => Label(t.ExitReason == ExitReason.EndOfTest ? t.ExitTimeMs - 1 : t.ExitTimeMs))
            .ToDictionary(g => g.Key, g => (Count: g.Count(), Net: g.Sum(t => t.NetPnl)));
        double start = r.Config.InitialCapital;
        string? current = null;
        double last = start;
        foreach (var p in r.Equity)
        {
            string q = Label(p.TimeMs - 1);
            if (current is not null && q != current)
            {
                rows.Add(Row(current, start, last, trades));
                start = last;
            }
            current = q;
            last = p.Equity;
        }
        if (current is not null) rows.Add(Row(current, start, last, trades));
        return rows;
    }

    private static QuarterReturn Row(string q, double start, double end, Dictionary<string, (int Count, double Net)> trades)
    {
        var (count, net) = trades.TryGetValue(q, out var t) ? t : (0, 0.0);
        return new QuarterReturn(q, start, end, start > 0 ? (end / start - 1) * 100 : double.NaN, count, net);
    }

    /// <summary>Per-quarter market statistics over bars opening in [fromMs, toMs).</summary>
    public static List<MarketQuarter> Market(IReadOnlyList<Bar> bars, IReadOnlyList<FundingRate> funding, long fromMs, long toMs)
    {
        var rows = new List<MarketQuarter>();
        foreach (var g in bars.Where(b => b.OpenTimeMs >= fromMs && b.OpenTimeMs < toMs).GroupBy(b => Label(b.OpenTimeMs)))
        {
            var q = g.ToList();
            var logRet = q.Skip(1).Select((b, i) => Math.Log(b.Close / q[i].Close)).ToList();
            double barsPerYear = q.Count > 1 ? 365.0 * 86_400_000L / (q[1].OpenTimeMs - q[0].OpenTimeMs) : 365.0 * 24;
            double vol = Statistics.StdDev(logRet) * Math.Sqrt(barsPerYear) * 100;

            // Efficiency ratio on daily closes: |net move| / sum of |daily moves| (1 = straight line, ~0 = chop).
            var dailyCloses = q.GroupBy(b => TimeUtil.DayIndex(b.OpenTimeMs)).Select(d => d.Last().Close).Prepend(q[0].Open).ToList();
            double path = dailyCloses.Skip(1).Select((c, i) => Math.Abs(c - dailyCloses[i])).Sum();
            double efficiency = path > 0 ? Math.Abs(dailyCloses[^1] - dailyCloses[0]) / path : 0;

            double range = q.GroupBy(b => TimeUtil.DayIndex(b.OpenTimeMs))
                .Average(d => (d.Max(b => b.High) - d.Min(b => b.Low)) / d.First().Open) * 100;

            double peak = double.MinValue, maxDd = 0;
            foreach (var b in q)
            {
                peak = Math.Max(peak, b.Close);
                maxDd = Math.Max(maxDd, (peak - b.Close) / peak);
            }

            long qFrom = q[0].OpenTimeMs, qTo = q[^1].OpenTimeMs;
            var rates = funding.Where(f => f.TimeMs >= qFrom && f.TimeMs <= qTo).Select(f => f.Rate).ToList();
            rows.Add(new MarketQuarter(g.Key, q.Count, (q[^1].Close / q[0].Open - 1) * 100, vol, efficiency, range, maxDd * 100,
                rates.Count > 0 ? rates.Average() : double.NaN));
        }
        return rows;
    }
}
