namespace Backtester.Core.Indicators;

// Incremental indicators, updated once per closed bar. They match TradingView's ta.ema / ta.rma / ta.atr
// seeding so results can be compared with Pine scripts.

/// <summary>ta.ema: seeded with the first value, alpha = 2 / (length + 1). Ready after <c>length</c> updates.</summary>
public sealed class Ema(int length)
{
    private readonly double _alpha = 2.0 / (length + 1);
    private int _count;

    public double Value { get; private set; } = double.NaN;
    public bool IsReady => _count >= length;

    public double Update(double x)
    {
        Value = _count == 0 ? x : _alpha * x + (1 - _alpha) * Value;
        _count++;
        return Value;
    }
}

/// <summary>ta.rma (Wilder): seeded with the SMA of the first <c>length</c> values, alpha = 1 / length.</summary>
public sealed class Rma(int length)
{
    private int _count;
    private double _seedSum;

    public double Value { get; private set; } = double.NaN;
    public bool IsReady => _count >= length;

    public double Update(double x)
    {
        _count++;
        if (_count < length) { _seedSum += x; return Value; }
        if (_count == length) { Value = (_seedSum + x) / length; return Value; }
        Value = (x + (length - 1) * Value) / length;
        return Value;
    }
}

/// <summary>ta.atr: Wilder RMA of true range.</summary>
public sealed class Atr(int length)
{
    private readonly Rma _rma = new(length);
    private double _prevClose = double.NaN;

    public double Value => _rma.Value;
    public bool IsReady => _rma.IsReady;

    public double Update(Bar b)
    {
        double tr = double.IsNaN(_prevClose)
            ? b.High - b.Low
            : Math.Max(b.High - b.Low, Math.Max(Math.Abs(b.High - _prevClose), Math.Abs(b.Low - _prevClose)));
        _prevClose = b.Close;
        return _rma.Update(tr);
    }
}

/// <summary>ta.rsi: Wilder RSI from RMA-smoothed gains and losses.</summary>
public sealed class Rsi(int length)
{
    private readonly Rma _up = new(length), _down = new(length);
    private double _prev = double.NaN;

    public double Value { get; private set; } = double.NaN;
    public bool IsReady => _up.IsReady;

    public double Update(double close)
    {
        if (double.IsNaN(_prev)) { _prev = close; return Value; }
        double change = close - _prev;
        _prev = close;
        _up.Update(Math.Max(change, 0));
        _down.Update(Math.Max(-change, 0));
        if (!_up.IsReady) return Value;
        Value = _down.Value == 0 ? 100 : _up.Value == 0 ? 0 : 100 - 100 / (1 + _up.Value / _down.Value);
        return Value;
    }
}

/// <summary>Highest (or lowest) of the last <c>length</c> values, O(1) amortized via a monotonic deque.</summary>
public sealed class RollingExtreme
{
    private readonly int _length;
    private readonly bool _max;
    private readonly long[] _idx;
    private readonly double[] _val;
    private int _head, _count;
    private long _n;

    public RollingExtreme(int length, bool max)
    {
        if (length < 1) throw new ArgumentException("Length must be at least 1.");
        _length = length;
        _max = max;
        _idx = new long[length + 1];
        _val = new double[length + 1];
    }

    public bool IsReady => _n >= _length;
    public double Value => _count == 0 ? double.NaN : _val[_head];

    public void Add(double x)
    {
        int cap = _idx.Length;
        while (_count > 0)
        {
            int back = (_head + _count - 1) % cap;
            if (_max ? _val[back] <= x : _val[back] >= x) _count--;
            else break;
        }
        int pos = (_head + _count) % cap;
        _idx[pos] = _n;
        _val[pos] = x;
        _count++;
        _n++;
        while (_idx[_head] <= _n - 1 - _length)
        {
            _head = (_head + 1) % cap;
            _count--;
        }
    }
}

/// <summary>Mean and sample standard deviation over the last <c>length</c> values.</summary>
public sealed class RollingStats(int length)
{
    private readonly double[] _buf = new double[length];
    private int _count, _next;
    private double _sum, _sumSq;

    public bool IsReady => _count >= length;
    public double Mean => _count == 0 ? double.NaN : _sum / Math.Min(_count, length);

    public double StdDev
    {
        get
        {
            int n = Math.Min(_count, length);
            if (n < 2) return double.NaN;
            double var = (_sumSq - _sum * _sum / n) / (n - 1);
            return Math.Sqrt(Math.Max(var, 0));
        }
    }

    public void Add(double x)
    {
        if (_count >= length)
        {
            double old = _buf[_next];
            _sum -= old;
            _sumSq -= old * old;
        }
        _buf[_next] = x;
        _sum += x;
        _sumSq += x * x;
        _next = (_next + 1) % length;
        _count++;
    }
}

/// <summary>Median of the last <c>length</c> values (mean of the two middle values when <c>length</c> is even).</summary>
public sealed class RollingMedian
{
    private readonly int _length;
    private readonly double[] _buf;
    private readonly List<double> _sorted;
    private int _count, _next;

    public RollingMedian(int length)
    {
        if (length < 1) throw new ArgumentException("Length must be at least 1.");
        _length = length;
        _buf = new double[length];
        _sorted = new List<double>(length);
    }

    public bool IsReady => _count >= _length;

    public double Value
    {
        get
        {
            int n = _sorted.Count;
            if (n == 0) return double.NaN;
            return n % 2 == 1 ? _sorted[n / 2] : (_sorted[n / 2 - 1] + _sorted[n / 2]) / 2;
        }
    }

    public void Add(double x)
    {
        if (_count >= _length) _sorted.RemoveAt(_sorted.BinarySearch(_buf[_next]));
        _buf[_next] = x;
        int at = _sorted.BinarySearch(x);
        _sorted.Insert(at < 0 ? ~at : at, x);
        _next = (_next + 1) % _length;
        _count++;
    }
}

/// <summary>
/// The bar length of a series: the smallest step between consecutive bar opens seen so far (0 until the second bar).
/// The audited data has no gaps, so it is exact from the second bar on.
/// </summary>
internal sealed class BarStep
{
    private long _prev = long.MinValue;

    public long Ms { get; private set; }

    public void Update(long openTimeMs)
    {
        if (_prev != long.MinValue && openTimeMs > _prev && (Ms == 0 || openTimeMs - _prev < Ms)) Ms = openTimeMs - _prev;
        _prev = openTimeMs;
    }
}

/// <summary>
/// UTC daily bars built from intraday bars of any interval that divides a day (see <see cref="BarStep"/>). A day counts
/// as complete when it started with its 00:00 bar and its last bar (23:00 on hourly data) has just closed with every bar
/// present. <see cref="DayClosed"/> is true only on that last bar, so a decision taken then fills at the next day's 00:00
/// open. Update with every bar, before reading.
/// </summary>
public sealed class DailyBars
{
    private const long DayMs = 86_400_000L;
    private readonly BarStep _step = new();
    private long _day = long.MinValue;
    private bool _fromMidnight;
    private int _count;
    private double _open, _high, _low, _volume;

    /// <summary>Completed days, oldest first. <see cref="Bar.OpenTimeMs"/> is the day's 00:00.</summary>
    public List<Bar> Days { get; } = [];
    public bool DayClosed { get; private set; }

    public void Update(Bar b)
    {
        _step.Update(b.OpenTimeMs);
        long day = TimeUtil.DayIndex(b.OpenTimeMs);
        if (day != _day)
        {
            _day = day;
            _fromMidnight = b.OpenTimeMs == day * DayMs;
            _count = 0;
            _open = b.Open;
            _high = b.High;
            _low = b.Low;
            _volume = 0;
        }
        else
        {
            _high = Math.Max(_high, b.High);
            _low = Math.Min(_low, b.Low);
        }
        _volume += b.Volume;
        _count++;
        long step = _step.Ms;
        DayClosed = _fromMidnight && step > 0 && _count == DayMs / step && b.OpenTimeMs + step == (day + 1) * DayMs;
        if (DayClosed) Days.Add(new Bar(day * DayMs, _open, _high, _low, b.Close, _volume));
    }

    /// <summary>The completed day <paramref name="daysAgo"/> days back (0 = the latest).</summary>
    public Bar Back(int daysAgo) => Days[Days.Count - 1 - daysAgo];
}

/// <summary>
/// UTC-aligned N-hour bars (N divides 24) built from shorter bars whose length divides N hours (see
/// <see cref="BarStep"/>; longer or non-dividing bars throw). A period counts as complete when it started on a multiple of
/// N hours and its last bar has just closed with every bar present. <see cref="Closed"/> is true only on that bar, so a
/// decision taken then fills at the next period's first open.
/// </summary>
public sealed class PeriodBars
{
    private readonly BarStep _step = new();
    private readonly int _hours;
    private long _period = long.MinValue;
    private bool _aligned;
    private int _count;
    private double _open, _high, _low, _volume;

    public PeriodBars(int hours)
    {
        if (hours < 1 || 24 % hours != 0) throw new ArgumentException("Period hours must divide 24.");
        _hours = hours;
    }

    public bool Closed { get; private set; }
    /// <summary>The latest completed period (valid once <see cref="Count"/> &gt; 0).</summary>
    public Bar Last { get; private set; }
    public int Count { get; private set; }

    public void Update(Bar b)
    {
        _step.Update(b.OpenTimeMs);
        long periodMs = _hours * 3_600_000L;
        long period = Math.DivRem(b.OpenTimeMs, periodMs, out long rem) - (rem < 0 ? 1 : 0);
        if (period != _period)
        {
            _period = period;
            _aligned = b.OpenTimeMs == period * periodMs;
            _count = 0;
            _open = b.Open;
            _high = b.High;
            _low = b.Low;
            _volume = 0;
        }
        else
        {
            _high = Math.Max(_high, b.High);
            _low = Math.Min(_low, b.Low);
        }
        _volume += b.Volume;
        _count++;
        long step = _step.Ms;
        if (step > 0 && (step > periodMs || periodMs % step != 0))
            throw new ArgumentException($"{_hours}-hour periods cannot be built from {step / 60_000}-minute bars.");
        Closed = _aligned && step > 0 && _count == periodMs / step && b.OpenTimeMs + step == (period + 1) * periodMs;
        if (Closed)
        {
            Last = new Bar(period * periodMs, _open, _high, _low, b.Close, _volume);
            Count++;
        }
    }
}

/// <summary>
/// LazyBear's WaveTrend on a price series (hlc3): ESA = EMA(ap, channel), D = EMA(|ap − ESA|, channel),
/// CI = (ap − ESA) / (0.015 D), WT1 = EMA(CI, average), WT2 = SMA(WT1, ma). Keeps the previous values for crosses.
/// </summary>
public sealed class WaveTrend(int channelLen, int averageLen, int maLen)
{
    private readonly Ema _esa = new(channelLen), _d = new(channelLen), _wt1 = new(averageLen);
    private readonly RollingStats _wt2 = new(maLen);
    private int _n;

    public double Wt1 { get; private set; } = double.NaN;
    public double Wt2 { get; private set; } = double.NaN;
    public double PrevWt1 { get; private set; } = double.NaN;
    public double PrevWt2 { get; private set; } = double.NaN;
    public bool IsReady => _n >= channelLen + averageLen + maLen && !double.IsNaN(PrevWt2);
    public bool CrossUp => IsReady && Wt1 > Wt2 && PrevWt1 <= PrevWt2;
    public bool CrossDown => IsReady && Wt1 < Wt2 && PrevWt1 >= PrevWt2;

    public void Update(double ap)
    {
        double esa = _esa.Update(ap);
        double d = _d.Update(Math.Abs(ap - esa));
        double ci = d > 0 ? (ap - esa) / (0.015 * d) : 0;
        _wt1.Update(ci);
        _wt2.Add(_wt1.Value);
        PrevWt1 = Wt1;
        PrevWt2 = Wt2;
        Wt1 = _wt1.Value;
        Wt2 = _wt2.IsReady ? _wt2.Mean : double.NaN;
        _n++;
    }
}

/// <summary>The "RSI+MFI area" money flow: SMA((close − open) / (high − low) × multiplier, period) − posY.</summary>
public sealed class MoneyFlowArea(int period, double multiplier, double posY)
{
    private readonly RollingStats _avg = new(period);

    public bool IsReady => _avg.IsReady;
    public double Value { get; private set; } = double.NaN;
    public double Prev { get; private set; } = double.NaN;

    public void Update(Bar b)
    {
        double range = b.High - b.Low;
        _avg.Add(range > 0 ? (b.Close - b.Open) / range * multiplier : 0);
        Prev = Value;
        Value = _avg.IsReady ? _avg.Mean - posY : double.NaN;
    }
}

/// <summary>Stochastic RSI: RSI of (log) closes, %K = SMA(stoch(RSI, stochLen), kSmooth), %D = SMA(%K, dSmooth).</summary>
public sealed class StochRsi(int stochLen, int rsiLen, int kSmooth, int dSmooth, bool useLog)
{
    private readonly Rsi _rsi = new(rsiLen);
    private readonly RollingExtreme _hi = new(stochLen, max: true), _lo = new(stochLen, max: false);
    private readonly RollingStats _k = new(kSmooth), _d = new(dSmooth);

    public bool IsReady => _d.IsReady;
    public double K => _k.Mean;
    public double D => _d.Mean;

    public void Update(double close)
    {
        _rsi.Update(useLog ? Math.Log(close) : close);
        if (!_rsi.IsReady) return;
        double r = _rsi.Value;
        _hi.Add(r);
        _lo.Add(r);
        if (!_hi.IsReady) return;
        double span = _hi.Value - _lo.Value;
        _k.Add(span > 0 ? (r - _lo.Value) / span * 100 : 50);
        if (_k.IsReady) _d.Add(_k.Mean);
    }
}

/// <summary>
/// SuperTrend: bands hl2 ∓ mult × ATR(atrLen) that only ratchet in the trend's favour; the trend flips when the close
/// crosses the active band. <see cref="Line"/> is the active band (the trailing stop of the current direction).
/// </summary>
public sealed class SuperTrend(int atrLen, double mult)
{
    private readonly Atr _atr = new(atrLen);
    private double _upper = double.NaN, _lower = double.NaN, _prevClose = double.NaN;

    public int Direction { get; private set; }
    public double Line => Direction > 0 ? _lower : Direction < 0 ? _upper : double.NaN;
    public bool IsReady => _atr.IsReady && Direction != 0;

    public void Update(Bar b)
    {
        _atr.Update(b);
        if (!_atr.IsReady) { _prevClose = b.Close; return; }
        double hl2 = (b.High + b.Low) / 2, atr = _atr.Value;
        double up = hl2 + mult * atr, dn = hl2 - mult * atr;
        double upper = double.IsNaN(_upper) || up < _upper || _prevClose > _upper ? up : _upper;
        double lower = double.IsNaN(_lower) || dn > _lower || _prevClose < _lower ? dn : _lower;
        // as in the common Pine version, the flip compares the close with the previous bar's bands
        if (Direction == 0) Direction = b.Close >= hl2 ? 1 : -1;
        else if (Direction < 0 && b.Close > _upper) Direction = 1;
        else if (Direction > 0 && b.Close < _lower) Direction = -1;
        _upper = upper;
        _lower = lower;
        _prevClose = b.Close;
    }
}

/// <summary>
/// Kaufman's efficiency ratio over <c>length</c> closes: |net change| / sum of |close-to-close changes|.
/// 1 = a straight line, near 0 = chop. Ready after <c>length + 1</c> closes.
/// </summary>
public sealed class EfficiencyRatio
{
    private readonly int _length;
    private readonly double[] _closes;
    private int _n;
    private double _path;

    public EfficiencyRatio(int length)
    {
        if (length < 1) throw new ArgumentException("Length must be at least 1.");
        _length = length;
        _closes = new double[length + 1];
    }

    public bool IsReady => _n > _length;
    public double Value { get; private set; } = double.NaN;

    public void Add(double close)
    {
        int cap = _closes.Length;
        if (_n > 0) _path += Math.Abs(close - _closes[(_n - 1) % cap]);
        if (_n > _length)
        {
            // drop the move between the two oldest closes still in the window
            double oldest = _closes[_n % cap], next = _closes[(_n + 1) % cap];
            _path -= Math.Abs(next - oldest);
        }
        _closes[_n % cap] = close;
        _n++;
        if (!IsReady) return;
        double net = Math.Abs(close - _closes[(_n - 1 - _length) % cap]);
        Value = _path > 0 ? net / _path : 0;
    }
}

/// <summary>
/// UTC-day levels: the high and low of the previous complete UTC day and the open of the current day.
/// A day counts as complete when its first bar opened at 00:00 and the next day followed directly
/// (the audited data has no gaps inside a day). Update with every bar, before reading.
/// </summary>
public sealed class UtcDayLevels
{
    private long _day = long.MinValue;
    private bool _startedAtMidnight;
    private double _high, _low;

    /// <summary>True when <see cref="PrevHigh"/> and <see cref="PrevLow"/> describe the previous complete day.</summary>
    public bool HasPrev { get; private set; }
    public double PrevHigh { get; private set; } = double.NaN;
    public double PrevLow { get; private set; } = double.NaN;
    /// <summary>Open of the current day's 00:00 bar; NaN if the day did not start at midnight.</summary>
    public double DayOpen { get; private set; } = double.NaN;

    public void Update(Bar b)
    {
        long day = TimeUtil.DayIndex(b.OpenTimeMs);
        if (day != _day)
        {
            HasPrev = _day != long.MinValue && day == _day + 1 && _startedAtMidnight;
            PrevHigh = HasPrev ? _high : double.NaN;
            PrevLow = HasPrev ? _low : double.NaN;
            _day = day;
            _startedAtMidnight = TimeUtil.UtcHour(b.OpenTimeMs) == 0 && b.OpenTimeMs % 3_600_000L == 0;
            DayOpen = _startedAtMidnight ? b.Open : double.NaN;
            _high = b.High;
            _low = b.Low;
            return;
        }
        _high = Math.Max(_high, b.High);
        _low = Math.Min(_low, b.Low);
    }
}
