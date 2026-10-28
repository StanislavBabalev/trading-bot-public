using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backtester.Core.Analytics;

namespace Backtester.Core.Reporting;

/// <summary>A saved run (result.json + equity.csv + trades.csv) read back from disk.</summary>
public sealed record RunArtifacts(
    string Dir,
    JsonObject Result,
    string Strategy,
    IReadOnlyDictionary<string, double> Parameters,
    BacktestConfig Config,
    PerformanceMetrics Metrics,
    PerformanceMetrics? Benchmark,
    IReadOnlyList<EquityPoint> Equity,
    IReadOnlyList<Trade> Trades)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool Exists(string dir) => File.Exists(Path.Combine(dir, "result.json"));

    public static RunArtifacts Load(string dir)
    {
        var result = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "result.json")))!.AsObject();
        var parameters = result["parameters"] is JsonObject p
            ? p.ToDictionary(kv => kv.Key, kv => Json.Number(kv.Value))
            : new Dictionary<string, double>();
        var config = result["config"].Deserialize<BacktestConfig>(Json.Options)!;
        var metrics = result["metrics"].Deserialize<PerformanceMetrics>(Json.Options)!;
        var bench = result["buyAndHold"] is JsonObject b ? b.Deserialize<PerformanceMetrics>(Json.Options) : null;

        return new RunArtifacts(dir, result, result["strategy"]?.GetValue<string>() ?? "?", parameters, config, metrics, bench,
            ReadEquity(Path.Combine(dir, "equity.csv")), ReadTrades(Path.Combine(dir, "trades.csv")));
    }

    public static List<EquityPoint> ReadEquity(string path)
    {
        var list = new List<EquityPoint>();
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = line.Split(',');
            list.Add(new EquityPoint(Ms(f[0]), double.Parse(f[1], Inv), double.Parse(f[2], Inv)));
        }
        return list;
    }

    public static List<Trade> ReadTrades(string path)
    {
        var list = new List<Trade>();
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = line.Split(',');
            list.Add(new Trade(
                int.Parse(f[0], Inv), Ms(f[1]), double.Parse(f[2], Inv), Ms(f[3]), double.Parse(f[4], Inv),
                double.Parse(f[5], Inv), double.Parse(f[6], Inv), double.Parse(f[7], Inv), double.Parse(f[8], Inv),
                Enum.Parse<ExitReason>(f[10]), int.Parse(f[11], Inv)));
        }
        return list;
    }

    private static long Ms(string iso) => DateTimeOffset.Parse(iso, Inv, DateTimeStyles.AssumeUniversal).ToUnixTimeMilliseconds();
}
