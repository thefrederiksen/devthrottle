"""The owner's decisions on a branch. A decided finding is never raised again there.

One JSON line per decision:
{"at": "...", "finding": "<key>", "title": "...", "file": "...", "decision": "fix|keep|drop",
 "note": "...", "severity": "error", "run": "<run id>"}
"""

from __future__ import annotations

import json
import re
import time
from pathlib import Path

import runstore

DECISIONS = ("fix", "keep", "drop")


def log_path(slug: str, branch: str) -> Path:
    safe_branch = re.sub(r"[^A-Za-z0-9._-]+", "_", branch)
    safe_repo = slug.replace("/", "__")
    return runstore.ship_root() / "decisions" / safe_repo / f"{safe_branch}.jsonl"


def finding_key(finding: dict) -> str:
    """Stable across review rounds: the reviewer's ids (F1, F2) are not."""
    title = re.sub(r"[^a-z0-9]+", " ", str(finding.get("title", "")).lower()).strip()
    return f"{finding.get('file', '')}::{title}"


def read(slug: str, branch: str) -> list[dict]:
    path = log_path(slug, branch)
    if not path.exists():
        return []
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line]


def record(slug: str, branch: str, run_id: str, finding: dict, decision: str, note: str) -> dict:
    entry = {
        "at": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "finding": finding_key(finding),
        "title": finding.get("title", ""),
        "file": finding.get("file", ""),
        "severity": finding.get("severity", ""),
        "decision": decision,
        "note": note,
        "run": run_id,
    }
    path = log_path(slug, branch)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as handle:
        handle.write(json.dumps(entry, ensure_ascii=True) + "\n")
    return entry


def closed_keys(slug: str, branch: str) -> set[str]:
    """Findings the owner kept or dropped. A 'fix' decision is closed once fixed and
    re-reviewed, which the next review round shows; it is not filtered here."""
    return {d["finding"] for d in read(slug, branch) if d["decision"] in ("keep", "drop")}
