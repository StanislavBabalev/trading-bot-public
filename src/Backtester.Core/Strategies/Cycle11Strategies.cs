using Backtester.Core.Engine;
using Backtester.Core.Indicators;

namespace Backtester.Core.Strategies;

// Hunt cycle 11: Veles-style DCA grid bots (artifacts/hunt/cycle_11_ideas/research.md), modelled on Veles's public help
// center: a market base order plus averaging limit orders spread over an "overlap" range, each later order larger by a
// martingale %, a take-profit limit a % from the average entry, an optional stop, and optional entry filters.

/// <summary>Decides whether a DCA bot may start a deal. Updated with every bar (warm-up included), asked at a bar's close.</summary>
public interface IDealFilter
{
    void Update(Bar b);
    bool Allows(int direction);
}

public sealed class NoDealFilter : IDealFilter
{
    public void Update(Bar b) { }
    public bool Allows(int direction) => true;
}

/// <summary>RSI(length) of 1h closes below <c>level</c> for a long deal (above 100 − level for a short).</summary>
public sealed class RsiDealFilter(int length, double level) : IDealFilter
{
    private readonly Rsi _rsi = new(length);
    public void Update(Bar b) => _rsi.Update(b.Close);
    public bool Allows(int direction) => _rsi.IsReady && (direction > 0 ? _rsi.Value < level : _rsi.Value > 100 - level);
}

/// <summary>The last daily close above the SMA of the last <c>maDays</c> daily closes for a long deal (below for a short).</summary>
public sealed class DailyTrendDealFilter(int maDays) : IDealFilter
{
    private readonly DailyBars _daily = new();
    private int _regime;

    public void Update(Bar b)
    {
        _daily.Update(b);
        if (!_daily.DayClosed || _daily.Days.Count < maDays) return;
        double sma = 0;
        for (int k = 0; k < maDays; k++) sma += _daily.Back(k).Close;
        _regime = Math.Sign(_daily.Back(0).Close - sma / maDays);
    }

    public bool Allows(int direction) => _regime == direction;
}

/// <summary>
/// A DCA grid bot. When flat and the filter allows, a deal starts at the next bar's open with a market base order at
/// P0. Averaging limit orders k = 1..orders−1 rest at P0 × (1 ∓ overlap × (k / (orders − 1))^logFactor); order k's notional
/// is the base order's × (1 + martingale)^k. The take-profit limit sits takeProfit beyond the current average entry
/// and moves as orders fill. The stop sits stopBuffer beyond the last order. Each deal is sized so that a fully filled
/// grid stopped out loses RiskPerTrade × equity (capped at MaxLeverage × equity in total notional).
/// Inside a bar, price is assumed to go open → low → high → close on an up bar and open → high → low → close on a
/// down bar; limit orders fill at their price (or the open if it gapped through), every fill pays commission and slippage.
/// A deal closed in a bar is followed by the next one at the next open (if the filter allows on that close).
/// </summary>
public sealed class DcaGridBot : ISelfSimulatingStrategy
{
    private readonly string _label;
    private readonly int _dir, _orders;
    private readonly double _overlap, _martingale, _takeProfit, _stopBuffer, _logFactor;
    private readonly Func<IDealFilter> _filter;

    public DcaGridBot(string label, int direction, int orders, double overlap, double martingale, double takeProfit,
        double stopBuffer, double logFactor, Func<IDealFilter> filter)
    {
        if (direction is not (1 or -1)) throw new ArgumentException("Direction must be 1 or -1.");
        if (orders < 1 || overlap <= 0 || takeProfit <= 0 || stopBuffer <= 0) throw new ArgumentException("Invalid DCA grid.");
        if (direction > 0 && overlap + stopBuffer >= 1) throw new ArgumentException("A long grid and its stop must stay above zero.");
        (_label, _dir, _orders, _overlap, _martingale, _takeProfit, _stopBuffer, _logFactor, _filter) =
            (label, direction, orders, overlap, martingale, takeProfit, stopBuffer, logFactor, filter);
    }

    public string Name => $"{_label}({_orders},{_overlap},{_martingale},{_takeProfit})";

    public Signal OnBarClose(StrategyContext ctx) => throw new NotSupportedException($"{Name} simulates itself; run it through BacktestEngine.Run.");

    public BacktestResult Simulate(Bar[] bars, FundingRate[] funding, BacktestConfig cfg, IReadOnlyDictionary<string, double> parameters)
    {
        if (cfg.Sizing.Mode != SizingMode.RiskPerTrade)
            throw new NotSupportedException("DCA grids are sized from the stop below the grid; use RiskPerTrade sizing.");
        return new Run(this, bars, funding, cfg, parameters).Execute();
    }

    /// <summary>One simulation: account, the open deal, and the result counters.</summary>
    private sealed class Run(DcaGridBot bot, Bar[] bars, FundingRate[] funding, BacktestConfig cfg, IReadOnlyDictionary<string, double> parameters)
    {
        private readonly int _s = bot._dir, _n = bot._orders;
        private readonly double _comm = cfg.Costs.CommissionRate, _slip = cfg.Costs.SlippageRate;
        private readonly double[] _levels = new double[bot._orders], _plannedQty = new double[bot._orders];
        private readonly bool[] _filled = new bool[bot._orders];
        private readonly List<Trade> _trades = [];
        private readonly List<EquityPoint> _equity = [];
        private double _cash = cfg.InitialCapital, _qty, _costBasis, _avg, _stop, _dealFees, _dealFunding;
        private double _fees, _fundingReceived, _slippageCost, _tradedNotional;
        private bool _inDeal;
        private long _entryTimeMs;
        private int _entryIndex, _barsInWindow, _barsInMarket;

        public BacktestResult Execute()
        {
            var filter = bot._filter();
            bool pending = false, started = false, exitedAtEnd = false;
            long startMs = 0;
            int fi = 0, lastIndex = -1;
            while (fi < funding.Length && funding[fi].TimeMs < cfg.FromMs) fi++;

            for (int i = 0; i < bars.Length; i++)
            {
                var bar = bars[i];
                if (bar.OpenTimeMs >= cfg.ToMs)
                {
                    if (started)
                    {
                        if (_inDeal) Close(bar.Open, bar.OpenTimeMs, i, ExitReason.EndOfTest);
                        exitedAtEnd = true;
                    }
                    break;
                }
                bool inWindow = bar.OpenTimeMs >= cfg.FromMs;
                if (inWindow)
                {
                    if (!started) { started = true; startMs = bar.OpenTimeMs; }
                    if (cfg.FundingEnabled)
                        for (; fi < funding.Length && funding[fi].TimeMs <= bar.OpenTimeMs; fi++)
                            if (_inDeal)
                            {
                                double payment = -_s * _qty * bar.Open * cfg.Costs.EffectiveFundingRate(funding[fi].Rate, _s);
                                _cash += payment;
                                _fundingReceived += payment;
                                _dealFunding += payment;
                            }
                    if (pending) { Open(bar, i); pending = false; }
                    if (_inDeal) Intrabar(bar, i);
                    _equity.Add(new EquityPoint(bar.OpenTimeMs + cfg.BarIntervalMs, _cash + (_inDeal ? _s * _qty * (bar.Close - _avg) : 0), _inDeal ? _s * _qty : 0));
                    _barsInWindow++;
                    if (_inDeal) _barsInMarket++;
                    lastIndex = i;
                }
                filter.Update(bar);
                if (inWindow && !_inDeal && filter.Allows(_s)) pending = true;
            }
            if (!started) throw new InvalidOperationException("No bars inside the trading window.");

            var last = bars[lastIndex];
            if (!exitedAtEnd && _inDeal) Close(last.Close, last.OpenTimeMs + cfg.BarIntervalMs, lastIndex, ExitReason.EndOfTest);
            if (_equity.Count > 0) _equity[^1] = _equity[^1] with { Equity = _cash, PositionQty = 0 };

            return new BacktestResult
            {
                StrategyName = bot.Name,
                Parameters = parameters,
                Config = cfg,
                Trades = _trades,
                Equity = _equity,
                StartMs = startMs,
                EndMs = last.OpenTimeMs + cfg.BarIntervalMs,
                FinalEquity = _cash,
                FeesPaid = _fees,
                FundingReceived = _fundingReceived,
                SlippageCost = _slippageCost,
                TradedNotional = _tradedNotional,
                BarsInWindow = _barsInWindow,
                BarsInMarket = _barsInMarket,
            };
        }

        private void Open(Bar bar, int index)
        {
            double p0 = bar.Open;
            double lossPerUnit = 0, weightSum = 0;
            for (int k = 0; k < _n; k++)
            {
                double offset = _n == 1 ? 0 : bot._overlap * Math.Pow((double)k / (_n - 1), bot._logFactor);
                _levels[k] = p0 * (1 - _s * offset);
                weightSum += Math.Pow(1 + bot._martingale, k);
            }
            _stop = p0 * (1 - _s * (bot._overlap + bot._stopBuffer));
            for (int k = 0; k < _n; k++)
            {
                double w = Math.Pow(1 + bot._martingale, k) / weightSum;   // share of the deal's total notional
                lossPerUnit += w / _levels[k] * _s * (_levels[k] - _stop);
            }
            double equity = _cash;
            if (equity <= 0 || lossPerUnit <= 0) return;
            double total = Math.Min(cfg.Sizing.RiskPerTrade * equity / lossPerUnit, cfg.Risk.MaxLeverage * equity);
            for (int k = 0; k < _n; k++)
            {
                _plannedQty[k] = total * Math.Pow(1 + bot._martingale, k) / weightSum / _levels[k];
                _filled[k] = false;
            }
            _inDeal = true;
            _qty = _costBasis = _dealFees = _dealFunding = 0;
            _entryTimeMs = bar.OpenTimeMs;
            _entryIndex = index;
            Fill(0, p0);
        }

        private void Fill(int k, double refPrice)
        {
            double fill = refPrice * (1 + _s * _slip), q = _plannedQty[k];
            double fee = q * fill * _comm;
            _cash -= fee;
            _fees += fee;
            _dealFees += fee;
            _slippageCost += q * refPrice * _slip;
            _tradedNotional += q * fill;
            _qty += q;
            _costBasis += q * fill;
            _avg = _costBasis / _qty;
            _filled[k] = true;
        }

        private void Intrabar(Bar bar, int index)
        {
            bool lowFirst = bar.Close >= bar.Open;
            bool adverseFirst = (_s > 0) == lowFirst;
            if (adverseFirst) { Adverse(bar, index); if (_inDeal) Favorable(bar, index); }
            else { Favorable(bar, index); if (_inDeal) Adverse(bar, index); }
        }

        /// <summary>The bar's extreme against the deal: averaging orders it reaches fill, then the stop is checked.</summary>
        private void Adverse(Bar bar, int index)
        {
            double extreme = _s > 0 ? bar.Low : bar.High;
            for (int k = 1; k < _n; k++)
            {
                if (_filled[k] || _s * (extreme - _levels[k]) > 0) continue;
                Fill(k, _s > 0 ? Math.Min(bar.Open, _levels[k]) : Math.Max(bar.Open, _levels[k]));
            }
            if (_s * (extreme - _stop) <= 0)
                Close(_s > 0 ? Math.Min(bar.Open, _stop) : Math.Max(bar.Open, _stop), bar.OpenTimeMs, index, ExitReason.Stop);
        }

        /// <summary>The bar's extreme in favour of the deal: the take-profit limit at the current average fills if reached.</summary>
        private void Favorable(Bar bar, int index)
        {
            double extreme = _s > 0 ? bar.High : bar.Low, target = _avg * (1 + _s * bot._takeProfit);
            if (_s * (extreme - target) >= 0)
                Close(_s > 0 ? Math.Max(bar.Open, target) : Math.Min(bar.Open, target), bar.OpenTimeMs, index, ExitReason.TakeProfit);
        }

        private void Close(double refPrice, long timeMs, int index, ExitReason reason)
        {
            double fill = refPrice * (1 - _s * _slip);
            double gross = _s * _qty * (fill - _avg);
            double fee = _qty * fill * _comm;
            _cash += gross - fee;
            _fees += fee;
            _dealFees += fee;
            _slippageCost += _qty * refPrice * _slip;
            _tradedNotional += _qty * fill;
            _trades.Add(new Trade(_s, _entryTimeMs, _avg, timeMs, fill, _qty, gross, _dealFees, _dealFunding, reason, index - _entryIndex + 1));
            _inDeal = false;
            _qty = _costBasis = 0;
        }
    }
}
