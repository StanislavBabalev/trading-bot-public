using System.Text.Json;
using System.Threading.Channels;
using Backtester.Core.Analytics;
using Backtester.Core.Reporting;

namespace Backtester.Core.Research;

/// <summary>One completed backtest. Failed runs are never stored, so a record always means a finished experiment.</summary>
public sealed record ExperimentRecord(
    string Id,
    ExperimentIdentity Identity,
    PerformanceMetrics Metrics,
    string TradesHash,
    int Cycle,
    string Stage,
    DateTime CompletedUtc,
    double ElapsedMs);

/// <summary>
/// Append-only JSON Lines file of completed experiments, indexed by identity. A background writer appends records and
/// flushes whenever it catches up, so backtest workers never wait on the disk, and a hard crash loses only the few
/// records still in flight (they are simply recomputed). Dispose writes everything out. A torn last line from a crash
/// is skipped on load. Thread-safe.
/// </summary>
public sealed class ExperimentStore : IDisposable
{
    private readonly Dictionary<string, ExperimentRecord> _byId = [];
    private readonly Lock _lock = new();
    private readonly Channel<string>? _lines;
    private readonly Task? _writerTask;

    private ExperimentStore(string? path)
    {
        Path = path;
        if (path is null) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        if (File.Exists(path))
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                ExperimentRecord? r = null;
                try { r = JsonSerializer.Deserialize<ExperimentRecord>(line, Json.Compact); }
                catch (JsonException) { }
                if (r is null) { SkippedLines++; continue; }
                // Written under an older identity format (e.g. before a field was added): never reusable, not corrupt.
                if (r.Id != r.Identity.Id) { StaleLines++; continue; }
                _byId[r.Id] = r;
            }
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 1 << 16);
        // A torn line from a crash has no newline; start on a fresh line so the next record stays parseable.
        if (stream.Length > 0 && LastByte(path) != (byte)'\n') stream.WriteByte((byte)'\n');
        var writer = new StreamWriter(stream, bufferSize: 1 << 16);
        _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var reader = _lines.Reader;
        _writerTask = Task.Run(async () =>
        {
            await using var w = writer;
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var line)) w.WriteLine(line);
                w.Flush();
            }
        });
    }

    /// <summary>Opens (or creates) the store at <paramref name="path"/>.</summary>
    public static ExperimentStore Open(string path) => new(path);

    /// <summary>A store that keeps nothing on disk.</summary>
    public static ExperimentStore InMemory() => new(null);

    public string? Path { get; }
    /// <summary>Lines that do not parse (a torn write).</summary>
    public int SkippedLines { get; }
    /// <summary>Records whose identity hashes differently today (an older identity format); ignored, never reused.</summary>
    public int StaleLines { get; }
    public int Count { get { lock (_lock) return _byId.Count; } }

    public bool TryGet(string id, out ExperimentRecord record)
    {
        lock (_lock) return _byId.TryGetValue(id, out record!);
    }

    public void Add(ExperimentRecord record)
    {
        lock (_lock)
            if (!_byId.TryAdd(record.Id, record)) return;
        _lines?.Writer.TryWrite(JsonSerializer.Serialize(record, Json.Compact));
    }

    public IReadOnlyList<ExperimentRecord> All()
    {
        lock (_lock) return _byId.Values.ToList();
    }

    /// <summary>Writes out every record added so far and closes the file. Throws if the disk write failed.</summary>
    public void Dispose()
    {
        _lines?.Writer.TryComplete();
        _writerTask?.GetAwaiter().GetResult();
    }

    private static byte LastByte(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        f.Seek(-1, SeekOrigin.End);
        return (byte)f.ReadByte();
    }
}
