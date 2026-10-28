"""config/hunt.json (JSON with // comments) and repository paths."""

from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_CONFIG = REPO_ROOT / "config" / "hunt.json"


def strip_json_comments(text: str) -> str:
    """Removes // and /* */ comments outside strings (the C# loader skips them too)."""
    out: list[str] = []
    i, n, in_string = 0, len(text), False
    while i < n:
        c = text[i]
        if in_string:
            out.append(c)
            if c == "\\" and i + 1 < n:
                out.append(text[i + 1])
                i += 2
                continue
            if c == '"':
                in_string = False
            i += 1
        elif c == '"':
            in_string = True
            out.append(c)
            i += 1
        elif text.startswith("//", i):
            j = text.find("\n", i)
            i = n if j < 0 else j
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2)
            i = n if j < 0 else j + 2
        else:
            out.append(c)
            i += 1
    return "".join(out)


def load_config(path: str | Path | None = None) -> dict:
    """The parsed config plus `_path` and `_sha256` (CRLF read as LF, as HoldoutGate.TextSha256 hashes it)."""
    p = Path(path) if path else DEFAULT_CONFIG
    raw = p.read_bytes()
    cfg = json.loads(strip_json_comments(raw.decode("utf-8-sig")))
    cfg["_path"] = str(p)
    cfg["_sha256"] = hashlib.sha256(raw.replace(b"\r\n", b"\n")).hexdigest()
    return cfg


def iso_to_ms(text: str) -> int:
    t = datetime.fromisoformat(text.replace("Z", "+00:00"))
    if t.tzinfo is None:
        t = t.replace(tzinfo=timezone.utc)
    return int(t.timestamp() * 1000)


def dev_window_ms(cfg: dict) -> tuple[int, int]:
    """Development window [from, to) in Unix ms. `to` is also where the locked holdout starts."""
    return iso_to_ms(cfg["split"]["devFrom"]), iso_to_ms(cfg["split"]["devTo"])


def validation_settings(cfg: dict) -> dict:
    return cfg["validation"]


def output_dir(cfg: dict) -> Path:
    return REPO_ROOT / validation_settings(cfg).get("outputDir", "artifacts/validation")
