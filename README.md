# Trading bot: local backtester and validation harness

A local research stack for BTCUSDT perpetual strategies on Bybit 1h data:

- **C# engine** (`src/Backtester.Core`, `src/Backtester.Cli`, .NET 10): backtests, the hunt and the research loop, plus
  `validate-export`.
- **Python validation layer** (`validation/`, run from the git-ignored `.venv`): turns each candidate into a
  `validation_report.json` with a pass/fail verdict.

Nothing in the backtest or validation path makes a network call or calls a language model. The only network code is the
manual `fetch` command, which downloads Bybit public data.

Phase 1 findings and the fix list are in [AUDIT.md](AUDIT.md). The research record is `hunt_log.json` (every cycle and
its verdicts), `artifacts/hunt/` (cycle reports, research notes and re-runs) and the validation summaries below.

## Quick start

```bash
dotnet test TradingBot.slnx
```

```bash
.venv/Scripts/python.exe -m unittest discover -s validation/tests -t .
```

```bash
.venv/Scripts/python.exe -m validation run
```

The last command is the whole pipeline in one go. It builds the CLI (Release, no restore) and exports the development
window: every hunt hypothesis with a profitable dev baseline, plus every registered trial. Then it analyses each candidate
and writes:

- `artifacts/validation/<strategy>/<id>/validation_report.json`: one per candidate, every gate with its value, threshold
  and verdict.
- `artifacts/validation/summary.md` and `summary.json`: one row per candidate.
- `artifacts/validation/trials/analysis.json`: effective number of trials and the PBO of the whole search.

Options:

- `--strategies a,b --param k=v`: one candidate with specific parameters.
- `--all`: every registered strategy at its defaults.
- `--skip-build`, `--skip-export`: reuse what is already built or exported.
- `--workers N`: number of analysis processes.

A full run takes about four minutes. The schema is
[validation/schema/validation_report.schema.json](validation/schema/validation_report.schema.json). For a worked example,
see the report for `funding-fade-wide`, the only strategy that ever reached the holdout:
`artifacts/validation/funding-fade-wide/<id>/validation_report.json`. Bundle directories are named after the
first 12 hex digits of the candidate identity, so they change whenever the engine build or the data changes.

## Validation gates

Every gate is computed on the development window (2021-01-01 to 2024-01-01) only. **All 13 hard gates must pass before
a candidate may run on the locked holdout.** Thresholds live in the `validation.gates` section of
[config/hunt.json](config/hunt.json).

| Gate | Rejects when | What it guards against | Source |
|---|---|---|---|
| `lookahead_audit` | the audit is not `pass` | signals, indicator series or funding settlement that use data after the decision bar | engine test, below |
| `deflated_sharpe` | DSR < 0.95 | a Sharpe ratio no better than the best of N lucky trials | Bailey & López de Prado (2014) |
| `pbo` | PBO > 0.20 | a search whose in-sample winner usually lands in the bottom half out of sample | Bailey, Borwein, López de Prado & Zhu (2017) |
| `plateau_score` | < 60% of neighbours stay profitable with PF > 1.05 and Sharpe > 0.30 | a parameter spike rather than a plateau | — |
| `plateau_edge` | a chosen value sits on the edge of its search grid | an optimum that may lie outside the range searched | — |
| `regime_losses` | the strategy loses money in ≥ 2 regimes that each hold ≥ 20 trades | regime dependence | — |
| `regime_concentration` | one regime earns all the profit with < 30% of the trades | a few lucky trades in one regime | — |
| `bootstrap_sharpe_p05` | 5th percentile of bootstrapped Sharpe < 0 | a Sharpe ratio that sampling noise alone explains | Politis & Romano (1994) |
| `bootstrap_pf_p05` | 5th percentile of bootstrapped PF < 1.0 | a profit factor that depends on a few trades | Politis & Romano (1994) |
| `stress_slippage_2x` | not profitable at 2× slippage | an edge that execution costs consume | — |
| `stress_commission_2x` | not profitable at 2× commission | an edge that fees consume (2× ≈ 0.08% per side) | — |
| `min_track_record` | dev days < MinTRL at 95% | too little data for the observed Sharpe to be significant | Bailey & López de Prado (2012) |
| `trades_per_year` | < 50 trades per year | too few independent bets | hunt rule |

### How each number is computed

**Look-ahead audit.** The audit runs on synthetic data, never the research data. Each candidate is re-run on markets that
are identical up to a cutoff bar and different after it:

- another seed's price path, and the same path mirrored, so every later move reverses;
- 3× volume after the cutoff;
- funding of the opposite sign at 5× the size.

Two seeds × two cutoffs give 8 comparisons. Three things must not change before the cutoff:

- the signal at every bar;
- every `FeatureCache` indicator series;
- every trade.

Each trade's funding must also equal the settlement rule recomputed independently. A strategy that reads one bar ahead
fails this test, and `ValidationTests` checks that it does. Code:
[LookAheadAudit.cs](src/Backtester.Core/Validation/LookAheadAudit.cs).

**DSR.**

- `DSR = PSR(SR*)`, where `PSR(SR*) = Φ((SR − SR*)·√(n−1) / √(1 − γ3·SR + (γ4−1)/4·SR²))`.
- `SR*` is the expected maximum Sharpe of N zero-skill trials:
  `√V · ((1−γ)Φ⁻¹(1−1/N) + γΦ⁻¹(1−1/(N·e)))`, where γ (in this formula only) is the Euler–Mascheroni constant.
- SR, γ3 (skewness) and γ4 (raw kurtosis) are those of the candidate's daily returns.

N and V come in two versions:

- **Raw**: every registered parameter set, as the hunt counts them (312 after cycle 12).
- **Effective**: Li & Ji's eigenvalue count on the correlation matrix of the distinct trial return series (N = 67 for 263
  series after cycle 12). V is then the variance of the Sharpe ratios of that many clusters (average linkage, inverse-variance cluster returns, as in
  López de Prado & Lewis 2019).

The gate uses whichever benchmark is stricter. After the 2026-10-28 grid re-run (2,084 trials) that is the raw one: 1.86
annualized, against 1.62 effective (N = 300). Every new trial moves both, so the bar rises as more ideas are tried.

The ONC clustering was tried and dropped. Its base step finds only 2 clusters in this matrix: the funding family and
everything else. That wrongly treats ~35 nearly uncorrelated strategies as one trial.

**PBO.** CSCV with 16 blocks (12,870 splits). PBO is a property of the selection procedure, not of one strategy. Every
hunt candidate is picked from all registered trials, so PBO is computed over all their distinct return series (263 after
cycle 12), and the gate
applies to every candidate. A local PBO over each candidate's own grid or neighbourhood is reported but not gated. Among
near-identical neighbours, the in-sample winner's out-of-sample rank is a coin toss even on a genuine plateau, so a
local PBO says little.

**CPCV** (reported, not gated):

- 6 groups, 2 test groups: 15 splits and 5 paths.
- Purge: training days whose holding window overlaps a test day are removed. H = ⌈max bars held / 24⌉ + 1 days.
- Embargo: 1% of the sample after each test block.
- In each split, the configuration with the best Sharpe on the purged training days is chosen from the candidate's
  selection set. That set is its registered grid if it has one, otherwise its perturbation neighbourhood.
- Reported per path: Sharpe, CAGR, max drawdown, Calmar and profit factor.
- The splits match `purgedcv.CombinatorialPurgedCV` index for index (`test_cpcv.py`).

**Plateau.**

- Continuous parameters move by ±10/20/30%, one at a time (1D) and in every pair (2D).
- Small integers and zero move to their adjacent grid values.
- These runs go to `artifacts/validation/robustness_registry.jsonl`, not to the hunt's trial registry. They can only
  reject a candidate, never choose its parameters (AUDIT.md D3).

**Regimes.** Each label is computed from BTC daily closes up to the previous day:

- trend: bull when the 20-day change of the 200-day SMA is positive, else bear;
- volatility: high-vol when 30-day realized vol is above the 75th percentile of its trailing year, else low-vol;
- efficiency: trending when the 30-day Kaufman efficiency ratio is above its trailing-year median, else ranging.

Trades are assigned to the regime of their entry day. The losing-regimes rule counts across all six regimes. AUDIT.md
D5 suggested applying it within each two-way partition, but a profitable strategy cannot lose in both halves of one
partition, so that version could never fire.

**Bootstrap.** Stationary block bootstrap, 5,000 paths for each mean block length of 24, 48 and 72 hours.

- Sharpe and max drawdown come from resampling the hourly mark-to-market returns.
- Profit factor comes from resampling the trade sequence, with a block length covering the same span of time.
- The gate takes the worst 5th percentile across the three block lengths.

**Stress.** The engine re-runs the candidate on dev with:

- slippage at 0–4× base and commission at 1–3× base;
- funding off, and funding at 3×;
- a constant adverse funding rate equal to the mean |rate| of each dev spike month (2021-05, 2022-06, 2022-11). 2024-03
  lies in the holdout and is not used (AUDIT.md D2).

A gap-through-stop scenario is also reported (not gated): the stop fills 10/15/20% beyond its price. Its loss is
1% + exposure × gap, at the largest exposure the strategy ever carried.

**MinTRL.** `1 + (1 − γ3·SR + (γ4−1)/4·SR²)·(Φ⁻¹(0.95)/SR)²` daily observations. The minimum backtest length (Bailey,
Borwein, López de Prado & Zhu 2014) is reported alongside.

**Cross-checks.**

- PSR, DSR, MinTRL and PBO are recomputed with `holdout-backtest` and must agree to 1e-12.
- `purgedcv` uses a ddof-0 Sharpe and bias-corrected moments, so its PSR differs by about 1e-4. That difference is
  reported, not required to vanish.

### Reading a report

- `verdict` is `PASS` only when every hard gate passed. `failedHardGates` lists the rest.
- Each `gates[]` entry has the measured `value`, the `threshold`, the comparator and a one-line `detail`.
- `multipleTesting` shows both trial counts. When `dsrRaw` and `dsrEffective` disagree a lot, the trial count matters
  more than the Sharpe ratio. Compare `expectedMaxSharpe*.annual` with the candidate's `annualSharpe`.
- `cpcv.paths`: if every path is positive but DSR fails, the strategy is consistent in time but not distinguishable from
  the best of many lucky trials.
- `attribution` splits net PnL into gross price PnL, slippage, commission and funding.
- `stress.ladder` shows how fast the edge decays with costs.

### The holdout gate

The locked holdout window (2024-01-01 to 2026-10-26) runs only through two paths: hunt step 7, and
`backtest --unlock-oos`. Both need a validation report whose identity matches exactly:

- strategy name, version and parameters;
- costs, sizing and the development window;
- the Backtester.Core build;
- the data hash.

The report must also come from the current validator code and `config/hunt.json`, and every hard gate in it must have
passed.

- Without such a report, the hunt pauses with the command to run.
- With a failing report, the candidate is rejected at stage `validation-gate`.
- Every allowed run is appended to `artifacts/oos_access.log` with a timestamp and the code state: the Backtester.Core
  build, a hash of all C#/Python/config sources and the git HEAD. The log also records the data hashes and the report
  hash.

`validation.holdoutGate: false` in config switches the gate off. The integration tests use that switch; the access log
records it.

## Current result

After the 2026-10-28 full-grid run every registered hypothesis has been searched over its whole grid on 1h bars, and the
interval screen ran every strategy at its defaults on 4h, 30m and 15m bars: 5,883 parameter sets are registered. The
chance threshold is an annualized Sharpe of 2.88 (raw count) / 2.87 (effective, N = 639); the search PBO is 0.50.

- **0 of 18** hunt hypotheses that made money on dev pass every hard gate at their defaults.
- **0 of 38** best 1h grid points pass, and none passes the hunt rules (every optimum sits on a grid edge or has no
  plateau). The closest is impulse-bar-continuation: dev Sharpe 1.78 and PF 1.55, but on the edge of all four grid
  ranges; it fails DSR (0.02), PBO and plateau_edge.
- **Other intervals:** of 118 default runs (2 period strategies cannot run on 4h bars) only funding-crowding-fade on 30m
  bars survives the baseline kill rules; its grid optimum (Sharpe 1.09) has no plateau and fails DSR and PBO.

See `artifacts/validation/summary.md` (defaults), `artifacts/hunt/grid_rerun_2026-10-28/` (the 16 profitable
hypotheses), `artifacts/hunt/grid_rerun_all_2026-10-28/` (the other 22, and all 38 best points re-validated) and
`artifacts/hunt/interval_screen_2026-10-28/` (4h/30m/15m).

### Other bar intervals

Every command takes `--interval M` (minutes that divide a day) and reads `data/bybit/<symbol>_<M>.csv`; fetch other
intervals with `fetch --interval M --no-funding` so the funding file stays as it is. Parameters counted in bars stay bar
counts; days, UTC hours, N-hour periods and funding stay clock time. Registered trials record their interval and replay
on their own data.
