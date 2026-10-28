using Backtester.Core.Indicators;

namespace Backtester.Core.Strategies;

// Research sprint 1, second batch (hunt cycle 6). See artifacts/hunt/cycle_5_ideas/research.md.

/// <summary>
/// S6, daily extremes (Padyšák &amp; Vojtko 2022): on a day's close at or above the highest close of the previous
/// <c>lookbackDays</c> days (trend) or at or below the lowest (reversion), go long; long only. Each new signal day
/// renews the holding period; exit after <c>holdDays</c> day closes without a signal. Stop <c>stopMult</c> × daily ATR.
/// </summary>
public sealed class MaxMinDaily(int lookbackDays, int holdDays, double stopMult) : DailyStrategy
{
    private int _daysLeft;

    public override string Name => $"max-min-daily({lookbackDays},{holdDays},{stopMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        if (!UpdateDaily(ctx.Bar) || Daily.Days.Count <= lookbackDays || !DailyAtr.IsReady) return Hold;
        double c = Daily.Back(0).Close, max = double.MinValue, min = double.MaxValue;
        for (int k = 1; k <= lookbackDays; k++)
        {
            double x = Daily.Back(k).Close;
            max = Math.Max(max, x);
            min = Math.Min(min, x);
        }
        if (c >= max || c <= min)
        {
            _daysLeft = holdDays;
            return Pos > 0 ? Hold : Signal.Long(c - stopMult * DailyAtr.Value);
        }
        if (Pos > 0 && --_daysLeft <= 0) return Signal.Flat;
        return Hold;
    }
}

/// <summary>
/// S7, multi-timeframe trend (price-to-moving-average, Detzel et al. 2021): the daily regime is the sign of the last
/// daily close minus the SMA of the last <c>maDays</c> daily closes. In an up regime, go long when the hourly close
/// crosses back above EMA(<c>entryLen</c>) (the end of a pullback); short: the mirror in a down regime. Exit when the
/// daily regime no longer matches. Stop trails the best close by <c>trailMult</c> × daily ATR.
/// </summary>
public sealed class MtfTrendPullback(int maDays, int entryLen, double trailMult) : DailyStrategy
{
    private readonly Ema _ema = new(entryLen);
    private int _regime;
    private double _best, _prevClose = double.NaN, _prevEma = double.NaN;

    public override string Name => $"mtf-trend-pullback({maDays},{entryLen},{trailMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        if (UpdateDaily(b) && Daily.Days.Count >= maDays)
        {
            double sma = 0;
            for (int k = 0; k < maDays; k++) sma += Daily.Back(k).Close;
            _regime = Math.Sign(Daily.Back(0).Close - sma / maDays);
        }
        _ema.Update(b.Close);
        var s = Hold;
        if (_ema.IsReady && DailyAtr.IsReady && !double.IsNaN(_prevEma))
        {
            double dist = trailMult * DailyAtr.Value;
            if (Pos == 0)
            {
                if (_regime > 0 && _prevClose <= _prevEma && b.Close > _ema.Value) { _best = b.Close; s = Signal.Long(b.Close - dist); }
                else if (_regime < 0 && _prevClose >= _prevEma && b.Close < _ema.Value) { _best = b.Close; s = Signal.Short(b.Close + dist); }
            }
            else if (Pos != _regime) s = Signal.Flat;
            else
            {
                _best = Pos > 0 ? Math.Max(_best, b.Close) : Math.Min(_best, b.Close);
                s = Trail(ctx, _best, dist);
            }
        }
        _prevClose = b.Close;
        _prevEma = _ema.Value;
        return s;
    }
}

/// <summary>
/// S8, efficiency regime switch (Kaufman 1995): ER = |net change| / path over <c>erLen</c> hourly closes. Trending
/// (ER ≥ <c>erThreshold</c>): long on a close above the 48-bar high (short below the low), exit on the opposite
/// 24-bar channel. Directionless (ER below it): fade a close outside SMA(48) ± 2 sd, exit at the SMA or after 48 bars.
/// Stop <c>atrMult</c> × ATR(14) from the signal close. An open trade keeps its own rules if the regime flips.
/// Unlike vol-regime-switch, the regime is set by directionality, not volatility.
/// </summary>
public sealed class EfficiencyRegimeSwitch(int erLen, double erThreshold, double atrMult) : EntryExitStrategy
{
    private const int ChannelLen = 48, ExitLen = 24, BandLen = 48, FadeMaxHold = 48;
    private const double BandMult = 2.0;
    private readonly EfficiencyRatio _er = new(erLen);
    private readonly Atr _atr = new(14);
    private readonly RollingStats _band = new(BandLen);
    private readonly RollingExtreme _hi = new(ChannelLen, max: true), _lo = new(ChannelLen, max: false);
    private readonly RollingExtreme _exitHi = new(ExitLen, max: true), _exitLo = new(ExitLen, max: false);
    private bool _trendTrade;

    public override string Name => $"efficiency-regime-switch({erLen},{erThreshold},{atrMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        _er.Add(b.Close);
        _atr.Update(b);
        _band.Add(b.Close);
        var s = Hold;
        if (_er.IsReady && _atr.IsReady && _hi.IsReady && _band.IsReady)
        {
            if (Pos == 0)
            {
                _trendTrade = _er.Value >= erThreshold;
                if (_trendTrade)
                {
                    // channels cover the bars *before* the current one
                    if (b.Close > _hi.Value) s = LongWithStop(b, _atr.Value, atrMult);
                    else if (b.Close < _lo.Value) s = ShortWithStop(b, _atr.Value, atrMult);
                }
                else
                {
                    double mid = _band.Mean, dev = BandMult * _band.StdDev;
                    if (b.Close < mid - dev) s = LongWithStop(b, _atr.Value, atrMult);
                    else if (b.Close > mid + dev) s = ShortWithStop(b, _atr.Value, atrMult);
                }
            }
            else if (_trendTrade)
            {
                if (Pos > 0 && b.Close < _exitLo.Value) s = Signal.Flat;
                else if (Pos < 0 && b.Close > _exitHi.Value) s = Signal.Flat;
            }
            else if (BarsHeld >= FadeMaxHold || (Pos > 0 && b.Close >= _band.Mean) || (Pos < 0 && b.Close <= _band.Mean)) s = Signal.Flat;
        }
        _hi.Add(b.High); _lo.Add(b.Low); _exitHi.Add(b.High); _exitLo.Add(b.Low);
        return s;
    }
}

/// <summary>
/// S9, price-volume interaction (Bianchi, Babiak &amp; Dickerson 2022; Llorente et al. 2002): a day is extreme when its
/// |return| is in the top <c>extremeFrac</c> of the previous 90 days. If its volume is below the median of the previous
/// 30 days (a move without participation: liquidity-driven), fade it; if above (participation), follow it. Hold
/// <c>holdDays</c> days (a new extreme day renews or flips). Stop <c>stopMult</c> × daily ATR.
/// </summary>
public sealed class VolumeConditionedReversal(double extremeFrac, int holdDays, double stopMult) : DailyStrategy
{
    private const int ReturnWindow = 90, VolumeWindow = 30;
    private int _daysLeft;

    public override string Name => $"volume-conditioned-reversal({extremeFrac},{holdDays},{stopMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        if (!UpdateDaily(ctx.Bar) || Daily.Days.Count <= ReturnWindow + 1 || !DailyAtr.IsReady) return Hold;
        double AbsReturn(int daysAgo) => Math.Abs(Daily.Back(daysAgo).Close / Daily.Back(daysAgo + 1).Close - 1);
        var today = Daily.Back(0);
        double ret = today.Close / Daily.Back(1).Close - 1, size = Math.Abs(ret);
        int smaller = 0;
        for (int k = 1; k <= ReturnWindow; k++) if (AbsReturn(k) < size) smaller++;
        bool extreme = smaller >= (1 - extremeFrac) * ReturnWindow;

        if (extreme && ret != 0)
        {
            var volumes = Enumerable.Range(1, VolumeWindow).Select(k => Daily.Back(k).Volume).OrderBy(v => v).ToList();
            double median = (volumes[VolumeWindow / 2 - 1] + volumes[VolumeWindow / 2]) / 2;
            int dir = today.Volume < median ? -Math.Sign(ret) : Math.Sign(ret);
            _daysLeft = holdDays;
            if (dir == Pos) return Hold;
            double dist = stopMult * DailyAtr.Value;
            return dir > 0 ? Signal.Long(today.Close - dist) : Signal.Short(today.Close + dist);
        }
        if (Pos != 0 && --_daysLeft <= 0) return Signal.Flat;
        return Hold;
    }
}

/// <summary>
/// Cycle 7 (ablation of the funding signal): funding alone, no price timing. On each daily close, short when the
/// 3-day funding rank (see funding-crowding-fade) is ≥ 0.5 + <c>band</c> (longs crowded), long when ≤ 0.5 − band,
/// otherwise flat; re-evaluated daily, so a stopped-out position is re-entered the next day if still signalled.
/// Stop <c>stopMult</c> × daily ATR.
/// </summary>
public sealed class FundingCarryTilt(double band, double stopMult) : DailyStrategy
{
    private double[]? _rank;

    public override string Name => $"funding-carry-tilt({band},{stopMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        _rank ??= ctx.Features.FundingRank(FundingCrowdingFade.AvgN, FundingCrowdingFade.Window);
        if (!UpdateDaily(ctx.Bar) || !DailyAtr.IsReady || double.IsNaN(_rank[ctx.Index])) return Hold;
        double r = _rank[ctx.Index], c = ctx.Bar.Close, dist = stopMult * DailyAtr.Value;
        int target = r >= 0.5 + band ? -1 : r <= 0.5 - band ? 1 : 0;
        if (target == Pos) return Hold;
        if (target == 0) return Signal.Flat;
        return target > 0 ? Signal.Long(c - dist) : Signal.Short(c + dist);
    }
}

/// <summary>
/// S10, crowding-filtered breakout (trend + BIS "Crypto carry"): Donchian breakout of the previous channelDays days of
/// bars, but a long is taken only if the 3-day funding rank (see funding-crowding-fade) is ≤ <c>maxRank</c> and a short
/// only if it is ≥ 1 − maxRank, i.e. never on the crowded side (maxRank 1 = no filter). Exit on the opposite channel of
/// half the length. Stop trails the best close by <c>trailMult</c> × daily ATR.
/// </summary>
public sealed class CrowdingFilteredBreakout : DailyStrategy
{
    private readonly int _channelDays;
    private readonly double _maxRank, _trailMult;
    private RollingExtreme _hi = null!, _lo = null!, _exitHi = null!, _exitLo = null!;   // sized on the first bar
    private double[]? _rank;
    private double _best;

    public CrowdingFilteredBreakout(int channelDays, double maxRank, double trailMult)
    {
        _channelDays = channelDays;
        _maxRank = maxRank;
        _trailMult = trailMult;
    }

    public override string Name => $"crowding-filtered-breakout({_channelDays},{_maxRank},{_trailMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        if (_hi is null)
        {
            int channel = (int)(_channelDays * 86_400_000L / ctx.IntervalMs);
            _hi = new RollingExtreme(channel, max: true);
            _lo = new RollingExtreme(channel, max: false);
            _exitHi = new RollingExtreme(channel / 2, max: true);
            _exitLo = new RollingExtreme(channel / 2, max: false);
        }
        UpdateDaily(b);
        _rank ??= ctx.Features.FundingRank(FundingCrowdingFade.AvgN, FundingCrowdingFade.Window);
        var s = Hold;
        if (_hi.IsReady && DailyAtr.IsReady)
        {
            double r = _rank[ctx.Index], dist = _trailMult * DailyAtr.Value;
            if (Pos == 0)
            {
                // channels cover the bars *before* the current one; an unknown rank (no funding history yet) does not filter
                if (b.Close > _hi.Value && !(r > _maxRank)) { _best = b.Close; s = Signal.Long(b.Close - dist); }
                else if (b.Close < _lo.Value && !(r < 1 - _maxRank)) { _best = b.Close; s = Signal.Short(b.Close + dist); }
            }
            else if (Pos > 0 && b.Close < _exitLo.Value) s = Signal.Flat;
            else if (Pos < 0 && b.Close > _exitHi.Value) s = Signal.Flat;
            else
            {
                _best = Pos > 0 ? Math.Max(_best, b.Close) : Math.Min(_best, b.Close);
                s = Trail(ctx, _best, dist);
            }
        }
        _hi.Add(b.High); _lo.Add(b.Low); _exitHi.Add(b.High); _exitLo.Add(b.Low);
        return s;
    }
}
