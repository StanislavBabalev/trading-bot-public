using Backtester.Core.Engine;

namespace Backtester.Core.Strategies;

/// <summary>
/// A strategy sees each bar once, after it closes, and returns the position it wants.
/// The engine fills that at the next bar's open; it can never fill on the bar that produced the signal.
/// </summary>
public interface IStrategy
{
    string Name { get; }
    Signal OnBarClose(StrategyContext ctx);
}

/// <summary>
/// A strategy that needs order types the bar engine does not model (a resting grid of averaging orders, a take-profit
/// limit) runs its own simulation over the same bars, funding and config, following the engine's conventions (start
/// flat in the window, signals act at the next bar's open, funding settles on the position held into each timestamp,
/// commission and slippage on every fill, equity marked at each close, everything closed at the window end) and returns
/// an ordinary result. <see cref="Engine.BacktestEngine.Run"/> hands such strategies their data and does nothing else.
/// </summary>
public interface ISelfSimulatingStrategy : IStrategy
{
    Engine.BacktestResult Simulate(Bar[] bars, FundingRate[] funding, BacktestConfig cfg, IReadOnlyDictionary<string, double> parameters);
}

/// <summary>What a strategy may look at: the bar that just closed, earlier bars, and its current position.</summary>
public sealed class StrategyContext
{
    private readonly Bar[] _bars;

    internal StrategyContext(Bar[] bars, FeatureCache features, long intervalMs)
    {
        _bars = bars;
        Features = features;
        IntervalMs = intervalMs;
    }

    /// <summary>Indicator series over the whole bar history, shared across runs. Read only up to <see cref="Index"/>.</summary>
    public FeatureCache Features { get; }
    /// <summary>Length of one bar in milliseconds (the data's interval; a UTC day holds a whole number of bars).</summary>
    public long IntervalMs { get; }

    public int Index { get; internal set; }
    public Bar Bar => _bars[Index];
    /// <summary>+1 long, -1 short, 0 flat, as of this bar's close.</summary>
    public int PositionDirection { get; internal set; }
    public double? StopPrice { get; internal set; }
    /// <summary>True for bars before the trading window (indicator warm-up).</summary>
    public bool IsWarmup { get; internal set; }

    /// <summary>The bar <paramref name="barsAgo"/> bars before the current one (0 = current). Future bars are not reachable.</summary>
    public Bar Back(int barsAgo)
    {
        if (barsAgo < 0) throw new ArgumentOutOfRangeException(nameof(barsAgo), "Look-ahead is not allowed.");
        int i = Index - barsAgo;
        if (i < 0) throw new ArgumentOutOfRangeException(nameof(barsAgo), "Not enough history.");
        return _bars[i];
    }
}
