using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backtester.Core.Engine;
using Backtester.Core.Reporting;

namespace Backtester.Core.Research;

/// <summary>
/// Everything that determines a backtest's output. Equal identities produce identical metrics, so a stored result
/// may be reused only when its <see cref="Id"/> matches. <see cref="CodeFingerprint"/> changes with any change to
/// Backtester.Core (engine, indicators, strategies, metrics), so results never survive a code change; the data
/// fingerprint does the same for the bars and funding rates.
/// </summary>
public sealed record ExperimentIdentity(
    string Strategy,
    int StrategyVersion,
    SortedDictionary<string, double> Parameters,
    string Symbol,
    int IntervalMinutes,
    string DataFingerprint,
    long FromMs,
    long ToMs,
    double InitialCapital,
    CostModel Costs,
    bool FundingEnabled,
    SizingConfig Sizing,
    RiskLimits Risk,
    string EngineVersion,
    string CodeFingerprint)
{
    /// <summary>The Backtester.Core build: deterministic builds keep it stable until the source changes.</summary>
    public static readonly string CurrentCodeFingerprint = typeof(BacktestEngine).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..16];

    public static ExperimentIdentity Create(
        string strategy, int strategyVersion, IReadOnlyDictionary<string, double> parameters,
        string symbol, string dataFingerprint, BacktestConfig cfg) =>
        new(strategy, strategyVersion, new SortedDictionary<string, double>(parameters.ToDictionary(), StringComparer.Ordinal),
            symbol, cfg.BarIntervalMinutes, dataFingerprint, cfg.FromMs, cfg.ToMs, cfg.InitialCapital,
            cfg.Costs, cfg.FundingEnabled, cfg.Sizing, cfg.Risk, BacktestEngine.Version, CurrentCodeFingerprint);

    /// <summary>SHA-256 (first 128 bits, hex) of the canonical JSON of every field.</summary>
    [JsonIgnore]
    public string Id
    {
        get
        {
            // Re-sort ordinally: a deserialized SortedDictionary comes back with the default (culture) comparer.
            var canonical = this with { Parameters = new SortedDictionary<string, double>(Parameters, StringComparer.Ordinal) };
            var json = JsonSerializer.Serialize(canonical, Json.Compact);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32];
        }
    }
}
