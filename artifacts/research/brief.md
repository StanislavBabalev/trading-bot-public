# Research brief (sanitized)

Generated 2026-10-28 by the local research controller. Aggregate metrics only: no code, price or trade rows, credentials or file paths. Review it before sharing it with anyone. Size: ~6415 tokens (estimated as characters / 4).

## Setup
- Instrument: BTCUSDT perpetual, 60-minute bars. Development window 2021-01 to 2023-12; the final holdout is excluded from this brief.
- Costs per side: 0.04% commission + 0.02% slippage; funding on. Sizing: 1% equity risk per trade, max 1x leverage, every entry needs a stop.
- Kill rules: PF < 1.1, Sharpe < 0.5, max DD > 30%, < 50 trades/yr, or more than 1 of 4 rule quarters (2022Q1, 2022Q3, 2023Q1, 2023Q3) failing. A rule quarter passes only if it makes money; sitting it out fails.
- Survivors are tuned over at most 200 grid points (≤ 4 parameters) and must sit on a plateau, not a range edge.
- Parameter sets evaluated so far: 5883. Expected best annualized Sharpe from that many zero-edge trials: 2.88.

## Hypotheses tested (39), default settings on the development window
| name | family | net % | PF | Sharpe | max DD % | trades/yr | turnover x/yr | edge before costs % | costs % | cause | failed rule quarters |
|---|---|---|---|---|---|---|---|---|---|---|---|
| donchian-breakout (DB) | trend | 16.6 | 1.10 | 0.36 | 20.8 | 91 | 76 | 32.9 | 13.6 | weak edge | 2022Q1 2022Q3 2023Q3 |
| bollinger-reversion (BR) | mean-reversion | -35.6 | 0.85 | -1.09 | 38.0 | 230 | 207 | -4.5 | 30.7 | no edge before costs | 2022Q1 2023Q3 |
| keltner-breakout (KB) | vol-breakout | 20.8 | 1.08 | 0.41 | 25.0 | 166 | 147 | 51.2 | 30.0 | edge consumed by costs | 2022Q3 2023Q3 |
| ts-momentum (TM) | momentum | 9.9 | 1.04 | 0.25 | 31.6 | 155 | 133 | 37.3 | 23.8 | edge consumed by costs | 2022Q3 2023Q3 |
| trend-pullback-rsi (TPR) | regime-filter | -16.0 | 0.84 | -0.80 | 16.9 | 162 | 146 | 9.0 | 23.8 | edge consumed by costs | 2022Q3 2023Q3 |
| failed-breakout-fade (FBF) | range-structure | -1.6 | 0.99 | 0.01 | 19.6 | 127 | 154 | 24.8 | 27.4 | edge consumed by costs | 2023Q1 |
| volume-climax-reversal (VCR) | volume | -43.0 | 0.68 | -1.52 | 46.9 | 129 | 167 | -21.0 | 21.6 | no edge before costs | 2022Q1 2022Q3 2023Q1 |
| session-momentum (SM) | time-of-day | -8.0 | 0.71 | -0.65 | 10.5 | 32 | 25 | -3.7 | 4.3 | no edge before costs | 2022Q1 2022Q3 2023Q1 2023Q3 |
| impulse-bar-continuation (IBC) | vol-state | 17.6 | 1.07 | 0.43 | 17.3 | 154 | 199 | 59.9 | 43.1 | edge consumed by costs | 2023Q3 |
| vol-regime-switch (VRS) | regime-switch | 1.7 | 1.01 | 0.12 | 30.1 | 145 | 177 | 30.4 | 29.7 | edge consumed by costs | 2022Q3 |
| donchian-ensemble-trend (DET) | trend-ensemble | 8.9 | 1.23 | 0.48 | 7.4 | 42 | 10 | 13.0 | 1.8 | weak edge | 2022Q3 2023Q3 |
| funding-crowding-fade (FCF) | funding-carry | 10.5 | 1.23 | 0.61 | 6.3 | 58 | 18 | 11.5 | 3.5 | meets the dev PF/Sharpe floors | 2022Q3 |
| noise-area-momentum (NAM) | intraday-momentum | -33.8 | 0.85 | -0.78 | 41.4 | 297 | 413 | 32.5 | 66.5 | edge consumed by costs | 2022Q3 2023Q1 2023Q3 |
| nr-compression-breakout (NCB) | vol-compression | 5.5 | 1.06 | 0.24 | 14.4 | 73 | 55 | 14.8 | 9.9 | edge consumed by costs | 2022Q3 2023Q3 |
| tsmom-vote (TV) | momentum-daily | 1.3 | 1.03 | 0.10 | 10.4 | 57 | 13 | 6.5 | 2.4 | weak edge | 2022Q1 2022Q3 2023Q3 |
| max-min-daily (MMD) | daily-extremes | 7.3 | 1.24 | 0.60 | 4.9 | 69 | 16 | 12.1 | 2.9 | meets the dev PF/Sharpe floors | 2022Q1 2022Q3 2023Q3 |
| mtf-trend-pullback (MTP) | multi-timeframe-trend | -2.9 | 0.93 | -0.13 | 11.2 | 49 | 11 | 1.9 | 2.0 | edge consumed by costs | 2022Q1 2022Q3 2023Q3 |
| efficiency-regime-switch (ERS) | efficiency-regime | -34.6 | 0.87 | -0.72 | 43.9 | 212 | 220 | -1.9 | 30.5 | no edge before costs | 2022Q1 2023Q3 |
| volume-conditioned-reversal (VCR) | price-volume | -0.8 | 0.98 | -0.03 | 10.2 | 76 | 21 | 3.3 | 3.9 | edge consumed by costs | 2022Q1 2023Q3 |
| crowding-filtered-breakout (CFB) | funding-filtered-trend | 15.9 | 1.41 | 0.85 | 5.2 | 48 | 11 | 19.0 | 2.1 | meets the dev PF/Sharpe floors | 2022Q3 2023Q3 |
| funding-tilt (FT) | funding-carry | 7.7 | 1.12 | 0.37 | 8.2 | 75 | 23 | 9.1 | 4.3 | weak edge | 2022Q1 2022Q3 |
| ema-cross-control (ECC) | control | -19.1 | 0.75 | -0.94 | 20.7 | 86 | 26 | -16.6 | 4.1 | no edge before costs | 2022Q1 2023Q1 2023Q3 |
| funding-carry-tilt (FCT) | funding-carry | 2.9 | 1.11 | 0.26 | 4.8 | 37 | 9 | 2.6 | 1.6 | edge consumed by costs | 2022Q3 |
| funding-fade-wide (FFW) | funding-carry | 21.3 | 1.37 | 0.98 | 5.5 | 66 | 20 | 22.5 | 4.1 | meets the dev PF/Sharpe floors | 2022Q1 |
| cipher-b-dots (CBD) | oscillator-reversal | -5.5 | 0.93 | -0.19 | 11.1 | 100 | 48 | 0.9 | 8.5 | edge consumed by costs | 2022Q1 2022Q3 2023Q1 2023Q3 |
| cipher-b-sommi-flag (CBSF) | oscillator-continuation | -5.7 | 0.87 | -0.37 | 14.3 | 60 | 53 | 3.8 | 9.6 | edge consumed by costs | 2022Q1 2022Q3 2023Q1 2023Q3 |
| cipher-b-divergence (CBD) | oscillator-divergence | -5.2 | 0.88 | -0.28 | 14.3 | 47 | 37 | 0.4 | 6.8 | edge consumed by costs | 2022Q1 2022Q3 2023Q1 2023Q3 |
| cipher-a-ribbon (CAR) | ema-ribbon | 23.7 | 1.22 | 0.57 | 12.3 | 73 | 39 | 32.9 | 7.4 | meets the dev PF/Sharpe floors | 2022Q1 2022Q3 2023Q3 |
| cipher-b-confluence (CBC) | oscillator-reversal | -2.4 | 0.95 | -0.10 | 12.0 | 61 | 28 | 1.1 | 5.1 | edge consumed by costs | 2022Q1 2023Q1 2023Q3 |
| supertrend (S) | atr-trend | 28.6 | 1.14 | 0.46 | 35.3 | 109 | 120 | 72.8 | 27.6 | weak edge | 2022Q1 2022Q3 2023Q3 |
| squeeze-momentum (SM) | vol-compression | -0.4 | 0.99 | 0.02 | 13.1 | 74 | 38 | 6.6 | 6.8 | edge consumed by costs | 2022Q3 2023Q3 |
| ichimoku-cloud (IC) | cloud-trend | 10.2 | 1.10 | 0.34 | 15.2 | 94 | 51 | 22.2 | 9.2 | weak edge | 2022Q3 2023Q3 |
| dca-grid-long (DGL) | dca-grid | -8.9 | 0.77 | -0.83 | 13.1 | 547 | 28 | -2.4 | 4.7 | no edge before costs | 2022Q1 2022Q3 2023Q3 |
| dca-grid-short (DGS) | dca-grid | -7.4 | 0.84 | -0.62 | 8.9 | 565 | 32 | -4.0 | 5.5 | no edge before costs | 2022Q1 2022Q3 2023Q1 |
| dca-rsi-long (DRL) | dca-grid | -2.3 | 0.74 | -0.46 | 5.5 | 91 | 5 | -1.2 | 0.9 | no edge before costs | 2022Q1 2022Q3 2023Q3 |
| dca-trend-long (DTL) | dca-grid | -5.4 | 0.75 | -0.67 | 8.3 | 290 | 15 | -1.3 | 2.6 | no edge before costs | 2022Q1 2022Q3 2023Q3 |
| cascade-fade (CF) | liquidation-cascade | -46.6 | 0.75 | -1.45 | 49.5 | 132 | 202 | -17.3 | 28.0 | no edge before costs | 2022Q1 2022Q3 2023Q1 2023Q3 |
| turmoil-reversal (TR) | turmoil-liquidity-provision | 2.4 | 1.05 | 0.16 | 10.5 | 42 | 25 | 6.9 | 4.5 | edge consumed by costs | 2023Q1 |
| round-number-bounce (RNB) | price-barrier | -31.0 | 0.87 | -0.65 | 43.3 | 123 | 178 | -0.2 | 29.5 | no edge before costs | 2022Q1 2022Q3 2023Q1 |

Rules of each (so new ideas do not repeat them):
- donchian-breakout: Long when close > highest high of the previous entryLen bars, short when close < lowest low of the previous entryLen bars; exit long when close < lowest low of the previous exitLen bars (short: close > highest high of previous exitLen bars); stop at entry-bar close -/+ ATR(14) x atrMult.
- bollinger-reversion: Long when close < SMA(bbLen) - bbMult x stdev(bbLen), short when close > SMA + bbMult x stdev; exit when close returns to the SMA or after 48 bars; stop at entry-bar close -/+ ATR(14) x atrMult.
- keltner-breakout: Long when close > EMA(emaLen) + bandMult x ATR(14), short when close < EMA - bandMult x ATR(14); exit when close crosses back over the EMA; stop at entry-bar close -/+ ATR(14) x atrMult.
- ts-momentum: Long when close / close[lookback] - 1 > threshold, short when < -threshold, otherwise keep the position; reversals flip directly; stop at entry-bar close -/+ ATR(14) x atrMult.
- trend-pullback-rsi: Long when close > EMA(trendLen) and RSI(rsiLen) < level; short when close < EMA(trendLen) and RSI > 100 - level; exit long when RSI > 55 (short: RSI < 45) or after 48 bars; stop at entry-bar close -/+ ATR(14) x atrMult.
- failed-breakout-fade: PDH/PDL = previous UTC day's high/low, M = midpoint. Long when close >= PDL after 1..breakBars consecutive closes below PDL, the lowest low of those bars <= PDL - excursionAtr x ATR(14), and M - close >= 1 ATR (short: the mirror at PDH); exit when the close reaches M (taken at entry) or after maxHold bars; stop at the excursion extreme -/+ stopBufferAtr x ATR.
- volume-climax-reversal: Long when volume >= volMult x mean volume of the previous 48 bars, range >= rangeAtr x previous ATR(14), lower wick >= 50% of the range and the low is the lowest of the last 24 bars (short: the mirror with the upper wick and 24-bar high); exit after maxHold bars; stop 0.25 ATR beyond the wick.
- session-momentum: On the 11:00 UTC bar's close, R = close - that day's 00:00 open, S = ATR(14) x sqrt(12); long if R >= moveMult x S, short if R <= -moveMult x S (fill at the 12:00 open); exit after holdBars bars; stop at signal close -/+ atrMult x ATR.
- impulse-bar-continuation: Long when true range >= rangeAtr x previous ATR(14), the bar closes up and its body >= bodyFrac x range (short: the mirror); exit after holdBars bars; stop at signal close -/+ atrMult x ATR.
- vol-regime-switch: Quiet when ATR(14) < its median over regimeLen bars, else expanding; HH/LL = previous channelLen bars. Quiet: long on a green close in the bottom edgeFrac of the channel (short: red close in the top edge), exit at the channel midpoint or after 24 bars, stop 0.5 ATR beyond the channel. Expanding: long on close > HH (short: < LL), exit on the opposite channelLen/2 channel, stop atrMult x ATR trailed on every close.
- donchian-ensemble-trend: Four Donchian models with lookbacks baseDays x 1, 2, 4, 8 days; each turns long on a close above the high of its previous bars (short below the low) and keeps that state. Long on a fresh breakout of the fastest channel when >= minAgree models are long (short: mirror); exit when long models no longer outnumber short ones; stop trails the best close by trailMult x dATR.
- funding-crowding-fade: Rank = percentile of the mean of the last 9 settled funding rates (3 days) among the previous 270 such means (90 days). Rank >= 1 - quantile (crowded longs): short when the close crosses below EMA(24); rank <= quantile (crowded shorts): long when it crosses above. Exit after holdBars; stop stopMult x dATR.
- noise-area-momentum: UTC-day session. sigma_h = mean |close/day open - 1| at hour h over the previous lookbackDays days; bands = open x (1 +/- bandMult x sigma_h). From firstHour to 22:00, long when the close moves above the upper band (short: below the lower), at most once per UTC day; exit when the close falls back below max(upper band, day VWAP) (short: above min(lower, VWAP)) or at the 23:00 close; stop at the day open.
- nr-compression-breakout: A day whose range is narrower than each of the previous nrLen - 1 days sets up the next UTC day: the first close above its high goes long (below its low: short), once. Stop stopMult x setup range beyond the broken level; exit after holdBars.
- tsmom-vote: On each daily close, vote = signs of the returns over baseDays, 2 x baseDays and 4 x baseDays days; hold the majority direction (unanimous = 1: only when all three agree, else flat), re-evaluated daily (a stopped-out position is re-entered next day if the vote still holds). Stop stopMult x dATR from the signal close.
- max-min-daily: Long only. On a daily close >= the highest close of the previous lookbackDays days (trend) or <= the lowest (reversion), go long; each signal day renews the hold; exit after holdDays day closes without a signal. Stop stopMult x dATR.
- mtf-trend-pullback: Daily regime = sign(daily close - SMA of the last maDays daily closes). Up regime: long when the hourly close crosses back above EMA(entryLen); down regime: short on a cross below. Exit when the daily regime no longer matches; stop trails the best close by trailMult x dATR.
- efficiency-regime-switch: ER = |net change| / sum |changes| over erLen hourly closes. ER >= erThreshold: long on a close above the previous 48-bar high (short below the low), exit on the opposite 24-bar channel. ER below it: fade a close outside SMA(48) +/- 2 sd, exit at the SMA or after 48 bars. Stop atrMult x ATR(14) from the signal close; a trade keeps its own rules if the regime flips.
- volume-conditioned-reversal: A day is extreme when its |return| is in the top extremeFrac of the previous 90 days. Volume below the previous 30 days' median: fade it; above: follow it. Hold holdDays days (a new extreme day renews or flips). Stop stopMult x dATR.
- crowding-filtered-breakout: Long on a close above the high of the previous channelDays x 24 bars only if the 3-day funding rank (as in funding-crowding-fade) is <= maxRank; short below the low only if the rank is >= 1 - maxRank (maxRank 1 = no filter). Exit on the opposite channel of half the length; stop trails the best close by trailMult x dATR.
- funding-tilt: funding-crowding-fade with the crowding threshold widened up to the median split: short on a close crossing below EMA(24) when the 3-day funding rank >= 1 - threshold, long on a cross above when <= threshold (0.5 = take every cross, on the side opposite the funding tilt). Exit after holdBars; stop stopMult x dATR.
- ema-cross-control: Ablation control for funding-tilt, funding ignored: long when the close crosses above EMA(24), short when it crosses below; exit after holdBars; stop stopMult x dATR.
- funding-carry-tilt: Funding alone, no price timing: on each daily close, short when the 3-day funding rank >= 0.5 + band, long when <= 0.5 - band, else flat; re-evaluated daily. Stop stopMult x dATR.
- funding-fade-wide: funding-crowding-fade (rank = 3-day mean funding vs the previous 90 days; short on a close crossing below EMA(24) when rank >= 1 - quantile, long on a cross above when rank <= quantile; exit after holdBars; stop stopMult x dATR) with the quantile range centred on cycle 5's edge optimum.
- cipher-b-dots: Market Cipher B green/red circles on tfHours bars: long when WT1 crosses above WT2 with WT2 <= -level, short when it crosses below with WT2 >= level; exit on the opposite WT cross (flip if that cross is a signal); stop atrMult x ATR(14) of the period bars.
- cipher-b-sommi-flag: Market Cipher B 'Sommi flag': long when money flow (RSI+MFI area 60, x150, -2.5) > 0, WT2 < 0, WT1 crosses up and the WT1 - WT2 of the last completed htfHours bar is > 0; short: the mirror. Exit on the opposite WT cross; stop atrMult x ATR(14) of the period bars.
- cipher-b-divergence: Market Cipher B divergences: a 5-bar WT2 bottom fractal <= -65 x limitScale with a lower price low but higher WT2 than the previous such fractal goes long when confirmed; a top fractal >= 45 x limitScale with a higher high and lower WT2 goes short. Exit on the opposite WT cross; stop atrMult x ATR(14) of the period bars.
- cipher-a-ribbon: Market Cipher A: EMA(11) crossing above EMA(33) goes long, below goes short; with diamondExit = 1 a long also exits on a 'blood diamond' (bearish WT cross on the period EMA(5) crosses under EMA(11)), a short on the mirror; stop atrMult x ATR(14) of the period bars.
- cipher-b-confluence: Market Cipher B confluence: the cipher-b-dots circle (WT cross at +/-level) only when money flow is turning the same way and Stochastic RSI (log, 14/14/3/3) %K is above %D for a buy (below for a sell). Exit on the opposite WT cross; stop atrMult x ATR(14) of the period bars.
- supertrend: SuperTrend(atrLen, mult) on tfHours bars: hold its direction and flip with it; the SuperTrend line is the trailing stop; a stop-out is re-entered on the next period close if the direction still holds.
- squeeze-momentum: LazyBear Squeeze Momentum (20): when Bollinger(2 sd) leaves the Keltner channel (kcMult x SMA of true range) after a squeeze, enter in the direction of linreg momentum; exit when momentum turns back toward zero; stop atrMult x ATR(14) of the period bars.
- ichimoku-cloud: Ichimoku 9/26/52/26 (periodSet 1) or 20/60/120/30 (periodSet 2): long when the close is above the cloud, tenkan > kijun and the close is above the close displacement-1 periods ago, on the period that first becomes true; exit on a close below kijun; short: the mirror; stop atrMult x ATR.
- dca-grid-long: Veles DCA bot, long, no entry filter: market base order, then orders - 1 averaging limits spread evenly over overlap below it, each (1 + martingale) times the previous notional; take-profit limit takeProfit above the running average entry; stop 5% below the last order; the next deal starts at the next open.
- dca-grid-short: dca-grid-long mirrored: market short, averaging sell limits spread over overlap above it, take-profit below the average, stop 5% above the last order.
- dca-rsi-long: dca-grid-long (5 orders, martingale 15%) that starts a deal only when RSI(14) of 1h closes is below rsiLevel at a bar's close.
- dca-trend-long: dca-grid-long (5 orders, martingale 15%) that starts a deal only while the last daily close is above the SMA of the last maDays daily closes.
- cascade-fade: Fade a liquidation cascade: long when an hourly bar's close - open <= -dropAtr x ATR(14) on volume >= volMult x the previous 48 bars' mean while the 3-day funding crowding rank >= 0.5 (longs crowded); short on the mirror with rank <= 0.5. Exit after holdBars or on a close back beyond the cascade bar's open; stop 1 ATR beyond the cascade bar's extreme.
- turmoil-reversal: On 4h bars: turmoil when 4h ATR(14) / open is >= the volQuantile quantile of its previous 540 values (90 days); in turmoil fade a 4h body of >= zEntry x the previous 4h ATR (long after a drop, short after a spike). Exit on a 4h close back across EMA(20) of 4h closes or after holdPeriods 4h bars; stop 1 4h-ATR beyond the period's extreme.
- round-number-bounce: Levels = multiples of levelStep USD. Long when a bar opens above the nearest level below it, trades down to it and closes back at or above it, with no bar at or below that level in the previous freshBars hours (first test); short on the mirror at the nearest level above. Skip bars reaching both levels. Exit after holdBars; stop 1 ATR(14) beyond the level.

## Quarter by quarter (net % of each continuous dev run; rule quarters marked *)
| quarter | BTC % | vol % | efficiency | DB | BR | KB | TM | TPR | FBF | VCR | SM | IBC | VRS | DET | FCF | NAM | NCB | TV | MMD | MTP | ERS | VCR | CFB | FT | ECC | FCT | FFW | CBD | CBSF | CBD | CAR | CBC | S | SM | IC | DGL | DGS | DRL | DTL | CF | TR | RNB | losing runs |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 2021Q1 | 104 | 105 | 0.21 | -4.1 | -5.8 | 2.2 | 1.3 | -3.8 | -5.5 | -6.0 | -0.4 | 0.6 | -12.2 | -1.5 | -0.7 | -9.1 | 7.1 | 3.9 | 2.5 | -0.3 | -11.3 | 2.3 | -1.5 | 1.2 | -4.5 | -0.4 | 0.2 | 2.6 | 1.7 | 3.8 | -2.9 | 3.7 | 43.9 | 3.2 | -1.3 | -1.9 | -2.0 | 1.4 | -1.9 | -4.3 | -0.6 | 0.4 | 22 |
| 2021Q2 | -41 | 99 | 0.16 | -2.8 | -9.8 | 5.4 | -7.9 | -6.0 | 7.5 | -7.2 | -4.1 | 9.7 | -9.7 | -2.7 | 4.6 | 7.0 | -3.4 | -3.9 | -1.1 | -1.9 | -8.7 | -2.8 | 0.4 | 0.7 | -2.9 | 2.0 | 3.2 | -1.8 | 2.0 | 1.4 | 2.5 | -2.0 | -14.7 | -1.1 | 0.6 | -5.7 | -1.5 | 0.2 | -3.5 | -6.2 | -0.2 | 4.8 | 24 |
| 2021Q3 | 25 | 66 | 0.08 | -4.8 | 2.7 | 3.5 | -5.2 | -2.8 | -0.3 | -4.9 | 2.0 | 10.2 | 5.5 | 1.0 | -1.5 | -2.7 | -7.2 | 1.2 | 0.1 | 1.0 | 0.7 | 2.0 | -0.7 | -0.5 | -0.2 | -1.2 | 2.8 | 0.6 | -0.6 | 2.4 | -3.6 | -1.0 | 5.2 | -1.2 | -3.3 | 1.1 | -2.4 | 0.5 | -1.0 | 11.4 | 1.7 | 10.8 | 19 |
| 2021Q4 | 6 | 62 | 0.02 | 0.1 | 0.6 | -1.3 | -2.3 | 2.2 | 0.6 | -0.5 | 0.2 | -5.3 | 5.7 | 1.9 | 0.5 | 3.7 | 2.5 | 2.7 | 1.3 | -0.2 | 1.8 | -0.9 | 1.5 | 0.8 | 0.3 | -1.6 | -0.2 | 0.4 | 3.4 | 1.1 | 2.0 | -1.2 | 1.9 | 2.6 | -2.0 | -1.0 | 0.9 | -0.6 | -1.2 | 0.3 | 1.2 | 1.3 | 13 |
| 2022Q1* | -1 | 66 | 0.01 | -2.1 | -3.6 | 1.9 | 26.5 | 2.7 | 4.1 | -11.1 | -1.9 | 0.8 | 13.9 | 0.7 | 3.4 | 2.6 | 2.3 | -3.1 | -0.9 | -0.3 | -11.6 | -1.0 | 1.9 | -2.9 | 0.4 | 2.0 | -1.4 | -1.6 | -2.8 | -2.7 | -1.4 | -0.3 | -4.0 | 1.7 | 0.5 | -0.7 | -1.6 | -1.7 | -0.5 | -15.6 | 6.3 | -11.1 | 23 |
| 2022Q2 | -56 | 77 | 0.31 | 11.8 | -5.2 | 3.9 | 3.7 | -1.3 | -8.1 | -6.9 | 0.4 | 12.3 | -3.2 | 3.4 | 2.4 | 7.6 | 3.0 | 1.2 | -1.4 | 2.5 | 3.8 | -0.2 | 2.2 | 3.6 | -7.4 | 1.5 | 8.5 | -1.5 | -2.0 | 0.5 | 1.5 | -2.3 | 3.4 | -3.6 | 1.5 | -3.1 | 4.2 | -1.2 | 0.0 | -10.7 | -3.6 | -10.8 | 18 |
| 2022Q3* | -3 | 61 | 0.01 | -5.7 | 10.4 | -8.9 | -9.1 | -4.0 | 5.4 | -6.4 | -3.7 | 2.1 | -7.2 | -3.1 | -0.9 | -15.3 | -1.2 | -1.1 | -0.4 | -1.6 | 0.6 | 0.1 | -1.8 | -1.2 | 2.0 | -2.0 | 0.2 | -2.5 | -0.9 | -1.9 | -1.3 | -0.4 | -10.3 | -2.4 | -3.3 | -0.2 | -0.8 | -1.8 | 0.0 | -1.6 | -0.5 | -7.1 | 32 |
| 2022Q4 | -15 | 46 | 0.11 | -1.8 | -14.4 | -4.2 | -17.3 | 1.8 | -7.3 | -4.9 | -0.0 | -2.6 | -10.6 | -1.7 | -2.0 | -9.5 | -2.9 | -1.8 | -0.8 | -3.2 | -20.1 | -0.2 | -1.3 | 1.6 | 2.8 | -1.6 | 0.9 | 6.9 | -0.7 | 0.5 | -3.8 | 6.7 | -7.7 | -5.5 | 2.5 | -1.0 | 0.6 | 0.6 | 0.0 | -11.4 | 0.1 | -1.4 | 28 |
| 2023Q1* | 72 | 54 | 0.28 | 36.1 | 0.5 | 28.1 | 23.0 | 7.0 | -2.2 | -6.3 | -0.6 | 4.9 | 18.7 | 9.2 | 5.9 | -1.2 | 0.9 | 5.9 | 3.8 | 8.7 | 21.7 | 7.2 | 13.7 | 8.5 | -3.7 | 3.8 | 4.4 | -1.9 | -0.5 | -3.2 | 33.7 | -0.8 | 22.9 | 2.8 | 21.5 | 1.6 | -4.3 | 0.3 | 1.0 | -14.3 | -3.2 | -11.0 | 13 |
| 2023Q2 | 7 | 42 | 0.05 | -1.9 | -4.5 | -2.2 | -8.4 | -4.6 | 0.3 | 1.5 | 1.5 | -4.9 | 7.0 | -0.0 | 0.5 | 2.7 | -0.0 | -3.0 | 1.2 | -4.5 | -3.6 | -2.8 | -0.1 | 1.5 | -3.6 | -0.0 | 3.7 | -1.9 | -0.6 | -1.0 | -3.3 | -1.2 | -1.4 | 2.1 | -5.0 | 1.1 | 0.5 | 0.4 | 1.1 | -0.5 | 1.1 | -4.5 | 24 |
| 2023Q3* | -12 | 32 | 0.14 | -12.0 | -1.3 | -14.4 | -10.0 | -3.5 | 0.8 | 3.8 | -3.1 | -4.6 | 2.8 | -4.3 | 2.2 | -15.8 | -7.0 | -2.4 | -1.4 | -4.2 | -5.3 | -2.6 | -2.7 | 0.8 | -2.1 | 3.7 | 1.1 | -0.9 | -4.0 | -3.3 | -3.9 | -0.6 | -6.0 | -0.6 | -2.9 | -0.6 | 0.6 | -0.8 | -0.8 | -1.3 | 0.3 | 2.7 | 29 |
| 2023Q4 | 57 | 41 | 0.28 | 10.4 | -10.5 | 10.6 | 27.1 | -4.1 | 4.5 | -5.1 | 1.4 | -4.6 | -3.7 | 6.4 | -3.9 | -6.7 | 13.0 | 2.4 | 4.5 | 2.0 | -3.8 | -1.5 | 4.4 | -5.8 | -1.6 | -3.1 | -3.4 | -3.6 | -0.6 | -2.7 | 7.2 | -2.7 | 4.4 | 2.1 | 3.3 | 1.5 | -1.4 | 0.3 | 1.3 | -3.5 | 0.2 | -7.8 | 20 |

## Findings
- 17 of 39 hypotheses: edge consumed by costs (keltner-breakout, ts-momentum, trend-pullback-rsi, failed-breakout-fade, impulse-bar-continuation, vol-regime-switch, noise-area-momentum, nr-compression-breakout, mtf-trend-pullback, volume-conditioned-reversal, funding-carry-tilt, cipher-b-dots, cipher-b-sommi-flag, cipher-b-divergence, cipher-b-confluence, squeeze-momentum, turmoil-reversal).
- 11 of 39 hypotheses: no edge before costs (bollinger-reversion, volume-climax-reversal, session-momentum, efficiency-regime-switch, ema-cross-control, dca-grid-long, dca-grid-short, dca-rsi-long, dca-trend-long, cascade-fade, round-number-bounce).
- 6 of 39 hypotheses: weak edge (donchian-breakout, donchian-ensemble-trend, tsmom-vote, funding-tilt, supertrend, ichimoku-cloud).
- 5 of 39 hypotheses: meets the dev PF/Sharpe floors (funding-crowding-fade, max-min-daily, crowding-filtered-breakout, funding-fade-wide, cipher-a-ribbon).
- Median turnover 38x equity a year, median cost drag 2.3% of starting equity a year (commission + slippage); median edge before costs 6.6% over the whole window.
- Rule quarter 2022Q1: failed by 24 of 39. BTC -1.5%, realized vol 66% (dev median 61%), efficiency 0.01 (median 0.12), daily range 5.3%.
- Rule quarter 2022Q3: failed by 30 of 39. BTC -2.6%, realized vol 61% (dev median 61%), efficiency 0.01 (median 0.12), daily range 4.9%.
- Rule quarter 2023Q1: failed by 13 of 39. BTC +71.9%, realized vol 54% (dev median 61%), efficiency 0.28 (median 0.12), daily range 4.2%.
- Rule quarter 2023Q3: failed by 29 of 39. BTC -11.5%, realized vol 32% (dev median 61%), efficiency 0.14 (median 0.12), daily range 2.5%.
- Quarters where the most continuous dev runs lost money: 2022Q3 (32 of 39), 2023Q3 (29 of 39), 2022Q4 (28 of 39).
- 5883 parameter sets have been evaluated on the development data; the best of that many zero-edge trials would show an annualized Sharpe near 2.88. A candidate's dev Sharpe needs to clear that, not just the 0.5 floor, to mean much.

## Directions
- Costs decide many outcomes (17 of 39): each round trip costs 0.12% of notional. Prefer fewer, longer trades (signals on 4h/daily structure, longer holds) or edges per trade well above that; avoid more 1h fade/continuation variants.
- The most-failed rule quarters were 2022Q1 directionless (efficiency 0.01 vs median 0.12, BTC -1%); 2022Q3 directionless (efficiency 0.01 vs median 0.12, BTC -3%); 2023Q3 quiet (vol 32% vs median 61%). A quarter spent flat still fails the rule, so a filter that stands aside is not enough: an idea has to earn something in quiet or range-bound markets too.
- Families not tried yet: day-of-week-seasonality, volatility-targeted-trend. Funding-rate history is already available locally.
- Keep the locked OOS window out of idea generation; propose ideas from the development evidence only.

## Requested output
Propose at most 5 new, distinct hypotheses as JSON only, in this shape (it is validated locally before anything is built):
```json
{ "hypotheses": [ { "name": "kebab-case-name", "family": "...", "entry": "...", "exit": "...", "stop": "...", "rationale": "...",
    "parameters": [ { "name": "lookback", "default": 48, "min": 24, "max": 96, "step": 24 } ] } ] }
```
Each: 1-4 parameters, defaults on their grid, grid ≤ 192 points, a stop on every entry, rules computable from 60-minute OHLCV and funding without look-ahead.
