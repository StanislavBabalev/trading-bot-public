namespace Backtester.Core;

/// <summary>One OHLCV candle. <see cref="OpenTimeMs"/> is the bar's open time in Unix milliseconds (UTC).</summary>
public readonly record struct Bar(long OpenTimeMs, double Open, double High, double Low, double Close, double Volume);

/// <summary>A settled perpetual funding rate. A positive rate means longs pay shorts.</summary>
public readonly record struct FundingRate(long TimeMs, double Rate);

/// <summary>
/// The position a strategy wants after a bar closes. The engine executes it at the next bar's open.
/// <paramref name="StopPrice"/> sets the protective stop when opening (or updates it while holding);
/// null while holding keeps the current stop.
/// </summary>
public readonly record struct Signal(int Direction, double? StopPrice = null)
{
    public static Signal Flat => new(0);
    public static Signal Long(double? stop = null) => new(1, stop);
    public static Signal Short(double? stop = null) => new(-1, stop);
}

public enum ExitReason { Signal, Stop, RiskHalt, EndOfTest, TakeProfit }

/// <summary>
/// A closed round trip. <see cref="Funding"/> is cash received (negative when the position paid funding).
/// </summary>
public sealed record Trade(
    int Direction,
    long EntryTimeMs,
    double EntryPrice,
    long ExitTimeMs,
    double ExitPrice,
    double Quantity,
    double GrossPnl,
    double Fees,
    double Funding,
    ExitReason ExitReason,
    int BarsHeld)
{
    public double NetPnl => GrossPnl - Fees + Funding;
}

/// <summary>Equity marked at a bar close. <see cref="TimeMs"/> is the close time (open + interval).</summary>
public readonly record struct EquityPoint(long TimeMs, double Equity, double PositionQty);
