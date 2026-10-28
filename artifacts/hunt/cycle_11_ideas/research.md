# Hunt cycle 11: Veles-style DCA grid bots (and Finandy)

Requested by the user on 2026-10-27: "Try Veles (The Dedicated Integration), Finandy (The Webhook Specialist)".
Checked that day with web search, page fetches and the built-in browser, public pages only. **No account was created,
no login used, no API key made or connected, no order placed.** Written before cycle 11 ran.

## Finandy: shut down

Finandy's official site ([finandy.com](https://finandy.com/), read 2026-10-27) is a closure notice. Its timeline:
brokerage and copy trading stopped 2026-04-13; brokerage withdrawals ended 2026-07-01; on **2026-08-10** a wallet used
for remaining withdrawals was compromised and withdrawals were suspended; on **2026-09-10** Finandy permanently ceased
operations. It says API trading and funds on connected exchanges were not affected, and **recommends deleting Finandy
API keys in connected exchanges**. No independent news coverage was found; the notice is on the company's own domain.
Finandy therefore cannot be used, and any Finandy API key on a Bybit/Binance account should be revoked.

## Veles: what it is

From [veles.finance](https://veles.finance/en/), its [backtests page](https://veles.finance/en/backtests/) and the
[help center](https://help.veles.finance/en/) (settings, DCA strategy, TradingView filter articles):

- A bot platform for Binance, Bybit, OKX, Gate, HTX, BingX, Bitget, Hyperliquid. Funds stay on the exchange, connected
  by trade-only API keys. Fees: 20% of profitable trades, capped at $50/month per trading type; backtest plans $0–99/month.
- **The only live bot type is a DCA grid** (grid bots and an "AI strategy generator" are "soon"): a base order at market
  ("first indent 0 / Market" is recommended), then averaging limit orders spread over an **overlap** (% range), a
  **logarithmic distribution** factor (1 = even spacing), each order's volume grown by a **martingale %**, a
  **take-profit** a % from the average entry (or several partial TPs, or an indicator exit), an optional **stop loss**,
  and entry either immediately after the previous deal ("no filters") or when indicator filters / a TradingView webhook
  alert allow (plain-text messages such as `BINANCE:BTCUSDT:1h:buy/long`). Templates in its DCA article: aggressive
  (log 1.5–2.0, martingale 30–50%), balanced (log ~1.0, martingale 15–25%), conservative (log 0.5–0.8, martingale 0–10%).
- Its own risk notes: if the grid fills completely often, the bot sits in "invest mode" with a long floating drawdown;
  martingale raises margin risk; it suggests a futures deposit of ~1/10 of the balance. Reinvest is "recommended only
  without stop loss".
- The marketplace presets on the home page are all leveraged **long** DCA bots on alts and ETH (e.g. "STARTER APT/USDT long
  4x +117.24%", "ETH long 4x +50.55%", one-year figures). Preset settings are behind a login. Backtests also need an
  account plus a live exchange API connection, so they were not run.
- Evidence: none independent. The showcase figures ("+312.7%, PF 1.92, 1,284 trades") are marketing samples; one-year
  results of leveraged long bots in a rising market say nothing about a bear market.

## What is tested here, and how

`DcaGridBot` (src/Backtester.Core/Strategies/Cycle11Strategies.cs) simulates Veles's documented DCA mechanics on our
BTCUSDT 1h data through the engine's `ISelfSimulatingStrategy` hook (the bar engine has one entry per trade; existing
strategies are unaffected: all 305 registered trials replay identically and the benchmark digest is unchanged):

- deal start at the next open after a bar close (immediately after the previous deal, or when the filter allows);
  market base order; orders − 1 averaging limits evenly spaced over `overlap`; notional grows by (1 + martingale) per
  order; take-profit limit at average × (1 ± takeProfit), moving as orders fill; stop **5% beyond the last order**.
- **Sizing follows the project rule** (1% equity risk per trade, every entry needs a stop, ≤ 1x): each deal's total
  planned notional is set so that a fully filled grid stopped out loses 1% of equity. Veles users typically run without
  a stop and with leverage, which this does not reproduce; the stop is what makes the risk bounded and testable.
- Intrabar path: open → low → high → close on up bars, open → high → low → close on down bars; limit orders fill at
  their price or the open if gapped through; commission 0.04% + slippage 0.02% on **every** fill (conservative: Bybit
  maker fills on resting limits would be cheaper); funding on the position held at each settlement.

## Hypotheses (defaults fixed before any research-data run)

| Name | Entry | Defaults | Grid |
|---|---|---|---|
| dca-grid-long | none (always in a deal) | 5 orders, overlap 10%, martingale 15%, TP 1% | orders 3/5/7 × overlap 5–20% × martingale 0/15/30% × TP 0.5–2% (144) |
| dca-grid-short | none, short | same | same |
| dca-rsi-long | RSI(14, 1h) < rsiLevel | RSI 30, overlap 10%, TP 1% (5 orders, 15%) | RSI 20–40 × overlap × TP (80) |
| dca-trend-long | daily close > SMA(maDays) | 200 days, overlap 10%, TP 1% | maDays 50–200 × overlap × TP (64) |

Expectations: DCA earns small, frequent take-profits in ranges (the 2022Q3 chop that killed the trend ideas) and loses
when a trend runs through the whole grid (stop-outs at −1%). dca-trend-long will sit out most of 2022 (BTC spent it below
its 200-day average), so it is likely to fail 2022's rule quarters by not trading. Trials 305 → 309 for the baselines.

## Results (cycle 11, dev window 2021–2023; console `cycle_11_console.txt`)

All four rejected at the dev baseline; trials 305 → 309; no optimization, no OOS run.

| Bot | Net | PF | Sharpe | Max DD | Deals/yr | Win rate | Take-profits | Stop-outs | Rule quarters 22Q1 / 22Q3 / 23Q1 / 23Q3 |
|---|---|---|---|---|---|---|---|---|---|
| dca-grid-long | −8.9% | 0.77 | −0.83 | 13.1% | 547 | 97% | 1,597 = +$3,056 | 41 = −$3,947 | −1.3 / −0.5 / +1.2 / −0.6 |
| dca-grid-short | −7.4% | 0.84 | −0.62 | 8.9% | 565 | 97% | 1,647 = +$3,826 | 47 = −$4,562 | −1.7 / −1.0 / −4.3 / +0.6 |
| dca-rsi-long | −2.3% | 0.74 | −0.46 | 5.5% | 91 | 97% | 263 = +$680 | 9 = −$915 | −1.7 / −1.8 / +0.3 / −0.8 |
| dca-trend-long | −5.4% | 0.75 | −0.67 | 8.3% | 290 | 97% | 846 = +$1,644 | 22 = −$2,187 | 0.0 / 0.0 / +1.0 / −0.8 |

- **The classic DCA profile:** a 97% win rate made of hundreds of ~$2 take-profits, erased by a few dozen stop-outs of
  ~1% of equity each (a trend that ran through the whole grid). The win rate is what platform showcases display; the
  expectancy is negative here.
- **Without the stop** (how Veles bots usually run), those ~40 deals per three years would not have closed at −1%: they
  would have stayed open in "invest mode" through moves like 2022's −65%, with leverage. The longest deals here already
  lasted ~840 hours (5 weeks) before the stop or the target.
- Costs are not what kills it: fees + slippage were ~5% of starting equity over three years, versus a stop-out total of
  ~39%. The problem is the payoff shape (small fixed wins, rare full-grid losses), not execution.
- The 200-day trend filter did what was expected: no deals in 2022 (both 2022 rule quarters at 0.0% = fail), and it
  still lost money in 2021 and 2023.
