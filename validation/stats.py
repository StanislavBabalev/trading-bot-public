"""Sharpe-ratio statistics. All Sharpe ratios are per observation period (daily here) unless a name says "annual".

Sources:
  [BLdP12] Bailey, D. H. & López de Prado, M. (2012). The Sharpe Ratio Efficient Frontier. Journal of Risk 15(2), 3-44.
           Probabilistic Sharpe Ratio and Minimum Track Record Length.
  [BLdP14] Bailey, D. H. & López de Prado, M. (2014). The Deflated Sharpe Ratio: Correcting for Selection Bias, Backtest
           Overfitting and Non-Normality. Journal of Portfolio Management 40(5), 94-107.
  [BBLZ14] Bailey, D. H., Borwein, J. M., López de Prado, M. & Zhu, Q. J. (2014). Pseudo-Mathematics and Financial
           Charlatanism: The Effects of Backtest Overfitting on Out-of-Sample Performance. Notices of the AMS 61(5), 458-471.
           Minimum Backtest Length.
These are the same formulas as src/Backtester.Core/Analytics/DeflatedSharpe.cs; tests check both against `holdout`.
"""

from __future__ import annotations

import math

import numpy as np
from scipy.stats import norm

EULER_GAMMA = 0.5772156649015329
DAYS_PER_YEAR = 365  # crypto trades every day; annualized Sharpe = daily Sharpe x sqrt(365), as in the engine


def sharpe(returns) -> float:
    """Mean / sample standard deviation (ddof 1); 0 for a flat series."""
    r = np.asarray(returns, dtype=float)
    if r.size < 2:
        return float("nan")
    sd = r.std(ddof=1)
    return float(r.mean() / sd) if sd > 0 else 0.0


def moments(returns) -> tuple[float, float]:
    """Skewness g1 and kurtosis (not excess; 3 for a normal) from population moments, as Statistics.cs."""
    r = np.asarray(returns, dtype=float)
    d = r - r.mean()
    m2 = float(np.mean(d**2))
    if m2 == 0:
        return 0.0, 3.0
    return float(np.mean(d**3) / m2**1.5), float(np.mean(d**4) / m2**2)


def psr(sr: float, n: int, skew: float, kurt: float, benchmark: float = 0.0) -> float:
    """Probabilistic Sharpe Ratio [BLdP12]: P(true SR > benchmark) given an observed SR over n returns,

        PSR(SR*) = Phi( (SR - SR*) sqrt(n - 1) / sqrt(1 - g3 SR + (g4 - 1) / 4 SR^2) ),

    with g3 the skewness and g4 the (non-excess) kurtosis of the returns."""
    denom = 1.0 - skew * sr + (kurt - 1.0) / 4.0 * sr * sr
    if n < 2 or not denom > 0 or not math.isfinite(sr):
        return float("nan")
    return float(norm.cdf((sr - benchmark) * math.sqrt(n - 1) / math.sqrt(denom)))


def expected_max_z(n_trials: float) -> float:
    """E[max of N independent standard normals] ~ (1 - gamma) Phi^-1(1 - 1/N) + gamma Phi^-1(1 - 1/(N e)) [BLdP14, BBLZ14]."""
    if n_trials <= 1:
        return 0.0
    return float((1 - EULER_GAMMA) * norm.ppf(1 - 1 / n_trials) + EULER_GAMMA * norm.ppf(1 - 1 / (n_trials * math.e)))


def expected_max_sharpe(n_trials: float, variance: float) -> float:
    """Expected maximum SR of N zero-skill trials whose SRs vary with `variance` across trials [BLdP14]:
    SR0 = sqrt(V[SR]) * E[max Z_N]."""
    return math.sqrt(variance) * expected_max_z(n_trials) if variance > 0 else 0.0


def deflated_sharpe(sr: float, n: int, skew: float, kurt: float, n_trials: float, variance: float) -> float:
    """Deflated Sharpe Ratio [BLdP14]: PSR against the SR expected from the best of `n_trials` lucky trials."""
    return psr(sr, n, skew, kurt, expected_max_sharpe(n_trials, variance))


def min_track_record_length(sr: float, skew: float, kurt: float, benchmark: float = 0.0, confidence: float = 0.95) -> float:
    """Minimum Track Record Length [BLdP12], in observations: the n at which PSR(benchmark) reaches `confidence`,

        MinTRL = 1 + (1 - g3 SR + (g4 - 1) / 4 SR^2) (Phi^-1(confidence) / (SR - SR*))^2.

    Infinite when SR <= benchmark: no length of track record makes such a ratio significant."""
    if not sr > benchmark:
        return float("inf")
    z = norm.ppf(confidence)
    return float(1 + (1 - skew * sr + (kurt - 1) / 4 * sr * sr) * (z / (sr - benchmark)) ** 2)


def min_backtest_length_years(n_trials: float, sharpe_annual: float) -> float:
    """Minimum Backtest Length [BBLZ14], in years: with fewer years of data, the best of `n_trials` zero-skill
    configurations is expected to show an annualized SR of at least `sharpe_annual`,

        MinBTL ~ ( ((1 - gamma) Phi^-1(1 - 1/N) + gamma Phi^-1(1 - 1/(N e))) / SR_annual )^2."""
    if n_trials <= 1:
        return 0.0
    if not sharpe_annual > 0:
        return float("inf")
    return (expected_max_z(n_trials) / sharpe_annual) ** 2


def equity_stats(returns, periods_per_year: float) -> dict:
    """Compounded path statistics of a return series: annualized Sharpe, CAGR, max drawdown, Calmar, total return."""
    r = np.asarray(returns, dtype=float)
    if r.size == 0:
        return {"sharpe": float("nan"), "cagr": float("nan"), "maxDrawdown": float("nan"), "calmar": float("nan"), "totalReturn": float("nan")}
    eq = np.cumprod(1.0 + r)
    peak = np.maximum.accumulate(np.concatenate([[1.0], eq]))[1:]
    max_dd = float(np.max(1.0 - eq / peak))
    years = r.size / periods_per_year
    cagr = float(eq[-1] ** (1 / years) - 1) if eq[-1] > 0 else -1.0
    return {
        "sharpe": sharpe(r) * math.sqrt(periods_per_year),
        "cagr": cagr,
        "maxDrawdown": max_dd,
        "calmar": cagr / max_dd if max_dd > 0 else float("nan"),
        "totalReturn": float(eq[-1] - 1),
    }


def profit_factor(pnl) -> float:
    """Gross wins / gross losses; infinite with wins and no losses, NaN with neither."""
    x = np.asarray(pnl, dtype=float)
    wins, losses = float(x[x > 0].sum()), float(-x[x < 0].sum())
    if losses > 0:
        return wins / losses
    return float("inf") if wins > 0 else float("nan")
