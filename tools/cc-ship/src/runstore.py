"""Where a run lives on disk, and the run record itself.

<data>/ship/runs/<run-id>/run.json       the run record (this module)
<data>/ship/runs/<run-id>/intent.md      the owner's intent, copied at start
<data>/ship/runs/<run-id>/brief-*.md     each session's brief
<data>/ship/runs/<run-id>/review-*.json  each review round
<data>/ship/runs/<run-id>/verify.json    the verifier's result
<data>/ship/runs/<run-id>/evidence/      screenshots and other evidence
<data>/ship/runs/<run-id>/pr-body.md     the pull request text
<data>/ship/decisions/<repo>/<branch>.jsonl  the owner's decisions (decisions.py)
"""

from __future__ import annotations

import json
import os
import sys
import time
import uuid
from pathlib import Path

# Installed, cc-storage is a declared dependency and is already importable. Run from a checkout,
# it is the sibling folder tools/cc_storage, which is not on the path - add it then, and only then.
_tools_dir = Path(__file__).resolve().parents[2]
if (_tools_dir / "cc_storage").is_dir() and str(_tools_dir) not in sys.path:
    sys.path.insert(0, str(_tools_dir))

from cc_storage.storage import CcStorage  # noqa: E402

# The one honest state a run is in.
WORKING = "working"                    # a spawned session is working; cc-ship wait
WAITING_ON_AUTHOR = "waiting-on-author"  # the author must fix something, then continue
WAITING_ON_OWNER = "waiting-on-owner"  # the owner must answer, or merge a parked PR
FAILED = "failed"                      # a step could not run; see failure and next_step
MERGED = "merged"
ABORTED = "aborted"
FINAL_STATES = (MERGED, ABORTED)

STEPS = ("sync", "checks", "review", "verify")
PENDING, COMPLETED, SKIPPED = "pending", "completed", "skipped"


def ship_root() -> Path:
    return CcStorage._base() / "ship"


def runs_root() -> Path:
    return ship_root() / "runs"


def new_run(repo: Path, branch: str, slug: str, author_session: str, author_agent: str,
            mission: str | None, intent_text: str) -> dict:
    run_id = time.strftime("%Y%m%d-%H%M%S-") + uuid.uuid4().hex[:6]
    folder = runs_root() / run_id
    (folder / "evidence").mkdir(parents=True)
    (folder / "intent.md").write_text(intent_text, encoding="utf-8")
    return {
        "id": run_id,
        "repo": str(repo),
        "slug": slug,
        "branch": branch,
        "author_session": author_session,
        "author_agent": author_agent,
        "mission": mission,
        "created": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "state": WORKING,
        "phase": "sync",
        "steps": {step: PENDING for step in STEPS},
        "review_round": 0,
        "fix_rounds": 0,
        "first_reviewed_head": None,
        "session": None,          # the spawned session cc-ship is waiting on
        "sessions": [],           # every session this run spawned
        "findings": [],           # open findings from the latest review
        "history": [],            # one line per review round, for the pull request
        "verify": None,
        "risk": None,
        "pr": None,
        "failure": None,
        "next_step": "",
    }


def folder(run: dict) -> Path:
    return runs_root() / run["id"]


def save(run: dict) -> None:
    path = folder(run) / "run.json"
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(run, indent=1, ensure_ascii=True), encoding="ascii")
    os.replace(tmp, path)


def load(run_id: str) -> dict:
    return json.loads((runs_root() / run_id / "run.json").read_text(encoding="ascii"))


def find_active(repo: Path, branch: str) -> dict | None:
    """The one unfinished run for this worktree and branch, if any."""
    if not runs_root().exists():
        return None
    found = []
    for path in sorted(runs_root().glob("*/run.json")):
        run = json.loads(path.read_text(encoding="ascii"))
        if (run["repo"] == str(repo) and run["branch"] == branch
                and run["state"] not in FINAL_STATES):
            found.append(run)
    if len(found) > 1:
        from .errors import ShipError
        raise ShipError(
            "several-runs",
            f"{len(found)} unfinished runs exist for {branch}: " + ", ".join(r["id"] for r in found),
            "Abort the ones you do not want: cc-ship abort --run <id>",
        )
    return found[0] if found else None
