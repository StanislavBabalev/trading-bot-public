using Backtester.Core.Indicators;

namespace Backtester.Core.Strategies;

// Hunt cycle 4 hypotheses (C1–C5), as specified in artifacts/hunt/cycle_4_ideas/shortlist.md.

/// <summary>
/// C1, range structure: fade a failed break of the previous UTC day's range. Long when the close is back at or above
/// the prior-day low after 1..<c>breakBars</c> consecutive closes below it, the excursion reached at least
/// <c>excursionAtr</c> × ATR(14) beyond the level, and the prior-day midpoint is at least 1 ATR away (short: the mirror
/// at the prior-day high). Exit when the close reaches the midpoint taken at entry, or after <c>maxHold</c> bars.
/// Stop beyond the excursion extreme by <c>stopBufferAtr</c> × ATR.
/// </summary>
public sealed class FailedBreakoutFade(int breakBars, double excursionAtr, double stopBufferAtr, int maxHold) : EntryExitStrategy
{
    private const double MinRewardAtr = 1.0;
    private readonly Atr _atr = new(14);
    private readonly UtcDayLevels _day = new();
    private double _target;

    public override string Name => $"failed-breakout-fade({breakBars},{excursionAtr},{stopBufferAtr},{maxHold})";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        _atr.Update(b);
        _day.Update(b);

        if (Pos != 0)
        {
            if (BarsHeld >= maxHold) return Signal.Flat;
            if (Pos > 0 && b.Close >= _target) return Signal.Flat;
            if (Pos < 0 && b.Close <= _target) return Signal.Flat;
            return Hold;
        }
        if (!_atr.IsReady || !_day.HasPrev) return Hold;

        double atr = _atr.Value, pdh = _day.PrevHigh, pdl = _day.PrevLow, mid = (pdh + pdl) / 2;
        if (b.Close >= pdl && mid - b.Close >= MinRewardAtr * atr
            && FailedBreak(ctx, below: true, pdl, out double extreme) && extreme <= pdl - excursionAtr * atr)
        {
            _target = mid;
            return Signal.Long(Math.Min(extreme, b.Low) - stopBufferAtr * atr);
        }
        if (b.Close <= pdh && b.Close - mid >= MinRewardAtr * atr
            && FailedBreak(ctx, below: false, pdh, out extreme) && extreme >= pdh + excursionAtr * atr)
        {
            _target = mid;
            return Signal.Short(Math.Max(extreme, b.High) + stopBufferAtr * atr);
        }
        return Hold;
    }

    /// <summary>
    /// True if the 1..breakBars bars just before the current one all closed beyond <paramref name="level"/> and the
    /// bar before them did not; <paramref name="extreme"/> is their lowest low (or highest high).
    /// </summary>
    private bool FailedBreak(StrategyContext ctx, bool below, double level, out double extreme)
    {
        extreme = below ? double.MaxValue : double.MinValue;
        int j = 0;
        for (int i = 1; i <= breakBars + 1 && i <= ctx.Index; i++)
        {
            var p = ctx.Back(i);
            if (below ? p.Close >= level : p.Close <= level) break;
            if (++j > breakBars) return false;
            extreme = below ? Math.Min(extreme, p.Low) : Math.Max(extreme, p.High);
        }
        return j >= 1;
    }
}

/// <summary>
/// C2, volume exhaustion: fade a climax bar. Long when volume ≥ <c>volMult</c> × the mean of the previous 48 bars,
/// the range ≥ <c>rangeAtr</c> × the previous bar's ATR(14), the lower wick is at least half the range and the low is
/// the lowest of the last 24 bars (short: the mirror). Exit after <c>maxHold</c> bars. Stop 0.25 ATR beyond the wick.
/// </summary>
public sealed class VolumeClimaxReversal(double volMult, double rangeAtr, int maxHold) : EntryExitStrategy
{
    private const int VolLen = 48, ExtremeLen = 24;
    private const double WickFraction = 0.5, StopBufferAtr = 0.25;
    private readonly Atr _atr = new(14);
    private readonly RollingStats _vol = new(VolLen);
    private readonly RollingExtreme _lo = new(ExtremeLen, max: false), _hi = new(ExtremeLen, max: true);

    public override string Name => $"volume-climax-reversal({volMult},{rangeAtr},{maxHold})";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        bool ready = _atr.IsReady && _vol.IsReady;
        double prevAtr = _atr.Value, meanVol = _vol.Mean;   // both exclude the current bar
        _atr.Update(b);
        _vol.Add(b.Volume);
        _lo.Add(b.Low);
        _hi.Add(b.High);
        if (!ready || !_lo.IsReady) return Hold;

        if (Pos != 0) return BarsHeld >= maxHold ? Signal.Flat : Hold;

        double range = b.High - b.Low;
        if (b.Volume < volMult * meanVol || range < rangeAtr * prevAtr || range <= 0) return Hold;
        bool longOk = Math.Min(b.Open, b.Close) - b.Low >= WickFraction * range && b.Low <= _lo.Value;
        bool shortOk = b.High - Math.Max(b.Open, b.Close) >= WickFraction * range && b.High >= _hi.Value;
        if (longOk && !shortOk) return Signal.Long(b.Low - StopBufferAtr * _atr.Value);
        if (shortOk && !longOk) return Signal.Short(b.High + StopBufferAtr * _atr.Value);
        return Hold;
    }
}

/// <summary>
/// C3, time of day: intraday session momentum. On the close of the bar that ends at 12:00 UTC (the 11:00 bar on hourly
/// data), R = close − the day's 00:00 open and S = ATR(14) × √(bars from 00:00 to 12:00), √12 on hourly data. Long if
/// R ≥ <c>moveMult</c> × S, short if R ≤ −moveMult × S; fill at the 12:00 open. Exit after <c>holdBars</c> bars. Stop at
/// the signal close ∓ <c>atrMult</c> × ATR.
/// </summary>
public sealed class SessionMomentum(double moveMult, int holdBars, double atrMult) : EntryExitStrategy
{
    private const long DayMs = 86_400_000L, DecisionCloseMs = 12 * 3_600_000L;
    private readonly Atr _atr = new(14);
    private readonly UtcDayLevels _day = new();

    public override string Name => $"session-momentum({moveMult},{holdBars},{atrMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        _atr.Update(b);
        _day.Update(b);
        if (!_atr.IsReady) return Hold;

        if (Pos != 0) return BarsHeld >= holdBars ? Signal.Flat : Hold;
        long closeOfDay = b.OpenTimeMs + ctx.IntervalMs - TimeUtil.DayIndex(b.OpenTimeMs) * DayMs;
        if (closeOfDay != DecisionCloseMs || double.IsNaN(_day.DayOpen)) return Hold;

        double move = b.Close - _day.DayOpen, scale = _atr.Value * Math.Sqrt(DecisionCloseMs / ctx.IntervalMs);
        if (move >= moveMult * scale) return LongWithStop(b, _atr.Value, atrMult);
        if (move <= -moveMult * scale) return ShortWithStop(b, _atr.Value, atrMult);
        return Hold;
    }
}

/// <summary>
/// C4, volatility state: impulse-bar continuation. Long when the true range ≥ <c>rangeAtr</c> × the previous bar's
/// ATR(14), the bar closes up and its body is at least <c>bodyFrac</c> of the range (short: the mirror).
/// Exit after <c>holdBars</c> bars. Stop at the signal close ∓ <c>atrMult</c> × ATR.
/// </summary>
public sealed class ImpulseBarContinuation(double rangeAtr, double bodyFrac, int holdBars, double atrMult) : EntryExitStrategy
{
    private readonly Atr _atr = new(14);

    public override string Name => $"impulse-bar-continuation({rangeAtr},{bodyFrac},{holdBars},{atrMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        bool ready = _atr.IsReady && ctx.Index >= 1;
        double prevAtr = _atr.Value;
        _atr.Update(b);
        if (!ready) return Hold;

        if (Pos != 0) return BarsHeld >= holdBars ? Signal.Flat : Hold;

        double prevClose = ctx.Back(1).Close, range = b.High - b.Low;
        double tr = Math.Max(range, Math.Max(Math.Abs(b.High - prevClose), Math.Abs(b.Low - prevClose)));
        if (tr < rangeAtr * prevAtr) return Hold;
        if (b.Close > b.Open && b.Close - b.Open >= bodyFrac * range) return LongWithStop(b, _atr.Value, atrMult);
        if (b.Close < b.Open && b.Open - b.Close >= bodyFrac * range) return ShortWithStop(b, _atr.Value, atrMult);
        return Hold;
    }
}

/// <summary>
/// C5, regime switch. Quiet when ATR(14) is below its median over the last <c>regimeLen</c> bars, else expanding.
/// HH/LL are the high/low of the previous <c>channelLen</c> bars. Quiet: fade a green close in the bottom
/// <c>edgeFrac</c> of the channel (short: a red close in the top edge), exit at the channel midpoint or after 24 bars,
/// stop 0.5 ATR beyond the channel. Expanding: break out on a close beyond HH/LL, exit on the opposite channel of
/// half the length, stop <c>atrMult</c> × ATR trailed on every close. An open trade keeps its own rules if the regime flips.
/// </summary>
public sealed class VolRegimeSwitch(int regimeLen, int channelLen, double edgeFrac, double atrMult) : EntryExitStrategy
{
    private const int FadeMaxHold = 24;
    private const double FadeStopBufferAtr = 0.5;
    // ATR(14) and its rolling median come from the engine's shared feature cache: the median over up to 1440 bars
    // was most of this strategy's run time, and it is identical for every run on the same bars.
    private double[]? _atr, _atrMedian;
    private readonly RollingExtreme _hi = new(channelLen, max: true), _lo = new(channelLen, max: false);
    private readonly RollingExtreme _exitHi = new(Math.Max(1, channelLen / 2), max: true), _exitLo = new(Math.Max(1, channelLen / 2), max: false);
    private bool _trendTrade;

    public override string Name => $"vol-regime-switch({regimeLen},{channelLen},{edgeFrac},{atrMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        _atr ??= ctx.Features.Atr(14);
        _atrMedian ??= ctx.Features.AtrMedian(14, regimeLen);
        double median = _atrMedian[ctx.Index];
        var s = Hold;
        if (!double.IsNaN(median) && _hi.IsReady)
        {
            // channels cover the bars *before* the current one
            double atr = _atr[ctx.Index], hh = _hi.Value, ll = _lo.Value;
            if (Pos == 0) s = Enter(b, atr, hh, ll, quiet: atr < median);
            else if (_trendTrade) s = ManageTrend(ctx, b, atr);
            else s = ManageFade(b, hh, ll);
        }
        _hi.Add(b.High); _lo.Add(b.Low); _exitHi.Add(b.High); _exitLo.Add(b.Low);
        return s;
    }

    private Signal Enter(Bar b, double atr, double hh, double ll, bool quiet)
    {
        if (quiet)
        {
            double range = hh - ll;
            if (range <= 0) return Hold;
            double pos = (b.Close - ll) / range;
            _trendTrade = false;
            if (pos >= 0 && pos <= edgeFrac && b.Close > b.Open) return Signal.Long(Math.Min(ll, b.Low) - FadeStopBufferAtr * atr);
            if (pos <= 1 && pos >= 1 - edgeFrac && b.Close < b.Open) return Signal.Short(Math.Max(hh, b.High) + FadeStopBufferAtr * atr);
            return Hold;
        }
        _trendTrade = true;
        if (b.Close > hh) return LongWithStop(b, atr, atrMult);
        if (b.Close < ll) return ShortWithStop(b, atr, atrMult);
        return Hold;
    }

    private Signal ManageFade(Bar b, double hh, double ll)
    {
        double mid = (hh + ll) / 2;
        if (BarsHeld >= FadeMaxHold) return Signal.Flat;
        if (Pos > 0 && b.Close >= mid) return Signal.Flat;
        if (Pos < 0 && b.Close <= mid) return Signal.Flat;
        return Hold;
    }

    private Signal ManageTrend(StrategyContext ctx, Bar b, double atr)
    {
        if (Pos > 0 && b.Close < _exitLo.Value) return Signal.Flat;
        if (Pos < 0 && b.Close > _exitHi.Value) return Signal.Flat;
        double trail = b.Close - Pos * atrMult * atr;
        if (ctx.StopPrice is double stop && (Pos > 0 ? trail <= stop : trail >= stop)) return Hold;
        return new Signal(Pos, trail);
    }
}
