"""Probe for issue 2935, assumption 1: cc-ship can tell when a spawned session has
finished, and can tell a crash apart from a finish.

Scenario "finish": the session writes its output file, then flags itself done.
Scenario "crash":  the session is told to wait a long time before writing; the
probe stops it while it is waiting, so it leaves the fleet with no output.

Usage: python probe_finish_detection.py <work-folder>
Writes <work-folder>/<scenario>/observations.json and prints one line per scenario.
"""

from __future__ import annotations

import json
import os
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from src import fleet  # noqa: E402

REPO = Path(__file__).resolve().parents[3]
AUTHOR = os.environ["CC_SESSION_ID"]

FINISH_BRIEF = """# Probe task (finish)

1. Write this exact JSON to the file {output}: {{"probe": "finish", "ok": true}}
2. Then run this shell command: cc-devthrottle session done --reason "probe finished"
3. Do nothing else. Do not edit any other file. Do not send any messages.
"""

CRASH_BRIEF = """# Probe task (crash)

1. Run this shell command in the foreground and wait for it to end (it takes 15 minutes, that is expected): python3 -c "import time; time.sleep(900)"
2. Then write this exact JSON to the file {output}: {{"probe": "crash", "ok": true}}
3. Then run: cc-devthrottle session done --reason "probe finished"
Do not edit any other file. Do not send any messages.
"""


def run_scenario(work: Path, scenario: str, brief_text: str, crash_after: float | None) -> dict:
    folder = work / scenario
    folder.mkdir(parents=True, exist_ok=True)
    output = folder / "output.json"
    brief = folder / "brief.md"
    brief.write_text(brief_text.format(output=output), encoding="ascii")

    session_id = fleet.spawn_session(
        REPO, "ClaudeCode", AUTHOR, f"cc-ship - Worker - probe {scenario}", brief
    )
    started = time.monotonic()

    if crash_after is not None:
        # Wait until the session is seen working, then stop it before it can write.
        time.sleep(crash_after)
        row = fleet.find_session(session_id)
        pre_stop = {"status": row and row["status"], "activity": row and row["activityState"],
                    "output_exists": output.exists()}
        pre_stop["screen"] = fleet.session_screen(session_id)[-1500:]
        stop_text = fleet.stop_session(session_id, "cc-ship probe: simulated crash")
    else:
        pre_stop, stop_text = None, None

    result = fleet.wait_for_output(session_id, output, timeout_seconds=600, poll_seconds=5)
    record = {
        "scenario": scenario,
        "session_id": session_id,
        "outcome": result.outcome,
        "reason": result.reason,
        "seconds": round(time.monotonic() - started),
        "pre_stop": pre_stop,
        "stop_output": stop_text,
        "observations": result.observations,
    }
    (folder / "observations.json").write_text(json.dumps(record, indent=1), encoding="ascii")
    return record


def main() -> None:
    work = Path(sys.argv[1])
    for scenario, text, crash_after in (
        ("finish", FINISH_BRIEF, None),
        ("crash", CRASH_BRIEF, 60.0),
    ):
        rec = run_scenario(work, scenario, text, crash_after)
        print(f"{scenario}: outcome={rec['outcome']} after {rec['seconds']}s - {rec['reason']}")


if __name__ == "__main__":
    main()
