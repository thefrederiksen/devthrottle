"""Probe for issue 2935, assumption 2: a reviewer session of another agent family
writes a schema-valid review.json from a FILE brief, and how often the correction
turn is needed.

Usage: python probe_review_contract.py <work-folder> <repo> <base> <head> <agent> <runs> <sabotaged>
Each run gets a fresh session. The last <sabotaged> runs have their first file replaced
with an invalid one, to prove the correction turn works; they are not counted in the rate. Writes <work-folder>/summary.json.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
import briefs  # noqa: E402
import contracts  # noqa: E402
import fleet  # noqa: E402

AUTHOR = os.environ["CC_SESSION_ID"]
MAX_CORRECTIONS = 2

INTENT = """# Intent

Goal: on the admin usage screens, show each account's share of all sessions as a whole
percentage (for example 3 of 8 sessions shows "38%"), using a shared formatter.

Constraints: formatting only; no new data, no API change. Show nothing when there is no total.

Ruled out: charts or colour changes.
"""

RULES = [
    "No secrets and no customer data in code, commits or public copy.",
    "No claims about unshipped features, and no AI provider names, in public copy.",
]


SABOTAGE = '{"risk_level": "severe", "findings": "none"}'


def one_run(work: Path, index: int, repo: Path, base: str, head: str, agent: str,
            sabotage: bool = False) -> dict:
    folder = work / f"run-{index}"
    folder.mkdir(parents=True, exist_ok=True)
    intent = folder / "intent.md"
    intent.write_text(INTENT, encoding="ascii")
    diff = folder / "diff.patch"
    diff.write_text(
        subprocess.run(["git", "-C", str(repo), "diff", f"{base}..{head}"],
                       capture_output=True, text=True, check=True).stdout,
        encoding="utf-8",
    )
    output = folder / "review-1.json"
    brief = folder / "brief-1.md"
    brief.write_text(briefs.reviewer_brief(
        repo=repo, base=base, head=head, intent=intent, diff=diff, decisions=None,
        output=output, repo_rules=RULES, first_reviewed_head=None,
    ), encoding="ascii")

    # Stagger spawns: a burst of simultaneous spawns left new sessions unable to
    # authenticate to the Gateway (first attempt of this probe).
    time.sleep(20 * ((index - 1) % 3))
    started = time.monotonic()
    session_id = fleet.spawn_session(
        repo, agent, AUTHOR, f"cc-ship - Reviewer - probe run {index}", brief
    )
    record: dict = {"run": index, "session_id": session_id, "sabotaged": sabotage,
                    "attempts": []}
    written_after = None
    for attempt in range(MAX_CORRECTIONS + 1):
        result = fleet.wait_for_output(session_id, output, 900, poll_seconds=5,
                                       written_after=written_after)
        entry = {"attempt": attempt, "outcome": result.outcome, "reason": result.reason}
        record["attempts"].append(entry)
        if result.outcome != fleet.FINISHED:
            break
        if sabotage and attempt == 0:
            # Mechanism test only: replace the first file with an invalid one.
            output.write_text(SABOTAGE, encoding="ascii")
            entry["sabotaged"] = True
        data, problems = contracts.load_json(output)
        if data is not None:
            problems = contracts.validate_review(data)
        entry["problems"] = problems
        if not problems:
            record["review"] = data
            break
        if attempt == MAX_CORRECTIONS:
            break
        # Invalid: keep the session alive, then hand it the problems as a file.
        entry["undo"] = fleet.clear_done_flag(session_id).strip()
        fix = folder / f"correction-{attempt + 1}.md"
        fix.write_text(briefs.correction_brief(output, problems, attempt + 1, "reviewer"), encoding="ascii")
        written_after = time.time()
        fleet.prompt_session(session_id, f"Read the file {fix} and follow it exactly.")

    record["valid"] = "review" in record
    record["corrections"] = len(record["attempts"]) - 1
    record["seconds"] = round(time.monotonic() - started)
    findings = record.get("review", {}).get("findings", [])
    record["found_planted_defect"] = any(
        "fmtShare" in json.dumps(f) or "round" in json.dumps(f).lower() for f in findings
    )
    record["actions"] = [contracts.finding_action(f) for f in findings]
    (folder / "record.json").write_text(json.dumps(record, indent=1), encoding="utf-8")
    return record


def main() -> None:
    work, repo, base, head, agent, runs, sabotaged = sys.argv[1:8]
    work_path, repo_path = Path(work), Path(repo)
    total = int(runs) + int(sabotaged)
    with ThreadPoolExecutor(max_workers=3) as pool:
        records = list(pool.map(
            lambda i: one_run(work_path, i, repo_path, base, head, agent,
                              sabotage=i > int(runs)),
            range(1, total + 1),
        ))
    summary = [{k: r.get(k) for k in ("run", "session_id", "sabotaged", "valid", "corrections",
                                      "seconds", "found_planted_defect", "actions")}
               | {"outcomes": [a["outcome"] for a in r["attempts"]],
                  "problems": [a.get("problems") for a in r["attempts"]]}
               for r in records]
    (work_path / "summary.json").write_text(json.dumps(summary, indent=1), encoding="utf-8")
    for s in summary:
        print(json.dumps(s))


if __name__ == "__main__":
    main()
