"""The hard gates. Every one must pass before a candidate may run on the locked holdout (the C# holdout gate checks the
report for all of REQUIRED_GATES, so a report missing one is refused, not passed). Thresholds: config "validation.gates".
"""

from __future__ import annotations

import math
from dataclasses import asdict, dataclass

# Mirrors HoldoutGate.RequiredGates in src/Backtester.Core/Validation/HoldoutGate.cs (a test keeps them equal).
REQUIRED_GATES = (
    "lookahead_audit", "deflated_sharpe", "pbo", "plateau_score", "plateau_edge", "regime_losses", "regime_concentration",
    "bootstrap_sharpe_p05", "bootstrap_pf_p05", "stress_slippage_2x", "stress_commission_2x", "min_track_record", "trades_per_year",
)


@dataclass
class Gate:
    name: str
    description: str
    value: object
    threshold: object
    comparator: str
    passed: bool
    hard: bool = True
    detail: str = ""


def _fmt(x) -> str:
    if isinstance(x, float):
        return "inf" if math.isinf(x) else "nan" if math.isnan(x) else f"{x:.4g}"
    return str(x)


def _cmp(name, description, value, op, threshold, hard=True, unit="") -> Gate:
    v = float(value) if value is not None else math.nan
    ok = {">=": v >= threshold, "<=": v <= threshold, ">": v > threshold, "<": v < threshold}[op] if not math.isnan(v) else False
    return Gate(name, description, value, threshold, op, bool(ok), hard,
                f"{_fmt(v)}{unit} {'meets' if ok else 'fails'} {op} {_fmt(float(threshold))}{unit}")


def evaluate(r: dict, g: dict) -> list[Gate]:
    """`r`: the analysis results (see report.analyze); `g`: config validation.gates."""
    mult = g["stressMultiplier"]
    mt, bt, pl, rg, st = r["multipleTesting"], r["bootstrap"], r["plateau"], r["regimes"], r["stress"]
    audit = r["lookAheadAudit"]
    gates = [
        Gate("lookahead_audit", "signals, indicator series and funding settlement use no data after the decision bar",
             audit["verdict"], "pass", "==", audit["verdict"] == "pass", True,
             f"{audit['verdict']}: {audit['comparisons']} spliced futures, {audit['tradesBeforeCut']} trades compared"
             + (f"; {'; '.join(audit['problems'][:3])}" if audit["problems"] else "")),
        _cmp("deflated_sharpe", "DSR against the stricter of the raw-count and effective-trials benchmarks",
             mt["dsrGate"], ">=", g["minDeflatedSharpe"]),
        _cmp("pbo", "probability of backtest overfitting of the search that produced the candidate (CSCV over all registered trials)",
             mt["pboSearch"]["pbo"], "<=", g["maxPbo"]),
        _cmp("plateau_score", "share of 1D/2D parameter neighbours still profitable with PF and Sharpe above the plateau floors",
             pl["score"], ">=", g["minPlateauScore"]),
        Gate("plateau_edge", "chosen parameters are not on the edge of their search grid", pl["onEdgeOf"], [], "==",
             not pl["onEdgeOf"], True, "interior" if not pl["onEdgeOf"] else f"on the edge of {', '.join(pl['onEdgeOf'])}"),
        Gate("regime_losses", f"loses money in at most {g['maxLosingRegimes']} regime(s) with >= {g['regimeMinTrades']} trades",
             rg["gates"]["losingRegimes"], g["maxLosingRegimes"], "count <=", rg["gates"]["lossesPassed"], True,
             f"losing in {', '.join(rg['gates']['losingRegimes']) or 'no regime'}"),
        Gate("regime_concentration", f"profit not confined to one regime holding < {g['regimeConcentrationMaxTradeShare']:.0%} of trades",
             rg["gates"]["concentratedIn"], g["regimeConcentrationMaxTradeShare"], "trade share >=", rg["gates"]["concentrationPassed"], True,
             "profit spread across regimes" if rg["gates"]["concentrationPassed"] else f"all profit from {', '.join(rg['gates']['concentratedIn'])}"),
        _cmp("bootstrap_sharpe_p05", "5th percentile of bootstrapped annualized Sharpe (worst block length)", bt["worstSharpeP05"], ">=",
             g["bootstrapMinSharpeP05"]),
        _cmp("bootstrap_pf_p05", "5th percentile of bootstrapped profit factor (worst block length)", bt["worstProfitFactorP05"], ">=",
             g["bootstrapMinProfitFactorP05"]),
        _cmp(f"stress_slippage_{mult:g}x", f"net profit at {mult:g}x base slippage", st["slippageAtMultiplier"]["netProfitPct"], ">", 0.0, unit="%"),
        _cmp(f"stress_commission_{mult:g}x", f"net profit at {mult:g}x base commission", st["commissionAtMultiplier"]["netProfitPct"], ">", 0.0, unit="%"),
        _cmp("min_track_record", "development window (days) at least the minimum track record length for the observed Sharpe",
             r["trackRecord"]["devDays"], ">=", r["trackRecord"]["minTrackRecordDays"], unit=" days"),
        _cmp("trades_per_year", "trades per year on dev", r["baseline"]["tradesPerYear"], ">=", g["minTradesPerYear"]),
    ]
    return gates


def summarize(gates: list[Gate]) -> dict:
    failed = [x.name for x in gates if x.hard and not x.passed]
    return {"gates": [asdict(x) for x in gates], "allHardGatesPassed": not failed, "failedHardGates": failed,
            "verdict": "PASS" if not failed else "REJECT"}
