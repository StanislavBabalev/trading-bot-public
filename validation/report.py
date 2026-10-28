"""Runs every analysis on one exported bundle and writes its validation_report.json (schema: schema/validation_report.schema.json)."""

from __future__ import annotations

import json
import math
import platform
from datetime import datetime, timezone
from importlib import metadata
from pathlib import Path

import numpy as np

from . import SCHEMA_VERSION, bootstrap, cpcv, crosscheck, regimes, robustness, stress
from .config import REPO_ROOT
from .data import Bundle, Trials, load_dev_bars
from .fingerprint import validator_fingerprint
from .gates import evaluate, summarize
from .pbo import cscv
from .stats import (DAYS_PER_YEAR, deflated_sharpe, expected_max_sharpe, min_backtest_length_years, min_track_record_length,
                    psr)
from .trials import drop_flat_columns, effective_trials

SCHEMA_PATH = Path(__file__).resolve().parent / "schema" / "validation_report.schema.json"
REPORT_FILE = "validation_report.json"


def num(x) -> float:
    """A number from the engine's JSON, which writes non-finite values as strings."""
    if isinstance(x, (int, float)):
        return float(x)
    return {"Infinity": math.inf, "-Infinity": -math.inf}.get(str(x), math.nan)


def sanitize(x):
    """JSON-safe copy: numpy scalars to Python, NaN to null, infinities to "Infinity"/"-Infinity"."""
    if isinstance(x, dict):
        return {str(k): sanitize(v) for k, v in x.items()}
    if isinstance(x, (list, tuple)):
        return [sanitize(v) for v in x]
    if isinstance(x, np.ndarray):
        return [sanitize(v) for v in x.tolist()]
    if isinstance(x, (np.bool_,)):
        return bool(x)
    if isinstance(x, (np.integer,)):
        return int(x)
    if isinstance(x, (float, np.floating)):
        f = float(x)
        if math.isnan(f):
            return None
        if math.isinf(f):
            return "Infinity" if f > 0 else "-Infinity"
        return f
    return x


def analyze_trials(cfg: dict, trials: Trials) -> dict:
    """Shared by every candidate: effective number of trials and the PBO of the whole registered search."""
    blocks = cfg["validation"]["cscv"]["blocks"]
    names = [c for c in trials.daily.columns if c != "day_ms"]
    matrix = trials.matrix()
    eff = effective_trials(matrix, names, trials.index["registry_daily_sharpe"].to_numpy(dtype=float))
    x, _, _ = drop_flat_columns(matrix, names)
    search = cscv(x, blocks)
    search["crossCheck"] = crosscheck.pbo(x, blocks, search["pbo"])
    search["selectionSet"] = "every distinct registered trial with non-flat dev returns"
    return {"export": trials.meta, "effectiveTrials": eff, "pboSearch": search}


def analyze(cfg: dict, bundle: Bundle, trials_analysis: dict, bars=None) -> dict:
    v, g = cfg["validation"], cfg["validation"]["gates"]
    meta, m = bundle.meta, bundle.meta["metrics"]
    sr, n = num(m["dailySharpe"]), int(m["dailyReturnCount"])
    skew, kurt = num(m["dailySkewness"]), num(m["dailyKurtosis"])
    eff = trials_analysis["effectiveTrials"]
    n_eff, var_eff = eff["effective"]["nTrials"], eff["effective"]["sharpeVariance"]
    n_raw, var_raw = eff["raw"]["nTrials"], eff["raw"]["sharpeVariance"]
    annual = sr * math.sqrt(DAYS_PER_YEAR)
    daily = bundle.daily_returns()

    psr0 = psr(sr, n, skew, kurt, 0.0)
    dsr_eff = deflated_sharpe(sr, n, skew, kurt, n_eff, var_eff)
    dsr_raw = deflated_sharpe(sr, n, skew, kurt, n_raw, var_raw)
    # The gate uses the stricter benchmark: never weaker than the hunt's own raw-count correction.
    gate_basis = "effective" if expected_max_sharpe(n_eff, var_eff) >= expected_max_sharpe(n_raw, var_raw) else "raw"
    mintrl = min_track_record_length(sr, skew, kurt, 0.0, g["minTrackRecordConfidence"])
    sel_names = [c for c in bundle.selection_daily.columns if c != "day_ms"]
    sel_x, _, _ = drop_flat_columns(bundle.selection_daily[sel_names].to_numpy(dtype=float), sel_names)
    local = cscv(sel_x, v["cscv"]["blocks"]) if sel_x.shape[1] >= 2 else {"note": "fewer than two non-flat configurations"}
    search = {k: trials_analysis["pboSearch"][k] for k in
              ("pbo", "nPartitions", "nConfigurations", "nBlocks", "probabilityOosLoss", "degradation", "logitMean", "crossCheck", "selectionSet")}

    multiple = {
        "dailySharpe": sr, "annualSharpe": annual, "observations": n, "skewness": skew, "kurtosis": kurt,
        "psr": psr0,
        "nTrialsRaw": n_raw, "nTrialsDistinct": eff["distinctReturnSeries"], "nTrialsEffective": n_eff,
        "trialSharpeVarianceRaw": var_raw, "trialSharpeVarianceEffective": var_eff,
        "expectedMaxSharpeRaw": {"daily": expected_max_sharpe(n_raw, var_raw), "annual": expected_max_sharpe(n_raw, var_raw) * math.sqrt(DAYS_PER_YEAR)},
        "expectedMaxSharpeEffective": {"daily": expected_max_sharpe(n_eff, var_eff), "annual": expected_max_sharpe(n_eff, var_eff) * math.sqrt(DAYS_PER_YEAR)},
        "dsrRaw": dsr_raw,
        "dsrEffective": dsr_eff,
        "dsrGate": dsr_eff if gate_basis == "effective" else dsr_raw,
        "dsrGateBasis": gate_basis,
        "minBacktestLengthYears": {"raw": min_backtest_length_years(n_raw, annual), "effective": min_backtest_length_years(n_eff, annual),
                                   "devYears": num(m["years"])},
        "pboSearch": search,
        "pboLocal": local,
        "alternativeTrialCounts": eff["alternativeCounts"],
    }

    horizon = int(math.ceil(meta["maxBarsHeld"] / (1440.0 / meta.get("intervalMinutes", 60)))) + 1   # days
    embargo = int(math.floor(len(bundle.selection_daily) * v["cpcv"]["embargoFraction"]))
    cp = cpcv.run(bundle.selection_daily, bundle.selection_trades, v["cpcv"]["groups"], v["cpcv"]["testGroups"], horizon, embargo)
    cp["selectionSource"] = bundle.selection["source"]

    pl = robustness.plateau(bundle.perturbations, meta, g["plateauMinProfitFactor"], g["plateauMinSharpe"])

    if bars is None:
        bars = load_dev_bars(cfg)
    labels = regimes.label_days(regimes.daily_closes(bars), v["regimes"])
    table = regimes.stratify(labels, daily, bundle.trades)
    rg = {"settings": v["regimes"], "table": table,
          "gates": regimes.gates(table, g["regimeMinTrades"], g["maxLosingRegimes"], g["regimeConcentrationMaxTradeShare"])}

    trades = bundle.trades
    trade_returns = trades["net_pnl"].to_numpy(dtype=float) / bundle.equity_before(trades["entry_ms"])
    bt = bootstrap.run(bundle.hourly_returns(), trade_returns, v["bootstrap"]["meanBlockLengths"], v["bootstrap"]["paths"], v["bootstrap"]["seed"],
                       interval_minutes=int(meta.get("intervalMinutes", 60)))

    mult = g["stressMultiplier"]
    slip = stress.scenario(bundle.stress, stress.multiplier_name("slippage", mult))
    comm = stress.scenario(bundle.stress, stress.multiplier_name("commission", mult))
    if slip is None or comm is None:
        raise ValueError(f"{bundle.path}: the export has no {mult}x slippage/commission scenario; add {mult} to the multipliers in config")
    risk = float(meta["candidate"]["identity"]["sizing"]["riskPerTrade"])
    st = {
        "baseCosts": bundle.stress["baseCosts"],
        "ladder": stress.ladder(bundle.stress),
        "slippageAtMultiplier": {"multiplier": mult, **slip["metrics"]},
        "commissionAtMultiplier": {"multiplier": mult, **comm["metrics"]},
        "fundingSpikes": meta["fundingSpikes"],
        "gapThroughStop": stress.gap_through_stop(trades, bundle.equity_before(trades["entry_ms"]), risk, v["stress"]["gapPcts"]),
    }

    baseline = {k: num(m[k]) if k not in ("from", "to") else m[k] for k in
                ("from", "to", "years", "netProfitPct", "cagrPct", "maxDrawdownPct", "profitFactor", "sharpe", "sortino", "calmar",
                 "trades", "tradesPerYear", "winRatePct", "timeInMarketPct", "turnoverPerYear", "feesPaid", "fundingReceived", "slippageCost")}
    results = {
        "baseline": baseline,
        "attribution": meta["attribution"],
        "multipleTesting": multiple,
        "trackRecord": {"devDays": n, "minTrackRecordDays": mintrl, "confidence": g["minTrackRecordConfidence"],
                        "minTrackRecordYears": mintrl / DAYS_PER_YEAR},
        "cpcv": cp,
        "plateau": pl,
        "regimes": rg,
        "bootstrap": bt,
        "stress": st,
        "lookAheadAudit": meta["lookAheadAudit"],
    }
    results["crossChecks"] = {
        "statistics": crosscheck.statistics(sr, n, skew, kurt, n_eff, var_eff, daily.to_numpy(), psr0, dsr_eff, mintrl, g["minTrackRecordConfidence"]),
        "pboSearch": search["crossCheck"],
    }
    return results


def build_report(cfg: dict, bundle: Bundle, results: dict) -> dict:
    meta = bundle.meta
    gates = summarize(evaluate(results, cfg["validation"]["gates"]))
    packages = {}
    for p in ("numpy", "scipy", "pandas", "scikit-learn", "purgedcv", "holdout-backtest", "jsonschema"):
        try:
            packages[p] = metadata.version(p)
        except metadata.PackageNotFoundError:
            packages[p] = None
    config_path = Path(cfg["_path"])
    report = {
        "schemaVersion": SCHEMA_VERSION,
        "generatedUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "candidate": {
            "id": meta["candidate"]["id"],
            "strategy": meta["strategy"]["name"],
            "family": meta["strategy"]["family"],
            "version": meta["strategy"]["version"],
            "parameters": meta["parameters"],
            "identity": meta["candidate"]["identity"],
        },
        "validator": {
            "fingerprint": validator_fingerprint(),
            "configSha256": cfg["_sha256"],
            "configPath": config_path.relative_to(REPO_ROOT).as_posix() if config_path.is_relative_to(REPO_ROOT) else str(config_path),
            "thresholds": cfg["validation"]["gates"],
            "python": platform.python_version(),
            "packages": packages,
        },
        "window": meta["window"],
        "data": meta["data"],
        **results,
        **gates,
    }
    return sanitize(report)


def validate_schema(report: dict) -> None:
    import jsonschema

    jsonschema.validate(report, json.loads(SCHEMA_PATH.read_text(encoding="utf-8")))


def write_report(bundle: Bundle, report: dict) -> Path:
    validate_schema(report)
    path = bundle.path / REPORT_FILE
    path.write_text(json.dumps(report, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    return path
