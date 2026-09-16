"""Git operations cc-ship needs. Every call is explicit; nothing is retried."""

from __future__ import annotations

import subprocess
from dataclasses import dataclass
from pathlib import Path

from errors import ShipError

MAIN = "main"
REMOTE_MAIN = "origin/main"


@dataclass
class GitResult:
    code: int
    out: str
    err: str


def run(repo: Path, *args: str, check: bool = True, env: dict | None = None,
        input_text: str | None = None) -> GitResult:
    proc = subprocess.run(
        ["git", "-C", str(repo), *args], capture_output=True, text=True,
        env=env, input=input_text, encoding="utf-8", errors="replace",
    )
    result = GitResult(proc.returncode, proc.stdout, proc.stderr)
    if check and proc.returncode != 0:
        raise ShipError(
            "git-failed",
            f"git {' '.join(args)} failed (exit {proc.returncode}): {proc.stderr.strip()}",
            "Fix the repository state shown above, then run: cc-ship continue",
        )
    return result


def toplevel(cwd: Path) -> Path:
    result = subprocess.run(["git", "-C", str(cwd), "rev-parse", "--show-toplevel"],
                            capture_output=True, text=True)
    if result.returncode != 0:
        raise ShipError("not-a-repo", f"{cwd} is not inside a git repository.",
                        "Run cc-ship from inside the worktree that holds your change.")
    return Path(result.stdout.strip())


def main_repo_root(repo: Path) -> Path:
    """The main checkout of this repository, even when repo is a worktree of it."""
    common = run(repo, "rev-parse", "--path-format=absolute", "--git-common-dir").out.strip()
    return Path(common).parent


def branch(repo: Path) -> str:
    name = run(repo, "rev-parse", "--abbrev-ref", "HEAD").out.strip()
    if name == "HEAD":
        raise ShipError("detached-head", "HEAD is detached; cc-ship ships a branch.",
                        "Create a branch for your change: git switch -c <name>")
    return name


def head(repo: Path) -> str:
    return run(repo, "rev-parse", "HEAD").out.strip()


def is_clean(repo: Path) -> bool:
    """Nothing uncommitted at all, untracked files included: a file the checks or the
    verifier can use but that is not committed would pass here and be missing from the
    merge. Ignored files do not count. Keep intent.md outside the worktree."""
    return run(repo, "status", "--porcelain").out.strip() == ""


def rebase_in_progress(repo: Path) -> bool:
    git_dir = Path(run(repo, "rev-parse", "--path-format=absolute", "--git-dir").out.strip())
    return (git_dir / "rebase-merge").exists() or (git_dir / "rebase-apply").exists()


def fetch(repo: Path) -> None:
    run(repo, "fetch", "--prune", "origin")


def behind_main(repo: Path) -> int:
    return int(run(repo, "rev-list", "--count", f"HEAD..{REMOTE_MAIN}").out.strip())


def ahead_of_main(repo: Path) -> int:
    return int(run(repo, "rev-list", "--count", f"{REMOTE_MAIN}..HEAD").out.strip())


def rebase_onto_main(repo: Path) -> None:
    """Rebase; on a conflict, put the tree back exactly as it was and say so."""
    result = run(repo, "rebase", REMOTE_MAIN, check=False)
    if result.code != 0:
        conflicted = run(repo, "diff", "--name-only", "--diff-filter=U", check=False).out.split()
        run(repo, "rebase", "--abort", check=False)
        raise ShipError(
            "rebase-conflict",
            "Rebasing onto origin/main conflicts in: " + (", ".join(conflicted) or "(unknown files)"),
            "Run: git rebase origin/main - resolve the conflicts, finish the rebase, "
            "then run: cc-ship continue",
        )


def show_main_file(repo: Path, path: str) -> str | None:
    result = run(repo, "show", f"{REMOTE_MAIN}:{path}", check=False)
    return result.out if result.code == 0 else None


def diff_text(repo: Path, base: str, head_sha: str) -> str:
    return run(repo, "diff", f"{base}..{head_sha}").out


def changed_files(repo: Path, base: str, head_sha: str) -> list[str]:
    out = run(repo, "diff", "--name-only", f"{base}..{head_sha}").out
    return [line for line in out.splitlines() if line.strip()]


def numstat(repo: Path, base: str, head_sha: str) -> tuple[int, int]:
    """(added, deleted) lines; binary files count as zero."""
    added = deleted = 0
    for line in run(repo, "diff", "--numstat", f"{base}..{head_sha}").out.splitlines():
        parts = line.split("\t")
        if len(parts) >= 2 and parts[0].isdigit() and parts[1].isdigit():
            added += int(parts[0])
            deleted += int(parts[1])
    return added, deleted


def commit_subjects(repo: Path, base: str, head_sha: str) -> list[str]:
    out = run(repo, "log", "--reverse", "--format=%s", f"{base}..{head_sha}").out
    return [line for line in out.splitlines() if line.strip()]


def merge_base(repo: Path) -> str:
    return run(repo, "merge-base", REMOTE_MAIN, "HEAD").out.strip()


def push_branch(repo: Path, name: str) -> None:
    # The branch was rebased, so a plain push may be refused; the lease makes sure we
    # only replace what we last saw on the remote.
    run(repo, "push", "--force-with-lease", "-u", "origin", f"{name}:{name}")


def remote_slug(repo: Path) -> str:
    url = run(repo, "remote", "get-url", "origin").out.strip()
    slug = url
    for prefix in ("https://github.com/", "git@github.com:", "ssh://git@github.com/"):
        if slug.startswith(prefix):
            slug = slug[len(prefix):]
    slug = slug.removesuffix(".git")
    if slug.count("/") != 1:
        raise ShipError("unknown-remote", f"origin is not a GitHub repository: {url}",
                        "cc-ship ships to GitHub; set origin to the GitHub repository.")
    return slug
