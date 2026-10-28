using System.Globalization;
using System.Text;
using Backtester.Core.Analytics;
using Backtester.Core.Engine;

namespace Backtester.Core.Reporting;

/// <summary>Writes a run's metrics, trades and equity curve so every reported number traces to a file.</summary>
public static class ArtifactWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string NewRunDir(string root, string label)
    {
        var safe = new string(label.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        var dir = Path.Combine(root, $"{DateTime.UtcNow:yyyyMMddTHHmmssfff}Z_{safe}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Writes result.json, trades.csv and equity.csv into <paramref name="dir"/>.</summary>
    public static void WriteRun(string dir, BacktestResult r, PerformanceMetrics metrics, PerformanceMetrics? benchmark, object? extra = null)
    {
        Directory.CreateDirectory(dir);
        Json.Write(Path.Combine(dir, "result.json"), new
        {
            strategy = r.StrategyName,
            parameters = r.Parameters,
            config = r.Config,
            metrics,
            buyAndHold = benchmark,
            diagnostics = new
            {
                r.EntriesSkippedNoStop,
                r.EntriesSkippedStopBreached,
                r.EntriesSkippedNoVolEstimate,
                r.DailyLossStopsHit,
                r.Halted,
                r.HaltReason,
                r.TradedNotional,
            },
            extra,
            files = new { trades = "trades.csv", equity = "equity.csv" },
        });

        var t = new StringBuilder("direction,entry_time,entry_price,exit_time,exit_price,quantity,gross_pnl,fees,funding,net_pnl,exit_reason,bars_held\n");
        foreach (var x in r.Trades)
            t.Append(Inv, $"{x.Direction},{TimeUtil.Iso(x.EntryTimeMs)},{x.EntryPrice:R},{TimeUtil.Iso(x.ExitTimeMs)},{x.ExitPrice:R},{x.Quantity:R},{x.GrossPnl:R},{x.Fees:R},{x.Funding:R},{x.NetPnl:R},{x.ExitReason},{x.BarsHeld}\n");
        File.WriteAllText(Path.Combine(dir, "trades.csv"), t.ToString());

        var e = new StringBuilder("time,equity,position_qty\n");
        foreach (var p in r.Equity)
            e.Append(Inv, $"{TimeUtil.Iso(p.TimeMs)},{p.Equity:R},{p.PositionQty:R}\n");
        File.WriteAllText(Path.Combine(dir, "equity.csv"), e.ToString());
    }
}
