using Backtester.Core;
using Backtester.Core.Analytics;
using Backtester.Core.Engine;
using static Backtester.Tests.TestData;

namespace Backtester.Tests;

public class AnalyticsTests
{
    [Theory]
    [InlineData(0.975, 1.959963985)]
    [InlineData(0.5, 0.0)]
    [InlineData(0.01, -2.326347874)]
    [InlineData(0.999, 3.090232306)]
    public void Normal_inverse_cdf_is_accurate(double p, double z) =>
        Assert.Equal(z, Statistics.NormalInv(p), 6);

    [Fact]
    public void Normal_cdf_round_trips_the_inverse()
    {
        foreach (var p in new[] { 0.001, 0.05, 0.3, 0.7, 0.95, 0.999 })
            Assert.Equal(p, Statistics.NormalCdf(Statistics.NormalInv(p)), 6);
    }

    [Fact]
    public void Moments_of_a_known_sample()
    {
        double[] x = [1, 2, 3, 4, 10];
        Assert.Equal(4, Statistics.Mean(x), 12);
        Assert.Equal(Math.Sqrt(12.5), Statistics.StdDev(x), 12);
        Assert.True(Statistics.Skewness(x) > 0);
        Assert.Equal(1, Statistics.Kurtosis(new double[] { -1, 1, -1, 1 }), 9);   // two-point distribution
    }

    [Fact]
    public void Probabilistic_sharpe_matches_the_closed_form()
    {
        // SR 0.1 per period, 101 observations, normal returns: z = 0.1 * 10 / sqrt(1 + 0.5 * 0.01)
        double expected = Statistics.NormalCdf(1.0 / Math.Sqrt(1.005));
        Assert.Equal(expected, DeflatedSharpe.Probabilistic(0.1, 0, 101, 0, 3), 9);
    }

    [Fact]
    public void Deflated_sharpe_penalizes_more_trials()
    {
        double one = DeflatedSharpe.Deflated(0.1, 1000, 0, 3, 1, 0.001);
        double ten = DeflatedSharpe.Deflated(0.1, 1000, 0, 3, 10, 0.001);
        double thousand = DeflatedSharpe.Deflated(0.1, 1000, 0, 3, 1000, 0.001);
        Assert.Equal(DeflatedSharpe.Probabilistic(0.1, 0, 1000, 0, 3), one, 12);
        Assert.True(one > ten && ten > thousand);
        Assert.True(DeflatedSharpe.ExpectedMaxSharpe(1000, 0.001) > DeflatedSharpe.ExpectedMaxSharpe(10, 0.001));
    }

    [Fact]
    public void Walk_forward_folds_on_the_development_window()
    {
        var folds = WalkForward.Generate(new DateTime(2021, 1, 1), new DateTime(2024, 1, 1), 12, 3);
        Assert.Equal(8, folds.Count);
        Assert.Equal(new DateTime(2021, 1, 1), folds[0].TrainFrom);
        Assert.Equal(new DateTime(2022, 1, 1), folds[0].TestFrom);
        Assert.Equal(new DateTime(2022, 4, 1), folds[0].TestTo);
        Assert.Equal(new DateTime(2023, 10, 1), folds[^1].TestFrom);
        Assert.Equal(new DateTime(2024, 1, 1), folds[^1].TestTo);
        Assert.All(folds, f => Assert.Equal(f.TrainTo, f.TestFrom));
    }

    [Fact]
    public void Daily_returns_use_end_of_day_equity()
    {
        var day = 86_400_000L;
        var eq = new List<EquityPoint>
        {
            new(T0 + Hour, 10_100, 0),
            new(T0 + day, 11_000, 0),          // closes at midnight -> still day 1
            new(T0 + day + Hour, 10_450, 0),
            new(T0 + 2 * day, 9_900, 0),
        };
        var r = PerformanceMetrics.DailyReturns(eq, 10_000);
        Assert.Equal(2, r.Count);
        Assert.Equal(0.10, r[0], 12);
        Assert.Equal(-0.10, r[1], 12);
    }

    [Fact]
    public void Metrics_for_a_simple_run()
    {
        var bars = Bars((100, 100, 100, 100), (100, 100, 100, 100), (50, 50, 50, 50), (150, 150, 150, 150), (150, 150, 150, 150));
        var r = new BacktestEngine(bars, []).Run(new ScriptedStrategy(i => i <= 2 ? Signal.Long() : Signal.Flat), Config(5));
        var m = PerformanceMetrics.Compute(r);
        Assert.Equal(1, m.Trades);
        Assert.Equal(50, m.NetProfitPct, 9);
        Assert.Equal(50, m.MaxDrawdownPct, 9);
        Assert.Equal(double.PositiveInfinity, m.ProfitFactor);
        Assert.Equal(60, m.TimeInMarketPct, 9);   // 3 of 5 bar closes in a position
    }

    [Fact]
    public void Buy_and_hold_pays_costs_but_no_funding()
    {
        var bars = Bars((100, 100, 100, 100), (100, 100, 100, 120));
        var cfg = Config(2, commission: 0.001, slippage: 0.001);
        var m = PerformanceMetrics.Compute(Benchmark.BuyAndHold(bars, cfg));
        double notional = 10_000 / 1.001, entry = 100 * 1.001, qty = notional / entry, exit = 120 * 0.999;
        double expected = 10_000 + qty * (exit - entry) - notional * 0.001 - qty * exit * 0.001;
        Assert.Equal(expected, m.FinalEquity, 6);
        Assert.Equal(0, m.FundingReceived);
    }
}
