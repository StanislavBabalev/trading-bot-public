<!-- model requested: cfp/deepseek-ai/deepseek-v4-pro-0813
     model reported: deepseek-ai/deepseek-v4-pro-0813
     received_utc: 2026-10-26T16:13:52.880Z
     latency_s: 120.8
     prompt: prompt_short.md, max_tokens 12000
     finish_reason: error
     usage: {} -->
### 1. volatility-adaptive-channel
- **Family**: Volatility-state / Adaptive trend
- **Rationale**: Standard fixed breakouts (e.g., 72 bars) produce false signals in low-volatility chop (Q3 2023). Scaling the breakout width by ATR keeps the channel tight during quiet periods and wide during trends, reducing whipsaws.
- **Entry long**: `Close > (EMA48 + K * ATR14)`
- **Entry short**: `Close < (EMA48 - K * ATR14)`
- **Exit**: Reverse position on cross of EMA48 (reversal allowed).
- **Initial stop**: `(K - 0.5) * ATR14` below/below EMA48 entry price.
- **Stop management**: Move stop to (EMA48 - 0.5 * ATR14) for Longs or (EMA48 + 0.5 * ATR14) for Shorts on next bar close (breakeven trail).
- **Tunable parameters**: `K = 2.0` [1.5, 3.0, 0.1] (Channel multiplier)
- **Fixed constants**: `EMA48`, `ATR14`
- **Expected trades per year**: ~100-150 (Adaptive to volatility).
- **Why it should not lose money in chop**: When ATR shrinks, the channel tightens, requiring a stronger move to enter, filtering out false breaks.
- **Main way it could fail**: In a massive trend, the channel may trail behind price, missing the move, or tight stops cause frequent small losses on minor corrections.

### 2. funding-gated-trend
- **Family**: Funding / Regime-switching
- **Rationale**: High 8h funding rates indicate strong demand (long bias) or aggressive shorts paying for coverage. Using funding as a filter ensures we trade only when the "cost of carry" confirms the directional edge.
- **Entry long**: `Close > EMA50` AND `8h_Funding > F_Rate`
- **Entry short**: `Close < EMA50` AND `8h_Funding < -F_Rate`
- **Exit**: Close position on cross of EMA50.
- **Initial stop**: `-1.5 * ATR14` from entry.
- **Stop management**: Trailing EMA50.
- **Tunable parameters**: `F_Rate = 0.03` [0.01, 0.10, 0.01] (Minimum funding %)
- **Fixed constants**: `EMA50`, `ATR14`
- **Expected trades per year**: ~50-80 (Gated by funding).
- **Why it should not lose money in chop**: If funding rates are flat/low, no trades are taken, avoiding the costs of losing trades in choppy markets.
- **Main way it could fail**: Funding rates can be misleading or slow; a strong trend might exist without high funding (e.g., demand is high but funding is capped), or funding spikes can reverse ahead of price.

### 3. us-open-mean-reversion
- **Family**: Time-of-day
- **Rationale**: BTC often gaps at the US Open (13:00 UTC) or London Open (08:00 UTC). These gaps are frequently closed by institutional liquidity provision within the first hour.
- **Entry long**: `(Hour ==
