"""Dev-only data access. Nothing in this package can obtain a holdout row.

* `load_dev_bars` reads the audited Bybit CSV and returns only bars opening before the end of the development window
  (earlier history is kept: it is indicator warm-up). There is deliberately no parameter to read further.
* Bundle readers check that every timestamp an export contains lies inside the development window, so a mistaken export
  is refused here rather than analysed. The locked holdout is run only by the C# CLI, behind its holdout gate.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import pandas as pd

from .config import REPO_ROOT, dev_window_ms

DAY_MS = 86_400_000
HOUR_MS = 3_600_000


class HoldoutLocked(RuntimeError):
    """Raised when data from the locked holdout window would enter the validation layer."""


def load_dev_bars(cfg: dict) -> pd.DataFrame:
    """Hourly bars with open time < devTo (columns open_time_ms, open, high, low, close, volume)."""
    _, dev_to = dev_window_ms(cfg)
    path = REPO_ROOT / cfg.get("dataDir", "data/bybit") / f"{cfg.get('symbol', 'BTCUSDT')}_{cfg.get('intervalMinutes', 60)}.csv"
    bars = pd.read_csv(path)
    bars = bars[bars["open_time_ms"] < dev_to].sort_values("open_time_ms").reset_index(drop=True)
    assert_dev_only(cfg, bars["open_time_ms"], "bar open times", inclusive_end=False)
    return bars


def assert_dev_only(cfg: dict, times_ms, what: str, inclusive_end: bool = True) -> None:
    """Refuses timestamps after the development window. Bar-close and exit stamps may equal devTo (the last dev bar
    closes at that instant); open stamps may not."""
    _, dev_to = dev_window_ms(cfg)
    t = np.asarray(times_ms, dtype=np.int64)
    if t.size == 0:
        return
    bad = t > dev_to if inclusive_end else t >= dev_to
    if bad.any():
        raise HoldoutLocked(f"{what} reach into the locked holdout window ({int(t[bad][0])} >= devTo {dev_to})")


@dataclass
class Bundle:
    """One candidate's export from `validate-export`."""

    path: Path
    meta: dict
    equity: pd.DataFrame
    trades: pd.DataFrame
    stress: dict
    perturbations: dict
    selection: dict
    selection_daily: pd.DataFrame
    selection_trades: pd.DataFrame

    @property
    def candidate_id(self) -> str:
        return self.meta["candidate"]["id"]

    def hourly_returns(self) -> np.ndarray:
        """Mark-to-market return of every bar (hourly unless meta intervalMinutes says otherwise; the first from the
        initial capital)."""
        initial = float(self.meta["candidate"]["identity"]["initialCapital"])
        eq = self.equity["equity"].to_numpy(dtype=float)
        prev = np.concatenate([[initial], eq[:-1]])
        return eq / prev - 1.0

    def daily_returns(self) -> pd.Series:
        """End-of-UTC-day equity returns indexed by day start (ms), as the C# engine computes them."""
        initial = float(self.meta["candidate"]["identity"]["initialCapital"])
        day = (self.equity["time_ms"].to_numpy(dtype=np.int64) - 1) // DAY_MS * DAY_MS
        ends = pd.Series(self.equity["equity"].to_numpy(dtype=float), index=day).groupby(level=0).last()
        prev = np.concatenate([[initial], ends.to_numpy()[:-1]])
        return pd.Series(ends.to_numpy() / prev - 1.0, index=ends.index)

    def equity_before(self, times_ms) -> np.ndarray:
        """Equity marked at the last close at or before each time (the initial capital before the first close)."""
        initial = float(self.meta["candidate"]["identity"]["initialCapital"])
        t = self.equity["time_ms"].to_numpy(dtype=np.int64)
        eq = self.equity["equity"].to_numpy(dtype=float)
        pos = np.searchsorted(t, np.asarray(times_ms, dtype=np.int64), side="right") - 1
        return np.where(pos >= 0, eq[np.clip(pos, 0, None)], initial)


def load_bundle(cfg: dict, path: str | Path) -> Bundle:
    p = Path(path)
    if not p.is_absolute():
        p = REPO_ROOT / p
    meta = json.loads((p / "meta.json").read_text(encoding="utf-8"))
    files = meta["files"]
    b = Bundle(
        path=p,
        meta=meta,
        equity=pd.read_csv(p / files["equity"]),
        trades=pd.read_csv(p / files["trades"]),
        stress=json.loads((p / files["stress"]).read_text(encoding="utf-8")),
        perturbations=json.loads((p / files["perturbations"]).read_text(encoding="utf-8")),
        selection=json.loads((p / files["selection"]).read_text(encoding="utf-8")),
        selection_daily=pd.read_csv(p / files["selectionDaily"]),
        selection_trades=pd.read_csv(p / files["selectionTrades"]),
    )
    dev_from, dev_to = dev_window_ms(cfg)
    if (meta["window"]["devFromMs"], meta["window"]["devToMs"]) != (dev_from, dev_to):
        raise HoldoutLocked(f"{p}: exported for window {meta['window']}, config says dev {dev_from}..{dev_to}")
    assert_dev_only(cfg, b.equity["time_ms"], "equity closes")
    assert_dev_only(cfg, b.trades["entry_ms"], "trade entries", inclusive_end=False)
    assert_dev_only(cfg, b.trades["exit_ms"], "trade exits")
    assert_dev_only(cfg, b.selection_daily["day_ms"], "selection days", inclusive_end=False)
    assert_dev_only(cfg, b.selection_trades["exit_ms"], "selection trade exits")
    return b


@dataclass
class Trials:
    """Every registered trial replayed on dev: `index` (one row per trial) and `daily` (one column per distinct series)."""

    index: pd.DataFrame
    daily: pd.DataFrame
    meta: dict

    def matrix(self) -> np.ndarray:
        return self.daily.drop(columns=["day_ms"]).to_numpy(dtype=float)


def load_trials(cfg: dict, path: str | Path) -> Trials:
    p = Path(path)
    t = Trials(
        index=pd.read_csv(p / "index.csv"),
        daily=pd.read_csv(p / "daily_returns.csv"),
        meta=json.loads((p / "meta.json").read_text(encoding="utf-8")),
    )
    assert_dev_only(cfg, t.daily["day_ms"], "trial days", inclusive_end=False)
    return t
