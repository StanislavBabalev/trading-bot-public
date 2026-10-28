using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Backtester.Core;
using Backtester.Core.Analytics;
using Backtester.Core.Reporting;
using Backtester.Core.Research;
using Backtester.Core.Strategies;

namespace Backtester.Cli;

/// <summary>
/// Engine, experiment store, limits and runner shared by <c>hunt</c> and <c>research run</c>. Holds an exclusive lock
/// so two runs can never write the hunt log or the store at the same time.
/// </summary>
internal sealed class ResearchSession : IDisposable
{
    private readonly FileStream _lock;

    private ResearchSession(ResearchLimits limits, ExperimentRunner runner, FileStream lockFile)
    {
        Limits = limits;
        Runner = runner;
        _lock = lockFile;
    }

    public ResearchLimits Limits { get; }
    public ExperimentRunner Runner { get; }

    public static ResearchSession Open(HuntConfig cfg, CliArgs a)
    {
        var r = cfg.Research;
        var limits = r with
        {
            MaxCyclesPerRun = a.Int("max-cycles") ?? r.MaxCyclesPerRun,
            MaxNewExperimentsPerRun = a.Int("max-experiments") ?? r.MaxNewExperimentsPerRun,
            MaxNewExperimentsPerCycle = a.Int("max-cycle-experiments") ?? r.MaxNewExperimentsPerCycle,
            MaxRuntimeMinutes = a.Double("max-minutes") ?? r.MaxRuntimeMinutes,
            Workers = a.Int("workers") ?? r.Workers,
            MaxManagedMemoryMb = a.Int("max-memory-mb") ?? r.MaxManagedMemoryMb,
        };

        Directory.CreateDirectory(Research.Dir);
        FileStream lockFile;
        try { lockFile = new FileStream(Path.Combine(Research.Dir, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException) { throw new InvalidOperationException("Another hunt or research run is active in this repository (artifacts/research/run.lock is held)."); }
        // A stop request can only be meant for a run that has ended by now.
        if (File.Exists(Research.StopFile)) File.Delete(Research.StopFile);
        // First Ctrl+C asks for a graceful stop (finish running backtests, record nothing half-done, flush the store);
        // a second one ends the process at once.
        Console.CancelKeyPress += (_, e) =>
        {
            if (File.Exists(Research.StopFile)) return;
            e.Cancel = true;
            File.WriteAllText(Research.StopFile, $"Ctrl+C {DateTime.UtcNow:O}\n");
            Console.Error.WriteLine("Stopping after the running backtests (Ctrl+C again to abort now)...");
        };

        var engine = Commands.LoadEngine(cfg);
        var store = a.Has("no-cache") ? ExperimentStore.InMemory() : ExperimentStore.Open(limits.ExperimentStore);
        if (store.SkippedLines > 0) Console.WriteLine($"note: skipped {store.SkippedLines} unreadable line(s) in {store.Path} (an interrupted write); those experiments will run again.");
        if (store.StaleLines > 0) Console.WriteLine($"note: ignored {store.StaleLines} record(s) in {store.Path} written under an older identity format; they are never reused.");
        var budget = new ExperimentBudget
        {
            MaxNewExperiments = limits.MaxNewExperimentsPerRun,
            MaxRuntime = TimeSpan.FromMinutes(limits.MaxRuntimeMinutes),
            MaxManagedMemoryBytes = limits.MaxManagedMemoryMb * 1048576L,
            StopFile = Research.StopFile,
        };
        return new ResearchSession(limits, new ExperimentRunner(engine, cfg.Symbol, store, budget, limits.ResolvedWorkers), lockFile);
    }

    public string Summary()
    {
        var b = Runner.Budget;
        var s = Runner.Store;
        return $"Experiments: {b.NewExperiments} new, {b.CacheHits} reused from the store ({s.Count} stored" +
               $"{(s.Path is null ? ", not saved: --no-cache" : $" in {s.Path}")}); {Runner.Workers} workers; {b.Elapsed.TotalSeconds:F1}s.";
    }

    public void Dispose()
    {
        Runner.Store.Dispose();
        _lock.Dispose();
    }
}

/// <summary>
/// The local research controller. <c>research run</c> loops over hunt cycles until the hypothesis queue is empty, a
/// limit is reached or a stop is requested, then writes a sanitized brief. Everything runs locally: no network
/// access, no model calls. The brief is what a person (or, if they choose, a model) reads to propose the next hypotheses.
/// </summary>
internal static class Research
{
    public static readonly string Dir = Path.Combine("artifacts", "research");
    public static readonly string StopFile = Path.Combine(Dir, "STOP");
    private static readonly string StatusFile = Path.Combine(Dir, "status.json");
    private static readonly string CallsFile = Path.Combine(Dir, "model_calls.jsonl");

    public static int Run(CliArgs a) => a.Sub switch
    {
        "run" or "resume" => RunLoop(a),
        "status" => Status(a),
        "stop" => Stop(),
        "brief" => Brief(a),
        "verify" => Verify(a),
        "log-call" => LogCall(a),
        "validate" => Validate(a),
        _ => throw new ArgumentException("Usage: research run|resume|status|stop|brief|verify|log-call|validate (see the main usage text)"),
    };

    private static int RunLoop(CliArgs a)
    {
        var cfg = Commands.LoadConfig(a);
        string logPath = a.Get("log") ?? "hunt_log.json";
        Hunt.RequireAuditedData(ReadLog(logPath)["cycles"]!.AsArray(), cfg);
        using var session = ResearchSession.Open(cfg, a);
        var started = DateTime.UtcNow;
        var completed = new List<CycleOutcome>();
        string state, reason;
        WriteStatus("running", "in progress", started, null, completed, session, logPath);

        while (true)
        {
            var queue = Hunt.Untested(ReadLog(logPath));
            if (queue.Count == 0) { state = "idle"; reason = "hypothesis queue empty: every registered strategy has been tested"; break; }
            if (completed.Count >= session.Limits.MaxCyclesPerRun) { state = "paused"; reason = $"limit of {session.Limits.MaxCyclesPerRun} cycles per run reached"; break; }

            var outcome = Hunt.RunCycle(cfg, logPath, null, session.Runner, session.Limits.MaxNewExperimentsPerCycle);
            if (!outcome.Completed) { state = "paused"; reason = outcome.PauseReason!; break; }
            completed.Add(outcome);
            WriteStatus("running", "in progress", started, null, completed, session, logPath);
        }
        bool stopRequested = File.Exists(StopFile);
        if (stopRequested) { File.Delete(StopFile); state = "stopped"; }

        var (brief, _, tokens) = ResearchBrief.Write(cfg, session.Runner.Engine, ReadLog(logPath), session.Runner.Store);
        WriteStatus(state, reason, started, DateTime.UtcNow, completed, session, logPath);

        Console.WriteLine($"\nResearch run {state}: {reason}.");
        Console.WriteLine($"Cycles completed this run: {completed.Count}" +
                          (completed.Count > 0 ? $" ({string.Join(", ", completed.Select(c => c.Cycle))}); trials {completed[0].TrialsBefore} -> {completed[^1].TrialsAfter}" : ""));
        Console.WriteLine(session.Summary());
        Console.WriteLine($"Brief: {brief} (~{tokens} tokens, estimated). Model calls made by this run: 0.");
        if (state == "paused") Console.WriteLine("Resume with: research run");
        return state == "paused" ? 3 : 0;
    }

    private static void WriteStatus(string state, string reason, DateTime started, DateTime? finished, List<CycleOutcome> cycles, ResearchSession s, string logPath)
    {
        var log = ReadLog(logPath);
        AtomicFile.WriteAllText(StatusFile, JsonSerializer.Serialize(new
        {
            state,
            reason,
            pid = Environment.ProcessId,
            startedUtc = started,
            finishedUtc = finished,
            cyclesCompleted = cycles.Select(c => new { c.Cycle, c.Strategies, c.TrialsAfter, c.LockedOosRuns, c.Champion, c.ReportPath }),
            experiments = new { created = s.Runner.Budget.NewExperiments, reused = s.Runner.Budget.CacheHits, stored = s.Runner.Store.Count },
            queue = Hunt.Untested(log),
            cumulativeTrials = Json.Number(log["cumulative_trial_count"]),
            limits = s.Limits,
            workers = s.Runner.Workers,
            modelCallsThisRun = 0,
        }, Json.Options));
    }

    private static int Status(CliArgs a)
    {
        string logPath = a.Get("log") ?? "hunt_log.json";
        var log = ReadLog(logPath);
        var cycles = log["cycles"]!.AsArray();
        Console.WriteLine($"Hunt log: {cycles.Count} entries, cumulative trials {Json.Number(log["cumulative_trial_count"])}, " +
                          $"champion {log["champion"]?["strategy"]?.GetValue<string>() ?? "none"}");
        var queue = Hunt.Untested(log);
        Console.WriteLine($"Queue: {(queue.Count == 0 ? "empty (no untested strategies registered)" : string.Join(", ", queue))}");
        if (File.Exists(StatusFile))
        {
            var s = JsonNode.Parse(File.ReadAllText(StatusFile))!;
            string state = s["state"]!.GetValue<string>();
            if (state == "running" && !Alive((int)Json.Number(s["pid"]))) state = "interrupted (process gone; run 'research run' to resume)";
            Console.WriteLine($"Last run: {state}: {s["reason"]}; started {s["startedUtc"]}, finished {s["finishedUtc"]?.ToString() ?? "-"}");
            Console.WriteLine($"  experiments created {s["experiments"]!["created"]}, reused {s["experiments"]!["reused"]}, stored {s["experiments"]!["stored"]}");
        }
        else Console.WriteLine("Last run: none recorded.");
        var cfg = Commands.LoadConfig(a);
        if (File.Exists(cfg.Research.ExperimentStore))
            Console.WriteLine($"Experiment store: {File.ReadLines(cfg.Research.ExperimentStore).Count(l => !string.IsNullOrWhiteSpace(l))} records in {cfg.Research.ExperimentStore}");
        var (calls, inTok, outTok, est) = Calls();
        Console.WriteLine($"Model calls logged: {calls} ({inTok} input / {outTok} output tokens{(est > 0 ? $", {est} call(s) estimated" : "")})");
        Console.WriteLine($"OOS accesses logged: {(File.Exists(Path.Combine("artifacts", "oos_access.log")) ? File.ReadLines(Path.Combine("artifacts", "oos_access.log")).Count() : 0)}");
        return 0;
    }

    private static int Stop()
    {
        if (!File.Exists(StatusFile) || JsonNode.Parse(File.ReadAllText(StatusFile))!["state"]?.GetValue<string>() != "running"
            || !Alive((int)Json.Number(JsonNode.Parse(File.ReadAllText(StatusFile))!["pid"])))
        {
            Console.WriteLine("No research run is active.");
            return 0;
        }
        Directory.CreateDirectory(Dir);
        File.WriteAllText(StopFile, $"stop requested {DateTime.UtcNow:O}\n");
        Console.WriteLine("Stop requested: the run pauses before its next experiment; completed experiments stay stored. Resume with: research run");
        return 0;
    }

    private static int Brief(CliArgs a)
    {
        var cfg = Commands.LoadConfig(a);
        var engine = Commands.LoadEngine(cfg);
        using var store = ExperimentStore.InMemory();
        var (brief, diagnostics, tokens) = ResearchBrief.Write(cfg, engine, ReadLog(a.Get("log") ?? "hunt_log.json"), store);
        Console.WriteLine($"Brief:       {brief} (~{tokens} tokens, estimated as characters / 4)");
        Console.WriteLine($"Diagnostics: {diagnostics}");
        return 0;
    }

    /// <summary>Re-runs every registered trial with the current code and data and compares it with what was recorded.</summary>
    private static int Verify(CliArgs a)
    {
        var cfg = Commands.LoadConfig(a);
        var engines = Commands.DevEngines(cfg);
        var trials = File.ReadLines(Path.Combine("artifacts", "hunt", "trials.jsonl")).Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<TrialRecord>(l, Json.Options)!).ToList();
        foreach (int m in trials.Select(t => t.IntervalMinutes).Distinct()) engines(m);   // load each interval's data up front
        var sw = Stopwatch.StartNew();
        var mismatches = new string?[trials.Count];
        Parallel.For(0, trials.Count, new ParallelOptions { MaxDegreeOfParallelism = cfg.Research.ResolvedWorkers }, i =>
        {
            var t = trials[i];
            var def = StrategyRegistry.Get(t.Strategy);
            var p = StrategyRegistry.Resolve(def, t.Parameters);
            var (engine, dev) = engines(t.IntervalMinutes);
            var m = PerformanceMetrics.Compute(engine.Run(def.Create(p)!, dev, p));
            static bool Same(double x, double y) => x.Equals(y);   // bitwise, NaN equals NaN
            if (!Same(m.DailySharpe, t.DailySharpe) || !Same(m.Sharpe, t.Sharpe) || !Same(m.ProfitFactor, t.ProfitFactor) || m.Trades != t.Trades)
                mismatches[i] = $"trial {t.Id} {t.Strategy} ({t.IntervalMinutes}m) {Commands.Fmt(t.Parameters)}: recorded Sharpe {t.Sharpe:R} PF {t.ProfitFactor:R} trades {t.Trades}, " +
                                $"now Sharpe {m.Sharpe:R} PF {m.ProfitFactor:R} trades {m.Trades}";
        });
        var bad = mismatches.Where(m => m is not null).ToList();
        Console.WriteLine($"Replayed {trials.Count} registered trials on the dev window in {sw.Elapsed.TotalSeconds:F2}s: {trials.Count - bad.Count} identical, {bad.Count} different.");
        foreach (var m in bad) Console.WriteLine($"  {m}");
        return bad.Count == 0 ? 0 : 1;
    }

    /// <summary>Appends a manual record of a model consultation, so token use is tracked alongside the research.</summary>
    private static int LogCall(CliArgs a)
    {
        var entry = new
        {
            timestampUtc = DateTime.UtcNow,
            model = a.Required("model"),
            purpose = a.Get("purpose") ?? "",
            inputTokens = a.Int("input-tokens") ?? throw new ArgumentException("--input-tokens is required"),
            outputTokens = a.Int("output-tokens") ?? throw new ArgumentException("--output-tokens is required"),
            counts = a.Has("estimated") ? "estimated" : "actual",
        };
        Directory.CreateDirectory(Dir);
        File.AppendAllText(CallsFile, JsonSerializer.Serialize(entry, Json.Compact) + Environment.NewLine);
        var (calls, inTok, outTok, _) = Calls();
        Console.WriteLine($"Logged. Total: {calls} call(s), {inTok} input / {outTok} output tokens.");
        return 0;
    }

    private static (int Calls, long Input, long Output, int Estimated) Calls()
    {
        if (!File.Exists(CallsFile)) return (0, 0, 0, 0);
        var rows = File.ReadLines(CallsFile).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => JsonNode.Parse(l)!).ToList();
        return (rows.Count, rows.Sum(r => (long)Json.Number(r["inputTokens"])), rows.Sum(r => (long)Json.Number(r["outputTokens"])),
            rows.Count(r => r["counts"]?.GetValue<string>() == "estimated"));
    }

    /// <summary>
    /// Validates proposed hypotheses (e.g. a model's reply to the brief) before anyone implements them. The file is
    /// untrusted input: it is parsed as data, checked against the hunt rules, and only the accepted entries are saved.
    /// </summary>
    private static int Validate(CliArgs a)
    {
        var cfg = Commands.LoadConfig(a);
        string file = a.Required("file");
        var text = File.ReadAllText(file);
        if (text.Length > 32_000) throw new ArgumentException($"{file} is {text.Length} characters; proposals must stay under 32,000.");
        var root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var items = (root?["hypotheses"] as JsonArray) ?? throw new ArgumentException("Expected an object with a \"hypotheses\" array.");

        var log = ReadLog(a.Get("log") ?? "hunt_log.json");
        var known = StrategyRegistry.All.Select(d => d.Name)
            .Concat((log["tested_strategies"] as JsonArray ?? []).Select(n => n!.GetValue<string>()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var accepted = new List<JsonNode>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (var h in items)
        {
            index++;
            var errors = ProposalErrors(h, cfg.Hunt, known, seen);
            string name = h?["name"]?.ToString() ?? $"#{index}";
            if (index > cfg.Hunt.HypothesesPerCycle) errors.Add($"more than {cfg.Hunt.HypothesesPerCycle} hypotheses per batch");
            if (errors.Count == 0) { accepted.Add(h!.DeepClone()); Console.WriteLine($"  ACCEPTED {name}"); }
            else Console.WriteLine($"  REJECTED {name}: {string.Join("; ", errors)}");
        }
        if (accepted.Count == 0) { Console.WriteLine("Nothing accepted."); return 1; }
        string outPath = Path.Combine(Dir, "proposals", $"{DateTime.UtcNow:yyyyMMddTHHmmss}Z_accepted.json");
        Json.Write(outPath, new { source = Path.GetFileName(file), validatedUtc = DateTime.UtcNow, hypotheses = accepted });
        Console.WriteLine($"{accepted.Count} accepted -> {outPath}. Implement each as an IStrategy and register it; the next 'research run' picks them up.");
        return 0;
    }

    private static List<string> ProposalErrors(JsonNode? h, HuntRules rules, HashSet<string> known, HashSet<string> seen)
    {
        var e = new List<string>();
        if (h is not JsonObject o) return ["not an object"];
        string? Str(string k) => o[k] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

        var name = Str("name");
        if (name is null || !Regex.IsMatch(name, "^[a-z][a-z0-9-]{2,40}$")) e.Add("name must be kebab-case, 3-41 characters");
        else if (known.Contains(name)) e.Add("name is already registered or tested");
        else if (!seen.Add(name)) e.Add("duplicate name in this file");
        foreach (var k in new[] { "family", "entry", "exit", "stop", "rationale" })
        {
            var s = Str(k);
            if (string.IsNullOrWhiteSpace(s)) e.Add($"'{k}' is required");
            else if (s.Length > 600) e.Add($"'{k}' is longer than 600 characters");
        }
        if (o["parameters"] is not JsonArray ps || ps.Count is < 1 or > 4) { e.Add("1-4 'parameters' are required"); return e; }
        long grid = 1;
        foreach (var p in ps)
        {
            string pn = p?["name"]?.ToString() ?? "?";
            double def = Json.Number(p?["default"]), min = Json.Number(p?["min"]), max = Json.Number(p?["max"]), step = Json.Number(p?["step"]);
            if (!double.IsFinite(def) || !double.IsFinite(min) || !double.IsFinite(max) || !(step > 0) || max < min)
            {
                e.Add($"parameter {pn}: needs finite default/min/max and step > 0 with max >= min");
                continue;
            }
            var values = new Backtester.Core.Optimization.ParamSpec(pn, min, max, step).Values();
            if (!values.Any(v => Math.Abs(v - def) < 1e-9)) e.Add($"parameter {pn}: default {def} is not on its grid");
            grid *= values.Count;
        }
        long budget = rules.OptimizerMaxEvaluations - 2L * ps.Count;
        if (grid > budget) e.Add($"grid has {grid} points; at most {budget} keeps the search exhaustive under the {rules.OptimizerMaxEvaluations}-evaluation cap");
        return e;
    }

    internal static JsonObject ReadLog(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static bool Alive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }
}
