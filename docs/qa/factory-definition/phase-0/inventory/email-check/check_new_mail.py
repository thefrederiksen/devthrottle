"""The Outreach seat's mail check: is there mail the seat must act on? (Factory Definition 5.5a, F6)

It READS ONLY. It asks Microsoft Graph for the sender, subject and time of recent messages in the
Outreach mailbox - never a message body - and counts the ones the seat must act on: mail from anyone the
ledger says we wrote to (a reply, an out-of-office), and delivery failures (a bounce). It never sends,
deletes, moves, flags or marks anything read: every mail call is an HTTP GET, and the only POST is the
sign-in that gets the read token.

The contract (5.5a): exit 0 and print ONE JSON object on standard output:
  count    integer, how many messages need the seat (0 = nothing to do)
  context  text of at most 8 KB: one line per message - when, from whom, the subject, the folder
State is a file, never output: the Director names it in CC_CHECK_STATE_FILE. This check reads it (per
folder: the newest receive time it has already read, and the ids read at exactly that time) and rewrites it.
Anything that goes wrong is a non-zero exit with the reason on standard error - a failed check.

A folder with more than 500 new messages is not a failure: the check reads the OLDEST 500, counts them,
advances that folder's state past them and says "more not shown"; the next run reads on from there. (Phase 0's
version failed and never advanced, so it failed forever.)

Secrets (mission 5.5a, the Delivery Lead's ruling): the start declares three cc-secrets entries and the Director
runs this check under `cc-secrets run`. Each entry arrives in ITS OWN variable, and the Director says which in
CC_CHECK_SECRET_VARS - a JSON object from each declared entry name to its variable. Which entry plays which part
is in mail-rules.json ("secrets"), not in this code:
  tenantId      setting  the mailbox's Microsoft 365 tenant
  clientId      setting  an app registration with Graph Mail.Read (application), limited to the one
                         mailbox by an application access policy
  clientSecret  secret   that app's client secret

Two optional rules keys say where Graph and its sign-in are: "graph" (default https://graph.microsoft.com/v1.0)
and "signIn" (default https://login.microsoftonline.com). Only a test rig sets them, to point the check at a fake
mailbox on this machine; the definition that reads a real mailbox leaves them out.

Rules (who counts, which entries) are in files/mail-rules.json under the working folder - where the definition
puts them, and the Director runs a check with the downloaded version's folder as its working folder - or in the file
named by --rules.

Usage:  run by the Director as a factory's check. By hand:
          cc-secrets run outreach-mail-client-secret --with outreach-mail-client-id --with outreach-mail-tenant-id
            -- python check_new_mail.py --rules mail-rules.json
        with CC_CHECK_STATE_FILE and CC_CHECK_SECRET_VARS set as the Director sets them.
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
SIGN_IN = "https://login.microsoftonline.com"
SECRET_PARTS = ("tenantId", "clientId", "clientSecret")
SECRET_VARS = "CC_CHECK_SECRET_VARS"
DEFAULT_RULES = os.path.join("files", "mail-rules.json")


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


def read_credentials(rules, env):
    """The three values, found by the entry names mail-rules.json gives, through CC_CHECK_SECRET_VARS."""
    raw = env.get(SECRET_VARS)
    if not raw:
        raise CheckFailed(f"{SECRET_VARS} is not set; the Director sets it for every check that declares secrets")
    try:
        variables = json.loads(raw)
    except json.JSONDecodeError:
        raise CheckFailed(f"{SECRET_VARS} is not a JSON object of entry names to variables") from None
    if not isinstance(variables, dict):
        raise CheckFailed(f"{SECRET_VARS} is not a JSON object of entry names to variables")
    credentials, missing = {}, []
    for part in SECRET_PARTS:
        entry = rules["secrets"][part]
        variable = variables.get(entry)
        if not variable:
            missing.append(f"{entry} ({part}) was not declared")
        elif not env.get(variable):
            missing.append(f"{entry} ({part}) should be in {variable}, which is not set")
        else:
            credentials[part] = env[variable]
    if missing:
        raise CheckFailed("not given: " + "; ".join(missing) + " - declare the entries on the start and run it from the Director")
    return credentials


def read_token(credentials, sign_in=SIGN_IN):
    """The app-only Graph token. A POST to the sign-in endpoint - the only non-GET call this makes."""
    url = f"{sign_in}/{credentials['tenantId']}/oauth2/v2.0/token"
    answer = http_json("POST", url, form={
        "client_id": credentials["clientId"],
        "client_secret": credentials["clientSecret"],
        "scope": "https://graph.microsoft.com/.default",
        "grant_type": "client_credentials",
    })
    token = answer.get("access_token")
    if not token:
        raise CheckFailed(f"the sign-in answered without a token: {answer.get('error_description') or answer.get('error')}")
    return token


def load_rules(path):
    if not os.path.isfile(path):
        raise CheckFailed(f"the rules file {os.path.abspath(path)} does not exist; the definition keeps it at "
                          f"{DEFAULT_RULES} beside checks/, or name one with --rules")
    with open(path, encoding="utf-8") as fh:
        rules = json.load(fh)
    for key in ("mailbox", "folders", "ledger", "bounceSubjectMarkers", "firstRunLookbackHours", "secrets"):
        if key not in rules:
            raise CheckFailed(f"{path} has no '{key}'")
    for part in SECRET_PARTS:
        if not isinstance(rules["secrets"].get(part), str) or not rules["secrets"][part]:
            raise CheckFailed(f"{path} names no entry for '{part}' under 'secrets'")
    for key in ("graph", "signIn"):
        if key in rules and (not isinstance(rules[key], str) or not rules[key]):
            raise CheckFailed(f"{path}: '{key}' must be an address in quotes, such as \"http://127.0.0.1:7994\"; "
                              "leave it out to use Microsoft's own")
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
    """Per folder: {"newest": time, "idsAtNewest": [...]}. An empty file is a first run."""
    if os.path.getsize(path) > STATE_CAP_BYTES:
        raise CheckFailed(f"the state file is over {STATE_CAP_BYTES} bytes")
    with open(path, encoding="utf-8") as fh:
        text = fh.read().strip()
    if not text:
        return {}
    state = json.loads(text)
    folders = state.get("folders") if isinstance(state, dict) else None
    if not isinstance(folders, dict):
        raise CheckFailed("the state file holds no 'folders'; reset the check's state to start again")
    return {name: {"newest": f["newest"], "idsAtNewest": list(f.get("idsAtNewest", []))} for name, f in folders.items()}


def write_state(path, folders):
    text = json.dumps({"folders": folders}, sort_keys=True)
    if len(text.encode("utf-8")) > STATE_CAP_BYTES:
        raise CheckFailed(f"the new state is over {STATE_CAP_BYTES} bytes")
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)


def messages_since(token, mailbox, folder, since_iso, graph=GRAPH):
    """Sender, subject and time of the messages received at or after since_iso, OLDEST first, at most
    PAGE_SIZE * MAX_PAGES_PER_FOLDER of them. GET only, no body. Returns (messages, more_left)."""
    query = urllib.parse.urlencode({
        "$select": "id,receivedDateTime,from,subject",
        "$filter": f"receivedDateTime ge {since_iso}",
        "$orderby": "receivedDateTime asc",
        "$top": str(PAGE_SIZE),
    })
    url = f"{graph}/users/{urllib.parse.quote(mailbox)}/mailFolders/{folder}/messages?{query}"
    out = []
    for _ in range(MAX_PAGES_PER_FOLDER):
        page = http_json("GET", url, headers={"Authorization": f"Bearer {token}"})
        for m in page.get("value", []):
            sender = ((m.get("from") or {}).get("emailAddress") or {}).get("address") or ""
            out.append({"id": m["id"], "received": m["receivedDateTime"], "from": sender.lower(),
                        "subject": m.get("subject") or "", "folder": folder})
        url = page.get("@odata.nextLink")
        if not url:
            return out, False
    return out, True


def build_context(found, notes):
    lines = [f"{m['received']} from {m['from'] or '(no sender)'} - {m['subject']} [{m['folder']}]" for m in found]
    tail = "\n".join(notes)
    budget = CONTEXT_CAP_BYTES - (len(tail.encode("utf-8")) + 1 if tail else 0)
    out, used = [], 0
    for i, line in enumerate(lines):
        size = len(line.encode("utf-8")) + 1
        rest = f"({len(lines) - i} more not shown)"
        if used + size + len(rest) + 1 > budget:
            out.append(rest)
            break
        out.append(line)
        used += size
    return "\n".join(out + notes)


def run(rules, env, state_path, now):
    credentials = read_credentials(rules, env)
    state = read_state(state_path)
    first_since = (now - timedelta(hours=rules["firstRunLookbackHours"])).strftime("%Y-%m-%dT%H:%M:%SZ")
    watched = watched_senders(rules["ledger"])
    markers = [m.lower() for m in rules["bounceSubjectMarkers"]]

    token = read_token(credentials, rules.get("signIn", SIGN_IN))
    found, notes, new_state = [], [], {}
    for folder in rules["folders"]:
        mark = state.get(folder)
        since = mark["newest"] if mark else first_since
        already = set(mark["idsAtNewest"]) if mark else set()
        seen, more_left = messages_since(token, rules["mailbox"], folder, since, rules.get("graph", GRAPH))
        fresh = [m for m in seen if m["id"] not in already]
        found.extend(m for m in fresh if m["from"] in watched or any(k in m["subject"].lower() for k in markers))

        newest = max([m["received"] for m in seen] + [since])
        ids = {m["id"] for m in seen if m["received"] == newest} | (already if newest == since else set())
        new_state[folder] = {"newest": newest, "idsAtNewest": sorted(ids)}
        if more_left:
            notes.append(f"(more not shown: {folder} has more than {PAGE_SIZE * MAX_PAGES_PER_FOLDER} new messages; "
                         f"this run read up to {newest} and the next run reads on from there)")

    found.sort(key=lambda m: m["received"])
    write_state(state_path, new_state)
    return {"count": len(found), "context": build_context(found, notes)}


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--rules", default=DEFAULT_RULES,
                    help=f"the rules file (default: {DEFAULT_RULES} under the working folder, the definition's layout)")
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
