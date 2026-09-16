"""Probe for issue 2935, assumption 3: a verifier session can drive a Vercel preview
of devthrottle_internal unattended and capture a screenshot into the run folder.

Usage: python probe_verify_preview.py <work-folder> <repo> <commit-with-a-preview> <agent>
"""

from __future__ import annotations

import json
import os
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
import briefs  # noqa: E402
import contracts  # noqa: E402
import fleet  # noqa: E402
import preview  # noqa: E402

INTENT = """# Intent

Goal: a visitor who opens the DevThrottle home page sees what the product is and a
clear way to create a free account, and can reach the pricing page from the header.

Constraints: none. Ruled out: nothing.
"""


def main() -> None:
    work, repo, sha, agent = Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[3], sys.argv[4]
    work.mkdir(parents=True, exist_ok=True)
    evidence = work / "evidence"
    evidence.mkdir(exist_ok=True)
    intent = work / "intent.md"
    intent.write_text(INTENT, encoding="ascii")

    url = preview.find_preview_url("thefrederiksen/devthrottle_internal", sha)
    if url is None:
        raise SystemExit(f"no successful preview for {sha}")
    state = work / "browser-state.json"
    preview.write_bypass_state(url, state)

    output = work / "verify.json"
    brief = work / "brief-verify.md"
    brief.write_text(briefs.verifier_brief(
        repo=repo, intent=intent, preview_url=url, browser_state=state,
        evidence_dir=evidence, output=output,
    ), encoding="ascii")

    started = time.monotonic()
    session_id = fleet.spawn_session(
        repo, agent, os.environ["CC_SESSION_ID"], "cc-ship - Verifier - probe preview", brief
    )
    result = fleet.wait_for_output(session_id, output, 900, poll_seconds=5)
    record = {"session_id": session_id, "outcome": result.outcome, "reason": result.reason,
              "seconds": round(time.monotonic() - started), "preview": url,
              "evidence_files": sorted(p.name for p in evidence.iterdir())}
    if result.outcome == fleet.FINISHED:
        data, problems = contracts.load_json(output)
        record["problems"] = problems if data is None else contracts.validate_verify(data)
        record["verify"] = data
    (work / "record.json").write_text(json.dumps(record, indent=1), encoding="utf-8")
    print(json.dumps({k: v for k, v in record.items() if k != "verify"}))
    if record.get("verify"):
        print(json.dumps(record["verify"], indent=1))


if __name__ == "__main__":
    main()
