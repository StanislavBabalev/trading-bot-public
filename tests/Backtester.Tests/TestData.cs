using Backtester.Core;
using Backtester.Core.Strategies;

namespace Backtester.Tests;

internal static class TestData
{
    public const long Hour = 3_600_000L;
    public static readonly long T0 = TimeUtil.ToMs(new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    /// <summary>Hourly bars from T0. Each tuple is (open, high, low, close).</summary>
    public static List<Bar> Bars(params (double o, double h, double l, double c)[] ohlc) =>
        ohlc.Select((x, i) => new Bar(T0 + i * Hour, x.o, x.h, x.l, x.c, 1)).ToList();

    /// <summary>n flat bars at a constant price.</summary>
    public static List<Bar> Flat(int n, double price) =>
        Enumerable.Range(0, n).Select(i => new Bar(T0 + i * Hour, price, price, price, price, 1)).ToList();

    /// <summary>
    /// A seeded random walk with wicks and varying volume, plus a 3% chance per bar of a 5x-volume shock bar:
    /// half reject a 3% spike back to near the open (long wick), half close 2.5% away with almost no wick.
    /// </summary>
    public static List<Bar> RandomOhlcv(int n, int seed)
    {
        var rng = new Random(seed);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        var bars = new List<Bar>(n);
        double close = 100;
        for (int i = 0; i < n; i++)
        {
            double open = close, high, low, volume = Math.Exp(0.3 * Gauss());
            if (rng.NextDouble() < 0.03)
            {
                int dir = rng.Next(2) == 0 ? 1 : -1;
                volume *= 5;
                if (rng.Next(2) == 0)
                {
                    close = open * (1 + dir * 0.004);
                    high = dir > 0 ? open * 1.03 : Math.Max(open, close) * 1.001;
                    low = dir < 0 ? open * 0.97 : Math.Min(open, close) * 0.999;
                }
                else
                {
                    close = open * (1 + dir * 0.025);
                    high = Math.Max(open, close) * 1.001;
                    low = Math.Min(open, close) * 0.999;
                }
            }
            else
            {
                close = open * Math.Exp(0.004 * Gauss());
                high = Math.Max(open, close) * (1 + 0.002 * Math.Abs(Gauss()));
                low = Math.Min(open, close) * (1 - 0.002 * Math.Abs(Gauss()));
            }
            bars.Add(new Bar(T0 + i * Hour, open, high, low, close, volume));
        }
        return bars;
    }

    public static BacktestConfig Config(int bars, double commission = 0, double slippage = 0, SizingConfig? sizing = null, RiskLimits? risk = null,
        bool funding = true, long? fromMs = null) => new()
    {
        InitialCapital = 10_000,
        Costs = new CostModel(commission, slippage),
        FundingEnabled = funding,
        Sizing = sizing ?? new SizingConfig { Mode = SizingMode.FixedFraction, Fraction = 1.0 },
        Risk = risk ?? new RiskLimits(),
        FromMs = fromMs ?? T0,
        ToMs = T0 + bars * Hour,
    };
}

/// <summary>Returns a fixed signal per bar index (default: flat).</summary>
internal sealed class ScriptedStrategy(Func<int, Signal> script) : IStrategy
{
    public string Name => "scripted";
    public List<int> SeenIndexes { get; } = [];

    public Signal OnBarClose(StrategyContext ctx)
    {
        SeenIndexes.Add(ctx.Index);
        return script(ctx.Index);
    }
}
