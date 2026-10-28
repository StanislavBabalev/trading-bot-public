"""Effective number of trials for the Deflated Sharpe Ratio.

The hunt's registry counts every parameter set (309), but most are near-copies: 250 come from one funding-crowding
family and 49 return series are exact duplicates. Counting correlated trials as independent misstates the selection bias
in both directions (more trials, but a smaller cross-trial variance). Here:

  N_eff  Li & Ji's eigenvalue count on the correlation matrix of the distinct trials' daily returns:
         N_eff = sum over eigenvalues l of [l >= 1] + (l - floor(l)). Parameter-free and deterministic.
  V_eff  the trials are grouped into N_eff clusters (average-linkage hierarchical clustering on the distance
         sqrt((1 - rho) / 2)); each cluster becomes one inverse-variance-weighted return series, and V_eff is the variance
         of those cluster Sharpe ratios, as López de Prado & Lewis do with their clusters.
The DSR gate uses whichever of (raw count, raw variance) and (N_eff, V_eff) gives the higher expected maximum Sharpe, so
it is never weaker than the hunt's own correction.

Why not ONC: its base step (k-means + silhouette t-stat, López de Prado 2020 §4.4.1) picks k = 2 on this matrix: one
cluster for the funding family and one for ~35 nearly uncorrelated strategies, which are not one trial. Li & Ji gives 64,
between the 37 strategies and the 260 distinct series.

Sources:
  Li, J. & Ji, L. (2005). Adjusting multiple testing in multilocus analyses using the eigenvalues of a correlation
    matrix. Heredity 95, 221-227.
  López de Prado, M. & Lewis, M. J. (2019). Detection of false investment strategies using unsupervised learning
    methods. Quantitative Finance 19(9), 1555-1565.
Cross-checks: `holdout.trials.effective_number_of_trials` (same eigenvalue method) and its participation-ratio count.
"""

from __future__ import annotations

import numpy as np
from scipy.cluster.hierarchy import fcluster, linkage
from scipy.spatial.distance import squareform

from .stats import sharpe


def drop_flat_columns(matrix: np.ndarray, names: list[str]) -> tuple[np.ndarray, list[str], list[str]]:
    sd = matrix.std(axis=0, ddof=1)
    keep = sd > 0
    return matrix[:, keep], [n for n, k in zip(names, keep) if k], [n for n, k in zip(names, keep) if not k]


def correlation(matrix: np.ndarray) -> np.ndarray:
    c = np.clip(np.corrcoef(matrix, rowvar=False), -1.0, 1.0)
    np.fill_diagonal(c, 1.0)
    return (c + c.T) / 2.0


def li_ji_count(corr: np.ndarray) -> float:
    """Li & Ji (2005): each eigenvalue >= 1 counts one, plus the fractional part of every eigenvalue."""
    ev = np.abs(np.linalg.eigvalsh(corr))
    rounded = np.round(ev)
    ev = np.where(np.abs(ev - rounded) < 1e-9, rounded, ev)   # 0.9999999999 is 1 in exact arithmetic
    return float(min(max(np.sum((ev >= 1.0) + (ev - np.floor(ev))), 1.0), corr.shape[0]))


def clusters(corr: np.ndarray, k: int) -> np.ndarray:
    """Labels 1..k from average-linkage clustering on sqrt((1 - rho) / 2)."""
    d = np.sqrt(np.clip((1.0 - corr) / 2.0, 0.0, None))
    np.fill_diagonal(d, 0.0)
    return fcluster(linkage(squareform(d, checks=False), "average"), t=k, criterion="maxclust")


def cluster_series(matrix: np.ndarray, labels: np.ndarray) -> list[np.ndarray]:
    """One return series per cluster: members weighted by inverse variance, weights summing to one."""
    out = []
    for k in np.unique(labels):
        cols = matrix[:, labels == k]
        w = 1.0 / cols.var(axis=0, ddof=1)
        out.append(cols @ (w / w.sum()))
    return out


def effective_trials(matrix: np.ndarray, names: list[str], registry_daily_sharpes) -> dict:
    """Raw and effective trial counts with the matching cross-trial Sharpe variances (daily, per observation)."""
    raw_sr = np.asarray([s for s in registry_daily_sharpes if np.isfinite(s)], dtype=float)
    x, kept, flat = drop_flat_columns(matrix, names)
    corr = correlation(x)
    n_eff = max(1, int(round(li_ji_count(corr))))
    labels = clusters(corr, n_eff)
    cluster_sr = np.asarray([sharpe(c) for c in cluster_series(x, labels)])
    distinct_sr = np.asarray([sharpe(x[:, j]) for j in range(x.shape[1])])

    alternatives: dict = {"distinctSeriesWithDistinctVariance": {"nTrials": int(x.shape[1]), "sharpeVariance": float(distinct_sr.var(ddof=1))}}
    try:
        from holdout.trials import effective_number_of_trials

        alternatives["holdoutEigenvalue"] = float(effective_number_of_trials(corr, method="eigenvalue"))
        alternatives["holdoutParticipation"] = float(effective_number_of_trials(corr, method="participation"))
    except Exception as e:  # noqa: BLE001 - a cross-check must never stop the run
        alternatives["error"] = f"{type(e).__name__}: {e}"

    return {
        "raw": {
            "nTrials": int(raw_sr.size),
            "sharpeVariance": float(raw_sr.var(ddof=1)) if raw_sr.size > 1 else 0.0,
            "note": "every registered parameter set, as the hunt counts them (duplicates included)",
        },
        "distinctReturnSeries": len(names),
        "flatSeriesDropped": flat,
        "effective": {
            "method": "Li & Ji eigenvalue count; variance of inverse-variance cluster Sharpe ratios over that many average-linkage clusters",
            "nTrials": n_eff,
            "liJiCount": li_ji_count(corr),
            "sharpeVariance": float(cluster_sr.var(ddof=1)) if n_eff > 1 else 0.0,
            "clusterSharpes": [float(s) for s in cluster_sr],
            "clusterMembers": {str(int(c)): [kept[i] for i in np.flatnonzero(labels == c)] for c in np.unique(labels)},
        },
        "medianOffDiagonalCorrelation": float(np.median(corr[np.triu_indices_from(corr, 1)])),
        "alternativeCounts": alternatives,
    }
