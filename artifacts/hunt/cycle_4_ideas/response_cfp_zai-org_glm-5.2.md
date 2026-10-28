<!-- model requested: cfp/zai-org/glm-5.2
     model reported: zai-org/glm-5.2
     received_utc: 2026-10-26T16:09:56.490Z
     latency_s: 96.9
     prompt: prompt_short.md, max_tokens 12000
     finish_reason: stop
     usage: {"prompt_tokens":1351,"completion_tokens":5419,"total_tokens":6770,"estimated":true,"tokens_per_second":57.764} -->
### 1. vol-filtered-range-breakout
- **Family:** Volatility-State
- **Rationale:** Standard breakouts failed in Q3 2023 because they triggered on low-volatility noise without price momentum. This strategy requires a "range expansion" event (High-Low > threshold) before allowing a breakout, filtering out the quiet chop periods.
- **Entry long:** Close > (High[15] + Low[15]) / 2 + ATR[14] AND (High - Low) > 2 * ATR[10]
- **Entry short:** Close < (High[15] + Low[15]) / 2 - ATR[14] AND (High - Low) > 2 * ATR[10]
- **Exit:** If Long: Close < (High[15] + Low[15]) / 2 - ATR[14] OR 48 bars elapsed. If Short: Mirror.
- **Initial stop:** (High[15] + Low[15]) / 2 +/- ATR[14]
- **Stop management:** None (fixed ATR stop).
- **Tunable parameters:** `atr_mult = 1` [0.5, 2, 0.5], `vol_filter_mult = 2` [1.5, 3, 0.5]
- **Fixed constants:** `lookback = 15`, `atr_len = 14`, `atr_len_vol = 10`, `max_hold = 48`
- **Expected trades per year:** 40-60 (requires volatility expansion).
- **Why it should not lose money in chop:** The `vol_filter_mult` condition ensures the market must be moving significantly (expanding range) to trigger; low-volatility chop will not generate signals.
- **Main way it could fail:** It might miss large, slow trend moves that lack the sharp intrabar range expansion required for entry.

### 2. funding-aided-trend
- **Family:** Funding
- **Rationale:** 8h funding rates are a strong proxy for sentiment. When longs are paying shorts aggressively (>0.01%), it signals overextension of the long side. This strategy enters longs only when funding is high and price is already above the trend, intending to catch the final push before liquidation cascades. Shorts are the inverse.
- **Entry long:** Close > EMA[50] AND 8h Funding > 0.01%
- **Entry short:** Close < EMA[50] AND 8h Funding < -0.01%
- **Exit:** Close < EMA[20] OR 40 bars elapsed.
- **Initial stop:** EMA[20] +/- 0.5 * ATR[14]
- **Stop management:** Once in profit > 2 * ATR[14], trailing stop moves to entry price +/- 1 * ATR[14].
- **Tunable parameters:** `funding_thresh = 0.01` [0.005, 0.03, 0.001], `trail_start_mult = 2` [1, 4, 0.5]
- **Fixed constants:** `ema_long = 50`, `ema_exit = 20`, `atr_len = 14`, `max_hold = 40`
- **Expected trades per year:** 20-30 (depends on funding rate frequency).
- **Why it should not lose money in chop:** It requires both a trend (price above EMA) and a sentiment trigger (high funding). In chop, funding rates are usually near zero, so no signal is generated.
- **Main way it could fail:** Funding rates can remain elevated during prolonged downtrends (liquidation cascades), potentially leading to a large stop loss loss if the trend reverses.

### 3. session-momentum-breakout
- **Family:** Time-of-Day
- **Rationale:** BTC often exhibits directional bias during active overlapping sessions (Asia/Europe). Global macro news tends to release during these windows. This strategy ignores signals outside these hours to avoid the low-volume, unpredictable periods that plagued Q3 2023.
- **Entry long:** Close > High[40] AND Bar Hour in [08:00, 21:00] UTC
- **Entry short:** Close < Low[40] AND Bar Hour in [08:00, 21:00] UTC
- **Exit:** Close < Low[20] OR 60 bars elapsed.
- **Initial stop:** High[20] +/- ATR[14]
- **Stop management:** None (fixed ATR stop).
- **Tunable parameters:** `lookback = 40` [20, 80, 10], `session_end = 21` [18, 23, 1]
- **Fixed constants:** `session_start = 8`, `atr_len = 14`, `max_hold = 60`
- **Expected trades per year:** 50-70.
- **Why it should not lose money in chop:** By restricting trading to active hours, it avoids the "dead" market periods where price action is random noise.
- **Main way it could fail:** If a major trend develops during the "quiet" off-hours (e.g., 01:00-07:00 UTC), the strategy will miss it entirely.

### 4. vwap-reversion
- **Family:** Range Structure
- **Rationale:** The rejected "Bollinger Reversion" failed because it faded extremes in low volatility without checking range size. This uses VWAP as a dynamic mean. It only fades moves if the range is large enough (indicating a potential exhaustion), rather than fading every small fluctuation.
- **Entry long:** Close < VWAP[50] AND (High - Low) > 1.5 * ATR[14] AND Close > VWAP[200]
- **Entry short:** Close > VWAP[50] AND (High - Low) > 1.5 * ATR[14] AND Close < VWAP[200]
- **Exit:** Close > VWAP[20] OR 48 bars elapsed.
- **Initial stop:** VWAP[20] +/- 0.5 * ATR[14]
- **Stop management:** None (fixed ATR stop).
- **Tunable parameters:** `vol_filter_mult = 1.5` [1.0, 3.0, 0.5], `vwap_fast = 20` [10, 50, 5]
- **Fixed constants:** `vwap_slow = 50`, `atr_len = 14`, `max_hold = 48`
- **Expected trades per year:** 35-55.
- **Why it should not lose money in chop:** The `vol_filter_mult` condition requires the bar to be wide (trending) to enter; in chop, bars are narrow, so entries are skipped.
- **Main way it could fail:** It may get stopped out on a "whipsaw" if price briefly crosses VWAP in a volatile but non-trending environment.

### 5. adaptive-momentum-vol
- **Family:** Adaptive Trend
- **Rationale:** The rejected "TS Momentum" failed because it entered on 1% moves regardless of volatility, often catching the top of a small range. This requires the move to be significant *and* the volatility to be high enough to allow the stop to be placed comfortably without getting eaten by costs.
- **Entry long:** Close > Price[60] * 1.005 AND (High - Low) > 1.2 * ATR[14]
- **Entry short:** Close < Price[60] * 0.995 AND (High - Low) > 1.2 * ATR[14]
- **Exit:** Close < Price[60] OR 48 bars elapsed.
- **Initial stop:** Close +/- 0.5 * ATR[14]
- **Stop management:** Once profit > 3 * ATR[14], trailing stop moves to Low[EntryBar] +/- 2 * ATR[14].
- **Tunable parameters:** `momentum_pct = 0.5` [0.1, 1.0, 0.1], `trail_start_mult = 3` [2, 5, 0.5]
- **Fixed constants:** `lookback = 60`, `atr_len = 14`, `max_hold = 48`
- **Expected trades per year:** 40-60.
- **Why it should not lose money in chop:** Low volatility bars fail the `vol_filter`, preventing entry. High volatility bars ensure the stop distance is large enough to cover costs.
- **Main way it could fail:** It might miss high-momentum, low-volatility breakouts (e.g., a fast 1% move on a tiny range) because the volatility check fails.
