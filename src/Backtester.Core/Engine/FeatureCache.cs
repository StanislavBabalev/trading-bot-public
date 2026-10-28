using System.Collections.Concurrent;
using Backtester.Core.Indicators;

namespace Backtester.Core.Engine;

/// <summary>
/// Indicator series over an engine's full bar history, computed once per key and shared by every run and thread.
/// Element i is the indicator's value after bar i closed (NaN until it is ready), produced by the same incremental
/// indicator class a strategy would update bar by bar, so reading series[ctx.Index] is bit-identical to updating it.
/// Strategies must only read indexes up to <see cref="Strategies.StrategyContext.Index"/>.
/// </summary>
public sealed class FeatureCache(Bar[] bars, FundingRate[] funding)
{
    private readonly ConcurrentDictionary<string, Lazy<double[]>> _series = new();

    /// <summary>
    /// Number of funding rates settled at or before each bar's open. A settled rate is public from its timestamp on;
    /// using the bar's open (not its close) is conservative by up to one bar.
    /// </summary>
    public double[] FundingCount() => Get("funding-count", b =>
    {
        var s = new double[b.Length];
        int k = 0;
        for (int i = 0; i < b.Length; i++)
        {
            while (k < funding.Length && funding[k].TimeMs <= b[i].OpenTimeMs) k++;
            s[i] = k;
        }
        return s;
    });

    /// <summary>The latest funding rate settled at or before each bar's open; NaN before the first.</summary>
    public double[] Funding() => Get("funding", b =>
    {
        var count = FundingCount();
        return count.Select(k => k > 0 ? funding[(int)k - 1].Rate : double.NaN).ToArray();
    });

    /// <summary>
    /// Crowding rank in [0, 1]: where the mean of the last <paramref name="avgN"/> settled rates sits among the same mean
    /// at each of the previous <paramref name="window"/> settlements (ties count half). High = longs pay unusually much.
    /// NaN until avgN + window rates have settled. Uses only rates settled by each bar's open.
    /// </summary>
    public double[] FundingRank(int avgN, int window) => Get($"funding-rank({avgN},{window})", b =>
    {
        var mean = new double[funding.Length + 1];   // mean[k] = mean of rates k-avgN .. k-1 (k rates settled)
        double sum = 0;
        for (int k = 1; k <= funding.Length; k++)
        {
            sum += funding[k - 1].Rate;
            if (k > avgN) sum -= funding[k - 1 - avgN].Rate;
            mean[k] = k >= avgN ? sum / avgN : double.NaN;
        }
        var rank = new double[funding.Length + 1];
        for (int k = 0; k <= funding.Length; k++)
        {
            if (k - window < avgN) { rank[k] = double.NaN; continue; }
            double below = 0;
            for (int j = k - window; j < k; j++)
                below += mean[j] < mean[k] ? 1 : mean[j] == mean[k] ? 0.5 : 0;
            rank[k] = below / window;
        }
        return FundingCount().Select(k => rank[(int)k]).ToArray();
    });

    public int Count => _series.Count;

    /// <summary>Keys of the series computed so far (for the look-ahead audit).</summary>
    public IReadOnlyList<string> ComputedKeys => _series.Where(kv => kv.Value.IsValueCreated).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>The series for <paramref name="key"/> if it has been computed, without computing it.</summary>
    public double[]? Peek(string key) => _series.TryGetValue(key, out var lazy) && lazy.IsValueCreated ? lazy.Value : null;

    /// <summary>The series for <paramref name="key"/>, computing it on first use. The key must identify the computation.</summary>
    public double[] Get(string key, Func<Bar[], double[]> compute) =>
        _series.GetOrAdd(key, _ => new Lazy<double[]>(() => compute(bars), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>ta.atr(<paramref name="length"/>) after each bar; NaN until ready.</summary>
    public double[] Atr(int length) => Get($"atr({length})", b =>
    {
        var atr = new Atr(length);
        var s = new double[b.Length];
        for (int i = 0; i < b.Length; i++)
        {
            atr.Update(b[i]);
            s[i] = atr.IsReady ? atr.Value : double.NaN;
        }
        return s;
    });

    /// <summary>Rolling median over the last <paramref name="length"/> ready values of ATR(<paramref name="atrLength"/>); NaN until ready.</summary>
    public double[] AtrMedian(int atrLength, int length) => Get($"median(atr({atrLength}),{length})", b =>
    {
        var atr = Atr(atrLength);
        var median = new RollingMedian(length);
        var s = new double[b.Length];
        for (int i = 0; i < b.Length; i++)
        {
            if (!double.IsNaN(atr[i])) median.Add(atr[i]);
            s[i] = median.IsReady ? median.Value : double.NaN;
        }
        return s;
    });
}
