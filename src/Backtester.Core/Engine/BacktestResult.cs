namespace Backtester.Core.Engine;

public sealed class BacktestResult
{
    public required string StrategyName { get; init; }
    public required IReadOnlyDictionary<string, double> Parameters { get; init; }
    public required BacktestConfig Config { get; init; }
    public required IReadOnlyList<Trade> Trades { get; init; }
    public required IReadOnlyList<EquityPoint> Equity { get; init; }

    /// <summary>Open time of the first bar traded.</summary>
    public long StartMs { get; init; }
    /// <summary>Close time of the last bar traded.</summary>
    public long EndMs { get; init; }
    public double FinalEquity { get; init; }

    public double FeesPaid { get; init; }
    /// <summary>Net funding cash received over the run (negative = paid).</summary>
    public double FundingReceived { get; init; }
    /// <summary>Price impact of slippage across all fills, in quote currency.</summary>
    public double SlippageCost { get; init; }
    /// <summary>Sum of |notional| over every fill (entries and exits).</summary>
    public double TradedNotional { get; init; }

    public int BarsInWindow { get; init; }
    public int BarsInMarket { get; init; }

    public int EntriesSkippedNoStop { get; init; }
    public int EntriesSkippedStopBreached { get; init; }
    public int EntriesSkippedNoVolEstimate { get; init; }
    public int DailyLossStopsHit { get; init; }
    public bool Halted { get; init; }
    public string? HaltReason { get; init; }
}
