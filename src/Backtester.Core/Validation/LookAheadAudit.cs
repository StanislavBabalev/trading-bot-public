using Backtester.Core.Data;
using Backtester.Core.Engine;
using Backtester.Core.Strategies;

namespace Backtester.Core.Validation;

/// <summary>Outcome of <see cref="LookAheadAudit.Run"/>. Verdict: pass, fail, or inconclusive (too few trades to compare).</summary>
public sealed record LookAheadAuditResult(
    string Strategy,
    IReadOnlyDictionary<string, double> Parameters,
    int Seeds,
    int Comparisons,
    int TradesBeforeCut,
    int FeatureSeriesChecked,
    int FundingTradesChecked,
    string Verdict,
    IReadOnlyList<string> Problems);

/// <summary>
/// Automated look-ahead audit for one strategy and parameter set, on seeded synthetic data (never the research data).
/// For each seed and each of two cutoff bars, two altered markets are built that are identical up to the cutoff and
/// different after it: "other" continues with another seed's path, "mirror" with this path reflected through the last
/// close before the cutoff (every later move reversed, so a strategy peeking at the direction of the next bar always sees
/// it flip). Both also get 3x volume and funding of the opposite sign at 5x the size after the cutoff. Then:
///   1. Signals: the signal returned at every bar before the cutoff must be identical, so each one uses only data up to
///      its own bar's close; reading even one bar ahead changes the signal at the bar before the cutoff. Every trade closed
///      before the cutoff, and every entry made before it, must be identical too (the only check available for
///      self-simulating strategies, which do not return per-bar signals).
///   2. Rolling indicators: every <see cref="FeatureCache"/> series the strategy computed must hold identical values for
///      all bars before the cutoff, so no shared indicator uses a future window.
///   3. Funding settlement: each trade's funding must equal the sum, over settlements strictly after its entry and up to
///      its exit (strictly before it for an end-of-test exit), of −direction × quantity × that bar's open × rate. Nothing
///      settled at or before the entry is charged, nothing after the exit. Self-simulating strategies average into
///      positions, so their quantity varies inside a trade and only checks 1–2 apply to them.
/// </summary>
public static class LookAheadAudit
{
    public const int MinTradesBeforeCut = 5;

    /// <summary>Audits on bars of <paramref name="intervalMinutes"/> (it must divide a day).</summary>
    public static LookAheadAuditResult Run(StrategyDefinition def, IReadOnlyDictionary<string, double> parameters,
        IReadOnlyList<int>? seeds = null, int days = 900, IReadOnlyList<int>? cutDays = null, int intervalMinutes = 60)
    {
        seeds ??= [1, 2];
        cutDays ??= [600, 750];
        int barsPerDay = 1440 / intervalMinutes;
        var problems = new List<string>();
        int comparisons = 0, tradesBefore = 0, seriesChecked = 0, fundingChecked = 0;
        var start = new DateTime(2020, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        IStrategy Create() => def.Create(parameters) ?? throw new ArgumentException($"Invalid parameters for {def.Name}.");
        bool selfSimulating = Create() is ISelfSimulatingStrategy;

        foreach (int seed in seeds)
        {
            var (bars, funding) = Market(start, barsPerDay * days, seed, intervalMinutes);
            var (other, otherFunding) = Market(start, barsPerDay * days, seed + 100, intervalMinutes);
            var cfg = new BacktestConfig
            {
                Costs = new CostModel(0.0004, 0.0002),
                Sizing = new SizingConfig { Mode = SizingMode.RiskPerTrade, RiskPerTrade = 0.01 },
                FromMs = TimeUtil.ToMs(start.AddDays(120)),
                ToMs = bars[^1].OpenTimeMs + intervalMinutes * 60_000L,
                BarIntervalMinutes = intervalMinutes,
            };
            var original = new BacktestEngine(bars, funding);
            var recA = selfSimulating ? null : new SignalRecorder(Create());
            var a = original.Run(recA ?? Create(), cfg, parameters);
            if (!selfSimulating) fundingChecked += CheckFunding(a, bars, funding, seed, problems);

            foreach (int cutDay in cutDays)
            {
                int cut = barsPerDay * cutDay + 7;
                long cutMs = bars[cut].OpenTimeMs;
                tradesBefore += a.Trades.Count(t => t.ExitTimeMs < cutMs);
                double level = bars[cut - 1].Close, scale = level / other[cut - 1].Close;
                var futures = new (string Kind, Func<Bar, int, Bar> After, List<FundingRate> Funding)[]
                {
                    ("other", (b, i) => other[i] with
                    {
                        Open = other[i].Open * scale, High = other[i].High * scale, Low = other[i].Low * scale, Close = other[i].Close * scale,
                        Volume = other[i].Volume * 3,
                    }, otherFunding),
                    ("mirror", (b, i) => b with
                    {
                        Open = level * level / b.Open, High = level * level / b.Low, Low = level * level / b.High, Close = level * level / b.Close,
                        Volume = b.Volume * 3,
                    }, funding),
                };
                foreach (var (kind, after, laterFunding) in futures)
                {
                    comparisons++;
                    string where = $"seed {seed}, cutoff bar {cut}, {kind} future";
                    var spliced = bars.Select((b, i) => i < cut ? b : after(b, i)).ToList();
                    var splicedFunding = funding.Where(f => f.TimeMs < cutMs)
                        .Concat(laterFunding.Where(f => f.TimeMs >= cutMs).Select(f => f with { Rate = -f.Rate * 5 })).ToList();
                    var changed = new BacktestEngine(spliced, splicedFunding);
                    var recB = selfSimulating ? null : new SignalRecorder(Create());
                    var b = changed.Run(recB ?? Create(), cfg, parameters);

                    if (recA is not null && recB is not null)
                        for (int i = 0; i < cut; i++)
                            if (recA.Signals[i] != recB.Signals[i])
                            {
                                problems.Add($"{where}: the signal at bar {i} ({recA.Signals[i]} vs {recB.Signals[i]}) depends on data after it");
                                break;
                            }
                    if (!a.Trades.Where(t => t.ExitTimeMs < cutMs).SequenceEqual(b.Trades.Where(t => t.ExitTimeMs < cutMs)))
                        problems.Add($"{where}: trades closed before the cutoff change when only later data changes");
                    // A DCA deal's entry price is its running average, which later averaging fills legitimately move; compare timing only.
                    var entriesA = a.Trades.Where(t => t.EntryTimeMs < cutMs).Select(t => (t.Direction, t.EntryTimeMs, selfSimulating ? 0 : t.EntryPrice));
                    var entriesB = b.Trades.Where(t => t.EntryTimeMs < cutMs).Select(t => (t.Direction, t.EntryTimeMs, selfSimulating ? 0 : t.EntryPrice));
                    if (!entriesA.SequenceEqual(entriesB))
                        problems.Add($"{where}: entries made before the cutoff change when only later data changes");

                    foreach (var key in original.Features.ComputedKeys)
                    {
                        if (changed.Features.Peek(key) is not { } later) continue;   // computed on one market only: nothing to compare
                        var before = original.Features.Peek(key)!;
                        seriesChecked++;
                        for (int i = 0; i < cut; i++)
                            if (!before[i].Equals(later[i]))
                            {
                                problems.Add($"{where}: indicator series '{key}' differs at bar {i}, before the cutoff");
                                break;
                            }
                    }
                }
            }
        }

        string verdict = problems.Count > 0 ? "fail" : tradesBefore < MinTradesBeforeCut ? "inconclusive" : "pass";
        return new LookAheadAuditResult(def.Name, new Dictionary<string, double>(parameters), seeds.Count, comparisons, tradesBefore,
            seriesChecked, fundingChecked, verdict, problems);
    }

    /// <summary>
    /// <see cref="SyntheticData"/> plus 3% shock bars (5x volume and a 3% wick on one side), so wick- and volume-triggered
    /// strategies trade on the audit market too.
    /// </summary>
    private static (List<Bar> Bars, List<FundingRate> Funding) Market(DateTime start, int count, int seed, int intervalMinutes)
    {
        var (bars, funding) = SyntheticData.Generate(start, count, seed, intervalMinutes);
        var rng = new Random(seed * 7919 + 1);
        for (int i = 0; i < bars.Count; i++)
        {
            if (rng.NextDouble() >= 0.03) continue;
            var b = bars[i];
            bars[i] = rng.Next(2) == 0
                ? b with { Low = Math.Min(b.Open, b.Close) * 0.97, Volume = b.Volume * 5 }
                : b with { High = Math.Max(b.Open, b.Close) * 1.03, Volume = b.Volume * 5 };
        }
        return (bars, funding);
    }

    /// <summary>Passes every bar to the strategy and keeps the signal it returned there, by bar index.</summary>
    private sealed class SignalRecorder(IStrategy inner) : IStrategy
    {
        public List<Signal> Signals { get; } = [];
        public string Name => inner.Name;

        public Signal OnBarClose(StrategyContext ctx)
        {
            var s = inner.OnBarClose(ctx);
            Signals.Add(s);
            return s;
        }
    }

    /// <summary>Recomputes every trade's funding from the settlement rule; returns the number of trades checked.</summary>
    private static int CheckFunding(BacktestResult r, List<Bar> bars, List<FundingRate> funding, int seed, List<string> problems)
    {
        int checkedTrades = 0;
        foreach (var t in r.Trades)
        {
            bool endOfTest = t.ExitReason == ExitReason.EndOfTest;
            double expected = 0;
            int bi = 0;
            foreach (var f in funding)
            {
                if (f.TimeMs <= t.EntryTimeMs) continue;
                if (endOfTest ? f.TimeMs >= t.ExitTimeMs : f.TimeMs > t.ExitTimeMs) break;
                while (bi < bars.Count && bars[bi].OpenTimeMs < f.TimeMs) bi++;
                if (bi == bars.Count) break;
                expected += -t.Direction * t.Quantity * bars[bi].Open * r.Config.Costs.EffectiveFundingRate(f.Rate, t.Direction);
            }
            checkedTrades++;
            if (Math.Abs(expected - t.Funding) > 1e-9 * Math.Max(1, Math.Abs(t.Funding)))
            {
                problems.Add($"seed {seed}: trade entered {TimeUtil.Iso(t.EntryTimeMs)} settled funding {t.Funding:G6}, " +
                             $"the settlement rule gives {expected:G6}");
                if (problems.Count > 20) break;
            }
        }
        return checkedTrades;
    }
}
