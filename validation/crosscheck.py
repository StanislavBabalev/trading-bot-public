"""Independent checks of our formulas against two third-party libraries (AUDIT.md D10).

`holdout` (holdout-backtest 0.1.0) uses the same conventions as ours (sample SD with ddof 1 in the Sharpe ratio, population
skewness, raw kurtosis), so PSR, DSR and MinTRL must agree to rounding. `purgedcv` (0.1.11) computes the Sharpe ratio with
ddof 0 and bias-corrected skewness/kurtosis from the returns themselves, so its PSR differs slightly by construction; the
difference is reported, not required to vanish. PBO is compared on the same return matrix and block count.
"""

from __future__ import annotations

import numpy as np

TOLERANCE = 1e-6


def _row(own: float, other: float, exact: bool, note: str = "") -> dict:
    diff = abs(own - other) if np.isfinite(own) and np.isfinite(other) else (0.0 if own == other else float("inf"))
    return {"own": own, "library": other, "absDiff": diff, "agree": bool(diff <= TOLERANCE) if exact else None, "note": note}


def statistics(sr: float, n: int, skew: float, kurt: float, n_eff: int, var_eff: float, daily_returns: np.ndarray,
               own_psr: float, own_dsr: float, own_mintrl: float, confidence: float) -> dict:
    out: dict = {}
    try:
        from holdout.deflated import deflated_sharpe_ratio, minimum_track_record_length, probabilistic_sharpe_ratio

        out["holdout"] = {
            "psr": _row(own_psr, probabilistic_sharpe_ratio(sr, n, skewness=skew, kurtosis=kurt), True),
            "dsr": _row(own_dsr, deflated_sharpe_ratio(sr, n, n_trials=max(1, n_eff), trials_variance=var_eff, skewness=skew, kurtosis=kurt), True),
            "minTrackRecord": _row(own_mintrl, minimum_track_record_length(sr, skewness=skew, kurtosis=kurt, confidence=confidence), True)
            if sr > 0 else {"note": "undefined: Sharpe ratio not above zero"},
        }
    except Exception as e:  # noqa: BLE001 - a cross-check must never stop the run
        out["holdout"] = {"error": f"{type(e).__name__}: {e}"}
    try:
        from purgedcv import probabilistic_sharpe_ratio as p_psr

        out["purgedcv"] = {"psr": _row(own_psr, float(p_psr(np.asarray(daily_returns, dtype=float), 0.0)), False,
                                       "ddof 0 Sharpe and bias-corrected moments by construction")}
    except Exception as e:  # noqa: BLE001
        out["purgedcv"] = {"error": f"{type(e).__name__}: {e}"}
    return out


def pbo(matrix: np.ndarray, n_blocks: int, own: float) -> dict:
    out: dict = {}
    try:
        from holdout.pbo import probability_of_backtest_overfitting as h_pbo

        out["holdout"] = _row(own, float(h_pbo(matrix, n_blocks=n_blocks).pbo), True)
    except Exception as e:  # noqa: BLE001
        out["holdout"] = {"error": f"{type(e).__name__}: {e}"}
    # purgedcv scores every combination in Python (minutes at 16 blocks and hundreds of configurations), so it is
    # compared at 8 blocks (70 combinations) against our CSCV at 8 blocks.
    try:
        from purgedcv import probability_of_backtest_overfitting as p_pbo

        from .pbo import cscv

        out["purgedcv"] = _row(cscv(matrix, 8)["pbo"], float(p_pbo(np.ascontiguousarray(matrix.T), 8).pbo), False,
                               "both at 8 blocks; purgedcv's own block and tie conventions, small differences possible")
    except Exception as e:  # noqa: BLE001
        out["purgedcv"] = {"error": f"{type(e).__name__}: {e}"}
    return out
