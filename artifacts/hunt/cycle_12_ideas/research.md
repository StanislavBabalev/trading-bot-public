# Research sprint 3 (hunt cycle 12): sources, screening and rules

Researched 2026-10-27 (all sources accessed that day) with generic web searches. **No code, data or results were sent
anywhere.** Sources only suggest hypotheses. The evidence for this project comes from our own runs: the hunt's dev
checks, then (new since cycle 11) the 13 hard validation gates (README "Validation gates"), then at most one
locked-OOS run.

## Starting point

- 36 hypotheses tested, 309 registered trials. The chance threshold for the best of them is an annualized Sharpe of
  ~1.10 (raw and effective trial counts agree). The 17 profitable baselines all fail DSR, PBO and the bootstrap PF gate.
- Sprint 1 already screened and rejected these: day-of-week, the CME weekend gap, the Monday Asia open, the hour-of-day
  drift, a funding-settlement drift (no source), the Williams volatility breakout and the hourly large-move fade
  (`artifacts/hunt/cycle_5_ideas/research.md`). They are not reconsidered here unless something new turned up.
- Constraints are unchanged: BTC 1h OHLCV + settled 8h funding only; at least 50 trades/yr; a stop on every entry;
  1% risk; costs of 0.12% per round trip. Six of the ten diagnosed ideas had an edge before costs that costs consumed.
  So every hypothesis below is event-driven (few trades, multi-hour holds), not a continuous position.
- Prior evidence against the whole programme: Hudson & Urquhart (2019) tested ~15,000 technical rules with
  data-snooping control and found **no out-of-sample predictability for Bitcoin** (source 1). This sprint therefore
  tests only three mechanism-based ideas rather than filling five slots.

## Sources

| # | Source | Date | What it gives | Limits |
|---|---|---|---|---|
| 1 | Hudson & Urquhart, *Technical trading and cryptocurrencies*, Annals of OR — [Springer](https://link.springer.com/article/10.1007/s10479-019-03357-1), [Hull](https://hull-repository.worktribe.com/output/2328042) | 2019 | ~15,000 rules; reality-check/SPA; no OOS predictability for BTC | Pre-2019 sample |
| 2 | Bianchi, Babiak, Dickerson, *Trading volume and liquidity provision in cryptocurrency markets* (JBF 2022) — [Riksbank WP 413](https://www.riksbank.se/globalassets/media/rapporter/working-papers/2022/no.-413-trading-volume-and-liquidity-provision-in-cryptocurreny-markets.pdf) | 2022 | Short-term reversal = return to liquidity provision; larger when volatile and illiquid | Cross-section of pairs |
| 3 | *Returns from liquidity provision in cryptocurrency markets* — [Dundee](https://discovery.dundee.ac.uk/en/publications/returns-from-liquidity-provision-in-cryptocurrency-markets/); JBF 2025 paper — [Southampton](https://eprints.soton.ac.uk/501151/2/1-s2.0-S0378426625000317-main.pdf) | 2024–25 | Liquidity-provision returns are predictable from realized variance and crash/tail risk; market makers are compensated during turmoil (100+ coins, 2017–2022) | Summary level; sample overlaps our dev window |
| 4 | Liquidation-cascade commentary: Amberdata, FalconX, Coinchange, tradermath — [Amberdata](https://blog.amberdata.io/leverage-liquidations-the-31b-deleveraging), [FalconX](https://www.falconx.io/newsroom/unpacking-the-january-31-liquidation-cascade), [Coinchange](https://www.coinchange.io/blog/bitcoins-2-billion-reckoning-how-novembers-liquidations-cascade-exposed-cryptos-structural-fragilities), [tradermath](https://www.tradermath.org/courses/introduction-to-digital-assets/funding-rates-and-perpetual-futures) | 2024–26 | Crowded positioning (high funding) precedes forced-liquidation cascades; funding resets as positions clear | Practitioner, no event study or significance |
| 5 | Lee & Seong, *Psychological barriers in Bitcoin* (ICIS 2019) — [AIS eLibrary](https://aisel.aisnet.org/icis2019/blockchain_fintech/blockchain_fintech/12) | 2019 | Proximity and hump tests: barriers at the 1,000 and 10,000 levels on three exchanges | Abstract only; barriers "jump" after new highs |
| 6 | Sieber, *Price clustering and psychological barriers in Bitcoin market* — [CEEOL](https://www.ceeol.com/search/article-detail?id=1043301); Hu, McInish, Miller, Zeng (FRL 2019) — [Memphis](https://digitalcommons.memphis.edu/facpubs/11603) | 2019–21 | Daily highs/lows cluster on round digits: robust for **support**, ambiguous for resistance; trades cluster on round numbers | Clustering, not a trading test |
| 7 | Jia, Liu, Yan (FRL 2021) — [RePEc](https://ideas.repec.org/a/eee/finlet/v39y2021ics1544612320303135.html); *Time-varying expected returns, conditional skewness and Bitcoin return predictability* (QREF 2024) — [UAH](https://ebuah.uah.es/xmlui/handle/10017/63078) | 2021 / 2024 | Skewness predicts crypto returns negatively, but **for BTC the sign was negative in 2018–2020 and positive in 2021–2022** | Second source covers our dev window |
| 8 | Özdamar et al. (Fin Innov 2021); Grobys & Junttila (JIFMIM 2021) — [Bilkent](https://repository.bilkent.edu.tr/items/78167f5c-d3e2-4f07-b4bc-5e8a3ed0b2c0), [Uwasa](https://osuva.uwasa.fi/handle/11111/1871) | 2021 | MAX (lottery) effect in crypto: positive in one study, negative in the other | Cross-sectional; opposite signs |
| 9 | NYDIG, K33, ParaFi, Amberdata on session returns; Bloomberg on the "night premium" ETF — [NYDIG](https://nydig.com/research/a-look-at-geographic-drivers-of-returns), [K33](https://k33.com/research/archive/articles/bitcoin-mostly-sleeps-during-asian-market-hours), [ParaFi](https://parafi.com/signals/bitcoin-biggest-moves-market-hours), [Amberdata](https://blog.amberdata.io/trading-between-hours-volatility-dispersion-across-multiple-regions), [BNN](https://www.bnnbloomberg.ca/why-bitcoin-sees-most-of-its-gains-while-us-traders-are-asleep-1.1777884) | 2021–26 | Returns concentrate in some sessions; which session changes over time | Industry notes; conflicting |
| 10 | Bitcoin jump studies — [Springer 2024](https://link.springer.com/article/10.1007/s42521-024-00116-1), [arXiv 2110.09429](https://arxiv.org/abs/2110.09429v1), Lee & Wang (JFQA 2025) — [RePEc](https://ideas.repec.org/a/cup/jfinqa/v60y2025i4p1859-1890_9.html) | 2021–25 | Jumps are frequent and predict lower returns cross-sectionally; no BTC-specific sign | Mostly cross-sectional |
| 11 | Funding mechanics — [He et al., arXiv 2212.06888](https://arxiv.org/pdf/2212.06888), [Maverick Quant](https://maverickquant.substack.com/p/perpetual-futures) | 2022–25 | Funding = 8h average premium; settlement at 00/08/16 UTC | No study of returns around settlement |

## Screening (9 ideas)

| Idea | Decision | Why |
|---|---|---|
| C1 Liquidation-cascade fade with funding crowding (2, 3, 4) | **Cycle 12** | Forced selling of the crowded side overshoots; liquidity providers are paid in turmoil. Differs from volume-climax-reversal (which needed a wick rejection and ignored funding) |
| C2 Turmoil reversal on 4h bars (2, 3) | **Cycle 12** | Reversal only in high realized-volatility regimes, where (3) finds liquidity provision is paid. vol-regime-switch faded only in *quiet* regimes; bollinger-reversion had no regime filter |
| C3 Round-number support bounce / resistance rejection (5, 6) | **Cycle 12** | New information (price level itself); academic evidence for barriers at the 1,000 level, support stronger than resistance |
| Round-number break continuation (5) | Rejected | The opposite bet on the same events as C3; testing both doubles trials on one question |
| Realized-skewness reversal (7) | Rejected | The source covering 2021–2022 finds the BTC sign flipped positive in our dev window. Testing the theory's sign is expected to fail; picking the sign from that paper would be dev-period look-ahead through the literature |
| MAX / lottery effect, time-series (8) | Rejected | Sign differs between the two studies; cross-sectional evidence only |
| Overnight / non-US-hours tilt (9) | Rejected | Needs ~250 round trips/yr ≈ 30%/yr of costs at 0.12% each; BTC's dev drift (~13%/yr) cannot cover it; sources disagree on the session |
| Jump reversal (10) | Rejected | No BTC-specific direction; overlaps impulse-bar-continuation and C1 |
| Funding-settlement drift (11) | Rejected again | Still no study of returns around settlement times |

## Retained hypotheses (rules and defaults fixed before any run on research data)

All three use the engine's fill at the next bar's open, one position at a time, and a stop on every entry. Defaults came
from the sources' scales and were then checked only on seeded **synthetic** data for the 50 trades/yr floor (see the
pilot section). Grids have three parameters and at most 192 points.

### C1 `cascade-fade` (liquidation cascade, hourly)
- **Hypothesis:** a large one-hour move on heavy volume against the side that funding shows to be crowded is forced
  liquidation, and it overshoots. Liquidity providers are paid to absorb it, so the move partly reverses over the next
  hours.
- **Rules:**
  - ATR(14) and the mean volume of the previous 48 bars are measured before the bar.
  - rank = the 3-day funding crowding rank (mean of the last 9 settled rates among the previous 90 days of such means;
    settled by the bar's open), as in funding-crowding-fade.
  - **Long** when close − open ≤ −dropAtr × ATR, volume ≥ volMult × mean volume and rank ≥ 0.5 (longs were crowded).
  - **Short** when close − open ≥ dropAtr × ATR, volume ≥ volMult × mean volume and rank ≤ 0.5.
  - Exit after holdBars bars, or earlier on a close beyond the cascade bar's open (the move fully retraced).
  - Stop 1 ATR beyond the cascade bar's extreme.
- **Defaults / grid:** dropAtr 1.5 (1–3, step 0.5), volMult 1.5 (1–3, step 0.5), holdBars 24 (12–48, step 12). That is
  100 points. The defaults were lowered from 2.5 / 2.5 by the synthetic pilot (below).
- **Regimes:** good in leverage flushes (2021-05, 2022-06, 2022-11); bad when a cascade starts a trend (the stop limits
  the loss).

### C2 `turmoil-reversal` (liquidity provision in high volatility, 4h)
- **Hypothesis:** in turmoil, large 4h moves are partly liquidity-driven and revert. In calm markets they carry
  information.
- **Rules:**
  - Work on UTC-aligned 4h bars, with ATR(14) and EMA(20) of 4h closes.
  - Turmoil when the 4h ATR / close ratio is at or above the volQuantile quantile of its previous 540 values (90 days).
  - **Long** when the 4h close − open ≤ −zEntry × the previous 4h ATR in turmoil.
  - **Short** on the mirror.
  - Exit on a 4h close back across EMA(20), or after holdPeriods 4h bars.
  - Stop 1 4h-ATR beyond the 4h bar's extreme.
- **Defaults / grid:** zEntry 1.0 (0.5–2, step 0.25), volQuantile 0.7 (0.5–0.9, step 0.1), holdPeriods 6 (3–12, step 3).
  That is 140 points. Changed from 1.5 / 0.75 by the synthetic pilot (below).
- **Differs from:** bollinger-reversion (1h, no regime), vol-regime-switch (fades only when quiet), efficiency-regime-switch
  (efficiency regime, 1h).

### C3 `round-number-bounce` (psychological barriers, hourly)
- **Hypothesis:** round levels act as barriers (5). The first test of a level from one side tends to hold, support more
  than resistance (6).
- **Rules:**
  - Levels are the multiples of levelStep (USD).
  - **Long** when a bar opens above a level L, trades down to or through it (low ≤ L) and closes back at or above L,
    and no bar in the previous freshBars hours traded at or below L (first test).
  - **Short** on the mirror at the nearest level above the open (high ≥ U, close ≤ U, no bar at or above U in the
    previous freshBars hours).
  - A bar that tests levels on both sides is skipped.
  - Exit after holdBars bars. Stop 1 ATR(14) beyond the level.
- **Defaults / grid:** levelStep 1000 (500–2500, step 500), freshBars 24 (12–60, step 12), holdBars 24 (12–48, step 12).
  That is 100 points. freshBars was lowered from 72 by the synthetic pilot (below), and the levelStep grid was re-centred so
  the source's 1,000 level is interior rather than on the grid edge.
- **Regimes:** good in ranges (2022Q3, 2023Q3 were the most-failed rule quarters); bad in strong trends that run through
  levels.

## Synthetic pilot (trade frequency only)

The pilot ran each strategy on 5 seeds of `SyntheticData`: 2021–2023 after 281 days of warm-up, prices scaled ×3 to BTC's
dev range, at hunt costs and sizing. It measured trades per year only. The research data were not touched. Synthetic
returns have thinner tails than BTC, so event strategies trade less on them; the pilot is a lower bound.

| Strategy | First defaults → trades/yr (median) | Final defaults → trades/yr (5 seeds) |
|---|---|---|
| cascade-fade | dropAtr 2.5, volMult 2.5 → ~1–3 | dropAtr 1.5, volMult 1.5 → 58 69 63 70 64 |
| turmoil-reversal | zEntry 1.5, volQuantile 0.75 → ~17 | zEntry 1.0, volQuantile 0.7 → 52 63 56 62 65 |
| round-number-bounce | levelStep 1000, freshBars 72 → ~5–52 | levelStep 1000, freshBars 24 → 22 84 57 50 120 |

The floor forced looser events than the sources' framing:
- cascade-fade is now a 1.5-ATR hourly body on 1.5× volume, a large move rather than a rare 3-sigma cascade;
- "turmoil" is the top 30% of 4h volatility rather than the top quartile.

This is the 50 trades/yr floor working against event strategies, as noted in CLAUDE.md. round-number-bounce depends
strongly on the price path: 22–120 trades/yr across seeds.

## Result (hunt cycle 12, dev 2021–2023)

All three were rejected at the dev baseline; no optimization or holdout run. Trials went from 309 to 312
(`artifacts/hunt/cycle_12/report.json`, console `artifacts/hunt/cycle_12_console.txt`).

| Strategy | Net % | PF | Sharpe | Max DD % | Trades/yr | Edge before costs % | Costs % | Failed rule quarters |
|---|---|---|---|---|---|---|---|---|
| cascade-fade | −46.6 | 0.75 | −1.45 | 49.5 | 132 | −17.3 | 28.0 | all four |
| turmoil-reversal | +2.4 | 1.05 | 0.16 | 10.5 | 42 | +6.9 | 4.5 | 2023Q1 |
| round-number-bounce | −31.0 | 0.87 | −0.65 | 43.3 | 123 | −0.2 | 29.5 | 2022Q1, 2022Q3, 2023Q1 |

- **cascade-fade** lost money before costs. On real BTC, 1.5-ATR hourly bodies on high volume continued rather than
  reverted, whatever funding said about crowding. Real data produced about twice the synthetic pilot's trade count (fat
  tails).
- **turmoil-reversal** had a small gross edge that costs mostly consumed, and it missed the trade floor. Its 4h events
  were rarer on real data than in the pilot.
- **round-number-bounce** had no edge before costs: first tests of $1,000 levels did not hold more often than chance.

The validation harness (`python -m validation run`) then re-checked every profitable dev baseline, now 18 with
turmoil-reversal. None passes. turmoil-reversal fails 9 of 13 hard gates, including DSR 0.04 and plateau score 0.11. The
three new losing trials raised the cross-trial Sharpe variance. The chance threshold moved from 1.09 / 1.10 (raw /
effective) to 1.12 / 1.18 annualized, so every earlier candidate's DSR fell as well.
