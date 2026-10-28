# Research sprint 1 (hunt cycles 5 and 6): sources, screening and rules

Researched 2026-10-26 (all sources accessed that day) with web search and page fetches. **No code, data or results
were sent anywhere**; searches were generic topic queries. Internet sources only suggest hypotheses. The evidence for
this project comes from our own runs: the hunt's dev baseline, optimizer, walk-forward and (if ever reached) the single
locked-OOS run.

## Constraints that shaped the designs

- Data available locally: Bybit BTCUSDT perp 1h OHLCV and 8h funding settlements (2020-03 → 2026-10). No basis, open
  interest, order flow, spot or other coins. Funding is used only once settled: as of each bar's open (conservative).
- Hunt kill rules: PF ≥ 1.1, Sharpe ≥ 0.5, max DD ≤ 30%, **≥ 50 trades/yr**, ≤ 1 failed rule quarter (2022Q1, 2022Q3,
  2023Q1, 2023Q3), and a rule quarter passes only if it makes money. The trade floor rules out classic slow trend
  following (10–30 trades/yr) and weekly calendar effects (≤ 52/yr); the quarter rule rules out "stand aside in chop".
- Lessons from cycles 3–4 (`artifacts/research/brief.md`): 6 of 10 ideas had an edge before costs that commission +
  slippage consumed (1h signals, ~150× turnover/yr); 3 had no edge at all; the hardest rule quarters were 2022Q3
  (directionless chop) and 2023Q3 (the quietest quarter). So this sprint leans to **daily structure and multi-day holds**,
  adds **funding** as a new information source, and adds a **directionality** (not volatility) regime variable.
- Defaults were fixed before any run on research data. A pilot on seeded **synthetic** data only (never the research
  data) checked trade frequency; it changed four defaults: noise-area-momentum got a one-entry-per-day rule (the literal
  rule traded ~620×/yr), tsmom-vote base 7 → 5 days, max-min-daily lookback 20 → 10 days, crowding-filtered-breakout
  channel 5 → 4 days (all stayed inside their sources' ranges), to clear the 50 trades/yr floor.
- **Sample overlap warning.** Several sources report results over periods that include our dev window (2021–2023) and
  some include our locked OOS window (2024–2026): Concretum (2018–2025), Catching Crypto Trends (to 2024/25),
  VanEck/K33 funding studies (to 2026), Padyšák & Vojtko (to 2022). Ideas picked because they were published as
  working over our holdout make a later OOS pass weaker evidence than it looks. Treat any OOS result with that in mind.

## Sources

| # | Source | Date | What it gives | Limits |
|---|---|---|---|---|
| 1 | Zarattini, Pagani, Barbon, *Catching Crypto Trends* — [abarbon.com](https://abarbon.com/papers/catching-crypto-trends), [RePEc rp2580](https://ideas.repec.org/p/chf/rpseri/rp2580.html) | SFI RP 2025, rev. 2025-04-09 | Ensemble of Donchian channels with several lookbacks + vol sizing; BTC then a rotating top-20 coin portfolio; Sharpe > 1.5, alpha 10.8%/yr net of fees | Portfolio and long-only framing; exact lookbacks not read (full text not fetched); unverified independently |
| 2 | Hong, *Bitcoin as an alternative investment vehicle* (Inf Technol Manag 2017) — [doi 10.1007/s10799-016-0264-6](https://www.doi.org/10.1007/s10799-016-0264-6) | 2017 | BTC time-series momentum over 1–8 weeks, partial reversal later | Early sample (pre-2017); abstract only |
| 3 | Han, Kang, Ryu, *Time series and cross-sectional momentum in the cryptocurrency market* — [ACFR pdf](https://acfr.aut.ac.nz/__data/assets/pdf_file/0009/918729/Time_Series_and_Cross_Sectional_Momentum_in_the_Cryptocurrency_Market_with_IA.pdf); Borgards, NAJEF 57 (2021) — [econpapers](https://econpapers.repec.org/RePEc:eee:ecofin:v:57:y:2021:i:c:s1062940821000590) | n.d. / 2021 | Time-series momentum evidence strong once costs are accounted for, cross-sectional weak; crypto momentum periods longer than in stocks | Search-summary level; date of the first not verified |
| 4 | Moskowitz, Ooi, Pedersen, *Time series momentum* (JFE 2012) | 2012 | Sign-of-past-return trend rule across asset classes | Not crypto; monthly horizon |
| 5 | Shen, Urquhart, Wang, *Bitcoin intraday time series momentum* (Financial Review 2022) — [Birmingham](https://research.birmingham.ac.uk/en/publications/bitcoin-intraday-time-series-momentum/), [working paper](https://reading-9.eprints-hosting.org/100181/3/21Sep2021Bitcoin%20Intraday%20Time-Series%20Momentum.R2.pdf) | 2022 | First half-hour predicts last half-hour (volume clock); liquidity-provision driven | Half-hour resolution; we have 1h bars |
| 6 | Zarattini, Aziz, Barbon, *Beat the Market: intraday momentum for SPY* — [SFI 24-97](https://www.sfi.ch/de/publications/n-24-97-beat-the-market-an-effective-intraday-momentum-strategy-for-s-p500-etf-spy), [RePEc](https://ideas.repec.org/p/chf/rpseri/rp2497.html) | 2024-05 (rev. 2025-02) | "Noise area" bands = open ± avg |move from open| at that time of day over 14 days; trend on a break; trailing exits; Sharpe 1.33 net on SPY 2007–2024 | SPY only; BTC has no session open |
| 7 | Concretum Group, *Seasonality in Bitcoin intraday trend trading* — [concretumgroup.com](https://concretumgroup.com/seasonality-in-bitcoin-intraday-trend-trading/) | ~2026-01 | BTC high-frequency trend ensemble Sharpe ~1.6 gross 2018–2025; strongest Sunday 19:00 ET → Monday ("Monday Asia open") | Rules not disclosed; gross of fees; vendor research |
| 8 | Schmeling, Schrimpf, Todorov, *Crypto carry*, BIS WP 1087 — [bis.org](https://www.bis.org/publ/work1087.htm) | 2023-04-04 | Carry (futures-spot) up to 60%/yr, driven by trend-chasing retail demand for leverage; **high carry predicts crashes** | Dated futures, not perpetual funding; horizon not stated on the page |
| 9 | He, Manela, Ross, von Wachter, *Fundamentals of Perpetual Futures* — [arXiv 2212.06888](https://arxiv.org/html/2212.06888v5) | 2022 (v5 later) | Perp–spot deviations large (60–90%/yr); funding mechanics | Arbitrage strategy needs spot; not a directional signal |
| 10 | VanEck (via Bitcoin Magazine) and K33 Research (via The Block, Decrypt) — [K33](https://www.theblock.co/post/315831/perp-signal-bitcoin-bottom-bullish-year-end-k33), [Decrypt](https://decrypt.co/366768/bitcoins-81k-rally-comes-amid-66-day-negative-funding-streak-heres-why) | 2024–2026 | Negative (30-day avg) funding preceded above-average 30–180-day BTC returns | 6–7 episodes; industry, not peer reviewed; contested interpretation |
| 11 | Crabel, *Day Trading with Short Term Price Patterns and Opening Range Breakout* (1990); Oxford Capital NR7 study — [oxfordstrat](https://oxfordstrat.com/trading-strategies/narrow-range/) | 1990 / n.d. | NR4/NR7 compression → breakout; tested on 42 futures markets | Practitioner; figures not captured; not crypto |
| 12 | Kaufman, *Smarter Trading* (1995); efficiency-ratio docs — [TradingView KAMA](https://tw.tradingview.com/support/solutions/43000773012/) | 1995 | Efficiency ratio as a trend/chop measure | No rigorous backtest found |
| 13 | Detzel, Liu, Strauss, Zhou, Zhu, *Learning and predictability via technical analysis: evidence from bitcoin* (Financial Management 50, 2021) — [RePEc](https://ideas.repec.org/a/bla/finmgt/v50y2021i1p107-137.html) | 2021 | Price-to-MA ratios predict daily BTC returns in and out of sample | Wei et al. (IJFE) with data-snooping control find only a short-term MA ratio survives |
| 14 | Padyšák & Vojtko, *Seasonality, trend-following and mean reversion in Bitcoin* (Quantpedia) — [quantpedia](https://quantpedia.com/trend-following-and-mean-reversion-in-bitcoin/) | 2022-03-15 | Buying BTC at its 10–50-day MAX (trend) or MIN (reversion) beat buy-and-hold 2015–2022 | In-sample, no costs, sample overlaps our dev |
| 15 | Bianchi, Babiak, Dickerson, *Trading volume and liquidity provision in cryptocurrency markets* (JBF 2022) — [Riksbank WP 413](https://www.riksbank.se/globalassets/media/rapporter/working-papers/2022/no.-413-trading-volume-and-liquidity-provision-in-cryptocurreny-markets.pdf) | 2022 | Reversal (liquidity provision) returns concentrate in low-activity conditions; lagged return × volume predicts | Cross-section of pairs; BTC is the most liquid pair |
| 16 | Munich working paper on intraday BTC reversal — [epub.ub.uni-muenchen.de/97651](https://epub.ub.uni-muenchen.de/97651/) (page did not load; search summary only); Ledger 2021 — [ledger.pitt.edu](https://ledger.pitt.edu/ojs/ledger/article/download/213/212/1232) | 2021–22 | Negative autocorrelation at 1–4h; bigger moves reverse more | Not read in full |
| 17 | Kitron & Wengrowicz, *Short-horizon mean reversion in cryptocurrency markets* — [arXiv 2608.21888](https://arxiv.org/pdf/2608.21888) | 2026-08 | 15-min reversal strong but **gross edge ~1.3 bp vs 5 bp round-trip cost** | 15-min horizon |
| 18 | Caporale & Plastun, *The day of the week effect in the crypto currency market* (FRL) — [DIW](https://www.diw.de/de/diw_01.c.567282.de/publikationen/diskussionspapiere/2017_1694/the_day_of_the_week_effect_in_the_crypto_currency_market.html) | 2017/2019 | Higher BTC Monday returns 2013–2017 | Published version: not significant vs random |
| 19 | Baur, Cahill, Godfrey, Liu, *Bitcoin time-of-day, day-of-week and month-of-year effects* — [UWA](https://research-repository.uwa.edu.au/en/publications/bitcoin-time-of-day-day-of-week-and-month-of-year-effects-in-retu/); Quantpedia intraday seasonality — [quantpedia](https://quantpedia.com/are-there-seasonal-intraday-or-overnight-anomalies-in-bitcoin/) | 2019 / n.d. | Academic: no persistent intraday pattern; practitioner: 21–23 UTC drift | Conflicting, data-mining risk |
| 20 | Barroso & Santa-Clara, *Momentum has its moments* (JFE 2015); Daniel & Moskowitz, *Momentum crashes* (JFE 2016) — [summary](https://www.cxoadvisory.com/momentum-investing/avoiding-momentum-strategy-crashes/) | 2015–16 | Vol-scaling cuts momentum crashes | Equities; sizing here is fixed by project rules |
| 21 | CME gap and Williams volatility breakout pages (TradingView scripts, Cointelegraph) | various | Popular claims ("~95% of CME gaps fill"; k = 0.5 breakout) | No study found; claims unverified |

## Screening (23 hypotheses)

| Hypothesis | Decision | Why |
|---|---|---|
| S1 Donchian ensemble trend (1) | **Cycle 5** | Recent crypto-specific evidence, net of fees; ensemble differs from cycle 3's single 72h channel |
| S2 Funding crowding fade (8, 10) | **Cycle 5** | New information source (funding); untried family |
| S3 Noise-area intraday momentum (6, 7) | **Cycle 5** | Rigorous recent method; different from session-momentum (fixed 11:00 decision); high cost risk |
| S4 NR4/NR7 compression breakout (11) | **Cycle 5** | Volatility contraction → expansion conditioned on daily structure; untested |
| S5 Daily TSMOM vote (2, 3, 4) | **Cycle 5** | Same family as cycle 3's ts-momentum but daily cadence, 3-horizon vote, weekly horizons |
| S6 Daily MAX/MIN (14) | **Cycle 6** | BTC-specific, buys both extremes; long only |
| S7 Multi-timeframe trend pullback (13) | **Cycle 6** | Untried family; P/MA daily regime + hourly entry |
| S8 Efficiency-ratio regime switch (12) | **Cycle 6** | Directionality regime; vol-regime-switch failed 2022Q3 where vol was normal but direction absent |
| S9 Volume-conditioned daily reversal/continuation (15) | **Cycle 6** | Price-volume interaction at daily horizon |
| S10 Crowding-filtered breakout (1, 8) | **Cycle 6** | Combination: trend entries never on the crowded side |
| Price-to-MA timing alone (13) | Merged into S7 | Substantially equivalent to ema-cross-atr / keltner-breakout |
| Monday Asia-open trend (7) | Rejected | ≤ 52 trades/yr fails the floor; seasonality found in a sample overlapping our OOS |
| Day-of-week (Monday) long (18) | Rejected | Not significant in the published version; ≤ 52 trades/yr |
| CME weekend gap fill (21) | Rejected | No study; ≤ 52 trades/yr; CME hours shift with DST |
| Hourly large-move fade (16, 17) | Rejected | Substantially equivalent to bollinger-reversion / volume-climax-reversal (no edge before costs); 15-min evidence says edge < costs |
| Last-hour intraday momentum (5) | Rejected | 1h hold: predicted move ≪ 0.12% round-trip cost at our resolution |
| Hour-of-day drift 21–23 UTC (19) | Rejected | Academic evidence says no persistent pattern; ~365 trades/yr of 2h holds are cost-bound |
| Funding-settlement drift | Rejected | No source found |
| Williams volatility breakout (21) | Merged into S3 | Same mechanism (break from the day open by a volatility band); S3 is the rigorous version |
| Bollinger-inside-Keltner squeeze | Merged into S4 | Same compression → breakout idea |
| Vol-managed trend sizing (20) | Not testable as such | Sizing is fixed (1% risk per trade); a vol filter would be a variant of S5 |
| Carry harvest (short perp for funding) (9) | Not testable | Needs a spot hedge; single-instrument engine |
| Volume-confirmed Donchian | Rejected | Variant of donchian-breakout |

## Retained hypotheses

Rules are exactly what is registered in `StrategyRegistry` (code: `src/Backtester.Core/Strategies/Cycle5Strategies.cs`,
`Cycle6Strategies.cs`). "dATR" = ATR(14) of completed UTC daily bars; daily decisions are taken on the 23:00 bar's close
and fill at 00:00. Every entry has a stop (1%-risk sizing); every grid has ≤ 192 points.

### S1 `donchian-ensemble-trend` (trend ensemble) — cycle 5
- **Hypothesis:** trends in BTC are best captured by agreement across several breakout horizons; requiring agreement
  filters the false breakouts that killed the single-channel donchian-breakout in 2022Q1/Q3 and 2023Q3.
- **Source:** 1 (ensemble of Donchian lookbacks). Evidence: net-of-fee Sharpe > 1.5 on a crypto portfolio. Not
  established: single-instrument BTC perp with a 1%-risk stop, short side, our lookbacks (shortened to meet the trade floor).
- **Why crypto:** retail trend-chasing and leverage (8) create persistent moves at days–weeks horizons (2).
- **Regimes:** good in 2021H1, 2022Q2, 2023Q1/Q4 type trends; bad in directionless quarters (2022Q3).
- **Data:** OHLCV only. **Differs from:** donchian-breakout (single 72h channel, fixed 1h-ATR stop, 24h exit channel).
- **Risks:** whipsaw in chop; the short leg is not in the source; still trend → still fails chop quarters unless the
  agreement filter keeps it flat-but-small. Overfitting: 3 parameters, grid 84.
- **Rules:** lookbacks baseDays × {1,2,4,8} days; model state flips on a close beyond its previous high/low; long on a fresh
  break of the fastest channel with ≥ minAgree models long (short mirror); exit when long models ≤ short models; stop trails
  the best close by trailMult × dATR. Defaults baseDays 2, minAgree 3, trailMult 2. Grid baseDays 1–6, minAgree 3–4,
  trailMult 1–4 step 0.5.

### S2 `funding-crowding-fade` (funding / carry) — cycle 5
- **Hypothesis:** extreme funding marks crowded leveraged positioning; crowded longs get liquidated (crashes), crowded
  shorts get squeezed. Enter against the crowd only once short-term price turns.
- **Sources:** 8 (high carry predicts crashes), 10 (negative funding preceded rallies), 9 (funding mechanics).
- **Evidence limits:** 8 uses dated-futures basis, not perp funding; 10 has 6–7 episodes. No source tests our rank rule.
- **Why crypto:** funding is paid by the crowded side every 8h; liquidation cascades (16) amplify unwinds.
- **Regimes:** good after euphoric runs and capitulations; bad in persistent trends where funding stays extreme.
- **Data:** funding settlements (local, 8h). **Differs from:** nothing before used funding.
- **Risks:** funding regime changes (structural delta-neutral supply, 10); rank window choice (fixed 9/270, not tuned).
- **Rules:** rank = percentile of the 3-day mean funding among the previous 90 days of such means (settled by the bar's
  open). Rank ≥ 1 − q: short when the close crosses below EMA(24); rank ≤ q: long on a cross above. Exit after holdBars;
  stop stopMult × dATR. Defaults q 0.2, holdBars 72, stopMult 1.5. Grid q 0.1–0.3, holdBars 24–120 step 24, stopMult 0.5–3.

### S3 `noise-area-momentum` (intraday momentum) — cycle 5
- **Hypothesis:** a move outside the usual intraday range for that hour signals a demand/supply imbalance that tends to
  continue for the rest of the session.
- **Sources:** 6 (method, SPY), 7 (BTC intraday trend is profitable gross, strongest around the Asia open), 5.
- **Evidence limits:** BTC has no session open (UTC midnight used); 7 is gross of fees with undisclosed rules.
- **Regimes:** good on volatile trending days; bad in mean-reverting hours (16) and with costs (~300 trades/yr).
- **Differs from:** session-momentum (one fixed 11:00 decision on an ATR√12 threshold, fixed 8h hold).
- **Rules:** σ_h = mean |close/open − 1| at hour h over lookbackDays previous days; bands open × (1 ± bandMult σ_h); from
  firstHour to 22:00, first close outside the band each day enters with the move; exit when the close is back inside
  max(band, VWAP) (short: min) or at the 23:00 close; stop at the day open. Defaults 14 days, 1.0, firstHour 2.
  Grid lookbackDays 7–28 step 7, bandMult 0.5–2 step 0.25, firstHour 0–6 step 2.

### S4 `nr-compression-breakout` (volatility contraction) — cycle 5
- **Hypothesis:** volatility clusters and mean-reverts; an unusually narrow day stores energy that the next day releases
  in the direction of the break.
- **Source:** 11. **Limits:** practitioner tests on futures, not crypto.
- **Differs from:** keltner-breakout/donchian (no compression condition; hourly bands), failed-breakout-fade (fades breaks).
- **Risks:** false breaks from narrow ranges; the stop is tight, so the 1x leverage cap binds often.
- **Rules:** NR-n day (range < each of previous n − 1 days) → next UTC day, first close above its high long / below its low
  short, once; stop stopMult × range beyond the broken level; exit after holdBars. Defaults NR4, 24 bars, 1.0. Grid nrLen
  4–7, holdBars 12–72 step 12, stopMult 0.5–1.5 step 0.25.

### S5 `tsmom-vote` (daily time-series momentum) — cycle 5
- **Hypothesis:** the sign of past 1–4 week returns predicts the next day's direction (2, 4); a vote over horizons is
  more robust than one lookback (cycle 3's 96h ts-momentum).
- **Sources:** 2, 3, 4. **Limits:** older samples; momentum crashes (20).
- **Regimes:** good in persistent trends; bad at turning points and in chop (re-entry after stops).
- **Rules:** on each daily close, vote = signs of returns over b, 2b, 4b days; hold the majority (unanimous = 1: only on
  3/3, else flat); re-evaluated daily; stop stopMult × dATR. Defaults b 5, majority, 2. Grid b 3–10, unanimous 0–1,
  stopMult 1–3.5.

### S6 `max-min-daily` (daily extremes) — cycle 6
- **Hypothesis:** BTC tends to rise the day after closing at an N-day high (breakout continuation) and after an N-day low
  (oversold rebound) (14).
- **Limits:** in-sample 2015–2022 without costs, overlaps our dev; long only, so 2022's bear quarters are a risk.
- **Rules:** daily close ≥ max or ≤ min of previous L closes → long; each signal day renews; exit after holdDays quiet
  days; stop stopMult × dATR. Defaults L 10, 1 day, 2. Grid L 10–50 step 10, holdDays 1–5, stopMult 1–3.

### S7 `mtf-trend-pullback` (multi-timeframe trend) — cycle 6
- **Hypothesis:** the price-to-MA ratio predicts daily BTC returns (13); entering on the end of an hourly pullback inside
  the daily trend improves entry price and turnover versus trading every hourly signal (cycle 3's trend-pullback-rsi).
- **Rules:** regime = sign(daily close − SMA(maDays)); in the regime's direction, enter when the hourly close re-crosses
  EMA(entryLen); exit when the regime changes; stop trails by trailMult × dATR. Defaults 20 days, 24, 2. Grid maDays
  10–50 step 10, entryLen 12–48 step 12, trailMult 1–3.

### S8 `efficiency-regime-switch` (directionality regime) — cycle 6
- **Hypothesis:** the right regime variable is directionality, not volatility: 2022Q3 had normal volatility but almost
  no net movement (daily efficiency 0.01), which vol-regime-switch could not see.
- **Source:** 12 plus our own diagnostics (dev data; this is a dev-informed hypothesis and is counted as a trial like any other).
- **Risks:** hourly signals → turnover ~220/yr on synthetic data, the cost problem of cycles 3–4.
- **Rules:** ER over erLen closes ≥ threshold → 48-bar breakout, exit on the opposite 24-bar channel; below it → fade
  SMA(48) ± 2 sd, exit at the SMA or after 48 bars; stop atrMult × ATR(14). Defaults 168, 0.2, 2.5. Grid erLen 72–264
  step 48, threshold 0.1–0.35 step 0.05, atrMult 1.5–3.5.

### S9 `volume-conditioned-reversal` (price-volume) — cycle 6
- **Hypothesis:** large daily moves on low volume are liquidity-driven and reverse; on high volume they carry
  information and continue (15; Llorente et al. 2002).
- **Limits:** 15 is cross-sectional; BTC is the most liquid pair, where the reversal premium is smallest.
- **Rules:** |daily return| in the top extremeFrac of the previous 90 days; volume < 30-day median → fade, else follow;
  hold holdDays; stop stopMult × dATR. Defaults 0.25, 1, 1.5. Grid extremeFrac 0.1–0.35, holdDays 1–3, stopMult 1–2.5.

### S10 `crowding-filtered-breakout` (trend + crowding) — cycle 6
- **Hypothesis:** breakouts that join an already crowded side are the ones that fail (8); skipping them improves a
  multi-day breakout.
- **Rules:** long on a close above the previous channelDays × 24 bars' high only if the funding rank ≤ maxRank; short
  only if ≥ 1 − maxRank; exit on the half-length opposite channel; trailing stop trailMult × dATR. maxRank 1 is the
  unfiltered control inside the grid. Defaults 4 days, 0.8, 2. Grid channelDays 2–7, maxRank 0.6–1.0, trailMult 1–3.

## Multiple-testing note

These 10 baselines add 10 trials (35 → 45). Any survivor adds up to its whole grid (≤ 192). The deflated Sharpe in the
cycle reports accounts for the cumulative count; with ~45 trials the best zero-edge trial already shows an annualized
Sharpe of roughly 1.2, so a dev Sharpe near the 0.5 floor is not evidence of an edge.

## Cycle 7: dev-informed ablation of the funding signal (written before cycle 7 ran)

Cycles 5–6 rejected all ten. Three cleared the dev PF/Sharpe floors, and two of those use funding:
funding-crowding-fade (PF 1.23, Sharpe 0.61, 58 trades/yr, failed only 2022Q3 by −0.9%; the only one that *earned*
funding, +$249) and crowding-filtered-breakout (PF 1.41, Sharpe 0.85, but 48 trades/yr and failed 2022Q3/2023Q3).
funding-crowding-fade passed the baseline and was optimized; its optimum sat on the edge of the quantile range (0.3, the
maximum), with Sharpe rising steadily toward the edge, so the rules rejected it.

That surface is the one result worth a follow-up, and it is **dev-informed**: the ranges and defaults below were chosen
after seeing cycle 5, so they are weaker evidence than an a-priori hypothesis, and every configuration counts as a trial.
Three hypotheses separate where the edge comes from:

| Hypothesis | Funding | Price timing | Question |
|---|---|---|---|
| `funding-tilt` | rank ≤ t / ≥ 1 − t, t up to 0.5 (median split) | EMA(24) cross | Does widening the crowding threshold keep improving it, and is the optimum interior? |
| `ema-cross-control` | ignored | EMA(24) cross | Same entries/exits without funding. If this does as well, funding adds nothing. |
| `funding-carry-tilt` | rank ≥ 0.5 + band → short, ≤ 0.5 − band → long, daily | none | Does the funding level alone predict direction? |

Defaults: funding-tilt t 0.4, hold 96 h, stop 1.5 dATR (grid t 0.3–0.5, hold 48–144, stop 1–2.5); control hold 96, stop
1.5 (grid 20 points); carry-tilt band 0.3, stop 2 (grid 25 points). funding-tilt at t = 0.3 repeats configurations
already counted under funding-crowding-fade (the registry counts them again under the new name, which only makes the
deflated Sharpe more conservative).

### Cycle 7 result and cycle 8 (written before cycle 8 ran)

| Hypothesis | Dev net | PF | Sharpe | Trades/yr | Funding received | Rule quarters failed |
|---|---|---|---|---|---|---|
| ema-cross-control (no funding) | −19.1% | 0.75 | −0.94 | 86 | +$155 | 2022Q1, 2023Q1, 2023Q3 |
| funding-tilt (t 0.4) | +7.7% | 1.12 | 0.37 | 75 | +$296 | 2022Q1, 2022Q3 |
| funding-carry-tilt (funding only) | +2.9% | 1.11 | 0.26 | 37 | +$192 | 2022Q3 |
| funding-crowding-fade (cycle 5, q 0.2) | +10.5% | 1.23 | 0.61 | 58 | +$249 | 2022Q3 |

On the development data, conditioning the same EMA(24) entries on funding turns −19% into +8–11%, and funding alone is
mildly positive: the funding signal carries information here. The widened threshold (0.4) is worse than cycle 5's 0.2
and its 0.3 optimum, so the surface peaks near 0.3. Cycle 8 re-tests that peak with the range 0.2–0.4 around it
(`funding-fade-wide`, default = the cycle 5 optimum q 0.3 / 96 h / 1.5 dATR, which was optimized but never
walk-forward tested). This is the remedy the edge rule asks for (is the optimum interior once the range covers it?), but
it is chosen from ~200 dev trials: its baseline is not independent evidence, the walk-forward folds lie inside the window
it was picked on, and only the single locked-OOS run would be a real test. The 2024–2026 OOS period is also covered by the
public VanEck/K33 funding studies that motivated S2.

### Cycle 8 result: rejected at the locked OOS run

funding-fade-wide passed everything on the development window: baseline PF 1.37, Sharpe 0.98, max DD 5.5%, 66 trades/yr;
the optimum stayed at q 0.3 / 96 h / 1.5 dATR and was interior with all six neighbours on the plateau (Sharpe 0.49–0.93);
walk-forward failed only 2022Q1 (−2.0%); slippage ×0–×2 kept net +22.9% … +19.8%. Its single locked-OOS run
(2024-01-01 → 2026-10-26, logged in `artifacts/oos_access.log`) made −0.2%: PF 1.00, Sharpe 0.02, max DD 9.8%, 195 trades;
funding received fell from $291 (dev) to $84. Buy-and-hold made +89.1% over the same window. Rejected.

Reading: a dev Sharpe of 0.98 picked from ~200 trials is about what the best zero-edge trial would show (~1.0), so this is
the outcome the deflated Sharpe predicted. **The OOS window is now spent for the funding-crowding family**: any later
funding variant designed after this result would be tuned with knowledge of 2024–2026 and must not use that window as
evidence.

### Effort and external calls for this sprint

- Web: 17 searches and 4 page fetches (1 failed: the Munich repository refused the connection); generic topic queries only.
- Model use: this sprint was one interactive Claude Code session; no other model was consulted, and the research loop made
  0 calls. Token use is logged as an estimate in `artifacts/research/model_calls.jsonl`.
- Local compute: cycles 5–8 = 14 hypotheses, 262 new trials, about 4 s of backtesting in total.
