"""Hashes that tie a report to the code that wrote it. Must match HoldoutGate.ValidatorFingerprint in the C# engine."""

from __future__ import annotations

import hashlib
from pathlib import Path

from .config import REPO_ROOT

_SKIPPED_DIRS = {"bin", "obj", "__pycache__"}


def validator_fingerprint(repo_root: Path = REPO_ROOT) -> str:
    """SHA-256 over validation/**/*.py (tests and caches excluded), sorted by relative path; each file contributes its
    UTF-8 relative path, a zero byte and the SHA-256 of its bytes with CRLF read as LF (so a checkout with other line
    endings keeps its reports valid)."""
    root = repo_root / "validation"
    rels = []
    for p in root.rglob("*.py"):
        rel = p.relative_to(repo_root).as_posix()
        if rel.startswith("validation/tests/") or _SKIPPED_DIRS & set(rel.split("/")):
            continue
        rels.append(rel)
    h = hashlib.sha256()
    for rel in sorted(rels):
        h.update(rel.encode("utf-8"))
        h.update(b"\0")
        h.update(hashlib.sha256(lf_bytes((repo_root / rel).read_bytes())).digest())
    return h.hexdigest()


def lf_bytes(raw: bytes) -> bytes:
    return raw.replace(b"\r\n", b"\n")


def text_sha256(raw: bytes) -> str:
    """SHA-256 of a text file's bytes with CRLF read as LF; HoldoutGate.TextSha256 in C# matches it."""
    return hashlib.sha256(lf_bytes(raw)).hexdigest()
