"""GitHub through the gh command line. No polling: every call is one look."""

from __future__ import annotations

import json
import subprocess
from pathlib import Path

from errors import ShipError


def _gh(args: list[str], check: bool = True) -> subprocess.CompletedProcess:
    proc = subprocess.run(["gh", *args], capture_output=True, text=True,
                          encoding="utf-8", errors="replace")
    if check and proc.returncode != 0:
        raise ShipError(
            "gh-failed",
            f"gh {' '.join(args[:3])} failed (exit {proc.returncode}): "
            f"{(proc.stderr or proc.stdout).strip()}",
            "Check 'gh auth status' and the repository, then run: cc-ship continue",
        )
    return proc


def open_pr_for_branch(slug: str, branch: str) -> dict | None:
    out = _gh(["pr", "list", "--repo", slug, "--head", branch, "--state", "open",
               "--json", "number,url"]).stdout
    prs = json.loads(out)
    return prs[0] if prs else None


def create_pr(slug: str, branch: str, title: str, body_file: Path) -> dict:
    _gh(["pr", "create", "--repo", slug, "--base", "main", "--head", branch,
         "--title", title, "--body-file", str(body_file)])
    pr = open_pr_for_branch(slug, branch)
    if pr is None:
        raise ShipError("pr-missing", f"gh created a pull request for {branch} but it cannot be found.",
                        "Check GitHub, then run: cc-ship continue")
    return pr


def edit_pr_body(slug: str, number: int, body_file: Path) -> None:
    _gh(["pr", "edit", str(number), "--repo", slug, "--body-file", str(body_file)])


def check_buckets(slug: str, number: int) -> list[dict]:
    """Every check on the pull request with its bucket: pass, fail, pending, skipping, cancel.
    No checks at all is an empty list, not an error: merge never waits for checks."""
    proc = _gh(["pr", "checks", str(number), "--repo", slug, "--json", "name,bucket"], check=False)
    text = (proc.stdout or "").strip()
    if not text:
        if "no checks reported" in (proc.stderr or ""):
            return []
        raise ShipError("gh-failed", f"gh pr checks failed: {(proc.stderr or '').strip()}",
                        "Check 'gh auth status', then run: cc-ship continue")
    return json.loads(text)


def failed_checks(slug: str, number: int) -> list[str]:
    return [c["name"] for c in check_buckets(slug, number) if c["bucket"] in ("fail", "cancel")]


def pr_state(slug: str, number: int) -> dict:
    out = _gh(["pr", "view", str(number), "--repo", slug,
               "--json", "state,mergedAt,mergeCommit,headRefOid,url"]).stdout
    return json.loads(out)


def merge_pr(slug: str, number: int, head_sha: str, subject: str, body: str) -> None:
    # --match-head-commit: merge exactly the head that was reviewed and verified.
    _gh(["pr", "merge", str(number), "--repo", slug, "--squash",
         "--match-head-commit", head_sha, "--subject", subject, "--body", body])


def delete_remote_branch(slug: str, branch: str) -> None:
    proc = _gh(["api", "-X", "DELETE", f"repos/{slug}/git/refs/heads/{branch}"], check=False)
    # 422 "Reference does not exist": the repository already deletes merged branches.
    if proc.returncode != 0 and "Reference does not exist" not in (proc.stdout + proc.stderr):
        raise ShipError("branch-delete-failed",
                        f"The pull request merged but deleting {branch} failed: {proc.stderr.strip()}",
                        f"Delete it by hand: git push origin --delete {branch}")

