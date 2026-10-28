using Backtester.Core.Optimization;

namespace Backtester.Tests;

public class OptimizerTests
{
    private static TrialOutcome Outcome(IReadOnlyDictionary<string, double> p, double objective, double pf = 2, double sharpe = 1, int trades = 100) =>
        new(new Dictionary<string, double>(p), objective, pf, sharpe, trades, sharpe / Math.Sqrt(365));

    private static readonly ParamSpec[] Space = [new("x", 0, 10, 1), new("y", 0, 6, 1)];

    [Fact]
    public void Finds_an_interior_optimum_on_a_plateau()
    {
        var r = GridOptimizer.Run(Space, p => Outcome(p, -Math.Pow(p["x"] - 5, 2) - Math.Pow(p["y"] - 3, 2)),
            new OptimizerOptions { MaxEvaluations = 1000 });
        Assert.Equal(5, r.Best!.Parameters["x"]);
        Assert.Equal(3, r.Best.Parameters["y"]);
        Assert.Empty(r.BestAtEdgeOf);
        Assert.True(r.PlateauPassed);
        Assert.Equal(4, r.Neighbours.Count);
        Assert.False(r.Sampled);
        Assert.Equal(77, r.Evaluations);
    }

    [Fact]
    public void Flags_an_optimum_on_the_edge_of_the_range()
    {
        var r = GridOptimizer.Run(Space, p => Outcome(p, p["x"]), new OptimizerOptions { MaxEvaluations = 1000 });
        Assert.Equal(10, r.Best!.Parameters["x"]);
        Assert.Contains("x", r.BestAtEdgeOf);
    }

    [Fact]
    public void Fails_the_plateau_when_a_neighbour_is_weak()
    {
        var r = GridOptimizer.Run(Space, p =>
            Outcome(p, -Math.Pow(p["x"] - 5, 2) - Math.Pow(p["y"] - 3, 2), pf: p["x"] == 6 ? 0.9 : 2),
            new OptimizerOptions { MaxEvaluations = 1000 });
        Assert.False(r.PlateauPassed);
        Assert.Contains(r.Verdict, v => v.Contains("x=6"));
    }

    [Fact]
    public void Respects_the_evaluation_cap_including_neighbours()
    {
        int calls = 0;
        var r = GridOptimizer.Run(Space, p => { calls++; return Outcome(p, -Math.Abs(p["x"] - 5)); },
            new OptimizerOptions { MaxEvaluations = 20 });
        Assert.True(r.Sampled);
        Assert.True(calls <= 20, $"{calls} evaluations");
        Assert.Equal(calls, r.Evaluations);
    }

    [Fact]
    public void Skips_invalid_combinations_without_counting_them()
    {
        var r = GridOptimizer.Run(Space, p => p["x"] <= p["y"] ? null : Outcome(p, p["x"] - p["y"]),
            new OptimizerOptions { MaxEvaluations = 1000 });
        Assert.All(r.Trials, t => Assert.True(t.Parameters["x"] > t.Parameters["y"]));
        Assert.Equal(r.Trials.Count, r.Evaluations);
    }

    [Fact]
    public void Ignores_trials_below_the_minimum_trade_count()
    {
        var r = GridOptimizer.Run(Space, p => Outcome(p, p["x"], trades: p["x"] > 7 ? 5 : 100),
            new OptimizerOptions { MaxEvaluations = 1000, MinTrades = 50 });
        Assert.Equal(7, r.Best!.Parameters["x"]);
    }

    [Fact]
    public void Rejects_more_than_four_parameters()
    {
        var space = Enumerable.Range(0, 5).Select(i => new ParamSpec($"p{i}", 0, 1, 1)).ToList();
        Assert.Throws<ArgumentException>(() => GridOptimizer.Run(space, p => Outcome(p, 0), new OptimizerOptions()));
    }

    [Fact]
    public void Parses_range_syntax()
    {
        var s = ParamSpec.Parse("fast=10:30:5");
        Assert.Equal(new double[] { 10, 15, 20, 25, 30 }, s.Values());
    }
}
