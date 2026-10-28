"""Combinatorial Purged Cross-Validation (CPCV) on daily strategy returns.

Source: López de Prado, M. (2018). Advances in Financial Machine Learning. Wiley, ch. 7 (purging and embargo) and
ch. 12 (CPCV). Conventions match `purgedcv.CombinatorialPurgedCV` (tests compare the splits):
  * the T rows are cut into N contiguous groups (the first T mod N groups get one extra row);
  * each of the C(N, k) combinations of k groups is one split's test set;
  * purge: row i's "label" is the half-open window [i, i + H) of days a position opened on day i can still be held,
    H = ceil(max holding period / 24 h) + 1. A training row is dropped when its window overlaps a test row's window,
    i.e. a - H < i < b + H around every contiguous test block [a, b];
  * embargo: the E rows right after every contiguous test block are dropped too (E = floor(T x embargoFraction));
  * paths: group g's k-th appearance as a test group (in split order) goes to path k, giving C(N-1, k-1) complete
    out-of-sample paths.
A fixed-rule strategy has nothing to fit, so in each split the configuration with the best Sharpe on the purged
training rows is selected from the candidate's selection set (its registered grid, else its parameter neighbourhood),
and its returns on the test groups form the paths. Per-path Sharpe, CAGR, max drawdown, Calmar and profit factor
(trades of the selected configuration entered on the path's test days) are reported.
"""

from __future__ import annotations

from dataclasses import dataclass
from itertools import combinations

import numpy as np
import pandas as pd

from .stats import DAYS_PER_YEAR, equity_stats, profit_factor, sharpe


@dataclass(frozen=True)
class Split:
    test_groups: tuple[int, ...]
    train: np.ndarray
    test: np.ndarray


def groups(n_rows: int, n_groups: int) -> list[np.ndarray]:
    size, extra = divmod(n_rows, n_groups)
    out, cursor = [], 0
    for k in range(n_groups):
        width = size + (1 if k < extra else 0)
        out.append(np.arange(cursor, cursor + width))
        cursor += width
    return out


def contiguous_blocks(sorted_idx: np.ndarray) -> list[tuple[int, int]]:
    if sorted_idx.size == 0:
        return []
    breaks = np.flatnonzero(np.diff(sorted_idx) != 1)
    starts = np.concatenate([[sorted_idx[0]], sorted_idx[breaks + 1]])
    ends = np.concatenate([sorted_idx[breaks], [sorted_idx[-1]]])
    return list(zip(starts.tolist(), ends.tolist()))


def splits(n_rows: int, n_groups: int, n_test: int, horizon: int, embargo: int) -> list[Split]:
    if not 1 <= n_test < n_groups <= n_rows:
        raise ValueError("need 1 <= test groups < groups <= rows")
    g = groups(n_rows, n_groups)
    out = []
    for combo in combinations(range(n_groups), n_test):
        test = np.concatenate([g[i] for i in combo])
        keep = np.ones(n_rows, dtype=bool)
        keep[test] = False
        for a, b in contiguous_blocks(np.sort(test)):
            keep[max(0, a - horizon + 1): min(n_rows, b + horizon)] = False   # purge: a - H < i < b + H
            keep[b + 1: min(n_rows, b + 1 + embargo)] = False                 # embargo after the block
        out.append(Split(combo, np.flatnonzero(keep), np.sort(test)))
    return out


def path_assignment(n_groups: int, n_test: int) -> np.ndarray:
    """assignment[p, g] = index of the split whose test set supplies group g to path p."""
    combos = list(combinations(range(n_groups), n_test))
    n_paths = len(list(combinations(range(n_groups - 1), n_test - 1)))
    seen = np.zeros(n_groups, dtype=int)
    out = np.full((n_paths, n_groups), -1, dtype=int)
    for s, combo in enumerate(combos):
        for g in combo:
            out[seen[g], g] = s
            seen[g] += 1
    return out


def run(daily: pd.DataFrame, trades: pd.DataFrame, n_groups: int, n_test: int, horizon: int, embargo: int) -> dict:
    """`daily`: day_ms + one column of daily returns per configuration (c000 = the candidate); `trades`: config, entry_ms, net_pnl."""
    days = daily["day_ms"].to_numpy(dtype=np.int64)
    configs = [c for c in daily.columns if c != "day_ms"]
    x = daily[configs].to_numpy(dtype=float)
    n_rows = x.shape[0]
    sp = splits(n_rows, n_groups, n_test, horizon, embargo)
    g = groups(n_rows, n_groups)
    chosen = [int(np.argmax([sharpe(x[s.train, j]) for j in range(x.shape[1])])) for s in sp]
    entry_day = np.searchsorted(days, (trades["entry_ms"].to_numpy(dtype=np.int64) // 86_400_000) * 86_400_000)

    paths = []
    for p, row in enumerate(path_assignment(n_groups, n_test)):
        returns, pnl = [], []
        for grp, s in enumerate(row):
            j = chosen[s]
            returns.append(x[g[grp], j])
            in_group = (trades["config"].to_numpy() == configs[j]) & np.isin(entry_day, g[grp])
            pnl.append(trades["net_pnl"].to_numpy(dtype=float)[in_group])
        st = equity_stats(np.concatenate(returns), DAYS_PER_YEAR)
        st["profitFactor"] = profit_factor(np.concatenate(pnl))
        st["configsUsed"] = [configs[chosen[s]] for s in row]
        paths.append({"path": p, **st})

    fixed = []
    for grp, rows in enumerate(g):
        st = equity_stats(x[rows, 0], DAYS_PER_YEAR)
        st["profitFactor"] = profit_factor(trades["net_pnl"].to_numpy(dtype=float)[(trades["config"].to_numpy() == configs[0]) & np.isin(entry_day, rows)])
        fixed.append({"group": grp, "from": int(days[rows[0]]), "to": int(days[rows[-1]]), **st})

    path_sharpes = np.asarray([q["sharpe"] for q in paths])
    return {
        "nGroups": n_groups,
        "nTestGroups": n_test,
        "nSplits": len(sp),
        "nPaths": len(paths),
        "purgeHorizonDays": horizon,
        "embargoDays": embargo,
        "selectionSetSize": len(configs),
        "candidateSelectedInSplits": int(sum(1 for c in chosen if c == 0)),
        "paths": paths,
        "pathSharpe": {
            "min": float(path_sharpes.min()),
            "median": float(np.median(path_sharpes)),
            "max": float(path_sharpes.max()),
            "fractionPositive": float(np.mean(path_sharpes > 0)),
        },
        "candidateByGroup": fixed,
    }
