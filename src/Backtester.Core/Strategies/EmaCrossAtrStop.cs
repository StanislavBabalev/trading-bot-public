using Backtester.Core.Indicators;

namespace Backtester.Core.Strategies;

/// <summary>
/// Port of strategies/ema-crossover-atr-stop.pine. Long on fast/slow EMA cross up; on cross down go short
/// (longOnly = 0) or flat (longOnly = 1). Fixed stop at signal close ∓ ATR × atrMult; atrMult = 0 disables it.
/// After a stop-out the engine keeps the strategy flat until it signals a different direction.
/// </summary>
public sealed class EmaCrossAtrStop : IStrategy
{
    private readonly Ema _fast, _slow;
    private readonly Atr _atr;
    private readonly bool _longOnly;
    private readonly double _atrMult;
    private double? _prevDiff;
    private int _target;

    public EmaCrossAtrStop(int fastLen, int slowLen, bool longOnly, int atrLen, double atrMult)
    {
        if (fastLen < 1 || slowLen < 1 || atrLen < 1 || atrMult < 0) throw new ArgumentException("Invalid parameters.");
        _fast = new Ema(fastLen);
        _slow = new Ema(slowLen);
        _atr = new Atr(atrLen);
        _longOnly = longOnly;
        _atrMult = atrMult;
        Name = $"ema-cross-atr({fastLen},{slowLen},{(longOnly ? "long" : "both")},{atrLen},{atrMult})";
    }

    public string Name { get; }

    public Signal OnBarClose(StrategyContext ctx)
    {
        var b = ctx.Bar;
        _fast.Update(b.Close);
        _slow.Update(b.Close);
        _atr.Update(b);
        if (!_fast.IsReady || !_slow.IsReady || !_atr.IsReady) return Signal.Flat;

        double diff = _fast.Value - _slow.Value;
        bool crossUp = _prevDiff is <= 0 && diff > 0;
        bool crossDown = _prevDiff is >= 0 && diff < 0;
        _prevDiff = diff;

        if (crossUp)
        {
            _target = 1;
            return Signal.Long(_atrMult > 0 ? b.Close - _atr.Value * _atrMult : null);
        }
        if (crossDown)
        {
            if (_longOnly) { _target = 0; return Signal.Flat; }
            _target = -1;
            return Signal.Short(_atrMult > 0 ? b.Close + _atr.Value * _atrMult : null);
        }
        return new Signal(_target);
    }
}
