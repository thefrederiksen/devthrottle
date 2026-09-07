"""The before picture for a Director restart QA run (issue 2719, Phase 7).

Records every session live on ONE Director at the moment it is run: id, name, mission, role, what
it was doing, its repository, its branch and its uncommitted count. Everything in the QA report is
measured against this list, so it is taken from the Gateway's own session records (the facts the
Director knows) and never asked of the agents.

It knows nothing about the rig. It takes a Gateway address, a credential and a Director id, so the
same script takes the before picture of a production Director from a driver on another machine.

    python scripts/restart-qa/before-picture.py --gateway http://127.0.0.1:7911 --key <token> \
        --director <director id> --out <dir>

Writes <out>/before-picture.json (the raw session records plus the derived rows) and
<out>/before-picture.md (the table the report embeds). Every value has one of three shapes: a value,
an explicit "not available" with the reason, or an error that stops the script. Nothing is defaulted.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import subprocess
import sys
import urllib.request


def fetch(gateway: str, key: str, path: str):
    req = urllib.request.Request(gateway.rstrip("/") + path, headers={"Authorization": "Bearer " + key})
    with urllib.request.urlopen(req, timeout=30) as resp:
        body = resp.read().decode("utf-8")
    return json.loads(body)


def items_of(payload, key: str):
    if isinstance(payload, list):
        return payload
    if isinstance(payload, dict) and key in payload:
        return payload[key]
    raise SystemExit("unexpected shape from the Gateway for " + key + ": " + json.dumps(payload)[:200])


def git(repo: str, *args: str) -> str | None:
    """Run git in a repository on THIS machine. None when the path is not here - which is the
    ordinary case when the driver sits on another machine - so the caller records 'not available'
    rather than a guess."""
    if not os.path.isdir(repo):
        return None
    proc = subprocess.run(["git", "-C", repo, *args], capture_output=True, text=True)
    if proc.returncode != 0:
        raise SystemExit("git " + " ".join(args) + " failed in " + repo + ": " + proc.stderr.strip())
    return proc.stdout.strip()


def role_of(session: dict) -> str:
    for field in ("explicitRole", "sessionRole", "groupRole"):
        value = session.get(field)
        if value:
            return str(value)
    return "(none)"


def doing(session: dict) -> str:
    parts = [str(session.get("activityState") or ""), str(session.get("stateLabel") or "")]
    hold = session.get("holdState")
    if hold and hold != "None":
        parts.append("hold=" + str(hold))
    if session.get("snoozeUntil"):
        parts.append("snoozed until " + str(session.get("snoozeUntil")))
    if session.get("promptDeliveryUnresolved"):
        parts.append("PROMPT DELIVERY UNRESOLVED")
    return " / ".join(p for p in parts if p)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--gateway", required=True)
    ap.add_argument("--key", required=True, help="a credential the Gateway accepts on GET /sessions")
    ap.add_argument("--director", required=True, help="the Director id whose sessions are recorded")
    ap.add_argument("--out", required=True, help="directory to write before-picture.json and .md into")
    ap.add_argument("--label", default="before", help="file stem: before (default) or after")
    args = ap.parse_args()

    taken_at = dt.datetime.now(dt.timezone.utc).isoformat()
    sessions = [s for s in items_of(fetch(args.gateway, args.key, "/sessions"), "sessions")
                if str(s.get("directorId", "")).lower() == args.director.lower()]
    directors = [d for d in items_of(fetch(args.gateway, args.key, "/directors"), "directors")
                 if str(d.get("directorId", "")).lower() == args.director.lower()]
    if len(directors) != 1:
        raise SystemExit("the Gateway lists " + str(len(directors)) + " Director(s) with id " + args.director
                         + " - expected exactly one; refusing to record a before picture against an unknown Director")
    director = directors[0]

    rows = []
    for s in sorted(sessions, key=lambda x: str(x.get("createdAt") or "")):
        repo = str(s.get("repoPath") or "")
        branch = git(repo, "rev-parse", "--abbrev-ref", "HEAD") if repo else None
        status = git(repo, "status", "--porcelain") if repo else None
        local_uncommitted = None if status is None else len([l for l in status.splitlines() if l.strip()])
        rows.append({
            "sessionId": s.get("sessionId"),
            "shortId": str(s.get("sessionId") or "")[:8],
            "name": s.get("name"),
            "agent": s.get("agent"),
            "model": s.get("modelDisplay") if isinstance(s.get("modelDisplay"), str) else (s.get("currentModel") or "(not recorded)"),
            "missionId": s.get("missionId"),
            "missionName": s.get("missionName") or "(none)",
            "role": role_of(s),
            "parentSessionId": s.get("parentSessionId"),
            "controllerSessionId": s.get("controllerSessionId"),
            "doing": doing(s),
            "status": s.get("status"),
            "activityState": s.get("activityState"),
            "holdState": s.get("holdState"),
            "turnCount": s.get("turnCount"),
            "repoPath": repo,
            "branch": branch if branch is not None else "not available: repository path is not on this machine",
            "uncommittedCountGateway": s.get("uncommittedCount"),
            "uncommittedCountLocalGit": local_uncommitted if local_uncommitted is not None else "not available: repository path is not on this machine",
            "createdAt": s.get("createdAt"),
            "claudeSessionId": s.get("claudeSessionId"),
            "claudeTranscriptPath": s.get("claudeTranscriptPath"),
            "promptDeliveryUnresolved": s.get("promptDeliveryUnresolved"),
        })

    os.makedirs(args.out, exist_ok=True)
    stem = os.path.join(args.out, args.label + "-picture")
    with open(stem + ".json", "w", encoding="ascii") as f:
        json.dump({
            "takenAtUtc": taken_at,
            "gateway": args.gateway,
            "director": director,
            "sessionCount": len(rows),
            "rows": rows,
            "rawSessions": sessions,
        }, f, indent=2, ensure_ascii=True)

    lines = [
        "# " + args.label.capitalize() + " picture - Director " + str(director.get("displayName")) + " (" + args.director + ")",
        "",
        "Taken " + taken_at + " from " + args.gateway + ". Director version " + str(director.get("version"))
        + ", pid " + str(director.get("pid")) + ", machine " + str(director.get("machineName")) + ". "
        + str(len(rows)) + " session(s).",
        "",
        "| Id | Name | Mission | Role | Reports to | Agent | Doing | Turns | Repository | Branch | Uncommitted (Gateway / local git) |",
        "|---|---|---|---|---|---|---|---|---|---|---|",
    ]
    for r in rows:
        reports_to = str(r["controllerSessionId"] or r["parentSessionId"] or "-")[:8]
        lines.append("| " + " | ".join(str(x) for x in [
            r["shortId"], r["name"], r["missionName"], r["role"], reports_to, r["agent"], r["doing"],
            r["turnCount"], r["repoPath"], r["branch"],
            str(r["uncommittedCountGateway"]) + " / " + str(r["uncommittedCountLocalGit"]),
        ]) + " |")
    with open(stem + ".md", "w", encoding="ascii") as f:
        f.write("\n".join(lines) + "\n")

    print("wrote " + stem + ".json and .md: " + str(len(rows)) + " session(s) on Director " + args.director)
    for r in rows:
        print("  " + r["shortId"] + "  " + str(r["name"]) + "  [" + r["missionName"] + " / " + r["role"] + "]  "
              + r["doing"] + "  " + str(r["branch"]) + "  uncommitted=" + str(r["uncommittedCountGateway"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
