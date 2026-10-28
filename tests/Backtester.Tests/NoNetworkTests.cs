using System.Text.RegularExpressions;

namespace Backtester.Tests;

/// <summary>
/// Tripwire: the only code allowed to touch the network is the Bybit public-data client behind the manual
/// <c>fetch</c> command. Backtests, hunts and research runs must have no network, account, publishing or trading path.
/// </summary>
public class NoNetworkTests
{
    private static readonly Regex Network = new(
        @"HttpClient|HttpRequestMessage|WebRequest|WebClient|WebSocket|TcpClient|UdpClient|System\.Net\.Sockets|Process\.Start|https?://|trader\.dev|mcp\.",
        RegexOptions.Compiled);

    private static readonly string[] Allowed = ["Backtester.Core/Data/BybitClient.cs"];

    /// <summary>The .xlsx writer must name the OOXML namespaces; they are identifiers, never fetched. Any other URL still trips.</summary>
    private const string XlsxWriter = "Backtester.Core/Reporting/Xlsx.cs";
    private static readonly Regex OoxmlNamespace = new(@"http://schemas\.openxmlformats\.org/[\w./-]*", RegexOptions.Compiled);

    internal static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TradingBot.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("TradingBot.slnx not found above the test output directory.");
    }

    [Fact]
    public void Only_the_fetch_client_references_the_network()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (Rel: Path.GetRelativePath(src, f).Replace('\\', '/'), Lines: File.ReadAllLines(f)))
            .Where(f => !Allowed.Contains(f.Rel))
            .SelectMany(f => f.Lines.Select((l, i) => (f.Rel, Line: i + 1, Text: f.Rel == XlsxWriter ? OoxmlNamespace.Replace(l.Trim(), "") : l.Trim())))
            // Program.cs only names HttpRequestException to report a failed 'fetch' cleanly.
            .Where(x => Network.IsMatch(x.Text) && !(x.Rel == "Backtester.Cli/Program.cs" && x.Text.Contains("HttpRequestException")))
            .Select(x => $"{x.Rel}:{x.Line}: {x.Text}")
            .ToList();
        Assert.True(offenders.Count == 0, "Network access outside the fetch client:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Only_the_fetch_command_creates_the_bybit_client()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var users = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && File.ReadAllText(f).Contains("new BybitClient("))
            .Select(f => Path.GetFileName(f))
            .ToList();
        Assert.Equal(["Program.cs"], users);
        var program = File.ReadAllText(Path.Combine(src, "Backtester.Cli", "Program.cs"));
        var fetchBody = program[program.IndexOf("public static async Task<int> Fetch", StringComparison.Ordinal)..program.IndexOf("public static int Audit", StringComparison.Ordinal)];
        Assert.Contains("new BybitClient(", fetchBody);
        Assert.Single(Regex.Matches(program, @"new BybitClient\("));
    }
}
