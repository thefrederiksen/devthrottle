"""Seed the isolated QA Gateway (port 7899) for the Factory Agents screenshot QA, at the pull request head.

- Triggers: created with the real `cc-devthrottle trigger add`.
- Trigger checks: the Director's real report route, so the Gateway itself decides each outcome and writes the
  trigger-actor rows ("nothing to do", "check failed", "skipped").
- A factory agent's rows: the record's own route (the one `cc-devthrottle factory record` calls). The command
  refuses on a no-auth rig (no session key to stamp as the actor), stamps the time as now, and takes no session id.

The stuck lock (six hours) is seeded by shoot2.py, because it needs the stand-in Director connected.
"""
import json
import os
import subprocess
import sys
from datetime import datetime, timedelta, timezone

import requests

GW = "http://127.0.0.1:7899"
MACHINE = os.environ["COMPUTERNAME"]
DIRECTOR = "qa-rig-director"
CLI = [os.path.join(os.environ["TEMP"], "wbf-qa", "venv", "Scripts", "cc-devthrottle.exe")]
ENV = dict(os.environ, PYTHONPATH=r"D:\ReposFred\devthrottle-wbf-cockpit\tools",
           CC_GATEWAY_URL=GW, CC_GATEWAY_SESSION_KEY="qa-rig-no-auth")
NOW = datetime.now(timezone.utc)
IDS = os.path.join(os.environ["TEMP"], "wbf-qa", "trigger-ids.json")


def iso(dt):
    return dt.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def cli(args):
    out = subprocess.run(CLI + args, env=ENV, capture_output=True, text=True)
    if out.returncode != 0:
        sys.exit(f"cc-devthrottle {' '.join(args[:2])} FAILED: {out.stdout} {out.stderr}")
    return out.stdout


def trigger_add(name, factory, agent, every):
    t = json.loads(cli(["trigger", "add", "--name", name, "--factory", factory, "--agent", agent, "--machine", MACHINE,
                        "--repo", os.environ["TEMP"], "--check", "cc-website-factory mail-waiting --json",
                        "--every", every, "--prompt", "There are {count} items waiting. Handle them.", "--json"]))
    print(f"trigger {name}: {t['id']}")
    return t["id"]


def register_director():
    requests.post(f"{GW}/directors/register",
                  json={"directorId": DIRECTOR, "machineName": MACHINE, "pid": os.getpid()}).raise_for_status()


def check(trigger_id, at, exit_code=0, output='{"count": 0}', error=None):
    body = {"checkedAtUtc": iso(at), "exitCode": exit_code, "timedOut": False, "output": output, "errorOutput": error}
    r = requests.post(f"{GW}/directors/{DIRECTOR}/triggers/{trigger_id}/checks", json=body)
    if r.status_code >= 300:
        sys.exit(f"check {trigger_id} FAILED {r.status_code}: {r.text}")
    return r.json()


def record_at(factory, agent, outcome, what, at, actor, subject=None, session=None, link=None):
    body = {"factory": factory, "factoryAgent": agent, "outcome": outcome, "what": what, "subject": subject,
            "sessionId": session, "link": link, "actor": actor, "occurredUtc": iso(at)}
    r = requests.post(f"{GW}/gateway/factory/activity", json=body)
    if r.status_code != 201:
        sys.exit(f"record FAILED {r.status_code}: {r.text}")
    return r.json()["id"]


def record_now(factory, agent, outcome, what, subject):
    # `cc-devthrottle factory record` refuses on this no-auth rig: it has no session key for the Gateway to stamp
    # as the actor ("Who acted is not known"). The same route, with the session named as the actor.
    return record_at(factory, agent, outcome, what, datetime.now(timezone.utc), "session:134", subject=subject,
                     session="134")


def main():
    ids = {
        "mail": trigger_add("New business mail", "website-business", "front-desk", "10m"),
        "invoices": trigger_add("Unpaid invoices", "website-business", "bookkeeper", "10m"),
        "reviews": trigger_add("Review requests", "website-business", "reviewer", "10m"),
        "uptime": trigger_add("Site uptime", "site-care", "site-keeper", "1m"),       # never checked: no checks ran
        "ranking": trigger_add("Search ranking", "site-care", "seo-writer", "10m"),   # paused from the Cockpit
        "publish": trigger_add("Publish queue", "site-care", "publisher", "10m"),     # the stuck lock, in shoot2
    }
    with open(IDS, "w") as f:
        json.dump(ids, f)
    register_director()

    # The night: the mail trigger checks and finds nothing, then the front desk works a thread in session 134.
    night = NOW - timedelta(hours=7)
    for i in range(24):
        check(ids["mail"], night + timedelta(minutes=10 * i))
    t = ids["mail"]
    record_at("website-business", "front-desk", "started", "Started to answer 1 new mail thread.",
              night + timedelta(hours=4), "trigger:" + t, subject="New business mail", session="134")
    record_at("website-business", "front-desk", "done", "Answered Pine Valley Plumbing's question about the launch date.",
              night + timedelta(hours=4, minutes=6), "session:134", subject="Pine Valley Plumbing", session="134")
    record_at("website-business", "front-desk", "blocked", "Refused to quote a price nobody approved.",
              night + timedelta(hours=4, minutes=8), "session:134", subject="Maple Dental", session="134")
    for i in range(17):
        check(ids["mail"], night + timedelta(hours=4, minutes=20 + 10 * i))
    check(ids["mail"], NOW - timedelta(seconds=20))  # recent, so the trigger is OK

    # Rows the front desk writes now.
    record_now("website-business", "front-desk", "asked",
               "Asked whether a 20 percent discount is allowed for a returning customer.", "Maple Dental")
    record_now("website-business", "front-desk", "escalated",
               "A customer asked for a refund of the first month.", "Lakeside Bakery")

    # The invoices check breaks: RED "check failed".
    for i in range(5):
        check(ids["invoices"], night + timedelta(minutes=30 * i))
    check(ids["invoices"], NOW - timedelta(seconds=15), exit_code=1, output="",
          error="cc-invoices: cannot reach the bank feed")

    # Review requests: every run all night is "nothing to do".
    for i in range(40):
        check(ids["reviews"], night + timedelta(minutes=10 * i + 3))
    check(ids["reviews"], NOW - timedelta(seconds=10))

    # Search ranking checks quietly; it is paused from the Cockpit during the QA.
    for i in range(3):
        check(ids["ranking"], NOW - timedelta(minutes=3 - i))

    # Publish queue: quiet checks through the night up to seven hours ago; shoot2 seeds its stuck session.
    for i in range(6):
        check(ids["publish"], night - timedelta(minutes=60 - 10 * i))
    print("seeded")


if __name__ == "__main__":
    main()
