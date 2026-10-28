"""Execution stress: the cost/funding ladder exported by `validate-export`, plus a gap-through-stop scenario.

Ladder (rerun by the engine on dev): slippage x0..4, commission x1..3, funding off, every funding rate x3, and every
settlement charged against the position at the mean |rate| of each dev funding-spike month (2021-05, 2022-06, 2022-11;
2024-03 lies in the locked holdout and is not used). Gate: the candidate must stay profitable at `stressMultiplier` x
base slippage and at `stressMultiplier` x base commission.

Gap through the stop (liquidation cascade, reported, not gated): the mark price gaps g = 10/15/20% through the stop
within one bar and the stop fills there. Positions are sized so that a fill at the stop loses `riskPerTrade` of equity,
so the event's loss is that plus exposure x g, where exposure = position notional / equity at entry. The scenario takes
the largest exposure the strategy ever carried, i.e. the worst single event.
"""

from __future__ import annotations

import numpy as np


def ladder(stress: dict) -> list[dict]:
    return [{"name": s["name"], "group": s["group"], "value": s["value"], "description": s["description"], **s["metrics"]}
            for s in stress["scenarios"]]


def scenario(stress: dict, name: str) -> dict | None:
    return next((s for s in stress["scenarios"] if s["name"] == name), None)


def multiplier_name(group: str, multiplier: float) -> str:
    m = int(multiplier) if float(multiplier).is_integer() else multiplier
    return f"{group}_x{m}"


def gap_through_stop(trades, equity_at_entry: np.ndarray, risk_per_trade: float, gaps: list[float]) -> list[dict]:
    if len(trades) == 0:
        return []
    exposure = trades["quantity"].to_numpy(dtype=float) * trades["entry_price"].to_numpy(dtype=float) / equity_at_entry
    worst = int(np.argmax(exposure))
    rows = []
    for g in gaps:
        rows.append({
            "gapPct": g * 100,
            "maxExposure": float(exposure[worst]),
            "worstTradeEntryMs": int(trades["entry_ms"].iloc[worst]),
            "fillVsStop": f"stop x (1 {'-' if trades['direction'].iloc[worst] > 0 else '+'} {g:.2f})",
            "equityLossPct": float((risk_per_trade + exposure[worst] * g) * 100),
        })
    return rows
