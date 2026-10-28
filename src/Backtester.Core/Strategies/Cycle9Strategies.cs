using Backtester.Core.Indicators;

namespace Backtester.Core.Strategies;

// Research sprint 2 (hunt cycles 9 and 10): Market Cipher components and other popular TradingView indicators,
// implemented from their public descriptions and the open-source "VuManChu Cipher B + Divergences" re-implementation
// (artifacts/hunt/cycle_9_ideas/research.md). Market Cipher itself is closed source; nothing of it is copied here.
// All signals are computed on UTC-aligned N-hour bars built from the base bars (1h by default) and taken on the
// period's last close.

/// <summary>N-hour bars plus ATR(14) over them.</summary>
public abstract class PeriodStrategy(int tfHours) : EntryExitStrategy
{
    protected readonly PeriodBars Period = new(tfHours);
    protected int TfHours { get; } = tfHours;
    protected readonly Atr PeriodAtr = new(14);

    /// <summary>Updates the period bars and their ATR; returns true on the base bar that completes a period.</summary>
    protected bool UpdatePeriod(Bar b)
    {
        Period.Update(b);
        if (Period.Closed) PeriodAtr.Update(Period.Last);
        return Period.Closed;
    }

    protected static double Hlc3(Bar b) => (b.High + b.Low + b.Close) / 3;

    /// <summary>Enters on <paramref name="buy"/>/<paramref name="sell"/> (flipping if needed), else exits on the opposite WaveTrend cross.</summary>
    protected Signal EnterOrExitOnCross(bool buy, bool sell, WaveTrend wt, double close, double dist)
    {
        if (Pos <= 0 && buy) return Signal.Long(close - dist);
        if (Pos >= 0 && sell) return Signal.Short(close + dist);
        if (Pos > 0 && wt.CrossDown) return Signal.Flat;
        if (Pos < 0 && wt.CrossUp) return Signal.Flat;
        return Hold;
    }
}

/// <summary>
/// M1, Cipher B "green/red circles": WaveTrend(9, 12, 3) on hlc3. Long when WT1 crosses above WT2 with WT2 ≤ −level
/// (oversold), short when it crosses below with WT2 ≥ level; exit on the opposite cross ("small dot"), flipping if that
/// cross is itself a signal. Stop <c>atrMult</c> × ATR(14) of the period bars.
/// </summary>
public sealed class CipherBDots(int tfHours, double level, double atrMult) : PeriodStrategy(tfHours)
{
    private readonly WaveTrend _wt = new(9, 12, 3);

    public override string Name => $"cipher-b-dots({TfHours},{level},{atrMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        if (!UpdatePeriod(ctx.Bar)) return Hold;
        var p = Period.Last;
        _wt.Update(Hlc3(p));
        if (!_wt.IsReady || !PeriodAtr.IsReady) return Hold;
        return EnterOrExitOnCross(_wt.CrossUp && _wt.Wt2 <= -level, _wt.CrossDown && _wt.Wt2 >= level, _wt, p.Close, atrMult * PeriodAtr.Value);
    }
}

/// <summary>
/// M2, Cipher B "Sommi flag" (trend continuation): long when the money flow (RSI+MFI area, 60, ×150, −2.5) is above 0,
/// WT2 is below 0 and WT1 crosses up, and the higher-timeframe "VWAP" (WT1 − WT2 on the last completed
/// <c>htfHours</c> bar) is above 0; short: the mirror. Exit on the opposite WT cross. Stop <c>atrMult</c> × ATR.
/// </summary>
public sealed class CipherBSommiFlag(int tfHours, int htfHours, double atrMult) : PeriodStrategy(tfHours)
{
    private readonly WaveTrend _wt = new(9, 12, 3), _htfWt = new(9, 12, 3);
    private readonly MoneyFlowArea _mf = new(60, 150, 2.5);
    private readonly PeriodBars _htf = new(htfHours);
    private double _htfVwap = double.NaN;

    public override string Name => $"cipher-b-sommi-flag({TfHours},{htfHours},{atrMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        _htf.Update(b);
        if (_htf.Closed)
        {
            _htfWt.Update(Hlc3(_htf.Last));
            if (_htfWt.IsReady) _htfVwap = _htfWt.Wt1 - _htfWt.Wt2;   // only completed higher-timeframe bars
        }
        if (!UpdatePeriod(b)) return Hold;
        var p = Period.Last;
        _wt.Update(Hlc3(p));
        _mf.Update(p);
        if (!_wt.IsReady || !_mf.IsReady || !PeriodAtr.IsReady || double.IsNaN(_htfVwap)) return Hold;
        bool bull = _mf.Value > 0 && _wt.Wt2 < 0 && _wt.CrossUp && _htfVwap > 0;
        bool bear = _mf.Value < 0 && _wt.Wt2 > 0 && _wt.CrossDown && _htfVwap < 0;
        return EnterOrExitOnCross(bull, bear, _wt, p.Close, atrMult * PeriodAtr.Value);
    }
}

/// <summary>
/// M3, Cipher B WaveTrend divergences: a WT2 bottom fractal (5 bars, low in the middle) at or below −65 × <c>limitScale</c>
/// whose price low is below the previous such fractal's low while WT2 is higher = bullish divergence (confirmed two
/// periods after the low); top fractals at or above 45 × limitScale with a higher high and lower WT2 = bearish. Enter
/// on the confirmation; exit on the opposite WT cross. Stop <c>atrMult</c> × ATR.
/// </summary>
public sealed class CipherBDivergence(int tfHours, double limitScale, double atrMult) : PeriodStrategy(tfHours)
{
    private readonly WaveTrend _wt = new(9, 12, 3);
    private readonly double[] _w = new double[5], _hi = new double[5], _lo = new double[5];
    private int _n;
    private double _prevBotWt = double.NaN, _prevBotLow = double.NaN, _prevTopWt = double.NaN, _prevTopHigh = double.NaN;

    public override string Name => $"cipher-b-divergence({TfHours},{limitScale},{atrMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        if (!UpdatePeriod(ctx.Bar)) return Hold;
        var p = Period.Last;
        _wt.Update(Hlc3(p));
        if (double.IsNaN(_wt.Wt2)) return Hold;
        for (int k = 4; k > 0; k--) { _w[k] = _w[k - 1]; _hi[k] = _hi[k - 1]; _lo[k] = _lo[k - 1]; }
        _w[0] = _wt.Wt2;
        _hi[0] = p.High;
        _lo[0] = p.Low;
        _n++;

        bool bull = false, bear = false;
        if (_n >= 5)
        {
            double w2 = _w[2];
            if (_w[4] > w2 && _w[3] > w2 && w2 < _w[1] && w2 < _w[0] && w2 <= -65 * limitScale)
            {
                bull = !double.IsNaN(_prevBotWt) && _lo[2] < _prevBotLow && w2 > _prevBotWt;
                _prevBotWt = w2;
                _prevBotLow = _lo[2];
            }
            if (_w[4] < w2 && _w[3] < w2 && w2 > _w[1] && w2 > _w[0] && w2 >= 45 * limitScale)
            {
                bear = !double.IsNaN(_prevTopWt) && _hi[2] > _prevTopHigh && w2 < _prevTopWt;
                _prevTopWt = w2;
                _prevTopHigh = _hi[2];
            }
        }
        if (!_wt.IsReady || !PeriodAtr.IsReady) return Hold;
        return EnterOrExitOnCross(bull, bear, _wt, p.Close, atrMult * PeriodAtr.Value);
    }
}

/// <summary>
/// M4, Cipher A EMA ribbon: EMA(11) crossing above EMA(33) ("green dot") goes long, crossing below goes short.
/// With <c>diamondExit</c>, a long also exits on a "blood diamond" (bearish WT cross on the same period as EMA(5)
/// crossing under EMA(11)); short: the mirror. Stop <c>atrMult</c> × ATR.
/// </summary>
public sealed class CipherARibbon(int tfHours, double atrMult, bool diamondExit) : PeriodStrategy(tfHours)
{
    private readonly Ema _e5 = new(5), _e11 = new(11), _e33 = new(33);
    private readonly WaveTrend _wt = new(9, 12, 3);
    private double _p5 = double.NaN, _p11 = double.NaN, _p33 = double.NaN;

    public override string Name => $"cipher-a-ribbon({TfHours},{atrMult},{(diamondExit ? 1 : 0)})";

    protected override Signal Decide(StrategyContext ctx)
    {
        if (!UpdatePeriod(ctx.Bar)) return Hold;
        var p = Period.Last;
        double e5 = _e5.Update(p.Close), e11 = _e11.Update(p.Close), e33 = _e33.Update(p.Close);
        _wt.Update(Hlc3(p));
        var s = Hold;
        if (_e33.IsReady && _wt.IsReady && PeriodAtr.IsReady && !double.IsNaN(_p33))
        {
            double dist = atrMult * PeriodAtr.Value;
            bool up = _p11 <= _p33 && e11 > e33, down = _p11 >= _p33 && e11 < e33;
            bool bloodDiamond = _wt.CrossDown && _p5 >= _p11 && e5 < e11;
            bool blueDiamond = _wt.CrossUp && _p5 <= _p11 && e5 > e11;
            if (Pos <= 0 && up) s = Signal.Long(p.Close - dist);
            else if (Pos >= 0 && down) s = Signal.Short(p.Close + dist);
            else if (diamondExit && Pos > 0 && bloodDiamond) s = Signal.Flat;
            else if (diamondExit && Pos < 0 && blueDiamond) s = Signal.Flat;
        }
        _p5 = e5;
        _p11 = e11;
        _p33 = e33;
        return s;
    }
}

/// <summary>
/// M5, Cipher B confluence: the M1 circle (WT cross at ±<c>level</c>) only when the money flow is turning the same way
/// (rising for a buy) and Stochastic RSI (log close, 14/14/3/3) has %K above %D (below for a sell). Exit on the opposite
/// WT cross. Stop <c>atrMult</c> × ATR.
/// </summary>
public sealed class CipherBConfluence(int tfHours, double level, double atrMult) : PeriodStrategy(tfHours)
{
    private readonly WaveTrend _wt = new(9, 12, 3);
    private readonly MoneyFlowArea _mf = new(60, 150, 2.5);
    private readonly StochRsi _stoch = new(14, 14, 3, 3, useLog: true);

    public override string Name => $"cipher-b-confluence({TfHours},{level},{atrMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        if (!UpdatePeriod(ctx.Bar)) return Hold;
        var p = Period.Last;
        _wt.Update(Hlc3(p));
        _mf.Update(p);
        _stoch.Update(p.Close);
        if (!_wt.IsReady || !_mf.IsReady || double.IsNaN(_mf.Prev) || !_stoch.IsReady || !PeriodAtr.IsReady) return Hold;
        bool buy = _wt.CrossUp && _wt.Wt2 <= -level && _mf.Value > _mf.Prev && _stoch.K > _stoch.D;
        bool sell = _wt.CrossDown && _wt.Wt2 >= level && _mf.Value < _mf.Prev && _stoch.K < _stoch.D;
        return EnterOrExitOnCross(buy, sell, _wt, p.Close, atrMult * PeriodAtr.Value);
    }
}

/// <summary>
/// P1, SuperTrend(atrLen, mult) on period bars: hold the SuperTrend direction, flipping when it flips, with the
/// SuperTrend line as the (trailing) stop. A stop-out is re-entered on the next period close if the trend still holds.
/// </summary>
public sealed class SuperTrendFlip(int tfHours, int atrLen, double mult) : PeriodStrategy(tfHours)
{
    private readonly SuperTrend _st = new(atrLen, mult);

    public override string Name => $"supertrend({TfHours},{atrLen},{mult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        if (!UpdatePeriod(ctx.Bar)) return Hold;
        _st.Update(Period.Last);
        if (!_st.IsReady) return Hold;
        double line = _st.Line;
        if (_st.Direction != Pos) return _st.Direction > 0 ? Signal.Long(line) : Signal.Short(line);
        if (ctx.StopPrice is double stop && (Pos > 0 ? line <= stop : line >= stop)) return Hold;
        return new Signal(Pos, line);
    }
}

/// <summary>
/// P2, Squeeze Momentum (LazyBear / TTM squeeze), length 20: squeeze on when the Bollinger Bands (2 population sd) sit
/// inside the Keltner Channel (SMA ± <c>kcMult</c> × SMA of true range). When a squeeze releases, enter in the direction
/// of the momentum value linreg(close − avg(avg(HH20, LL20), SMA20), 20); exit when momentum starts fading (value
/// turns back toward zero). Stop <c>atrMult</c> × ATR.
/// </summary>
public sealed class SqueezeMomentum(int tfHours, double kcMult, double atrMult) : PeriodStrategy(tfHours)
{
    private const int Len = 20;
    private readonly RollingStats _close = new(Len), _range = new(Len);
    private readonly RollingExtreme _hh = new(Len, max: true), _ll = new(Len, max: false);
    private readonly Queue<double> _delta = new();
    private double _prevClose = double.NaN, _prevVal = double.NaN;
    private bool _prevSqzOn;

    public override string Name => $"squeeze-momentum({TfHours},{kcMult},{atrMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        if (!UpdatePeriod(ctx.Bar)) return Hold;
        var p = Period.Last;
        double tr = double.IsNaN(_prevClose) ? p.High - p.Low
            : Math.Max(p.High - p.Low, Math.Max(Math.Abs(p.High - _prevClose), Math.Abs(p.Low - _prevClose)));
        _prevClose = p.Close;
        _close.Add(p.Close);
        _range.Add(tr);
        _hh.Add(p.High);
        _ll.Add(p.Low);
        if (!_close.IsReady || !_hh.IsReady) return Hold;

        double sma = _close.Mean, sd = _close.StdDev * Math.Sqrt((Len - 1.0) / Len);   // Pine's stdev is the population one
        bool sqzOn = sma - 2 * sd > sma - kcMult * _range.Mean && sma + 2 * sd < sma + kcMult * _range.Mean;
        _delta.Enqueue(p.Close - ((_hh.Value + _ll.Value) / 2 + sma) / 2);
        if (_delta.Count > Len) _delta.Dequeue();
        double val = _delta.Count == Len ? LinRegLast(_delta) : double.NaN;

        var s = Hold;
        if (!double.IsNaN(val) && !double.IsNaN(_prevVal) && PeriodAtr.IsReady)
        {
            double dist = atrMult * PeriodAtr.Value;
            bool fired = _prevSqzOn && !sqzOn;
            if (Pos <= 0 && fired && val > 0) s = Signal.Long(p.Close - dist);
            else if (Pos >= 0 && fired && val < 0) s = Signal.Short(p.Close + dist);
            else if (Pos > 0 && val < _prevVal) s = Signal.Flat;
            else if (Pos < 0 && val > _prevVal) s = Signal.Flat;
        }
        _prevSqzOn = sqzOn;
        _prevVal = val;
        return s;
    }

    /// <summary>ta.linreg(src, n, 0): the least-squares line through the last n values, evaluated at the newest one.</summary>
    public static double LinRegLast(IEnumerable<double> values)
    {
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        int n = 0;
        foreach (var y in values)
        {
            sx += n; sy += y; sxx += (double)n * n; sxy += n * y;
            n++;
        }
        double slope = (n * sxy - sx * sy) / (n * sxx - sx * sx);
        double intercept = (sy - slope * sx) / n;
        return intercept + slope * (n - 1);
    }
}

/// <summary>
/// P3, Ichimoku cloud breakout: tenkan/kijun/span B lengths 9/26/52 (periodSet 1) or 20/60/120 (periodSet 2, a common
/// crypto setting), displacement 26 or 30. Long when the close is above the cloud plotted at this bar (spans computed
/// displacement − 1 periods ago), tenkan is above kijun, and the close is above the close displacement − 1 periods ago
/// (chikou), on the period where that first becomes true; exit on a close below kijun. Short: the mirror. Stop
/// <c>atrMult</c> × ATR.
/// </summary>
public sealed class IchimokuCloud : PeriodStrategy
{
    private readonly int _periodSet, _disp;
    private readonly double _atrMult;
    private readonly RollingExtreme _convHi, _convLo, _baseHi, _baseLo, _spanHi, _spanLo;
    private readonly List<double> _spanA = [], _spanB = [], _closes = [];
    private bool _prevLong, _prevShort;

    public IchimokuCloud(int tfHours, int periodSet, double atrMult) : base(tfHours)
    {
        _periodSet = periodSet;
        _atrMult = atrMult;
        (int conv, int bas, int span, _disp) = periodSet == 1 ? (9, 26, 52, 26) : (20, 60, 120, 30);
        _convHi = new(conv, max: true); _convLo = new(conv, max: false);
        _baseHi = new(bas, max: true); _baseLo = new(bas, max: false);
        _spanHi = new(span, max: true); _spanLo = new(span, max: false);
    }

    public override string Name => $"ichimoku-cloud({TfHours},{_periodSet},{_atrMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        if (!UpdatePeriod(ctx.Bar)) return Hold;
        var p = Period.Last;
        _convHi.Add(p.High); _convLo.Add(p.Low);
        _baseHi.Add(p.High); _baseLo.Add(p.Low);
        _spanHi.Add(p.High); _spanLo.Add(p.Low);
        _closes.Add(p.Close);
        if (!_spanHi.IsReady) return Hold;
        double tenkan = (_convHi.Value + _convLo.Value) / 2, kijun = (_baseHi.Value + _baseLo.Value) / 2;
        _spanA.Add((tenkan + kijun) / 2);
        _spanB.Add((_spanHi.Value + _spanLo.Value) / 2);
        int lag = _disp - 1;
        if (_spanA.Count <= lag || !PeriodAtr.IsReady) return Hold;

        double cloudTop = Math.Max(_spanA[^(lag + 1)], _spanB[^(lag + 1)]), cloudBottom = Math.Min(_spanA[^(lag + 1)], _spanB[^(lag + 1)]);
        double chikouRef = _closes[^(lag + 1)];
        bool longOk = p.Close > cloudTop && tenkan > kijun && p.Close > chikouRef;
        bool shortOk = p.Close < cloudBottom && tenkan < kijun && p.Close < chikouRef;
        double dist = _atrMult * PeriodAtr.Value;
        var s = Hold;
        if (Pos <= 0 && longOk && !_prevLong) s = Signal.Long(p.Close - dist);
        else if (Pos >= 0 && shortOk && !_prevShort) s = Signal.Short(p.Close + dist);
        else if (Pos > 0 && p.Close < kijun) s = Signal.Flat;
        else if (Pos < 0 && p.Close > kijun) s = Signal.Flat;
        _prevLong = longOk;
        _prevShort = shortOk;
        return s;
    }
}
