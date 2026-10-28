using Backtester.Core.Indicators;
using Backtester.Core.Strategies;

namespace Backtester.Core.Engine;

/// <summary>
/// Bar-by-bar simulator for a USDT-margined linear perpetual.
///
/// Order of events on each bar inside the trading window:
///   1. funding settles on the position held into this bar's open (price proxy: this bar's open),
///   2. any risk-forced exit, then the previous bar's signal, fill at this bar's open,
///   3. a resting stop fills if this bar trades through it (at the stop, or at the open if it gapped past),
///   4. equity is marked at the close and risk limits are checked,
///   5. the strategy sees the closed bar; its signal waits for the next bar's open.
/// A signal is therefore never filled on the bar that produced it.
///
/// Every fill pays slippage (price moved against the trade) and commission on its notional.
/// The run starts flat: a position the strategy already held during warm-up is not entered until the
/// strategy signals a new direction. After a stop-out the same direction is not re-entered until the
/// strategy changes direction. Positions are sized once at entry and never rebalanced.
/// </summary>
public sealed class BacktestEngine
{
    /// <summary>
    /// Methodology version: bump when fill, cost, funding, sizing or stop semantics change, so stored results are not reused.
    /// (Any code change already invalidates them through the code fingerprint; this marks intent in the record.)
    /// </summary>
    public const string Version = "1";

    // Arrays, not interfaces: the bar loop runs ~30k times per backtest. Both are private copies, never mutated.
    private readonly Bar[] _bars;
    private readonly FundingRate[] _funding;
    private string? _dataFingerprint;

    public BacktestEngine(IReadOnlyList<Bar> bars, IReadOnlyList<FundingRate> funding)
    {
        for (int i = 1; i < bars.Count; i++)
            if (bars[i].OpenTimeMs <= bars[i - 1].OpenTimeMs)
                throw new ArgumentException($"Bars must be strictly increasing in time (row {i}).");
        for (int i = 1; i < funding.Count; i++)
            if (funding[i].TimeMs <= funding[i - 1].TimeMs)
                throw new ArgumentException($"Funding rates must be strictly increasing in time (row {i}).");
        _bars = bars.ToArray();
        _funding = funding.ToArray();
        Features = new FeatureCache(_bars, _funding);
    }

    public IReadOnlyList<Bar> Bars => _bars;
    public IReadOnlyList<FundingRate> Funding => _funding;

    /// <summary>Indicator series computed once for these bars and shared by every run (and thread) on this engine.</summary>
    public FeatureCache Features { get; }

    /// <summary>SHA-256 (first 128 bits, hex) of every bar and funding rate this engine trades on.</summary>
    public string DataFingerprint => _dataFingerprint ??= ComputeFingerprint();

    private string ComputeFingerprint()
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        sha.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_bars.AsSpan()));
        sha.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_funding.AsSpan()));
        return Convert.ToHexString(sha.GetHashAndReset())[..32];
    }

    public BacktestResult Run(IStrategy strategy, BacktestConfig cfg, IReadOnlyDictionary<string, double>? parameters = null)
    {
        if (cfg.ToMs <= cfg.FromMs) throw new ArgumentException("ToMs must be after FromMs.");
        if (cfg.InitialCapital <= 0) throw new ArgumentException("InitialCapital must be positive.");
        if (strategy is ISelfSimulatingStrategy self) return self.Simulate(_bars, _funding, cfg, parameters ?? new Dictionary<string, double>());

        var sim = new Simulator(cfg, capacity: Math.Max(0, LowerBound(cfg.ToMs) - LowerBound(cfg.FromMs)));
        var ctx = new StrategyContext(_bars, Features, cfg.BarIntervalMs);
        // Realized volatility is only read by vol-target sizing; skip the per-bar log/sqrt otherwise.
        var returns = cfg.Sizing.Mode == SizingMode.VolTarget ? new RollingStats(Math.Max(2, cfg.Sizing.VolLookbackBars)) : null;
        double barsPerYear = 365.0 * 24 * 60 / cfg.BarIntervalMinutes;

        Signal? pending = null;
        double pendingVol = double.NaN;
        int fi = 0;
        while (fi < _funding.Length && _funding[fi].TimeMs < cfg.FromMs) fi++;

        bool started = false, exitedAtWindowEnd = false;
        long currentDay = long.MinValue;
        double dayStartEquity = cfg.InitialCapital;
        int lastIndex = -1;

        for (int i = 0; i < _bars.Length; i++)
        {
            var bar = _bars[i];

            if (bar.OpenTimeMs >= cfg.ToMs)
            {
                if (started)
                {
                    sim.CloseAll(bar.Open, bar.OpenTimeMs, i, ExitReason.EndOfTest);
                    exitedAtWindowEnd = true;
                }
                break;
            }

            if (bar.OpenTimeMs >= cfg.FromMs)
            {
                if (!started)
                {
                    started = true;
                    sim.StartMs = bar.OpenTimeMs;
                    // Start flat: ignore a direction the strategy already held during warm-up.
                    if (pending is { Direction: not 0 } warm) sim.LockoutDirection = warm.Direction;
                }

                long day = TimeUtil.DayIndex(bar.OpenTimeMs);
                if (day != currentDay)
                {
                    currentDay = day;
                    dayStartEquity = sim.Equity(bar.Open);
                }

                if (cfg.FundingEnabled)
                    for (; fi < _funding.Length && _funding[fi].TimeMs <= bar.OpenTimeMs; fi++)
                        sim.ApplyFunding(_funding[fi].Rate, bar.Open);

                if (sim.ForceFlat)
                {
                    sim.CloseAll(bar.Open, bar.OpenTimeMs, i, ExitReason.RiskHalt);
                    sim.ForceFlat = false;
                }
                if (pending is { } sig) sim.Execute(sig, bar, i, day, pendingVol);
                pending = null;

                sim.CheckStop(bar, i);

                sim.MarkClose(bar.OpenTimeMs + cfg.BarIntervalMs, bar.Close);
                sim.CheckRisk(sim.Equity(bar.Close), dayStartEquity, day);
                lastIndex = i;
            }

            ctx.Index = i;
            ctx.PositionDirection = sim.Direction;
            ctx.StopPrice = sim.StopPrice;
            ctx.IsWarmup = bar.OpenTimeMs < cfg.FromMs;
            var signal = strategy.OnBarClose(ctx);

            pending = signal;
            if (returns is not null)
            {
                if (i > 0) returns.Add(Math.Log(bar.Close / _bars[i - 1].Close));
                pendingVol = returns.IsReady ? returns.StdDev * Math.Sqrt(barsPerYear) : double.NaN;
            }
        }

        if (!started) throw new InvalidOperationException("No bars inside the trading window.");

        var last = _bars[lastIndex];
        if (!exitedAtWindowEnd) sim.CloseAll(last.Close, last.OpenTimeMs + cfg.BarIntervalMs, lastIndex, ExitReason.EndOfTest);
        sim.RestampFinalEquity();

        return new BacktestResult
        {
            StrategyName = strategy.Name,
            Parameters = parameters ?? new Dictionary<string, double>(),
            Config = cfg,
            Trades = sim.Trades,
            Equity = sim.EquityCurve,
            StartMs = sim.StartMs,
            EndMs = last.OpenTimeMs + cfg.BarIntervalMs,
            FinalEquity = sim.Cash,
            FeesPaid = sim.FeesPaid,
            FundingReceived = sim.FundingReceived,
            SlippageCost = sim.SlippageCost,
            TradedNotional = sim.TradedNotional,
            BarsInWindow = sim.BarsInWindow,
            BarsInMarket = sim.BarsInMarket,
            EntriesSkippedNoStop = sim.SkippedNoStop,
            EntriesSkippedStopBreached = sim.SkippedStopBreached,
            EntriesSkippedNoVolEstimate = sim.SkippedNoVol,
            DailyLossStopsHit = sim.DailyStopsHit,
            Halted = sim.Halted,
            HaltReason = sim.HaltReason,
        };
    }

    /// <summary>Index of the first bar opening at or after <paramref name="timeMs"/>.</summary>
    private int LowerBound(long timeMs)
    {
        int lo = 0, hi = _bars.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_bars[mid].OpenTimeMs < timeMs) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>Account state for one run: cash, one position, the open trade, and risk flags.</summary>
    private sealed class Simulator(BacktestConfig cfg, int capacity)
    {
        private readonly double _commission = cfg.Costs.CommissionRate;
        private readonly double _slippage = cfg.Costs.SlippageRate;

        public double Cash = cfg.InitialCapital;
        public double PositionQty;          // signed: + long, - short
        public double EntryPrice;
        public double? StopPrice;
        public int LockoutDirection;
        public bool ForceFlat;
        public bool Halted;
        public string? HaltReason;
        public long StartMs;

        private long _entryTimeMs;
        private int _entryIndex;
        private double _entryFee;
        private double _tradeFunding;
        private double _peakEquity = cfg.InitialCapital;
        private long? _blockedDay;

        public readonly List<Trade> Trades = [];
        public readonly List<EquityPoint> EquityCurve = new(capacity);
        public double FeesPaid, FundingReceived, SlippageCost, TradedNotional;
        public int BarsInWindow, BarsInMarket, SkippedNoStop, SkippedStopBreached, SkippedNoVol, DailyStopsHit;

        public int Direction => Math.Sign(PositionQty);

        public double Equity(double price) => Cash + PositionQty * (price - EntryPrice);

        public void ApplyFunding(double rate, double price)
        {
            if (PositionQty == 0) return;
            double payment = -PositionQty * price * cfg.Costs.EffectiveFundingRate(rate, Direction);
            Cash += payment;
            FundingReceived += payment;
            _tradeFunding += payment;
        }

        public void Execute(Signal sig, Bar bar, int index, long day, double annualVol)
        {
            int target = sig.Direction;
            if (Halted || _blockedDay == day) target = 0;
            if (LockoutDirection != 0)
            {
                if (target == LockoutDirection) target = Direction;
                else LockoutDirection = 0;
            }

            if (target == Direction)
            {
                if (target != 0 && sig.StopPrice is double newStop) StopPrice = newStop;
                return;
            }
            if (Direction != 0) Close(bar.Open, bar.OpenTimeMs, index, ExitReason.Signal);
            if (target != 0) Open(target, sig.StopPrice, bar, index, annualVol);
        }

        private void Open(int dir, double? stop, Bar bar, int index, double annualVol)
        {
            double fill = bar.Open * (1 + _slippage * dir);
            if (stop is double s && (dir > 0 ? s >= fill : s <= fill)) { SkippedStopBreached++; return; }

            double equity = Cash;
            if (equity <= 0) return;

            double notional;
            switch (cfg.Sizing.Mode)
            {
                case SizingMode.FixedFraction:
                    notional = equity * cfg.Sizing.Fraction;
                    break;
                case SizingMode.RiskPerTrade:
                    if (stop is not double sp) { SkippedNoStop++; return; }
                    notional = equity * cfg.Sizing.RiskPerTrade / Math.Abs(fill - sp) * fill;
                    break;
                case SizingMode.VolTarget:
                    if (double.IsNaN(annualVol) || annualVol <= 0) { SkippedNoVol++; return; }
                    notional = equity * cfg.Sizing.VolTargetAnnual / annualVol;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown sizing mode {cfg.Sizing.Mode}");
            }
            notional = Math.Min(notional, equity * cfg.Risk.MaxLeverage);
            if (notional <= 0) return;

            double qty = notional / fill;
            double fee = notional * _commission;
            Cash -= fee;
            FeesPaid += fee;
            SlippageCost += qty * bar.Open * _slippage;
            TradedNotional += notional;

            PositionQty = dir * qty;
            EntryPrice = fill;
            StopPrice = stop;
            _entryTimeMs = bar.OpenTimeMs;
            _entryIndex = index;
            _entryFee = fee;
            _tradeFunding = 0;
        }

        private void Close(double refPrice, long timeMs, int index, ExitReason reason)
        {
            int dir = Direction;
            double qty = Math.Abs(PositionQty);
            double fill = refPrice * (1 - _slippage * dir);
            double gross = PositionQty * (fill - EntryPrice);
            double fee = qty * fill * _commission;
            Cash += gross - fee;
            FeesPaid += fee;
            SlippageCost += qty * refPrice * _slippage;
            TradedNotional += qty * fill;

            Trades.Add(new Trade(dir, _entryTimeMs, EntryPrice, timeMs, fill, qty, gross, _entryFee + fee,
                _tradeFunding, reason, index - _entryIndex + 1));
            PositionQty = 0;
            EntryPrice = 0;
            StopPrice = null;
        }

        public void CloseAll(double refPrice, long timeMs, int index, ExitReason reason)
        {
            if (PositionQty != 0) Close(refPrice, timeMs, index, reason);
        }

        public void CheckStop(Bar bar, int index)
        {
            if (PositionQty == 0 || StopPrice is not double stop) return;
            if (PositionQty > 0 && bar.Low <= stop)
            {
                Close(Math.Min(bar.Open, stop), bar.OpenTimeMs, index, ExitReason.Stop);
                LockoutDirection = 1;
            }
            else if (PositionQty < 0 && bar.High >= stop)
            {
                Close(Math.Max(bar.Open, stop), bar.OpenTimeMs, index, ExitReason.Stop);
                LockoutDirection = -1;
            }
        }

        public void MarkClose(long closeTimeMs, double close)
        {
            EquityCurve.Add(new EquityPoint(closeTimeMs, Equity(close), PositionQty));
            BarsInWindow++;
            if (PositionQty != 0) BarsInMarket++;
        }

        public void CheckRisk(double equity, double dayStartEquity, long day)
        {
            _peakEquity = Math.Max(_peakEquity, equity);
            if (equity <= 0 && !Halted)
            {
                Halted = true;
                HaltReason = "equity <= 0";
                ForceFlat = PositionQty != 0;
                return;
            }
            if (cfg.Risk.MaxDrawdownStop is double maxDd && !Halted && equity <= _peakEquity * (1 - maxDd))
            {
                Halted = true;
                HaltReason = $"max drawdown stop {maxDd:P1} hit";
                ForceFlat = PositionQty != 0;
            }
            if (cfg.Risk.DailyLossStop is double dayStop && _blockedDay != day && equity <= dayStartEquity * (1 - dayStop))
            {
                _blockedDay = day;
                DailyStopsHit++;
                ForceFlat = PositionQty != 0;
            }
        }

        /// <summary>After the final exit, the last equity point reflects realized cash (exit costs included).</summary>
        public void RestampFinalEquity()
        {
            if (EquityCurve.Count == 0) return;
            var lastPoint = EquityCurve[^1];
            EquityCurve[^1] = lastPoint with { Equity = Cash, PositionQty = 0 };
        }
    }
}
