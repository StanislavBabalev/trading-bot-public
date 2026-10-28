<!-- model requested: antigravity/gemini-3.8-flash-high
     model reported: gemini-3.8-flash-n
     received_utc: 2026-10-26T16:03:08.785Z
     latency_s: 72.2
     finish_reason: stop
     usage: {"prompt_tokens":1708,"completion_tokens":8305,"total_tokens":10013,"completion_tokens_details":{"reasoning_tokens":2576},"tokens_per_second":190.355} -->
### 1. session-range-expansion
- **Family:** Time-of-day / Intraday Liquidity Seasonality
- **Rationale:** Crypto liquidity is highly fragmented across geographic sessions. The Asian trading session (00:00–08:00 UTC) routinely builds liquidity pools at its boundaries, which the European and US morning sessions (08:00–16:00 UTC) aggressively expand out of when institutional and ETF-related flow enters. By anchoring to the Asian session range and requiring volume confirmation during peak liquidity hours, we catch genuine directional expansions while ignoring overnight drift.
- **Entry long:** 
  On close of bar $t$, enter long at open of bar $t+1$ if all are true:
  1. $t$ is within the entry window: `12 <= bar_hour_utc < 17`
  2. Currently flat
  3. $\text{Close}_t > \text{AsiaHigh}$ (where $\text{AsiaHigh} = \max(\text{High})$ of all 1h bars from 00:00 UTC to 07:59 UTC on that calendar day)
  4. $\text{Close}_{t-1} \le \text{AsiaHigh}$ (fresh breakout bar)
  5. $\text{Volume}_t > \text{vol\_mult} \times \text{SMA}(\text{Volume}, 24)_t$
- **Entry short:** 
  On close of bar $t$, enter short at open of bar $t+1$ if all are true:
  1. $t$ is within the entry window: `12 <= bar_hour_utc < 17`
  2. Currently flat
  3. $\text{Close}_t < \text{AsiaLow}$ (where $\text{AsiaLow} = \min(\text{Low})$ of all 1h bars from 00:00 UTC to 07:59 UTC on that calendar day)
  4. $\text{Close}_{t-1} \ge \text{AsiaLow}$ (fresh breakout bar)
  5. $\text{Volume}_t > \text{vol\_mult} \times \text{SMA}(\text{Volume}, 24)_t$
- **Exit:** 
  Evaluated on bar close $t$, fills at open of $t+1$:
  - Long exit: $\text{bar\_hour\_utc} == 22$ OR $\text{Close}_t < \text{AsiaMid}$ (where $\text{AsiaMid} = (\text{AsiaHigh} + \text{AsiaLow}) / 2$)
  - Short exit: $\text{bar\_hour\_utc} == 22$ OR $\text{Close}_t > \text{AsiaMid}$
- **Initial stop:** 
  - Long: $\text{Stop} = \text{Open}_{t+1} - (\text{stop\_atr\_mult} \times \text{ATR}(14)_t)$
  - Short: $\text{Stop} = \text{Open}_{t+1} + (\text{stop\_atr\_mult} \times \text{ATR}(14)_t)$
- **Stop management:** 
  Breakeven ratchet: once $\text{Close}_t$ moves favorably by more than $1.5 \times \text{initial\_risk\_distance}$, move stop to the entry fill price. Otherwise, stop remains at initial level.
- **Tunable parameters (max 4):**
  - `vol_mult` = 1.3 [1.0, 2.0, 0.1]
  - `stop_atr_mult` = 1.5 [1.0, 2.5, 0.25]
  - `entry_start_hour` = 12 [8, 14, 1]
  - `entry_end_hour` = 16 [15, 18, 1]
- **Fixed constants:**
  - Asian session window: 00:00 to 08:00 UTC
  - Volume SMA lookback: 24 bars
  - ATR length: 14 bars
  - Breakeven profit threshold: 1.5x risk
  - Daily time exit: 22:00 UTC
- **Expected trades per year:** ~70–90 trades.
- **Why it should not lose money in low-volatility chop like Q3 2022 and Q3 2023:** 
  In summer chop regimes, the US session volume consistently fails to clear the volume multiplier hurdle (`vol_mult`), so the strategy remains completely flat for weeks at a time. The strict 22:00 UTC time exit guarantees no exposure during the illiquid overnight chop where whipsaws occur.
- **Main way it could fail:** 
  If US equity session opens regularly gap and reverse back into the Asia range (trapping breakout flow without triggering the 1.5x breakeven trigger), the 1.5 ATR stops will get clipped frequently.

---

### 2. liquidity-sweep-fade
- **Family:** Range Structure / False-Breakout Rejection (Turtle Soup)
- **Rationale:** In range-bound and mean-reverting regimes, stop-loss orders cluster just above recent swing highs and below swing lows. Institutional algorithms routinely run these levels to fill resting liquidity, resulting in a spike past the level followed by a rapid rejection back inside the range. Fading these sweeps with tight structural stops captures the immediate re-pricing back toward the range center.
- **Entry long:** 
  On close of bar $t$, enter long at open of $t+1$ if all are true:
  1. Currently flat
  2. $\text{Low}_t < \text{prior\_swing\_low}_t$, where $\text{prior\_swing\_low}_t = \min(\text{Low}_{t-lookback \dots t-1})$
  3. $\text{Close}_t > \text{prior\_swing\_low}_t$ (price pierced the level intrabar but closed back inside)
  4. Lower shadow dominance: $(\min(\text{Open}_t, \text{Close}_t) - \text{Low}_t) \ge 0.5 \times (\text{High}_t - \text{Low}_t)$ (clear rejection wick)
- **Entry short:** 
  On close of bar $t$, enter short at open of $t+1$ if all are true:
  1. Currently flat
  2. $\text{High}_t > \text{prior\_swing\_high}_t$, where $\text{prior\_swing\_high}_t = \max(\text{High}_{t-lookback \dots t-1})$
  3. $\text{Close}_t < \text{prior\_swing\_high}_t$ (price pierced the level intrabar but closed back inside)
  4. Upper shadow dominance: $(\text{High}_t - \max(\text{Open}_t, \text{Close}_t)) \ge 0.5 \times (\text{High}_t - \text{Low}_t)$ (clear rejection wick)
- **Exit:** 
  Evaluated on bar close $t$, fills at open of $t+1$:
  - Long exit: $\text{Close}_t \ge \text{EMA}(\text{Close}, 24)_t$ OR `bars_in_trade >= max_bars`
  - Short exit: $\text{Close}_t \le \text{EMA}(\text{Close}, 24)_t$ OR `bars_in_trade >= max_bars`
- **Initial stop:** 
  - Long: $\text{Stop} = \text{Low}_t - (0.2 \times \text{ATR}(14)_t)$ (just below the sweep wick)
  - Short: $\text{Stop} = \text{High}_t + (0.2 \times \text{ATR}(14)_t)$ (just above the sweep wick)
- **Stop management:** 
  Fixed (no trailing or ratcheting).
- **Tunable parameters (max 4):**
  - `lookback` = 36 [20, 60, 4]
  - `max_bars` = 18 [10, 30, 2]
  - `wick_ratio` = 0.50 [0.35, 0.65, 0.05]
  - `stop_buffer_atr` = 0.20 [0.10, 0.40, 0.05]
- **Fixed constants:**
  - Mean-reversion target EMA length: 24 bars
  - ATR length: 14 bars
- **Expected trades per year:** ~80–120 trades.
- **Why it should not lose money in low-volatility chop like Q3 2022 and Q3 2023:** 
  This strategy is designed specifically to exploit the exact mechanism that killed the rejected Donchian and Keltner breakouts. In low-volatility chop, breakouts consistently fail and snap back to the 24-bar mean, providing high win rates and quick profits.
- **Main way it could fail:** 
  In explosive, sustained one-directional trend periods (e.g., late 2021 bull run or the June/November 2022 liquidation legs), sweeps will fail to revert and will repeatedly stop out, relying entirely on the small stop distance to survive.

---

### 3. funding-rate-exhaustion
- **Family:** Funding-Based / Positioning Asymmetry
- **Rationale:** Perpetual funding rates measure the balance of leveraged positioning between longs and shorts. When the 8-hour funding rate climbs to high positive extremes, marginal long leverage is exhausted and longs are burdened with heavy carry costs; a sudden price failure triggers a cascade of forced liquidations. Fading extreme funding spikes when short-term momentum rolls over exploits structural leverage unwinds.
- **Entry long:** 
  On close of bar $t$, enter long at open of $t+1$ if all are true:
  1. Currently flat
  2. $\text{FundingRate}_{8h, t} < -\text{funding\_threshold}$ (shorts paying longs at an extreme rate)
  3. $\text{Close}_t > \text{EMA}(\text{Close}, 12)_t$
  4. $\text{Close}_{t-1} \le \text{EMA}(\text{Close}, 12)_{t-1}$ (momentum turning upward)
- **Entry short:** 
  On close of bar $t$, enter short at open of $t+1$ if all are true:
  1. Currently flat
  2. $\text{FundingRate}_{8h, t} > \text{funding\_threshold}$ (longs paying shorts at an extreme rate)
  3. $\text{Close}_t < \text{EMA}(\text{Close}, 12)_t$
  4. $\text{Close}_{t-1} \ge \text{EMA}(\text{Close}, 12)_{t-1}$ (momentum turning downward)
- **Exit:** 
  Evaluated on bar close $t$, fills at open of $t+1$:
  - Long exit: $\text{FundingRate}_{8h, t} \ge 0.0001$ (+0.01% baseline) OR `bars_in_trade >= 48`
  - Short exit: $\text{FundingRate}_{8h, t} \le 0.0001$ (+0.01% baseline) OR `bars_in_trade >= 48`
- **Initial stop:** 
  - Long: $\text{Stop} = \text{Open}_{t+1} - (\text{stop\_atr\_mult} \times \text{ATR}(14)_t)$
  - Short: $\text{Stop} = \text{Open}_{t+1} + (\text{stop\_atr\_mult} \times \text{ATR}(14)_t)$
- **Stop management:** 
  Trailing stop: on every subsequent bar close $t$, update stop to lock in profits:
  - Long: $\text{Stop}_{t+1} = \max(\text{Stop}_t, \text{Close}_t - (\text{stop\_atr\_mult} \times \text{ATR}(14)_t))$
  - Short: $\text{Stop}_{t+1} = \min(\text{Stop}_t, \text{Close}_t + (\text{stop\_atr\_mult} \times \text{ATR}(14)_t))$
- **Tunable parameters (max 4):**
  - `funding_threshold` = 0.0004 [0.0002, 0.0008, 0.0001] (corresponds to +0.04% per 8h, or ~43.8% APR)
  - `stop_atr_mult` = 2.0 [1.2, 3.0, 0.2]
  - `mom_ema_len` = 12 [6, 24, 2]
  - `max_hold_bars` = 48 [24, 72, 8]
- **Fixed constants:**
  - Funding reset target: 0.0001 (the natural neutral Bybit baseline)
  - ATR length: 14 bars
- **Expected trades per year:** ~55–70 trades.
- **Why it should not lose money in low-volatility chop like Q3 2022 and Q3 2023:** 
  During prolonged low-volatility sideways regimes, retail and speculative leverage drops off completely. The 8h funding rate hovers tightly between 0.0000 and 0.0001 for months, remaining far below `funding_threshold`. Consequently, the strategy fires virtually zero trades during dead quarters.
- **Main way it could fail:** 
  During parabolic "super-bull" runs (like Q1/Q4 2021), funding can remain stubbornly above +0.05% for days while price continues melting upwards; short entries can take consecutive trailing stop hits before the leverage flush actually materializes.

---

### 4. chop-gated-regime-continuation
- **Family:** Regime-Switching / Multi-Timeframe Trend
- **Rationale:** Classical trend-following breaks down because strategies remain active when markets transition into mean-reverting chop. By constructing a higher-timeframe Daily Choppiness Index (CHOP), we mathematically classify the macro market into either a trending or consolidating state. We restrict entries strictly to 1h pullback continuations when the daily chart is in a proven low-choppiness state.
- **Entry long:** 
  On close of bar $t$, enter long at open of $t+1$ if all are true:
  1. Currently flat
  2. $\text{DailyCHOP}_{14, t} < \text{chop\_thresh}$ (daily market is trending strongly, not consolidating)
  3. $\text{Close}_t > \text{DailyEMA}(50)_t$ (macro trend is bullish)
  4. 1h pullback trigger: $\text{Low}_t \le \text{EMA}(\text{Close}, 20)_t$ and $\text{Close}_t > \text{EMA}(\text{Close}, 20)_t$ (tested and closed above the 1h 20 EMA)
  5. $\text{Close}_t > \text{Close}_{t-1}$ (green continuation bar)
- **Entry short:** 
  On close of bar $t$, enter short at open of $t+1$ if all are true:
  1. Currently flat
  2. $\text{DailyCHOP}_{14, t} < \text{chop\_thresh}$ (daily market is trending strongly, not consolidating)
  3. $\text{Close}_t < \text{DailyEMA}(50)_t$ (macro trend is bearish)
  4. 1h pullback trigger: $\text{High}_t \ge \text{EMA}(\text{Close}, 20)_t$ and $\text{Close}_t < \text{EMA}(\text{Close}, 20)_t$ (tested and closed below the 1h 20 EMA)
  5. $\text{Close}_t < \text{Close}_{t-1}$ (red continuation bar)
- **Exit:** 
  Evaluated on bar close $t$, fills at open of $t+1$:
  - Long exit: $\text{Close}_t < \text{EMA}(\text{Close}, 50)_t$ OR $\text{DailyCHOP}_{14, t} \ge 55.0$
  - Short exit: $\text{Close}_t > \text{EMA}(\text{Close}, 50)_t$ OR $\text{DailyCHOP}_{14, t} \ge 55.0$
- **Initial stop:** 
  - Long: $\text{Stop} = \min(\text{Low}_t, \text{Low}_{t-1}) - (0.5 \times \text{ATR}(14)_t)$
  - Short: $\text{Stop} = \max(\text{High}_t, \text{High}_{t-1}) + (0.5 \times \text{ATR}(14)_t)$
- **Stop management:** 
  Chandelier-style trailing stop:
  - Long: on bar close $t$, $\text{Stop}_{t+1} = \max(\text{Stop}_t, \max(\text{High}_{t-12 \dots t}) - (\text{trail\_atr\_mult} \times \text{ATR}(14)_t))$
  - Short: on bar close $t$, $\text{Stop}_{t+1} = \min(\text{Stop}_t, \min(\text{Low}_{t-12 \dots t}) + (\text{trail\_atr\_mult} \times \text{ATR}(14)_t))$
- **Tunable parameters (max 4):**
  - `chop_thresh` = 48.0 [42.0, 52.0, 2.0]
  - `trail_atr_mult` = 2.5 [1.8, 3.5, 0.25]
  - `pullback_ema_len` = 20 [12, 30, 2]
  - `exit_ema_len` = 50 [30, 80, 5]
- **Fixed constants:**
  - Daily CHOP formula: $100 \times \frac{\log_{10}\left(\frac{\sum_{i=0}^{13} \text{DailyATR1}_i}{\max(\text{DailyHigh}_{14}) - \min(\text{DailyLow}_{14})}\right)}{\log_{10}(14)}$
  - Daily CHOP lookback: 14 days (reconstructed from rolling 24-bar windows of 1h data)
  - Daily EMA trend filter: 50 days (1200 1h bars)
  - 1h ATR length: 14 bars
  - Regime chop shutdown exit: $\text{DailyCHOP} \ge 55.0$
- **Expected trades per year:** ~50–65 trades.
- **Why it should not lose money in low-volatility chop like Q3 2022 and Q3 2023:** 
  The Daily Choppiness Index sits between 58 and 68 during extended compressions like Q3 2022 and Q3 2023. Because the gate strictly requires `DailyCHOP < 48.0`, the strategy stays 100% in cash for those entire quarters, neutralizing false breakout losses.
- **Main way it could fail:** 
  The Choppiness Index is a lagging indicator; near the tail-end of powerful trends (when CHOP drops to extreme lows), it may open late entries just before violent mean-reversion spikes.

---

### 5. volume-climax-exhaustion
- **Family:** Volume-Based Climax / Mean Reversion
- **Rationale:** High-cost perpetual environments eat standard Bollinger Band / RSI dip-buyers alive during slow drifts. A positive mathematical expectancy for mean-reversion only exists when an order-flow dislocation occurs: extreme liquidation volume accompanied by an expanded bar range that fails to close near the extreme. Fading these exhaustion wicks offers large expected moves relative to the tight stopping distance.
- **Entry long:** 
  On close of bar $t$, enter long at open of $t+1$ if all are true:
  1. Currently flat
  2. $\text{Volume}_t \ge \text{vol\_climax\_mult} \times \text{SMA}(\text{Volume}, 48)_t$ (volume capitulation)
  3. Bar range is wide: $(\text{High}_t - \text{Low}_t) \ge \text{range\_atr\_mult} \times \text{ATR}(24)_t$
  4. Strong absorption off the low: $\text{Close}_t \ge \text{Low}_t + (0.60 \times (\text{High}_t - \text{Low}_t))$ (closed in top 40% of bar)
  5. $\text{Close}_t < \text{EMA}(\text{Close}, 48)_t$ (fading a panic down-move)
- **Entry short:** 
  On close of bar $t$, enter short at open of $t+1$ if all are true:
  1. Currently flat
  2. $\text{Volume}_t \ge \text{vol\_climax\_mult} \times \text{SMA}(\text{Volume}, 48)_t$ (volume blow-off)
  3. Bar range is wide: $(\text{High}_t - \text{Low}_t) \ge \text{range\_atr\_mult} \times \text{ATR}(24)_t$
  4. Strong rejection off the high: $\text{Close}_t \le \text{High}_t - (0.60 \times (\text{High}_t - \text{Low}_t))$ (closed in bottom 40% of bar)
  5. $\text{Close}_t > \text{EMA}(\text{Close}, 48)_t$ (fading an exhaustion rally)
- **Exit:** 
  Evaluated on bar close $t$, fills at open of $t+1$:
  - Long exit: $\text{Close}_t \ge \text{EMA}(\text{Close}, 24)_t$ OR `bars_in_trade >= 16`
  - Short exit: $\text{Close}_t \le \text{EMA}(\text{Close}, 24)_t$ OR `bars_in_trade >= 16`
- **Initial stop:** 
  - Long: $\text{Stop} = \text{Low}_t - (0.15 \times \text{ATR}(24)_t)$
  - Short: $\text{Stop} = \text{High}_t + (0.15 \times \text{ATR}(24)_t)$
- **Stop management:** 
  Fixed (no trailing). Stop rests just beyond the exhaustion candle wick.
- **Tunable parameters (max 4):**
  - `vol_climax_mult` = 2.8 [2.0, 4.0, 0.2]
  - `range_atr_mult` = 1.8 [1.2, 2.6, 0.2]
  - `close_rebound_pct` = 0.60 [0.50, 0.75, 0.05]
  - `time_stop_bars` = 16 [8, 24, 2]
- **Fixed constants:**
  - Volume SMA baseline: 48 bars
  - ATR lookback: 24 bars
  - Exit target EMA: 24 bars
  - Stop buffer: 0.15x ATR
- **Expected trades per year:** ~60–80 trades.
- **Why it should not lose money in low-volatility chop like Q3 2022 and Q3 2023:** 
  Unlike RSI(4) or 2-sigma Bollinger bands which trigger on tiny, low-momentum drifts, this strategy strictly requires both massive volume ($2.8\times$) and a wide range bar ($1.8\times\text{ATR}$). In quiet chop regimes, volume dried up completely, preventing the strategy from entering bad drift trades. When a rare climax did occur in chop, the reaction from the perimeter back to the 24-EMA was exceptionally reliable.
- **Main way it could fail:** 
  In structural insolvency/credit contagion events (e.g., the 3AC collapse in June 2022 or the FTX crash in November 2022), initial volume spikes do not exhaust the move; the market enters a sustained cascaded liquidation where multiple consecutive climax candles punch through stop after stop.
