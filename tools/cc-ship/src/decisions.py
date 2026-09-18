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

from . import runstore

DECISIONS = ("fix", "keep", "drop")


def log_path(slug: str, branch: str) -> Path:
    safe_branch = re.sub(r"[^A-Za-z0-9._-]+", "_", branch)
    safe_repo = slug.replace("/", "__")
    return runstore.ship_root() / "decisions" / safe_repo / f"{safe_branch}.jsonl"


def _words(value: object) -> str:
    return re.sub(r"[^a-z0-9]+", " ", str(value or "").lower()).strip()


def finding_key(finding: dict) -> str:
    """The same place and title: a SIMILAR finding. Never enough to suppress one."""
    return f"{finding.get('file', '')}::{_words(finding.get('title'))}"


def exact_key(finding: dict) -> str:
    """The same place, title and failing sequence: the SAME owner call."""
    return f"{finding_key(finding)}::{_words(finding.get('sequence'))}"


def read(slug: str, branch: str) -> list[dict]:
    path = log_path(slug, branch)
    if not path.exists():
        return []
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line]


def record(slug: str, branch: str, run_id: str, finding: dict, decision: str, note: str) -> dict:
    entry = {
        "at": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "finding": exact_key(finding),
        "similar": finding_key(finding),
        "sequence": finding.get("sequence", ""),
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


def listed(slug: str, branch: str) -> list[dict]:
    """Every decision with a stable id (D1, D2, ...) in the order it was made."""
    return [dict(d, id=f"D{i}") for i, d in enumerate(read(slug, branch), start=1)]


def closed(slug: str, branch: str) -> tuple[set[str], dict[str, dict]]:
    """(exact keys the owner kept or dropped, similar key -> that decision).

    Only an exact match is suppressed. A finding that merely looks like a decided one
    goes to the owner again, marked as similar, because it may be a different defect.
    A 'fix' decision closes once the fix is re-reviewed, so it is not listed here."""
    done = [d for d in read(slug, branch) if d["decision"] in ("keep", "drop")]
    return {d["finding"] for d in done}, {d.get("similar", ""): d for d in done}
