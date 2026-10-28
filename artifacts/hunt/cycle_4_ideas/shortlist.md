# Hunt cycle 4: hypothesis shortlist

Brainstormed 2026-10-26 through the local OmniRoute gateway. **Nothing here has been implemented, registered or backtested.** Brainstorming is not a trial: `trials.jsonl`, `hunt_log.json` and the OOS window were not touched.

## How this was produced

| Family | Model (OmniRoute id) | Prompt | Result | Raw file |
|---|---|---|---|---|
| Anthropic | `claude-sonnet-5-5` | [prompt.md](prompt.md) | OK on the 2nd try (the 1st, at 12k max tokens, was cut off by reasoning; rerun at 32k) | [response_claude-sonnet-5-5.md](response_claude-sonnet-5-5.md) |
| Google | `antigravity/gemini-3.8-flash-high` | prompt.md | OK | [response_antigravity_gemini-3.8-flash-high.md](response_antigravity_gemini-3.8-flash-high.md) |
| OpenAI | `ddgw/gpt-5.4-mini` | prompt.md | OK | [response_ddgw_gpt-5.4-mini.md](response_ddgw_gpt-5.4-mini.md) |
| DeepSeek | `auto` (routed to `deepseek-v4-pro`) | prompt.md | OK | [response_auto_deepseek-v4-pro.md](response_auto_deepseek-v4-pro.md) |
| DeepSeek | `cfp/deepseek-ai/deepseek-v4-pro-0813` | prompt_short.md | Cut off at Cloudflare's 120 s limit after 2½ ideas | [response_cfp_deepseek-ai_deepseek-v4-pro-0813_PARTIAL.md](response_cfp_deepseek-ai_deepseek-v4-pro-0813_PARTIAL.md) |
| Zhipu | `cfp/zai-org/glm-5.2` | [prompt_short.md](prompt_short.md) | OK on the 2nd try | [response_cfp_zai-org_glm-5.2.md](response_cfp_zai-org_glm-5.2.md) |
| Moonshot | `cfp/moonshotai/kimi-k2.7-code` | prompt_short.md | OK on the 2nd try | [response_cfp_moonshotai_kimi-k2.7-code.md](response_cfp_moonshotai_kimi-k2.7-code.md) |

- The Cloudflare Playground models (`cfp/...`) reject prompts over **6,000 characters** and time out at about **120 s**. GLM and Kimi spent the whole 120 s on hidden reasoning with the full prompt; the switches I tried (`enable_thinking: false`, `reasoning_effort: low`, `thinking: disabled`) did not turn it off. So they got [prompt_short.md](prompt_short.md): the same rules condensed to 3.6k characters, plus a line asking them to keep their reasoning short.
- Only the rules summary was sent: no repo code, no data files and no backtest numbers.
- Response quality varied a lot. Claude, Gemini and DeepSeek V4 Pro gave exact, implementable rules. GPT-5.4 mini was decent but stuffed in hidden constants. GLM and Kimi (condensed prompt, rushed reasoning) mostly returned thin variants of rejected ideas or ambiguous rules.

## Two engine facts that shaped the shortlist

1. **A walk-forward fold only passes if it makes money** (`NetProfit > 0 && PF >= 1.0`, `src/Backtester.Cli/Hunt.cs:424`). A quarter with zero trades fails. So "switch off in chop" is not enough on its own: a strategy that sits flat through both Q3 folds fails 2 of 4 and dies. Several models argued "it just won't trade in Q3 2023". I counted that as a weakness, not a defence.
2. **Strategies see only OHLCV bars and UTC timestamps.** Funding is charged by the engine but is not in `StrategyContext`, so any funding-signal idea needs a contract change first. Every funding idea also failed the 50-trades/yr floor on its own estimate, so none made the list.

Other contract notes that apply to every candidate:
- The stop must be known **at signal time** (`Signal.Long(stop)`), because the fill price isn't known yet. Where a model wrote "fill price ∓ x", I use the signal-bar close ∓ x, as every existing strategy does. The engine skips the entry if the next open gaps past the stop.
- Structural stops close to the price (C1, C2, C5's fade side) often sit less than 1% away. The 1x leverage cap then binds, so the trade risks less than 1% and the 0.12% round-trip cost is a larger share of the risk.
- The optimizer searches the full grid only when it has ≤ 192 points (200 minus the plateau neighbours); bigger grids are randomly sampled. Every range below is sized to ≤ 192 points.

## Shortlist (5 = `hypothesesPerCycle`)

Five different families. I rank them by my prior belief that they survive the Q3 folds (not data): **C1 > C5 > C2 > C4 > C3**.

---

### C1. `failed-breakout-fade` (range structure / stop-run reversal)
- **Proposed by:** Claude (`failed-breakout-trap`), Gemini (`liquidity-sweep-fade`).
- **Logic:** The trend and breakout ideas in cycle 3 bled from false breakouts in exactly the folds they failed. This takes the other side. When price closes beyond yesterday's range and then closes back inside within a few bars, fade it back toward yesterday's midpoint, with the stop just beyond the failed excursion.
- **Levels:** PDH and PDL are the high and low of the previous complete UTC day (24 bars, 00:00–23:00), taken for the UTC day that contains bar t. M = (PDH + PDL) / 2.
- **Entry long (flat only):** C_t ≥ PDL, AND the j bars just before t (1 ≤ j ≤ `m`) all closed below PDL, AND the lowest low of those j bars ≤ PDL − `d`·ATR14, AND M − C_t ≥ 1.0·ATR14 (reward filter, so the target is worth more than costs).
- **Entry short:** the mirror: closes above PDH, then back to C_t ≤ PDH; excursion high ≥ PDH + `d`·ATR14; C_t − M ≥ 1.0·ATR14.
- **Exit:** close crosses M (long: C ≥ M; short: C ≤ M), or `H` bars held. Fills at the next open.
- **Stop:** long = lowest low of the excursion bars and the signal bar − `b`·ATR14 (short: the mirror). Fixed.
- **Parameters (4, grid 4×4×4×3 = 192):**
  - `m` = 6 [3, 12, 3]: max bars the break may last
  - `d` = 0.25 [0, 0.75, 0.25]: min excursion beyond the level, in ATR. Claude suggested 0.3; I moved it onto the grid.
  - `b` = 0.5 [0.25, 1.0, 0.25]: stop buffer in ATR
  - `H` = 24 [12, 36, 12]: max bars held
- **Fixed constants:** ATR14, prior UTC day, the 1.0·ATR reward filter. The reward filter is a structural choice, but it is a real degree of freedom.
- **Trades/yr:** Claude estimated 50–90. My guess is 60–120: BTC takes out one side of the prior day on most days, and many of those breaks fail. The floor risk is low-to-medium. If the floor is a problem, Gemini's variant (rolling 36-bar swing high/low, rejection wick ≥ 50% of the bar, stop at the wick − 0.2 ATR) fires more often but uses tighter stops.
- **Why it might survive Q3 2022 / Q3 2023:** Range chop is repeated failed probes of the prior day's extremes, and the snap back toward the middle is what this trades. It is the counterparty to the Donchian and Keltner losses in those folds (PF 0.37–0.64). In quiet quarters the prior-day ranges are narrow, so the reward filter drops setups whose target is too close.
- **Main risk:** a breakout that retests and then runs (June 2022, Aug 17 2023), which means clustered stop-outs. Stops are tight, so the stop-out rate is high and the leverage cap often binds.
- **Build cost:** low. It needs a UTC-day high/low tracker from `Bar.OpenTimeMs`. `Atr` and `EntryExitStrategy.BarsHeld` already exist.

---

### C5. `vol-regime-switch` (regime switching: fade when quiet, break out when expanding)
- **Proposed by:** DeepSeek V4 Pro (`vol-gated-range-fade` + `atr-regime-trend-switch`, merged into one strategy). The same idea of gating by volatility or choppiness came from Gemini (`chop-gated-regime-continuation`) and GPT (`adaptive-trend-pullback-with-volatility-gate`).
- **Logic:** Cycle 3's fold results show breakouts made their money in the trending quarter (Q1 2023: Donchian +37%, Keltner +28%) and lost in the quiet ones. A range fade does the opposite. Switching on the volatility state trades in **every** regime, which avoids the zero-trade fold failure that sinks pure "stay flat in chop" filters. (This uses dev-window fold results to design a dev hypothesis. That's allowed, and the OOS window stays locked.)
- **Regime:** quiet if ATR14_t < median(ATR14 over the last `L` bars, including t); otherwise expanding.
- **Channel:** HH and LL = highest high and lowest low of the previous `N` bars (excluding t); mid = (HH + LL) / 2; pos = (C_t − LL) / (HH − LL).
- **Quiet regime, entry (flat only):** long if 0 ≤ pos ≤ `e` and C_t > O_t; short if 1 − `e` ≤ pos ≤ 1 and C_t < O_t.
  - Exit: close crosses mid, or 24 bars held.
  - Stop: long = min(LL, L_t) − 0.5·ATR14 (short: the mirror). Fixed.
- **Expanding regime, entry (flat only):** long if C_t > HH; short if C_t < LL.
  - Exit: long when the close is below the lowest low of the previous N/2 bars (short: the mirror).
  - Stop: C_t ∓ `s`·ATR14, trailed on every bar close (long: max(stop, C − `s`·ATR14)).
- **An open trade keeps its own exit rules** even if the regime flips.
- **Parameters (4, grid 4×4×4×3 = 192):**
  - `L` = 720 [360, 1440, 360]: regime median window (30 days)
  - `N` = 48 [24, 96, 24]: channel length
  - `e` = 0.10 [0.05, 0.20, 0.05]: edge zone for fades
  - `s` = 2.0 [1.5, 2.5, 0.5]: trend-side stop and trail in ATR
- **Fixed constants:** ATR14, fade stop buffer 0.5 ATR, fade max hold 24 bars, trend exit channel N/2.
- **Trades/yr:** about 75–125 (DeepSeek estimated 35–55 for the fade half and 40–70 for the trend half).
- **Why it might survive Q3 2022 / Q3 2023:** Those quarters sit mostly in the "quiet" state, where it fades range edges with a stop just outside the range, which is where cycle 3's breakouts bled. In the expanding state it runs the breakout logic, which earned the trending folds.
- **Main risk:**
  - The regime label lags, so the first leg of a breakout out of a quiet range (Aug 17 2023) hits the fade side.
  - **The trend half alone is a gated Donchian.** DeepSeek itself flagged that as a possible thin variant of `donchian-breakout`. I think the regime switch makes it a different hypothesis, but that's your call.
- **Build cost:** medium. It needs a rolling-median indicator; `RollingExtreme` covers the channels.

---

### C2. `volume-climax-reversal` (volume-based exhaustion)
- **Proposed by:** Claude (`volume-climax-wick-reversal`), Gemini (`volume-climax-exhaustion`), GPT (`volume-climax-fade-with-regime-filter`), DeepSeek (`volume-climax-reversal`). Four of six families proposed it.
- **Logic:** A bar with abnormal volume, an abnormal range and a long rejection wick at a local extreme marks absorbed forced flow (liquidations, stop runs). Fade it for a few hours, with the stop just past the wick. It is the first idea in the hunt to use volume.
- **Entry long (flat only):**
  - V_t ≥ `m`·SMA(V) over bars t−48…t−1
  - H_t − L_t ≥ `r`·ATR14(t−1)
  - lower wick (min(O_t, C_t) − L_t) ≥ 0.5·(H_t − L_t)
  - L_t is the lowest low of the last 24 bars, including t
- **Entry short:** the mirror, with an upper wick and a 24-bar highest high.
- **Exit:** `H` bars held. Gemini's variant exits earlier on a close back through EMA(24); kept as a fallback only.
- **Stop:** long L_t − 0.25·ATR14 (short H_t + 0.25·ATR14). Fixed.
- **Parameters (3, grid 5×7×4 = 140):**
  - `m` = 2.0 [1.5, 3.5, 0.5]
  - `r` = 1.5 [1.0, 2.5, 0.25]. The models' defaults ranged 1.2–2.0; I took the middle.
  - `H` = 12 [6, 24, 6]
- **Fixed constants:** volume SMA 48, extreme lookback 24, wick fraction 0.5, stop buffer 0.25 ATR, ATR14. I dropped GPT's ADX < 18 and Bollinger conditions: stacking five filters makes the 50/yr floor unlikely.
- **Trades/yr:** the models said 40–90 (Claude), 60–80 (Gemini), 80–160 (GPT), 30–60 (DeepSeek, at m = 3). **The 50/yr floor is the main risk.** Per Claude's advice: if the defaults come in under 50, reject it rather than loosen it after the fact.
- **Why it might survive Q3 2022 / Q3 2023:** Volume and range are measured relative to recent history, so it still fires in quiet quarters, but only on real sweeps, and wick rejections in a range tend to retrace. It differs from the rejected Bollinger and RSI fades because it needs evidence of forced flow, not just distance from a mean.
- **Main risk:** capitulation that keeps going (May, June and Nov 2022), which produces clustered 1% losses. Average wins are small relative to costs.
- **Disagreement to note:** DeepSeek's version is the opposite on close location (it fades bars that close near their extreme, with no wick). Three of four models wanted the wick rejection, so that's the spec here.
- **Build cost:** low. Volume SMA via `RollingStats`, extremes via `RollingExtreme`.

---

### C4. `impulse-bar-continuation` (volatility state / momentum ignition)
- **Proposed by:** Claude (`marubozu-impulse-continuation`). Similar single-bar ATR-expansion entries came from Kimi (`volatility-expansion-trend`) and GLM (`vol-filtered-range-breakout`).
- **Logic:** A bar whose range is far above recent ATR and whose body fills most of that range, with no rejection, signals one-sided aggression that tends to drift on as others chase. It is the **logical opposite of C2** (body vs. wick, so they never fire on the same bar). Testing both tells you the sign of follow-through after large bars.
- **Entry long (flat only):** TR_t ≥ `m`·ATR14(t−1) AND C_t > O_t AND (C_t − O_t) ≥ `q`·(H_t − L_t).
- **Entry short:** the mirror, with a down body.
- **Exit:** `H` bars held.
- **Stop:** C_t ∓ `s`·ATR14(t). Fixed.
- **Parameters (4, grid 4×4×3×4 = 192):**
  - `m` = 2.5 [2.0, 3.5, 0.5]
  - `q` = 0.6 [0.5, 0.8, 0.1]
  - `H` = 12 [6, 18, 6]
  - `s` = 2.0 [1.5, 3.0, 0.5]
- **Fixed constants:** ATR14.
- **Trades/yr:** 70–150 (Claude).
- **Why it might survive Q3 2022 / Q3 2023:** It trades only on abnormal expansion, so most dead hours produce nothing. The few big moves in Q3 2023 (Jul 13, Aug 17) were impulse bars with follow-through. Claude's honest view is that isolated blips in chop come out roughly break-even minus costs, so this is an exposure limit, not an edge in chop.
- **Main risk:** big BTC bars often mean-revert (C2's premise), so the sign of the edge is genuinely uncertain. **Closest rejected relative: `keltner-breakout`** (same vol-breakout family). It differs in the single-bar trigger, the body filter and the fixed 12-bar hold, as opposed to a band breakout held until an EMA cross.
- **Build cost:** low.

---

### C3. `session-momentum` (time-of-day / intraday momentum)
- **Proposed by:** Claude (`session-momentum-12h`). Other time-of-day ideas came from every family: Gemini (`session-range-expansion`), DeepSeek (`overnight-session-drift`), GPT (`session-open-range-impulse`), GLM and Kimi (session breakouts). This is the cleanest one, with 3 parameters.
- **Logic:** The Asia and Europe morning (00:00–12:00 UTC) carries much of the day's directional information. US-hours flow (macro releases, ETF flows, the equity open) tends to extend a clear morning move. Intraday momentum like this is documented in other liquid markets. One bounded trade per day at most, with no overnight hold.
- **Entry (flat only, evaluated only on the 11:00 UTC bar's close):**
  - R = C_t − the open of that UTC day's 00:00 bar; S = ATR14·√12.
  - Long if R ≥ `k`·S; short if R ≤ −`k`·S. Fills at the 12:00 open.
- **Exit:** on the close of the `H`-th bar held, with the 12:00 bar counted as bar 1 (default H = 8 exits at the 20:00 open).
- **Stop:** C_t ∓ `s`·ATR14. Fixed.
- **Parameters (3, grid 5×5×5 = 125):**
  - `k` = 1.0 [0.5, 1.5, 0.25]
  - `H` = 8 [4, 12, 2]
  - `s` = 3.0 [2.0, 4.0, 0.5]
- **Fixed constants:** decision hour 11:00 UTC, ATR14, the √12 scaling.
- **Trades/yr:** 90–130 (about 35% of days clear the k = 1 threshold).
- **Why it might survive Q3 2022 / Q3 2023:** It has no structural chop protection, as Claude itself said. It survives only if the intraday-momentum effect holds across regimes. Its case is diversification: it shares no logic with C1, C2, C4 or C5, it is the cheapest to test (3 parameters, one decision a day) and it adds few trials. It has the lowest prior of the five.
- **Main risk:** the effect may not exist in BTC perps or may be specific to certain years. Costs are about 0.04–0.08% of equity per trade at ~100 trades/yr, so the edge per trade must clear that.
- **Build cost:** low. It needs the UTC hour and the day-open price from `Bar.OpenTimeMs`.

---

## Bench (viable, not picked)

- **`kama-slope-4h`** (Claude): a 4h KAMA slope above θ·ATR4h, trailing stop, 3 parameters. Well specified, but by design it sits mostly flat in chop, so it is exposed to zero-trade fold failures. Its own estimate is 35–70 trades/yr. It's the best pure trend idea if you want one.
- **`asia-range-expansion`** (Gemini): a breakout of the 00:00–08:00 UTC range during 12:00–16:00 UTC with volume above 1.3× its 24-bar average and a 22:00 time exit, 4 parameters. It's the alternative time-of-day idea, but it is still a breakout, and Gemini expects it to sit flat for weeks in chop.
- **`overnight-session-drift`** (DeepSeek): 00:00–08:00 UTC trades in the direction of the 24h return, gated by a ~200-day EMA, 4 parameters. It's an alternative to C3 with ~200+ trades/yr.

## Rejected (one line each)

| Idea | Model | Reason |
|---|---|---|
| adaptive-trend-pullback-with-volatility-gate | GPT | A thin variant of `trend-pullback-rsi` (trend filter plus pullback) with 6+ hidden constants; its vol gate leaves the chop folds with no trades |
| volatility-contraction-breakout-with-confirmation | GPT | Donchian breakout plus squeeze and volume filters, so a thin variant of `donchian-breakout`; the contraction and breakout conditions half-contradict each other |
| session-open-range-impulse | GPT | Time-of-day family is covered by C3; this one needs a daily VWAP, same-hour cumulative volume and 5 fixed constants on top of 4 parameters |
| volume-climax-fade-with-regime-filter | GPT | Merged into C2; its extra ADX and Bollinger filters were dropped (trade floor) |
| daily-trend-weekend-mean-reversion-gate | GPT | Two strategies in one with ~10 effective degrees of freedom (separate weekday and weekend rules, holds and persistence filter) |
| session-range-expansion | Gemini | Bench (see above) |
| liquidity-sweep-fade | Gemini | Merged into C1 (kept as the higher-frequency variant) |
| funding-rate-exhaustion | Gemini | Needs funding in `StrategyContext`; ±0.04% funding is rare in 2022–23, so far below 50 trades/yr and flat through the chop folds |
| chop-gated-regime-continuation | Gemini | Flat for whole chop quarters by design (those folds fail); ~50–65 trades/yr; pullback-in-trend is close to `trend-pullback-rsi` |
| volume-climax-exhaustion | Gemini | Merged into C2 |
| failed-breakout-trap / volume-climax-wick-reversal / marubozu-impulse-continuation / session-momentum-12h | Claude | Became C1 / C2 / C4 / C3 |
| kama-slope-4h | Claude | Bench (see above) |
| vol-gated-range-fade + atr-regime-trend-switch | DeepSeek | Merged into C5 (on its own, the trend half is a gated Donchian and the fade half has 35–55 trades/yr) |
| volume-climax-reversal | DeepSeek | Merged into C2 (its no-wick close rule is noted under C2) |
| overnight-session-drift | DeepSeek | Bench (see above) |
| funding-extreme-contrarian | DeepSeek | Needs funding in `StrategyContext`; 25–50 trades/yr by its own estimate |
| volatility-adaptive-channel | DeepSeek (Cloudflare, partial) | It is `keltner-breakout` (EMA48 ± K·ATR) under a new name |
| funding-gated-trend | DeepSeek (Cloudflare, partial) | Funding input; a trend filter on EMA50 is close to the rejected trend family |
| us-open-mean-reversion | DeepSeek (Cloudflare, partial) | Cut off before its rules were stated |
| vol-filtered-range-breakout | GLM | A Keltner/Donchian variant with a bar-range filter; ambiguous rules (`High[15]`); 40–60 trades/yr |
| funding-aided-trend | GLM | Funding input; 20–30 trades/yr |
| session-momentum-breakout | GLM | Donchian(40) with an hour-of-day filter: a thin variant of `donchian-breakout` |
| vwap-reversion | GLM | Undefined `VWAP[n]`, a stop that can sit on the wrong side of entry, pullback-in-trend similar to `trend-pullback-rsi`, 35–55 trades/yr |
| adaptive-momentum-vol | GLM | `ts-momentum` with a range filter (thin variant); a 0.5 ATR stop means the leverage cap binds and costs dominate |
| volatility-expansion-trend | Kimi | Folded into C4 (Claude's spec is exact; Kimi's exit rule is ambiguous) |
| funding-scaled-reversion | Kimi | Funding input; 30–50 trades/yr; "scale size by funding" breaks the fixed 1%-risk sizing |
| session-range-breakout | Kimi | Its entry rule contradicts itself (close > session high AND close < session high) |
| keltner-volatility-filter | Kimi | `keltner-breakout` with an ATR filter: a thin variant |
| rsi-funding-regime | Kimi | A second funding idea (the limit was one); the RSI-14 30/70 fade is the rejected mean-reversion family again |

## If you go ahead

- Each candidate's default run on dev is one trial (cumulative 30 → 35 for all five). Survivors then get up to 200 evaluations each.
- No candidate uses funding, so no engine or contract change is needed.
- New indicator code needed:
  - UTC day high/low/open and hour helpers (C1, C3)
  - a rolling median (C5)
  - everything else reuses `Atr`, `Ema`, `RollingStats` and `RollingExtreme`
