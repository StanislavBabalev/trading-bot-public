<!-- model requested: auto
     model reported: deepseek-v4-pro
     received_utc: 2026-10-26T16:14:27.323Z
     latency_s: 24.9
     prompt: prompt.md, max_tokens 16000
     finish_reason: stop
     usage: {"prompt_tokens":2080,"completion_tokens":3935,"total_tokens":6015,"estimated":true,"tokens_per_second":175.328} -->
# Five Testable Hypotheses for Bybit BTCUSDT Perp (1h)

All five are deliberately built around **what killed the rejected set**: Q3 2022 and Q3 2023 were low-volatility, low-trend, range-bound quarters where any always-on trend/breakout/momentum engine gets chopped. Every hypothesis below either (a) *turns itself off* in low-volatility chop via an explicit volatility/regime gate, or (b) is structurally a short-duration trade that only fires on rare events, so it does not bleed across a quiet quarter. I state weaknesses honestly.

---

### 1. vol-gated-range-fade

- **Family:** Mean reversion with a volatility-state gate (only trades *quiet* regimes, the inverse of the rejected ideas).
- **Rationale:** In BTC perps, the quiet-regime weeks (Q3 2022, Q3 2023) are exactly where price mean-reverts inside a stable range, while high-volatility regimes (2021 tops, 2022 crashes) are where fading gets you run over. The rejected Bollinger fade failed because it faded *every* regime, including the trending ones. This one only fades when realized volatility is *below* its own recent median, i.e. when the range is likely to hold, and it uses the range boundary itself (not a band) plus a tight invalidation.
- **Entry long:** On bar close, if `ATR14(close) < median(ATR14, 720 bars)` (quiet regime) AND `close < min(low, 48 bars)` is false but `close` is within the lowest 10% of the 48-bar range AND `close > open` (a down-probe that closes green). Operationally: `range48 = highest(high,48) - lowest(low,48)`; `pos = (close - lowest(low,48)) / range48`; enter long if `quiet AND pos < 0.10 AND close > open`.
- **Entry short:** Mirror: `quiet AND pos > 0.90 AND close < open`.
- **Exit:** Close position at next open when `close` crosses back through the 48-bar midpoint (`(highest(high,48)+lowest(low,48))/2`), OR after 24 bars, OR on stop.
- **Initial stop:** `lowest(low, 24) - 0.5 * ATR14` for longs (mirror for shorts). This is a structural stop just beyond the recent probe low, so the 1% risk sizing gives a sane notional.
- **Stop management:** Fixed (no trailing — trailing turns this into a trend trade and reintroduces chop bleed).
- **Tunable parameters:**
  - `atr_len` = 14 [10, 21, 1]
  - `range_len` = 48 [24, 96, 8]
  - `pos_thresh` = 0.10 [0.05, 0.20, 0.01]
  - `vol_median_len` = 720 [480, 1440, 120]
- **Fixed constants:** exit at 48-bar midpoint; max hold 24 bars; stop buffer 0.5 ATR.
- **Expected trades per year:** ~35–55 (the quiet gate cuts trade count hard; this is the main risk to the ≥50 rule).
- **Why it should not lose money in Q3 2022 / Q3 2023:** Those quarters *are* the quiet regime this strategy is designed for — it fires only when ATR is below its 30-day median, which is exactly the Q3 2022/Q3 2023 condition, and it fades range extremes with a structural stop rather than chasing breakouts. It should be *most active and most accurate* in those quarters.
- **Main way it could fail:** The quiet gate may not produce 50 trades/year (kill rule), and a "quiet" regime can still trend slowly (e.g. a grinding drift), which makes range-fade entries get stopped repeatedly with small losses that costs amplify. Also, `close > open` filter is a weak confirmation and may be optimized away.

---

### 2. overnight-session-drift

- **Family:** Time-of-day / seasonality.
- **Rationale:** BTC perp liquidity and flow are dominated by US and Asian session opens; the 00:00–08:00 UTC window (Asian overnight) has historically shown persistent directional drift in trending years and mean-reverting chop in others. A session-conditional momentum trade that only holds during a fixed overnight window avoids the intraday chop that killed the always-on ideas, and its short holding period limits cost drag per trade.
- **Entry long:** At the close of the 23:00 UTC bar (i.e. entering at 00:00 UTC open), if the 24-bar return `close/close[24] - 1 > 0` AND `close > EMA200` (daily-scale trend filter built from 1h bars: EMA of close over 200 * 24 = 4800 1h bars — see fixed constants note) → go long for the overnight session.
- **Entry short:** At the same bar, if `close/close[24] - 1 < 0` AND `close < EMA200` → go short.
- **Exit:** At the close of the 07:00 UTC bar (exit at 08:00 UTC open), unconditionally, OR on stop.
- **Initial stop:** `entry_price ∓ 1.5 * ATR14` (1.5 ATR from entry).
- **Stop management:** Fixed.
- **Tunable parameters:**
  - `entry_hour` = 23 [20, 2, 1] (UTC hour of the bar whose close triggers entry; default 23 = enter at 00:00)
  - `hold_hours` = 8 [4, 12, 1]
  - `mom_lookback` = 24 [12, 48, 6]
  - `atr_mult` = 1.5 [1.0, 2.5, 0.25]
- **Fixed constants:** EMA200 on the *daily* timeframe approximated as EMA(4800) on 1h closes (conventional daily trend filter; not tuned).
- **Expected trades per year:** ~200–260 (one per day, filtered by trend — this comfortably clears 50).
- **Why it should not lose money in Q3 2022 / Q3 2023:** The trade is only *held* 8 hours and is gated by a 24-bar momentum + daily-trend filter, so in a flat chop quarter the momentum filter will often be near zero and the EMA200 filter will keep it out of counter-trend overnight holds; losses per trade are small and bounded by the 1.5 ATR stop, and the short holding period means very few funding payments.
- **Main way it could fail:** Time-of-day edges are notoriously regime-dependent and can decay; if the overnight drift flips sign in the held-out period the strategy has no way to know. Also, the daily EMA(4800) is a slow filter that may keep it flat for long stretches (reducing trades) or lag badly at trend turns.

---

### 3. volume-climax-reversal

- **Family:** Volume-based exhaustion / mean reversion (event-driven, not always-on).
- **Rationale:** BTC perps exhibit volume climaxes at local extremes: a bar with volume far above its recent average plus a wide range often marks capitulation (longs) or blow-off (shorts) that mean-reverts over the next few hours. Because this only fires on *rare* high-volume events, it is dormant through quiet chop like Q3 2022/Q3 2023 and does not bleed there — the opposite failure mode of the rejected always-on fades.
- **Entry long:** On bar close, if `volume > 3 * SMA(volume, 48)` AND `close < open` (down bar) AND `(high - low) > 2 * ATR14` (wide range) AND `close` is in the bottom 25% of the bar's range (`(close - low)/(high - low) < 0.25`) → long at next open.
- **Entry short:** Mirror: `volume > 3 * SMA(volume,48)` AND `close > open` AND `(high-low) > 2*ATR14` AND `(close-low)/(high-low) > 0.75` → short at next open.
- **Exit:** Close at next open when `close` crosses back through the entry bar's midpoint (`(entry_bar_high + entry_bar_low)/2`), OR after 12 bars, OR on stop.
- **Initial stop:** `entry_bar_low - 0.25 * ATR14` for longs (mirror for shorts) — just beyond the climax bar's extreme.
- **Stop management:** Fixed.
- **Tunable parameters:**
  - `vol_mult` = 3.0 [2.0, 5.0, 0.5]
  - `vol_sma_len` = 48 [24, 96, 8]
  - `range_atr_mult` = 2.0 [1.5, 3.0, 0.25]
  - `max_hold` = 12 [6, 24, 2]
- **Fixed constants:** close-position thresholds 0.25/0.75; stop buffer 0.25 ATR; ATR length 14.
- **Expected trades per year:** ~30–60 (event-driven; the ≥50 rule is a real risk — the vol_mult default of 3.0 may need to sit near the low end of the range to clear 50).
- **Why it should not lose money in Q3 2022 / Q3 2023:** In quiet quarters, 3× volume spikes with 2× ATR wide ranges are rare, so the strategy simply does not trade — it cannot bleed in chop because it is flat. Its few trades are at genuine exhaustion points where short-horizon mean reversion is strongest.
- **Main way it could fail:** Too few trades to clear 50/year at default `vol_mult=3.0`; and a volume climax in a strong trend (e.g. a crash continuation) can keep going, so the "reversal" is really a knife-catch with a structural stop that gaps. Costs are a large fraction of the small average win.

---

### 4. atr-regime-trend-switch

- **Family:** Regime-switching / adaptive trend (trend only when volatility *expands*, flat when it contracts).
- **Rationale:** The rejected trend ideas (Donchian, Keltner, EMA cross) failed in low-vol chop because they kept taking breakouts that immediately reversed. A trend edge in BTC perps is concentrated in volatility-expansion regimes; when ATR is contracting relative to its recent level, breakouts are noise. This strategy runs a simple Donchian-style trend entry *only* when `ATR14 > its 240-bar median` (expansion regime) and is flat otherwise — explicitly turning itself off in Q3 2022/Q3 2023-style contraction.
- **Entry long:** On bar close, if `ATR14 > median(ATR14, 240)` (expansion) AND `close > highest(high, 48)[1]` (close above prior 48-bar high) → long at next open.
- **Entry short:** Mirror: expansion AND `close < lowest(low, 48)[1]` → short at next open.
- **Exit:** Close at next open on opposite 24-bar channel close (`close < lowest(low,24)` exits long; `close > highest(high,24)` exits short), OR on stop.
- **Initial stop:** `entry_price ∓ 2.0 * ATR14` (2 ATR), i.e. a wider stop so the 1% risk sizing yields modest notional and breakout noise doesn't stop it out.
- **Stop management:** Trailing: move stop to `max(current_stop, close - 2.0*ATR14)` for longs each bar close (mirror for shorts). This is the one trailing idea, justified because expansion regimes trend.
- **Tunable parameters:**
  - `breakout_len` = 48 [24, 96, 8]
  - `exit_len` = 24 [12, 48, 6]
  - `atr_mult` = 2.0 [1.5, 3.0, 0.25]
  - `vol_median_len` = 240 [120, 480, 60]
- **Fixed constants:** ATR length 14; expansion gate uses strict `>`.
- **Expected trades per year:** ~40–70 (expansion gate + 48-bar breakout is selective; ≥50 is plausible but not guaranteed).
- **Why it should not lose money in Q3 2022 / Q3 2023:** In those quarters ATR was contracting below its median for much of the time, so the expansion gate keeps the strategy flat — it does not take the false breakouts that killed the earlier Donchian/Keltner ideas. It only re-engages when volatility expands, which is when trends actually pay.
- **Main way it could fail:** This is a thin variant risk relative to the rejected Donchian — the *only* real difference is the volatility gate, so it may still be judged a thin variant; if ATR-median gating doesn't cleanly separate the chop quarters it degenerates into rejected idea #1. Also, expansion regimes can be one-bar spikes (news) that immediately reverse, producing whipsaw losses at 2 ATR.

---

### 5. funding-extreme-contrarian (the ONE funding-based idea)

- **Family:** Funding-based sentiment contrarian.
- **Rationale:** Funding is the one positioning signal available; persistently extreme positive funding means longs are crowded and paying heavily, historically a contrarian short signal near local tops, and extreme negative funding marks capitulation lows. Because extreme funding is rare, the strategy is dormant in neutral chop like Q3 2022/Q3 2023 and trades only at sentiment extremes — structurally different from price-pattern ideas and not a thin variant of anything rejected. It "earns" the funding input because funding is a direct positioning proxy that OHLCV cannot provide.
- **Entry long:** On the bar close at/after a funding print (funding is 8-hourly; the bar closing at 00:00, 08:00, 16:00 UTC), if `funding_rate < -0.01%` (i.e. < -0.0001) AND `close > EMA(48)` (don't catch a falling knife below trend) → long at next open.
- **Entry short:** Mirror: `funding_rate > +0.03%` (> 0.0003) AND `close < EMA(48)` → short at next open.
- **Exit:** Close at next open when funding reverts to the neutral band (`|funding| < 0.005%`) OR after 24 bars OR on stop.
- **Initial stop:** `entry_price ∓ 1.5 * ATR14`.
- **Stop management:** Fixed.
- **Tunable parameters:**
  - `funding_long_thresh` = -0.0001 [-0.0003, -0.00005, 0.00005]
  - `funding_short_thresh` = 0.0003 [0.0001, 0.0006, 0.00005]
  - `ema_len` = 48 [24, 96, 12]
  - `max_hold` = 24 [12, 48, 6]
- **Fixed constants:** ATR length 14; neutral funding band 0.00005; stop 1.5 ATR.
- **Expected trades per year:** ~25–50 (extreme funding is rare; the ≥50 rule is the biggest threat — the short threshold default 0.03% may need to be near the low end).
- **Why it should not lose money in Q3 2022 / Q3 2023:** Funding in those quiet quarters hovered near neutral/slightly positive, rarely crossing the extreme thresholds, so the strategy is mostly flat and cannot bleed in the chop. Its few trades occur at genuine crowding extremes with a trend filter and 1.5 ATR stop.
- **Main way it could fail:** Too few trades to satisfy ≥50/year; and "extreme funding" can persist for weeks in a strong bull (2021), so a contrarian short against persistent positive funding gets repeatedly stopped in an uptrend — the EMA(48) filter helps but does not eliminate this. Funding thresholds are also the kind of parameter that optimizes into overfit.

---

## Cross-cutting honesty notes

- **Trade-count risk is the dominant kill-rule threat** for ideas 1, 3, and 5 (all event/gate-driven). Their defaults are chosen to be genuinely believed, but if any fails the ≥50-trades/year rule at defaults it is rejected outright — I am flagging that rather than hiding it.
- **Idea 4 is the closest to a "thin variant"** of rejected #1 (Donchian); the volatility-expansion gate is the only substantive addition. It may not survive a strict novelty check — treat it as the weakest of the five.
- **Only idea 5 uses funding**, as permitted.
- All five share the same weakness: their edge is conditional on a regime that the *development window* happens to contain (quiet quarters for 1/3/5, expansion for 2/4). The held-out period is the real test; none of these should be optimized hard, and Deflated Sharpe will punish the ones with 4 parameters most.
