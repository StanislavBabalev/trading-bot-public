using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Backtester.Core;
using Backtester.Core.Data;
using Backtester.Core.Strategies;

namespace Backtester.Tests;

/// <summary>
/// Runs the real CLI (<c>research run</c>) in a temporary repository on seeded synthetic data, with relaxed kill rules so
/// both hypotheses go through optimization, walk-forward and the locked-OOS step. Checks that an interrupted and resumed
/// run, and a single-threaded run without the store, produce exactly what one uninterrupted run produces.
/// </summary>
public class CliIntegrationTests : IDisposable
{
    private static readonly string[] Queue = ["keltner-breakout", "session-momentum"];
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bt_cli_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string NewRepo(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        var (bars, funding) = SyntheticData.Generate(new DateTime(2020, 7, 1, 0, 0, 0, DateTimeKind.Utc), 24 * 914, seed: 6);   // seed 6: both queued strategies optimize to an interior plateau and reach the OOS step
        string barsPath = Path.Combine(dir, "data", "bybit", "BTCUSDT_60.csv"), fundingPath = Path.Combine(dir, "data", "bybit", "BTCUSDT_funding.csv");
        CsvStore.WriteBars(barsPath, bars);
        CsvStore.WriteFunding(fundingPath, funding);
        File.WriteAllText(Path.Combine(dir, "config", "hunt.json"), """
            {
              "symbol": "BTCUSDT", "intervalMinutes": 60, "dataDir": "data/bybit", "initialCapital": 10000,
              "costs": { "commissionRate": 0.0004, "slippageRate": 0.0002 }, "fundingEnabled": true,
              "sizing": { "mode": "RiskPerTrade", "riskPerTrade": 0.01 },
              "risk": { "maxLeverage": 1.0 },
              "hunt": {
                "hypothesesPerCycle": 1, "optimizerMaxEvaluations": 200,
                "minProfitFactor": 0, "minSharpe": -100, "maxDrawdownPct": 100, "minTradesPerYear": 0,
                "plateauMinProfitFactor": 0, "plateauMinSharpe": -100,
                "walkForward": { "trainMonths": 12, "testMonths": 3, "stepMonths": 6, "maxFoldFailures": 10, "foldMinProfitFactor": 0 }
              },
              "research": { "workers": 4 },
              "validation": { "holdoutGate": false, "fundingSpikeMonths": ["2021-05", "2022-06"] },
              "split": { "devFrom": "2021-01-01T00:00:00Z", "devTo": "2022-07-01T00:00:00Z", "oosFrom": "2022-07-01T00:00:00Z", "oosTo": "2023-01-01T00:00:00Z" }
            }
            """);
        var tested = new JsonArray(StrategyRegistry.All.Select(d => d.Name).Where(n => !Queue.Contains(n)).Select(n => (JsonNode)n).ToArray());
        var log = new JsonObject
        {
            ["schema_version"] = 1,
            ["cumulative_trial_count"] = 0,
            ["champion"] = null,
            ["runner_ups"] = new JsonArray(),
            ["rejected"] = new JsonArray(),
            ["cycles"] = new JsonArray(new JsonObject
            {
                ["cycle"] = 1,
                ["result"] = "AUDIT PASSED",
                ["checks"] = new JsonObject
                {
                    ["data_btcusdt_1h"] = new JsonObject { ["file_sha256"] = Sha256(barsPath) },
                    ["funding_rate_data"] = new JsonObject { ["file_sha256"] = Sha256(fundingPath) },
                },
            }),
            ["tested_strategies"] = tested,
        };
        File.WriteAllText(Path.Combine(dir, "hunt_log.json"), log.ToJsonString());
        return dir;
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static (int Exit, string Output) Cli(string dir, params string[] args)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "Backtester.Cli.dll");
        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(dll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000)) { p.Kill(); throw new TimeoutException(string.Join(" ", args)); }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>Everything a run records, minus wall-clock timestamps.</summary>
    private static string Record(string dir)
    {
        static string Strip(string s) => string.Join("\n", s.Split('\n').Where(l => !l.Contains("timestamp_utc") && !l.Contains("timestampUtc")));
        var parts = new List<string> { Strip(File.ReadAllText(Path.Combine(dir, "hunt_log.json"))), File.ReadAllText(Path.Combine(dir, "artifacts", "hunt", "trials.jsonl")) };
        foreach (var c in Directory.GetDirectories(Path.Combine(dir, "artifacts", "hunt"), "cycle_*").Order())
            parts.Add(Strip(File.ReadAllText(Path.Combine(c, "report.json"))));
        var oos = Path.Combine(dir, "artifacts", "oos_access.log");
        parts.Add(File.Exists(oos) ? string.Join("\n", File.ReadAllLines(oos).Select(l => l[(l.IndexOf('\t') + 1)..])) : "");
        return string.Join("\n----\n", parts);
    }

    [Fact]
    public void Interrupted_resumed_and_single_threaded_runs_record_exactly_what_one_run_records()
    {
        // Reference: one uninterrupted run over the whole queue.
        string a = NewRepo("a");
        var (exitA, outA) = Cli(a, "research", "run");
        Assert.True(exitA == 0, outA);
        Assert.Contains("hypothesis queue empty", outA);
        var logA = JsonNode.Parse(File.ReadAllText(Path.Combine(a, "hunt_log.json")))!;
        Assert.Equal(3, logA["cycles"]!.AsArray().Count);
        Assert.True(Json(logA["cumulative_trial_count"]) > 2, "both hypotheses should have been optimized");
        var stages = logA["cycles"]!.AsArray().Skip(1).Select(c => $"{c!["hypotheses"]![0]!["strategy"]}: {c["hypotheses"]![0]!["stage"]} {c["hypotheses"]![0]!["reasons"]!.ToJsonString()}").ToList();
        var oosLines = File.Exists(Path.Combine(a, "artifacts", "oos_access.log")) ? File.ReadAllLines(Path.Combine(a, "artifacts", "oos_access.log")) : [];
        Assert.True(oosLines.Length == 2, $"each candidate should reach its single locked-OOS run: {string.Join(" | ", stages)}");
        Assert.True(File.Exists(Path.Combine(a, "artifacts", "research", "brief.md")));

        // Interrupted twice by the experiment limit, then resumed to the end.
        string b = NewRepo("b");
        var (exit1, out1) = Cli(b, "research", "run", "--max-experiments", "40");
        Assert.True(exit1 == 3, out1);
        Assert.Contains("paused", out1);
        Assert.Single(JsonNode.Parse(File.ReadAllText(Path.Combine(b, "hunt_log.json")))!["cycles"]!.AsArray());   // nothing recorded mid-cycle
        Assert.False(File.Exists(Path.Combine(b, "artifacts", "hunt", "trials.jsonl")));
        var (exit2, out2) = Cli(b, "research", "run", "--max-experiments", "80");
        Assert.True(exit2 == 3, out2);
        var (exit3, out3) = Cli(b, "research", "run");
        Assert.True(exit3 == 0, out3);
        Assert.Contains("reused from the store", out3);
        Assert.Equal(Record(a), Record(b));

        // One worker, no store: same research record.
        string c = NewRepo("c");
        var (exitC, outC) = Cli(c, "research", "run", "--workers", "1", "--no-cache");
        Assert.True(exitC == 0, outC);
        Assert.False(File.Exists(Path.Combine(c, "artifacts", "cache", "experiments.jsonl")));
        Assert.Equal(Record(a), Record(c));

        // Status reflects the finished run; stop without an active run is a no-op.
        var (_, status) = Cli(a, "research", "status");
        Assert.Contains("Queue: empty", status);
        Assert.Contains("Model calls logged: 0", status);
        var (_, stop) = Cli(a, "research", "stop");
        Assert.Contains("No research run is active", stop);
        Assert.False(File.Exists(Path.Combine(a, "artifacts", "research", "STOP")));

        // Export: workbook, CSV and JSON from the saved reports; audit cycles contribute step and result only.
        var (exitE, outE) = Cli(a, "export");
        Assert.True(exitE == 0, outE);
        string export = Path.Combine(a, "artifacts", "export");
        string fullText = File.ReadAllText(Path.Combine(export, "hunt_results.json"));
        var full = JsonNode.Parse(fullText)!;
        Assert.Equal(2, full["summary"]!["hypotheses"]!.GetValue<int>());
        Assert.Equal(2, full["summary"]!["reachedLockedOos"]!.GetValue<int>());
        Assert.Equal(1, Json(full["auditCycles"]![0]!["cycle"]));
        Assert.DoesNotContain("file_sha256", fullText);
        Assert.Equal(3, File.ReadAllLines(Path.Combine(export, "hunt_strategies.csv")).Length);   // header + 2 hypotheses
        using var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(export, "hunt_results.xlsx"));
        Assert.Equal(15, zip.Entries.Count);   // 10 sheets + content types, package rels, workbook, workbook rels, styles
    }

    [Fact]
    public void Hunt_refuses_to_run_when_the_data_changed_since_the_audit()
    {
        string d = NewRepo("d");
        File.AppendAllText(Path.Combine(d, "data", "bybit", "BTCUSDT_funding.csv"), "1700000000000,0.0001\n");
        var (exit, output) = Cli(d, "hunt");
        Assert.Equal(1, exit);
        Assert.Contains("Data files changed since the STEP 0 audit", output);
        Assert.False(File.Exists(Path.Combine(d, "artifacts", "hunt", "trials.jsonl")));
        Assert.False(File.Exists(Path.Combine(d, "artifacts", "cache", "experiments.jsonl")));
    }

    [Fact]
    public void Proposals_are_validated_against_the_hunt_rules()
    {
        string d = NewRepo("v");
        File.WriteAllText(Path.Combine(d, "proposals.json"), """
            { "hypotheses": [
              { "name": "funding-fade", "family": "funding-carry", "entry": "e", "exit": "x", "stop": "s", "rationale": "r",
                "parameters": [ { "name": "lookback", "default": 48, "min": 24, "max": 96, "step": 24 } ] },
              { "name": "keltner-breakout", "family": "f", "entry": "e", "exit": "x", "stop": "s", "rationale": "r",
                "parameters": [ { "name": "a", "default": 1, "min": 0, "max": 2, "step": 1 } ] },
              { "name": "huge-grid", "family": "f", "entry": "e", "exit": "x", "stop": "s", "rationale": "r",
                "parameters": [ { "name": "a", "default": 1, "min": 1, "max": 100, "step": 1 }, { "name": "b", "default": 1, "min": 1, "max": 10, "step": 1 } ] },
              { "name": "no-stop", "family": "f", "entry": "e", "exit": "x", "rationale": "r",
                "parameters": [ { "name": "a", "default": 1.5, "min": 0, "max": 2, "step": 1 } ] }
            ] }
            """);
        var (exit, output) = Cli(d, "research", "validate", "--file", "proposals.json");
        Assert.True(exit == 0, output);
        Assert.Contains("ACCEPTED funding-fade", output);
        Assert.Contains("REJECTED keltner-breakout: name is already registered or tested", output);
        Assert.Contains("REJECTED huge-grid: grid has 1000 points", output);
        Assert.Contains("'stop' is required", output);
        Assert.Contains("default 1.5 is not on its grid", output);
        var saved = Directory.GetFiles(Path.Combine(d, "artifacts", "research", "proposals"));
        Assert.Single(saved);
        Assert.Single(JsonNode.Parse(File.ReadAllText(saved[0]))!["hypotheses"]!.AsArray());
    }

    [Fact]
    public void Holdout_gate_pauses_the_hunt_and_refuses_unlocked_backtests_without_a_validation_report()
    {
        string g = NewRepo("g");
        string cfgPath = Path.Combine(g, "config", "hunt.json");
        File.WriteAllText(cfgPath, File.ReadAllText(cfgPath).Replace("\"holdoutGate\": false", "\"holdoutGate\": true"));

        // The first candidate passes the (relaxed) dev checks; without a validation report the cycle pauses before OOS.
        var (exit, output) = Cli(g, "research", "run");
        Assert.True(exit == 3, output);
        Assert.Contains("needs a validation report before its locked-OOS run", output);
        Assert.Contains("python -m validation run --strategies keltner-breakout", output);
        Assert.False(File.Exists(Path.Combine(g, "artifacts", "oos_access.log")));
        Assert.Single(JsonNode.Parse(File.ReadAllText(Path.Combine(g, "hunt_log.json")))!["cycles"]!.AsArray());

        var (exitB, outB) = Cli(g, "backtest", "--strategy", "keltner-breakout", "--window", "oos", "--unlock-oos");
        Assert.Equal(1, exitB);
        Assert.Contains("Holdout gate refused the locked OOS run", outB);
        Assert.Contains("no validation report for keltner-breakout", outB);
        Assert.False(File.Exists(Path.Combine(g, "artifacts", "oos_access.log")));
    }

    [Fact]
    public void Validate_export_writes_dev_only_bundles_with_stress_perturbations_and_an_audit()
    {
        string d = NewRepo("x");
        var (exit, output) = Cli(d, "validate-export", "--strategies", "keltner-breakout");
        Assert.True(exit == 0, output);
        string bundle = Directory.GetDirectories(Path.Combine(d, "artifacts", "validation", "keltner-breakout")).Single();
        foreach (var f in new[] { "meta.json", "equity.csv", "trades.csv", "stress.json", "perturbations.json", "selection.json", "selection_daily.csv", "selection_trades.csv" })
            Assert.True(File.Exists(Path.Combine(bundle, f)), f);

        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(bundle, "meta.json")))!;
        Assert.Equal("pass", meta["lookAheadAudit"]!["verdict"]!.GetValue<string>());
        long devTo = meta["window"]!["devToMs"]!.GetValue<long>();
        Assert.All(File.ReadLines(Path.Combine(bundle, "equity.csv")).Skip(1), l => Assert.True(long.Parse(l.Split(',')[0]) <= devTo));

        // emaLen 48 and bandMult 2 move by ±10/20/30% (6 values each); atrMult 3 moves to its grid neighbours 2 and 4.
        var runs = JsonNode.Parse(File.ReadAllText(Path.Combine(bundle, "perturbations.json")))!["runs"]!.AsArray();
        Assert.Equal(6 + 6 + 2, runs.Count(r => r!["kind"]!.GetValue<string>() == "1d"));
        Assert.Equal(6 * 6 + 6 * 2 + 6 * 2, runs.Count(r => r!["kind"]!.GetValue<string>() == "2d"));
        var stress = JsonNode.Parse(File.ReadAllText(Path.Combine(bundle, "stress.json")))!["scenarios"]!.AsArray()
            .Select(s => s!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["slippage_x0", "slippage_x1", "slippage_x2", "slippage_x3", "slippage_x4", "commission_x1", "commission_x2", "commission_x3",
            "funding_off", "funding_x3", "funding_spike_2021-05", "funding_spike_2022-06"], stress);

        // Perturbations are recorded apart from the hunt's trial registry.
        Assert.True(File.Exists(Path.Combine(d, "artifacts", "validation", "robustness_registry.jsonl")));
        Assert.False(File.Exists(Path.Combine(d, "artifacts", "hunt", "trials.jsonl")));
    }

    [Fact]
    public void Interval_option_reads_that_intervals_data_and_every_trial_replays_on_its_own_interval()
    {
        string d = NewRepo("iv");
        string dataDir = Path.Combine(d, "data", "bybit");
        var hourly = CsvStore.ReadBars(Path.Combine(dataDir, "BTCUSDT_60.csv"));
        CsvStore.WriteBars(Path.Combine(dataDir, "BTCUSDT_240.csv"), hourly.Chunk(4)
            .Select(c => new Bar(c[0].OpenTimeMs, c[0].Open, c.Max(b => b.High), c.Min(b => b.Low), c[^1].Close, c.Sum(b => b.Volume))).ToList());
        var defaults = StrategyRegistry.Get("keltner-breakout").Defaults;

        JsonNode Run(params string[] extra)
        {
            string runs = Path.Combine(d, "artifacts", "runs");
            var before = Directory.Exists(runs) ? Directory.GetDirectories(runs).ToHashSet() : [];
            var (exit, output) = Cli(d, ["backtest", "--strategy", "keltner-breakout", "--window", "dev", .. extra]);
            Assert.True(exit == 0, output);
            string dir = Directory.GetDirectories(runs).Single(x => !before.Contains(x));
            return JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "result.json")))!;
        }
        var h1 = Run();
        var h4 = Run("--interval", "240");
        Assert.Equal(60, h1["config"]!["barIntervalMinutes"]!.GetValue<int>());
        Assert.Equal(240, h4["config"]!["barIntervalMinutes"]!.GetValue<int>());
        Assert.NotEqual(h1["metrics"]!["trades"]!.GetValue<int>(), h4["metrics"]!["trades"]!.GetValue<int>());

        string Trial(int id, JsonNode run, int? interval)
        {
            var m = run["metrics"]!;
            var t = new JsonObject
            {
                ["id"] = id, ["cycle"] = 1, ["strategy"] = "keltner-breakout", ["stage"] = "baseline",
                ["parameters"] = new JsonObject(defaults.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                ["dailySharpe"] = m["dailySharpe"]!.DeepClone(), ["sharpe"] = m["sharpe"]!.DeepClone(),
                ["profitFactor"] = m["profitFactor"]!.DeepClone(), ["trades"] = m["trades"]!.DeepClone(), ["source"] = "test",
            };
            if (interval is int i) t["interval"] = i;
            return t.ToJsonString();
        }
        string registry = Path.Combine(d, "artifacts", "hunt", "trials.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        File.WriteAllLines(registry, [Trial(1, h1, null), Trial(2, h4, 240)]);
        var (ok, okOut) = Cli(d, "research", "verify");
        Assert.True(ok == 0, okOut);
        Assert.Contains("2 identical, 0 different", okOut);

        // The same records with their intervals swapped no longer replay.
        File.WriteAllLines(registry, [Trial(1, h1, 240), Trial(2, h4, null)]);
        var (bad, badOut) = Cli(d, "research", "verify");
        Assert.Equal(1, bad);
        Assert.Contains("0 identical, 2 different", badOut);

        var (refused, refusedOut) = Cli(d, "backtest", "--strategy", "keltner-breakout", "--interval", "50");
        Assert.Equal(1, refused);
        Assert.Contains("must divide a day", refusedOut);
    }

    private static double Json(JsonNode? n) => Backtester.Core.Reporting.Json.Number(n);
}
