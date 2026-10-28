using System.Globalization;

namespace Backtester.Core.Optimization;

/// <summary>An inclusive grid min, min+step, …, max for one strategy parameter.</summary>
public sealed record ParamSpec(string Name, double Min, double Max, double Step)
{
    public IReadOnlyList<double> Values()
    {
        if (Step <= 0 || Max < Min) throw new ArgumentException($"Bad range for '{Name}': {Min}:{Max}:{Step}");
        int n = (int)Math.Floor((Max - Min) / Step + 1e-9);
        return Enumerable.Range(0, n + 1).Select(k => Math.Round(Min + k * Step, 10)).ToList();
    }

    /// <summary>Parses "name=min:max:step".</summary>
    public static ParamSpec Parse(string text)
    {
        var (name, range) = text.Split('=', 2) is [var n, var r] ? (n, r) : throw new FormatException($"Expected name=min:max:step, got '{text}'");
        var parts = range.Split(':');
        if (parts.Length != 3) throw new FormatException($"Expected name=min:max:step, got '{text}'");
        var v = parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return new ParamSpec(name, v[0], v[1], v[2]);
    }
}

/// <summary>What the optimizer needs from one evaluated parameter set.</summary>
public sealed record TrialOutcome(
    IReadOnlyDictionary<string, double> Parameters,
    double Objective,
    double ProfitFactor,
    double Sharpe,
    int Trades,
    double DailySharpe);

public sealed record OptimizerOptions
{
    /// <summary>Hard cap on evaluations, including the plateau neighbours of the best point.</summary>
    public int MaxEvaluations { get; init; } = 200;
    public int MinTrades { get; init; }
    public int MaxParameters { get; init; } = 4;
    public int Seed { get; init; } = 42;
    public double PlateauMinProfitFactor { get; init; } = 1.05;
    public double PlateauMinSharpe { get; init; } = 0.3;
}

public sealed record OptimizationReport(
    IReadOnlyList<ParamSpec> Space,
    long GridSize,
    bool Sampled,
    int Evaluations,
    IReadOnlyList<TrialOutcome> Trials,
    TrialOutcome? Best,
    IReadOnlyList<string> BestAtEdgeOf,
    IReadOnlyList<TrialOutcome> Neighbours,
    bool PlateauPassed,
    IReadOnlyList<string> Verdict);

/// <summary>
/// Exhaustive grid search, or a seeded random sample of the grid when it is larger than the evaluation budget.
/// The best point is rejected if it sits on the edge of any range, and must sit on a plateau: every ±1-step
/// neighbour has to keep profit factor and Sharpe above the configured floors.
/// </summary>
public static class GridOptimizer
{
    /// <param name="prefetch">
    /// Optional: receives every search point (not the plateau neighbours) before the first <paramref name="evaluate"/> call,
    /// e.g. to backtest them in parallel into a cache. <paramref name="evaluate"/> is still called once per point, in
    /// the same order as without it, so the report does not depend on whether or how points were prefetched.
    /// </param>
    public static OptimizationReport Run(
        IReadOnlyList<ParamSpec> space,
        Func<IReadOnlyDictionary<string, double>, TrialOutcome?> evaluate,
        OptimizerOptions options,
        IReadOnlyDictionary<string, double>? fixedParameters = null,
        Action<IReadOnlyList<IReadOnlyDictionary<string, double>>>? prefetch = null)
    {
        if (space.Count == 0) throw new ArgumentException("Nothing to optimize.");
        if (space.Count > options.MaxParameters)
            throw new ArgumentException($"{space.Count} tunable parameters exceeds the limit of {options.MaxParameters}.");

        var grids = space.Select(s => s.Values()).ToList();
        long gridSize = grids.Aggregate(1L, (acc, g) => acc * g.Count);
        int neighbourBudget = 2 * space.Count;
        int searchBudget = Math.Max(1, options.MaxEvaluations - neighbourBudget);

        var sizes = grids.Select(g => g.Count).ToArray();
        bool sampled = gridSize > searchBudget;
        var points = sampled ? Sample(sizes, searchBudget, options.Seed) : Cartesian(sizes).ToList();

        var cache = new Dictionary<string, TrialOutcome?>();
        var trials = new List<TrialOutcome>();
        var indexOf = new Dictionary<TrialOutcome, int[]>(ReferenceEqualityComparer.Instance);
        int evaluations = 0;

        Dictionary<string, double> Params(int[] idx)
        {
            var p = new Dictionary<string, double>(fixedParameters ?? new Dictionary<string, double>());
            for (int d = 0; d < space.Count; d++) p[space[d].Name] = grids[d][idx[d]];
            return p;
        }

        TrialOutcome? Eval(int[] idx)
        {
            string key = string.Join(',', idx);
            if (cache.TryGetValue(key, out var cached)) return cached;
            var outcome = evaluate(Params(idx));
            cache[key] = outcome;
            if (outcome is not null)
            {
                evaluations++;
                trials.Add(outcome);
                indexOf[outcome] = (int[])idx.Clone();
            }
            return outcome;
        }

        prefetch?.Invoke(points.Select(Params).ToList());
        foreach (var idx in points) Eval(idx);

        var best = trials
            .Where(t => t.Trades >= options.MinTrades && !double.IsNaN(t.Objective))
            .OrderByDescending(t => t.Objective)
            .FirstOrDefault();

        var verdict = new List<string>();
        var atEdge = new List<string>();
        var neighbours = new List<TrialOutcome>();
        bool plateau = false;

        if (best is null)
        {
            verdict.Add($"no trial with at least {options.MinTrades} trades");
        }
        else
        {
            var bi = indexOf[best];
            for (int d = 0; d < space.Count; d++)
                if (grids[d].Count > 1 && (bi[d] == 0 || bi[d] == grids[d].Count - 1)) atEdge.Add(space[d].Name);
            if (atEdge.Count > 0) verdict.Add($"best point is on the edge of: {string.Join(", ", atEdge)}");

            for (int d = 0; d < space.Count; d++)
                foreach (int delta in new[] { -1, 1 })
                {
                    int j = bi[d] + delta;
                    if (j < 0 || j >= grids[d].Count) continue;
                    var n = (int[])bi.Clone();
                    n[d] = j;
                    if (Eval(n) is { } outcome) neighbours.Add(outcome);
                }

            var weak = neighbours.Where(n =>
                !(n.ProfitFactor >= options.PlateauMinProfitFactor) || !(n.Sharpe >= options.PlateauMinSharpe) || n.Trades < options.MinTrades).ToList();
            plateau = neighbours.Count > 0 && weak.Count == 0;
            if (neighbours.Count == 0) verdict.Add("no valid neighbours to check the plateau");
            foreach (var w in weak)
                verdict.Add($"neighbour {Describe(w.Parameters, space)} fails plateau (PF {w.ProfitFactor:F2}, Sharpe {w.Sharpe:F2}, trades {w.Trades})");
        }

        return new OptimizationReport(space, gridSize, sampled, evaluations, trials, best, atEdge, neighbours, plateau, verdict);
    }

    /// <summary>Every grid point in the optimizer's exhaustive order, each overlaid on <paramref name="fixedParameters"/>.</summary>
    public static IEnumerable<Dictionary<string, double>> Enumerate(IReadOnlyList<ParamSpec> space, IReadOnlyDictionary<string, double>? fixedParameters = null)
    {
        var grids = space.Select(s => s.Values()).ToList();
        foreach (var idx in Cartesian(grids.Select(g => g.Count).ToArray()))
        {
            var p = new Dictionary<string, double>(fixedParameters ?? new Dictionary<string, double>());
            for (int d = 0; d < space.Count; d++) p[space[d].Name] = grids[d][idx[d]];
            yield return p;
        }
    }

    private static string Describe(IReadOnlyDictionary<string, double> p, IReadOnlyList<ParamSpec> space) =>
        string.Join(" ", space.Select(s => $"{s.Name}={p[s.Name].ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>Distinct random grid points, without materializing the whole grid.</summary>
    private static List<int[]> Sample(int[] sizes, int count, int seed)
    {
        var rng = new Random(seed);
        var seen = new HashSet<string>();
        var points = new List<int[]>(count);
        while (points.Count < count)
        {
            var idx = sizes.Select(n => rng.Next(n)).ToArray();
            if (seen.Add(string.Join(',', idx))) points.Add(idx);
        }
        return points;
    }

    private static IEnumerable<int[]> Cartesian(int[] sizes)
    {
        var idx = new int[sizes.Length];
        while (true)
        {
            yield return (int[])idx.Clone();
            int d = sizes.Length - 1;
            while (d >= 0 && ++idx[d] == sizes[d]) { idx[d] = 0; d--; }
            if (d < 0) yield break;
        }
    }
}
