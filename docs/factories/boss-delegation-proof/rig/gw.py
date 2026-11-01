"""Drive the proof rig's Gateway with its shared token. Every call is added to transcript.txt beside this folder.

Usage: python gw.py publish | python gw.py GET <path> | python gw.py POST <path> <json file>
"""
import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(os.environ["LOCALAPPDATA"]) / "cc-director-restart-qa-rig"
BASE = "http://127.0.0.1:7911"
TOKEN = (ROOT / "config" / "director" / "gateway-token.txt").read_text().strip()
HERE = Path(__file__).resolve().parent
LOG = HERE.parent / "transcript.txt"

FACTORY = "delegation-proof"
FILES = {
    "factory.yaml": (
        f"factory: {FACTORY}\ntitle: Delegation Proof\ngoalText: Prove a Boss can delegate.\nbossSeat: boss\n"
        "defaults:\n  machine: SOREN_NORTH\n  agent: ClaudeCode\n  model: claude-sonnet-5-5\n"
    ),
    "seats/boss.yaml": (
        "seat: boss\nname: Boss\nrole: Boss\nbrief: |\n  Run the proof factory. The seat scribe writes the notes.\n"
        "starts:\n  - cron: \"0 3 1 1 *\"\n    timeZone: America/Toronto\n    prompt: |\n"
        "      Have the seat scribe write today's note. Then, once it has come back, tell me in one line what it said\n"
        "      and report the run.\n"
    ),
    "seats/scribe.yaml": (
        "seat: scribe\nname: Scribe\nrole: Writer\nmodel: claude-haiku-5-5\ngrants: [write-notes]\n"
        "brief: |\n  You write notes. Every note starts with the words BRIEF-MARK-4417.\n"
    ),
    "grants.yaml": (
        "grants:\n  - id: write-notes\n    allows: Write one note file in your work folder\n"
        "    limits: GRANT-MARK-9052 at most one note a run\n    seats: [scribe]\n"
    ),
}


def call(method, path, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(BASE + path, data=data, method=method)
    req.add_header("Authorization", "Bearer " + TOKEN)
    req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            status, text = resp.status, resp.read().decode()
    except urllib.error.HTTPError as err:
        status, text = err.code, err.read().decode()
    with LOG.open("a", encoding="ascii", errors="replace") as out:
        out.write(f"{method} {path} -> {status}\n{text[:1500]}\n\n")
    print(status, text[:1500])
    return status, text


def main():
    verb = sys.argv[1]
    if verb == "publish":
        status, text = call("POST", "/factory-definitions/import", {"note": "the proof", "content": FILES})
        if status >= 300:
            return
        version = json.loads(text).get("version", 1)
        call("POST", f"/factory-definitions/{FACTORY}/publish", {"version": version, "replaces": 0})
    else:
        call(verb, sys.argv[2], json.loads(Path(sys.argv[3]).read_text()) if len(sys.argv) > 3 else None)


if __name__ == "__main__":
    main()
