"""python -m validation: one command from the C# export to the reports.

  run      [--strategies a,b [--param k=v ...]] [--all] [--skip-build] [--skip-export] [--config PATH]
           build the C# CLI (Release, no restore), run validate-export (default: every hunt hypothesis with a profitable
           dev baseline), analyse every bundle, write validation_report.json per candidate and summary.json/summary.md
  analyze  BUNDLE_DIR ...   re-analyse existing bundles (uses the trial analysis of the last run)
  --workers N               analysis processes (default min(8, CPUs - 1))
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

from .config import REPO_ROOT, load_config, output_dir
from .data import load_bundle, load_dev_bars, load_trials
from .fingerprint import validator_fingerprint
from .netguard import block_network
from .report import analyze, analyze_trials, build_report, num, sanitize, write_report

CLI_DLL = REPO_ROOT / "src" / "Backtester.Cli" / "bin" / "Release" / "net10.0" / "Backtester.Cli.dll"


def _dotnet(args: list[str]) -> None:
    print("$ dotnet " + " ".join(args), flush=True)
    subprocess.run(["dotnet", *args], cwd=REPO_ROOT, check=True)


def _trials_analysis(cfg: dict, refresh: bool) -> dict:
    """The effective-trials / search-PBO analysis, cached next to the trial export it was computed from."""
    tdir = output_dir(cfg) / "trials"
    cache = tdir / "analysis.json"
    trials = load_trials(cfg, tdir)
    key = {"export": trials.meta, "validator": validator_fingerprint(), "config": cfg["_sha256"]}
    if cache.exists() and not refresh:
        cached = json.loads(cache.read_text(encoding="utf-8"))
        if cached.get("key") == sanitize(key):
            return cached["analysis"]
    analysis = sanitize(analyze_trials(cfg, trials))
    cache.write_text(json.dumps({"key": sanitize(key), "analysis": analysis}, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    return analysis


def _analyze_one(config_path: str | None, path: str, trials: dict) -> tuple[dict, float]:
    """One bundle -> its report on disk and a summary row (runs in a worker process)."""
    block_network()
    t0 = time.time()
    cfg = load_config(config_path)
    b = load_bundle(cfg, path)
    report = build_report(cfg, b, analyze(cfg, b, trials, load_dev_bars(cfg)))
    out = write_report(b, report)
    g = {x["name"]: x for x in report["gates"]}
    mt = report["multipleTesting"]
    row = {
        "strategy": report["candidate"]["strategy"],
        "parameters": report["candidate"]["parameters"],
        "verdict": report["verdict"],
        "failedHardGates": report["failedHardGates"],
        "netProfitPct": report["baseline"]["netProfitPct"],
        "sharpe": report["baseline"]["sharpe"],
        "profitFactor": report["baseline"]["profitFactor"],
        "tradesPerYear": report["baseline"]["tradesPerYear"],
        "dsrGate": mt["dsrGate"],
        "dsrEffective": mt["dsrEffective"],
        "dsrRaw": mt["dsrRaw"],
        "pboSearch": mt["pboSearch"]["pbo"],
        "plateauScore": report["plateau"]["score"],
        "bootstrapSharpeP05": report["bootstrap"]["worstSharpeP05"],
        "bootstrapPfP05": report["bootstrap"]["worstProfitFactorP05"],
        "slippage2xNetPct": report["stress"]["slippageAtMultiplier"]["netProfitPct"],
        "commission2xNetPct": report["stress"]["commissionAtMultiplier"]["netProfitPct"],
        "minTrackRecordDays": report["trackRecord"]["minTrackRecordDays"],
        "losingRegimes": report["regimes"]["gates"]["losingRegimes"],
        "lookahead": g["lookahead_audit"]["value"],
        "report": out.relative_to(REPO_ROOT).as_posix(),
    }
    return row, time.time() - t0


def _analyze_bundles(config_path: str | None, bundles: list[Path], trials: dict, workers: int) -> list[dict]:
    """Analyses bundles in parallel processes; rows come back in bundle order, so the summary is deterministic."""
    rows = []
    with ProcessPoolExecutor(max_workers=max(1, min(workers, len(bundles)))) as pool:
        futures = [pool.submit(_analyze_one, config_path, str(p), trials) for p in bundles]
        for f in futures:
            row, seconds = f.result()
            rows.append(row)
            print(f"  {row['strategy']:<28} {row['verdict']:<7} failed: {', '.join(row['failedHardGates']) or '-'}  ({seconds:.1f}s)", flush=True)
    return rows


def _fmt(x, digits=2):
    if x is None:
        return "n/a"
    if isinstance(x, str):
        return "inf" if x == "Infinity" else x
    return f"{x:.{digits}f}"


def _write_summary(cfg: dict, rows: list[dict], trials: dict) -> Path:
    out = output_dir(cfg)
    eff = trials["effectiveTrials"]
    survivors = [r for r in rows if r["verdict"] == "PASS"]
    summary = {
        "candidates": len(rows),
        "passedEveryHardGate": len(survivors),
        "survivors": [r["strategy"] for r in survivors],
        "trials": {"raw": eff["raw"]["nTrials"], "distinct": eff["distinctReturnSeries"], "effective": eff["effective"]["nTrials"]},
        "pboSearch": trials["pboSearch"]["pbo"],
        "rows": rows,
    }
    (out / "summary.json").write_text(json.dumps(sanitize(summary), indent=2, allow_nan=False) + "\n", encoding="utf-8")
    gate_counts: dict[str, int] = {}
    for r in rows:
        for gname in r["failedHardGates"]:
            gate_counts[gname] = gate_counts.get(gname, 0) + 1
    lines = [
        "# Validation summary", "",
        f"{len(survivors)} of {len(rows)} candidates pass every hard gate. Trials: {eff['raw']['nTrials']} registered, "
        f"{eff['distinctReturnSeries']} distinct return series, {eff['effective']['nTrials']} effective (Li & Ji eigenvalue count). "
        f"PBO of the whole search (CSCV, {trials['pboSearch']['nConfigurations']} configurations): {trials['pboSearch']['pbo']:.2f}.", "",
        "| strategy | net % | Sharpe | PF | trades/yr | DSR (eff.) | DSR (raw) | plateau | boot. Sharpe p05 | boot. PF p05 | 2x slip net % | 2x comm net % | MinTRL days | failed hard gates |",
        "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|",
    ]
    for r in sorted(rows, key=lambda r: -(num(r["sharpe"]) if r["sharpe"] is not None else -9)):
        lines.append(f"| {r['strategy']} | {_fmt(r['netProfitPct'], 1)} | {_fmt(r['sharpe'])} | {_fmt(r['profitFactor'])} | {_fmt(r['tradesPerYear'], 0)} | "
                     f"{_fmt(r['dsrEffective'], 3)} | {_fmt(r['dsrRaw'], 3)} | {_fmt(r['plateauScore'])} | {_fmt(r['bootstrapSharpeP05'])} | "
                     f"{_fmt(r['bootstrapPfP05'])} | {_fmt(r['slippage2xNetPct'], 1)} | {_fmt(r['commission2xNetPct'], 1)} | "
                     f"{_fmt(r['minTrackRecordDays'], 0)} | {', '.join(r['failedHardGates']) or '-'} |")
    lines += ["", "Hard gates failed (number of candidates): " + ", ".join(f"{k} {v}" for k, v in sorted(gate_counts.items(), key=lambda kv: -kv[1])), ""]
    path = out / "summary.md"
    path.write_text("\n".join(lines), encoding="utf-8")
    return path


def main(argv: list[str] | None = None) -> int:
    block_network()
    ap = argparse.ArgumentParser(prog="python -m validation", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="command", required=True)
    run = sub.add_parser("run")
    run.add_argument("--strategies")
    run.add_argument("--param", action="append", default=[])
    run.add_argument("--all", action="store_true")
    run.add_argument("--skip-build", action="store_true")
    run.add_argument("--skip-export", action="store_true")
    run.add_argument("--config")
    an = sub.add_parser("analyze")
    an.add_argument("bundles", nargs="+")
    an.add_argument("--config")
    for p in (run, an):
        p.add_argument("--workers", type=int, default=min(8, max(1, (os.cpu_count() or 2) - 1)), help="analysis processes")
    a = ap.parse_args(argv)

    cfg = load_config(a.config)
    if a.command == "run":
        if not a.skip_build:
            _dotnet(["build", "TradingBot.slnx", "-c", "Release", "--no-restore", "-v", "q", "-nologo"])
        if not a.skip_export:
            args = [str(CLI_DLL), "validate-export"]
            if a.config:
                args += ["--config", a.config]
            if a.strategies:
                args += ["--strategies", a.strategies] + [x for p in a.param for x in ("--param", p)]
            elif a.all:
                args.append("--all")
            _dotnet(args)
        listing = json.loads((output_dir(cfg) / "candidates.json").read_text(encoding="utf-8"))
        bundles = [REPO_ROOT / c["bundle"] for c in listing["candidates"]]
    else:
        bundles = [Path(b) for b in a.bundles]

    trials = _trials_analysis(cfg, refresh=a.command == "run" and not a.skip_export)
    print(f"Trials: {trials['effectiveTrials']['raw']['nTrials']} registered, {trials['effectiveTrials']['distinctReturnSeries']} distinct, "
          f"{trials['effectiveTrials']['effective']['nTrials']} effective; search PBO {trials['pboSearch']['pbo']:.3f}", flush=True)
    rows = _analyze_bundles(a.config, bundles, trials, a.workers)
    path = _write_summary(cfg, rows, trials)
    passed = sum(r["verdict"] == "PASS" for r in rows)
    print(f"\n{passed} of {len(rows)} candidates pass every hard gate. Summary: {path.relative_to(REPO_ROOT).as_posix()}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
