"""The Outreach seat's mail check: is there mail the seat must act on? (Factory Definition 5.5a, F6)

It READS ONLY. It asks Microsoft Graph for the sender, subject and time of recent messages in the
Outreach mailbox - never a message body - and counts the ones the seat must act on: mail from anyone the
ledger says we wrote to (a reply, an out-of-office), and delivery failures (a bounce). It never sends,
deletes, moves, flags or marks anything read: every mail call is an HTTP GET, and the only POST is the
sign-in that gets the read token.

The contract (5.5a): exit 0 and print ONE JSON object on standard output:
  count    integer, how many messages need the seat (0 = nothing to do)
  context  text of at most 8 KB: one line per message - when, from whom, the subject, the folder
State is a file, never output: the Director names it in CC_CHECK_STATE_FILE. This check reads it (the
newest receive time it has already counted, and the ids counted at exactly that time) and rewrites it.
Anything that goes wrong is a non-zero exit with the reason on standard error - a failed check.

Secrets, by name, through `cc-secrets run` (entry name upper-cased, dashes to underscores):
  OUTREACH_MAIL_TENANT_ID      setting  the mailbox's Microsoft 365 tenant
  OUTREACH_MAIL_CLIENT_ID      setting  an app registration with Graph Mail.Read (application),
                                        limited to the one mailbox by an application access policy
  OUTREACH_MAIL_CLIENT_SECRET  secret   that app's client secret

Rules (who counts) are in mail-rules.json beside this file, or the file named by --rules.

Usage:  cc-secrets run outreach-mail-client-secret --with outreach-mail-client-id --with outreach-mail-tenant-id
            -- python check_new_mail.py [--rules mail-rules.json]
"""

import argparse
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timedelta, timezone

CONTEXT_CAP_BYTES = 8 * 1024
STATE_CAP_BYTES = 64 * 1024
PAGE_SIZE = 50
MAX_PAGES_PER_FOLDER = 10
GRAPH = "https://graph.microsoft.com/v1.0"
REQUIRED_ENV = ("OUTREACH_MAIL_TENANT_ID", "OUTREACH_MAIL_CLIENT_ID", "OUTREACH_MAIL_CLIENT_SECRET")


class CheckFailed(Exception):
    """A reason the check cannot answer. Printed on standard error; the exit code is 1."""


def http_json(method, url, headers=None, form=None):
    """One HTTP call returning parsed JSON. Replaced in the tests by a fake that records every call."""
    data = urllib.parse.urlencode(form).encode("ascii") if form is not None else None
    req = urllib.request.Request(url, data=data, method=method, headers=headers or {})
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", errors="replace")[:500]
        raise CheckFailed(f"{method} {url.split('?')[0]} answered HTTP {e.code}: {body}") from None
    except urllib.error.URLError as e:
        raise CheckFailed(f"{method} {url.split('?')[0]} could not be reached: {e.reason}") from None


def read_token(env):
    """The app-only Graph token. A POST to the sign-in endpoint - the only non-GET call this makes."""
    url = f"https://login.microsoftonline.com/{env['OUTREACH_MAIL_TENANT_ID']}/oauth2/v2.0/token"
    answer = http_json("POST", url, form={
        "client_id": env["OUTREACH_MAIL_CLIENT_ID"],
        "client_secret": env["OUTREACH_MAIL_CLIENT_SECRET"],
        "scope": "https://graph.microsoft.com/.default",
        "grant_type": "client_credentials",
    })
    token = answer.get("access_token")
    if not token:
        raise CheckFailed(f"the sign-in answered without a token: {answer.get('error_description') or answer.get('error')}")
    return token


def load_rules(path):
    with open(path, encoding="utf-8") as fh:
        rules = json.load(fh)
    for key in ("mailbox", "folders", "ledger", "bounceSubjectMarkers", "firstRunLookbackHours"):
        if key not in rules:
            raise CheckFailed(f"{path} has no '{key}'")
    return rules


def watched_senders(ledger_path):
    """Everyone the ledger says we wrote to: their mail is a reply, an out-of-office or a bounce notice."""
    path = os.path.expandvars(ledger_path)
    with open(path, encoding="utf-8") as fh:
        ledger = json.load(fh)
    people = ledger.get("people")
    if not isinstance(people, list):
        raise CheckFailed(f"the ledger {path} has no 'people' list")
    return {p["address"].strip().lower() for p in people if p.get("address") and p.get("sent")}


def read_state(path):
    if os.path.getsize(path) > STATE_CAP_BYTES:
        raise CheckFailed(f"the state file is over {STATE_CAP_BYTES} bytes")
    with open(path, encoding="utf-8") as fh:
        text = fh.read().strip()
    if not text:
        return None
    state = json.loads(text)
    return {"newest": state["newest"], "idsAtNewest": list(state.get("idsAtNewest", []))}


def write_state(path, state):
    text = json.dumps(state, sort_keys=True)
    if len(text.encode("utf-8")) > STATE_CAP_BYTES:
        raise CheckFailed(f"the new state is over {STATE_CAP_BYTES} bytes")
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)


def messages_since(token, mailbox, folder, since_iso):
    """Sender, subject and time of every message received at or after since_iso. GET only, no body."""
    query = urllib.parse.urlencode({
        "$select": "id,receivedDateTime,from,subject",
        "$filter": f"receivedDateTime ge {since_iso}",
        "$orderby": "receivedDateTime desc",
        "$top": str(PAGE_SIZE),
    })
    url = f"{GRAPH}/users/{urllib.parse.quote(mailbox)}/mailFolders/{folder}/messages?{query}"
    out = []
    for _ in range(MAX_PAGES_PER_FOLDER):
        page = http_json("GET", url, headers={"Authorization": f"Bearer {token}"})
        for m in page.get("value", []):
            sender = ((m.get("from") or {}).get("emailAddress") or {}).get("address") or ""
            out.append({"id": m["id"], "received": m["receivedDateTime"], "from": sender.lower(),
                        "subject": m.get("subject") or "", "folder": folder})
        url = page.get("@odata.nextLink")
        if not url:
            return out
    raise CheckFailed(f"more than {PAGE_SIZE * MAX_PAGES_PER_FOLDER} new messages in {folder}; the state is too far behind")


def build_context(found):
    lines = [f"{m['received']} from {m['from'] or '(no sender)'} - {m['subject']} [{m['folder']}]" for m in found]
    out, used = [], 0
    for i, line in enumerate(lines):
        size = len(line.encode("utf-8")) + 1
        rest = f"({len(lines) - i} more not shown)"
        if used + size + len(rest) + 1 > CONTEXT_CAP_BYTES:
            out.append(rest)
            break
        out.append(line)
        used += size
    return "\n".join(out)


def run(rules, env, state_path, now):
    missing = [n for n in REQUIRED_ENV if not env.get(n)]
    if missing:
        raise CheckFailed(f"not given: {', '.join(missing)} - run this under cc-secrets run with those entries")
    state = read_state(state_path)
    since = state["newest"] if state else (now - timedelta(hours=rules["firstRunLookbackHours"])).strftime("%Y-%m-%dT%H:%M:%SZ")
    already = set(state["idsAtNewest"]) if state else set()
    watched = watched_senders(rules["ledger"])
    markers = [m.lower() for m in rules["bounceSubjectMarkers"]]

    token = read_token(env)
    seen = []
    for folder in rules["folders"]:
        seen.extend(messages_since(token, rules["mailbox"], folder, since))
    fresh = [m for m in seen if m["id"] not in already]
    found = sorted((m for m in fresh if m["from"] in watched or any(k in m["subject"].lower() for k in markers)),
                   key=lambda m: m["received"])

    newest = max([m["received"] for m in seen] + [since])
    ids_at_newest = sorted({m["id"] for m in seen if m["received"] == newest} | (already if newest == since else set()))
    write_state(state_path, {"newest": newest, "idsAtNewest": ids_at_newest})
    return {"count": len(found), "context": build_context(found)}


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--rules", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "mail-rules.json"))
    args = ap.parse_args()
    try:
        state_path = os.environ.get("CC_CHECK_STATE_FILE")
        if not state_path:
            raise CheckFailed("CC_CHECK_STATE_FILE is not set; the Director names the state file for every run")
        if not os.path.isfile(state_path):
            raise CheckFailed(f"the state file {state_path} does not exist; the Director writes it before the run")
        result = run(load_rules(args.rules), os.environ, state_path, datetime.now(timezone.utc))
    except CheckFailed as e:
        print(f"check failed: {e}", file=sys.stderr)
        return 1
    print(json.dumps(result))
    return 0


if __name__ == "__main__":
    sys.exit(main())
