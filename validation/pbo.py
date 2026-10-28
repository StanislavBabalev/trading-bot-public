"""Probability of Backtest Overfitting by Combinatorially Symmetric Cross-Validation (CSCV).

Source: Bailey, D. H., Borwein, J. M., López de Prado, M. & Zhu, Q. J. (2017). The Probability of Backtest Overfitting.
Journal of Computational Finance 20(4), 39-69.

Given a T x N matrix of returns of N configurations that a selection process chose among:
  1. cut the T rows into S contiguous blocks (S even; the first T mod S blocks get one extra row);
  2. for each of the C(S, S/2) ways to pick S/2 blocks as in-sample (IS), the rest out-of-sample (OOS):
     select n* = argmax of IS Sharpe, find the relative rank w = rank(OOS Sharpe of n*) / (N + 1) among all N OOS Sharpe
     ratios (rank 1 = worst, ties share their average rank) and the logit lambda = ln(w / (1 - w));
  3. PBO = fraction of combinations with lambda <= 0 (the IS winner lands at or below the OOS median).
Also reported: the probability that the IS winner loses OOS, and the slope of OOS on IS Sharpe of the winners
(performance degradation). A configuration that is flat on part of the sample has Sharpe 0 there.
"""

from __future__ import annotations

from itertools import combinations

import numpy as np


def block_bounds(rows: int, n_blocks: int) -> np.ndarray:
    sizes = np.full(n_blocks, rows // n_blocks, dtype=np.int64)
    sizes[: rows % n_blocks] += 1
    return np.concatenate([[0], np.cumsum(sizes)])


def cscv(returns: np.ndarray, n_blocks: int = 16) -> dict:
    x = np.asarray(returns, dtype=float)
    rows, n = x.shape
    if n < 2:
        raise ValueError("CSCV needs at least two configurations")
    if n_blocks % 2 or rows < 2 * n_blocks:
        raise ValueError(f"{rows} rows cannot fill {n_blocks} (even) blocks of two rows each")

    # Sums of centred returns per block, so every partition's Sharpe ratio is a matrix product (as in `holdout`).
    centre = x.mean(axis=0)
    c = x - centre
    bounds = block_bounds(rows, n_blocks)
    sums = np.add.reduceat(c, bounds[:-1], axis=0)
    squares = np.add.reduceat(c * c, bounds[:-1], axis=0)
    counts = np.diff(bounds).astype(float)
    partitions = list(combinations(range(n_blocks), n_blocks // 2))
    member = np.zeros((len(partitions), n_blocks))
    for i, chosen in enumerate(partitions):
        member[i, list(chosen)] = 1.0

    def sharpe(mask: np.ndarray) -> np.ndarray:
        cnt = (mask @ counts)[:, None]
        shifted = (mask @ sums) / cnt
        var = ((mask @ squares) - cnt * shifted * shifted) / (cnt - 1.0)
        sd = np.sqrt(np.maximum(var, 0.0))
        scale = np.sqrt(np.maximum((mask @ squares) / cnt, np.finfo(float).tiny))
        return np.where(sd > 1e-7 * scale, (shifted + centre) / np.where(sd > 0, sd, 1.0), 0.0)

    r_in, r_out = sharpe(member), sharpe(1.0 - member)
    idx = np.arange(len(partitions))
    selected = np.argmax(r_in, axis=1)
    winner_out = r_out[idx, selected]
    below = np.sum(r_out < winner_out[:, None], axis=1)
    ties = np.sum(r_out == winner_out[:, None], axis=1)
    w = (below + (ties + 1.0) / 2.0) / (n + 1.0)
    logits = np.log(w / (1.0 - w))
    winner_in = r_in[idx, selected]
    slope, intercept = np.polyfit(winner_in, winner_out, 1) if np.ptp(winner_in) > 0 else (float("nan"), float("nan"))
    return {
        "pbo": float(np.mean(logits <= 0.0)),
        "nPartitions": len(partitions),
        "nConfigurations": int(n),
        "nBlocks": int(n_blocks),
        "rows": int(rows),
        "logitMean": float(logits.mean()),
        "logitQuantiles": {q: float(np.quantile(logits, float(q))) for q in ("0.05", "0.25", "0.5", "0.75", "0.95")},
        "probabilityOosLoss": float(np.mean(winner_out < 0.0)),
        "degradation": {"slope": float(slope), "intercept": float(intercept)},
        "meanIsSharpeOfWinner": float(winner_in.mean()),
        "meanOosSharpeOfWinner": float(winner_out.mean()),
        "selectionFrequency": {int(k): int(v) for k, v in zip(*np.unique(selected, return_counts=True))},
    }
