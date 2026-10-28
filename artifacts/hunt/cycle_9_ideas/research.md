# Research sprint 2 (hunt cycles 9 and 10): Market Cipher and popular TradingView indicators

Requested by the user on 2026-10-26: "Try checking in TradingView Market Cipher if you can, or reverse engineer it".
Researched that day with web search and the built-in browser (public, logged-out TradingView pages). Written before
cycles 9–10 ran.

## What Market Cipher is, and what was (not) done

- **Market Cipher A/B** are paid, closed-source (invite-only) TradingView indicator suites. Their code was **not**
  accessed, decompiled or copied, and no attempt was made to get around TradingView's protection.
- What is public: descriptions of their signals (TradingView "OpenCipher A" pages, community guides) and an
  **open-source re-implementation**, "VuManChu Cipher B + Divergences" by vumanchu
  ([TradingView](https://www.tradingview.com/script/Msm4SjwI-VuManChu-Cipher-B-Divergences/), first published
  2019-11-11, last updated 2023-10-23, ~626k views). Its own notes call it "an attempt to imitate Market Cipher B",
  assembled from other public scripts (LazyBear's WaveTrend, an MFI+RSI area, fractal divergences), with defaults
  "for 4h", tested by its author "only 2h and 4h". The page marks it open source; republishing the code is subject to
  TradingView's House Rules. The concepts below are re-implemented in our own C#; no Pine code is copied.
- **Evidence:** none. No source reports an out-of-sample, cost-inclusive test of Market Cipher signals. The script's own
  author says the circles "are not certain signs to trade". These are popular retail tools, not research results.
- **A look-ahead trap in the clone:** its "Sommi diamond" reads higher-timeframe Heikin-Ashi candles with
  `security(..., barmerge.lookahead_on)`, which on historical bars sees the higher-timeframe candle before it has closed.
  A TradingView backtest built on that signal would look better than it can trade. Our version of the Sommi *flag* uses
  only completed higher-timeframe bars, and the Sommi diamond is not used.

## Components (as implemented)

- **WaveTrend** (LazyBear, via the clone; formula also documented by
  [LuxAlgo](https://www.luxalgo.com/library/concept/wave-trend-oscillator.md) and
  [ProRealCode](https://www.prorealcode.com/prorealtime-indicators/wave-trend-oscillator/)): ap = hlc3; ESA = EMA(ap, 9);
  D = EMA(|ap − ESA|, 9); CI = (ap − ESA) / (0.015 D); WT1 = EMA(CI, 12); WT2 = SMA(WT1, 3). Levels ±53 (also 60, −75).
- **Money flow ("RSI+MFI area")**: SMA((close − open) / (high − low) × 150, 60) − 2.5.
- **Divergences**: 5-bar fractals on WT2; regular bullish = price lower low, WT2 higher low, fractal ≤ −65 (bearish ≥ 45).
- **Stochastic RSI**: RSI(14) of log close, stoch 14, %K SMA 3, %D SMA 3.
- **Market Cipher A** ("OpenCipher A" descriptions): EMA ribbon 5/11/15/18/21/25/29/33; green dot = EMA 11 crosses
  over EMA 33; red cross = EMA 5 crosses under EMA 11; red diamond = bearish WaveTrend cross; blood diamond = red
  diamond and red cross on the same candle.
- Signals are computed on UTC-aligned N-hour bars built from our 1h data (`PeriodBars`), decided on the period's last
  close, filled at the next period's open; stop = atrMult × ATR(14) of the period bars (1%-risk sizing needs a stop).

## Popular-indicator batch (cycle 10)

Search found no independent evidence for SuperTrend, Squeeze Momentum or Ichimoku on BTC: only self-reported script
backtests (e.g. a squeeze-momentum strategy reporting "good results on 1h to 4h" on BitMEX, without costs or OOS). They
are included because they are among the most used TradingView tools and because "more tests" was requested; they belong
to families already tested at 1h (ATR trend, compression breakout, MA-based trend), here on 2–8h bars.

## Hypotheses (8; defaults fixed before any research-data run)

| Cycle | Name | Rule (short) | Defaults | Grid |
|---|---|---|---|---|
| 9 | cipher-b-dots | WT cross up with WT2 ≤ −level → long; cross down with WT2 ≥ level → short; exit on opposite cross | 4h, 53, 2.5 ATR | tf 2–8, level 41–65 step 6, atr 1.5–3.5 (100) |
| 9 | cipher-b-sommi-flag | money flow > 0, WT2 < 0, WT cross up, 12h WT1−WT2 > 0 → long (mirror short) | **2h**, htf 12h, 2.5 | tf 2–8, htf 12/24, atr (40, invalid combos skipped) |
| 9 | cipher-b-divergence | confirmed WT2 fractal divergence beyond −65/45 × scale | **2h**, 1.0, 2.0 | tf 2–8, scale 0.5–1.25, atr 1–3 (80) |
| 9 | cipher-a-ribbon | EMA 11/33 cross; optional blood-diamond exit | 4h, 2.5, exit on | tf 2–8, atr 1.5–3.5, exit 0/1 (40) |
| 9 | cipher-b-confluence | dots + money flow turning + StochRSI %K vs %D | 4h, 53, 2.5 | as dots (100) |
| 10 | supertrend | SuperTrend(10, 3) direction, line as trailing stop | 4h | tf, atrLen 7/10/13, mult 1.5–4 (72) |
| 10 | squeeze-momentum | squeeze release → linreg momentum direction; exit when momentum fades | 4h, kc 1.5, 2.5 | tf, kc 1–2, atr (100) |
| 10 | ichimoku-cloud | close above cloud + TK + chikou → long; exit below kijun (mirror short) | 4h, 9/26/52, 2.5 | tf, set 1/2, atr (40) |

Synthetic-data pilot (never the research data): at 4h every default cleared 50 trades/yr except cipher-b-divergence
(25–30/yr; 56–62 at 2h) and cipher-b-sommi-flag (3–4/yr: on 4h bars the 12h filter almost never agrees with a 4h
cross-up from below zero; 55–64/yr at 2h), so those two default to 2h, the other timeframe the clone's author tested.

## Expectations and risks

- WaveTrend crosses at extremes are an oscillator-reversal family; at 1h, that family (bollinger-reversion,
  volume-climax-reversal) had no edge before costs. 2–4h bars reduce cost drag but not necessarily the lack of edge.
- Trend-type indicators (ribbon, SuperTrend, Ichimoku) have failed the directionless 2022Q3 and quiet 2023Q3 rule
  quarters in every earlier cycle.
- Multiple testing: 8 baselines take trials 297 → 305; survivors add their grids.

## Results (cycles 9–10, dev window 2021–2023; console `cycle_9_10_console.txt`)

All eight rejected at the dev baseline; trials 297 → 305; no optimization, no OOS run.

| Hypothesis | Net | PF | Sharpe | Max DD | Trades/yr | Edge before costs | Costs | Rule quarters 22Q1 / 22Q3 / 23Q1 / 23Q3 |
|---|---|---|---|---|---|---|---|---|
| cipher-b-dots (4h) | −5.5% | 0.93 | −0.19 | 11.1% | 100 | +0.9% | 8.5% | −1.6 / −2.0 / −1.8 / −0.9 (0 of 4) |
| cipher-b-sommi-flag (2h) | −5.7% | 0.87 | −0.37 | 14.3% | 60 | +3.8% | 9.6% | −2.8 / −0.9 / −0.5 / −4.1 (0 of 4) |
| cipher-b-divergence (2h) | −5.2% | 0.88 | −0.28 | 14.3% | 47 | +0.4% | 6.8% | −2.7 / −1.9 / −3.2 / −3.3 (0 of 4) |
| cipher-b-confluence (4h) | −2.4% | 0.95 | −0.10 | 12.0% | 61 | +1.1% | 5.1% | −0.3 / +0.1 / −0.8 / −0.6 (1 of 4) |
| cipher-a-ribbon (4h) | +23.7% | 1.22 | 0.57 | 12.3% | 73 | +32.9% | 7.4% | −1.0 / −1.4 / **+34.7** / −3.9 (1 of 4) |
| supertrend (4h) | +28.6% | 1.14 | 0.46 | 35.3% | 109 | +72.8% | 27.6% | −3.7 / −10.1 / +23.3 / −5.5 (1 of 4) |
| squeeze-momentum (4h) | −0.4% | 0.99 | 0.02 | 13.1% | 74 | +6.6% | 6.8% | +1.7 / −2.4 / +2.8 / −0.6 (2 of 4) |
| ichimoku-cloud (4h) | +10.2% | 1.10 | 0.34 | 15.2% | 94 | +22.2% | 9.2% | +1.2 / −3.4 / +21.6 / −2.9 (2 of 4) |

Buy-and-hold over the same window: +46.2% (max DD 77%).

- **Market Cipher B has no usable edge here.** Its four signal types (oversold/overbought circles, Sommi flag,
  divergences, confluence) earned 0.4–3.8% *before* costs over three years and lost money in almost every rule quarter.
  WaveTrend extremes on 2–4h BTC bars behave like the 1h reversal ideas of cycles 3–4.
- **Market Cipher A's ribbon is a trend signal** with a real gross edge (+32.9%), but the edge comes almost entirely
  from the 2023Q1 trend; it loses in the chop (2022Q1/Q3) and the quiet drift (2023Q3), like every trend idea so far.
  SuperTrend shows the same shape with a bigger drawdown and heavy funding paid (−$1,663, long through the funding-rich
  bull phases).
- Popular TradingView tools did not beat the research-derived ideas; the best of them (ribbon, Sharpe 0.57) is in line
  with crowding-filtered-breakout (0.85) and max-min-daily (0.60) from sprint 1, which also failed the quarter rule.
