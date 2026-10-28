Backtest research on a personal project (no live trading). Propose testable trading-strategy hypotheses that fit these rules exactly.

MARKET/DATA: Bybit BTCUSDT perpetual, 1h bars, long/short/flat. Dev window 2021-01-01 to 2024-01-01 (2021 bull, 2022 bear, 2023 recovery). Signals may use only 1h OHLCV up to the just-closed bar plus UTC timestamps (you may build 4h/daily bars from them). No order book, open interest, liquidations, other symbols, on-chain or news. 8h funding history exists but is not yet a signal input: at most ONE idea may use it.

EXECUTION: signal on bar close, market fill at the NEXT bar's open. One position at a time; no pyramiding or partial exits; reversals allowed. The only resting order is a stop (fills at the stop, or the open on a gap); it may be moved on any bar close (trailing/breakeven OK). Targets and other exits are evaluated on the close and fill at the next open.

COSTS: 0.04% commission + 0.02% slippage per side (~0.12% round trip) plus 8h funding (usually longs pay).

SIZING: each entry risks 1% of equity: notional = equity x 1% / stop distance, capped at 1x equity. So every entry needs a stop; a stop closer than ~1% risks less than 1%. With a 2% stop one round trip costs ~6% of the risk unit, so small average wins die to costs.

PARAMETERS: max 4 tunable, each with an a-priori default and range. Only conventional fixed constants (e.g. ATR 14). The idea is tested at its DEFAULTS first and rejected if they fail any kill rule; survivors get <=200 optimizer evaluations and must sit on a plateau.

KILL RULES (3-year dev window), reject if any: PF < 1.1; Sharpe < 0.5; max DD > 30%; < 50 trades/yr; > 1 failed walk-forward fold. Walk-forward: 12m train / 3m test, 6m step, fixed parameters; test quarters Q1 2022, Q3 2022, Q1 2023, Q3 2023; a fold fails if it loses money or PF < 1.0. Ranked by Deflated Sharpe, so fewer parameters is better.

ALREADY REJECTED (no thin variants):
1. donchian-breakout: close beyond 72-bar high/low, exit on 24-bar opposite channel, ATR stop.
2. bollinger-reversion: fade close outside 40-bar 2-sigma bands, exit at mid band or 48 bars, ATR stop.
3. keltner-breakout: close beyond EMA48 +/- 2 ATR, exit on cross back over EMA, ATR stop.
4. ts-momentum: long if 96-bar return > +1%, short if < -1%, else hold; ATR stop.
5. trend-pullback-rsi: buy RSI(4) < 20 above EMA200 (mirror short), exit RSI 55/45 or 48 bars, ATR stop.
6. ema-cross-atr: EMA 80/260 cross, long-only, 5x ATR stop.
All of 1-5 failed Q3 2022 and Q3 2023: low-volatility range chop after big moves (Q3 2023 was one of BTC's quietest periods ever). Trend/breakout ideas bled on false breakouts; the mean-reversion ones won most trades but lost after costs and occasional big losses; the EMA cross traded too rarely.

ASK: 5 DISTINCT hypotheses from different families (e.g. regime-switching, volatility-state, time-of-day, volume, range structure, adaptive trend, gated mean reversion, funding). Template for each:
### n. kebab-case-name
- Family:
- Rationale (why the edge exists in BTC perps):
- Entry long:
- Entry short (or none):
- Exit:
- Initial stop:
- Stop management:
- Tunable parameters (max 4): name = default [min, max, step]
- Fixed constants:
- Expected trades per year:
- Why it should not lose money in chop like Q3 2022 / Q3 2023:
- Main way it could fail:
Rules must be exact (formulas, operators, lookbacks) so a programmer needs no guessing. Be honest about weaknesses.

Your host times out after about two minutes: keep private reasoning to a few sentences and keep each field to one or two lines.
