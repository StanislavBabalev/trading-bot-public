<!-- model requested: claude-sonnet-5-5
     model reported: claude-sonnet-5-5
     received_utc: 2026-10-26T16:08:07.186Z
     latency_s: 155.8
     prompt: prompt.md, max_tokens 32000
     finish_reason: stop
     usage: {"prompt_tokens":2262,"completion_tokens":18805,"total_tokens":21067,"reasoning_tokens":13346,"completion_tokens_details":{"reasoning_tokens":13346},"tokens_per_second":121.334} -->
# Common definitions (used in all five)

- **TR_t** = max(H_t − L_t, |H_t − C_{t−1}|, |L_t − C_{t−1}|)
- **ATR_t(14)** = Wilder RMA of TR (alpha = 1/14, seeded with the SMA of the first 14 TR). It is evaluated on the bar that just closed (t), and "ATR_{t−1}" means the value one bar earlier.
- Signal bar = t. The order fills at the open of t+1. Stops are set at the fill using the signal bar's ATR: stop = fill price ∓ distance.
- Signals that appear while a position is open are ignored, unless a strategy says otherwise.
- **Funding:** I deliberately did not use it. Funding changes every 8h and extremes are rare, so a funding signal would struggle to reach 50 trades per year. Its edge would also mostly overlap with trend regime, so it doesn't clearly earn its slot.

---

### 1. failed-breakout-trap
- **Family:** Range/structure (fading failed breaks of the prior-day range)
- **Rationale:** The trend and breakout ideas that failed bled from false breakouts of recent ranges, and this idea trades the other side of those. Prior-UTC-day high and low are widely watched levels. Stops cluster just beyond them, and breaks that fail to hold tend to snap back toward the middle of the range. The stop sits just past the failed excursion, so risk is structural and defined.
- **Entry long:** Let PDL and PDH be the low and high of the previous completed UTC day (24 bars, 00:00–23:00). The levels used for every comparison are those of the UTC day containing bar t. Let j be the number of consecutive bars immediately before t (t−1, t−2, …) with Close < PDL. Enter long at the next open if all of these hold:
  - C_t ≥ PDL
  - 1 ≤ j ≤ m
  - min(Low over those j bars) ≤ PDL − d·ATR_t
  - (PDH+PDL)/2 − C_t ≥ 1.0·ATR_t (the fixed cost filter described below)
- **Entry short:** The mirror image. j counts consecutive closes above PDH. Require C_t ≤ PDH, 1 ≤ j ≤ m, max(High over those j bars) ≥ PDH + d·ATR_t, and C_t − (PDH+PDL)/2 ≥ 1.0·ATR_t.
- **Exit:** Whichever comes first:
  - A close crosses the prior-day midpoint M = (PDH+PDL)/2 (long: C ≥ M; short: C ≤ M). The exit fills at the next open.
  - H bars have elapsed since entry (exit order on the close of the H-th bar held).
  - The stop is hit.
- **Initial stop:** Long: lowest Low of the excursion bars − b·ATR_t. Short: highest High of the excursion bars + b·ATR_t.
- **Stop management:** Fixed.
- **Tunable parameters:**
  - m = 6 [3, 12, 3] (max bars the break may last)
  - d = 0.3 [0.0, 0.6, 0.15] (minimum excursion in ATR beyond the level)
  - b = 0.5 [0.25, 1.0, 0.25] (stop buffer in ATR)
  - H = 24 [12, 48, 12] (max bars held)
- **Fixed constants:** ATR length 14. The reward filter of 1.0 ATR to the midpoint exists only to avoid trades whose target is smaller than costs. It is a structural choice, but I count it as a degree of freedom.
- **Expected trades per year:** About 50–90, and this is uncertain. The m and d filters could push it under 50, in which case relax d first.
- **Why it should not lose money in low-volatility chop:** Chop consists of repeated probes of prior-day extremes that fail, and reversion toward yesterday's midpoint is what happens. Quiet days also have narrow prior-day ranges, so the 1.0 ATR reward filter removes trades there instead of letting costs eat them.
- **Main way it could fail:** Real breakouts that look like failures, such as a break that retests and then runs (June 2022 style). Stops are close to the entry, which means a high stop-out rate, and the stop is often under 1% away, so the 1x leverage cap binds.

### 2. session-momentum-12h
- **Family:** Time-of-day / intraday seasonality
- **Rationale:** The Asia and Europe morning (00:00–12:00 UTC) is when much of the day's directional information arrives. US-hours participation (macro releases, ETF/futures flows, and the equity open) may extend a clear morning move in the same direction. Intraday momentum, where the early-session return predicts the later-session return, is documented in other liquid assets. This is a single bounded-duration bet per day, with no overnight exposure.
- **Entry long:** Only evaluated when bar t is the 11:00 UTC bar. Let O0 be the open of that UTC day's 00:00 bar. Let R = C_t − O0 and S = ATR_t(14)·√12. Go long if R ≥ k·S, with the fill at the 12:00 open.
- **Entry short:** If R ≤ −k·S.
- **Exit:** Time exit. The exit order goes out on the close of the H-th bar held, counting the 12:00 bar as bar 1. With H = 8 that is the 19:00 bar, filling at the 20:00 open. Also exit at the stop.
- **Initial stop:** Fill price ∓ s·ATR_t (ATR from the 11:00 bar).
- **Stop management:** Fixed.
- **Tunable parameters:**
  - k = 1.0 [0.5, 1.5, 0.25]
  - H = 8 [4, 12, 2]
  - s = 3.0 [2.0, 4.0, 0.5]
- **Fixed constants:** ATR length 14, the √12 scaling (the number of hours in the measurement window), and decision hour 11:00 UTC (US-session overlap start).
- **Expected trades per year:** About 90–130.
- **Why it should not lose money in low-volatility chop:** Honestly, there is no explicit protection. The threshold scales with current ATR, so quiet periods still generate signals. The only mitigations are the short fixed hold (8h, limited exposure) and the filter that skips days without a ≥1σ morning move (roughly 65% of days). If chop produces morning moves that revert in the afternoon, this strategy loses.
- **Main way it could fail:** The intraday momentum effect may simply not exist in BTC perps, or may be an artifact of particular years. Costs: about 0.06–0.1% of equity per trade, ~100 trades a year, so the average edge per trade must exceed that.

### 3. volume-climax-wick-reversal
- **Family:** Volume-based (exhaustion/capitulation)
- **Rationale:** A heavy-volume bar with a long rejection wick means aggressive flow was absorbed (liquidation cascades, stop runs) and then price was pushed back. After such flushes, forced sellers or buyers are largely done, and the next several hours tend to show partial retracement. The wick extreme is a natural, tight stop location.
- **Entry long:** Bar t qualifies if all of these hold:
  - V_t ≥ m·SMA(V, 48) over bars t−48…t−1 (excluding t)
  - H_t − L_t ≥ r·ATR_{t−1}
  - (min(O_t, C_t) − L_t) ≥ 0.5·(H_t − L_t)
  - The bar's L_t is the lowest Low of the last 24 bars including t
  - Then go long at the next open.
- **Entry short:** Mirror: V_t ≥ m·SMA(V,48); range ≥ r·ATR_{t−1}; (H_t − max(O_t, C_t)) ≥ 0.5·(H_t − L_t); H_t is the highest High of the last 24 bars including t. Go short at the next open.
- **Exit:** After H bars (exit order on the close of the H-th bar held), or at the stop.
- **Initial stop:** Long: L_t − 0.25·ATR_t. Short: H_t + 0.25·ATR_t.
- **Stop management:** Fixed.
- **Tunable parameters:**
  - m = 2.0 [1.5, 3.5, 0.5]
  - r = 1.2 [0.8, 2.0, 0.2]
  - H = 12 [6, 24, 6]
- **Fixed constants:** ATR(14), volume SMA 48, extreme lookback 24, wick fraction 0.5, stop buffer 0.25 ATR. The last three are structural choices, but they are real degrees of freedom and should be counted as such.
- **Expected trades per year:** About 40–90. This is the main risk to the 50/yr rule. If defaults come in below 50, the idea should be rejected rather than loosened post hoc.
- **Why it should not lose money in low-volatility chop:** Volume is measured relative to the last 48 bars and range relative to ATR, so quiet periods only produce signals on real sweeps. Wick rejections that retrace are exactly the chop behaviour. Few signals fire in dead markets, so the damage is bounded.
- **Main way it could fail:** Capitulation in a real crash (May 2022, Nov 2022) continues for hours or days, and the stop sits at the wick extreme. Losses are capped at 1% of equity but can arrive in clusters. Also, relative volume spikes may be common enough in the 2021 bull phase to generate noise.

### 4. marubozu-impulse-continuation
- **Family:** Volatility-state (volatility expansion, momentum ignition)
- **Rationale:** Volatility clusters. A sudden bar with a range far above recent ATR whose body is most of the range (no rejection wick) signals one-sided aggression, often stop-driven and followed by further drift as others chase and reposition. This is the mirror image of idea 3: it takes the impulse bars with no rejection, and the body and wick conditions make the two mutually exclusive.
- **Entry long:** Bar t qualifies if TR_t ≥ m·ATR_{t−1}, C_t > O_t, and (C_t − O_t) ≥ q·(H_t − L_t). Go long at the next open.
- **Entry short:** TR_t ≥ m·ATR_{t−1}, C_t < O_t, and (O_t − C_t) ≥ q·(H_t − L_t). Go short at the next open.
- **Exit:** After H bars (exit order on the close of the H-th bar held), or at the stop.
- **Initial stop:** Fill price ∓ s·ATR_t.
- **Stop management:** Fixed.
- **Tunable parameters:**
  - m = 2.5 [1.5, 3.5, 0.5]
  - q = 0.6 [0.5, 0.8, 0.1]
  - H = 12 [6, 24, 6]
  - s = 2.0 [1.5, 3.0, 0.5]
- **Fixed constants:** ATR length 14.
- **Expected trades per year:** About 70–150.
- **Why it should not lose money in low-volatility chop:** It fires only on abnormal range expansion, so most hours in dead markets produce no trade. This is an exposure limit, not a guarantee. In chop, an isolated impulse bar is more likely a one-off blip that reverts, and there I expect roughly break-even minus costs, not profit.
- **Main way it could fail:** Impulse bars in BTC often mean-revert after liquidation cascades (the premise of idea 3), so the sign of the edge is genuinely uncertain. Costs are high relative to a 12-hour drift: the stop is about 1–1.5% away, and the 1x cap often binds.

### 5. kama-slope-4h
- **Family:** Adaptive trend / regime-switching (efficiency-ratio gated)
- **Rationale:** Kaufman's adaptive moving average slows down automatically when price is noisy (low efficiency ratio) and speeds up when price moves directionally. Requiring the KAMA slope to exceed a threshold in ATR units gives a built-in trend/chop switch: in chop the slope is tiny and the strategy stays flat, which is what the earlier trend ideas lacked. Working on 4h bars keeps costs per trade lower than 1h ideas.
- **Entry long:** Build 4h bars from 1h bars (UTC-aligned at 00, 04, 08, 12, 16, 20; a bar is complete only when its 1h bar with hour 03, 07, 11, 15, 19, 23 has closed). Decisions are made only at those 4h closes, with the fill at the next 1h open. Using 4h closes C and ATR_4h(14) (Wilder, on 4h TR):
  - ER_t = |C_t − C_{t−n}| / Σ_{i=0}^{n−1} |C_{t−i} − C_{t−i−1}|
  - SC_t = (ER_t·(2/3 − 2/31) + 2/31)²
  - KAMA_t = KAMA_{t−1} + SC_t·(C_t − KAMA_{t−1}), seeded with KAMA = C at the first bar with ER defined
  - Δ_t = KAMA_t − KAMA_{t−1}
  - Go long if Δ_t ≥ θ·ATR_4h and the position is not already long.
- **Entry short:** Δ_t ≤ −θ·ATR_4h and not already short. If long when this fires, reverse to short.
- **Exit:** Flat when Δ_t ≤ 0 (long) or Δ_t ≥ 0 (short), at the next 1h open. Or at the stop.
- **Initial stop:** Fill price ∓ s·ATR_4h (4h ATR from the signal bar).
- **Stop management:** Trailing. At each 4h close, long stop = max(old stop, C_t − s·ATR_4h,t). Short is the mirror (min). The stop never loosens.
- **Tunable parameters:**
  - n = 10 [6, 30, 2] (ER window, Kaufman default)
  - θ = 0.04 [0.02, 0.10, 0.02] (slope threshold in ATR_4h per 4h bar)
  - s = 3.0 [2.0, 5.0, 0.5]
- **Fixed constants:** KAMA fast = 2 and slow = 30 (Kaufman's conventional values), ATR_4h length 14.
- **Expected trades per year:** About 35–70. This is the weakest point against the 50/yr rule, and a smaller n or θ trades more.
- **Why it should not lose money in low-volatility chop:** With ER near 0.1, SC is about 0.01, so Δ is a small fraction of 0.01·ATR_4h per bar, well below any plausible threshold. The strategy mostly sits flat in chop. Caveat: a fold with very few trades can still lose on one or two whipsaws.
- **Main way it could fail:** It is still a trend follower, the family that failed before, and a threshold that is too low brings back whipsaw losses while one that is too high gives too few trades. The default θ rests on my back-of-envelope calculation of KAMA slopes, not on data, so the walk-forward result is the real test. It is also the idea most likely to fall below 50 trades per year.

---

**Ranking by prior belief** (my own subjective view, not data):
1. failed-breakout-trap
2. volume-climax-wick-reversal
3. kama-slope-4h
4. marubozu-impulse-continuation
5. session-momentum-12h

The first two have a mechanism that directly targets the false-breakout and flush behaviour that hurt the earlier strategies. The last two are the most likely to show no edge after costs. Ideas 3 and 4 are designed as logical opposites on large-range bars, so at least one of them is likely to fail. Seeing which one survives is informative in itself.
