"""Parameter robustness: the plateau score of the 1D and 2D perturbation grid exported by `validate-export`.

Neighbours: every valid perturbed configuration (continuous parameters moved by +/-10/20/30% alone and in pairs;
discrete ones moved to the adjacent grid value). A neighbour is robust when it stays profitable with PF above
`plateauMinProfitFactor` and Sharpe above `plateauMinSharpe`. Plateau score = robust / valid neighbours.
Edge: the chosen value of any tunable parameter sits on the minimum or maximum of its search grid.
"""

from __future__ import annotations

import math


def plateau(perturbations: dict, meta: dict, min_pf: float, min_sharpe: float) -> dict:
    runs = perturbations["runs"]
    valid = [r for r in runs if r["valid"]]

    def robust(r: dict) -> bool:
        m = r["metrics"]
        return m["netProfitPct"] > 0 and _num(m["profitFactor"]) > min_pf and _num(m["sharpe"]) > min_sharpe

    by_kind = {}
    for kind in ("1d", "2d"):
        k = [r for r in valid if r["kind"] == kind]
        by_kind[kind] = {"valid": len(k), "robust": sum(robust(r) for r in k)}
    one_d = []
    for r in (r for r in valid if r["kind"] == "1d"):
        (name, step), = r["steps"].items()
        m = r["metrics"]
        one_d.append({"parameter": name, "value": step["value"], "relative": step["relative"], "gridSteps": step["gridSteps"],
                      "netProfitPct": m["netProfitPct"], "profitFactor": m["profitFactor"], "sharpe": m["sharpe"],
                      "trades": m["trades"], "robust": robust(r)})

    params = meta["parameters"]
    edge = []
    for spec in meta["searchSpace"]:
        v, values = params[spec["name"]], spec["values"]
        if len(values) > 1 and (v <= min(values) + 1e-12 or v >= max(values) - 1e-12):
            edge.append(spec["name"])
    n_robust = sum(robust(r) for r in valid)
    return {
        "neighbours": len(runs),
        "valid": len(valid),
        "invalid": len(runs) - len(valid),
        "robust": n_robust,
        "score": n_robust / len(valid) if valid else 0.0,
        "byKind": by_kind,
        "oneDimensional": one_d,
        "onEdgeOf": edge,
        "criteria": {"netProfit": "> 0", "profitFactor": f"> {min_pf}", "sharpe": f"> {min_sharpe}"},
    }


def _num(x) -> float:
    if isinstance(x, (int, float)):
        return float(x)
    return {"Infinity": math.inf, "-Infinity": -math.inf}.get(str(x), math.nan)
