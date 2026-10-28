using Backtester.Core;
using Backtester.Core.Data;
using Backtester.Core.Engine;
using Backtester.Core.Indicators;
using Backtester.Core.Strategies;
using static Backtester.Tests.TestData;

namespace Backtester.Tests;

public class DataAndIndicatorTests
{
    [Fact]
    public void Audit_finds_gaps_duplicates_and_bad_rows()
    {
        var bars = Flat(10, 100);
        bars.RemoveAt(4);
        bars.RemoveAt(4);                                   // two missing hours after bar 3
        bars.Add(bars[^1]);                                 // duplicate last row
        bars.Add(new Bar(T0 + 20 * Hour, 100, 90, 95, 100, 1));   // high below close, and a 10-hour gap

        var r = DataAudit.AuditBars(bars, Hour, T0, T0 + 10 * Hour);
        Assert.Equal(1, r.DuplicateTimestamps);
        Assert.Equal(1, r.InvalidOhlcRows);
        Assert.Equal(2, r.Gaps.Count);
        Assert.Equal(2, r.Gaps[0].MissingSteps);
        Assert.Equal(T0 + 3 * Hour, r.Gaps[0].AfterMs);
        Assert.Equal(8, r.WindowBars);
        Assert.Equal(2, r.WindowMissingBars);
    }

    [Fact]
    public void Funding_audit_reports_irregular_intervals()
    {
        long eight = 8 * Hour;
        var rates = new List<FundingRate> { new(T0, 0.0001), new(T0 + eight, 0.0002), new(T0 + 3 * eight, -0.0001) };
        var r = DataAudit.AuditFunding(rates, eight);
        var gap = Assert.Single(r.IrregularIntervals);
        Assert.Equal(1, gap.MissingSteps);
    }

    [Fact]
    public void Csv_round_trips_exactly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bars_{Guid.NewGuid():N}.csv");
        try
        {
            var bars = new List<Bar> { new(T0, 0.1 + 0.2, 1e5 / 3, 1.5, 2.25, 123.456) };
            CsvStore.WriteBars(path, bars);
            Assert.Equal(bars, CsvStore.ReadBars(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Parses_bybit_kline_and_funding_responses()
    {
        const string kline = """{"retCode":0,"retMsg":"OK","result":{"symbol":"BTCUSDT","category":"linear","list":[["1793023200000","79662.5","80220","79542.4","80170.2","1863.197","148908100.3659"]]},"time":1}""";
        var b = Assert.Single(BybitClient.ParseKlines(kline));
        Assert.Equal(new Bar(1793023200000, 79662.5, 80220, 79542.4, 80170.2, 1863.197), b);

        const string funding = """{"retCode":0,"retMsg":"OK","result":{"category":"linear","list":[{"symbol":"BTCUSDT","fundingRate":"-0.00003118","fundingRateTimestamp":"1793001600000"}]},"time":1}""";
        Assert.Equal(new FundingRate(1793001600000, -0.00003118), Assert.Single(BybitClient.ParseFunding(funding)));

        Assert.Throws<BybitApiException>(() => BybitClient.ParseKlines("""{"retCode":10001,"retMsg":"params error","result":{}}"""));
    }

    [Fact]
    public void Ema_is_seeded_with_the_first_value()
    {
        var ema = new Ema(3);
        Assert.Equal(10, ema.Update(10));
        Assert.Equal(15, ema.Update(20), 12);       // alpha 0.5
        Assert.False(ema.IsReady);
        Assert.Equal(22.5, ema.Update(30), 12);
        Assert.True(ema.IsReady);
    }

    [Fact]
    public void Rma_is_seeded_with_the_sma()
    {
        var rma = new Rma(3);
        rma.Update(3);
        rma.Update(6);
        Assert.Equal(6, rma.Update(9), 12);          // SMA seed
        Assert.Equal((12 + 2 * 6) / 3.0, rma.Update(12), 12);
    }

    [Fact]
    public void Atr_uses_true_range_with_previous_close()
    {
        var atr = new Atr(2);
        atr.Update(new Bar(0, 10, 12, 9, 11, 1));     // TR 3 (no previous close)
        atr.Update(new Bar(1, 11, 11, 10, 10, 1));    // TR max(1, 0, 1) = 1
        Assert.Equal(2, atr.Value, 12);
        atr.Update(new Bar(2, 15, 16, 15, 16, 1));    // gap up: TR = 16 - 10 = 6
        Assert.Equal((6 + 2) / 2.0, atr.Value, 12);
    }

    [Fact]
    public void Ema_cross_goes_long_on_uptrend_and_flat_on_downtrend_in_long_only_mode()
    {
        var prices = Enumerable.Range(0, 40).Select(i => 100.0 - i)                  // down
            .Concat(Enumerable.Range(0, 40).Select(i => 60.0 + 2 * i))              // up
            .Concat(Enumerable.Range(0, 40).Select(i => 140.0 - 2 * i)).ToList();   // down
        var bars = prices.Select((p, i) => new Bar(T0 + i * Hour, p, p + 0.5, p - 0.5, p, 1)).ToList();
        var strategy = new EmaCrossAtrStop(3, 8, longOnly: true, atrLen: 3, atrMult: 0);
        var r = new BacktestEngine(bars, []).Run(strategy, Config(bars.Count));

        var t = Assert.Single(r.Trades);
        Assert.Equal(1, t.Direction);
        Assert.True(t.EntryTimeMs > bars[40].OpenTimeMs && t.EntryTimeMs < bars[50].OpenTimeMs);
        Assert.True(t.ExitTimeMs > bars[80].OpenTimeMs && t.ExitTimeMs < bars[90].OpenTimeMs);
        Assert.Equal(ExitReason.Signal, t.ExitReason);
    }

    [Fact]
    public void Strategy_context_blocks_look_ahead()
    {
        var bars = Flat(3, 100);
        var probe = new LookAheadProbe();
        new BacktestEngine(bars, []).Run(probe, Config(3));
        Assert.True(probe.Blocked);
    }

    private sealed class LookAheadProbe : IStrategy
    {
        public bool Blocked { get; private set; }
        public string Name => "probe";

        public Signal OnBarClose(StrategyContext ctx)
        {
            try { ctx.Back(-1); }
            catch (ArgumentOutOfRangeException) { Blocked = true; }
            return Signal.Flat;
        }
    }
}
