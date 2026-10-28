"""Regime stratification of the candidate's dev results (AUDIT.md D5).

Three two-way partitions of the UTC days of the development window, each labelled from BTC daily closes up to the
previous day's close only (no future information, and no holdout bar):
  trend       bull: the 200-day SMA of daily closes rose over the last 20 days; bear: otherwise
  volatility  high-vol: 30-day realized volatility above the 75th percentile of its own trailing 365 days
              (at least 90 days of history); low-vol: otherwise
  efficiency  trending: 30-day Kaufman efficiency ratio |close - close 30 days ago| / sum |daily changes| above its
              trailing 365-day median; ranging: otherwise
Per regime: days, compounded net return of the daily strategy returns, annualized Sharpe of those days, trades (by
entry day), their profit factor and net PnL.

Gates (config "validation.gates"), applied across all six regimes:
  regime_losses         reject if the strategy's trades lose money in two or more regimes that hold at least
                        `regimeMinTrades` trades each;
  regime_concentration  reject if one regime earns all the profit (its net PnL is at least the total, so the rest of its
                        partition made nothing) while holding less than `regimeConcentrationMaxTradeShare` of the trades.
The audit's D5 suggested applying the first rule within each partition; within a two-way partition a profitable strategy
cannot lose in both halves, so that version could never fire. It is applied across the six regimes, as specified.
"""

from __future__ import annotations

import math

import numpy as np
import pandas as pd

from .stats import DAYS_PER_YEAR, profit_factor

DAY_MS = 86_400_000
PARTITIONS = {"trend": ("bull", "bear"), "volatility": ("high-vol", "low-vol"), "efficiency": ("trending", "ranging")}


def daily_closes(bars: pd.DataFrame) -> pd.Series:
    """Close of each complete UTC day (24 hourly bars), indexed by the day's start in ms."""
    day = bars["open_time_ms"].to_numpy(dtype=np.int64) // DAY_MS * DAY_MS
    g = pd.DataFrame({"day": day, "close": bars["close"].to_numpy(dtype=float)}).groupby("day")
    closes = g["close"].last()
    return closes[g.size() == 24]


def label_days(closes: pd.Series, settings: dict) -> pd.DataFrame:
    """Regime labels for every day, each computed from closes up to the previous day (shifted by one day)."""
    c = closes.astype(float)
    sma = c.rolling(settings["smaDays"]).mean()
    slope = sma - sma.shift(settings["slopeDays"])
    rv = np.log(c).diff().rolling(settings["volDays"]).std(ddof=1) * math.sqrt(DAYS_PER_YEAR)
    rv_threshold = rv.rolling(settings["lookbackDays"], min_periods=settings["minHistoryDays"]).quantile(settings["volQuantile"])
    er = (c - c.shift(settings["erDays"])).abs() / c.diff().abs().rolling(settings["erDays"]).sum()
    er_median = er.rolling(settings["lookbackDays"], min_periods=settings["minHistoryDays"]).median()
    known = pd.DataFrame({"slope": slope, "rv": rv, "rvThreshold": rv_threshold, "er": er, "erMedian": er_median}).shift(1)
    out = pd.DataFrame(index=c.index)
    out["trend"] = np.where(known["slope"].isna(), None, np.where(known["slope"] > 0, "bull", "bear"))
    out["volatility"] = np.where(known["rvThreshold"].isna() | known["rv"].isna(), None,
                                 np.where(known["rv"] > known["rvThreshold"], "high-vol", "low-vol"))
    out["efficiency"] = np.where(known["erMedian"].isna() | known["er"].isna(), None,
                                 np.where(known["er"] > known["erMedian"], "trending", "ranging"))
    return pd.concat([out, known], axis=1)


def stratify(labels: pd.DataFrame, daily_returns: pd.Series, trades: pd.DataFrame) -> list[dict]:
    """One row per regime: days, compounded return, Sharpe, trades, profit factor, net PnL and shares of the total."""
    lab = labels.reindex(daily_returns.index)
    entry_day = trades["entry_ms"].to_numpy(dtype=np.int64) // DAY_MS * DAY_MS
    trade_lab = labels.reindex(entry_day)
    pnl = trades["net_pnl"].to_numpy(dtype=float)
    total_pnl, total_trades = float(pnl.sum()), int(len(trades))
    rows = []
    for partition, names in PARTITIONS.items():
        for name in names:
            day_mask = (lab[partition] == name).to_numpy()
            r = daily_returns.to_numpy()[day_mask]
            t_mask = (trade_lab[partition] == name).to_numpy()
            sd = r.std(ddof=1) if r.size > 1 else 0.0
            rows.append({
                "partition": partition,
                "regime": name,
                "days": int(day_mask.sum()),
                "netReturnPct": float((np.prod(1.0 + r) - 1.0) * 100) if r.size else 0.0,
                "sharpe": float(r.mean() / sd * math.sqrt(DAYS_PER_YEAR)) if sd > 0 else 0.0,
                "trades": int(t_mask.sum()),
                "profitFactor": profit_factor(pnl[t_mask]),
                "netPnl": float(pnl[t_mask].sum()),
                "tradeShare": float(t_mask.sum() / total_trades) if total_trades else 0.0,
                "pnlShare": float(pnl[t_mask].sum() / total_pnl) if total_pnl else float("nan"),
            })
    unlabeled = int(lab[list(PARTITIONS)].isna().any(axis=1).sum())
    if unlabeled:
        raise ValueError(f"{unlabeled} dev days have no regime label; the bar history before dev is too short")
    return rows


def gates(rows: list[dict], min_trades: int, max_losing: int, max_trade_share: float) -> dict:
    total_pnl = sum(r["netPnl"] for r in rows if r["partition"] == "trend")
    losing = [r["regime"] for r in rows if r["trades"] >= min_trades and r["netPnl"] < 0]
    concentrated = [r["regime"] for r in rows
                    if total_pnl > 0 and r["netPnl"] >= total_pnl and r["tradeShare"] < max_trade_share]
    return {
        "losingRegimes": losing,
        "lossesPassed": len(losing) <= max_losing,
        "concentratedIn": concentrated,
        "concentrationPassed": not concentrated,
    }
