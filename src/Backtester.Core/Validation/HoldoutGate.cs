using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backtester.Core.Engine;
using Backtester.Core.Reporting;
using Backtester.Core.Research;
using Backtester.Core.Strategies;

namespace Backtester.Core.Validation;

/// <summary>
/// Exactly what a validation report vouches for: one strategy version and parameter set, evaluated on the development
/// window with these constants, this Backtester.Core build and this data. Any change gives a new <see cref="Id"/>, so a
/// report never carries over to code, data or parameters it did not see.
/// </summary>
public sealed record CandidateIdentity(
    string Strategy,
    int StrategyVersion,
    SortedDictionary<string, double> Parameters,
    string Symbol,
    int IntervalMinutes,
    long DevFromMs,
    long DevToMs,
    double InitialCapital,
    CostModel Costs,
    bool FundingEnabled,
    SizingConfig Sizing,
    RiskLimits Risk,
    string EngineVersion,
    string CodeFingerprint,
    string DataFingerprint)
{
    public static CandidateIdentity Create(StrategyDefinition def, IReadOnlyDictionary<string, double> parameters, HuntConfig cfg, string dataFingerprint) =>
        new(def.Name, def.Version, new SortedDictionary<string, double>(parameters.ToDictionary(), StringComparer.Ordinal),
            cfg.Symbol, cfg.IntervalMinutes, TimeUtil.ToMs(cfg.Split.DevFrom), TimeUtil.ToMs(cfg.Split.DevTo), cfg.InitialCapital,
            cfg.Costs, cfg.FundingEnabled, cfg.Sizing, cfg.Risk, BacktestEngine.Version, ExperimentIdentity.CurrentCodeFingerprint, dataFingerprint);

    /// <summary>SHA-256 (first 128 bits, lowercase hex) of the canonical JSON of every field.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Id
    {
        get
        {
            var canonical = this with { Parameters = new SortedDictionary<string, double>(Parameters, StringComparer.Ordinal) };
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical, Json.Compact))))[..32];
        }
    }

    /// <summary>The candidate's validation bundle: export files from <c>validate-export</c> and the Python report.</summary>
    public string BundleDir(string outputDir) => Path.Combine(outputDir, Strategy, Id[..12]);
}

public enum GateStatus { Passed, Missing, Failed }

public sealed record GateDecision(GateStatus Status, IReadOnlyList<string> Reasons, string ReportPath, string? ReportSha256);

/// <summary>
/// The holdout gate: the locked out-of-sample window may only be run for a candidate whose validation report
/// (<c>python -m validation</c>) exists for its exact identity, was written by the current validator code and config,
/// and shows every hard gate passed. Every holdout run is appended to artifacts/oos_access.log with a timestamp and the
/// code and data state.
/// </summary>
public static class HoldoutGate
{
    public const string ReportFile = "validation_report.json";
    public const string AccessLog = "artifacts/oos_access.log";

    /// <summary>Gates a report must contain; a report missing any of them is treated as failed, not as passed.</summary>
    public static readonly IReadOnlyList<string> RequiredGates =
    [
        "lookahead_audit", "deflated_sharpe", "pbo", "plateau_score", "plateau_edge", "regime_losses", "regime_concentration",
        "bootstrap_sharpe_p05", "bootstrap_pf_p05", "stress_slippage_2x", "stress_commission_2x", "min_track_record", "trades_per_year",
    ];

    /// <summary>Checks the candidate's report; the output dir and the validator sources are resolved under <paramref name="repoRoot"/>.</summary>
    public static GateDecision Check(HuntConfig cfg, CandidateIdentity id, string repoRoot = ".")
    {
        string path = Path.Combine(repoRoot, id.BundleDir(cfg.Validation.OutputDir), ReportFile);
        if (!File.Exists(path))
            return new(GateStatus.Missing, [$"no validation report for {id.Strategy} {Describe(id.Parameters)} at this code and data state (expected {path.Replace('\\', '/')})"], path, null);

        var bytes = File.ReadAllBytes(path);
        var reasons = new List<string>();
        JsonObject report;
        try { report = JsonNode.Parse(bytes)!.AsObject(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return new(GateStatus.Failed, [$"unreadable validation report {path}: {e.Message}"], path, null);
        }

        if (report["candidate"]?["id"]?.GetValue<string>() != id.Id) reasons.Add("the report is for a different candidate identity");
        string validator = ValidatorFingerprint(repoRoot);
        if (!string.Equals(report["validator"]?["fingerprint"]?.GetValue<string>(), validator, StringComparison.OrdinalIgnoreCase))
            reasons.Add("the validator code changed since the report was written; re-run python -m validation");
        string? configHash = cfg.SourcePath is { } cp && File.Exists(cp) ? TextSha256(cp) : null;
        if (configHash is null || !string.Equals(report["validator"]?["configSha256"]?.GetValue<string>(), configHash, StringComparison.OrdinalIgnoreCase))
            reasons.Add("config/hunt.json changed since the report was written; re-run python -m validation");

        var gates = (report["gates"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var names = gates.Select(g => g["name"]?.GetValue<string>()).ToHashSet();
        foreach (var missing in RequiredGates.Where(n => !names.Contains(n))) reasons.Add($"gate '{missing}' is missing from the report");
        foreach (var g in gates.Where(g => g["hard"]?.GetValue<bool>() == true && g["passed"]?.GetValue<bool>() != true))
            reasons.Add($"gate '{g["name"]}' failed: {g["detail"]?.GetValue<string>() ?? "(no detail)"}");
        if (report["allHardGatesPassed"]?.GetValue<bool>() != true && reasons.Count == 0)
            reasons.Add("the report does not state that every hard gate passed");

        return new(reasons.Count == 0 ? GateStatus.Passed : GateStatus.Failed, reasons, path, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>
    /// Appends one holdout access to <see cref="AccessLog"/>: UTC time, window, what ran, then the code state (Backtester.Core
    /// build, source-tree hash, git HEAD), the data file hashes and the validation report hash (or why there was none).
    /// The first three tab-separated fields keep their original meaning.
    /// </summary>
    public static void LogAccess(HuntConfig cfg, DateTime from, DateTime to, string what, GateDecision? decision, string repoRoot = ".")
    {
        string log = Path.Combine(repoRoot, AccessLog);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        string report = decision?.ReportSha256 is { } h ? $"validation_report_sha256={h}" : "validation_report=none (holdout gate disabled in config)";
        File.AppendAllText(log, string.Join('\t',
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), $"{from:yyyy-MM-dd}..{to:yyyy-MM-dd}", what,
            $"code={ExperimentIdentity.CurrentCodeFingerprint}", $"source_sha256={SourceTreeFingerprint(repoRoot)}", $"git_head={GitHead(repoRoot)}",
            $"bars_sha256={FileSha256(cfg.BarsPath)}", $"funding_sha256={FileSha256(cfg.FundingPath)}", report) + Environment.NewLine);
    }

    public static string Describe(IReadOnlyDictionary<string, double> p) =>
        string.Join(" ", p.Select(kv => $"{kv.Key}={kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));

    public static string FileSha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    /// <summary>
    /// Hash of the Python validator (validation/**/*.py, tests and caches excluded). The Python side computes the same value:
    /// SHA-256 over the files sorted by relative path, each contributing its UTF-8 path, a zero byte and the SHA-256 of its
    /// bytes with CRLF read as LF.
    /// </summary>
    public static string ValidatorFingerprint(string repoRoot) =>
        TreeHash(repoRoot, Files(repoRoot, "validation", "*.py").Where(r => !r.StartsWith("validation/tests/", StringComparison.Ordinal)));

    /// <summary>The same hash over every source, project and config file (C#, Python validator, config) — the code state.</summary>
    public static string SourceTreeFingerprint(string repoRoot) =>
        TreeHash(repoRoot, Files(repoRoot, "src", "*.cs").Concat(Files(repoRoot, "src", "*.csproj")).Concat(Files(repoRoot, "validation", "*.py"))
            .Concat(Files(repoRoot, "config", "*.json")).Concat(File.Exists(Path.Combine(repoRoot, "TradingBot.slnx")) ? ["TradingBot.slnx"] : []));

    /// <summary>The commit HEAD points to, read from .git without running git; "none" outside a repository.</summary>
    public static string GitHead(string repoRoot)
    {
        string git = Path.Combine(repoRoot, ".git");
        string headFile = Path.Combine(git, "HEAD");
        if (!File.Exists(headFile)) return "none";
        string head = File.ReadAllText(headFile).Trim();
        if (!head.StartsWith("ref: ", StringComparison.Ordinal)) return head;
        string refName = head[5..];
        string loose = Path.Combine(git, refName.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(loose)) return File.ReadAllText(loose).Trim();
        string packed = Path.Combine(git, "packed-refs");
        if (File.Exists(packed))
            foreach (var line in File.ReadLines(packed))
                if (line.EndsWith(" " + refName, StringComparison.Ordinal)) return line[..line.IndexOf(' ')];
        return $"unborn ({refName})";
    }

    private static IEnumerable<string> Files(string repoRoot, string dir, string pattern)
    {
        string root = Path.Combine(repoRoot, dir);
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(repoRoot, f).Replace('\\', '/'))
            .Where(r => !r.Split('/').Any(part => part is "bin" or "obj" or "__pycache__"));
    }

    private static string TreeHash(string repoRoot, IEnumerable<string> relativePaths)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var rel in relativePaths.Distinct().OrderBy(r => r, StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(rel));
            sha.AppendData([0]);
            sha.AppendData(SHA256.HashData(LfBytes(Path.Combine(repoRoot, rel))));
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>SHA-256 of a text file with CRLF read as LF, so a checkout with other line endings keeps its reports valid.</summary>
    public static string TextSha256(string path) => Convert.ToHexStringLower(SHA256.HashData(LfBytes(path)));

    private static byte[] LfBytes(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var lf = new List<byte>(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
            if (!(bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')) lf.Add(bytes[i]);
        return lf.ToArray();
    }
}
