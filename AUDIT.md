# Phase 1 audit: validation rigor (2026-10-27)

Audited commit `2b73fa3` (clean tree). Read: the engine, strategy interface, feature cache, all 37 registry entries, the
optimizer, the hunt pipeline, the metrics/DSR code, the data layer, the trial registry and the cycle reports. Nothing was
changed and no backtest was run for this audit. The only computations were statistics over the existing
`artifacts/hunt/trials.jsonl`. Baseline: `dotnet test TradingBot.slnx` passes all 127 tests (CLAUDE.md still says 120).

**Bottom line.** The bar engine is causal and its cost model is mostly sound. The weak points are in the process
around it:

1. The look-ahead splice test covers 26 of the 37 registered strategies.
2. DSR has never been computed for any hypothesis. It runs only for candidates that already passed the locked OOS run.
3. The trial count of 309 contains 48 duplicates and is dominated by one family.
4. The "locked" holdout was partly seen before the hunt began.
5. Nothing in the planned Python layer would enforce the OOS lock.

The pasted figures check out against the repository: 309 trials, 17 of 36 hypotheses profitable on dev, 5 with Sharpe ≥ 0.50, best 0.98, and a threshold of 1.09.

## Status after Phase 2 (2026-10-27)

Fix items 1–11 (P0 and P1) are implemented; README.md "Validation gates" describes them.

- **P0.1 (L2):** `LookAheadAudit` now runs on all 37 strategies. It compares per-bar signals, `FeatureCache` series,
  trades and funding settlement, on 8 spliced and mirrored futures.
- **P0.2 (L14):** the Python layer cannot read rows from the holdout window (`validation/data.py`). Its network access is
  blocked and a test checks it.
- **P0.3:** `validate-export` replays all 309 registered trials (260 distinct series, 0 replay mismatches) and exports one
  bundle per candidate.
- **P0.4 (L13):** hunt step 7 and `backtest --unlock-oos` both require a passing report for the exact candidate. Each
  holdout access is logged with the code, source, git and data hashes.
- **P1.5–P1.11:** the gates, the report schema and the single command `python -m validation run`.

Where the implementation differs from the D recommendations:

- **Effective N (§4):** Li & Ji's eigenvalue count (64), with the variance taken across 64 hierarchical clusters. The ONC
  base step found only 2 clusters on this matrix. The DSR gate uses the stricter of the raw and effective benchmarks.
- **D5:** the losing-regimes rule counts across all six regimes. Within a two-way partition it could never fire for a
  profitable strategy.
- **D6:** the PBO gate is computed over the whole search (all 260 trial series). The local PBO and CPCV are reported but
  not gated.

**Result: 0 of the 17 profitable hypotheses pass** (see `artifacts/validation/summary.md`). Still open: the P2 items
(12–15) and Phases 3–5.

---

## 1. Repository map

| Concern | Where | Notes |
|---|---|---|
| CLI entry point | [src/Backtester.Cli/Program.cs](src/Backtester.Cli/Program.cs) | `strategies, fetch, audit, backtest, optimize, walkforward, hunt, research, report, export, bench` |
| Hunt pipeline (steps 1–9) | [src/Backtester.Cli/Hunt.cs](src/Backtester.Cli/Hunt.cs) | baseline → kill → optimize → optimized kill + folds + slippage → single locked-OOS run → DSR ranking |
| Research loop | [src/Backtester.Cli/Research.cs](src/Backtester.Cli/Research.cs), [Research/](src/Backtester.Core/Research/) | experiment store keyed by full identity (code MVID + data hash); resumable |
| Engine | [Engine/BacktestEngine.cs](src/Backtester.Core/Engine/BacktestEngine.cs) | bar loop, one position, stop orders only; `ISelfSimulatingStrategy` bypasses it entirely |
| Strategy interface | [Strategies/IStrategy.cs](src/Backtester.Core/Strategies/IStrategy.cs), [Models.cs](src/Backtester.Core/Models.cs) | `OnBarClose(ctx) → Signal(direction, stop)`; `ctx.Back(n)` blocks n < 0 |
| Shared indicators | [Engine/FeatureCache.cs](src/Backtester.Core/Engine/FeatureCache.cs) | full-history arrays; "read only up to ctx.Index" is a convention |
| Strategies | [Strategies/StrategyRegistry.cs](src/Backtester.Core/Strategies/StrategyRegistry.cs) + `Cycle*Strategies.cs` | 37 definitions (36 hunt hypotheses + `ema-cross-atr`) |
| Parameter search | [Optimization/GridOptimizer.cs](src/Backtester.Core/Optimization/GridOptimizer.cs) | exhaustive grid ≤ 192 pts, objective Sharpe, edge check, ±1-step plateau (all neighbours must pass) |
| Metrics | [Analytics/PerformanceMetrics.cs](src/Backtester.Core/Analytics/PerformanceMetrics.cs) | daily UTC returns × √365; max DD on bar-close equity; PF on net trade PnL (funding included) |
| DSR / PSR | [Analytics/DeflatedSharpe.cs](src/Backtester.Core/Analytics/DeflatedSharpe.cs) | Bailey & López de Prado (2014); formula correct (non-excess kurtosis, `(γ4−1)/4`) |
| "Walk-forward" | [Analytics/WalkForward.cs](src/Backtester.Core/Analytics/WalkForward.cs), `Hunt.RunFolds` | 4 test quarters inside dev with fixed parameters (no refit). These are sub-period checks, not walk-forward |
| Trial registry | `TrialRegistry` in [Hunt.cs](src/Backtester.Cli/Hunt.cs) → `artifacts/hunt/trials.jsonl` | stores Sharpe/PF/trades per config, **not** return series |
| Data | [Data/BybitClient.cs](src/Backtester.Core/Data/BybitClient.cs), [CsvStore.cs](src/Backtester.Core/Data/CsvStore.cs), [DataAudit.cs](src/Backtester.Core/Data/DataAudit.cs) | `data/bybit/BTCUSDT_60.csv` + `BTCUSDT_funding.csv`; STEP 0 audit pins their SHA-256 (`Hunt.RequireAuditedData`) |
| Export | [src/Backtester.Cli/Export.cs](src/Backtester.Cli/Export.cs) + [Reporting/Xlsx.cs](src/Backtester.Core/Reporting/Xlsx.cs) | 10 sheets: Summary, Strategies, Parameters, Quarters, Optimization, Top trials, Slippage, Locked OOS, Cycles, Settings |
| Python | `.venv` only, no Python code in the repo | purgedcv 0.1.11, holdout-backtest 0.1.0, scikit-learn 1.9.1, duckdb, polars, pyarrow; **rank_bm25 not installed** |

---

## 2. Look-ahead audit

| # | Entry point | Current behaviour | Evidence | Verdict |
|---|---|---|---|---|
| L1 | Signal timestamp | Strategy sees bar *i* after it closes; signal fills at bar *i+1* open | `BacktestEngine.cs:127,137-143`; test `Signal_fills_at_next_bar_open_never_same_bar` | OK |
| L2 | Access to future bars | `ctx.Back(n<0)` throws, but `FeatureCache` hands out full-history arrays, and `ISelfSimulatingStrategy.Simulate` receives the whole `Bar[]`/`FundingRate[]` | `IStrategy.cs` (StrategyContext), `FeatureCache.cs`, `BacktestEngine.cs:69` | **Convention only.** Guarded by the splice test, which covers **26 of 37** strategies (`Cycle5StrategyTests.cs` `Sprint` list). Not covered: `ema-cross-atr`, the 5 cycle-3 and 5 cycle-4 strategies |
| L3 | Funding settlement | Rates with `TimeMs ≤ bar open` settle before that bar's fill, on the position held into the timestamp; a position opened at that open doesn't pay, one closed at it does | `BacktestEngine.cs:118-120`; test `Funding_at_the_entry_bar_open_is_not_charged` | OK (conservative on the close side) |
| L4 | Funding as a signal input | `Funding()`, `FundingRank()` use rates settled by each bar's *open*, so up to one bar later than necessary | `FeatureCache.cs` `FundingCount` | OK (conservative) |
| L5 | Bar close vs open | Decision on close, fill at next open at exactly that price (+0.02% slippage); zero latency assumed | `BacktestEngine.cs:257` | Assumption (see C6) |
| L6 | Stop fills | Resting stop fills at `min(open, stop)` (long) the bar it's touched, including the entry bar; updated stops apply from the next open | `BacktestEngine.cs:323-336`, `:248` | Causal; **optimistic** in fast markets (fills at the stop + 0.02%) |
| L7 | Limit / take-profit fills (DCA only) | Intrabar path assumed O→L→H→C on up bars, O→H→L→C on down bars; limits and TP fill on **touch** | `Cycle11Strategies.cs:215-242` | Heuristic + optimistic touch fills; it only affects the 4 DCA bots, all rejected |
| L8 | Window end | An open position is closed at the open of the first bar **after** the window, so every dev run's last exit uses the 2024-01-01 00:00 price, the first holdout bar's open | `BacktestEngine.cs:91-98` | Minor leak of one price; fix by closing at the last in-window close |
| L9 | Warm-up | Indicators warm up on earlier bars; the run starts flat and ignores a direction held during warm-up | `BacktestEngine.cs:101-109` | OK; OOS warm-up reads dev bars, which is fine |
| L10 | Leverage / liquidation | No maintenance margin, liquidation price or ADL; the equity ≤ 0 halt is checked at bar **close** only | `BacktestEngine.cs:345-367` | Unreachable at ≤ 1x with stops, except under a gap through the stop (Phase 2E scenario) |
| L11 | Data | Only closed klines fetched (`now − interval`); last-trade klines, not mark price; no gaps in 57,735 bars | `BybitClient.cs:41-42` | OK |
| L12 | Research-process leakage | `ema-cross-atr` 80/260/5×ATR was chosen on trader-dev using **2024-10-26 → 2026-10-26**, which is inside the locked OOS window. The funding-crowding family has spent its OOS run. Ideas also came from LLMs whose training data covers 2024–2026 BTC | CLAUDE.md "Backtests run"; `artifacts/oos_access.log` | **The holdout is not blind.** It was never logged as touched for the EMA family |
| L13 | OOS escape hatch | `backtest --unlock-oos` runs any config on OOS; it's logged (timestamp + config) but not gated, and the log has no code or data hash | `Program.cs:157,409-420` | Needs the Phase 2 gate |
| L14 | Python side | Nothing stops a Python script from reading CSV rows ≥ 2024-01-01 | n/a | **Must be built** before any Python validation code exists |

---

## 3. Cost and execution assumptions

| # | Assumption | Value / behaviour | Risk |
|---|---|---|---|
| C1 | Commission | 0.04% per side on every fill, treated as taker | Bybit's base (non-VIP) taker fee for USDT perps is 0.055%, as far as I know. **Confirm your fee tier.** At 0.055% a round trip costs 0.15% instead of 0.12% |
| C2 | Slippage | Constant 0.02% per side, independent of volatility, size or order type | Understates stop fills in cascades; overstates nothing |
| C3 | Stop execution | Stop-market fills at the stop price (or open if gapped) + 0.02% | Optimistic on liquidation-cascade bars (2021-05-19, 2022-06-13, 2022-11-08) |
| C4 | Funding | Priced at the bar open's **last** price (mark proxy), sign correct (longs pay positive rates), 8h interval as in the data | Small; mark vs last basis is usually < 0.1% |
| C5 | Sizing | 1% of **cash** at entry ÷ stop distance from the slipped fill, capped at 1x; never rebalanced | Realized loss at a stop = 1% + round-trip costs + funding (≈ 1.1–1.3% typical) |
| C6 | Latency / microstructure | Zero latency; no partial fills, no lot-size or min-qty rounding, no queue position | Fine at $10k equity; touch-fill limits (DCA) are optimistic |
| C7 | Drawdown | Max DD on hourly **close** equity | Understates intrabar DD; already warned in reports |
| C8 | Sharpe | Daily UTC returns × √365; flat days count as zero returns | Standard; flat days lower the volatility and raise the observation count *n* in PSR |
| C9 | Cost stress today | Slippage 0, 0.5, 1, 1.5, 2× is **reported only** (not a kill rule); no commission or funding stress | `Hunt.cs:395-402` |

The PnL components already exist per run (`FeesPaid`, `SlippageCost`, `FundingReceived`, and gross per trade), so the attribution in Phase 3C needs no engine change except a regime-beta term.

---

## 4. Trial counting and multiple testing

**How 309 was counted.** It's the number of distinct `(strategy name, parameter set)` pairs ever evaluated on the full dev window
(`TrialRegistry.Add`, deduplicated per strategy name). Fold runs and slippage variants don't count.

| Source | Trials |
|---|---|
| `ema-cross-atr` pre-hunt local grid (smoke test) | 25 |
| Baselines of 36 hypotheses | 36 |
| `funding-crowding-fade` optimizer (grid + plateau neighbours) | 149 (150 incl. baseline) |
| `funding-fade-wide` optimizer | 99 (100 incl. baseline) |
| **Total** | **309** |

Findings:

- **48 trials are exact duplicates.** `funding-fade-wide` is `funding-crowding-fade` under another name. Their grids overlap on 48 points with identical results (max Sharpe difference 0.0), so there are **261 distinct return streams**.
- **81% of trials (250/309) come from one family**, and neighbouring grid points are highly correlated. The search space is not independent: the effective number of trials is far below 309.
- The cycle-7 ablations (`funding-tilt`, `ema-cross-control`, `funding-carry-tilt`) and `funding-fade-wide` were designed after looking at dev results. They're counted, which is correct, but they're forking paths.
- The trader-dev EMA work (~180 credits of sweeps and parity checks) isn't counted. That's correct for dev, because it ran on the holdout window. That's the L12 problem.
- **DSR was never computed on any hypothesis.** `Hunt.cs:222` computes it only for `PASSED` candidates, after the locked OOS run, to rank champions. It's a ranking tool, not a pre-OOS gate. `cycle_8/report.json` has `dsr: null` for the only candidate that reached OOS.
- PSR, MinTRL, PBO, CPCV, bootstrap and regime stratification don't exist yet.
- The registry stores only summary metrics, so correlations, effective N and CSCV need per-config return series. To get them, re-run the 261 distinct configs on dev. That's about a second of CPU, and they aren't new trials.

**The chance threshold depends on how N and the variance are estimated** (annualized; from `trials.jsonl`):

| Estimator | N | σ(SR) across trials | E[max SR] under zero edge |
|---|---|---|---|
| As reported: raw count, pooled variance | 309 | 0.37 | **1.09** |
| One per strategy, pooled variance (inconsistent mix) | 37 | 0.37 | 0.81 |
| One per strategy, variance of strategy-mean Sharpes (López de Prado 2019 style) | 37 | 0.57 | **1.22** |

The consistent estimators (rows 1 and 3) put the best dev Sharpe (0.98) below chance, so the "no demonstrated edge" conclusion
holds. It should rest on clustering by return correlation (ONC or hierarchical) with the variance taken across clusters, not on the
raw count. `holdout.trials.effective_number_of_trials` and `purgedcv.effective_n_trials` can cross-check that.

---

## 5. Existing gates vs the requested Phase 2 gates

| Requested gate | What exists | Gap |
|---|---|---|
| A. DSR ≥ 0.95, PBO ≤ 0.20, PSR, min backtest length | DSR/PSR functions; DSR used only after OOS | Gate, effective N, PBO, MinBTL, CPCV all missing |
| B. Plateau score ≥ 0.60, not on grid edge | Edge check; all ±1-step 1D neighbours must pass PF ≥ 1.05, Sharpe ≥ 0.30 | No ±10/20/30% perturbation, no 2D, no fractional score |
| C. Regimes | 4 rule quarters + quarter table in the brief | No regime labels or per-regime stats |
| D. Bootstrap | Trade-return percentiles (descriptive) | No resampling |
| E. Execution stress, fail at 2× slip or 2× commission | Slippage 0–2× reported, not gated | Commission, funding and gap scenarios; gate |
| F. Look-ahead audit | Splice test (26/37), `Back(-1)` probe, engine timing tests | 11 strategies uncovered; no settlement-time or rolling-window assertions per indicator |
| G. MinTRL, ≥ 50 trades/yr | Trade floor is enforced | MinTRL missing |
| Holdout gate + code hash | `optimize`/`walkforward` refuse OOS; hunt reaches OOS only after kill/plateau/folds; log has timestamp + config | No requirement for a validation report; no code/data hash; `--unlock-oos` bypass |

---

## 6. Decisions needed before Phase 2

These are places where the pasted spec conflicts with the project's rules or is ambiguous. Each has a recommendation.

| # | Issue | Recommendation |
|---|---|---|
| D1 | **Where validation lives.** The engine is C#; the spec names Python libraries | Keep the single C# engine. Add an `export-returns` command (per-config hourly mark-to-market returns + trades, dev only) and build the validation layer in Python on top of it. A second engine in Python would be a second source of truth |
| D2 | **Funding stress 2024-03 is inside the holdout.** Replaying it reads holdout data | Use the dev spikes (2021-05, 2022-06, 2022-11) plus a synthetic 3× scenario, and leave 2024-03 out |
| D3 | **Do perturbation-grid runs count as trials?** ±10/20/30% in 1D + 2D adds ~50–150 new dev evaluations per strategy | Keep them in a separate robustness registry, out of the DSR count: they can only reject a candidate, never choose its parameters. Counting them would inflate N for every later candidate |
| D4 | **Bootstrap units.** "Trade returns" with "block length 24–72 bars" mixes units | Stationary block bootstrap of hourly mark-to-market returns (mean block 24–72 h) for Sharpe and DD; block bootstrap of the trade sequence for PF |
| D5 | **Regimes overlap** (bull/bear/high-vol/low-vol/trending/ranging) | Three 2-way partitions, each computed causally on dev only: trend (sign of 200-day SMA slope), volatility (30-day realized vol vs its trailing 75th percentile), efficiency (Kaufman ER above/below its dev median). Apply the "loses in two regimes" rule within each partition |
| D6 | **CPCV for fixed-rule strategies.** Without a fit step, CPCV only slices one fixed path | Use CPCV paths for per-path metrics, and run PBO via CSCV over the selection set: each grid (FCF 150, FFW 100, EMA 25) and the 36-hypothesis "which idea" choice. Refitting per CPCV split would multiply evaluations past the 200-per-strategy cap |
| D7 | **Gate strictness.** DSR ≥ 0.95 over 3 years at the current N needs a dev Sharpe of roughly 2 | Accept it and say so: the expected number of survivors is zero, which is consistent with the evidence |
| D8 | **Phase 4 downloader** duplicates `fetch`, opens a second network path and would invalidate the STEP 0 hashes | Convert the audited CSVs to Parquet with no network, with a manifest that keeps the source SHA-256. Keep `data/download.py` as an optional refresh that is excluded from the no-network check, and don't run it unless you approve |
| D9 | **rank_bm25 is not installed** (pip needs network) | Implement BM25 in-house (~40 lines, Okapi k1 = 1.5, b = 0.75) and avoid a new dependency, or approve one `pip install rank_bm25` |
| D10 | **Third-party maturity.** purgedcv 0.1.11 is alpha; holdout-backtest 0.1.0 has a single author | I scanned both: no network, subprocess, eval or pickle imports. Use them as cross-checks against our own formula implementation (C# `DeflatedSharpe` + a Python reference), and flag disagreements above 1e-6 |
| D11 | **Holdout status.** Spent for the funding family, seen for the EMA family, known to LLMs and to you | Record it as such. Treat data after 2026-10-26 (forward paper data) as the true final holdout, and keep 2024–2026 as a secondary check |

---

## 7. Prioritized fix list

**P0: before any new validation number is trusted**
1. Extend the splice look-ahead test to iterate `StrategyRegistry.All` (closes the 11-strategy gap). Add assertions for funding settlement time and for every `FeatureCache` series (value *i* unchanged when bars after *i* change).
2. Python OOS lock: one data loader that refuses rows ≥ `devTo` unless the gated holdout entry point calls it. Add a Python no-network tripwire, mirroring `NoNetworkTests`.
3. C# `export-returns`: per-config dev returns and trades for every registered trial (261 distinct), deduplicated, so effective N, CSCV and the bootstrap have a returns matrix.
4. Holdout gate: refuse unless `validation_report.json` for the exact (strategy, version, params, code fingerprint, data hash) shows every hard gate passed. Log the timestamp, git HEAD, dirty-tree hash, Backtester.Core MVID and data SHA-256. Route `backtest --unlock-oos` through the same gate.

**P1: Phase 2 modules, in dependency order**

5. Multiple testing (A, G): PSR, DSR at effective N (with the raw-N value shown alongside), MinTRL, minimum backtest length, PBO via CSCV. Use our own implementation, cross-checked against purgedcv and holdout.
6. Execution stress (E): slippage 0–4×, commission 1–3×, dev funding spikes + 3× funding, gap through the stop by 10/15/20%. Gate at 2×.
7. Parameter robustness (B): 1D ±10/20/30% (adjacent values for integers) + 2D pairs, plateau score, edge flag; separate robustness registry (D3).
8. Regime stratification (C), with the D5 partitions.
9. Bootstrap (D), with the D4 units.
10. CPCV with purge (max holding period) and embargo, per-path metrics (A).
11. `validation_report.json` schema + one command that runs export → validation → report for the 17 profitable strategies.

**P2: later or optional**

12. Confirm the commission tier (C1); if it's 0.055%, change `config/hunt.json` (this changes every stored result).
13. Close window-end positions at the last in-window close (L8). Bump `BacktestEngine.Version`.
14. Intrabar max DD from bar highs/lows (C7).
15. Phase 3 (memory DB, BM25, bull/bear reports, attribution), Phase 4 (Parquet/DuckDB), Phase 5 (7 new xlsx sheets).

**Expected outcome of the re-evaluation (acceptance criterion 4):** most likely 0 of the 17 profitable strategies pass the hard gates.
The best dev Sharpe (0.98) is below every consistent chance threshold, and DSR ≥ 0.95 needs roughly double that.
