using Backtester.Core.Indicators;

namespace Backtester.Core.Strategies;

// Hunt cycle 12 (research sprint 3, artifacts/hunt/cycle_12_ideas/research.md): liquidity provision after forced or
// turbulent moves, and psychological price barriers.

/// <summary>
/// C1, liquidation-cascade fade. A bar whose body moves at least <c>dropAtr</c> × ATR(14) against the side that funding
/// shows to be crowded, on at least <c>volMult</c> × the mean volume of the previous 48 bars, is treated as a forced
/// liquidation and faded at the next open: long after a drop while the 3-day funding crowding rank is ≥ 0.5 (longs
/// crowded), short after a spike while it is ≤ 0.5. ATR and mean volume are measured before the bar. Exit after
/// <c>holdBars</c> bars or on a close back beyond the cascade bar's open; stop 1 ATR beyond the cascade bar's extreme.
/// </summary>
public sealed class CascadeFade(double dropAtr, double volMult, int holdBars) : EntryExitStrategy
{
    private const int VolLen = 48;
    private readonly Atr _atr = new(14);
    private readonly RollingStats _vol = new(VolLen);
    private double[]? _rank;
    private double _cascadeOpen = double.NaN;

    public override string Name => $"cascade-fade({dropAtr},{volMult},{holdBars})";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        _rank ??= ctx.Features.FundingRank(FundingCrowdingFade.AvgN, FundingCrowdingFade.Window);
        bool ready = _atr.IsReady && _vol.IsReady;
        double atr = _atr.Value, meanVol = _vol.Mean;   // both exclude the current bar
        _atr.Update(b);
        _vol.Add(b.Volume);
        if (!ready) return Hold;

        if (Pos != 0)
        {
            if (BarsHeld >= holdBars) return Signal.Flat;
            return (Pos > 0 ? b.Close >= _cascadeOpen : b.Close <= _cascadeOpen) ? Signal.Flat : Hold;
        }

        double rank = _rank[ctx.Index], move = b.Close - b.Open;
        if (double.IsNaN(rank) || b.Volume < volMult * meanVol) return Hold;
        if (move <= -dropAtr * atr && rank >= 0.5)
        {
            _cascadeOpen = b.Open;
            return Signal.Long(b.Low - atr);
        }
        if (move >= dropAtr * atr && rank <= 0.5)
        {
            _cascadeOpen = b.Open;
            return Signal.Short(b.High + atr);
        }
        return Hold;
    }
}

/// <summary>
/// C2, turmoil reversal on UTC-aligned 4h bars. Turmoil = the 4h ATR(14) / open of the period, as known before the
/// period, is at least the <c>volQuantile</c> quantile of its previous 540 values (90 days). In turmoil, a 4h body of at
/// least <c>zEntry</c> × that ATR is faded at the next open (long after a drop, short after a spike). Exit on a 4h close
/// back across EMA(20) of 4h closes or after <c>holdPeriods</c> 4h bars; stop 1 4h-ATR beyond the period's extreme.
/// </summary>
public sealed class TurmoilReversal(double zEntry, double volQuantile, int holdPeriods) : PeriodStrategy(4)
{
    private const int VolWindow = 540;
    private readonly Ema _ema = new(20);
    private readonly Queue<double> _history = new();

    public override string Name => $"turmoil-reversal({zEntry},{volQuantile},{holdPeriods})";

    protected override Signal Decide(StrategyContext ctx)
    {
        bool atrReady = PeriodAtr.IsReady;
        double prevAtr = PeriodAtr.Value;   // as of the previous period's close
        if (!UpdatePeriod(ctx.Bar)) return Hold;
        var p = Period.Last;
        _ema.Update(p.Close);
        if (!atrReady) return Hold;

        double ratio = prevAtr / p.Open;
        bool historyReady = _history.Count == VolWindow;
        double below = historyReady ? _history.Count(h => h < ratio) : 0;
        _history.Enqueue(ratio);
        if (_history.Count > VolWindow) _history.Dequeue();

        if (Pos != 0)
        {
            if (BarsHeld >= holdPeriods * TfHours * 3_600_000L / ctx.IntervalMs) return Signal.Flat;
            return (Pos > 0 ? p.Close >= _ema.Value : p.Close <= _ema.Value) ? Signal.Flat : Hold;
        }
        if (!historyReady || !_ema.IsReady || below / VolWindow < volQuantile) return Hold;

        double move = p.Close - p.Open;
        if (move <= -zEntry * prevAtr) return Signal.Long(p.Low - PeriodAtr.Value);
        if (move >= zEntry * prevAtr) return Signal.Short(p.High + PeriodAtr.Value);
        return Hold;
    }
}

/// <summary>
/// C3, round-number barriers. Levels are the multiples of <c>levelStep</c>. Long when a bar opens above the nearest
/// level L below it, trades down to it (low ≤ L) and closes back at or above it, and no bar of the previous
/// <c>freshBars</c> hours traded at or below L (the level's first test). Short on the mirror at the nearest level U above
/// the open (high ≥ U, close ≤ U, no earlier bar at or above U). A bar that reaches both levels is skipped. Exit after
/// <c>holdBars</c> bars; stop 1 ATR(14) beyond the level.
/// </summary>
public sealed class RoundNumberBounce(double levelStep, int freshBars, int holdBars) : EntryExitStrategy
{
    private readonly Atr _atr = new(14);
    private readonly RollingExtreme _lo = new(freshBars, max: false), _hi = new(freshBars, max: true);

    public override string Name => $"round-number-bounce({levelStep},{freshBars},{holdBars})";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        bool ready = _lo.IsReady && _atr.IsReady;
        double prevLow = _lo.Value, prevHigh = _hi.Value;   // the previous freshBars bars, not this one
        _atr.Update(b);
        _lo.Add(b.Low);
        _hi.Add(b.High);
        if (!ready) return Hold;

        if (Pos != 0) return BarsHeld >= holdBars ? Signal.Flat : Hold;

        double below = Math.Ceiling(b.Open / levelStep) * levelStep - levelStep;   // highest level strictly below the open
        double above = Math.Floor(b.Open / levelStep) * levelStep + levelStep;     // lowest level strictly above it
        bool reachesBelow = b.Low <= below, reachesAbove = b.High >= above;
        if (reachesBelow && reachesAbove) return Hold;
        if (reachesBelow && b.Close >= below && prevLow > below) return Signal.Long(below - _atr.Value);
        if (reachesAbove && b.Close <= above && prevHigh < above) return Signal.Short(above + _atr.Value);
        return Hold;
    }
}
