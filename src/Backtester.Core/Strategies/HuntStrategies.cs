using Backtester.Core.Indicators;

namespace Backtester.Core.Strategies;

/// <summary>
/// Base for strategies with explicit entries and exits. It keeps the strategy's view of its position in
/// sync with the engine's (a stop-out or a skipped entry leaves the engine flat), counts bars held, and
/// leaves <see cref="Decide"/> to return Long/Short with a stop on entry, Flat to exit, or <c>Hold</c>.
/// </summary>
public abstract class EntryExitStrategy : IStrategy
{
    protected int Pos { get; private set; }
    protected int BarsHeld { get; private set; }
    protected Signal Hold => new(Pos);

    public abstract string Name { get; }

    public Signal OnBarClose(StrategyContext ctx)
    {
        if (ctx.PositionDirection != Pos) { Pos = ctx.PositionDirection; BarsHeld = 0; }
        else if (Pos != 0) BarsHeld++;

        var s = Decide(ctx);
        if (s.Direction != Pos) { Pos = s.Direction; BarsHeld = 0; }
        return s;
    }

    /// <summary>Update indicators with <c>ctx.Bar</c> and return the desired position.</summary>
    protected abstract Signal Decide(StrategyContext ctx);

    protected static Signal LongWithStop(Bar b, double atr, double mult) => Signal.Long(b.Close - atr * mult);
    protected static Signal ShortWithStop(Bar b, double atr, double mult) => Signal.Short(b.Close + atr * mult);
}

/// <summary>
/// Trend: Donchian channel breakout. Enter long when the close breaks above the highest high of the previous
/// <c>entryLen</c> bars (short below the lowest low). Exit long when the close falls below the lowest low of the
/// previous <c>exitLen</c> bars (short: above the highest high). Fixed stop at entry close ∓ ATR(14) × atrMult.
/// </summary>
public sealed class DonchianBreakout(int entryLen, int exitLen, double atrMult) : EntryExitStrategy
{
    private readonly RollingExtreme _hi = new(entryLen, max: true), _lo = new(entryLen, max: false);
    private readonly RollingExtreme _exitHi = new(exitLen, max: true), _exitLo = new(exitLen, max: false);
    private readonly Atr _atr = new(14);

    public override string Name => $"donchian-breakout({entryLen},{exitLen},{atrMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        _atr.Update(b);
        var s = Hold;
        if (_hi.IsReady && _exitLo.IsReady && _atr.IsReady)
        {
            if (Pos == 0)
            {
                if (b.Close > _hi.Value) s = LongWithStop(b, _atr.Value, atrMult);
                else if (b.Close < _lo.Value) s = ShortWithStop(b, _atr.Value, atrMult);
            }
            else if (Pos > 0 && b.Close < _exitLo.Value) s = Signal.Flat;
            else if (Pos < 0 && b.Close > _exitHi.Value) s = Signal.Flat;
        }
        // channels cover the bars *before* the current one
        _hi.Add(b.High); _lo.Add(b.Low); _exitHi.Add(b.High); _exitLo.Add(b.Low);
        return s;
    }
}

/// <summary>
/// Mean reversion: Bollinger band fade. Enter long when the close is below SMA(bbLen) − bbMult × stdev
/// (short above the upper band). Exit at the middle band or after 48 bars. Fixed ATR(14) × atrMult stop.
/// </summary>
public sealed class BollingerReversion(int bbLen, double bbMult, double atrMult) : EntryExitStrategy
{
    private const int MaxHold = 48;
    private readonly RollingStats _bb = new(bbLen);
    private readonly Atr _atr = new(14);

    public override string Name => $"bollinger-reversion({bbLen},{bbMult},{atrMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        _bb.Add(b.Close);
        _atr.Update(b);
        if (!_bb.IsReady || !_atr.IsReady) return Hold;

        double mid = _bb.Mean, band = bbMult * _bb.StdDev;
        if (Pos == 0)
        {
            if (b.Close < mid - band) return LongWithStop(b, _atr.Value, atrMult);
            if (b.Close > mid + band) return ShortWithStop(b, _atr.Value, atrMult);
            return Hold;
        }
        if (BarsHeld >= MaxHold) return Signal.Flat;
        if (Pos > 0 && b.Close >= mid) return Signal.Flat;
        if (Pos < 0 && b.Close <= mid) return Signal.Flat;
        return Hold;
    }
}

/// <summary>
/// Volatility breakout / range expansion: Keltner channel. Enter long when the close is above
/// EMA(emaLen) + bandMult × ATR(14) (short below the lower band); exit when the close crosses back over the EMA.
/// Fixed ATR(14) × atrMult stop.
/// </summary>
public sealed class KeltnerBreakout(int emaLen, double bandMult, double atrMult) : EntryExitStrategy
{
    private readonly Ema _ema = new(emaLen);
    private readonly Atr _atr = new(14);

    public override string Name => $"keltner-breakout({emaLen},{bandMult},{atrMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        _ema.Update(b.Close);
        _atr.Update(b);
        if (!_ema.IsReady || !_atr.IsReady) return Hold;

        double mid = _ema.Value, band = bandMult * _atr.Value;
        if (Pos == 0)
        {
            if (b.Close > mid + band) return LongWithStop(b, _atr.Value, atrMult);
            if (b.Close < mid - band) return ShortWithStop(b, _atr.Value, atrMult);
            return Hold;
        }
        if (Pos > 0 && b.Close < mid) return Signal.Flat;
        if (Pos < 0 && b.Close > mid) return Signal.Flat;
        return Hold;
    }
}

/// <summary>
/// Momentum: time-series momentum. Long when the <c>lookback</c>-bar return exceeds +threshold, short when it is
/// below −threshold; in between keep the current position. A reversal flips directly. Fixed ATR(14) × atrMult stop.
/// </summary>
public sealed class TimeSeriesMomentum(int lookback, double threshold, double atrMult) : EntryExitStrategy
{
    private readonly Atr _atr = new(14);

    public override string Name => $"ts-momentum({lookback},{threshold},{atrMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        _atr.Update(b);
        if (ctx.Index < lookback || !_atr.IsReady) return Hold;

        double roc = b.Close / ctx.Back(lookback).Close - 1;
        int want = roc > threshold ? 1 : roc < -threshold ? -1 : Pos;
        if (want == Pos) return Hold;
        return want > 0 ? LongWithStop(b, _atr.Value, atrMult) : ShortWithStop(b, _atr.Value, atrMult);
    }
}

/// <summary>
/// Regime filter + mean reversion hybrid: buy RSI(rsiLen) dips below <c>level</c> while the close is above
/// EMA(trendLen); sell RSI spikes above 100 − level while below it. Exit when RSI crosses back through 55 / 45
/// or after 48 bars. Fixed ATR(14) × atrMult stop.
/// </summary>
public sealed class TrendPullbackRsi(int trendLen, int rsiLen, double level, double atrMult) : EntryExitStrategy
{
    private const int MaxHold = 48;
    private readonly Ema _trend = new(trendLen);
    private readonly Rsi _rsi = new(rsiLen);
    private readonly Atr _atr = new(14);

    public override string Name => $"trend-pullback-rsi({trendLen},{rsiLen},{level},{atrMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        _trend.Update(b.Close);
        _rsi.Update(b.Close);
        _atr.Update(b);
        if (!_trend.IsReady || !_rsi.IsReady || !_atr.IsReady) return Hold;

        if (Pos == 0)
        {
            if (b.Close > _trend.Value && _rsi.Value < level) return LongWithStop(b, _atr.Value, atrMult);
            if (b.Close < _trend.Value && _rsi.Value > 100 - level) return ShortWithStop(b, _atr.Value, atrMult);
            return Hold;
        }
        if (BarsHeld >= MaxHold) return Signal.Flat;
        if (Pos > 0 && _rsi.Value > 55) return Signal.Flat;
        if (Pos < 0 && _rsi.Value < 45) return Signal.Flat;
        return Hold;
    }
}
