"""Refuse to spawn into a repository the agent has not trusted.

A session spawned there opens on the agent's "trust this folder?" question, the
Director types the brief prompt into it, and the session quits within seconds
(issue 2898). Both agents decide trust for the whole repository, worktrees included,
so the check is made on the main checkout's folder.
"""

from __future__ import annotations

import json
import tomllib
from pathlib import Path

from .errors import ShipError


def _norm(path: str | Path) -> str:
    return str(path).replace("\\", "/").rstrip("/").lower()


def claude_trusts(repo_root: Path, home: Path | None = None) -> bool:
    path = (home or Path.home()) / ".claude.json"
    if not path.exists():
        return False
    projects = json.loads(path.read_text(encoding="utf-8")).get("projects", {})
    wanted = _norm(repo_root)
    return any(_norm(k) == wanted and v.get("hasTrustDialogAccepted") is True
               for k, v in projects.items())


def codex_trusts(repo_root: Path, home: Path | None = None) -> bool:
    path = (home or Path.home()) / ".codex" / "config.toml"
    if not path.exists():
        return False
    projects = tomllib.loads(path.read_text(encoding="utf-8")).get("projects", {})
    wanted = _norm(repo_root)
    return any(_norm(k) == wanted and v.get("trust_level") == "trusted"
               for k, v in projects.items())


CHECKS = {"ClaudeCode": claude_trusts, "Codex": codex_trusts}

FIXES = {
    "ClaudeCode": "Open Claude Code once in {root} and choose 'Yes, I trust this folder'.",
    "Codex": 'Add to ~/.codex/config.toml:  [projects."{root}"]  trust_level = "trusted"',
}


def require_trust(agent: str, repo_root: Path) -> None:
    check = CHECKS.get(agent)
    if check is None:
        raise ShipError(
            "agent-unsupported", f"cc-ship cannot check whether {agent} trusts {repo_root}.",
            "Use ClaudeCode or Codex as the reviewer and verifier agents in .ship.yaml.",
        )
    if not check(repo_root):
        raise ShipError(
            "repo-not-trusted",
            f"{agent} has not trusted {repo_root}; a session spawned there would quit at once.",
            FIXES[agent].format(root=repo_root) + " Then run: cc-ship continue",
        )
