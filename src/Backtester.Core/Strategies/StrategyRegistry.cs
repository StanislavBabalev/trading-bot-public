using Backtester.Core.Optimization;

namespace Backtester.Core.Strategies;

/// <summary>
/// A named strategy with its tunable parameters. <see cref="Create"/> returns null for parameter
/// combinations that make no sense (e.g. fast ≥ slow) so the optimizer can skip them.
/// Bump <see cref="Version"/> when the rules change; it is part of every stored experiment's identity.
/// </summary>
public sealed record StrategyDefinition(
    string Name,
    string Family,
    string Description,
    IReadOnlyDictionary<string, double> Defaults,
    IReadOnlyList<ParamSpec> DefaultSpace,
    Func<IReadOnlyDictionary<string, double>, IStrategy?> Create,
    int Version = 1);

public static class StrategyRegistry
{
    public static readonly IReadOnlyList<StrategyDefinition> All =
    [
        new StrategyDefinition(
            "ema-cross-atr",
            "trend",
            "EMA fast/slow crossover; longOnly 1 = long/flat, 0 = long/short; fixed ATR stop (atrMult 0 = off).",
            new Dictionary<string, double> { ["fast"] = 80, ["slow"] = 260, ["longOnly"] = 1, ["atrLen"] = 14, ["atrMult"] = 5 },
            [
                new ParamSpec("fast", 10, 120, 10),
                new ParamSpec("slow", 100, 400, 20),
                new ParamSpec("atrMult", 2, 8, 1),
            ],
            p =>
            {
                int fast = (int)p["fast"], slow = (int)p["slow"];
                if (fast >= slow) return null;
                return new EmaCrossAtrStop(fast, slow, p["longOnly"] >= 0.5, (int)p["atrLen"], p["atrMult"]);
            }),

        new StrategyDefinition(
            "donchian-breakout",
            "trend",
            "Long when close > highest high of the previous entryLen bars, short when close < lowest low of the previous entryLen bars; " +
            "exit long when close < lowest low of the previous exitLen bars (short: close > highest high of previous exitLen bars); " +
            "stop at entry-bar close -/+ ATR(14) x atrMult.",
            new Dictionary<string, double> { ["entryLen"] = 72, ["exitLen"] = 24, ["atrMult"] = 3 },
            [new ParamSpec("entryLen", 24, 168, 24), new ParamSpec("exitLen", 12, 72, 12), new ParamSpec("atrMult", 2, 5, 1)],
            p => p["exitLen"] >= p["entryLen"] ? null : new DonchianBreakout((int)p["entryLen"], (int)p["exitLen"], p["atrMult"])),

        new StrategyDefinition(
            "bollinger-reversion",
            "mean-reversion",
            "Long when close < SMA(bbLen) - bbMult x stdev(bbLen), short when close > SMA + bbMult x stdev; " +
            "exit when close returns to the SMA or after 48 bars; stop at entry-bar close -/+ ATR(14) x atrMult.",
            new Dictionary<string, double> { ["bbLen"] = 40, ["bbMult"] = 2, ["atrMult"] = 3 },
            [new ParamSpec("bbLen", 20, 80, 10), new ParamSpec("bbMult", 1.5, 3, 0.5), new ParamSpec("atrMult", 2, 5, 1)],
            p => new BollingerReversion((int)p["bbLen"], p["bbMult"], p["atrMult"])),

        new StrategyDefinition(
            "keltner-breakout",
            "vol-breakout",
            "Long when close > EMA(emaLen) + bandMult x ATR(14), short when close < EMA - bandMult x ATR(14); " +
            "exit when close crosses back over the EMA; stop at entry-bar close -/+ ATR(14) x atrMult.",
            new Dictionary<string, double> { ["emaLen"] = 48, ["bandMult"] = 2, ["atrMult"] = 3 },
            [new ParamSpec("emaLen", 24, 120, 24), new ParamSpec("bandMult", 1, 3, 0.5), new ParamSpec("atrMult", 2, 5, 1)],
            p => new KeltnerBreakout((int)p["emaLen"], p["bandMult"], p["atrMult"])),

        new StrategyDefinition(
            "ts-momentum",
            "momentum",
            "Long when close / close[lookback] - 1 > threshold, short when < -threshold, otherwise keep the position; " +
            "reversals flip directly; stop at entry-bar close -/+ ATR(14) x atrMult.",
            new Dictionary<string, double> { ["lookback"] = 96, ["threshold"] = 0.01, ["atrMult"] = 3 },
            [new ParamSpec("lookback", 24, 216, 24), new ParamSpec("threshold", 0, 0.02, 0.005), new ParamSpec("atrMult", 2, 5, 1)],
            p => new TimeSeriesMomentum((int)p["lookback"], p["threshold"], p["atrMult"])),

        new StrategyDefinition(
            "trend-pullback-rsi",
            "regime-filter",
            "Long when close > EMA(trendLen) and RSI(rsiLen) < level; short when close < EMA(trendLen) and RSI > 100 - level; " +
            "exit long when RSI > 55 (short: RSI < 45) or after 48 bars; stop at entry-bar close -/+ ATR(14) x atrMult.",
            new Dictionary<string, double> { ["trendLen"] = 200, ["rsiLen"] = 4, ["level"] = 20, ["atrMult"] = 3 },
            [new ParamSpec("trendLen", 100, 400, 100), new ParamSpec("rsiLen", 2, 8, 2), new ParamSpec("level", 10, 25, 5), new ParamSpec("atrMult", 2, 4, 1)],
            p => new TrendPullbackRsi((int)p["trendLen"], (int)p["rsiLen"], p["level"], p["atrMult"])),

        // Hunt cycle 4 (artifacts/hunt/cycle_4_ideas/shortlist.md). Every grid is ≤ 192 points so the optimizer searches it exhaustively.
        new StrategyDefinition(
            "failed-breakout-fade",
            "range-structure",
            "PDH/PDL = previous UTC day's high/low, M = midpoint. Long when close >= PDL after 1..breakBars consecutive closes below PDL, " +
            "the lowest low of those bars <= PDL - excursionAtr x ATR(14), and M - close >= 1 ATR (short: the mirror at PDH); " +
            "exit when the close reaches M (taken at entry) or after maxHold bars; stop at the excursion extreme -/+ stopBufferAtr x ATR.",
            new Dictionary<string, double> { ["breakBars"] = 6, ["excursionAtr"] = 0.25, ["stopBufferAtr"] = 0.5, ["maxHold"] = 24 },
            [
                new ParamSpec("breakBars", 3, 12, 3),
                new ParamSpec("excursionAtr", 0, 0.75, 0.25),
                new ParamSpec("stopBufferAtr", 0.25, 1.0, 0.25),
                new ParamSpec("maxHold", 12, 36, 12),
            ],
            p => new FailedBreakoutFade((int)p["breakBars"], p["excursionAtr"], p["stopBufferAtr"], (int)p["maxHold"])),

        new StrategyDefinition(
            "volume-climax-reversal",
            "volume",
            "Long when volume >= volMult x mean volume of the previous 48 bars, range >= rangeAtr x previous ATR(14), lower wick >= 50% of the range " +
            "and the low is the lowest of the last 24 bars (short: the mirror with the upper wick and 24-bar high); " +
            "exit after maxHold bars; stop 0.25 ATR beyond the wick.",
            new Dictionary<string, double> { ["volMult"] = 2.0, ["rangeAtr"] = 1.5, ["maxHold"] = 12 },
            [new ParamSpec("volMult", 1.5, 3.5, 0.5), new ParamSpec("rangeAtr", 1.0, 2.5, 0.25), new ParamSpec("maxHold", 6, 24, 6)],
            p => new VolumeClimaxReversal(p["volMult"], p["rangeAtr"], (int)p["maxHold"])),

        new StrategyDefinition(
            "session-momentum",
            "time-of-day",
            "On the 11:00 UTC bar's close, R = close - that day's 00:00 open, S = ATR(14) x sqrt(12); long if R >= moveMult x S, short if R <= -moveMult x S " +
            "(fill at the 12:00 open); exit after holdBars bars; stop at signal close -/+ atrMult x ATR.",
            new Dictionary<string, double> { ["moveMult"] = 1.0, ["holdBars"] = 8, ["atrMult"] = 3.0 },
            [new ParamSpec("moveMult", 0.5, 1.5, 0.25), new ParamSpec("holdBars", 4, 12, 2), new ParamSpec("atrMult", 2.0, 4.0, 0.5)],
            p => new SessionMomentum(p["moveMult"], (int)p["holdBars"], p["atrMult"])),

        new StrategyDefinition(
            "impulse-bar-continuation",
            "vol-state",
            "Long when true range >= rangeAtr x previous ATR(14), the bar closes up and its body >= bodyFrac x range (short: the mirror); " +
            "exit after holdBars bars; stop at signal close -/+ atrMult x ATR.",
            new Dictionary<string, double> { ["rangeAtr"] = 2.5, ["bodyFrac"] = 0.6, ["holdBars"] = 12, ["atrMult"] = 2.0 },
            [
                new ParamSpec("rangeAtr", 2.0, 3.5, 0.5),
                new ParamSpec("bodyFrac", 0.5, 0.8, 0.1),
                new ParamSpec("holdBars", 6, 18, 6),
                new ParamSpec("atrMult", 1.5, 3.0, 0.5),
            ],
            p => new ImpulseBarContinuation(p["rangeAtr"], p["bodyFrac"], (int)p["holdBars"], p["atrMult"])),

        new StrategyDefinition(
            "vol-regime-switch",
            "regime-switch",
            "Quiet when ATR(14) < its median over regimeLen bars, else expanding; HH/LL = previous channelLen bars. " +
            "Quiet: long on a green close in the bottom edgeFrac of the channel (short: red close in the top edge), exit at the channel midpoint " +
            "or after 24 bars, stop 0.5 ATR beyond the channel. Expanding: long on close > HH (short: < LL), exit on the opposite channelLen/2 channel, " +
            "stop atrMult x ATR trailed on every close.",
            new Dictionary<string, double> { ["regimeLen"] = 720, ["channelLen"] = 48, ["edgeFrac"] = 0.10, ["atrMult"] = 2.0 },
            [
                new ParamSpec("regimeLen", 360, 1440, 360),
                new ParamSpec("channelLen", 24, 96, 24),
                new ParamSpec("edgeFrac", 0.05, 0.20, 0.05),
                new ParamSpec("atrMult", 1.5, 2.5, 0.5),
            ],
            p => new VolRegimeSwitch((int)p["regimeLen"], (int)p["channelLen"], p["edgeFrac"], p["atrMult"])),

        // Research sprint 1 = hunt cycles 5 and 6 (artifacts/hunt/cycle_5_ideas/research.md): sources, evidence and rules.
        // Daily = completed UTC days built from the 1h bars; dATR = ATR(14) of daily bars. Defaults were fixed before any
        // run on research data. Every grid is ≤ 192 points.
        new StrategyDefinition(
            "donchian-ensemble-trend",
            "trend-ensemble",
            "Four Donchian models with lookbacks baseDays x 1, 2, 4, 8 days; each turns long on a close above the high of its previous bars " +
            "(short below the low) and keeps that state. Long on a fresh breakout of the fastest channel when >= minAgree models are long " +
            "(short: mirror); exit when long models no longer outnumber short ones; stop trails the best close by trailMult x dATR.",
            new Dictionary<string, double> { ["baseDays"] = 2, ["minAgree"] = 3, ["trailMult"] = 2 },
            [new ParamSpec("baseDays", 1, 6, 1), new ParamSpec("minAgree", 3, 4, 1), new ParamSpec("trailMult", 1, 4, 0.5)],
            p => new DonchianEnsembleTrend((int)p["baseDays"], (int)p["minAgree"], p["trailMult"])),

        new StrategyDefinition(
            "funding-crowding-fade",
            "funding-carry",
            "Rank = percentile of the mean of the last 9 settled funding rates (3 days) among the previous 270 such means (90 days). " +
            "Rank >= 1 - quantile (crowded longs): short when the close crosses below EMA(24); rank <= quantile (crowded shorts): long when it " +
            "crosses above. Exit after holdBars; stop stopMult x dATR.",
            new Dictionary<string, double> { ["quantile"] = 0.2, ["holdBars"] = 72, ["stopMult"] = 1.5 },
            [new ParamSpec("quantile", 0.1, 0.3, 0.05), new ParamSpec("holdBars", 24, 120, 24), new ParamSpec("stopMult", 0.5, 3, 0.5)],
            p => new FundingCrowdingFade(p["quantile"], (int)p["holdBars"], p["stopMult"])),

        new StrategyDefinition(
            "noise-area-momentum",
            "intraday-momentum",
            "UTC-day session. sigma_h = mean |close/day open - 1| at hour h over the previous lookbackDays days; bands = open x (1 +/- bandMult x sigma_h). " +
            "From firstHour to 22:00, long when the close moves above the upper band (short: below the lower), at most once per UTC day; exit when the close falls back below " +
            "max(upper band, day VWAP) (short: above min(lower, VWAP)) or at the 23:00 close; stop at the day open.",
            new Dictionary<string, double> { ["lookbackDays"] = 14, ["bandMult"] = 1.0, ["firstHour"] = 2 },
            [new ParamSpec("lookbackDays", 7, 28, 7), new ParamSpec("bandMult", 0.5, 2.0, 0.25), new ParamSpec("firstHour", 0, 6, 2)],
            p => new NoiseAreaMomentum((int)p["lookbackDays"], p["bandMult"], (int)p["firstHour"])),

        new StrategyDefinition(
            "nr-compression-breakout",
            "vol-compression",
            "A day whose range is narrower than each of the previous nrLen - 1 days sets up the next UTC day: the first close above its high goes " +
            "long (below its low: short), once. Stop stopMult x setup range beyond the broken level; exit after holdBars.",
            new Dictionary<string, double> { ["nrLen"] = 4, ["holdBars"] = 24, ["stopMult"] = 1.0 },
            [new ParamSpec("nrLen", 4, 7, 1), new ParamSpec("holdBars", 12, 72, 12), new ParamSpec("stopMult", 0.5, 1.5, 0.25)],
            p => new NrCompressionBreakout((int)p["nrLen"], (int)p["holdBars"], p["stopMult"])),

        new StrategyDefinition(
            "tsmom-vote",
            "momentum-daily",
            "On each daily close, vote = signs of the returns over baseDays, 2 x baseDays and 4 x baseDays days; hold the majority direction " +
            "(unanimous = 1: only when all three agree, else flat), re-evaluated daily (a stopped-out position is re-entered next day if the " +
            "vote still holds). Stop stopMult x dATR from the signal close.",
            new Dictionary<string, double> { ["baseDays"] = 5, ["unanimous"] = 0, ["stopMult"] = 2 },
            [new ParamSpec("baseDays", 3, 10, 1), new ParamSpec("unanimous", 0, 1, 1), new ParamSpec("stopMult", 1, 3.5, 0.5)],
            p => new TsmomVote((int)p["baseDays"], p["unanimous"] >= 0.5, p["stopMult"])),

        new StrategyDefinition(
            "max-min-daily",
            "daily-extremes",
            "Long only. On a daily close >= the highest close of the previous lookbackDays days (trend) or <= the lowest (reversion), go long; " +
            "each signal day renews the hold; exit after holdDays day closes without a signal. Stop stopMult x dATR.",
            new Dictionary<string, double> { ["lookbackDays"] = 10, ["holdDays"] = 1, ["stopMult"] = 2 },
            [new ParamSpec("lookbackDays", 10, 50, 10), new ParamSpec("holdDays", 1, 5, 1), new ParamSpec("stopMult", 1, 3, 0.5)],
            p => new MaxMinDaily((int)p["lookbackDays"], (int)p["holdDays"], p["stopMult"])),

        new StrategyDefinition(
            "mtf-trend-pullback",
            "multi-timeframe-trend",
            "Daily regime = sign(daily close - SMA of the last maDays daily closes). Up regime: long when the hourly close crosses back above " +
            "EMA(entryLen); down regime: short on a cross below. Exit when the daily regime no longer matches; stop trails the best close by trailMult x dATR.",
            new Dictionary<string, double> { ["maDays"] = 20, ["entryLen"] = 24, ["trailMult"] = 2 },
            [new ParamSpec("maDays", 10, 50, 10), new ParamSpec("entryLen", 12, 48, 12), new ParamSpec("trailMult", 1, 3, 0.5)],
            p => new MtfTrendPullback((int)p["maDays"], (int)p["entryLen"], p["trailMult"])),

        new StrategyDefinition(
            "efficiency-regime-switch",
            "efficiency-regime",
            "ER = |net change| / sum |changes| over erLen hourly closes. ER >= erThreshold: long on a close above the previous 48-bar high (short below " +
            "the low), exit on the opposite 24-bar channel. ER below it: fade a close outside SMA(48) +/- 2 sd, exit at the SMA or after 48 bars. " +
            "Stop atrMult x ATR(14) from the signal close; a trade keeps its own rules if the regime flips.",
            new Dictionary<string, double> { ["erLen"] = 168, ["erThreshold"] = 0.2, ["atrMult"] = 2.5 },
            [new ParamSpec("erLen", 72, 264, 48), new ParamSpec("erThreshold", 0.1, 0.35, 0.05), new ParamSpec("atrMult", 1.5, 3.5, 0.5)],
            p => new EfficiencyRegimeSwitch((int)p["erLen"], p["erThreshold"], p["atrMult"])),

        new StrategyDefinition(
            "volume-conditioned-reversal",
            "price-volume",
            "A day is extreme when its |return| is in the top extremeFrac of the previous 90 days. Volume below the previous 30 days' median: fade it; " +
            "above: follow it. Hold holdDays days (a new extreme day renews or flips). Stop stopMult x dATR.",
            new Dictionary<string, double> { ["extremeFrac"] = 0.25, ["holdDays"] = 1, ["stopMult"] = 1.5 },
            [new ParamSpec("extremeFrac", 0.1, 0.35, 0.05), new ParamSpec("holdDays", 1, 3, 1), new ParamSpec("stopMult", 1, 2.5, 0.5)],
            p => new VolumeConditionedReversal(p["extremeFrac"], (int)p["holdDays"], p["stopMult"])),

        new StrategyDefinition(
            "crowding-filtered-breakout",
            "funding-filtered-trend",
            "Long on a close above the high of the previous channelDays x 24 bars only if the 3-day funding rank (as in funding-crowding-fade) is " +
            "<= maxRank; short below the low only if the rank is >= 1 - maxRank (maxRank 1 = no filter). Exit on the opposite channel of half the " +
            "length; stop trails the best close by trailMult x dATR.",
            new Dictionary<string, double> { ["channelDays"] = 4, ["maxRank"] = 0.8, ["trailMult"] = 2 },
            [new ParamSpec("channelDays", 2, 7, 1), new ParamSpec("maxRank", 0.6, 1.0, 0.1), new ParamSpec("trailMult", 1, 3, 0.5)],
            p => new CrowdingFilteredBreakout((int)p["channelDays"], p["maxRank"], p["trailMult"])),

        // Hunt cycle 7: a dev-informed ablation of the one signal that cleared the dev floors with an edge-of-range optimum
        // (funding-crowding-fade, best at quantile 0.3 = the range edge). Defaults and ranges were chosen after seeing
        // cycle 5; every configuration counts as a trial. See artifacts/hunt/cycle_5_ideas/research.md, "Cycle 7".
        new StrategyDefinition(
            "funding-tilt",
            "funding-carry",
            "funding-crowding-fade with the crowding threshold widened up to the median split: short on a close crossing below EMA(24) when the " +
            "3-day funding rank >= 1 - threshold, long on a cross above when <= threshold (0.5 = take every cross, on the side opposite the funding " +
            "tilt). Exit after holdBars; stop stopMult x dATR.",
            new Dictionary<string, double> { ["threshold"] = 0.4, ["holdBars"] = 96, ["stopMult"] = 1.5 },
            [new ParamSpec("threshold", 0.3, 0.5, 0.05), new ParamSpec("holdBars", 48, 144, 24), new ParamSpec("stopMult", 1, 2.5, 0.5)],
            p => new FundingCrowdingFade(p["threshold"], (int)p["holdBars"], p["stopMult"], "funding-tilt")),

        new StrategyDefinition(
            "ema-cross-control",
            "control",
            "Ablation control for funding-tilt, funding ignored: long when the close crosses above EMA(24), short when it crosses below; exit after " +
            "holdBars; stop stopMult x dATR.",
            new Dictionary<string, double> { ["holdBars"] = 96, ["stopMult"] = 1.5 },
            [new ParamSpec("holdBars", 48, 144, 24), new ParamSpec("stopMult", 1, 2.5, 0.5)],
            p => new FundingCrowdingFade(1.0, (int)p["holdBars"], p["stopMult"], "ema-cross-control")),

        new StrategyDefinition(
            "funding-carry-tilt",
            "funding-carry",
            "Funding alone, no price timing: on each daily close, short when the 3-day funding rank >= 0.5 + band, long when <= 0.5 - band, else " +
            "flat; re-evaluated daily. Stop stopMult x dATR.",
            new Dictionary<string, double> { ["band"] = 0.3, ["stopMult"] = 2 },
            [new ParamSpec("band", 0, 0.4, 0.1), new ParamSpec("stopMult", 1, 3, 0.5)],
            p => new FundingCarryTilt(p["band"], p["stopMult"])),

        // Hunt cycle 8: funding-crowding-fade's edge-of-range optimum (quantile 0.3) re-tested with the range widened around it.
        // Its default IS that optimum, picked from cycles 5 and 7, so this is the most dev-informed hypothesis in the registry;
        // only the locked OOS run (if it ever gets there) would be real evidence. See research.md, "Cycle 8".
        new StrategyDefinition(
            "funding-fade-wide",
            "funding-carry",
            "funding-crowding-fade (rank = 3-day mean funding vs the previous 90 days; short on a close crossing below EMA(24) when rank >= 1 - " +
            "quantile, long on a cross above when rank <= quantile; exit after holdBars; stop stopMult x dATR) with the quantile range centred on " +
            "cycle 5's edge optimum.",
            new Dictionary<string, double> { ["quantile"] = 0.3, ["holdBars"] = 96, ["stopMult"] = 1.5 },
            [new ParamSpec("quantile", 0.2, 0.4, 0.05), new ParamSpec("holdBars", 48, 144, 24), new ParamSpec("stopMult", 1, 2.5, 0.5)],
            p => new FundingCrowdingFade(p["quantile"], (int)p["holdBars"], p["stopMult"], "funding-fade-wide")),

        // Research sprint 2 = hunt cycles 9 and 10 (artifacts/hunt/cycle_9_ideas/research.md): Market Cipher A/B components
        // (from the open-source VuManChu Cipher B re-implementation) and popular TradingView indicators, all on N-hour bars
        // (tfHours) built from the 1h data. WaveTrend = (channel 9, average 12, MA 3) on hlc3, as in that script.
        new StrategyDefinition(
            "cipher-b-dots",
            "oscillator-reversal",
            "Market Cipher B green/red circles on tfHours bars: long when WT1 crosses above WT2 with WT2 <= -level, short when it crosses below with " +
            "WT2 >= level; exit on the opposite WT cross (flip if that cross is a signal); stop atrMult x ATR(14) of the period bars.",
            new Dictionary<string, double> { ["tfHours"] = 4, ["level"] = 53, ["atrMult"] = 2.5 },
            [new ParamSpec("tfHours", 2, 8, 2), new ParamSpec("level", 41, 65, 6), new ParamSpec("atrMult", 1.5, 3.5, 0.5)],
            p => new CipherBDots((int)p["tfHours"], p["level"], p["atrMult"])),

        new StrategyDefinition(
            "cipher-b-sommi-flag",
            "oscillator-continuation",
            "Market Cipher B 'Sommi flag': long when money flow (RSI+MFI area 60, x150, -2.5) > 0, WT2 < 0, WT1 crosses up and the WT1 - WT2 of the last " +
            "completed htfHours bar is > 0; short: the mirror. Exit on the opposite WT cross; stop atrMult x ATR(14) of the period bars.",
            new Dictionary<string, double> { ["tfHours"] = 2, ["htfHours"] = 12, ["atrMult"] = 2.5 },
            [new ParamSpec("tfHours", 2, 8, 2), new ParamSpec("htfHours", 12, 24, 12), new ParamSpec("atrMult", 1.5, 3.5, 0.5)],
            p => (int)p["htfHours"] % (int)p["tfHours"] != 0 ? null : new CipherBSommiFlag((int)p["tfHours"], (int)p["htfHours"], p["atrMult"])),

        new StrategyDefinition(
            "cipher-b-divergence",
            "oscillator-divergence",
            "Market Cipher B divergences: a 5-bar WT2 bottom fractal <= -65 x limitScale with a lower price low but higher WT2 than the previous such " +
            "fractal goes long when confirmed; a top fractal >= 45 x limitScale with a higher high and lower WT2 goes short. Exit on the opposite WT " +
            "cross; stop atrMult x ATR(14) of the period bars.",
            new Dictionary<string, double> { ["tfHours"] = 2, ["limitScale"] = 1.0, ["atrMult"] = 2.0 },
            [new ParamSpec("tfHours", 2, 8, 2), new ParamSpec("limitScale", 0.5, 1.25, 0.25), new ParamSpec("atrMult", 1, 3, 0.5)],
            p => new CipherBDivergence((int)p["tfHours"], p["limitScale"], p["atrMult"])),

        new StrategyDefinition(
            "cipher-a-ribbon",
            "ema-ribbon",
            "Market Cipher A: EMA(11) crossing above EMA(33) goes long, below goes short; with diamondExit = 1 a long also exits on a 'blood diamond' " +
            "(bearish WT cross on the period EMA(5) crosses under EMA(11)), a short on the mirror; stop atrMult x ATR(14) of the period bars.",
            new Dictionary<string, double> { ["tfHours"] = 4, ["atrMult"] = 2.5, ["diamondExit"] = 1 },
            [new ParamSpec("tfHours", 2, 8, 2), new ParamSpec("atrMult", 1.5, 3.5, 0.5), new ParamSpec("diamondExit", 0, 1, 1)],
            p => new CipherARibbon((int)p["tfHours"], p["atrMult"], p["diamondExit"] >= 0.5)),

        new StrategyDefinition(
            "cipher-b-confluence",
            "oscillator-reversal",
            "Market Cipher B confluence: the cipher-b-dots circle (WT cross at +/-level) only when money flow is turning the same way and Stochastic RSI " +
            "(log, 14/14/3/3) %K is above %D for a buy (below for a sell). Exit on the opposite WT cross; stop atrMult x ATR(14) of the period bars.",
            new Dictionary<string, double> { ["tfHours"] = 4, ["level"] = 53, ["atrMult"] = 2.5 },
            [new ParamSpec("tfHours", 2, 8, 2), new ParamSpec("level", 41, 65, 6), new ParamSpec("atrMult", 1.5, 3.5, 0.5)],
            p => new CipherBConfluence((int)p["tfHours"], p["level"], p["atrMult"])),

        new StrategyDefinition(
            "supertrend",
            "atr-trend",
            "SuperTrend(atrLen, mult) on tfHours bars: hold its direction and flip with it; the SuperTrend line is the trailing stop; a stop-out is " +
            "re-entered on the next period close if the direction still holds.",
            new Dictionary<string, double> { ["tfHours"] = 4, ["atrLen"] = 10, ["mult"] = 3 },
            [new ParamSpec("tfHours", 2, 8, 2), new ParamSpec("atrLen", 7, 13, 3), new ParamSpec("mult", 1.5, 4, 0.5)],
            p => new SuperTrendFlip((int)p["tfHours"], (int)p["atrLen"], p["mult"])),

        new StrategyDefinition(
            "squeeze-momentum",
            "vol-compression",
            "LazyBear Squeeze Momentum (20): when Bollinger(2 sd) leaves the Keltner channel (kcMult x SMA of true range) after a squeeze, enter in the " +
            "direction of linreg momentum; exit when momentum turns back toward zero; stop atrMult x ATR(14) of the period bars.",
            new Dictionary<string, double> { ["tfHours"] = 4, ["kcMult"] = 1.5, ["atrMult"] = 2.5 },
            [new ParamSpec("tfHours", 2, 8, 2), new ParamSpec("kcMult", 1, 2, 0.25), new ParamSpec("atrMult", 1.5, 3.5, 0.5)],
            p => new SqueezeMomentum((int)p["tfHours"], p["kcMult"], p["atrMult"])),

        new StrategyDefinition(
            "ichimoku-cloud",
            "cloud-trend",
            "Ichimoku 9/26/52/26 (periodSet 1) or 20/60/120/30 (periodSet 2): long when the close is above the cloud, tenkan > kijun and the close is above " +
            "the close displacement-1 periods ago, on the period that first becomes true; exit on a close below kijun; short: the mirror; stop atrMult x ATR.",
            new Dictionary<string, double> { ["tfHours"] = 4, ["periodSet"] = 1, ["atrMult"] = 2.5 },
            [new ParamSpec("tfHours", 2, 8, 2), new ParamSpec("periodSet", 1, 2, 1), new ParamSpec("atrMult", 1.5, 3.5, 0.5)],
            p => new IchimokuCloud((int)p["tfHours"], (int)p["periodSet"], p["atrMult"])),

        // Hunt cycle 11 (artifacts/hunt/cycle_11_ideas/research.md): Veles-style DCA grid bots, simulated by DcaGridBot.
        // Fixed: stop 5% beyond the last averaging order (each deal sized so a full grid stopped out loses 1% of equity),
        // uniform order spacing (logarithmic factor 1, Veles's "balanced" template).
        new StrategyDefinition(
            "dca-grid-long",
            "dca-grid",
            "Veles DCA bot, long, no entry filter: market base order, then orders - 1 averaging limits spread evenly over overlap below it, each " +
            "(1 + martingale) times the previous notional; take-profit limit takeProfit above the running average entry; stop 5% below the last " +
            "order; the next deal starts at the next open.",
            new Dictionary<string, double> { ["orders"] = 5, ["overlap"] = 0.10, ["martingale"] = 0.15, ["takeProfit"] = 0.01 },
            [new ParamSpec("orders", 3, 7, 2), new ParamSpec("overlap", 0.05, 0.20, 0.05), new ParamSpec("martingale", 0, 0.30, 0.15), new ParamSpec("takeProfit", 0.005, 0.02, 0.005)],
            p => new DcaGridBot("dca-grid-long", 1, (int)p["orders"], p["overlap"], p["martingale"], p["takeProfit"], 0.05, 1.0, () => new NoDealFilter())),

        new StrategyDefinition(
            "dca-grid-short",
            "dca-grid",
            "dca-grid-long mirrored: market short, averaging sell limits spread over overlap above it, take-profit below the average, stop 5% " +
            "above the last order.",
            new Dictionary<string, double> { ["orders"] = 5, ["overlap"] = 0.10, ["martingale"] = 0.15, ["takeProfit"] = 0.01 },
            [new ParamSpec("orders", 3, 7, 2), new ParamSpec("overlap", 0.05, 0.20, 0.05), new ParamSpec("martingale", 0, 0.30, 0.15), new ParamSpec("takeProfit", 0.005, 0.02, 0.005)],
            p => new DcaGridBot("dca-grid-short", -1, (int)p["orders"], p["overlap"], p["martingale"], p["takeProfit"], 0.05, 1.0, () => new NoDealFilter())),

        new StrategyDefinition(
            "dca-rsi-long",
            "dca-grid",
            "dca-grid-long (5 orders, martingale 15%) that starts a deal only when RSI(14) of 1h closes is below rsiLevel at a bar's close.",
            new Dictionary<string, double> { ["rsiLevel"] = 30, ["overlap"] = 0.10, ["takeProfit"] = 0.01, ["orders"] = 5, ["martingale"] = 0.15 },
            [new ParamSpec("rsiLevel", 20, 40, 5), new ParamSpec("overlap", 0.05, 0.20, 0.05), new ParamSpec("takeProfit", 0.005, 0.02, 0.005)],
            p =>
            {
                double level = p["rsiLevel"];
                return new DcaGridBot("dca-rsi-long", 1, (int)p["orders"], p["overlap"], p["martingale"], p["takeProfit"], 0.05, 1.0, () => new RsiDealFilter(14, level));
            }),

        new StrategyDefinition(
            "dca-trend-long",
            "dca-grid",
            "dca-grid-long (5 orders, martingale 15%) that starts a deal only while the last daily close is above the SMA of the last maDays daily closes.",
            new Dictionary<string, double> { ["maDays"] = 200, ["overlap"] = 0.10, ["takeProfit"] = 0.01, ["orders"] = 5, ["martingale"] = 0.15 },
            [new ParamSpec("maDays", 50, 200, 50), new ParamSpec("overlap", 0.05, 0.20, 0.05), new ParamSpec("takeProfit", 0.005, 0.02, 0.005)],
            p =>
            {
                int maDays = (int)p["maDays"];
                return new DcaGridBot("dca-trend-long", 1, (int)p["orders"], p["overlap"], p["martingale"], p["takeProfit"], 0.05, 1.0, () => new DailyTrendDealFilter(maDays));
            }),

        // Research sprint 3 = hunt cycle 12 (artifacts/hunt/cycle_12_ideas/research.md): liquidity provision after forced
        // or turbulent moves, and round-number price barriers. Defaults fixed before any run on research data.
        new StrategyDefinition(
            "cascade-fade",
            "liquidation-cascade",
            "Fade a liquidation cascade: long when an hourly bar's close - open <= -dropAtr x ATR(14) on volume >= volMult x the previous " +
            "48 bars' mean while the 3-day funding crowding rank >= 0.5 (longs crowded); short on the mirror with rank <= 0.5. Exit after " +
            "holdBars or on a close back beyond the cascade bar's open; stop 1 ATR beyond the cascade bar's extreme.",
            new Dictionary<string, double> { ["dropAtr"] = 1.5, ["volMult"] = 1.5, ["holdBars"] = 24 },
            [new ParamSpec("dropAtr", 1, 3, 0.5), new ParamSpec("volMult", 1, 3, 0.5), new ParamSpec("holdBars", 12, 48, 12)],
            p => new CascadeFade(p["dropAtr"], p["volMult"], (int)p["holdBars"])),

        new StrategyDefinition(
            "turmoil-reversal",
            "turmoil-liquidity-provision",
            "On 4h bars: turmoil when 4h ATR(14) / open is >= the volQuantile quantile of its previous 540 values (90 days); in turmoil " +
            "fade a 4h body of >= zEntry x the previous 4h ATR (long after a drop, short after a spike). Exit on a 4h close back across " +
            "EMA(20) of 4h closes or after holdPeriods 4h bars; stop 1 4h-ATR beyond the period's extreme.",
            new Dictionary<string, double> { ["zEntry"] = 1, ["volQuantile"] = 0.7, ["holdPeriods"] = 6 },
            [new ParamSpec("zEntry", 0.5, 2, 0.25), new ParamSpec("volQuantile", 0.5, 0.9, 0.1), new ParamSpec("holdPeriods", 3, 12, 3)],
            p => new TurmoilReversal(p["zEntry"], p["volQuantile"], (int)p["holdPeriods"])),

        new StrategyDefinition(
            "round-number-bounce",
            "price-barrier",
            "Levels = multiples of levelStep USD. Long when a bar opens above the nearest level below it, trades down to it and closes back " +
            "at or above it, with no bar at or below that level in the previous freshBars hours (first test); short on the mirror at the " +
            "nearest level above. Skip bars reaching both levels. Exit after holdBars; stop 1 ATR(14) beyond the level.",
            new Dictionary<string, double> { ["levelStep"] = 1000, ["freshBars"] = 24, ["holdBars"] = 24 },
            [new ParamSpec("levelStep", 500, 2500, 500), new ParamSpec("freshBars", 12, 60, 12), new ParamSpec("holdBars", 12, 48, 12)],
            p => new RoundNumberBounce(p["levelStep"], (int)p["freshBars"], (int)p["holdBars"])),
    ];

    public static StrategyDefinition Get(string name) =>
        All.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown strategy '{name}'. Known: {string.Join(", ", All.Select(d => d.Name))}");

    /// <summary>Defaults overlaid with <paramref name="overrides"/>; unknown names are rejected.</summary>
    public static Dictionary<string, double> Resolve(StrategyDefinition def, IReadOnlyDictionary<string, double> overrides)
    {
        var p = new Dictionary<string, double>(def.Defaults);
        foreach (var (k, v) in overrides)
        {
            if (!p.ContainsKey(k)) throw new ArgumentException($"Strategy '{def.Name}' has no parameter '{k}'. Known: {string.Join(", ", p.Keys)}");
            p[k] = v;
        }
        return p;
    }
}
