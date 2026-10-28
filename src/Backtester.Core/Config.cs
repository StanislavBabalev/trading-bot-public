using System.Text.Json;
using Backtester.Core.Reporting;

namespace Backtester.Core;

/// <summary>
/// Per-side costs as fractions of traded notional (0.0004 = 0.04%), plus two funding stress knobs used only by
/// validation scenarios: <paramref name="FundingScale"/> multiplies every settled rate, and
/// <paramref name="AdverseFundingRate"/>, when set, replaces every rate by one of that size that the position always pays.
/// </summary>
public sealed record CostModel(double CommissionRate, double SlippageRate, double FundingScale = 1, double? AdverseFundingRate = null)
{
    /// <summary>The rate a position with sign <paramref name="direction"/> settles at a funding timestamp whose rate is <paramref name="rate"/>.</summary>
    public double EffectiveFundingRate(double rate, int direction) =>
        AdverseFundingRate is double adverse ? direction * Math.Abs(adverse) : rate * FundingScale;
}

public enum SizingMode
{
    /// <summary>Notional = equity × <see cref="SizingConfig.Fraction"/>.</summary>
    FixedFraction,
    /// <summary>Quantity sized so that a stop-out loses equity × <see cref="SizingConfig.RiskPerTrade"/>. Needs a stop.</summary>
    RiskPerTrade,
    /// <summary>Notional = equity × target vol / realized vol of hourly log returns.</summary>
    VolTarget,
}

public sealed record SizingConfig
{
    public SizingMode Mode { get; init; } = SizingMode.FixedFraction;
    public double Fraction { get; init; } = 1.0;
    public double RiskPerTrade { get; init; } = 0.01;
    public double VolTargetAnnual { get; init; } = 0.20;
    public int VolLookbackBars { get; init; } = 720;
}

public sealed record RiskLimits
{
    /// <summary>Cap on position notional / equity at entry, applied in every sizing mode.</summary>
    public double MaxLeverage { get; init; } = 1.0;
    /// <summary>Go flat and stop trading for good when equity falls this far below its peak (0.30 = 30%).</summary>
    public double? MaxDrawdownStop { get; init; }
    /// <summary>Go flat and block entries for the rest of the UTC day after losing this much since the day's open.</summary>
    public double? DailyLossStop { get; init; }
}

public sealed record BacktestConfig
{
    public double InitialCapital { get; init; } = 10_000;
    public CostModel Costs { get; init; } = new(0.0004, 0.0002);
    public bool FundingEnabled { get; init; } = true;
    public SizingConfig Sizing { get; init; } = new();
    public RiskLimits Risk { get; init; } = new();
    /// <summary>Inclusive start of the trading window (bar open time, Unix ms).</summary>
    public long FromMs { get; init; }
    /// <summary>Exclusive end of the trading window (bar open time, Unix ms).</summary>
    public long ToMs { get; init; }
    public int BarIntervalMinutes { get; init; } = 60;
    public long BarIntervalMs => BarIntervalMinutes * 60_000L;
}

public sealed record DataSplit(DateTime DevFrom, DateTime DevTo, DateTime OosFrom, DateTime OosTo);

public sealed record WalkForwardRules
{
    public int TrainMonths { get; init; } = 12;
    public int TestMonths { get; init; } = 3;
    public int StepMonths { get; init; } = 6;
    /// <summary>A strategy is killed when more than this many folds fail.</summary>
    public int MaxFoldFailures { get; init; } = 1;
    public double FoldMinProfitFactor { get; init; } = 1.0;
}

/// <summary>Kill thresholds and budgets for a hunt cycle.</summary>
public sealed record HuntRules
{
    public int HypothesesPerCycle { get; init; } = 5;
    public int OptimizerMaxEvaluations { get; init; } = 200;
    public double MinProfitFactor { get; init; } = 1.1;
    public double MinSharpe { get; init; } = 0.5;
    public double MaxDrawdownPct { get; init; } = 30;
    public double MinTradesPerYear { get; init; } = 50;
    public double PlateauMinProfitFactor { get; init; } = 1.05;
    public double PlateauMinSharpe { get; init; } = 0.3;
    public WalkForwardRules WalkForward { get; init; } = new();
}

/// <summary>
/// Bounds for the local research controller (<c>research run</c>) and <c>hunt</c>. They limit work, never change results.
/// New experiments are those not already in the experiment store; stored results are reused for free.
/// </summary>
public sealed record ResearchLimits
{
    public int MaxCyclesPerRun { get; init; } = 20;
    public int MaxNewExperimentsPerRun { get; init; } = 100_000;
    public int MaxNewExperimentsPerCycle { get; init; } = 5_000;
    public double MaxRuntimeMinutes { get; init; } = 120;
    /// <summary>Backtest worker threads; 0 = one per logical processor minus one.</summary>
    public int Workers { get; init; }
    public int MaxManagedMemoryMb { get; init; } = 4096;
    /// <summary>Experiment store (JSON Lines). Git-ignored: it is a cache, and the hunt log and cycle reports stay the record.</summary>
    public string ExperimentStore { get; init; } = "artifacts/cache/experiments.jsonl";

    public int ResolvedWorkers => Workers > 0 ? Workers : Math.Max(1, Environment.ProcessorCount - 1);
}

/// <summary>
/// The engine-side part of the validation harness (config "validation"): what <c>validate-export</c> runs, and whether a
/// locked-OOS run requires a passing validation report. The gate thresholds in the same section are read by the Python
/// layer (<c>python -m validation</c>) and are ignored here.
/// </summary>
public sealed record ValidationSettings
{
    /// <summary>Refuse every locked-OOS run (hunt step 7, <c>backtest --unlock-oos</c>) without a passing report.</summary>
    public bool HoldoutGate { get; init; } = true;
    public string OutputDir { get; init; } = "artifacts/validation";
    /// <summary>Relative perturbations of each continuous parameter, applied as ± (1D) and crossed in pairs (2D).</summary>
    public double[] PerturbationSteps { get; init; } = [0.1, 0.2, 0.3];
    public double[] SlippageMultipliers { get; init; } = [0, 1, 2, 3, 4];
    public double[] CommissionMultipliers { get; init; } = [1, 2, 3];
    public double FundingScale { get; init; } = 3;
    /// <summary>Calendar months (yyyy-MM, inside dev) whose mean |funding rate| is replayed as a constant adverse rate.</summary>
    public string[] FundingSpikeMonths { get; init; } = ["2021-05", "2022-06", "2022-11"];
    /// <summary>A strategy's own registered grid is the CPCV selection set when it holds at least this many trials.</summary>
    public int MinRegisteredGridForSelection { get; init; } = 10;
}

/// <summary>The fixed research constants, loaded from config/hunt.json.</summary>
public sealed record HuntConfig
{
    public string Symbol { get; init; } = "BTCUSDT";
    public int IntervalMinutes { get; init; } = 60;
    public string DataDir { get; init; } = "data/bybit";
    public double InitialCapital { get; init; } = 10_000;
    public CostModel Costs { get; init; } = new(0.0004, 0.0002);
    public bool FundingEnabled { get; init; } = true;
    public SizingConfig Sizing { get; init; } = new();
    public RiskLimits Risk { get; init; } = new();
    public HuntRules Hunt { get; init; } = new();
    public ResearchLimits Research { get; init; } = new();
    public ValidationSettings Validation { get; init; } = new();
    /// <summary>The file this config was loaded from (its hash is part of a validation report's identity).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? SourcePath { get; init; }
    public DataSplit Split { get; init; } = new(
        new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc));

    public string BarsPath => Path.Combine(DataDir, $"{Symbol}_{IntervalMinutes}.csv");
    public string FundingPath => Path.Combine(DataDir, $"{Symbol}_funding.csv");

    public static HuntConfig Load(string path) =>
        (JsonSerializer.Deserialize<HuntConfig>(File.ReadAllText(path), Json.Options)
         ?? throw new InvalidDataException($"Could not read {path}")) with { SourcePath = path };

    public BacktestConfig ToBacktestConfig(DateTime fromUtc, DateTime toUtc) => new()
    {
        InitialCapital = InitialCapital,
        Costs = Costs,
        FundingEnabled = FundingEnabled,
        Sizing = Sizing,
        Risk = Risk,
        FromMs = TimeUtil.ToMs(fromUtc),
        ToMs = TimeUtil.ToMs(toUtc),
        BarIntervalMinutes = IntervalMinutes,
    };
}

public static class TimeUtil
{
    public static long ToMs(DateTime t) =>
        new DateTimeOffset(DateTime.SpecifyKind(t, t.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : t.Kind)).ToUnixTimeMilliseconds();

    public static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    public static string Iso(long ms) => FromMs(ms).ToString("yyyy-MM-ddTHH:mm:ssZ");

    public static long DayIndex(long ms) => Math.DivRem(ms, 86_400_000L, out var rem) - (rem < 0 ? 1 : 0);

    /// <summary>UTC hour of day (0–23) of a timestamp.</summary>
    public static int UtcHour(long ms) => (int)((ms - DayIndex(ms) * 86_400_000L) / 3_600_000L);
}
