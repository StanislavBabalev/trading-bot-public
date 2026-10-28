"""Stationary block bootstrap of the candidate's returns.

Source: Politis, D. N. & Romano, J. P. (1994). The Stationary Bootstrap. Journal of the American Statistical Association
89(428), 1303-1313. Blocks start at uniform random positions, have geometric lengths with mean L, and wrap around the
end of the sample, so the resampled series is stationary and keeps serial dependence up to about L observations.

Units (AUDIT.md D4): Sharpe ratio and max drawdown come from resampling the per-bar mark-to-market returns (hourly by
default) with mean block length L hours (24-72), i.e. L x 60 / bar minutes bars; profit factor comes from resampling the
sequence of trade returns (net PnL / equity at entry) with a mean block length covering the same span of time,
L bars x trades / bars (at least one trade).
"""

from __future__ import annotations

import math

import numpy as np

from .stats import DAYS_PER_YEAR, profit_factor

PERCENTILES = (5, 50, 95)


def stationary_indices(n: int, mean_block: float, n_paths: int, rng: np.random.Generator) -> np.ndarray:
    """(n_paths, n) resampling indices: position t continues its block (previous index + 1, wrapping) unless a new block
    starts there, which happens with probability 1 / mean_block (always at t = 0)."""
    restart = rng.random((n_paths, n)) < 1.0 / mean_block
    restart[:, 0] = True
    block = np.cumsum(restart, axis=1) - 1                      # block number of each position, per path
    blocks_per_path = block[:, -1] + 1
    first = np.concatenate([[0], np.cumsum(blocks_per_path)[:-1]])
    starts = rng.integers(0, n, size=int(blocks_per_path.sum()))   # one uniform start per block
    t = np.arange(n)
    block_start = np.maximum.accumulate(np.where(restart, t, 0), axis=1)
    return (starts[first[:, None] + block] + (t - block_start)) % n


def _path_stats(hourly: np.ndarray, hours_per_day: int) -> tuple[np.ndarray, np.ndarray]:
    """Annualized Sharpe of daily compounded returns, and max drawdown, for each row of resampled per-bar returns
    (hours_per_day = bars per day)."""
    paths, n = hourly.shape
    days = n // hours_per_day
    growth = 1.0 + hourly[:, : days * hours_per_day].reshape(paths, days, hours_per_day)
    daily = np.prod(growth, axis=2) - 1.0
    sd = daily.std(axis=1, ddof=1)
    sharpe = np.where(sd > 0, daily.mean(axis=1) / np.where(sd > 0, sd, 1.0), 0.0) * math.sqrt(DAYS_PER_YEAR)
    eq = np.cumprod(1.0 + hourly, axis=1)
    peak = np.maximum(np.maximum.accumulate(eq, axis=1), 1.0)
    return sharpe, np.max(1.0 - eq / peak, axis=1)


def run(hourly_returns: np.ndarray, trade_returns: np.ndarray, mean_blocks: list[float], n_paths: int, seed: int,
        chunk: int = 250, interval_minutes: int = 60) -> dict:
    """hourly_returns are per-bar returns of interval_minutes bars; mean_blocks are in hours."""
    hourly = np.asarray(hourly_returns, dtype=float)
    bars_per_hour = 60 / interval_minutes
    trades = np.asarray(trade_returns, dtype=float)
    out = {"paths": n_paths, "seed": seed, "byMeanBlockLength": []}
    for L in mean_blocks:
        rng = np.random.default_rng([seed, int(L)])
        sharpes, drawdowns = [], []
        for start in range(0, n_paths, chunk):
            m = min(chunk, n_paths - start)
            idx = stationary_indices(hourly.size, L * bars_per_hour, m, rng)
            s, d = _path_stats(hourly[idx], 1440 // interval_minutes)
            sharpes.append(s)
            drawdowns.append(d)
        sharpes, drawdowns = np.concatenate(sharpes), np.concatenate(drawdowns)

        trade_block = max(1.0, L * bars_per_hour * trades.size / hourly.size)
        pfs = np.full(n_paths, np.nan)
        if trades.size >= 2:
            idx = stationary_indices(trades.size, trade_block, n_paths, rng)
            sample = trades[idx]
            wins = np.where(sample > 0, sample, 0.0).sum(axis=1)
            losses = -np.where(sample < 0, sample, 0.0).sum(axis=1)
            pfs = np.where(losses > 0, wins / np.where(losses > 0, losses, 1.0), np.inf)
        out["byMeanBlockLength"].append({
            "meanBlockHours": L,
            "meanBlockTrades": trade_block,
            "sharpe": {f"p{q:02d}": float(np.percentile(sharpes, q)) for q in PERCENTILES},
            "profitFactor": {f"p{q:02d}": float(np.percentile(pfs, q)) for q in PERCENTILES},
            "maxDrawdown": {f"p{q:02d}": float(np.percentile(drawdowns, q)) for q in PERCENTILES},
            "fractionSharpeNegative": float(np.mean(sharpes < 0)),
        })
    out["observedProfitFactor"] = profit_factor(trades)
    worst = min(out["byMeanBlockLength"], key=lambda r: r["sharpe"]["p05"])
    out["worstSharpeP05"] = worst["sharpe"]["p05"]
    out["worstProfitFactorP05"] = min(r["profitFactor"]["p05"] for r in out["byMeanBlockLength"])
    return out
