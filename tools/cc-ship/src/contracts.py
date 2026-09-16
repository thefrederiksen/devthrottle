"""The two files spawned sessions write for cc-ship: review.json and verify.json.

Validation is hand-written (no third-party schema library) so cc-ship runs on a
bare Python on Windows and macOS. Each validator returns a list of plain-English
problems; an empty list means the file is valid. The same text is what the
session is shown when it is asked to correct its file.
"""

from __future__ import annotations

import json
from pathlib import Path

RISK_LEVELS = ("low", "medium", "high")
ACTIONS = ("auto-fix", "ask-owner", "note")
SEVERITIES = ("error", "warning", "info")
RESULTS = ("pass", "fail", "untested")
VERDICTS = ("go", "no-go", "inconclusive", "no-surface")

REVIEW_EXAMPLE = {
    "risk_level": "low",
    "risk_reason": "One sentence on why.",
    "checked": ["What you checked, one item per entry."],
    "not_covered": ["What those checks do not cover."],
    "simplification": ["Each branch, flag, fallback, alias or duplicated rule the change adds."],
    "findings": [
        {
            "id": "F1",
            "title": "Short statement of the defect.",
            "file": "path/relative/to/repo.py",
            "line": 12,
            "sequence": "The real sequence, in intended use, that produces the wrong result.",
            "severity": "error",
            "action": "auto-fix",
            "remedy": "What should change.",
        }
    ],
}


def _text(value: object) -> bool:
    return isinstance(value, str) and value.strip() != ""


def _text_list(obj: dict, key: str, problems: list[str], where: str = "") -> None:
    value = obj.get(key)
    if not isinstance(value, list) or not all(isinstance(v, str) for v in value):
        problems.append(f"{where}'{key}' must be a list of strings")


def load_json(path: Path) -> tuple[dict | None, list[str]]:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        return None, [f"file is not readable JSON: {exc}"]
    if not isinstance(data, dict):
        return None, ["the top level must be a JSON object"]
    return data, []


def validate_review(data: dict) -> list[str]:
    problems: list[str] = []
    if data.get("risk_level") not in RISK_LEVELS:
        problems.append(f"'risk_level' must be one of {', '.join(RISK_LEVELS)}")
    if not _text(data.get("risk_reason")):
        problems.append("'risk_reason' must be a non-empty sentence")
    for key in ("checked", "not_covered", "simplification"):
        _text_list(data, key, problems)
    if not data.get("checked"):
        problems.append("'checked' must name at least one thing you checked")

    findings = data.get("findings")
    if not isinstance(findings, list):
        problems.append("'findings' must be a list (use [] when there are none)")
        return problems
    seen: set[str] = set()
    for i, f in enumerate(findings):
        where = f"findings[{i}]: "
        if not isinstance(f, dict):
            problems.append(f"{where}must be an object")
            continue
        for key in ("id", "title", "file", "sequence", "remedy"):
            if not _text(f.get(key)):
                problems.append(f"{where}'{key}' must be a non-empty string")
        finding_id = f.get("id")
        if isinstance(finding_id, str):
            if finding_id in seen:
                problems.append(f"{where}'id' {finding_id!r} is used twice")
            seen.add(finding_id)
        line = f.get("line")
        if line is not None and (not isinstance(line, int) or isinstance(line, bool) or line < 1):
            problems.append(f"{where}'line' must be a positive whole number or null")
        if f.get("severity") not in SEVERITIES:
            problems.append(f"{where}'severity' must be one of {', '.join(SEVERITIES)}")
        same = f.get("same_as_decision")
        if same is not None and not (isinstance(same, str) and same.startswith("D")
                                     and same[1:].isdigit()):
            problems.append(f"{where}'same_as_decision' must be a decision id like \"D1\", or absent")
        # A missing action is allowed and is treated as ask-owner; a wrong one is not.
        if "action" in f and f["action"] not in ACTIONS:
            problems.append(f"{where}'action' must be one of {', '.join(ACTIONS)}")
    return problems


def finding_action(finding: dict) -> str:
    """A finding with no action counts as ask-owner."""
    return finding.get("action") or "ask-owner"


def validate_verify(data: dict, surface_available: bool) -> list[str]:
    """surface_available: cc-ship handed the verifier something to run (a preview, a build).
    With a surface, 'no-surface' is never true: failing to drive it is 'inconclusive'."""
    problems: list[str] = []
    scenarios = data.get("scenarios")
    if not isinstance(scenarios, list) or not scenarios:
        problems.append("'scenarios' must be a list with at least one entry")
        scenarios = []
    verdict = data.get("verdict")
    if verdict not in VERDICTS:
        problems.append(f"'verdict' must be one of {', '.join(VERDICTS)}")

    for i, s in enumerate(scenarios):
        where = f"scenarios[{i}]: "
        if not isinstance(s, dict):
            problems.append(f"{where}must be an object")
            continue
        if not _text(s.get("name")):
            problems.append(f"{where}'name' must be a non-empty string")
        if s.get("result") not in RESULTS:
            problems.append(f"{where}'result' must be one of {', '.join(RESULTS)}")
        if not isinstance(s.get("live"), bool):
            problems.append(f"{where}'live' must be true or false")
        if not isinstance(s.get("evidence"), str) or not isinstance(s.get("reason"), str):
            problems.append(f"{where}'evidence' and 'reason' must both be strings")
        if s.get("result") in ("pass", "fail") and not _text(s.get("evidence")):
            problems.append(f"{where}a {s.get('result')} needs evidence")
        if s.get("result") in ("pass", "fail") and s.get("live") is not True:
            problems.append(f"{where}a {s.get('result')} must have been run live - otherwise it is untested")
        if s.get("result") == "untested" and s.get("live") is not False:
            problems.append(f"{where}an untested scenario cannot be live")
        if s.get("result") == "untested" and not _text(s.get("reason")):
            problems.append(f"{where}an untested scenario needs a reason naming what was missing")

    results = [s.get("result") for s in scenarios if isinstance(s, dict)]
    if "fail" in results and verdict != "no-go":
        problems.append("a failed scenario forces verdict 'no-go'")
    live_passes = [s for s in scenarios
                   if isinstance(s, dict) and s.get("result") == "pass" and s.get("live") is True]
    if verdict == "go" and not live_passes:
        problems.append("verdict 'go' needs at least one scenario that passed live; "
                        "use 'inconclusive' when a surface exists but could not be driven")
    if verdict == "no-surface" and surface_available:
        problems.append("verdict 'no-surface' is wrong: a surface was provided; "
                        "use 'inconclusive' if it could not be driven")
    if verdict == "no-surface" and not all(
        isinstance(s, dict) and s.get("result") == "untested" and s.get("live") is False
        for s in scenarios
    ):
        problems.append("verdict 'no-surface' is valid only when every scenario is untested and not live")
    return problems
