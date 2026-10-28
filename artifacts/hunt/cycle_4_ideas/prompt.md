You are helping with quantitative research on a personal backtesting project. Propose new, testable trading-strategy hypotheses that fit the rules below exactly. This is a backtest research exercise; no live trading is involved.

## Market and data
- Instrument: Bybit BTCUSDT linear perpetual, 1-hour bars. Long, short or flat.
- Research (development) window: 2021-01-01 to 2024-01-01 (2021 bull market and top, 2022 bear market and crashes, 2023 recovery). A later period is held out and used once at the end.
- A signal may use only the 1h OHLCV bars (open, high, low, close, volume) up to and including the bar that just closed, plus each bar's UTC timestamp. It may build its own higher timeframes (4h, daily) from those 1h bars.
- Not available: order book, open interest, liquidations, long/short ratios, spot or other symbols, news, on-chain data.
- The 8-hourly funding rate history exists in the data but is not yet exposed to signals. At most ONE of your ideas may use funding as an input, and only if it clearly earns it.

## Execution model
- The strategy sees each bar once, after it closes, and outputs a target position (long, short or flat). The order fills at the NEXT bar's open. No same-bar fills, no limit orders.
- One position at a time. No pyramiding, no scaling in or out, no partial exits. A reversal (long to short) is allowed.
- The only resting order is a protective stop. It triggers intrabar and fills at the stop price (or at the open if the bar gaps through it). The strategy may move the stop on any bar close (e.g. a trailing or breakeven stop).
- Profit targets and other exits are evaluated on the bar close and fill at the next open.

## Costs
- 0.04% commission plus 0.02% slippage per side, so about 0.12% of notional per round trip.
- Funding is charged every 8h on the open position (BTC funding is usually positive, so longs usually pay shorts, more so in bull phases).

## Sizing and risk
- Every entry risks 1% of equity: position notional = equity x 1% / (distance from entry to stop). So EVERY entry needs a stop price, set at entry.
- Maximum leverage 1x: notional is capped at 100% of equity, so a stop closer than about 1% from entry risks less than 1%.
- No drawdown or daily-loss circuit breakers.
- Cost intuition: with a stop 2% away, notional is about 50% of equity and one round trip costs about 0.06% of equity, i.e. about 6% of the 1% risk unit. Strategies with small average wins get eaten by costs.

## Parameters
- At most 4 tunable parameters per strategy, each with an a-priori default and a search range.
- Fixed constants are fine only when they are conventional (e.g. ATR length 14). Do not hide tuning in constants.
- The strategy is first tested at its DEFAULTS. If the defaults fail any kill rule below, the idea is rejected without optimization. So the defaults must be values you genuinely believe in.
- Survivors are optimized with at most 200 evaluations, and the chosen point must sit on a plateau (neighbouring settings also profitable).

## Kill rules (on the 3-year development window)
A strategy is rejected if ANY of these is true:
- Profit factor < 1.1
- Sharpe ratio < 0.5 (daily returns, annualized)
- Maximum drawdown > 30%
- Fewer than 50 trades per year
- More than 1 failed walk-forward fold. Walk-forward: 12-month train / 3-month test, 6-month step, parameters fixed across folds. The four test quarters are Q1 2022, Q3 2022, Q1 2023 and Q3 2023. A fold fails if it loses money or its profit factor is below 1.0.

Candidates are ranked by Deflated Sharpe, which penalizes the number of trials, so simple ideas with few parameters are preferred.

## Already tried and rejected (do not propose these or thin variants of them)
1. donchian-breakout: enter on a close beyond the 72-bar high/low channel, exit on the opposite 24-bar channel, ATR stop.
2. bollinger-reversion: fade a close outside 40-bar 2-sigma Bollinger bands, exit at the middle band or after 48 bars, ATR stop.
3. keltner-breakout: enter on a close beyond EMA(48) +/- 2 ATR, exit when the close crosses back over the EMA, ATR stop.
4. ts-momentum: long when the 96-bar return > +1%, short when < -1%, otherwise hold; ATR stop.
5. trend-pullback-rsi: buy RSI(4) < 20 dips while above EMA(200) (short the mirror), exit on RSI back through 55/45 or after 48 bars, ATR stop.
6. ema-cross-atr: EMA 80/260 crossover, long-only, fixed 5x ATR stop.

What went wrong: every one of the first five failed the Q3 2022 and Q3 2023 folds. Both were low-volatility, range-bound chop after big moves (Q3 2022 drifted sideways after the June 2022 crash; Q3 2023 was one of BTC's quietest periods on record). Trend and breakout ideas bled from repeated false breakouts in those quarters. The two mean-reversion ideas won most of their trades but lost money overall once costs and the occasional large loss were counted. The EMA crossover traded far too rarely.

## What to send back
Propose 5 DISTINCT hypotheses, ideally from different families (e.g. regime-switching, volatility-state, time-of-day/seasonality, volume-based, range/structure, adaptive trend, mean reversion with a regime gate, funding-based). For each one, use exactly this template:

### <n>. <kebab-case-name>
- Family:
- Rationale: why this edge should exist in BTC perpetuals (2-4 sentences)
- Entry long:
- Entry short (or "none, long-only"):
- Exit:
- Initial stop:
- Stop management (trailing/breakeven, or "fixed"):
- Tunable parameters (max 4): name = default [min, max, step], one per line
- Fixed constants:
- Expected trades per year:
- Why it should not lose money in low-volatility chop like Q3 2022 and Q3 2023:
- Main way it could fail:

Rules must be exact and unambiguous (formulas, comparison operators, lookback lengths), so a programmer can implement them without guessing. Use only data listed as available above. Be honest about weaknesses.
