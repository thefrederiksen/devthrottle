"""Publish a run's evidence files to the orphan `ship-evidence` branch (decision D6).

The branch never merges. Files are added with git plumbing and a private index, so
nothing is checked out and the author's worktree is never touched. Links are pinned
to the evidence commit so they never change.
"""

from __future__ import annotations

import os
import tempfile
from pathlib import Path

import gitops
from errors import ShipError

BRANCH = "ship-evidence"


def publish(repo: Path, slug: str, run_id: str, files: list[Path]) -> dict[str, str]:
    """Returns {file name: permanent URL}. No files, no commit."""
    if not files:
        return {}
    fetched = gitops.run(repo, "fetch", "origin", f"+refs/heads/{BRANCH}:refs/remotes/origin/{BRANCH}",
                         check=False)
    parent = None
    if fetched.code == 0:
        parent = gitops.run(repo, "rev-parse", f"refs/remotes/origin/{BRANCH}").out.strip()
    elif "couldn't find remote ref" not in fetched.err:
        raise ShipError("evidence-fetch-failed", f"Fetching {BRANCH} failed: {fetched.err.strip()}",
                        "Check the network, then run: cc-ship continue")

    with tempfile.TemporaryDirectory() as tmp:
        env = dict(os.environ, GIT_INDEX_FILE=str(Path(tmp) / "index"))
        if parent:
            gitops.run(repo, "read-tree", parent, env=env)
        else:
            gitops.run(repo, "read-tree", "--empty", env=env)
        for path in files:
            blob = gitops.run(repo, "hash-object", "-w", str(path), env=env).out.strip()
            gitops.run(repo, "update-index", "--add", "--cacheinfo",
                       f"100644,{blob},runs/{run_id}/{path.name}", env=env)
        tree = gitops.run(repo, "write-tree", env=env).out.strip()
    args = ["commit-tree", tree, "-m", f"Evidence for run {run_id}"]
    if parent:
        args[2:2] = ["-p", parent]
    commit = gitops.run(repo, *args).out.strip()
    pushed = gitops.run(repo, "push", "origin", f"{commit}:refs/heads/{BRANCH}", check=False)
    if pushed.code != 0:
        raise ShipError(
            "evidence-push-failed",
            f"Publishing evidence to {BRANCH} failed (another run may have published at the "
            f"same moment): {pushed.err.strip()}",
            "Run: cc-ship continue",
        )
    return {path.name: f"https://github.com/{slug}/blob/{commit}/runs/{run_id}/{path.name}"
            for path in files}
