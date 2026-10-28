using System.Text.Json.Nodes;
using Backtester.Core;
using Backtester.Core.Data;
using Backtester.Core.Engine;
using Backtester.Core.Strategies;
using Backtester.Core.Validation;
using static Backtester.Tests.TestData;

namespace Backtester.Tests;

/// <summary>The engine side of the validation harness: look-ahead audit, funding stress, holdout gate.</summary>
public class ValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bt_val_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    public static TheoryData<string> RegisteredStrategies() => new(StrategyRegistry.All.Select(d => d.Name));

    /// <summary>Every registered strategy, not only the cycle 5-11 ones the old splice test listed (AUDIT.md L2).</summary>
    [Theory]
    [MemberData(nameof(RegisteredStrategies))]
    public void Every_registered_strategy_passes_the_look_ahead_audit(string name)
    {
        var def = StrategyRegistry.Get(name);
        // A driftless random walk sits below its 200-day average most of the time (volatility drag), so the long-only
        // trend filter is audited with its 50-day setting, which trades there.
        var p = name == "dca-trend-long" ? new Dictionary<string, double>(def.Defaults) { ["maDays"] = 50 } : def.Defaults;
        var audit = LookAheadAudit.Run(def, p);
        Assert.True(audit.Verdict == "pass", $"{name}: {audit.Verdict} ({audit.TradesBeforeCut} trades) {string.Join("; ", audit.Problems)}");
        if (def.Create(p) is not ISelfSimulatingStrategy) Assert.True(audit.FundingTradesChecked > 0);
    }

    [Fact]
    public void Look_ahead_audit_catches_a_strategy_that_reads_the_next_bar()
    {
        var def = Definition("peeks-next-close", () => new PeeksNextClose());
        var audit = LookAheadAudit.Run(def, def.Defaults);
        Assert.Equal("fail", audit.Verdict);
        // Reading one bar ahead only changes the signal at the bar before the cutoff, which fills after it: trades closed
        // before the cutoff can stay identical, so the per-bar signal comparison is what catches it.
        Assert.Contains(audit.Problems, p => p.Contains($"the signal at bar {24 * 600 + 6} ") && p.Contains("depends on data after it"));
    }

    [Fact]
    public void Look_ahead_audit_catches_an_indicator_with_a_future_window()
    {
        var def = Definition("centered-average", () => new CenteredAverage());
        var audit = LookAheadAudit.Run(def, def.Defaults);
        Assert.Equal("fail", audit.Verdict);
        Assert.Contains(audit.Problems, p => p.Contains("indicator series 'centered-sma(24)' differs"));
    }

    [Fact]
    public void Funding_stress_scales_the_rate_or_charges_it_against_either_side()
    {
        var bars = Flat(4, 100);
        var funding = new List<FundingRate> { new(T0 + 2 * Hour, 0.001) };
        double Funding(int dir, CostModel costs)
        {
            var cfg = Config(4) with { Costs = costs };
            return new BacktestEngine(bars, funding).Run(new ScriptedStrategy(i => i <= 1 ? new Signal(dir) : Signal.Flat), cfg).FundingReceived;
        }
        Assert.Equal(-10, Funding(1, new CostModel(0, 0)), 9);
        Assert.Equal(-30, Funding(1, new CostModel(0, 0, FundingScale: 3)), 9);
        Assert.Equal(30, Funding(-1, new CostModel(0, 0, FundingScale: 3)), 9);
        // Adverse: both sides pay |rate| whatever the sign of the settled rate.
        Assert.Equal(-50, Funding(1, new CostModel(0, 0, AdverseFundingRate: 0.005)), 9);
        Assert.Equal(-50, Funding(-1, new CostModel(0, 0, AdverseFundingRate: -0.005)), 9);
    }

    // ------------------------------------------------------------------ holdout gate

    private (HuntConfig Cfg, CandidateIdentity Id) GateRepo()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        Directory.CreateDirectory(Path.Combine(_root, "validation"));
        File.WriteAllText(Path.Combine(_root, "validation", "gates.py"), "# validator\n");
        var (bars, funding) = SyntheticData.Generate(new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc), 48, 3);
        string data = Path.Combine(_root, "data");
        CsvStore.WriteBars(Path.Combine(data, "BTCUSDT_60.csv"), bars);
        CsvStore.WriteFunding(Path.Combine(data, "BTCUSDT_funding.csv"), funding);
        string configPath = Path.Combine(_root, "config", "hunt.json");
        File.WriteAllText(configPath, $$"""{ "dataDir": "{{data.Replace('\\', '/')}}" }""");
        var cfg = HuntConfig.Load(configPath);
        var def = StrategyRegistry.Get("keltner-breakout");
        return (cfg, CandidateIdentity.Create(def, def.Defaults, cfg, "DATA"));
    }

    private string WriteReport(HuntConfig cfg, CandidateIdentity id, Action<JsonObject>? edit = null)
    {
        var report = new JsonObject
        {
            ["candidate"] = new JsonObject { ["id"] = id.Id },
            ["validator"] = new JsonObject
            {
                ["fingerprint"] = HoldoutGate.ValidatorFingerprint(_root),
                ["configSha256"] = HoldoutGate.TextSha256(cfg.SourcePath!),
            },
            ["gates"] = new JsonArray(HoldoutGate.RequiredGates.Select(n => (JsonNode)new JsonObject
            {
                ["name"] = n, ["hard"] = true, ["passed"] = true, ["detail"] = "ok",
            }).ToArray()),
            ["allHardGatesPassed"] = true,
        };
        edit?.Invoke(report);
        string dir = Path.Combine(_root, id.BundleDir(cfg.Validation.OutputDir));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, HoldoutGate.ReportFile);
        File.WriteAllText(path, report.ToJsonString());
        return path;
    }

    [Fact]
    public void Holdout_gate_needs_a_report_for_the_exact_candidate()
    {
        var (cfg, id) = GateRepo();
        Assert.Equal(GateStatus.Missing, HoldoutGate.Check(cfg, id, _root).Status);

        WriteReport(cfg, id);
        var pass = HoldoutGate.Check(cfg, id, _root);
        Assert.True(pass.Status == GateStatus.Passed, string.Join("; ", pass.Reasons));

        // Other parameters, code or data: a different identity, so no report.
        var other = id with { Parameters = new SortedDictionary<string, double>(id.Parameters) { ["emaLen"] = 72 } };
        Assert.Equal(GateStatus.Missing, HoldoutGate.Check(cfg, other, _root).Status);
        Assert.NotEqual(id.Id, (id with { CodeFingerprint = "other" }).Id);
        Assert.NotEqual(id.Id, (id with { DataFingerprint = "other" }).Id);
    }

    [Theory]
    [InlineData("failed-gate")]
    [InlineData("missing-gate")]
    [InlineData("validator-changed")]
    [InlineData("config-changed")]
    [InlineData("wrong-identity")]
    public void Holdout_gate_refuses_a_report_that_does_not_hold(string change)
    {
        var (cfg, id) = GateRepo();
        WriteReport(cfg, id, r =>
        {
            var gates = r["gates"]!.AsArray();
            switch (change)
            {
                case "failed-gate": gates[1]!["passed"] = false; gates[1]!["detail"] = "DSR 0.41 < 0.95"; break;
                case "missing-gate": gates.RemoveAt(0); break;
                case "wrong-identity": r["candidate"]!["id"] = "0123"; break;
            }
        });
        if (change == "validator-changed") File.AppendAllText(Path.Combine(_root, "validation", "gates.py"), "MAX_PBO = 0.9\n");
        if (change == "config-changed") File.AppendAllText(cfg.SourcePath!, "\n");

        var d = HoldoutGate.Check(cfg, id, _root);
        Assert.Equal(GateStatus.Failed, d.Status);
        string reasons = string.Join("; ", d.Reasons);
        Assert.Contains(change switch
        {
            "failed-gate" => "gate 'deflated_sharpe' failed: DSR 0.41 < 0.95",
            "missing-gate" => "gate 'lookahead_audit' is missing",
            "validator-changed" => "validator code changed",
            "config-changed" => "config/hunt.json changed",
            _ => "different candidate identity",
        }, reasons);
    }

    [Fact]
    public void Validator_fingerprint_ignores_tests_and_caches_but_not_validator_code()
    {
        GateRepo();
        string before = HoldoutGate.ValidatorFingerprint(_root);
        Directory.CreateDirectory(Path.Combine(_root, "validation", "tests"));
        File.WriteAllText(Path.Combine(_root, "validation", "tests", "test_x.py"), "x = 1\n");
        Directory.CreateDirectory(Path.Combine(_root, "validation", "__pycache__"));
        File.WriteAllText(Path.Combine(_root, "validation", "__pycache__", "gates.py"), "cached\n");
        Assert.Equal(before, HoldoutGate.ValidatorFingerprint(_root));
        File.WriteAllText(Path.Combine(_root, "validation", "stats.py"), "y = 2\n");
        Assert.NotEqual(before, HoldoutGate.ValidatorFingerprint(_root));
    }

    /// <summary>Same tree and value as FingerprintTests in validation/tests/test_lock_and_network.py.</summary>
    [Fact]
    public void Validator_fingerprint_matches_the_python_vector()
    {
        Directory.CreateDirectory(Path.Combine(_root, "validation", "sub"));
        Directory.CreateDirectory(Path.Combine(_root, "validation", "tests"));
        Directory.CreateDirectory(Path.Combine(_root, "validation", "__pycache__"));
        File.WriteAllBytes(Path.Combine(_root, "validation", "a.py"), "x = 1\n"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_root, "validation", "sub", "b.py"), "y = 2\n"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_root, "validation", "tests", "t.py"), "ignored\n"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_root, "validation", "__pycache__", "c.py"), "ignored\n"u8.ToArray());
        Assert.Equal("c82e5801b91a26aa07003dcef2a8da4ac1fb3645af7787f85b8e8d9ad30cdb96", HoldoutGate.ValidatorFingerprint(_root));
        // A checkout with CRLF line endings hashes the same.
        File.WriteAllBytes(Path.Combine(_root, "validation", "a.py"), "x = 1\r\n"u8.ToArray());
        Assert.Equal("c82e5801b91a26aa07003dcef2a8da4ac1fb3645af7787f85b8e8d9ad30cdb96", HoldoutGate.ValidatorFingerprint(_root));
    }

    [Fact]
    public void Holdout_access_is_logged_with_the_code_and_data_state()
    {
        var (cfg, id) = GateRepo();
        WriteReport(cfg, id);
        var decision = HoldoutGate.Check(cfg, id, _root);
        HoldoutGate.LogAccess(cfg, cfg.Split.OosFrom, cfg.Split.OosTo, "backtest keltner-breakout", decision, _root);
        var fields = File.ReadAllText(Path.Combine(_root, HoldoutGate.AccessLog)).TrimEnd().Split('\t');
        Assert.Equal("2024-01-01..2026-10-26", fields[1]);
        Assert.Equal("backtest keltner-breakout", fields[2]);
        Assert.StartsWith("code=", fields[3]);
        Assert.Equal($"source_sha256={HoldoutGate.SourceTreeFingerprint(_root)}", fields[4]);
        Assert.Equal("git_head=none", fields[5]);
        Assert.Equal($"bars_sha256={HoldoutGate.FileSha256(cfg.BarsPath)}", fields[6]);
        Assert.Equal($"validation_report_sha256={decision.ReportSha256}", fields[8]);
    }

    // ------------------------------------------------------------------ deliberately broken strategies

    private static StrategyDefinition Definition(string name, Func<IStrategy> create) =>
        new(name, "test", "deliberate look-ahead", new Dictionary<string, double>(), [], _ => create());

    /// <summary>Goes long when the next bar closes higher — reads the future through a shared feature series.</summary>
    private sealed class PeeksNextClose : IStrategy
    {
        public string Name => "peeks-next-close";

        public Signal OnBarClose(StrategyContext ctx)
        {
            var closes = ctx.Features.Get("close", b => b.Select(x => x.Close).ToArray());
            if (ctx.Index + 1 >= closes.Length) return Signal.Flat;
            double c = ctx.Bar.Close;
            return closes[ctx.Index + 1] > c ? Signal.Long(c * 0.98) : Signal.Short(c * 1.02);
        }
    }

    /// <summary>Trades a centred 24-bar average: each value averages 12 bars back and 12 bars forward.</summary>
    private sealed class CenteredAverage : IStrategy
    {
        public string Name => "centered-average";

        public Signal OnBarClose(StrategyContext ctx)
        {
            var sma = ctx.Features.Get("centered-sma(24)", b => b.Select((_, i) =>
                i < 12 || i + 12 > b.Length ? double.NaN : b.Skip(i - 12).Take(24).Average(x => x.Close)).ToArray());
            double c = ctx.Bar.Close, s = sma[ctx.Index];
            if (double.IsNaN(s)) return Signal.Flat;
            return c > s ? Signal.Long(c * 0.98) : Signal.Short(c * 1.02);
        }
    }
}
