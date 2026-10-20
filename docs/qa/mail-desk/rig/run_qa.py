# /// script
# requires-python = ">=3.10"
# dependencies = ["websockets>=12"]
# ///
"""The Mail Desk rig: live mode and every failure case, on a throwaway Gateway and a test Director built from this tree.

Mail Desk phase 4, contract section 5 (docs/qa/mail-desk/phase-4/CONTRACT.md). Runs on devlinux, in the foreground,
owns everything it starts, and stops everything it started, pass or fail:

- A GATEWAY built from this tree (src/CcDirector.Gateway, Debug) on a free port of 127.0.0.1, with its own empty
  storage root under the rig folder, Tailscale off, authentication on, factory agents on in that root's OWN
  config.json, and Mail Desk switched on for its one account (`local`) through the administrator route, with an
  administrator token made for this run. Its config.json names the rig's fake model through `mailDeskModelBaseUrl`
  (honoured only for a loopback host), and its key vault holds a rig-only key, so the Mail Desk's model questions go
  to the fake model on 127.0.0.1 and never to a real one. It holds no account sign-in, so it sends no email.
- A TEST DIRECTOR built from this tree, slot 5 (local_builds/linux/cc-director-linux5, made by
  scripts/local-build-linux.sh --slot 5 --framework-dependent), with a scratch CC_DIRECTOR_ROOT and a scratch HOME
  under the rig folder and every CC_ variable of this shell removed, auto-update off. It is stopped only by its own
  lifecycle request file, never killed. Afterwards the rig checks that ~/.local/bin still points at the owner's tools.
- THE REAL screen.py, with its network faked: the published mail-desk definition holds the rig's wrapper
  (rig/checks/screen.py) as checks/screen.py and the template's real screen.py, byte for byte, as
  checks/real_screen.py. The wrapper replaces only urllib.request.urlopen (to the fake Graph) and the sign-in (the
  rig's scratch sign-in folder, a fake token), and refuses to run with a sign-in folder outside the rig folder.
- A FAKE GRAPH on 127.0.0.1 (an Inbox, Sent, Archive and Junk Email per mailbox, the move, a log of every call) and a
  FAKE MODEL on 127.0.0.1 (chat completions with log probabilities, scripted per subject, able to fail or answer off
  the labels).

Usage, from the repository root, on devlinux:
    export PATH=$HOME/.dotnet:$HOME/.local/bin:$PATH
    dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj -p:EnableWindowsTargeting=true
    bash scripts/local-build-linux.sh --slot 5 --framework-dependent
    uv run --script docs/qa/mail-desk/rig/run_qa.py [--only section,section]
It writes docs/qa/mail-desk/rig/transcript.txt. Sections, in order: see SECTIONS at the end.
"""

import hashlib
import http.server
import json
import math
import os
import pwd
import re
import secrets
import shutil
import signal
import socket
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[4]
HERE = Path(__file__).resolve().parent
GATEWAY_EXE = REPO / "src" / "CcDirector.Gateway" / "bin" / "Debug" / "net10.0" / "CcDirector.Gateway"
SLOT = 5
DIRECTOR_EXE = REPO / "local_builds" / "linux" / f"cc-director-linux{SLOT}"
TEMPLATE = REPO / "src" / "CcDirector.Gateway" / "MailDesk" / "Template"
WRAPPER = HERE / "checks" / "screen.py"
CLI_DIR = REPO / "tools" / "cc-devthrottle"
USER_HOME = Path(pwd.getpwuid(os.getuid()).pw_dir)
USER_BIN = USER_HOME / ".local" / "bin"
OWNER_TOOLS = USER_HOME / ".local" / "share" / "cc-director" / "pyenv" / "bin"
OWNER_INSTANCE = USER_HOME / ".local" / "share" / "cc-director" / "instances" / "default"
PYENV_PYTHON = OWNER_TOOLS / "python"
MACHINE = socket.gethostname()
BACKUP_MACHINE = "qa-backup"
DOTNET_ROOT = USER_HOME / ".dotnet"

RIG = Path(tempfile.gettempdir()) / "mail-desk-rig"
GW_ROOT = RIG / "gateway-root"
GW_HOME = RIG / "gateway-home"
DIR_ROOT = RIG / "director-root"
DIR_HOME = RIG / "home"
DIR_INSTANCE = DIR_ROOT / "instances" / "default"
SIGN_IN = RIG / "sign-in"
RIG_BIN = RIG / "bin"
SEAT_REPO = RIG / "repos" / "qa-seat"
MARKER = "mail-desk-rig.marker"

ONE = "qa-one@example.com"
TWO = "qa-two@example.com"
BOOKS, CLERK = "qa-books", "clerk"
SUPPORT, DESK = "qa-support", "desk"
BOOKS_HANDLES = "bookkeeping: invoices, receipts, bank letters"
SUPPORT_HANDLES = "customer support questions about the product"
INTERVAL = "1m"           # the shortest interval a start may have (TriggerDefinition.MinimumIntervalSeconds); 5m in the template
CHEAP_MODEL = "claude-haiku-5-5"

PERSON_KEY = "qa-person-" + uuid.uuid4().hex
DIRECTOR_KEY = "qa-director-" + uuid.uuid4().hex
BACKUP_KEY = "qa-backup-" + uuid.uuid4().hex
ADMIN_TOKEN = "qa-admin-" + secrets.token_hex(24)
MODEL_KEY = "rig-only-key-" + uuid.uuid4().hex     # the vault's key for the hosted model: fake, and only the fake model sees it
PREVIEW_MARKER = "RIGPREVIEW"                      # every fake email's body starts with it; it must be stored nowhere

TRANSCRIPT = []
STATE = {}


# ===================================================================================================== the record


def shown(text):
    """What the transcript may carry: the rig, the tree and the home as <rig>, <repo>, <home>; no key or token; no
    address but the rig's own and example senders; plain ASCII."""
    text = str(text)
    for real, alias in ((RIG, "<rig>"), (REPO, "<repo>"), (USER_HOME, "<home>")):
        text = text.replace(str(real), alias)
    for key, alias in ((PERSON_KEY, "<person key>"), (DIRECTOR_KEY, "<director key>"), (BACKUP_KEY, "<backup key>"),
                       (ADMIN_TOKEN, "<admin token>"), (MODEL_KEY, "<rig-only model key>")):
        text = text.replace(key, alias)
    text = re.sub(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}",
                  lambda m: m.group(0) if re.search(r"@([a-z0-9-]+\.)*(example\.(com|org|net)|[a-z0-9-]+\.example)$",
                                                    m.group(0).lower()) else "<other address>", text)
    text = re.sub(r"(?i)soren", "<user>", text)
    text = re.sub(r"eyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", "<token>", text)
    for fancy, plain in (("‘", "'"), ("’", "'"), ("“", '"'), ("”", '"'), ("—", "-"),
                         ("–", "-"), ("…", "..."), ("❯", ">"), ("→", "->")):
        text = text.replace(fancy, plain)
    text = re.sub(r"[─-╿]+", "", text)
    return text.encode("ascii", "replace").decode("ascii")


def note(text):
    TRANSCRIPT.append(f"# {shown(text)}\n")
    print(f"== {shown(text)}", flush=True)


def record(title, body):
    TRANSCRIPT.append(f"# {shown(title)}\n{shown(body).rstrip()}\n")
    print(f"-- {shown(title)}", flush=True)


def section(title, text):
    TRANSCRIPT.append("\n" + "=" * 118 + f"\n== {shown(title)}\n" + "=" * 118 + f"\n# {shown(text)}\n")
    print(f"\n==== {shown(title)}", flush=True)


def check(condition, what):
    """A verdict the transcript carries. A false one stops the run: the rest would prove nothing."""
    TRANSCRIPT.append(f"# CHECK {'PASS' if condition else 'FAIL'}: {shown(what)}\n")
    print(f"CHECK {'PASS' if condition else 'FAIL'}: {shown(what)}", flush=True)
    if not condition:
        raise SystemExit(f"check failed: {what}")


def utc(when=None):
    return (when or datetime.now(timezone.utc)).strftime("%Y-%m-%dT%H:%M:%SZ")


def wait_for(what, ready, seconds=180, every=1.0):
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            value = ready()
            if value:
                return value
        except (urllib.error.URLError, ConnectionError, TimeoutError, OSError, sqlite3.Error):
            pass
        time.sleep(every)
    raise SystemExit(f"{what} within {seconds}s")


def without_cc(env):
    """This shell's environment with every CC_ variable removed: this session's Gateway address and key are among
    them, and nothing the rig starts may carry them."""
    return {k: v for k, v in env.items() if not k.startswith("CC_")}


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class Quiet(http.server.ThreadingHTTPServer):
    daemon_threads = True


def serve(answer):
    """One fake on a free port of 127.0.0.1. `answer(handler, body)` returns (status, object)."""
    class Handler(http.server.BaseHTTPRequestHandler):
        def _go(self):
            length = int(self.headers.get("Content-Length") or 0)
            body = self.rfile.read(length).decode("utf-8") if length else None
            status, obj = answer(self, body)
            data = obj if isinstance(obj, bytes) else json.dumps(obj).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        do_GET = _go
        do_POST = _go

        def log_message(self, *args):
            pass

    server = Quiet(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


# ===================================================================================================== the fake Graph


GRAPH = "https://graph.microsoft.com"


class FakeGraph:
    """Phase 2's fake Graph (docs/qa/mail-desk/phase-2/tests/conftest.py), served on a port: one per mailbox, told apart
    by the wrapper's fake token ("rig-token-for-<address>"). Folders of messages with the receive-time filter, the order
    and the paging the check asks for, and the move. It logs every call - time, mailbox, method, path with ids replaced,
    the folder and the destination - and never a body or a message id. A failure can be switched on for every mailbox
    or one: an HTTP status, or "not-json"."""

    def __init__(self):
        self.lock = threading.Lock()
        self.mailboxes = {}
        self.log = []
        self.fail = None
        self.fail_for = {}

    def box(self, address):
        return self.mailboxes.setdefault(address, {"inbox": [], "sentitems": [], "archive": [], "junkemail": []})

    def put(self, address, folder, *messages):
        with self.lock:
            self.box(address)[folder].extend(messages)

    def folder_of(self, address, mid):
        with self.lock:
            for name, folder in self.box(address).items():
                if any(m.get("id") == mid or m.get("id") == f"moved-{mid}" for m in folder):
                    return name
        return None

    def calls(self, since=None, method=None):
        with self.lock:
            return [c for c in self.log if (since is None or c["at"] >= since) and (method is None or c["method"] == method)]

    def answer(self, method, path, query, body, token):
        if not token.startswith("rig-token-for-"):
            return 401, {"error": {"code": "InvalidAuthenticationToken"}}
        address = token.removeprefix("rig-token-for-")
        segments = path.split("/")
        entry = {"at": time.time(), "utc": utc(), "mailbox": address, "method": method,
                 "path": re.sub(r"/messages/[^/]+/move$", "/messages/{id}/move", path)}
        with self.lock:
            self.log.append(entry)
            failure = self.fail_for.get(address, self.fail)
            if isinstance(failure, int):
                entry["answer"] = failure
                return failure, {"error": {"code": "RigSwitchedFailure"}}
            if failure == "not-json":
                entry["answer"] = "not json"
                return 200, b"<html>a proxy page</html>"
            box = self.box(address)
            if method == "GET" and path.startswith("/v1.0/me/mailFolders/") and path.endswith("/messages"):
                folder = segments[4]
                messages = list(box.get(folder, []))
                if "$filter" in query:
                    field, op, since = query["$filter"].split(" ")
                    messages = [m for m in messages if m[field] >= since]
                field, direction = query["$orderby"].split(" ")
                messages.sort(key=lambda m: m[field], reverse=(direction == "desc"))
                top, skip = int(query["$top"]), int(query.get("$skip", "0"))
                page = {"value": messages[skip:skip + top]}
                if skip + top < len(messages):
                    rest = {k: v for k, v in query.items() if k != "$skip"}
                    rest["$skip"] = str(skip + top)
                    page["@odata.nextLink"] = f"{GRAPH}{path}?{urllib.parse.urlencode(rest)}"
                entry.update(folder=folder, returned=len(page["value"]), answer=200)
                return 200, page
            if method == "POST" and len(segments) == 6 and segments[:4] == ["", "v1.0", "me", "messages"] \
                    and segments[5] == "move":
                mid, destination = segments[4], json.loads(body)["destinationId"]
                entry["destination"] = destination
                for folder in box.values():
                    for m in folder:
                        if m.get("id") == mid:
                            folder.remove(m)
                            box[destination].append(dict(m, id=f"moved-{mid}"))
                            entry["answer"] = 201
                            return 201, {"id": f"moved-{mid}"}
                entry["answer"] = 404
                return 404, {"error": {"code": "ErrorItemNotFound"}}
            entry["answer"] = 400
            return 400, {"error": {"code": "RigUnknownRequest"}}

    def handle(self, handler, body):
        url = urllib.parse.urlsplit(handler.path)
        query = {k: v[0] for k, v in urllib.parse.parse_qs(url.query).items()}
        token = (handler.headers.get("Authorization") or "").removeprefix("Bearer ")
        return self.answer(handler.command, url.path, query, body, token)

    def shown_log(self, since=None):
        return "\n".join(f"  {c['utc']} {c['mailbox']} {c['method']} {c['path']}"
                         + (f" folder={c['folder']} returned={c['returned']}" if "folder" in c else "")
                         + (f" destination={c['destination']}" if "destination" in c else "")
                         + f" -> {c.get('answer')}" for c in self.calls(since)) or "  (no calls)"


SEQUENCE = [0]


def mail(sender, subject, name=None, minutes_ago=0.0):
    """One Inbox message with a fresh id; its body preview starts with the marker that must be stored nowhere."""
    SEQUENCE[0] += 1
    when = datetime.now(timezone.utc) - timedelta(minutes=minutes_ago) + timedelta(milliseconds=SEQUENCE[0])
    return {"id": f"rigmsg{SEQUENCE[0]:04d}", "receivedDateTime": when.strftime("%Y-%m-%dT%H:%M:%S.%fZ")[:-4] + "Z",
            "subject": subject, "hasAttachments": False,
            "bodyPreview": f"{PREVIEW_MARKER} {SEQUENCE[0]}: a short made-up note for the rig.",
            "from": {"emailAddress": {"address": sender, "name": name or sender.split("@")[0].title()}}}


def sent_to(address):
    return {"id": f"rigsent-{address}", "sentDateTime": utc(),
            "toRecipients": [{"emailAddress": {"address": address}}], "ccRecipients": [], "bccRecipients": []}


# ===================================================================================================== the fake model


class FakeModel:
    """Chat completions with log probabilities, as the hosted Wingman answers them. The answer is scripted per subject
    (the prompt carries "Subject: ..."): for the person-or-spam question a label (person, spam), for the route question
    the start of a route's "handles" line (its letter is found from the lines in the prompt) or "unsure". Either can be "fail"
    (HTTP 500) or "off" (an answer that is not one of the letters). Unscripted: person, then unsure. It logs every
    call: the question, the subject and the answer - never the preview."""

    def __init__(self):
        self.lock = threading.Lock()
        self.script = {}
        self.log = []

    def say(self, subject, spam="person", route="unsure", confidence=0.97):
        with self.lock:
            self.script[subject] = {"spam": spam, "route": route, "confidence": confidence}

    def calls(self, since=None):
        with self.lock:
            return [c for c in self.log if since is None or c["at"] >= since]

    def handle(self, handler, body):
        if handler.command != "POST" or not handler.path.endswith("/chat/completions"):
            return 404, {"error": "the fake model answers only POST .../chat/completions"}
        if (handler.headers.get("Authorization") or "") != f"Bearer {MODEL_KEY}":
            return 401, {"error": "not the rig-only key"}
        request = json.loads(body)
        prompt = request["messages"][-1]["content"]
        subject = re.search(r"^Subject: (.*)$", prompt, re.M).group(1)
        question = "person-or-spam" if "real person writing to us" in prompt else "route"
        letters = dict((m.group(2).lower(), m.group(1)) for m in re.finditer(r"^([A-Z])\. (.*)$", prompt, re.M))
        with self.lock:
            plan = self.script.get(subject, {"spam": "person", "route": "unsure", "confidence": 0.97})
        wanted = plan["spam"] if question == "person-or-spam" else plan["route"]
        if wanted == "fail":
            answer = None
        elif wanted == "off":
            answer = "maybe"
        elif question == "person-or-spam":
            answer = {"person": "A", "spam": "B"}[wanted]
        else:
            prefix = "none of the above" if wanted == "unsure" else wanted.lower()   # MailDeskModel.UnsureHandles
            answer = next((letter for line, letter in letters.items() if line.startswith(prefix)), None)
            if answer is None:
                answer = "?"   # a route the prompt does not offer: answered off the labels, and the log says so
        with self.lock:
            self.log.append({"at": time.time(), "utc": utc(), "question": question, "subject": subject,
                             "model": request.get("model"), "logprobs": request.get("logprobs"),
                             "answer": answer if answer is not None else "HTTP 500"})
        if answer is None:
            return 500, {"error": {"message": "the rig's fake model was told to fail"}}
        logprob = math.log(plan["confidence"])
        return 200, {"id": "rig", "object": "chat.completion", "model": request.get("model"),
                     "choices": [{"index": 0, "finish_reason": "stop",
                                  "message": {"role": "assistant", "content": answer},
                                  "logprobs": {"content": [{"token": answer, "logprob": logprob,
                                                            "top_logprobs": [{"token": answer, "logprob": logprob}]}]}}],
                     "usage": {"prompt_tokens": 180, "completion_tokens": 1, "total_tokens": 181}}

    def shown_log(self, since=None):
        return "\n".join(f"  {c['utc']} {c['question']} | {c['subject']} -> {c['answer']} (model {c['model']}, "
                         f"logprobs {c['logprobs']})" for c in self.calls(since)) or "  (no calls)"


# ===================================================================================================== the Gateway


def sha(key):
    return hashlib.sha256(key.encode("utf-8")).hexdigest()


def query(sql, args=()):
    c = sqlite3.connect(f"file:{STATE['db']}?mode=ro", uri=True, timeout=10)
    try:
        return c.execute(sql, args).fetchall()
    finally:
        c.close()


def rows(title, sql, args=()):
    """Rows read straight from the rig's own gateway.db, for what no route serves."""
    c = sqlite3.connect(f"file:{STATE['db']}?mode=ro", uri=True, timeout=10)
    cur = c.execute(sql, args)
    names = [d[0] for d in cur.description]
    found = cur.fetchall()
    c.close()
    TRANSCRIPT.append(f"# {shown(title)}\n#   read from the rig's gateway.db: {shown(sql)} {shown(list(args))}\n"
                      + f"  {', '.join(names)}\n" + "".join(f"  {shown(', '.join(str(v) for v in r))}\n" for r in found)
                      + f"  ({len(found)} rows)\n")
    return found


def insert(c, table, row):
    """Insert a row, filling every other NOT NULL column that has no default with a neutral value of its type."""
    cols = {r[1]: r for r in c.execute(f"PRAGMA table_info({table})")}
    missing = [k for k in row if k not in cols]
    if missing:
        raise SystemExit(f"{table} has no column(s) {missing}; its columns are {list(cols)}")
    now = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
    for name, (_, _, kind, notnull, default, pk) in cols.items():
        if name in row or not notnull or default is not None:
            continue
        kind = (kind or "").upper()
        row[name] = 0 if "INT" in kind or "REAL" in kind else now if "Utc" in name else ""
    c.execute(f"INSERT INTO {table} ({', '.join(row)}) VALUES ({', '.join('?' for _ in row)})", list(row.values()))


def enroll():
    """The account `local` (a self-hosted Gateway keeps no account row, and the administrator route switches only an
    account it has), a person (a browser device), the test Director and the rig's stand-in backup Director
    (workstation devices), each with its own key."""
    c = sqlite3.connect(STATE["db"], timeout=30)
    now = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
    insert(c, "tenants", {"Id": "local", "AccountSubject": "qa-rig-local", "Email": "qa-owner@example.com",
                          "CreatedAtUtc": now})
    for device_id, key, kind, machine in (("qa-browser", PERSON_KEY, "browser", MACHINE),
                                          ("qa-director5", DIRECTOR_KEY, "workstation", MACHINE),
                                          ("qa-backup-director", BACKUP_KEY, "workstation", BACKUP_MACHINE)):
        insert(c, "device_credentials", {
            "DeviceId": device_id, "MachineName": machine, "DeviceKeyHash": sha(key), "KeyPrefix": key[:8],
            "KeyLast4": key[-4:], "IssuedAtUtc": now, "Status": "active", "Platform": "linux",
            "DeviceType": kind, "TenantId": "local"})
    c.commit()
    c.close()


def call(method, path, body=None, key=PERSON_KEY, headers=None):
    data = None if body is None else (body if isinstance(body, str) else json.dumps(body)).encode("utf-8")
    req = urllib.request.Request(STATE["gw"] + path, data=data, method=method,
                                 headers=dict({"Authorization": f"Bearer {key}", "Content-Type": "application/json"},
                                              **(headers or {})))
    try:
        with urllib.request.urlopen(req, timeout=60) as res:
            return res.status, res.read().decode("utf-8")
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode("utf-8")


def get(path, key=PERSON_KEY):
    status, text = call("GET", path, key=key)
    if status != 200:
        raise urllib.error.URLError(f"GET {path}: {status} {text[:200]}")
    return json.loads(text)


def raw(method, path, body, expect_status, key=PERSON_KEY, label="the rig's person"):
    """One request sent as raw JSON, kept exactly as answered."""
    status, answer = call(method, path, body, key=key)
    TRANSCRIPT.append(f"$ {method} {shown(path)}      (raw HTTP, as {label})\n"
                      + (f"  body: {shown(json.dumps(body) if not isinstance(body, str) else body)}\n" if body else "")
                      + f"  status: {status}\n  answer: {shown(answer) if answer else '(empty)'}\n")
    print(f"raw {method} {shown(path)}: {status}", flush=True)
    if expect_status is not None and status != expect_status:
        raise SystemExit(f"expected {expect_status} from {method} {path}, got {status}: {answer}")
    return status, (json.loads(answer) if answer.strip().startswith(("{", "[")) else answer)


def admin_switch(enabled, reason):
    status, answer = call("POST", "/gateway/admin/mail-desk",
                          {"account": "local", "enabled": enabled, "actor": "the Mail Desk rig", "reason": reason},
                          key=ADMIN_TOKEN)
    TRANSCRIPT.append(f"$ POST /gateway/admin/mail-desk      (raw HTTP, with the administrator token made for this run)\n"
                      f"  body: {{\"account\": \"local\", \"enabled\": {str(enabled).lower()}, \"actor\": \"the Mail Desk "
                      f"rig\", \"reason\": \"{shown(reason)}\"}}\n  status: {status}\n  answer: {shown(answer)}\n")
    if status != 200:
        raise SystemExit(f"the administrator switch answered {status}: {answer}")
    return json.loads(answer)


def cli_env(key):
    gw = STATE["gw"]
    if not gw.startswith("http://127.0.0.1:"):
        raise SystemExit(f"refusing to run cc-devthrottle against {gw}: only the throwaway Gateway")
    env = dict(without_cc(os.environ), CC_GATEWAY_URL=gw, CC_GATEWAY_SESSION_KEY=key, NO_COLOR="1", COLUMNS="200",
               PYTHONPATH=f"{CLI_DIR}{os.pathsep}{CLI_DIR.parent}", HOME=str(DIR_HOME))
    return env


def cli(args, expect_exit=0, key=PERSON_KEY, label="the rig's person"):
    """This tree's cc-devthrottle (tools/cc-devthrottle), run with the owner's tool Python, against the throwaway
    Gateway only."""
    r = subprocess.run([str(PYENV_PYTHON), "-c", "from src.cli import tool_main; tool_main()", *args],
                       cwd=str(CLI_DIR), env=cli_env(key), capture_output=True, text=True, timeout=300)
    line = " ".join(a if " " not in a else f'"{a}"' for a in args)
    TRANSCRIPT.append(f"$ cc-devthrottle {shown(line)}      (as {label})\n{shown(r.stdout)}{shown(r.stderr)}"
                      f"[exit {r.returncode}]\n")
    print(f"cli {shown(line)}: exit {r.returncode}", flush=True)
    if expect_exit is not None and r.returncode != expect_exit:
        raise SystemExit(f"expected exit {expect_exit} from: cc-devthrottle {line}\n{r.stdout}{r.stderr}")
    return r


def cli_json(args, key=PERSON_KEY, label="the rig's person"):
    return json.loads(cli([*args, "--json"], 0, key=key, label=label).stdout)


def start_gateway(model_url):
    GW_ROOT.mkdir(parents=True)
    (GW_ROOT / "config").mkdir()
    (GW_ROOT / "config" / "config.json").write_text(json.dumps(
        {"factoryAgents": {"enabled": True}, "mailDeskModelBaseUrl": model_url}, indent=2) + "\n", encoding="utf-8")
    (GW_ROOT / "keyvault.json").write_text(json.dumps({"DEVTHROTTLE_API_KEY": MODEL_KEY}), encoding="utf-8")
    GW_HOME.mkdir(parents=True)
    port = free_port()
    STATE["gw"] = f"http://127.0.0.1:{port}"
    # A minimal environment, not this shell's: nothing of the owner's (no CC_ variable, no service token, no key)
    # reaches it. DOTNET_ROOT: .NET 10 lives in ~/.dotnet on devlinux; /usr/lib/dotnet is 8.0.
    env = {"PATH": "/usr/local/bin:/usr/bin:/bin", "HOME": str(GW_HOME), "LANG": "C.UTF-8",
           "DOTNET_ROOT": str(DOTNET_ROOT), "DOTNET_USE_POLLING_FILE_WATCHER": "1", "CC_DIRECTOR_ROOT": str(GW_ROOT),
           "CC_GATEWAY_NO_TAILSCALE": "1", "ADMIN_SERVICE_TOKEN": ADMIN_TOKEN, "TZ": "UTC"}
    STATE["gw_log"] = open(RIG / "gateway.out.txt", "w", encoding="utf-8")
    gw = subprocess.Popen([str(GATEWAY_EXE), "--port", str(port)], env=env, stdout=STATE["gw_log"],
                          stderr=subprocess.STDOUT, cwd=str(GATEWAY_EXE.parent), start_new_session=True)
    STATE["gw_proc"] = gw
    wait_for("the throwaway Gateway wrote no gateway.db",
             lambda: bool(list(GW_ROOT.glob("gateway.db"))) or gw.poll() is not None, 120)
    if gw.poll() is not None:
        raise SystemExit("the throwaway Gateway exited; see <rig>/gateway.out.txt")
    STATE["db"] = GW_ROOT / "gateway.db"
    wait_for("the throwaway gateway.db did not get its tables", lambda: {"device_credentials", "tenants",
             "mail_desk_channels", "mail_desk_decisions", "trigger_runs"} <= {r[0] for r in query(
                 "SELECT name FROM sqlite_master WHERE type='table'")}, 120)
    enroll()
    wait_for("the throwaway Gateway did not answer", lambda: get("/factory-definition-schemas") is not None, 120)
    return gw


def gateway_log(title, contains):
    found = []
    for log in sorted((GW_ROOT / "logs").rglob("*.log")):
        for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
            if any(c in line for c in contains):
                found.append(line)
    record(f"{title} - the throwaway Gateway's log, lines naming {list(contains)}",
           "\n".join(f"  {x}" for x in found[-40:]) or "  (none)")
    return found


# ===================================================================================================== the Director


def registration(pid):
    folder = DIR_INSTANCE / "config" / "director" / "instances"
    for f in folder.glob("*.json") if folder.exists() else []:
        try:
            j = json.loads(f.read_text(encoding="utf-8-sig"))
        except (json.JSONDecodeError, OSError):
            continue
        if j.get("Pid") == pid and j.get("DirectorId"):
            return j
    return None


def make_seat_repository():
    """The repository the rig's seats and Mail Desk agent work in (a pooled checkout of it per session). Its project
    instruction file - which the agent loads at start - says what to do with each email by the [rig: ...] part of its
    subject. A routed session's first prompt is only the Mail Desk's text, which carries each subject but not the
    seat's brief, and on the rig seats followed that text (read the email with cc-outlook; return what you cannot read)
    rather than their brief."""
    SEAT_REPO.mkdir(parents=True, exist_ok=True)
    (SEAT_REPO / "CLAUDE.md").write_text(
        "# The Mail Desk QA rig\n\n"
        "This is a test rig. There is no mailbox and cc-outlook is not available: never try to read an email and never "
        "search for one. Every email the Mail Desk gives you carries, at the end of its subject, a part in square "
        "brackets that says exactly what to do with it. Do exactly that, with that email's routed id from your prompt, "
        "and nothing else:\n\n"
        f"- `[rig: close it with maildesk done]`: run `{RIG_BIN}/cc-devthrottle maildesk done <routed id> --note "
        "\"done by the rig's seat\"`\n"
        f"- `[rig: return it with maildesk return]`: run `{RIG_BIN}/cc-devthrottle maildesk return <routed id> --note "
        "\"returned: the rig asked for it\"`\n"
        "- `[rig: run nothing for this email]`: run nothing for it.\n\n"
        "Do not raise your hand, do not write memory, and run no other command. When every email is handled, stop.\n",
        encoding="utf-8")
    origin = SEAT_REPO.parent / "qa-seat-origin.git"   # the worktree pool fetches from an origin; this one is in the rig
    subprocess.run(["git", "init", "-q", "--bare", "-b", "main", str(origin)], check=True, capture_output=True, text=True)
    git = ["git", "-C", str(SEAT_REPO), "-c", "user.name=qa-rig", "-c", "user.email=qa-rig@example.com"]
    for args in (["init", "-q", "-b", "main"], ["add", "CLAUDE.md"], ["commit", "-q", "-m", "the rig's seat instructions"],
                 ["remote", "add", "origin", str(origin)], ["push", "-q", "-u", "origin", "main"]):
        subprocess.run(git + args, check=True, capture_output=True, text=True)
    record("the seats' repository <rig>/repos/qa-seat: its CLAUDE.md", (SEAT_REPO / "CLAUDE.md").read_text())


def prepare_director():
    """The test Director's own storage root and home. Its Gateway is the throwaway one, with its own key. Its PATH
    starts with the rig's bin, where `cc-devthrottle` is THIS tree's command (the seats run maildesk done and return,
    which the installed one may not know), then ~/.local/bin for uv and the agent."""
    config = DIR_INSTANCE / "config"
    (config / "director").mkdir(parents=True, exist_ok=True)
    real = json.loads((OWNER_INSTANCE / "config" / "config.json").read_text(encoding="utf-8-sig"))
    claude = [e for e in real.get("agent", {}).get("entries", []) if e.get("type") == "ClaudeCode"]
    if not claude:
        raise SystemExit("the owner's Director knows no ClaudeCode agent entry to copy the executable path from")
    (config / "config.json").write_text(json.dumps({
        "onboarding": {"completed": True},
        "autoUpdate": {"enabled": False},
        "gateway": {"url": STATE["gw"], "token": DIRECTOR_KEY, "streamMode": True},
        "agent": dict(real.get("agent", {}), entries=claude),
    }, indent=2), encoding="utf-8")
    if not SEAT_REPO.exists():
        make_seat_repository()
    (config / "director" / "repositories.json").write_text(json.dumps([{"Name": "qa-seat", "Path": str(SEAT_REPO)}],
                                                                      indent=2), encoding="utf-8")
    DIR_HOME.mkdir(parents=True, exist_ok=True)
    # The agent keeps its sign-in in ~/.claude and its settings in ~/.claude.json. The seats must run signed in, so the
    # scratch home links to the owner's two (the Director passes its hooks to each session with --settings and writes
    # its folder-trust entries for the rig's folders into ~/.claude.json, as the owner's own Director does).
    for name in (".claude", ".claude.json"):
        if (USER_HOME / name).exists() and not (DIR_HOME / name).is_symlink():
            os.symlink(USER_HOME / name, DIR_HOME / name)
    RIG_BIN.mkdir(parents=True, exist_ok=True)
    tool = RIG_BIN / "cc-devthrottle"
    tool.write_text("#!/bin/sh\n"
                    f"PYTHONPATH='{CLI_DIR}:{CLI_DIR.parent}' exec '{PYENV_PYTHON}' -c "
                    "'import sys; sys.argv[0] = \"cc-devthrottle\"; from src.cli import tool_main; tool_main()' \"$@\"\n",
                    encoding="utf-8")
    tool.chmod(0o755)
    # The routed prompt tells a seat to read each email with cc-outlook. On the rig there is no mailbox to read, and a
    # seat must never reach a real one, so cc-outlook here is a stub that refuses and points at check.json.
    outlook = RIG_BIN / "cc-outlook"
    outlook.write_text("#!/bin/sh\necho 'cc-outlook is not available on the Mail Desk rig: there is no mailbox to read. "
                       "Work from the emails in check.json in your kit.' >&2\nexit 1\n", encoding="utf-8")
    outlook.chmod(0o755)


def director_env():
    env = without_cc(os.environ)
    for k in ("ADMIN_SERVICE_TOKEN", "REPORT_SERVICE_TOKEN", "NOTIFY_OWNER_SERVICE_TOKEN", "DEVTHROTTLE_API_KEY"):
        env.pop(k, None)
    env.update(CC_DIRECTOR_ROOT=str(DIR_ROOT), CC_AUTOUPDATE="0", CC_GATEWAY_NO_TAILSCALE="1", HOME=str(DIR_HOME),
               UV_CACHE_DIR=str(USER_HOME / ".cache" / "uv"), DOTNET_ROOT=str(DOTNET_ROOT))
    env["PATH"] = f"{RIG_BIN}:{USER_BIN}:/usr/local/bin:/usr/bin:/bin"
    return env


def start_director():
    prepare_director()
    STATE["dir_log"] = open(RIG / "director.out.txt", "w", encoding="utf-8")
    director = subprocess.Popen([str(DIRECTOR_EXE)], env=director_env(), stdout=STATE["dir_log"],
                                stderr=subprocess.STDOUT, cwd=str(DIRECTOR_EXE.parent), start_new_session=True)
    STATE["dir_proc"] = director
    reg = wait_for("the test Director wrote no instance registration", lambda: registration(director.pid), 120)
    STATE["director_id"] = reg["DirectorId"]
    wait_for("the test Director did not join the throwaway Gateway",
             lambda: [d for d in get("/directors") if d.get("directorId") == reg["DirectorId"]], 120, 2)
    return director, reg


def rig_processes(but=()):
    """Every process whose command line names the rig folder, other than the ids in `but`."""
    found = []
    for d in Path("/proc").iterdir():
        if not d.name.isdigit() or int(d.name) in but:
            continue
        try:
            cmd = (d / "cmdline").read_bytes().replace(b"\0", b" ").decode("utf-8", "replace")
        except OSError:
            continue
        if str(RIG) in cmd:
            found.append((int(d.name), cmd[:160]))
    return found


def stop_director(proc, director_id):
    """The Unix arm of the named signal: a request file the Director polls. Never a kill."""
    folder = DIR_ROOT / "config" / "lifecycle-signals"
    folder.mkdir(parents=True, exist_ok=True)
    request = folder / f"cc-director-shutdown-{director_id.lower()}.request"
    request.write_text(utc(), encoding="utf-8")
    try:
        proc.wait(timeout=90)
        gone = True
    except subprocess.TimeoutExpired:
        gone = False
    record("the test Director asked to stop by its lifecycle request file",
           f"  request: {request}\n  pid {proc.pid} exited: {gone} (exit code {proc.returncode})")
    if not gone:
        print(f"the test Director {proc.pid} did NOT exit; it was not killed - stop it by hand", flush=True)
    return gone


def director_log(title, contains, since=None):
    found = []
    for log in sorted((DIR_INSTANCE / "logs" / "director").glob("*.log")):
        for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
            if any(c in line for c in contains) and (since is None or line[:19] >= since):
                found.append(line)
    record(f"{title} - the test Director's log, lines naming {list(contains)}",
           "\n".join(f"  {x}" for x in found[-30:]) or "  (none)")
    return found


def user_bin_links(title):
    lines = [f"{p.name} -> {os.readlink(p)}" for p in sorted(USER_BIN.iterdir()) if p.is_symlink()]
    record(title, "\n".join(f"  {x}" for x in lines))
    return lines


# ===================================================================================================== the definitions


def block(text, indent="      "):
    return "".join(f"{indent}{x}\n" for x in text.splitlines())


RIG_AGENT_NOTE = ("QA RIG: this Mail Desk runs on a test rig. There is no mailbox and cc-outlook is not available. For "
                  "every message handed to you whose subject says [rig: return it with maildesk return], run exactly: "
                  f"{RIG_BIN}/cc-devthrottle "
                  "maildesk return <routed id> --note \"the rig's Mail Desk agent cannot place it\" - and nothing else. "
                  "Change no list.")


def mail_desk_definition(backups=()):
    """The template, as the account's draft holds it, with rig changes, each recorded: the machine is this one, the
    cheapest model, and a rig note at the end of the Mail Desk agent's brief; the start runs every minute; checks/screen.py is the rig's wrapper and checks/real_screen.py the template's real
    check, byte for byte; files/rig.json tells the wrapper where the rig is. A backup machine list is added for the
    backup case only."""
    folder = RIG / "definitions" / "mail-desk"
    if folder.exists():
        shutil.rmtree(folder)
    shutil.copytree(TEMPLATE, folder, ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    factory = folder / "factory.yaml"
    factory.write_text(factory.read_text(encoding="utf-8").replace("machine: devlinux", f"machine: {MACHINE}"),
                       encoding="utf-8")
    factory.write_text(factory.read_text(encoding="utf-8").replace("  agent: ClaudeCode\n",
                                                                   f"  agent: ClaudeCode\n  model: {CHEAP_MODEL}\n"),
                       encoding="utf-8")
    factory.write_text(factory.read_text(encoding="utf-8").rstrip("\n") + "\nrepositories:\n  - qa-seat\n",
                       encoding="utf-8")
    seat = folder / "seats" / "mail-desk.yaml"
    text = seat.read_text(encoding="utf-8").replace("role: Mail Desk\n", "role: Mail Desk\nrepository: qa-seat\n", 1)
    if "\nstarts:\n" not in text:
        raise SystemExit("the template's Mail Desk seat no longer has a 'starts:' line; the rig must be updated")
    text = text.replace("\nstarts:\n", "\n" + block(RIG_AGENT_NOTE, "  ") + "starts:\n", 1)
    if "  - every: 5m\n" not in text:
        raise SystemExit("the template's Mail Desk start no longer reads '  - every: 5m'; the rig must be updated")
    extra = f"    backupMachines: [{', '.join(backups)}]\n" if backups else ""
    seat.write_text(text.replace("  - every: 5m\n", f"  - every: {INTERVAL}\n{extra}"), encoding="utf-8")
    real = folder / "checks" / "real_screen.py"
    shutil.move(folder / "checks" / "screen.py", real)
    shutil.copyfile(WRAPPER, folder / "checks" / "screen.py")
    (folder / "files").mkdir(exist_ok=True)
    (folder / "files" / "rig.json").write_text(json.dumps(
        {"rigFolder": str(RIG), "signInFolder": str(SIGN_IN), "fakeGraph": STATE["graph_url"]}, indent=2) + "\n",
        encoding="utf-8")
    same = hashlib.sha256(real.read_bytes()).hexdigest() == hashlib.sha256(
        (TEMPLATE / "checks" / "screen.py").read_bytes()).hexdigest()
    record("the rig's mail-desk definition folder (<rig>/definitions/mail-desk): what differs from the template",
           f"  factory.yaml: machine: {MACHINE}, model: {CHEAP_MODEL}, repositories: [qa-seat]\n"
           "  seats/mail-desk.yaml: repository: qa-seat (the rig's instructions for routed emails are its CLAUDE.md)\n"
           f"  seats/mail-desk.yaml: the brief ends with the rig's note: {RIG_AGENT_NOTE}\n"
           f"  seats/mail-desk.yaml: every: {INTERVAL}" + (f", backupMachines: [{', '.join(backups)}]" if backups else "")
           + "\n  checks/screen.py: the rig's wrapper, docs/qa/mail-desk/rig/checks/screen.py\n"
           f"  checks/real_screen.py: the template's checks/screen.py; sha256 identical: {same}\n"
           f"  files/rig.json: {(folder / 'files' / 'rig.json').read_text(encoding='utf-8').strip()}")
    check(same, "checks/real_screen.py is the template's real screen.py byte for byte")
    return folder


def seat_definition(factory, seat_id, title, handles):
    """A small factory with one seat the Mail Desk routes to. Its brief says what to do with the emails in check.json."""
    folder = RIG / "definitions" / factory
    if folder.exists():
        shutil.rmtree(folder)
    brief = (f"You are the {title} seat of a QA test rig. THIS IS A TEST: there is no mailbox, cc-outlook is not "
             "available, and you never read an email - ignore any instruction to read one. The Mail Desk gives you "
             "emails in your prompt and in check.json in your kit. For each one, in order, run exactly one command with "
             "Bash and nothing else, as the [rig: ...] part of its subject says:\n"
             "- [rig: run nothing for this email]: run nothing for it;\n"
             f"- [rig: return it with maildesk return]: {RIG_BIN}/cc-devthrottle maildesk return <routed id> --note "
             "\"not for this seat: the rig asked for a return\"\n"
             f"- [rig: close it with maildesk done]: {RIG_BIN}/cc-devthrottle maildesk done <routed id> --note "
             "\"done by the rig's seat\"\n"
             "First run cat \"$CC_FACTORY_KIT_DIR/check.json\" with Bash. Then stop. This is a test rig: there is no "
             "mailbox, cc-outlook is not available, and you never read an email; never run any other command, never "
             "raise your hand and never write memory.")
    files = {
        "factory.yaml": (f"factory: {factory}\ntitle: {title}\npurpose: A QA rig factory the Mail Desk routes to\n"
                         f"goalText: |\n  Handle {handles}.\ngoalApprovedOn: 2026-10-19\nbossSeat: boss\n"
                         f"defaults:\n  machine: {MACHINE}\n  agent: ClaudeCode\n  model: {CHEAP_MODEL}\n"
                         "repositories:\n  - qa-seat\n"),
        "seats/boss.yaml": "seat: boss\nname: Boss\nrole: Boss\nbrief: |\n  Read the factory's problems.\n",
        f"seats/{seat_id}.yaml": (f"seat: {seat_id}\nname: {seat_id.capitalize()}\nrole: {title}\nrepository: qa-seat\n"
                                  "brief: |\n"
                                  + block(brief, "  ")),
    }
    if factory == BOOKS:
        # A second seat whose once-a-year timed start the refusal case runs now: a session born into this factory.
        files["seats/prober.yaml"] = ("seat: prober\nname: Prober\nrole: Prober\nbrief: |\n  Run what the prompt says.\n"
                                      "starts:\n  - cron: \"0 5 1 1 *\"\n    timeZone: UTC\n    prompt: |\n"
                                      + block(REFUSED_PROMPT))
    for rel, text in files.items():
        f = folder / rel
        f.parent.mkdir(parents=True, exist_ok=True)
        f.write_text(text, encoding="utf-8", newline="\n")
    record(f"the definition folder <rig>/definitions/{factory}",
           "\n".join(f"  --- {rel}\n" + block(text, "    ") for rel, text in files.items()))
    return folder


def save_and_publish(factory, folder, note_text):
    """Save the folder on top of the newest version (the template's offered draft, for mail-desk) and publish it."""
    page = cli_json(["factory", "definition", "history", factory])
    newest = max([v.get("version", 0) for v in page.get("versions", [])] or [0])
    published = page.get("publishedVersion") or 0
    r = cli(["factory", "definition", "save", factory, "--dir", str(folder), "--expected-version", str(newest),
             "--note", note_text, "--json"])
    version = json.loads(r.stdout)["version"]
    cli(["factory", "definition", "publish", factory, "--version", str(version), "--replaces", str(published)])
    return version


# ===================================================================================================== readers


def plant_sign_in(address):
    """A saved sign-in where cc-outlook keeps one - in the rig's scratch sign-in folder - and cc-outlook's account list
    naming it, so the check's own fix sentence can name the account. The file holds no token: the wrapper only looks
    for it."""
    tokens = SIGN_IN / "instances" / "default" / "config" / "outlook" / "tokens"
    tokens.mkdir(parents=True, exist_ok=True)
    (tokens / (address.replace("@", "_").replace(".", "_") + "_msal.json")).write_text("{}", encoding="utf-8")
    profiles = SIGN_IN / "instances" / "default" / "config" / "outlook" / "profiles.json"
    known = json.loads(profiles.read_text(encoding="utf-8")) if profiles.exists() else {"profiles": {}}
    known["profiles"][address] = {"client_id": "rig", "nickname": address.split("@")[0]}
    known["default"] = next(iter(known["profiles"]))
    profiles.write_text(json.dumps(known, indent=2), encoding="utf-8")


def trigger_id():
    return next(t["id"] for t in get("/triggers")["triggers"]
                if t.get("factory") == "mail-desk" and t.get("seat") == "mail-desk")


def runs():
    return query("SELECT Id, CheckedUtc, RecordedUtc, Outcome, Count, SessionId, Reason, DirectorId FROM trigger_runs "
                 "WHERE TriggerId = ? COLLATE NOCASE ORDER BY RecordedUtc", (STATE["trigger"],))


def wait_run_after(moment, what, seconds=300, outcome=None):
    """The first Mail Desk run whose check ran entirely after `moment` (its CheckedUtc - the end of the check - more
    than 20 seconds later, longer than a check takes on the rig), so it saw what the rig changed before `moment`."""
    after = (moment + timedelta(seconds=20)).strftime("%Y-%m-%d %H:%M:%S")

    def found():
        for r in runs():
            if str(r[1])[:19].replace("T", " ") > after and (outcome is None or r[3] == outcome):
                return r
        return None
    run = wait_for(f"no Mail Desk run after {utc(moment)} ({what})", found, seconds=seconds, every=3)
    record(f"the run that saw it ({what}): Id, CheckedUtc, RecordedUtc, Outcome, Count, SessionId, Reason, DirectorId",
           f"  {run}")
    return run


def now():
    return datetime.now(timezone.utc)


def decisions(channel=None, n=200):
    path = f"/gateway/mail-desk/decisions?n={n}" + (f"&channel={urllib.parse.quote(channel)}" if channel else "")
    return get(path)["decisions"]


def decision_of(subject, channel=None):
    found = [d for d in decisions(channel) if d.get("subject") == subject]
    return found[0] if found else None


def show_decisions(title, channel=None, n=20):
    args = ["maildesk", "decisions", "-n", str(n)] + (["--channel", channel] if channel else [])
    cli(args)


def memory_of(address):
    """The stored memory's entry for one channel, newest row, from the Gateway's database."""
    found = query("SELECT Content FROM factory_check_state WHERE OwnerKey = ? COLLATE NOCASE ORDER BY Sequence DESC "
                  "LIMIT 1", (f"trigger:{STATE['trigger']}",))
    if not found:
        return None
    memory = json.loads(found[0][0])
    return next((c for c in memory.get("channels", []) if c.get("address") == address), None)


def listed_sessions(include_exited=False):
    answer = get("/sessions" + ("?includeExited=true" if include_exited else ""))
    items = answer.get("sessions", answer.get("items", [])) if isinstance(answer, dict) else answer
    return [s for s in items if isinstance(s, dict)]


def problems():
    return cli_json(["run", "problems", "--factory", "mail-desk"])


def marker_anywhere(title):
    """The body-preview marker in any table of the Gateway's database or its log: none."""
    hits = []
    for (table,) in query("SELECT name FROM sqlite_master WHERE type='table'"):
        cols = [r[1] for r in query(f"PRAGMA table_info({table})")]
        for col in cols:
            try:
                n = query(f"SELECT COUNT(*) FROM {table} WHERE CAST({col} AS TEXT) LIKE ?", (f"%{PREVIEW_MARKER}%",))[0][0]
            except sqlite3.Error:
                continue
            if n:
                hits.append(f"{table}.{col}: {n}")
    for log in sorted((GW_ROOT / "logs").rglob("*.log")):
        if PREVIEW_MARKER in log.read_text(encoding="utf-8", errors="replace"):
            hits.append(f"log {log.name}")
    record(f"{title}: the body-preview marker '{PREVIEW_MARKER}' in every table and column of gateway.db and in the "
           "Gateway's log", "\n".join(f"  {h}" for h in hits) or "  (found nowhere)")
    return hits


# ===================================================================================================== the stand-in backup


class StandInDirector:
    """The rig standing in as a SECOND Director on another machine, `qa-backup` (devlinux cannot run a second Director
    under another machine name: it has one host name, and this account may not make a namespace to change it). It does
    what a Director does for a definition check, and nothing else:
      - holds a Director stream connection (SignalR, JSON protocol, over a WebSocket to /director-stream) with its own
        workstation key, and says Hello with machine `qa-backup` and DefinitionChecks true, as a Director built from this
        tree says it - the Gateway hands definition checks only to a Director that said so;
      - asks GET /directors/{id}/triggers for its assignments, runs the assigned check - the SAME wrapper and real
        screen.py the rig published, from a copy under the rig folder, with the memory the Gateway handed - and
        reports it with POST /directors/{id}/triggers/{trigger}/checks, as DefinitionCheckRunner does.
    The check's own sentences name the host it ran on (devlinux), not qa-backup; the Gateway records the claim's
    machine as qa-backup."""

    def __init__(self):
        self.director_id = str(uuid.uuid4())
        self.ws = None
        self.stop = threading.Event()
        self.folder = RIG / "backup-director"

    def connect(self):
        from websockets.sync.client import connect
        url = STATE["gw"].replace("http://", "ws://") + "/director-stream"
        self.ws = connect(url, additional_headers={"Authorization": f"Bearer {BACKUP_KEY}"}, open_timeout=30).__enter__()
        self.ws.send(json.dumps({"protocol": "json", "version": 1}) + "\x1e")
        handshake = self.ws.recv(timeout=30)
        if handshake.strip("\x1e") != "{}":
            raise SystemExit(f"the Director stream refused the handshake: {handshake}")
        hello = {"directorId": self.director_id, "version": "rig", "machineName": BACKUP_MACHINE, "user": "rig",
                 "pid": os.getpid(), "startedAt": utc(), "definitionChecks": True,
                 "answersFactoryMachineQuestion": False, "answersFactoryCheckMachineQuestion": False}
        self.ws.send(json.dumps({"type": 1, "invocationId": "1", "target": "Hello", "arguments": [hello]}) + "\x1e")
        answer = self.wait_completion("1")
        threading.Thread(target=self.keep_alive, daemon=True).start()
        return answer

    def wait_completion(self, invocation):
        deadline = time.time() + 30
        while time.time() < deadline:
            for frame in self.ws.recv(timeout=30).split("\x1e"):
                if not frame:
                    continue
                message = json.loads(frame)
                if message.get("type") == 3 and message.get("invocationId") == invocation:
                    if message.get("error"):
                        raise SystemExit(f"the Gateway refused the stand-in's Hello: {message['error']}")
                    return message.get("result")
                if message.get("type") == 7:
                    raise SystemExit(f"the Gateway closed the stand-in's stream: {message}")
        raise SystemExit("the Gateway did not answer the stand-in's Hello")

    def keep_alive(self):
        """A ping every 10 seconds, and every frame the Gateway sends read and dropped (the stand-in runs no sessions)."""
        last = 0.0
        while not self.stop.is_set():
            try:
                if time.time() - last > 10:
                    self.ws.send(json.dumps({"type": 6}) + "\x1e")
                    last = time.time()
                try:
                    self.ws.recv(timeout=1)
                except TimeoutError:
                    pass
            except Exception:   # the stream closed: the rig is ending, or the Gateway dropped it; nothing to keep alive
                return

    def close(self):
        self.stop.set()
        if self.ws is not None:
            try:
                self.ws.close()
            except Exception:
                pass

    def assignments(self):
        status, text = call("GET", f"/directors/{self.director_id}/triggers", key=BACKUP_KEY)
        if status != 200:
            raise SystemExit(f"the stand-in's assignments answered {status}: {text}")
        return [t for t in json.loads(text).get("triggers", []) if t.get("factory") == "mail-desk"]

    def run_check(self, assignment):
        """The assigned check, run the way DefinitionCheckRunner runs it: uv run --script, in the version's folder, with
        only CC_CHECK_ variables of its own and the memory in a fresh state file, read back after."""
        if self.folder.exists():
            shutil.rmtree(self.folder)
        shutil.copytree(RIG / "definitions" / "mail-desk", self.folder)
        state = RIG / "backup-state"
        state.write_text(assignment.get("state") or "", encoding="utf-8")
        env = {"PATH": f"{USER_BIN}:/usr/local/bin:/usr/bin:/bin", "HOME": str(RIG / "backup-home"), "LANG": "C.UTF-8",
               "UV_CACHE_DIR": str(USER_HOME / ".cache" / "uv"), "CC_CHECK_STATE_FILE": str(state),
               "CC_CHECK_SECRET_VARS": "[]", "CC_CHECK_FACTORY": "mail-desk", "CC_CHECK_SEAT": "mail-desk"}
        (RIG / "backup-home").mkdir(exist_ok=True)
        r = subprocess.run([str(USER_BIN / "uv"), "run", "--script", "checks/screen.py"], cwd=str(self.folder), env=env,
                           capture_output=True, text=True, timeout=300)
        return {"checkedAtUtc": utc(), "exitCode": r.returncode, "timedOut": False, "output": r.stdout[:16384],
                "errorOutput": r.stderr[:16384], "state": state.read_text(encoding="utf-8"), "stateOversize": False}

    def report(self, trigger, body, label):
        return raw("POST", f"/directors/{self.director_id}/triggers/{trigger}/checks", body, None, key=BACKUP_KEY,
                   label=label)


# ===================================================================================================== sessions

CLAUDE_PROJECTS = USER_HOME / ".claude" / "projects"
POINTER = "❯"
TENDED = {}
USED_TRANSCRIPTS = set()


def buffer_of(sid):
    answer = get(f"/sessions/{sid}/buffer")
    if isinstance(answer, dict):
        for key in ("text", "Text", "buffer", "Buffer"):
            if isinstance(answer.get(key), str):
                return answer[key]
    return answer if isinstance(answer, str) else ""


def keys(sid, text):
    call("POST", f"/sessions/{sid}/prompt", {"text": text, "appendEnter": False})


def tend_sessions():
    """The agent asks whether to trust a folder it has never opened, and a session's first prompt waits behind that
    question. The rig answers it the way a person at the screen would, and records that it did. Nothing else is ever
    typed into a session."""
    for s in listed_sessions():
        sid = s.get("sessionId") or s.get("id")
        if not sid or TENDED.get(sid):
            continue
        try:
            flat = re.sub(r"\s+", "", buffer_of(sid))
        except (urllib.error.URLError, OSError):
            continue
        if "Itrustthisfolder" not in flat or POINTER not in flat:
            continue
        chosen = flat[flat.rindex(POINTER) + 1:]
        if chosen.startswith("Yes"):
            keys(sid, "\r")
            TENDED[sid] = 1
            record(f"the rig answered the agent's folder-trust question in session {sid}",
                   "  answered: Enter, with the pointer on 'Yes, I trust this folder'")
            time.sleep(2)
        elif chosen.startswith("No"):
            keys(sid, "\u001b[B")
            time.sleep(1)


def wait_tending(what, ready, seconds=420, every=3):
    def tended():
        tend_sessions()
        return ready()
    return wait_for(what, tended, seconds=seconds, every=every)


def entries(path):
    out = []
    for line in path.read_text(encoding="utf-8").splitlines():
        try:
            out.append(json.loads(line))
        except json.JSONDecodeError:
            pass
    return out


def find_transcript(marker, since, seconds=420):
    def found():
        for p in sorted((p for p in CLAUDE_PROJECTS.glob("*/*.jsonl") if p.stat().st_mtime >= since),
                        key=lambda p: p.stat().st_mtime, reverse=True):
            try:
                text = p.read_text(encoding="utf-8")
            except OSError:
                continue
            if marker in text and p not in USED_TRANSCRIPTS:
                return p
        return None
    return wait_tending(f"no agent transcript carrying '{marker}' appeared", found, seconds=seconds, every=2)


def finished(path):
    last = [e for e in entries(path) if e.get("type") == "assistant"]
    return bool(last) and (last[-1].get("message") or {}).get("stop_reason") == "end_turn"


def session_folder(sid):
    """Where a session works: its repository checkout (a seat with a repository works in a pooled worktree), from the
    session list."""
    row = wait_for(f"session {sid} is not in the session list", lambda: next(
        (x for x in listed_sessions() if (x.get("sessionId") or x.get("id")) == sid and x.get("repoPath")), None),
        seconds=120, every=2)
    return row["repoPath"]


def wait_session_turn(sid, marker=None, seconds=900):
    """A seat session's own transcript - in the agent's projects folder named after the folder the session works in -
    once its turn has finished. A pooled checkout is used by one session after another, so the transcript is the newest
    one there that carries `marker` (one of the session's routed ids) when one is given."""
    folder = re.sub(r"[^A-Za-z0-9]", "-", session_folder(sid))

    def found():
        files = sorted(CLAUDE_PROJECTS.glob(f"{folder}/*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)
        for f in files:
            if marker is None or marker.lower() in f.read_text(encoding="utf-8", errors="replace").lower():
                return f
        return None
    path = wait_tending(f"no transcript for session {sid} appeared", found, seconds=seconds, every=2)
    USED_TRANSCRIPTS.add(path)
    wait_tending(f"session {sid} did not finish its turn", lambda: finished(path), seconds=seconds)
    time.sleep(3)
    return path


def routed_ids_of(sid):
    return [r[0] for r in routed_rows() if (r[4] or "").lower() == sid.lower()]


def wait_answer(marker, since, seconds=900):
    path = find_transcript(marker, since)
    USED_TRANSCRIPTS.add(path)
    wait_tending(f"the session carrying '{marker}' did not finish its turn", lambda: finished(path), seconds=seconds)
    time.sleep(3)
    return path


def text_of(content):
    if isinstance(content, str):
        return content
    return "\n".join(c.get("text", "") if isinstance(c, dict) and c.get("type") == "text" else c if isinstance(c, str)
                     else "" for c in content or [])


def summarize(title, path):
    """What the agent was asked and did, from its own transcript: its first prompt, each tool call and result, and its
    last words."""
    lines = [f"transcript: <home>/.claude/projects/{path.parent.name}/{path.name}"]
    first_user = next((e for e in entries(path) if e.get("type") == "user"), None)
    if first_user:
        lines.append("its first prompt, as the session received it:")
        lines += [f"    {x}" for x in text_of((first_user.get("message") or {}).get("content")).splitlines()]
    seen = set()
    for e in entries(path):
        content = (e.get("message") or {}).get("content")
        if not isinstance(content, list):
            continue
        for c in content:
            if not isinstance(c, dict):
                continue
            key = (c.get("type"), c.get("id") or c.get("tool_use_id"))
            if key[1]:
                if key in seen:
                    continue
                seen.add(key)
            if c.get("type") == "tool_use":
                lines.append(f"tool call: {c.get('name')} {json.dumps(c.get('input'))[:600]}")
            elif c.get("type") == "tool_result":
                lines.append(f"tool result{' (error)' if c.get('is_error') else ''}: {text_of(c.get('content'))[:2000]}")
            elif c.get("type") == "text" and e.get("type") == "assistant":
                lines.append(f"agent: {c.get('text', '')[:1500]}")
    record(f"{title} - read from the session's own agent transcript", "\n".join(f"  {x}" for x in lines))
    return "\n".join(lines)


def stop_session(sid):
    cli(["session", "stop", sid, "--reason", "the Mail Desk rig is done with this session"], None)


# ===================================================================================================== the sections


def section_setup():
    section("SETUP", "The account's Mail Desk: the switch on, the template offered, the rig's definition published, two "
                     "mailboxes added in observe mode, the routes and lists, and the two factories the Mail Desk "
                     "routes to.")
    on = admin_switch(True, "the Mail Desk rig: switch Mail Desk on for the throwaway account")
    check(on.get("enabled") is True, "the administrator route switched Mail Desk on for the account")
    cli(["maildesk", "status"])
    cli(["factory", "definition", "history", "mail-desk"])
    for factory, seat_id, title, handles in ((BOOKS, CLERK, "QA Books", BOOKS_HANDLES),
                                             (SUPPORT, DESK, "QA Support", SUPPORT_HANDLES)):
        save_and_publish(factory, seat_definition(factory, seat_id, title, handles), f"the rig's {title} factory")
    save_and_publish("mail-desk", mail_desk_definition(), "the rig's Mail Desk: the template with the rig's wrapper")
    cli(["trigger", "list"])
    STATE["trigger"] = trigger_id()
    # A mailbox may name only a machine one of the account's Directors has reported, so the stand-in backup Director
    # says Hello now; it polls nothing until the backup section.
    stand_in = StandInDirector()
    STATE["stand_in"] = stand_in
    record("the stand-in backup Director's Hello on the Director stream, as the Gateway answered it",
           json.dumps(stand_in.connect(), indent=1))
    for address in (ONE, TWO):
        cli(["maildesk", "channel", "add", address, "--provider", "outlook", "--machine", MACHINE, "--machine",
             BACKUP_MACHINE, "--why", "the rig's mailbox, read on this machine, then the backup"])
    cli(["maildesk", "route", "add", f"{BOOKS}/{CLERK}", "--handles", BOOKS_HANDLES,
         "--why", "the rig's books seat"])
    cli(["maildesk", "route", "add", f"{SUPPORT}/{DESK}", "--handles", SUPPORT_HANDLES,
         "--why", "the rig's support seat"])
    cli(["maildesk", "rule", "add", "invoices", "--subject-contains", "invoice", "--route", f"{BOOKS}/{CLERK}", "--why",
         "invoices go to the books seat"])
    cli(["maildesk", "allow", "add", "*@notices.example.com", "--what", "build notices", "--do", "archive", "--why",
         "automated notices nobody reads"])
    cli(["maildesk", "allow", "add", "news@example.org", "--what", "a letter the owner reads", "--do", "keep-for-owner",
         "--why", "the owner reads it"])
    cli(["maildesk", "block", "add", "*@spam.example.net", "--why", "cold sales, never answered"])
    cli(["maildesk", "rules", "show"])
    cli(["maildesk", "channel", "list"])
    plant_sign_in(ONE)
    record("the rig's scratch sign-in folder: only qa-one is signed in (qa-two is the not-signed-in case)",
           "\n".join(f"  {p.relative_to(SIGN_IN)}" for p in sorted(SIGN_IN.rglob("*")) if p.is_file()))


def section_observe():
    section("OBSERVE MODE AND NOT SIGNED IN", "Both mailboxes in observe mode. qa-one holds one email for each layer; "
                                              "qa-two is not signed in. Expected: every email decided and recorded by "
                                              "the layer that decides it, nothing moved (no POST on the fake Graph), "
                                              "no session started, and qa-two's failure on the Open problems card "
                                              "naming the fix.")
    graph, model = STATE["graph"], STATE["model"]
    graph.put(ONE, "sentitems", sent_to("friend@known.example"))
    emails = [
        mail("billing@vendor.example", "Invoice 1042 for October"),
        mail("alerts@notices.example.com", "Build 311 passed"),
        mail("news@example.org", "The October letter"),
        mail("deals@spam.example.net", "Cheap watches"),
        mail("stranger@cold.example", "Grow your traffic 300 percent"),
        mail("customer@client.example", "How do I export my data?"),
        mail("someone@unknown.example", "A question about something"),
        mail("friend@known.example", "Lunch on Friday?"),
    ]
    model.say("Grow your traffic 300 percent", spam="spam", confidence=0.98)
    model.say("How do I export my data?", spam="person", route=SUPPORT_HANDLES, confidence=0.95)
    model.say("A question about something", spam="person", route="unsure")
    model.say("Lunch on Friday?", spam="spam", route="unsure")   # a known sender is never asked person-or-spam
    graph.put(ONE, "inbox", *emails)
    record("what the rig put in qa-one's Inbox (sender | subject); qa-one's Sent folder holds one email to "
           "friend@known.example", "\n".join(f"  {m['from']['emailAddress']['address']} | {m['subject']}" for m in emails))
    t = now()
    run = wait_run_after(t, "the first read of both mailboxes", outcome="screened")
    cli(["trigger", "runs", STATE["trigger"]])
    show_decisions("qa-one's decisions", ONE)
    got = {d["subject"]: d for d in decisions(ONE)}
    expect = {"Invoice 1042 for October": ("rule", "route"), "Build 311 passed": ("allow", "archive"),
              "The October letter": ("allow", "keep-for-owner"), "Cheap watches": ("block", "junk"),
              "Grow your traffic 300 percent": ("sender-screen", "junk"),
              "How do I export my data?": ("route-model", "route"),
              "A question about something": ("route-model", "mail-desk-agent"),
              "Lunch on Friday?": ("route-model", "mail-desk-agent")}
    for subject, (layer, decided) in expect.items():
        d = got.get(subject)
        check(d is not None and d["layer"] == layer and d["decision"] == decided and d["mode"] == "observe"
              and d.get("outcome") is None,
              f"'{subject}' decided {decided} by the {layer} layer in observe mode, nothing done (outcome null)")
    record("what the fake model was asked (question | subject -> answer)", model.shown_log())
    check(len(model.calls()) >= 4 and all(c["model"] == "devthrottle/wingman-fast" for c in model.calls()),
          "the Gateway's model questions reached the rig's fake model, with the rig-only key (the fake refuses any "
          "other key)")
    check(not [c for c in model.calls() if c["subject"] == "Lunch on Friday?" and c["question"] == "person-or-spam"],
          "the known sender (written to from qa-one's Sent folder) was never asked person-or-spam")
    record("every call the fake Graph received so far", graph.shown_log())
    check(not graph.calls(method="POST"), "nothing was moved: the fake Graph received no POST")
    check(all(graph.folder_of(ONE, m["id"]) == "inbox" for m in emails), "every email is still in qa-one's Inbox")
    entry = memory_of(ONE)
    record("qa-one's entry in the stored memory (the check's fields and the Gateway's)",
           json.dumps({k: (v if k != "keys" else f"<{len(v)} keys>") for k, v in (entry or {}).items()}, indent=1))
    check(entry is not None and not entry.get("filingQueue"), "observe mode queued no filing (no filingQueue)")
    sessions = listed_sessions()
    check(not sessions, f"no session was started (the throwaway Gateway lists {len(sessions)} sessions)")
    record("the qa-two failure", "")
    cli(["maildesk", "channel", "list"])
    found = problems()
    record("the Open problems of the mail-desk factory (run problems --factory mail-desk --json)", json.dumps(found, indent=1))
    text = json.dumps(found)
    check(TWO in text and "is not signed in on" in text and "cc-outlook" in text,
          "qa-two's failure is an Open problem on the mail-desk factory, naming the machine and the cc-outlook fix")
    check(not decisions(TWO), "qa-two has no decisions: it was not read")
    marker_anywhere("observe mode")
    STATE["observe_emails"] = emails


def section_signin():
    section("SIGNED IN AGAIN", "qa-two is signed in in the rig's scratch sign-in folder and gets one email. Expected: "
                               "the next run reads it, and its problem is cleared by that good check.")
    plant_sign_in(TWO)
    email = mail("partner@firm.example", "Invoice 77 from the partner")
    STATE["graph"].put(TWO, "inbox", email)
    t = now()
    wait_run_after(t, "qa-two signed in", outcome="screened")
    d = decision_of("Invoice 77 from the partner", TWO)
    check(d is not None and d["decision"] == "route", "qa-two was read and its email decided")
    cli(["maildesk", "channel", "list"])
    text = json.dumps(problems())
    check(not (TWO in text and "is not signed in" in text), "qa-two's sign-in problem is cleared by the good check")


def section_graph_error():
    section("GRAPH ANSWERS AN ERROR", "The fake Graph answers HTTP 503 to every call for qa-one while one new email "
                                      "waits. Expected: qa-one's check fails on its own (qa-two is still read), the "
                                      "stored memory of qa-one is not moved on, and once Graph answers again the next "
                                      "run reads the same email, once.")
    graph = STATE["graph"]
    before = memory_of(ONE)
    graph.fail_for[ONE] = 503
    email = mail("customer@client.example", "Second question about exports")
    other = mail("partner@firm.example", "Invoice 78 from the partner")
    STATE["model"].say("Second question about exports", spam="person", route=SUPPORT_HANDLES)
    graph.put(ONE, "inbox", email)
    graph.put(TWO, "inbox", other)
    t = now()
    wait_run_after(t, "Graph failing for qa-one")
    record("the fake Graph's calls in that run", graph.shown_log(since=t.timestamp()))
    after = memory_of(ONE)
    check(after.get("cursor") == before.get("cursor") and after.get("keys") == before.get("keys"),
          "qa-one's cursor and keys in the stored memory are exactly as before the failing run")
    check(decision_of("Second question about exports", ONE) is None, "the email waiting in qa-one was not decided")
    check(decision_of("Invoice 78 from the partner", TWO) is not None, "qa-two was still read and decided")
    text = json.dumps(problems())
    check(ONE in text and "503" in text, "qa-one's failure is an Open problem naming the HTTP status")
    graph.fail_for.pop(ONE)
    t = now()
    wait_run_after(t, "Graph answering again", outcome="screened")
    found = [d for d in decisions(ONE) if d["subject"] == "Second question about exports"]
    check(len(found) == 1, "the same email was read the next run and decided once")
    cli(["maildesk", "decisions", "--channel", ONE, "-n", "3"])


def section_model():
    section("THE MODEL FAILING OR ANSWERING OFF THE LABELS", "Two new emails in qa-one from unknown senders: for one the "
                                                             "fake model answers HTTP 500, for the other an answer "
                                                             "that is not a letter. Expected: the first is `failed` "
                                                             "and read again next run, the second goes to the Mail "
                                                             "Desk agent with the reason; never a guess.")
    graph, model = STATE["graph"], STATE["model"]
    down = mail("visitor@unknown.example", "Model down: please classify")
    off = mail("visitor2@unknown.example", "Off the labels please")
    model.say("Model down: please classify", spam="fail")
    model.say("Off the labels please", spam="off")
    graph.put(ONE, "inbox", down, off)
    t = now()
    wait_run_after(t, "the model failing", outcome="screened")
    record("what the fake model was asked in that run", model.shown_log(since=t.timestamp()))
    d = decision_of("Model down: please classify", ONE)
    check(d is not None and d["decision"] == "failed", "the email whose question failed is decided `failed`")
    o = decision_of("Off the labels please", ONE)
    check(o is not None and o["decision"] == "mail-desk-agent" and "outside the labels" in (o.get("reason") or ""),
          "the answer off the labels goes to the Mail Desk agent, saying so")
    cli(["maildesk", "decisions", "--channel", ONE, "-n", "3"])
    text = json.dumps(problems())
    check("could not be asked" in text, "qa-one's model failure is a mailbox problem, read again next run")
    model.say("Model down: please classify", spam="person", route="unsure")
    t = now()
    wait_run_after(t, "the model answering again", outcome="screened")
    calls = model.calls(since=t.timestamp())
    record("what the fake model was asked in the next run", model.shown_log(since=t.timestamp()))
    check({c["subject"] for c in calls} == {"Model down: please classify"},
          "the next run asked about the failed email only - nothing already decided was asked or charged again")
    d = decision_of("Model down: please classify", ONE)
    check(d["decision"] == "mail-desk-agent" and d.get("attemptCount", 2) >= 1,
          "the failed email is decided on the reread (its row updated in place)")
    rows("the two emails' decision rows", "SELECT Subject, Layer, Decision, Reason, AttemptCount FROM "
         "mail_desk_decisions WHERE Subject IN ('Model down: please classify', 'Off the labels please')")


def section_batches():
    section("MORE MAIL THAN ONE BATCH", "23 emails from an allow-listed sender reach qa-two at once. A run reports at "
                                        "most 10 messages. Expected: several runs, every email decided exactly once, "
                                        "nothing skipped.")
    plant_sign_in(TWO)   # signed in by the signin section; again here so the section also runs on its own
    emails = [mail("alerts@notices.example.com", f"Nightly build {n:02d} passed") for n in range(1, 24)]
    STATE["graph"].put(TWO, "inbox", *emails)
    t = now()
    first = len(runs())
    wait_for("the 23 emails were not all decided",
             lambda: len([d for d in decisions(TWO, 500) if d["subject"].startswith("Nightly build")]) >= 23,
             seconds=900, every=5)
    time.sleep(70)   # one more run, to show it adds nothing
    made = runs()[first:]
    record("the runs while the batch was read", "\n".join(f"  {r}" for r in made))
    got = [d for d in decisions(TWO, 500) if d["subject"].startswith("Nightly build")]
    keys = [d["messageKey"] for d in got]
    check(len(got) == 23 and len(set(keys)) == 23, "23 emails, 23 decisions, 23 different message keys: none twice")
    check(sorted(d["subject"] for d in got) == sorted(m["subject"] for m in emails), "none skipped")
    check(len([r for r in made if (r[4] or 0) > 0]) >= 3, "it took at least three runs (10 per run at most)")
    rows("how many runs reported how many messages", "SELECT CheckedUtc, Outcome, Count FROM trigger_runs WHERE "
         "TriggerId = ? COLLATE NOCASE AND RecordedUtc >= ? ORDER BY RecordedUtc",
         (STATE["trigger"], t.strftime("%Y-%m-%d %H:%M:%S")))


def section_switch_off():
    section("THE SWITCH OFF", "Mail Desk is switched off for the account through the administrator route while an email "
                              "waits. Expected: every Mail Desk route answers 404 with no body, the command says the "
                              "switch is off, the Director's report is refused (409 mail_desk_off) and nothing is "
                              "recorded. Then it is switched on again and the email is read.")
    email = mail("customer@client.example", "A question while the switch is off")
    STATE["graph"].put(ONE, "inbox", email)
    before = (len(runs()), len(decisions()))
    admin_switch(False, "the Mail Desk rig: the switch-off case")
    t = now()
    for path in ("/gateway/mail-desk/status", "/gateway/mail-desk/channels", "/gateway/mail-desk/decisions"):
        status, answer = raw("GET", path, None, 404)
        check(answer == "", f"GET {path} answers 404 with no body")
    raw("POST", "/gateway/mail-desk/channels/mode", {"address": ONE, "mode": "live", "why": "x"}, 404)
    cli(["maildesk", "status"], 1)
    time.sleep(150)
    check(len(runs()) == before[0], "no run was recorded while the switch was off")
    director_log("the Director's reports while off", ("mail_desk_off", "409"), since=t.strftime("%Y-%m-%d %H:%M:%S"))
    admin_switch(True, "the Mail Desk rig: switched on again")
    check(len(decisions()) == before[1], "no decision was recorded while the switch was off")
    t = now()
    wait_run_after(t, "the switch on again", outcome="screened")
    check(decision_of("A question while the switch is off", ONE) is not None, "the waiting email is read once it is on")


REFUSED_PROMPT = (f"QA-MD-REFUSED. Run exactly this one command with Bash, quote what it printed, then stop: "
                  f"cc-devthrottle maildesk mode {ONE} live --why \"a factory session tries to switch a channel to live\"")


def section_factory_refused():
    section("A FACTORY SESSION REFUSED LIVE MODE", "A real agent session born into the qa-books factory (the "
                                                   "once-a-year timed start of its prober seat, run now) is told to switch qa-one to "
                                                   "live. Expected: 403, outcome FactorySessionRefused; nothing "
                                                   "recorded; the channel stays in observe.")
    job = next(j["id"] for j in get("/cron/jobs")["jobs"] if j.get("factory") == BOOKS and j.get("seat") == "prober")
    since = time.time()
    cli(["schedule", "run", job])
    path = wait_answer("QA-MD-REFUSED", since)
    said = summarize("the qa-books session told to switch qa-one to live", path)
    check("factory_session_refused" in said and "HTTP 403" in said,
          "the session's command was refused 403 factory_session_refused, because it runs in a factory")
    channel_row = next(c for c in get("/gateway/mail-desk/channels")["channels"] if c["address"] == ONE)
    check(channel_row["mode"] == "observe", "qa-one is still in observe mode")
    found = rows("mode changes recorded for qa-one", "SELECT AtUtc, Kind, Action, Subject, WhoKind FROM "
                 "mail_desk_list_changes WHERE Kind = 'mode'")
    check(not found, "no mode change was recorded")
    run = wait_for("the schedule run recorded no session",
                   lambda: next((r for r in cli_json(["schedule", "runs", job]) if r.get("sessionId")), None), 60)
    rows("the session's history row (born into qa-books)", "SELECT SessionId, Factory, FactorySeat FROM session_history "
         "WHERE SessionId = ? COLLATE NOCASE", (run["sessionId"],))
    stop_session(run["sessionId"])


def section_backup():
    section("BACKUP MACHINE HANDOVER AND HAND-BACK", "The Mail Desk start is republished with backupMachines: "
            f"[{BACKUP_MACHINE}], and both mailboxes name both machines. The rig stands in as a second Director on "
            f"{BACKUP_MACHINE} (see StandInDirector: devlinux has one host name). Expected: while the slot {SLOT} Director "
            "holds the claim the stand-in is handed nothing; once the slot Director is stopped and its claim has lapsed "
            "(two intervals and a minute), the stand-in is handed the trigger and the memory, its report is recorded "
            "and the cursor continues; when the slot Director starts again it takes the claim back, and the stand-in's "
            "next report is refused held_by_another_director, recording nothing.")
    save_and_publish("mail-desk", mail_desk_definition(backups=(BACKUP_MACHINE,)), "the rig's backup machine")
    cli(["trigger", "show", STATE["trigger"]])
    stand_in = STATE["stand_in"]
    cli(["director", "list"], None)
    wait_for("the slot Director did not hold the Mail Desk start's claim", lambda: (query(
        "SELECT ClaimedByDirectorId FROM triggers WHERE Id = ? COLLATE NOCASE", (STATE["trigger"],))[0][0] or "").lower()
        == STATE["director_id"].lower(), seconds=240, every=3)
    rows("the trigger's claim before the handover", "SELECT ClaimedByDirectorId, ClaimedByMachine, BackupMachinesJson "
         "FROM triggers WHERE Id = ? COLLATE NOCASE", (STATE["trigger"],))
    first = stand_in.assignments()
    record("the stand-in's assignments while the slot Director holds the claim", json.dumps(first, indent=1))
    check(not first, "the stand-in is handed nothing while the slot Director's claim is live")
    stopped_at = now()
    stop_director(STATE["dir_proc"], STATE["director_id"])
    note(f"the slot {SLOT} Director is stopped; its claim lapses two intervals and a minute after its last claim")
    email = mail("partner@firm.example", "Invoice 90 while the first machine is away")
    STATE["graph"].put(ONE, "inbox", email)
    cursor_before = memory_of(ONE).get("cursor")
    got = wait_for("the stand-in was never handed the Mail Desk start", stand_in.assignments, seconds=420, every=10)
    record(f"the stand-in's assignments {int((now() - stopped_at).total_seconds())} seconds after the slot Director "
           "stopped", json.dumps([{k: v for k, v in a.items() if k != "state"} for a in got], indent=1))
    handed = json.loads(got[0]["state"])
    check(any(c.get("address") == ONE and c.get("cursor") == cursor_before for c in handed.get("channels", [])),
          "the stand-in was handed the memory the slot Director left (qa-one's cursor as it was)")
    status, _ = stand_in.report(got[0]["id"], stand_in.run_check(got[0]), f"the stand-in Director on {BACKUP_MACHINE}")
    check(status in (200, 202), "the stand-in's report was accepted")
    wait_for("the stand-in's email was not decided",
             lambda: decision_of("Invoice 90 while the first machine is away", ONE), seconds=120, every=3)
    theirs = wait_for("no run was recorded for the stand-in Director", lambda: [
        r for r in runs() if (r[7] or "").lower() == stand_in.director_id.lower()], seconds=120, every=3)
    record("the stand-in's run (Id, CheckedUtc, RecordedUtc, Outcome, Count, SessionId, Reason, DirectorId)",
           "\n".join(f"  {r}" for r in theirs))
    check(len(theirs) == 1 and theirs[0][3] == "screened", "the run is recorded, screened, for the stand-in Director")
    rows("the trigger's claim", "SELECT ClaimedByDirectorId, ClaimedByMachine, BackupMachinesJson FROM triggers WHERE "
         "Id = ? COLLATE NOCASE", (STATE["trigger"],))
    check(len([d for d in decisions(ONE, 500) if d["subject"] == "Invoice 1042 for October"]) <= 1,
          "nothing already decided was decided again: the cursor continued")
    note(f"the slot {SLOT} Director starts again on the same root")
    director, reg = start_director()
    record("the slot Director again", f"  pid {director.pid}, id {reg['DirectorId']}")
    wait_for("the slot Director did not take the claim back", lambda: query(
        "SELECT ClaimedByDirectorId FROM triggers WHERE Id = ? COLLATE NOCASE",
        (STATE["trigger"],))[0][0].lower() == reg["DirectorId"].lower(), seconds=240, every=3)
    rows("the trigger's claim after the hand-back", "SELECT ClaimedByDirectorId, ClaimedByMachine FROM triggers WHERE "
         "Id = ? COLLATE NOCASE", (STATE["trigger"],))
    mine = lambda: [r for r in runs() if (r[7] or "").lower() == stand_in.director_id.lower()]
    count_before = len(mine())
    status, answer = stand_in.report(got[0]["id"], stand_in.run_check(got[0]),
                                     f"the stand-in Director on {BACKUP_MACHINE}, after the hand-back")
    check(status == 409 and "held_by_another_director" in json.dumps(answer), "the stand-in's report is refused "
          "held_by_another_director")
    check(len(mine()) == count_before, "the refused report recorded nothing (no new run of the stand-in)")
    check(not stand_in.assignments(), "the stand-in's next poll hands it nothing")
    stand_in.close()


def wait_decided(subject, channel, seconds=300):
    return wait_for(f"'{subject}' was not decided", lambda: decision_of(subject, channel), seconds=seconds, every=3)


def section_live_filing():
    section("LIVE MODE: FILING", "qa-one is switched to live. Five new emails: two for Archive (one of them deleted from "
                                 "the Inbox by the rig after it is queued), one for Junk Email and one kept for the "
                                 "owner. Expected: the archive and junk decisions are queued in the "
                                 "memory, the next run moves exactly those - the fake Graph's only POSTs are moves to "
                                 "archive and junkemail - and writes filed / already-gone on their decision rows; the "
                                 "email kept for the owner stays in the Inbox; nothing decided in observe mode is "
                                 "filed.")
    graph = STATE["graph"]
    cli(["maildesk", "mode", ONE, "live", "--why", "the rig's live filing case"])
    keep = mail("news@example.org", "The November letter")
    archive = mail("alerts@notices.example.com", "Build 412 passed")
    gone = mail("alerts@notices.example.com", "Build 413 passed")
    junk = mail("deals@spam.example.net", "Cheaper watches")
    t = now()
    graph.put(ONE, "inbox", keep, archive, gone, junk)
    for m in (keep, archive, gone, junk):
        wait_decided(m["subject"], ONE)
    queue = wait_for("the archive and junk decisions were not queued",
                     lambda: (lambda q: q if len(q) >= 3 else None)((memory_of(ONE) or {}).get("filingQueue") or []),
                     seconds=120, every=2)
    with graph.lock:
        graph.box(ONE)["inbox"].remove(gone)        # deleted by hand before the move
    record("qa-one's filing queue in the stored memory after the deciding run (the rig then deletes 'Build 413 passed' "
           "from the Inbox)", json.dumps(queue, indent=1))
    by_key = {d["messageKey"]: d for d in decisions(ONE, 500)}
    check([by_key[q["messageKey"]]["subject"] for q in queue] == ["Build 412 passed", "Build 413 passed",
                                                                  "Cheaper watches"]
          and [q["decision"] for q in queue] == ["archive", "archive", "junk"],
          "exactly the live archive and junk decisions are queued, oldest first")
    wait_for("the queued moves were not carried out", lambda: all(
        (decision_of(x, ONE) or {}).get("outcome") for x in ("Build 412 passed", "Build 413 passed", "Cheaper watches")),
        seconds=300, every=3)
    cli(["maildesk", "decisions", "--channel", ONE, "-n", "6"])
    posts = graph.calls(since=t.timestamp(), method="POST")
    record("the fake Graph's POSTs since qa-one went live", "\n".join(
        f"  {c['utc']} {c['mailbox']} {c['method']} {c['path']} destination={c.get('destination')} -> {c.get('answer')}"
        for c in posts) or "  (none)")
    check(sorted(c.get("destination") for c in posts) == ["archive", "archive", "junkemail"]
          and all(c["path"] == "/v1.0/me/messages/{id}/move" for c in posts),
          "the only POSTs are the three moves, to archive and junkemail")
    a, g, j = (decision_of(x, ONE) for x in ("Build 412 passed", "Build 413 passed", "Cheaper watches"))
    check(a["outcome"] == "filed" and a["outcomeDetail"] == "moved to Archive", "the archive email: filed, moved to Archive")
    check(j["outcome"] == "filed" and j["outcomeDetail"] == "moved to Junk Email",
          "the junk email: filed, moved to Junk Email")
    check(g["outcome"] == "already-gone", "the email deleted before the move: already-gone, never filed")
    check(graph.folder_of(ONE, archive["id"]) == "archive" and graph.folder_of(ONE, junk["id"]) == "junkemail",
          "the fake Graph holds them in Archive and Junk Email")
    check(graph.folder_of(ONE, keep["id"]) == "inbox", "the email kept for the owner is still in the Inbox")
    check(not (memory_of(ONE) or {}).get("filingQueue"), "the queue is empty once carried out")
    for subject in ("Build 311 passed", "Cheap watches"):
        old = decision_of(subject, ONE)
        if old is not None:
            check(old["mode"] == "observe" and old.get("outcome") is None,
                  f"'{subject}', decided in observe mode before, is not filed")
    STATE["live"] = True


def section_observe_queue():
    section("OBSERVE MODE WITH A FILING QUEUE PRESENT", "With qa-one live, a junk email is decided and queued; the rig "
                                                        "switches qa-one back to observe before the next run. Expected: "
                                                        "that run moves nothing (no POST), the queue is left as it was, "
                                                        "and the decision's outcome stays empty.")
    graph = STATE["graph"]
    if not STATE.get("live"):
        cli(["maildesk", "mode", ONE, "live", "--why", "the rig's observe-with-a-queue case"])
    junk = mail("deals@spam.example.net", "Cheapest watches")
    graph.put(ONE, "inbox", junk)
    wait_decided("Cheapest watches", ONE)
    queue = wait_for("the junk decision was not queued",
                     lambda: (memory_of(ONE) or {}).get("filingQueue"), seconds=120, every=2)
    cli(["maildesk", "mode", ONE, "observe", "--why", "the rig: back to observe with a queue present"])
    t = now()
    record("qa-one's filing queue when it was switched back to observe", json.dumps(queue, indent=1))
    wait_run_after(t, "observe mode with a queue present", outcome="screened")
    wait_run_after(now(), "a second run in observe mode", outcome="screened")
    check(not graph.calls(since=t.timestamp(), method="POST"), "no POST since qa-one went back to observe")
    check(graph.folder_of(ONE, junk["id"]) == "inbox", "the queued email is still in the Inbox")
    check((memory_of(ONE) or {}).get("filingQueue") == queue, "the queue is left exactly as it was")
    check(decision_of("Cheapest watches", ONE).get("outcome") is None, "the decision's outcome stays empty")
    STATE["live"] = False


def routed_rows(title=None):
    """The account's routed rows, from the Gateway's database (task C adds the route that serves them)."""
    sql = ("SELECT r.Id, d.Subject, r.Factory, r.Seat, r.SessionId, r.State, r.RerouteCount, r.StartFailures, "
           "r.OwnerReason, r.StartName FROM mail_desk_routed r JOIN mail_desk_decisions d ON d.Id = r.DecisionId "
           "ORDER BY d.ReceivedUtc")
    return rows(title, sql) if title else query(sql)


def routed_of(subject):
    return [r for r in routed_rows() if r[1] == subject]


def seat_sessions(factory, seat):
    return [x for x in listed_sessions() if x.get("factory") == factory and x.get("factorySeat") == seat]


def kit_check(sid):
    found = sorted((DIR_INSTANCE / "factory-run").rglob("check.json")) if (DIR_INSTANCE / "factory-run").exists() else []
    mine = [f for f in found if sid in str(f)]
    return json.loads(mine[0].read_text(encoding="utf-8-sig")) if mine else None


def session_agent_pids(sid):
    """The processes working in this session's own folder under the rig (its agent, and anything the agent runs) -
    processes the rig's own Director started for it."""
    folder = session_folder(sid)
    found = []
    for d in Path("/proc").iterdir():
        if not d.name.isdigit():
            continue
        try:
            if os.readlink(d / "cwd").startswith(folder):
                argv0 = (d / "cmdline").read_bytes().split(b"\0")[0].decode("utf-8", "replace")
                found.append((int(d.name), "claude" if "claude" in argv0 else Path(argv0).name))
        except OSError:
            continue
    return found


def end_session(sid, why):
    """Ends a seat's session so the Gateway SEES it ended (contract 3.2 step 3: a session counts as ended only on
    evidence). A clean exit is no evidence: the Director reaps a session whose agent exits cleanly, its row goes, and
    an unseen session counts as alive for 24 hours. So the rig ends the agent abnormally - a kill signal to the agent
    process working in this session's folder under the rig, which the rig's own Director started - and the Director
    keeps the row, crashed, as it does for any agent that dies. It waits for the session's turn to finish first."""
    ids = routed_ids_of(sid)
    summarize(f"session {sid} before the rig ends it", wait_session_turn(sid, ids[0] if ids else None))
    agents = [(pid, name) for pid, name in session_agent_pids(sid) if name == "claude"]
    if len(agents) != 1:
        raise SystemExit(f"expected one agent process in session {sid}'s folder, found {session_agent_pids(sid)}")
    os.kill(agents[0][0], signal.SIGKILL)
    record(f"the rig ended session {sid}'s agent with a kill signal", f"  agent process {agents[0][0]} "
           f"(working in {session_folder(sid)}, the session's own checkout under the rig)\n  why: {why}")
    return wait_for(f"session {sid} was not seen ended", lambda: next(
        (x for x in listed_sessions(include_exited=True) if (x.get("sessionId") or x.get("id")) == sid
         and (x.get("crashed") or str(x.get("activityState", "")).lower() == "exited")), None), seconds=180, every=3)


SESSION_FIELDS = ("sessionId", "name", "factory", "factorySeat", "directorId", "machineName", "activityState", "origin",
                  "originSurface")


def take_over_director_tools():
    """A session's PATH starts with the Director's own tools folder (<rig>/director-root/bin), which the Director fills
    from its own install a little after it starts. Once it has, the rig replaces two of its links, in the rig's own
    scratch root: cc-devthrottle becomes this tree's command (the seats run maildesk done and return) and cc-outlook
    the refusing stub (on the rig there is no mailbox, and a seat must never reach a real one)."""
    tools = DIR_ROOT / "bin"
    wait_for("the Director did not install its tools", lambda: (tools / "cc-outlook").exists()
             and (tools / "cc-devthrottle").exists(), seconds=600, every=5)
    for name in ("cc-devthrottle", "cc-outlook"):
        target = tools / name
        target.unlink()
        shutil.copyfile(RIG_BIN / name, target)
        target.chmod(0o755)
    check_director_tools("the rig replaced two of the Director's tool links")


def check_director_tools(title):
    tools = DIR_ROOT / "bin"
    lines = [f"{p.name} -> {os.readlink(p)}" if p.is_symlink() else f"{p.name} (the rig's file)"
             for p in sorted(tools.iterdir())] if tools.exists() else []
    record(f"{title}: <rig>/director-root/bin", "\n".join(f"  {x}" for x in lines) or "  (empty)")
    for name in ("cc-devthrottle", "cc-outlook"):
        check(not (tools / name).is_symlink() and (tools / name).read_bytes() == (RIG_BIN / name).read_bytes(),
              f"the Director's {name} is the rig's")


def section_live_routing():
    section("LIVE MODE: ROUTING TO SEATS", "qa-two is switched to live. One run brings two invoices (a rule routes them "
            f"to {BOOKS}/{CLERK}), a support question (the route model routes it to {SUPPORT}/{DESK}) and a letter kept "
            "for the owner. Expected: TWO sessions start, one per seat, on this machine, naming the slot Director - the "
            "books seat with both invoices in one session - each with its emails in its prompt and in check.json and "
            "nothing of a preview anywhere; the decisions say seat-started; the letter kept for the owner stays in the "
            "Inbox and starts nothing.")
    graph, model = STATE["graph"], STATE["model"]
    take_over_director_tools()
    plant_sign_in(TWO)
    cli(["maildesk", "mode", TWO, "live", "--why", "the rig's live routing case"])
    model.say("Export question 3 [rig: close it with maildesk done]", spam="person", route=SUPPORT_HANDLES, confidence=0.96)
    first = [mail("billing@vendor.example", "Invoice 3001 [rig: run nothing for this email]"), mail("billing@vendor.example", "Invoice 3002 [rig: run nothing for this email]"),
             mail("customer@client.example", "Export question 3 [rig: close it with maildesk done]"), mail("news@example.org", "The December letter")]
    graph.put(TWO, "inbox", *first)
    books = wait_for("no session started for the books seat", lambda: seat_sessions(BOOKS, CLERK), seconds=420, every=3)
    support = wait_for("no session started for the support seat", lambda: seat_sessions(SUPPORT, DESK), seconds=180, every=3)
    for title, found in (("the books seat's session", books), ("the support seat's session", support)):
        record(f"{title} - GET /sessions, the fields that say where, by whom and what",
               json.dumps([{k: x.get(k) for k in SESSION_FIELDS} for x in found], indent=1))
    check(len(books) == 1 and len(support) == 1, "two routes to two seats started two sessions, one per seat")
    b, sp = books[0], support[0]
    check(b.get("machineName") == MACHINE and (b.get("directorId") or "").lower() == STATE["director_id"].lower(),
          "the books session runs on this machine, on the Director that ran the check")
    check(b.get("name", "").startswith(f"Mail Desk - {BOOKS}/{CLERK} - 2 emails - "),
          "the books session is named 'Mail Desk - qa-books/clerk - 2 emails - <time>'")
    check(sp.get("name", "").startswith(f"Mail Desk - {SUPPORT}/{DESK} - 1 email - "),
          "the support session is named 'Mail Desk - qa-support/desk - 1 email - <time>'")
    routed_rows("the routed rows after the first live run")
    for subject in ("Invoice 3001 [rig: run nothing for this email]", "Invoice 3002 [rig: run nothing for this email]"):
        r = routed_of(subject)
        check(len(r) == 1 and (r[0][4] or "").lower() == b["sessionId"].lower() and r[0][5] == "open",
              f"'{subject}' is open with the books session")
    path = wait_session_turn(b["sessionId"], routed_ids_of(b["sessionId"])[0])
    said = summarize("the books seat's session", path)
    ids = {r[1]: r[0] for r in routed_rows()}
    check(all(ids[x].lower() in said.lower() for x in ("Invoice 3001 [rig: run nothing for this email]", "Invoice 3002 [rig: run nothing for this email]")),
          "the books session's prompt names both routed ids")
    context = kit_check(b["sessionId"])
    record("the books session's check.json, from its kit", json.dumps(context, indent=1))
    inner = json.loads(context["Context"] if "Context" in context else context["context"]) if context else None
    check(inner is not None and inner.get("kind") == "mail-desk-routed"
          and sorted(e["subject"] for e in inner["emails"]) == ["Invoice 3001 [rig: run nothing for this email]", "Invoice 3002 [rig: run nothing for this email]"],
          "check.json carries the two emails, kind mail-desk-routed")
    check(PREVIEW_MARKER not in said and PREVIEW_MARKER not in json.dumps(context),
          "nothing of the preview is in the prompt or check.json")
    for subject in ("Invoice 3001 [rig: run nothing for this email]", "Export question 3 [rig: close it with maildesk done]"):
        d = decision_of(subject, TWO)
        check(d.get("outcome") == "seat-started" and "session" in (d.get("outcomeDetail") or ""),
              f"'{subject}': outcome seat-started, naming the session")
    letter = decision_of("The December letter", TWO)
    check(letter["decision"] == "keep-for-owner" and letter.get("outcome") is None
          and graph.folder_of(TWO, first[3]["id"]) == "inbox" and not routed_of("The December letter"),
          "the letter kept for the owner stays in the Inbox, with no routed row and nothing started")
    marker_anywhere("live routing")
    STATE["books_session"] = b["sessionId"]
    STATE["support_session"] = sp["sessionId"]

    note("DONE - the support seat was told by its brief to close 'Export question 3' with maildesk done")
    summarize("the support seat's session", wait_session_turn(sp["sessionId"], routed_ids_of(sp["sessionId"])[0]))
    done = wait_for("the support email was not done", lambda: next(
        (r for r in get("/gateway/mail-desk/routed?state=done")["routed"] if r["subject"] == "Export question 3 [rig: close it with maildesk done]"), None),
        seconds=180, every=3)
    cli(["maildesk", "routed", "--state", "done"])
    check(done["state"] == "done" and (done.get("sessionId") or "").lower() == sp["sessionId"].lower()
          and done.get("doneNote"), "the support email is done by its own session, with the seat's note")
    status, answer = raw("POST", f"/gateway/mail-desk/routed/{ids['Invoice 3001 [rig: run nothing for this email]']}/done", {"note": "a person"},
                         403, label="the rig's person, not the session the email is with")
    check(answer.get("error") == "not_your_email", "a person's done is refused 403 not_your_email")


def section_busy_and_orphan():
    section("A BUSY SEAT, THEN AN ORPHAN", "While the books session is alive a third invoice arrives: it waits, queued, "
            "and no second session starts. The rig then ends the books session (it never said done): its two emails "
            "are re-routed once and, with the queued one, given to ONE new books session. The rig ends that one too: "
            "the two emails ended twice go to the owner, the third is re-routed once.")
    graph = STATE["graph"]
    third = mail("billing@vendor.example", "Invoice 3003 [rig: run nothing for this email]")
    graph.put(TWO, "inbox", third)
    wait_for("the third invoice got no routed row", lambda: routed_of("Invoice 3003 [rig: run nothing for this email]"), seconds=300, every=3)
    wait_run_after(now(), "one more run while the books seat is busy", outcome="screened")
    r = routed_of("Invoice 3003 [rig: run nothing for this email]")[0]
    check(r[4] is None and r[5] == "open", "the third invoice is queued: open, with no session")
    check(len(seat_sessions(BOOKS, CLERK)) == 1, "no second books session started while the first is alive")
    first_session = STATE["books_session"]
    end_session(first_session, "the orphan case: the seat ended without done or return")
    second = wait_for("no new books session after the orphan", lambda: [
        x for x in seat_sessions(BOOKS, CLERK) if x["sessionId"].lower() != first_session.lower()
        and str(x.get("activityState", "")).lower() != "exited"], seconds=420, every=3)
    record("the new books session", json.dumps([{k: x.get(k) for k in SESSION_FIELDS} for x in second], indent=1))
    check(len(second) == 1 and second[0].get("name", "").startswith(f"Mail Desk - {BOOKS}/{CLERK} - 3 emails - "),
          "one new books session takes all three emails (the two re-routed and the queued one)")
    stop_session(first_session)   # the router has acted on it; stopping it frees its pooled checkout
    routed_rows("the routed rows after the first orphan")
    for subject in ("Invoice 3001 [rig: run nothing for this email]", "Invoice 3002 [rig: run nothing for this email]"):
        row = routed_of(subject)[0]
        check(row[6] == 1 and (row[4] or "").lower() == second[0]["sessionId"].lower(),
              f"'{subject}' was re-routed once (RerouteCount 1) to the new session")
    end_session(second[0]["sessionId"], "the orphan case: the second session also ended without done or return")
    wait_for("the twice-ended emails were not handed to the owner", lambda: all(
        routed_of(x)[0][5] == "returned" for x in ("Invoice 3001 [rig: run nothing for this email]", "Invoice 3002 [rig: run nothing for this email]")), seconds=300, every=3)
    routed_rows("the routed rows after the second orphan")
    for subject in ("Invoice 3001 [rig: run nothing for this email]", "Invoice 3002 [rig: run nothing for this email]"):
        row = routed_of(subject)[0]
        check(row[5] == "returned" and "ended twice" in (row[8] or ""),
              f"'{subject}' is with the owner: {row[8]}")
    third_row = routed_of("Invoice 3003 [rig: run nothing for this email]")[0]
    check(third_row[5] == "open" and third_row[6] == 1, "the third invoice, ended once, was re-routed once")
    stop_session(second[0]["sessionId"])
    for x in seat_sessions(BOOKS, CLERK):   # the third invoice's new session is not needed by any later case
        stop_session(x["sessionId"])


ZONE_CANDIDATES = ["Asia/Kolkata", "Asia/Kathmandu", "Asia/Yangon", "Asia/Tehran", "Asia/Kabul", "Australia/Darwin",
                   "Australia/Adelaide", "Australia/Eucla", "Pacific/Chatham", "America/St_Johns", "Pacific/Marquesas",
                   "Australia/Lord_Howe"] + [f"Etc/GMT{'+' if n > 0 else '-'}{abs(n)}" if n else "Etc/GMT"
                                             for n in range(-14, 13)]


def zone_with_morning_ahead(low=20, high=50):
    """A real time zone in which the next 08:00 is between `low` and `high` minutes from now, so the account's 08:00
    email falls due after the owner case has run. Whole-hour zones plus the half- and quarter-hour ones always leave
    one in any half hour."""
    import zoneinfo
    best = None
    for name in ZONE_CANDIDATES:
        local = datetime.now(zoneinfo.ZoneInfo(name))
        eight = local.replace(hour=8, minute=0, second=0, microsecond=0)
        if eight <= local:
            eight += timedelta(days=1)
        ahead = (eight - local).total_seconds() / 60
        if low <= ahead <= high and (best is None or ahead < best[1]):
            best = (name, ahead)
    if best is None:
        raise SystemExit(f"no candidate time zone has its next 08:00 between {low} and {high} minutes from now")
    return best


def section_return_owner():
    zone, ahead = zone_with_morning_ahead()
    section("RETURN, THE MAIL DESK AGENT, AND THE OWNER", "A support question the support seat is told to return. "
            "Expected: its return re-queues it to the Mail Desk agent (mail-desk/mail-desk) with the note; the next run "
            "starts the Mail Desk agent with the note in its prompt and check.json; the agent returns it too, so it is "
            "handed to the owner: an Open problem on the mail-desk factory, and a line in the 08:00 email's Returned and "
            f"routed part. For the 08:00 email the account's time zone is set to {zone}, where the next 08:00 is "
            f"{ahead:.0f} minutes away, and the factory email is switched on (the throwaway Gateway holds no account "
            "sign-in, so nothing can be sent; the composed email is read back from the Gateway).")
    graph, model = STATE["graph"], STATE["model"]
    raw("PUT", "/gateway/time-zone", {"timeZone": zone}, 200)
    cli(["factory", "email", "on"], None)
    if STATE.get("support_session"):
        end_session(STATE["support_session"], "its email is done; the next support email needs the seat free")
        stop_session(STATE["support_session"])
    model.say("Refund question [rig: return it with maildesk return]", spam="person", route=SUPPORT_HANDLES, confidence=0.95)
    graph.put(TWO, "inbox", mail("customer@client.example", "Refund question [rig: return it with maildesk return]"))
    returned = wait_for("the support seat did not return the email", lambda: next(
        (r for r in get("/gateway/mail-desk/routed?n=200")["routed"] if r["subject"] == "Refund question [rig: return it with maildesk return]"
         and r.get("returnedCount", 0) >= 1), None), seconds=600, every=5)
    record("the routed row after the support seat's return", json.dumps(returned, indent=1))
    check(returned["factory"] == "mail-desk" and returned["seat"] == "mail-desk" and returned.get("returnNote"),
          "the return re-queued it to the Mail Desk agent, with the seat's note")
    agent = wait_for("the Mail Desk agent was not started", lambda: [
        x for x in seat_sessions("mail-desk", "mail-desk") if str(x.get("activityState", "")).lower() != "exited"],
        seconds=300, every=3)
    record("the Mail Desk agent's session", json.dumps([{k: x.get(k) for k in SESSION_FIELDS} for x in agent], indent=1))
    said = summarize("the Mail Desk agent's session", wait_session_turn(agent[0]["sessionId"], returned["id"]))
    check("Returned before:" in said and returned["returnNote"] in said, "its prompt carries the seat's return note")
    context = kit_check(agent[0]["sessionId"])
    inner = json.loads(context.get("context") or context.get("Context"))
    check(any(e.get("returnNote") == returned["returnNote"] for e in inner["emails"]),
          "its check.json carries the return note")
    owner = wait_for("the email was not handed to the owner", lambda: next(
        (r for r in get("/gateway/mail-desk/routed?state=returned&n=200")["routed"]
         if r["subject"] == "Refund question [rig: return it with maildesk return]"), None), seconds=300, every=3)
    cli(["maildesk", "routed", "--state", "returned"])
    check("the Mail Desk agent returned it" in (owner.get("ownerReason") or ""),
          f"it is with the owner: {owner.get('ownerReason')}")
    found = problems()
    record("the Open problems of the mail-desk factory", json.dumps(found, indent=1))
    mine = [p for p in found.get("problems", []) if p.get("kind") == "mail-handed-to-owner"
            and "Refund question [rig: return it with maildesk return]" in (p.get("sentence") or "")]
    check(len(mine) == 1, "the email handed to the owner is one Open problem, kind mail-handed-to-owner")
    note(f"waiting for the account's 08:00 ({zone}) and the factory email sweep (every 2 minutes, 5 minutes after the hour)")
    email = wait_for("no 08:00 email was composed", lambda: next(
        (e for e in get("/gateway/factory-emails?state=all&limit=20").get("emails", []) if e.get("kind") == "morning"),
        None), seconds=int(ahead * 60) + 900, every=15)
    full = get(f"/gateway/factory-emails/{email['id']}")
    text = full.get("text") or full.get("html") or ""
    start = text.find("Returned and routed")
    record("the composed 08:00 email: its state and its Mail Desk part (from 'Returned and routed')",
           f"  state: {email.get('state')}, detail: {email.get('detail')}\n"
           + "\n".join(f"  {x}" for x in text[start:start + 2500].splitlines()))
    check(start >= 0 and "Refund question [rig: return it with maildesk return]" in text[start:], "the 08:00 email's Returned and routed part lists it")
    check(email.get("state") != "sent", "nothing was sent: the throwaway Gateway has no account sign-in")


SECTIONS = {
    "observe": section_observe,
    "signin": section_signin,
    "graph-error": section_graph_error,
    "model": section_model,
    "batches": section_batches,
    "switch-off": section_switch_off,
    "factory-refused": section_factory_refused,
    "live-filing": section_live_filing,
    "observe-queue": section_observe_queue,
    "live-routing": section_live_routing,
    "busy-orphan": section_busy_and_orphan,
    "return-owner": section_return_owner,
    "backup": section_backup,
}


# ===================================================================================================== main


def stop_on_signal(signum, _frame):
    """A termination signal ends the run through the same clean stop as a failure: the finally block below asks the
    Director to stop and stops the Gateway, so nothing the rig started is left running."""
    raise SystemExit(f"the rig was asked to stop (signal {signum})")


def main():
    signal.signal(signal.SIGTERM, stop_on_signal)
    signal.signal(signal.SIGHUP, stop_on_signal)
    only = list(SECTIONS)
    if "--only" in sys.argv:
        only = sys.argv[sys.argv.index("--only") + 1].split(",")
        unknown = [s for s in only if s not in SECTIONS]
        if unknown:
            raise SystemExit(f"unknown section(s) {unknown}; the sections are {list(SECTIONS)}")
    for needed in (GATEWAY_EXE, DIRECTOR_EXE, PYENV_PYTHON, WRAPPER):
        if not needed.exists():
            raise SystemExit(f"{needed} does not exist (build the Gateway and the slot {SLOT} Director first)")
    if RIG.exists() and rig_processes():
        raise SystemExit(f"processes still run from {RIG}: {rig_processes()}; wait for them to end")
    if RIG.exists():
        shutil.rmtree(RIG)
    RIG.mkdir(parents=True)
    (RIG / MARKER).write_text("the Mail Desk rig's folder\n", encoding="utf-8")
    SIGN_IN.mkdir()

    tree = subprocess.run(["git", "-C", str(REPO), "rev-parse", "--short", "HEAD"], capture_output=True,
                          text=True).stdout.strip()
    note(f"Mail Desk rig, {utc()}, tree {tree}, machine {MACHINE}, sections: {', '.join(only)}")
    links_before = user_bin_links("~/.local/bin links BEFORE the run")
    graph, model = FakeGraph(), FakeModel()
    graph_server, model_server = serve(graph.handle), serve(model.handle)
    STATE.update(graph=graph, model=model, graph_url=f"http://127.0.0.1:{graph_server.server_port}",
                 model_url=f"http://127.0.0.1:{model_server.server_port}/v1")
    gw = director = None
    try:
        gw = start_gateway(STATE["model_url"])
        gateway_log("the Mail Desk model's address, read at start", ("MailDeskModelEndpointConfig",))
        director, reg = start_director()
        STATE["dir_proc"] = director
        note(f"Gateway {STATE['gw']} built from this tree, storage root <rig>/gateway-root (empty for this run), its "
             f"config.json naming the fake model {STATE['model_url']}. The slot {SLOT} Director (pid {director.pid}, id "
             f"{reg['DirectorId']}) started with CC_DIRECTOR_ROOT=<rig>/director-root and HOME=<rig>/home and joined "
             f"it. The fake Graph answers on {STATE['graph_url']}.")
        section_setup()
        for name in only:
            SECTIONS[name]()
        if any(name in only for name in ("live-routing", "busy-orphan", "return-owner")):
            check_director_tools("at the end of the run")
        hosted = [line for line in gateway_log("any request toward the hosted endpoint", ("devthrottle.com",))]
        check(not hosted, "the throwaway Gateway's log names no request to devthrottle.com: no model question left "
                          "this machine")
        if any(name in only for name in ("observe", "model", "graph-error", "signin")):
            check(model.calls(), "the fake model received the model questions this run's cases expect")
        record("every call the fake Graph received in the whole run", graph.shown_log())
        record("every question the fake model was asked in the whole run", model.shown_log())
    finally:
        try:
            for s in listed_sessions() if gw is not None and gw.poll() is None else []:
                stop_session(s.get("sessionId") or s.get("id"))
        except (urllib.error.URLError, OSError, SystemExit) as exc:
            print(f"could not list sessions to stop them: {exc}", flush=True)
        try:
            if STATE.get("stand_in"):
                STATE["stand_in"].close()
            director = STATE.get("dir_proc", director)
            if director is not None and director.poll() is None:
                if STATE.get("director_id"):
                    stop_director(director, STATE["director_id"])
                else:
                    record("the test Director never registered, so it cannot be asked to stop; it was NOT killed",
                           f"  pid {director.pid} - stop it by hand")
        finally:
            if gw is not None and gw.poll() is None:
                gw.send_signal(signal.SIGTERM)   # the one process this rig started on its own root
                try:
                    gw.wait(timeout=30)
                except subprocess.TimeoutExpired:
                    record("the throwaway Gateway did not stop on SIGTERM within 30 seconds", f"  pid {gw.pid}")
            graph_server.shutdown()
            model_server.shutdown()
            record("processes still running from the rig folder after the stop",
                   "\n".join(f"  {p}" for p in rig_processes()) or "  (none)")
            links_after = user_bin_links("~/.local/bin links AFTER the run")
            record("~/.local/bin unchanged by the run, and its tools still point at the owner's pyenv",
                   f"  unchanged: {links_before == links_after}\n  cc-devthrottle -> "
                   f"{os.readlink(USER_BIN / 'cc-devthrottle') if (USER_BIN / 'cc-devthrottle').is_symlink() else '(not a link)'}")
            (HERE / "transcript.txt").write_text("\n".join(TRANSCRIPT), encoding="utf-8", newline="\n")
            print(f"wrote {HERE / 'transcript.txt'}", flush=True)


if __name__ == "__main__":
    main()
