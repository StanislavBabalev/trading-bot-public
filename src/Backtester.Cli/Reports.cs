using System.Text.Json.Nodes;
using Backtester.Core;
using Backtester.Core.Analytics;
using Backtester.Core.Data;
using Backtester.Core.Engine;
using Backtester.Core.Reporting;

namespace Backtester.Cli;

/// <summary>Writes report.html next to saved artifacts and keeps artifacts/index.html up to date.</summary>
internal sealed class ReportWriter
{
    private const string Root = "artifacts";
    private readonly HuntConfig _cfg;
    private IReadOnlyList<Bar>? _bars;

    public ReportWriter(HuntConfig cfg, IReadOnlyList<Bar>? bars = null)
    {
        _cfg = cfg;
        _bars = bars;
    }

    private static string IndexPath => Path.Combine(Root, "index.html");
    private static string HuntRoot => Path.Combine(Root, "hunt");
    public static string HuntOverviewPath => Path.Combine(HuntRoot, "index.html");

    /// <summary>Buy-and-hold over the same window and costs, if the market data is on disk.</summary>
    private BacktestResult? BuyAndHold(BacktestConfig c)
    {
        if (_bars is null)
        {
            if (!File.Exists(_cfg.BarsPath)) return null;
            _bars = CsvStore.Normalize(CsvStore.ReadBars(_cfg.BarsPath));
        }
        try { return Benchmark.BuyAndHold(_bars, c); }
        catch (InvalidOperationException) { return null; }
    }

    public string WriteRun(string runDir)
    {
        var run = RunArtifacts.Load(runDir);
        var path = Path.Combine(runDir, "report.html");
        File.WriteAllText(path, HtmlReport.RunPage(run, BuyAndHold(run.Config), Back(runDir)));
        return path;
    }

    public string WriteWalkForward(string wfDir)
    {
        var wf = JsonNode.Parse(File.ReadAllText(Path.Combine(wfDir, "walkforward.json")))!.AsObject();
        var path = Path.Combine(wfDir, "report.html");
        File.WriteAllText(path, HtmlReport.WalkForwardPage(wf, Back(wfDir)));
        return path;
    }

    /// <summary>Writes the cycle page plus a run page for every run it references.</summary>
    public string WriteCycle(string cycleDir)
    {
        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(cycleDir, "report.json")))!.AsObject();
        var runs = new Dictionary<string, RunArtifacts?>();
        RunArtifacts? Load(string p)
        {
            if (!runs.TryGetValue(p, out var r))
                runs[p] = r = RunArtifacts.Exists(p) ? RunArtifacts.Load(p) : null;
            return r;
        }
        string? Href(string runDir)
        {
            if (!RunArtifacts.Exists(runDir)) return null;
            WriteRun(runDir);
            return Rel(cycleDir, Path.Combine(runDir, "report.html"));
        }
        var path = Path.Combine(cycleDir, "report.html");
        File.WriteAllText(path, HtmlReport.CyclePage(report, Load, BuyAndHold, Href, Back(cycleDir)));
        return path;
    }

    /// <summary>Regenerates every report under artifacts/ and the index. Returns the number of pages written.</summary>
    public int WriteAll()
    {
        int n = 0;
        if (!Directory.Exists(Root)) return 0;
        foreach (var dir in Directory.EnumerateFiles(Root, "result.json", SearchOption.AllDirectories).Select(Path.GetDirectoryName))
        {
            WriteRun(dir!);
            n++;
        }
        foreach (var file in Directory.EnumerateFiles(Root, "walkforward.json", SearchOption.AllDirectories))
        {
            WriteWalkForward(Path.GetDirectoryName(file)!);
            n++;
        }
        var huntRoot = Path.Combine(Root, "hunt");
        if (Directory.Exists(huntRoot))
            foreach (var file in Directory.EnumerateFiles(huntRoot, "report.json", SearchOption.AllDirectories))
            {
                WriteCycle(Path.GetDirectoryName(file)!);
                n++;
            }
        RefreshIndex();
        return n;
    }

    /// <summary>
    /// The research notes covering a cycle: the latest cycle_K_ideas notes (research.md, or cycle 4's shortlist.md) with
    /// K at or before it, since one sprint's notes cover several cycles. Paths are as found under <paramref name="huntRoot"/>.
    /// </summary>
    internal static Func<int, string?> ResearchNotes(string huntRoot)
    {
        var notes = !Directory.Exists(huntRoot) ? [] : Directory.EnumerateDirectories(huntRoot, "cycle_*_ideas")
            .Select(d => (Cycle: int.TryParse(Path.GetFileName(d).Split('_')[1], out int k) ? k : -1,
                          Md: new[] { "research.md", "shortlist.md" }.Select(f => Path.Combine(d, f)).FirstOrDefault(File.Exists)))
            .Where(x => x.Cycle >= 0 && x.Md is not null)
            .ToList();
        return cycle => notes.Where(n => n.Cycle <= cycle).OrderByDescending(n => n.Cycle).Select(n => n.Md).FirstOrDefault();
    }

    /// <summary>Writes artifacts/hunt/index.html: every hypothesis of every hunt cycle on one page. Null when no cycle has run.</summary>
    public string? WriteHuntOverview()
    {
        if (!Directory.Exists(HuntRoot)) return null;
        var notes = ResearchNotes(HuntRoot);
        var cycles = new List<HtmlReport.CycleEntry>();
        foreach (var json in Directory.EnumerateFiles(HuntRoot, "report.json", SearchOption.AllDirectories))
        {
            var report = JsonNode.Parse(File.ReadAllText(json))!.AsObject();
            string? note = notes(report["cycle"]?.GetValue<int>() ?? 0);
            cycles.Add(new(report, Rel(HuntRoot, Path.Combine(Path.GetDirectoryName(json)!, "report.html")), note is null ? null : Rel(HuntRoot, note)));
        }
        if (cycles.Count == 0) return null;
        File.WriteAllText(HuntOverviewPath, HtmlReport.HuntOverviewPage(cycles, Back(HuntRoot)));
        return HuntOverviewPath;
    }

    /// <summary>Rewrites the hunt overview and artifacts/index.html, which lists the overview first.</summary>
    public string RefreshIndex()
    {
        var entries = new List<HtmlReport.IndexEntry>();
        if (WriteHuntOverview() is { } overview)
            entries.Add(new("Hunt overview", "All hunt cycles", Rel(Root, overview), "every hypothesis from every cycle, best development Sharpe first", File.GetLastWriteTimeUtc(overview)));
        if (Directory.Exists(Root))
            foreach (var html in Directory.EnumerateFiles(Root, "report.html", SearchOption.AllDirectories))
            {
                var dir = Path.GetDirectoryName(html)!;
                var href = Rel(Root, html);
                var modified = File.GetLastWriteTimeUtc(html);
                if (File.Exists(Path.Combine(dir, "report.json")))
                {
                    var r = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "report.json")))!;
                    var champ = r["champion"]?["strategy"]?.GetValue<string>();
                    int hyps = (r["hypotheses"] as JsonArray)?.Count ?? 0;
                    entries.Add(new("Hunt cycles", $"Hunt cycle {r["cycle"]}", href, $"{hyps} hypotheses · champion: {champ ?? "none"}", modified));
                }
                else if (File.Exists(Path.Combine(dir, "walkforward.json")))
                {
                    var w = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "walkforward.json")))!;
                    entries.Add(new("Walk-forward", $"{w["strategy"]}", href, $"{w["foldsFailed"]} of {(w["folds"] as JsonArray)?.Count} folds failed", modified));
                }
                else if (RunArtifacts.Exists(dir))
                {
                    var res = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "result.json")))!;
                    var m = res["metrics"];
                    string net = m?["netProfitPct"] is JsonValue v && v.TryGetValue<double>(out var d) ? $"{d:F2}%" : "–";
                    string from = m?["from"]?.GetValue<string>() is { Length: >= 10 } f ? f[..10] : "?";
                    string to = m?["to"]?.GetValue<string>() is { Length: >= 10 } t ? t[..10] : "?";
                    entries.Add(new("Runs", $"{res["strategy"]}", href, $"{from} → {to} · net {net} · {Rel(Root, dir)}", modified));
                }
            }
        Directory.CreateDirectory(Root);
        File.WriteAllText(IndexPath, HtmlReport.IndexPage(entries));
        return IndexPath;
    }

    private static string Back(string fromDir) => Rel(fromDir, IndexPath);

    private static string Rel(string fromDir, string to) => Path.GetRelativePath(fromDir, to).Replace('\\', '/');
}
