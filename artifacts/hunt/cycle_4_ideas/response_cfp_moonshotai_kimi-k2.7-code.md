<!-- model requested: cfp/moonshotai/kimi-k2.7-code
     model reported: moonshotai/kimi-k2.7-code
     received_utc: 2026-10-26T16:11:37.062Z
     latency_s: 85.6
     prompt: prompt_short.md, max_tokens 12000
     finish_reason: stop
     usage: {"prompt_tokens":1351,"completion_tokens":2870,"total_tokens":4221,"estimated":true,"tokens_per_second":35.251} -->
### 1. volatility-expansion-trend
- Family: Volatility-State / Adaptive Trend
- Rationale: BTC trends aggressively after volatility expansion (ATR relative to history). Enters only when market is "awake," filtering Q3 2022 chop where ATR was low.
- Entry long: Close > Previous Close + (ATR(n) * mult_entry)
- Entry short: Close < Previous Close - (ATR(n) * mult_entry)
- Exit: Close < Entry Close - (ATR(n) * mult_exit) OR Close > Entry Close + (ATR(n) * mult_exit)
- Initial stop: Below lowest low of last L bars
- Stop management: None
- Tunable parameters: atr_period = 24 [14, 48], mult_entry = 1.0 [0.5, 2.0], mult_exit = 1.0 [0.5, 2.0], lookback_stop = 12 [8, 24]
- Fixed constants: None
- Expected trades per year: 60-100
- Why it should not lose money in chop: ATR is low; entry signal rarely triggers.
- Main way it could fail: Volatility expands on a reversal (fakeout).

### 2. funding-scaled-reversion
- Family: Funding / Gated Mean Reversion
- Rationale: Longs paying high funding implies market is extended. Scaling size inversely to funding reduces risk and costs; reverts to VWAP.
- Entry long: Funding > threshold% for n consecutive 8h bars AND Close < 24h VWAP
- Entry short: Funding < -threshold% for n consecutive 8h bars AND Close > 24h VWAP
- Exit: Close > VWAP (long) OR Close < VWAP (short)
- Initial stop: Below 2 * 24h ATR
- Stop management: None
- Tunable parameters: funding_threshold = 0.05 [0.01, 0.10], funding_bars = 3 [2, 5], atr_period = 24 [14, 48], vwap_mult = 0.5 [0.2, 1.0]
- Fixed constants: None
- Expected trades per year: 30-50
- Why it should not lose money in chop: Funding rarely spikes in chop; signals are sparse.
- Main way it could fail: Funding can remain high (trend) without reversion.

### 3. session-range-breakout
- Family: Time-of-Day / Range Structure
- Rationale: London session (7-12 UTC) captures specific global liquidity. Breakout of the session range is more reliable than daily range.
- Entry long: Close > (High of session range) AND Close < High of session range
- Entry short: Close < (Low of session range) AND Close > Low of session range
- Exit: Close < 0.8 * (Session Low) OR Close > 0.8 * (Session High)
- Initial stop: Below session low OR Above session high
- Stop management: None
- Tunable parameters: start_hour = 7 [5, 9], end_hour = 12 [10, 14], range_window = 24 [12, 48], exit_mult = 0.8 [0.5, 1.0]
- Fixed constants: None
- Expected trades per year: 40-70
- Why it should not lose money in chop: Volatility is higher during this window, reducing false breakouts.
- Main way it could fail: Liquidity gaps or session overlap issues.

### 4. keltner-volatility-filter
- Family: Volatility-State / Gated Breakout
- Rationale: Standard Keltner channels ignore volatility scaling. Using ATR(20) to scale the channel ensures breakouts only occur when range is expanding.
- Entry long: Close > (EMA(Close, 20) + ATR(20) * vol_mult) AND ATR(20) > mean_ATR(20)
- Entry short: Close < (EMA(Close, 20) - ATR(20) * vol_mult) AND ATR(20) > mean_ATR(20)
- Exit: Close < EMA(Close, 20) OR Close > EMA(Close, 20)
- Initial stop: Below (EMA - ATR * vol_mult) OR Above (EMA + ATR * vol_mult)
- Stop management: None
- Tunable parameters: ema_period = 20 [14, 28], atr_period = 20 [14, 28], vol_mult = 2.0 [1.5, 3.0], vol_filter = 1.0 [0.5, 1.5]
- Fixed constants: None
- Expected trades per year: 50-80
- Why it should not lose money in chop: Requires ATR > mean_ATR, filtering quiet periods.
- Main way it could fail: EMA cross is slow; whipsaws in volatile markets.

### 5. rsi-funding-regime
- Family: Regime-Switching / Volatility-State
- Rationale: Combines RSI momentum with funding cost structure. Only trades when funding is favorable (longs pay) and RSI indicates oversold/overbought.
- Entry long: Funding > 0 AND RSI(close, 14) < 30
- Entry short: Funding < 0 AND RSI(close, 14) > 70
- Exit: RSI(close, 14) > 50 (long) OR RSI(close, 14) < 50 (short)
- Initial stop: Below 2 * ATR(14)
- Stop management: None
- Tunable parameters: rsi_period = 14 [10, 20], funding_min = 0 [0, 0.05], atr_period = 14 [10, 20], rsi_exit = 50 [45, 55]
- Fixed constants: None
- Expected trades per year: 60-100
- Why it should not lose money in chop: Funding is usually flat/negative in chop; short setups are rare.
- Main way it could fail: RSI and funding can diverge (e.g., strong trend with low funding).
