"""The repository's .ship.yaml, always read from origin/main.

A branch cannot weaken its own gate: the checks and risk rules that judge a change
come from main, never from the change. The file is written in JSON syntax, which is
valid YAML, so cc-ship needs nothing outside the Python standard library.

Shape:
{
  "checks": ["npm --prefix website run lint", "..."],
  "rules": ["No secrets ...", "..."],
  "reviewer_agent": "Codex",
  "reviewer_model": "claude-fable-5-1",      (optional; ClaudeCode only; full model id)
  "verifier_agent": "ClaudeCode",
  "verify": {"surface": "vercel-preview" | "none" | "skip"},
  "docs_only_paths": ["*.md", "docs/**"],
  "risk": {"high_paths": ["website/api/**", "..."]}
}
"""

from __future__ import annotations

import fnmatch
import json
import re
from dataclasses import dataclass, field
from pathlib import Path

import gitops
from errors import ShipError

FILE_NAME = ".ship.yaml"
AGENTS = ("ClaudeCode", "Codex", "Gemini", "OpenCode", "Grok", "Copilot", "Pi")
SURFACES = ("vercel-preview", "none", "skip")

@dataclass
class ShipConfig:
    checks: list[str]
    rules: list[str]
    reviewer_agent: str | None
    verifier_agent: str
    surface: str
    docs_only_paths: list[str]
    high_paths: list[str] = field(default_factory=list)
    reviewer_model: str | None = None


def _bad(detail: str) -> ShipError:
    return ShipError(
        "bad-ship-config", f"{FILE_NAME} on origin/main is invalid: {detail}",
        f"Fix {FILE_NAME} in its own pull request to main; cc-ship only reads it from main.",
    )


def _string_list(data: dict, key: str) -> list[str]:
    value = data.get(key, [])
    if not isinstance(value, list) or not all(isinstance(v, str) and v.strip() for v in value):
        raise _bad(f"'{key}' must be a list of non-empty strings")
    return value


def parse(text: str) -> ShipConfig:
    try:
        data = json.loads(text)
    except json.JSONDecodeError as exc:
        raise _bad(f"not readable (write it in JSON syntax): {exc}") from exc
    if not isinstance(data, dict):
        raise _bad("the top level must be an object")
    known = {"checks", "rules", "reviewer_agent", "verifier_agent", "verify",
             "docs_only_paths", "risk", "reviewer_model"}
    unknown = sorted(set(data) - known)
    if unknown:
        raise _bad(f"unknown keys: {', '.join(unknown)}")
    reviewer = data.get("reviewer_agent")
    if reviewer is not None and reviewer not in AGENTS:
        raise _bad(f"'reviewer_agent' must be one of {', '.join(AGENTS)}")
    verifier = data.get("verifier_agent", "ClaudeCode")
    if verifier not in AGENTS:
        raise _bad(f"'verifier_agent' must be one of {', '.join(AGENTS)}")
    verify = data.get("verify", {})
    if not isinstance(verify, dict) or verify.get("surface") not in SURFACES:
        raise _bad(f"'verify.surface' must be one of {', '.join(SURFACES)}")
    risk = data.get("risk", {})
    if not isinstance(risk, dict):
        raise _bad("'risk' must be an object")
    reviewer_model = data.get("reviewer_model")
    if reviewer_model is not None:
        # A full id only: the author's model is reported as a full id, and an alias such
        # as "opus" could name the author's own model without the comparison noticing.
        if not (isinstance(reviewer_model, str)
                and re.fullmatch(r"claude-[a-z0-9.-]+(\[[a-z0-9]+\])?", reviewer_model, re.I)):
            raise _bad("'reviewer_model' must be a full model id such as claude-fable-5-1; an "
                       "alias such as opus cannot be compared with the author's model")
        if reviewer != "ClaudeCode":
            raise _bad("'reviewer_model' can be set only when reviewer_agent is ClaudeCode")
    return ShipConfig(
        checks=_string_list(data, "checks"),
        rules=_string_list(data, "rules"),
        reviewer_agent=reviewer,
        verifier_agent=verifier,
        surface=verify["surface"],
        docs_only_paths=_string_list(data, "docs_only_paths"),
        high_paths=_string_list(risk, "high_paths"),
        reviewer_model=reviewer_model,
    )


def load_from_main(repo: Path) -> ShipConfig:
    text = gitops.show_main_file(repo, FILE_NAME)
    if text is None:
        raise ShipError(
            "no-ship-config", f"origin/main has no {FILE_NAME}.",
            f"Add {FILE_NAME} to main in its own pull request first (see tools/cc-ship/README.md).",
        )
    return parse(text)


def matches(path: str, patterns: list[str] | tuple[str, ...]) -> bool:
    lowered = path.lower()
    for pattern in patterns:
        pat = pattern.lower()
        if fnmatch.fnmatchcase(lowered, pat):
            return True
        # "dir/**" also matches files directly and deeply under dir.
        if pat.endswith("/**") and lowered.startswith(pat[:-2]):
            return True
        # A bare file pattern like "*.md" matches in any folder.
        if "/" not in pat and fnmatch.fnmatchcase(lowered.rsplit("/", 1)[-1], pat):
            return True
    return False


def is_docs_only(files: list[str], cfg: ShipConfig) -> bool:
    return bool(files) and bool(cfg.docs_only_paths) and all(
        matches(f, cfg.docs_only_paths) for f in files
    )
