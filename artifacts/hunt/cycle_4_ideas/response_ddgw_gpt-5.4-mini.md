<!-- model requested: ddgw/gpt-5.4-mini
     model reported: gpt-5.4-mini
     received_utc: 2026-10-26T16:01:56.588Z
     latency_s: 22.3
     finish_reason: stop
     usage: {"prompt_tokens":2376,"completion_tokens":3444,"total_tokens":5820,"estimated":true,"tokens_per_second":166.755} -->
### 1. adaptive-trend-pullback-with-volatility-gate
- Family: regime-switching / adaptive trend
- Rationale: BTC often trends strongly after volatility expansion, but the failed breakout ideas in your tests likely kept firing during quiet chop. This hypothesis only trades in markets that are both directional and active enough to overcome costs, then buys/sells pullbacks inside that trend instead of chasing breakouts. It should reduce false entries in low-volatility ranges while still participating in sustained moves.
- Entry long: On bar close, go long if all are true: 1) close > EMA(200) of 1h closes; 2) EMA(20) > EMA(50); 3) 20-bar rolling ATR% = ATR(14)/close is above its 252-bar median; 4) close is below EMA(20) and above EMA(50); 5) the current close is at least 0.5 * ATR(14) below the highest close of the last 20 bars.
- Entry short (or "none, long-only"): Mirror the long rules: close < EMA(200), EMA(20) < EMA(50), ATR% above 252-bar median, close above EMA(20) and below EMA(50), and current close is at least 0.5 * ATR(14) above the lowest close of the last 20 bars.
- Exit: Exit on bar close when close crosses back through EMA(20) against the position, or after 72 bars, whichever comes first.
- Initial stop: Long: min(low of entry bar, lowest low of last 10 bars) - 1.5 * ATR(14). Short: max(high of entry bar, highest high of last 10 bars) + 1.5 * ATR(14).
- Stop management (trailing/breakeven, or "fixed"): After the trade has unrealized profit of 1.5R, move stop to breakeven plus 0.1R. After 2.5R, trail by 1.8 * ATR(14) from the highest high/lowest low since entry.
- Tunable parameters (max 4): trend-ema-fast = 20 [10, 40, 5]  
  trend-ema-slow = 50 [30, 120, 10]  
  atr-mult-stop = 1.5 [1.0, 2.5, 0.25]  
  pullback-threshold-atr = 0.5 [0.25, 1.5, 0.25]
- Fixed constants: EMA(200), ATR(14), ATR% median lookback 252 bars, max holding 72 bars, breakeven at 1.5R, trail at 2.5R, trail distance 1.8 * ATR(14).
- Expected trades per year: 90-180
- Why it should not lose money in low-volatility chop like Q3 2022 and Q3 2023: The ATR% median gate blocks most signals when volatility is compressed, and the EMA(200) plus EMA alignment prevents taking both sides of a flat range. Pullback entries also require an established trend and a retracement, which is less likely to trigger repeatedly in dead chop.
- Main way it could fail: If BTC enters a slow grind trend with suppressed ATR, the volatility gate may exclude the best moves and leave only late entries.

### 2. volatility-contraction-breakout-with-confirmation
- Family: volatility-state / breakout
- Rationale: Breakouts are weak in BTC when they happen inside noisy consolidation, but stronger when they follow a prolonged contraction. This idea requires a volatility squeeze first, then waits for directional expansion with volume confirmation, which should avoid the repeated false breakouts that hurt your prior channel systems. It aims to trade the first expansion out of compressed regimes rather than random channel pokes.
- Entry long: On bar close, go long if all are true: 1) close > highest high of the last 20 completed bars; 2) 20-bar ATR% is below its 120-bar median; 3) current volume > 1.25 * SMA(20) of volume; 4) close > open of the signal bar; 5) the prior 5 bars had a net range compression, defined as highest high of last 5 bars minus lowest low of last 5 bars < 0.6 * ATR(14) * 5.
- Entry short (or "none, long-only"): Mirror the long rules with close < lowest low of last 20 completed bars and close < open.
- Exit: Exit on close back inside the 20-bar high/low channel against the position, or after 48 bars, whichever comes first.
- Initial stop: Long: lowest low of the last 10 bars minus 1.0 * ATR(14). Short: highest high of the last 10 bars plus 1.0 * ATR(14).
- Stop management (trailing/breakeven, or "fixed"): After 1R, move stop to breakeven. After 2R, trail by 2.0 * ATR(14) from the best close since entry.
- Tunable parameters (max 4): channel-lookback = 20 [10, 40, 5]  
  contraction-lookback = 120 [60, 240, 20]  
  volume-multiplier = 1.25 [1.0, 2.0, 0.1]  
  stop-atr-mult = 1.0 [0.75, 2.0, 0.25]
- Fixed constants: ATR(14), channel confirmation uses completed bars only, max holding 48 bars, breakeven at 1R, trail at 2R, trail distance 2.0 * ATR(14).
- Expected trades per year: 70-140
- Why it should not lose money in low-volatility chop like Q3 2022 and Q3 2023: The contraction filter requires a pre-breakout squeeze, but the volume and close-direction confirmation should sharply reduce random intrarange pokes. In quiet chop, most touches of the channel should fail the contraction-plus-volume test.
- Main way it could fail: In prolonged low-volatility regimes, BTC may still produce enough fake expansion bars to trigger entries that reverse immediately.

### 3. session-open-range-impulse
- Family: time-of-day / seasonality
- Rationale: BTC has persistent intraday liquidity and volatility patterns even though it trades 24/7. The first hours of the UTC day often set a directional tone, and follow-through is stronger when the opening range is broken with above-normal volume. This hypothesis uses a narrow opening range and a same-day bias, which can be easier to hold through costs than random intraday mean reversion.
- Entry long: Define the UTC day open range as the high/low of the first 3 hourly bars of each UTC day. After the third bar of the day closes, go long when a later bar closes above the opening-range high by at least 0.25 * ATR(14), provided: 1) the day’s cumulative volume so far is above the 20-day average cumulative volume at the same hour; 2) close > VWAP of the current UTC day computed from bar typical price and volume; 3) the prior close was not already above the opening-range high by 0.25 * ATR(14).
- Entry short (or "none, long-only"): Mirror using the opening-range low, close below day VWAP, and short break below the range by 0.25 * ATR(14).
- Exit: Exit at the first of: day’s UTC 20:00 bar close, close back inside the opening range, or 24 bars after entry.
- Initial stop: Long: min(opening-range low, entry bar low) - 1.0 * ATR(14). Short: max(opening-range high, entry bar high) + 1.0 * ATR(14).
- Stop management (trailing/breakeven, or "fixed"): At 1R profit, move stop to breakeven. After that, trail by 1.25 * ATR(14) behind the best close since entry.
- Tunable parameters (max 4): opening-range-bars = 3 [2, 6, 1]  
  breakout-buffer-atr = 0.25 [0.0, 0.75, 0.25]  
  stop-atr-mult = 1.0 [0.75, 2.0, 0.25]  
  max-hold-bars = 24 [12, 48, 4]
- Fixed constants: Day VWAP uses same-day bars only, volume confirmation uses 20-day same-hour average, breakeven at 1R, trail distance 1.25 * ATR(14), forced exit at 20:00 UTC.
- Expected trades per year: 120-220
- Why it should not lose money in low-volatility chop like Q3 2022 and Q3 2023: It trades only when price escapes the day’s early balance area and is accepted above/below VWAP with volume confirmation. In chop, opening-range breaks are less likely to get the volume and VWAP alignment needed for entry.
- Main way it could fail: BTC can trend before the opening range is established, causing missed moves or late entries that reverse.

### 4. volume-climax-fade-with-regime-filter
- Family: volume-based / mean reversion with regime gate
- Rationale: BTC often overreacts to one-bar volume spikes, but fading every spike is dangerous in trending markets. This hypothesis only fades exhaustion after a large one-bar move and volume climax, while requiring the broader market to be range-like rather than trending. That should improve on your prior mean-reversion ideas by making the trigger more selective and by avoiding strong trend regimes.
- Entry long: Go long on bar close if all are true: 1) close < open on the signal bar; 2) the bar’s true range is at least 1.8 * ATR(14); 3) volume on the signal bar is at least 2.0 * SMA(20) volume; 4) close is below the 20-bar lower Bollinger band computed on closes with 2 standard deviations; 5) 50-bar ADX computed from 1h OHLC is below 18.
- Entry short (or "none, long-only"): Mirror the rules with an up bar closing above the 20-bar upper Bollinger band and a high-volume expansion.
- Exit: Exit when close re-enters the middle Bollinger band, or after 36 bars, whichever comes first.
- Initial stop: Long: signal-bar low minus 1.0 * ATR(14). Short: signal-bar high plus 1.0 * ATR(14).
- Stop management (trailing/breakeven, or "fixed"): Once price moves 1R in favor, move stop to breakeven. No further trailing.
- Tunable parameters (max 4): volume-multiplier = 2.0 [1.5, 4.0, 0.25]  
  tr-multiplier = 1.8 [1.2, 3.0, 0.2]  
  adx-threshold = 18 [12, 25, 1]  
  max-hold-bars = 36 [12, 72, 6]
- Fixed constants: ATR(14), Bollinger length 20, Bollinger stdev 2, ADX length 50, breakeven at 1R.
- Expected trades per year: 80-160
- Why it should not lose money in low-volatility chop like Q3 2022 and Q3 2023: The ADX filter explicitly avoids low-directionality chop masquerading as opportunity, and the volume-climax requirement means most small oscillations are ignored. Only unusually large dislocations in otherwise non-trending conditions get traded.
- Main way it could fail: In crisis periods, high-volume extremes can keep extending instead of reverting, producing repeated stop-outs.

### 5. daily-trend-weekend-mean-reversion-gate
- Family: regime-switching / calendar effect
- Rationale: BTC exhibits different behavior across the weekly cycle, with weekend liquidity often thinner and short-term dislocations more likely to revert, while weekdays carry more trend persistence. A simple calendar gate can separate a trend-following mode from a controlled mean-reversion mode, reducing the tendency to fight strong weekday trends while still harvesting weekend noise. This is testable using only timestamps and OHLCV.
- Entry long: If the bar timestamp is Saturday or Sunday UTC, go long when close is below the 24-bar SMA by at least 1.5 * ATR(14), provided the 24-bar return is not less than -4% and 24-bar ADX is below 22. On Monday through Friday, go long only when close > EMA(100) and close crosses back above SMA(20) after being below it for at least 6 bars.
- Entry short (or "none, long-only"): If the bar timestamp is Saturday or Sunday UTC, go short when close is above the 24-bar SMA by at least 1.5 * ATR(14), provided the 24-bar return is not greater than +4% and 24-bar ADX is below 22. On Monday through Friday, go short only when close < EMA(100) and close crosses back below SMA(20) after being above it for at least 6 bars.
- Exit: Weekend trades exit on a close back to the 24-bar SMA or after 18 bars. Weekday trades exit on a close back through SMA(20) against the position or after 60 bars.
- Initial stop: Weekend long: weekend signal low minus 1.0 * ATR(14); weekend short: weekend signal high plus 1.0 * ATR(14). Weekday long: lowest low of last 8 bars minus 1.25 * ATR(14); weekday short: highest high of last 8 bars plus 1.25 * ATR(14).
- Stop management (trailing/breakeven, or "fixed"): Move stop to breakeven after 1R for all trades. For weekday trades only, trail by 1.5 * ATR(14) after 2R.
- Tunable parameters (max 4): weekend-deviation-atr = 1.5 [1.0, 3.0, 0.25]  
  weekend-adx-threshold = 22 [15, 30, 1]  
  weekday-sma-length = 20 [10, 40, 5]  
  weekday-trend-ema = 100 [50, 200, 10]
- Fixed constants: ATR(14), weekend uses UTC Saturday/Sunday, weekend max hold 18 bars, weekday max hold 60 bars, 6-bar persistence filter, breakeven at 1R.
- Expected trades per year: 100-190
- Why it should not lose money in low-volatility chop like Q3 2022 and Q3 2023: The weekday side only participates when a higher-timeframe trend exists, while the weekend side requires both a large deviation and low ADX, so it is not simply fading every noisy bar. That should materially reduce the repeated false signals that hurt your prior breakout systems in quiet quarters.
- Main way it could fail: If BTC’s weekday/weekend microstructure changes, the calendar edge may disappear and the regime split may become arbitrary.
