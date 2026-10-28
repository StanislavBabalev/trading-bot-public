using Backtester.Core.Indicators;

namespace Backtester.Core.Strategies;

// Research sprint 1 (hunt cycles 5 and 6), as specified in artifacts/hunt/cycle_5_ideas/research.md.
// Daily structure comes from DailyBars (decisions on the 23:00 bar's close fill at the next 00:00 open);
// "daily ATR" is ta.atr(14) over completed UTC days. Funding comes from ctx.Features (rates settled by each bar's open).

/// <summary>Shared daily state: completed UTC days and ATR(14) over them.</summary>
public abstract class DailyStrategy : EntryExitStrategy
{
    protected readonly DailyBars Daily = new();
    protected readonly Atr DailyAtr = new(14);

    /// <summary>Updates the daily bars and the daily ATR; returns true on the bar that completes a day.</summary>
    protected bool UpdateDaily(Bar b)
    {
        Daily.Update(b);
        if (Daily.DayClosed) DailyAtr.Update(Daily.Back(0));
        return Daily.DayClosed;
    }

    /// <summary>Moves a trailing stop to <paramref name="best"/> ∓ <paramref name="distance"/>; never loosens it.</summary>
    protected Signal Trail(StrategyContext ctx, double best, double distance)
    {
        double trail = best - Pos * distance;
        if (ctx.StopPrice is double stop && (Pos > 0 ? trail <= stop : trail >= stop)) return Hold;
        return new Signal(Pos, trail);
    }
}

/// <summary>
/// S1, trend ensemble (Zarattini, Pagani &amp; Barbon 2025): four Donchian models with lookbacks of baseDays × 1, 2, 4
/// and 8 days. A model turns long when the close breaks the high of its previous bars and short when it breaks the
/// low, and keeps that state. Enter long on a fresh breakout of the fastest channel when at least <c>minAgree</c> of
/// the four models are long (short: mirror); exit when no more models agree than disagree. Stop trails the best close
/// since entry by <c>trailMult</c> × daily ATR.
/// </summary>
public sealed class DonchianEnsembleTrend : DailyStrategy
{
    private RollingExtreme[] _hi = [], _lo = [];
    private readonly int[] _state = new int[4];
    private readonly int _baseDays, _minAgree;
    private readonly double _trailMult;
    private double _best;

    public DonchianEnsembleTrend(int baseDays, int minAgree, double trailMult)
    {
        _baseDays = baseDays;
        _minAgree = minAgree;
        _trailMult = trailMult;
    }

    public override string Name => $"donchian-ensemble-trend({_baseDays},{_minAgree},{_trailMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        if (_hi.Length == 0)
        {
            long barsPerDay = 86_400_000L / ctx.IntervalMs;
            int[] lens = new[] { 1, 2, 4, 8 }.Select(k => (int)(_baseDays * k * barsPerDay)).ToArray();
            _hi = lens.Select(n => new RollingExtreme(n, max: true)).ToArray();
            _lo = lens.Select(n => new RollingExtreme(n, max: false)).ToArray();
        }
        UpdateDaily(b);
        bool freshUp = false, freshDown = false;
        for (int k = 0; k < 4; k++)
        {
            if (!_hi[k].IsReady) continue;
            // channels cover the bars *before* the current one
            if (b.Close > _hi[k].Value) { _state[k] = 1; freshUp |= k == 0; }
            else if (b.Close < _lo[k].Value) { _state[k] = -1; freshDown |= k == 0; }
        }
        for (int k = 0; k < 4; k++) { _hi[k].Add(b.High); _lo[k].Add(b.Low); }
        if (!_hi[3].IsReady || !DailyAtr.IsReady) return Hold;

        int up = _state.Count(s => s > 0), down = _state.Count(s => s < 0);
        double dist = _trailMult * DailyAtr.Value;
        if (Pos == 0)
        {
            if (freshUp && up >= _minAgree) { _best = b.Close; return Signal.Long(b.Close - dist); }
            if (freshDown && down >= _minAgree) { _best = b.Close; return Signal.Short(b.Close + dist); }
            return Hold;
        }
        if (Pos > 0 ? up <= down : down <= up) return Signal.Flat;
        _best = Pos > 0 ? Math.Max(_best, b.Close) : Math.Min(_best, b.Close);
        return Trail(ctx, _best, dist);
    }
}

/// <summary>
/// S2, funding crowding (BIS WP 1087 "Crypto carry"; VanEck/K33 on negative funding): rank = where the mean of the
/// last 9 settled funding rates (3 days) sits among the previous 270 such means (90 days). Crowded longs
/// (rank ≥ 1 − <c>quantile</c>): short when the close crosses below EMA(24). Crowded shorts (rank ≤ quantile): long
/// when the close crosses above EMA(24). Exit after <c>holdBars</c>. Stop <c>stopMult</c> × daily ATR.
/// Cycle 7 reuses it as funding-tilt (quantile up to 0.5) and, with quantile 1 (funding ignored), as ema-cross-control.
/// </summary>
public sealed class FundingCrowdingFade(double quantile, int holdBars, double stopMult, string label = "funding-crowding-fade") : DailyStrategy
{
    public const int AvgN = 9, Window = 270;
    private readonly Ema _ema = new(24);
    private double[]? _rank;
    private double _prevClose = double.NaN, _prevEma = double.NaN;

    public override string Name => $"{label}({quantile},{holdBars},{stopMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        UpdateDaily(b);
        _rank ??= ctx.Features.FundingRank(AvgN, Window);
        _ema.Update(b.Close);
        var s = Hold;
        if (Pos != 0)
        {
            if (BarsHeld >= holdBars) s = Signal.Flat;
        }
        else if (_ema.IsReady && DailyAtr.IsReady && !double.IsNaN(_prevEma) && !double.IsNaN(_rank[ctx.Index]))
        {
            double r = _rank[ctx.Index], dist = stopMult * DailyAtr.Value;
            if (r >= 1 - quantile && _prevClose >= _prevEma && b.Close < _ema.Value) s = Signal.Short(b.Close + dist);
            else if (r <= quantile && _prevClose <= _prevEma && b.Close > _ema.Value) s = Signal.Long(b.Close - dist);
        }
        _prevClose = b.Close;
        _prevEma = _ema.Value;
        return s;
    }
}

/// <summary>
/// S3, intraday "noise area" momentum (Zarattini, Aziz &amp; Barbon 2024), with the UTC day as the session. For each
/// time of day h (the bar's slot: the hour on hourly data), σ_h = mean of |close / day open − 1| at h over the previous
/// <c>lookbackDays</c> days; bands = day open × (1 ± <c>bandMult</c> × σ_h). From hour <c>firstHour</c> up to the day's
/// second-to-last bar (22:00 on hourly data), long when the close moves above the upper band (short: below the lower
/// band), at most once per UTC day (the paper's one directional bet per session; re-entering after every exit traded
/// ~600 times a year on synthetic data). Exit when the close falls back below max(upper band, day VWAP) (short: above
/// min(lower band, VWAP)) or on the day's last close (23:00 on hourly data), so every trade is flat by the next day.
/// Stop at the day open.
/// </summary>
public sealed class NoiseAreaMomentum : EntryExitStrategy
{
    private const long DayMs = 86_400_000L, HourMs = 3_600_000L;
    private readonly int _lookbackDays, _firstHour;
    private readonly double _bandMult;
    private Queue<double>[] _moves = [];
    private double[] _sum = [];
    private readonly UtcDayLevels _day = new();
    private double _pv, _vol, _prevClose = double.NaN, _prevUpper = double.NaN, _prevLower = double.NaN;
    private long _prevDay = long.MinValue, _tradedDay = long.MinValue;

    public NoiseAreaMomentum(int lookbackDays, double bandMult, int firstHour)
    {
        _lookbackDays = lookbackDays;
        _bandMult = bandMult;
        _firstHour = firstHour;
    }

    public override string Name => $"noise-area-momentum({_lookbackDays},{_bandMult},{_firstHour})";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        long slots = DayMs / ctx.IntervalMs;
        if (_moves.Length == 0)
        {
            _moves = Enumerable.Range(0, (int)slots).Select(_ => new Queue<double>()).ToArray();
            _sum = new double[slots];
        }
        _day.Update(b);
        long dayIdx = TimeUtil.DayIndex(b.OpenTimeMs);
        int h = (int)((b.OpenTimeMs - dayIdx * DayMs) / ctx.IntervalMs);
        bool Tradable(int slot) => slot * ctx.IntervalMs >= _firstHour * HourMs;
        if (dayIdx != _prevDay) { _pv = 0; _vol = 0; }
        _pv += (b.High + b.Low + b.Close) / 3 * b.Volume;
        _vol += b.Volume;
        double vwap = _vol > 0 ? _pv / _vol : b.Close;
        double open = _day.DayOpen;

        double upper = double.NaN, lower = double.NaN;
        if (!double.IsNaN(open) && _moves[h].Count == _lookbackDays)
        {
            double sigma = _sum[h] / _lookbackDays;   // previous days only: today's move is added below
            upper = open * (1 + _bandMult * sigma);
            lower = open * (1 - _bandMult * sigma);
        }

        var s = Hold;
        if (Pos != 0)
        {
            if (h == slots - 1) s = Signal.Flat;
            else if (Pos > 0 && b.Close < (double.IsNaN(upper) ? vwap : Math.Max(upper, vwap))) s = Signal.Flat;
            else if (Pos < 0 && b.Close > (double.IsNaN(lower) ? vwap : Math.Min(lower, vwap))) s = Signal.Flat;
        }
        else if (!double.IsNaN(upper) && Tradable(h) && h < slots - 1 && _tradedDay != dayIdx)
        {
            // only a fresh move out of the noise area: the previous bar (same day, already tradable) was inside
            bool sameDay = _prevDay == dayIdx && Tradable(h - 1);
            bool wasAbove = sameDay && _prevClose > _prevUpper, wasBelow = sameDay && _prevClose < _prevLower;
            if (b.Close > upper && !wasAbove) s = Signal.Long(open);
            else if (b.Close < lower && !wasBelow) s = Signal.Short(open);
            if (s.Direction != 0) _tradedDay = dayIdx;
        }

        if (!double.IsNaN(open))
        {
            double move = Math.Abs(b.Close / open - 1);
            _moves[h].Enqueue(move);
            _sum[h] += move;
            if (_moves[h].Count > _lookbackDays) _sum[h] -= _moves[h].Dequeue();
        }
        _prevDay = dayIdx;
        _prevClose = b.Close;
        _prevUpper = upper;
        _prevLower = lower;
        return s;
    }
}

/// <summary>
/// S4, volatility contraction (Crabel 1990, NR4/NR7): a day whose range is narrower than each of the previous
/// <c>nrLen</c> − 1 days sets up the next UTC day. On that day, the first close above the setup day's high goes long
/// (below its low: short), once. Stop <c>stopMult</c> × the setup range beyond the broken level. Exit after <c>holdBars</c>.
/// </summary>
public sealed class NrCompressionBreakout(int nrLen, int holdBars, double stopMult) : DailyStrategy
{
    private double _hi = double.NaN, _lo = double.NaN;
    private long _setupDay = long.MinValue;
    private bool _used;

    public override string Name => $"nr-compression-breakout({nrLen},{holdBars},{stopMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        var b = ctx.Bar;
        long day = TimeUtil.DayIndex(b.OpenTimeMs);
        var s = Hold;
        if (Pos != 0)
        {
            if (BarsHeld >= holdBars) s = Signal.Flat;
        }
        else if (day == _setupDay && !_used)
        {
            double range = _hi - _lo;
            if (b.Close > _hi) { s = Signal.Long(_hi - stopMult * range); _used = true; }
            else if (b.Close < _lo) { s = Signal.Short(_lo + stopMult * range); _used = true; }
        }

        if (UpdateDaily(b) && Daily.Days.Count >= nrLen)
        {
            var d = Daily.Back(0);
            double range = d.High - d.Low;
            bool narrowest = range > 0;
            for (int k = 1; k < nrLen && narrowest; k++)
                if (Daily.Back(k).High - Daily.Back(k).Low <= range) narrowest = false;
            if (narrowest)
            {
                _hi = d.High;
                _lo = d.Low;
                _setupDay = day + 1;
                _used = false;
            }
        }
        return s;
    }
}

/// <summary>
/// S5, daily time-series momentum vote (Moskowitz, Ooi &amp; Pedersen 2012; Bitcoin horizons of 1–8 weeks, Hong 2017):
/// on each day's close, vote = the signs of the returns over baseDays, 2 × baseDays and 4 × baseDays days. Hold the
/// majority direction (<c>unanimous</c> = 1: only when all three agree, else flat). Re-evaluated daily, so a stopped-out
/// position is re-entered the next day if the vote still says so. Stop <c>stopMult</c> × daily ATR from the signal close.
/// </summary>
public sealed class TsmomVote(int baseDays, bool unanimous, double stopMult) : DailyStrategy
{
    public override string Name => $"tsmom-vote({baseDays},{(unanimous ? 1 : 0)},{stopMult})";

    protected override Signal Decide(StrategyContext ctx)
    {
        if (!UpdateDaily(ctx.Bar) || Daily.Days.Count <= 4 * baseDays || !DailyAtr.IsReady) return Hold;
        double c = Daily.Back(0).Close;
        int vote = Math.Sign(c - Daily.Back(baseDays).Close) + Math.Sign(c - Daily.Back(2 * baseDays).Close)
                   + Math.Sign(c - Daily.Back(4 * baseDays).Close);
        int target = unanimous ? (vote == 3 ? 1 : vote == -3 ? -1 : 0) : Math.Sign(vote);
        if (target == Pos) return Hold;
        if (target == 0) return Signal.Flat;
        double dist = stopMult * DailyAtr.Value;
        return target > 0 ? Signal.Long(c - dist) : Signal.Short(c + dist);
    }
}
