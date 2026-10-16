"""QA for Factory Definition, phase 5b: checks, on a throwaway Gateway and a REAL Director built from this tree.

Runs in the foreground, owns everything it starts, and stops it when the run ends, pass or fail:
- a Gateway (CcDirector.Gateway, Debug build of THIS tree) on port 7995, with its own storage root under %TEMP%
  (CC_DIRECTOR_ROOT), Tailscale off, authentication ON and factory agents switched on in the rig's OWN config.json, as
  phases 4 and 5. It can touch neither the owner's Gateway nor any factory. It holds no account sign-in, so the
  factory email it composes cannot be sent to anyone.
- a real Director: scripts\\local-build\\cc-director7.exe (SLOT 7 ONLY), launched ONLY through a scheduled task of its
  own, `cc-director-launch-p5b`, set up exactly as CLAUDE.md rule 0b sets up `cc-director-launch` (one action, its
  working folder, a far-future trigger, on demand only), and stopped ONLY by its named shutdown signal. The task runs a
  wrapper that sets the Director's own storage root under the rig; the rig registers it at the start and unregisters it
  at the end. The shared `cc-director-launch` task, used by phase 5's QA on slot 6, is never touched.
- a fake mailbox: the phase 0 email check's fake Graph, served by this rig on 127.0.0.1:7994. The email check runs as a
  DEFINITION check (checks/new_mail.py, files/mail-rules.json) and the rules file points it at the fake. It never
  reaches the real mailbox. The fake keeps no request body and no path that carries a value.
- cc-secrets: the starts declare entries that ALREADY exist on this machine. No value is ever read, printed or kept by
  the rig; the fake Graph ignores what it is sent. The missing entry is a name that does not exist.

Sessions are real Claude Code seats with tiny prompts. What an agent did is read from its own transcript in the user's
Claude Code projects folder, found by the marker its prompt carries.

Usage (from the repository root, after building the Gateway and the slot 7 Director):
    dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
    powershell -NoProfile -File scripts\\local-build-avalonia.ps1 -Slot 7 -OutputDir "<repo>\\scripts\\local-build"
    uv run --no-project --with "typer>=0.16.1" --with "click>=8.2.1" --with "rich>=13.0.0" --with "requests>=2.31.0" \\
        --with tzdata python docs/qa/factory-definition/phase-5b/rig/run_qa.py <out dir> [--only section,section]
Sections, in order: undefined, publish, mail, probes, failures, draft, refusals, timed, problems.
"""

import hashlib
import http.server
import json
import os
import re
import shutil
import sqlite3
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zoneinfo
from datetime import datetime, timedelta, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[5]
HERE = Path(__file__).resolve().parent
EXE = REPO / "src" / "CcDirector.Gateway" / "bin" / "Debug" / "net10.0" / "CcDirector.Gateway.exe"
DIRECTOR_EXE = REPO / "scripts" / "local-build" / "cc-director7.exe"
CLI_DIR = REPO / "tools" / "cc-devthrottle"
EMAIL_CHECK = REPO / "docs" / "qa" / "factory-definition" / "phase-0" / "inventory" / "email-check" / "check_new_mail.py"
TASK = "cc-director-launch-p5b"
PORT = 7995
GRAPH_PORT = 7994
GW = f"http://127.0.0.1:{PORT}"
FAKE = f"http://127.0.0.1:{GRAPH_PORT}"
MACHINE = os.environ["COMPUTERNAME"]
FACTORY = "qa-checks"
UNDEFINED = "qa-plain"
MODEL = "claude-sonnet-5-5"

# Entries that exist on this machine (names and kinds only; no value is ever read). The tenant and client parts are
# SETTINGS, the client secret is a read-only token of kind secret whose entry has no envName of its own, so it arrives
# in the variable the naming rule gives it.
TENANT_ENTRY = "cloudflare-account-id"            # setting, no envName -> CLOUDFLARE_ACCOUNT_ID
CLIENT_ENTRY = "posthog-project-id"               # setting, envName POSTHOG_PROJECT_ID
SECRET_ENTRY = "cloudflare-registrar-token"       # secret, no envName -> CLOUDFLARE_REGISTRAR_TOKEN
SELF_CALL_ENTRY = "posthog-project-key"           # setting; the probe asks cc-secrets for it without declaring it
MISSING_ENTRY = "qa-p5b-no-such-secret"           # does not exist on this machine

RIG = Path(os.environ["TEMP"]) / "factory-definition-p5b-qa"
GW_ROOT = RIG / "gateway-root"
DIR_ROOT = RIG / "director-root"
HOME = DIR_ROOT / "instances" / "default"
PLAIN = RIG / "repos" / "qa-plain"
USER_HOME = Path(os.environ["USERPROFILE"])
CLAUDE_PROJECTS = USER_HOME / ".claude" / "projects"
NOW = datetime.now(timezone.utc).replace(tzinfo=None)

PERSON_KEY = "qa-person-" + uuid.uuid4().hex
DIRECTOR_KEY = "qa-director-" + uuid.uuid4().hex

TRANSCRIPT = []
USED_TRANSCRIPTS = set()
STATE = {}          # what one section hands the next: the Director's id, versions, trigger and job ids, times


# ===================================================================================================== the record


def shown(text):
    """What the transcript may carry: the rig and the tree as <rig> and <repo>, and no person's name or machine."""
    text = str(text)
    for real, alias in ((RIG, "<rig>"), (REPO, "<repo>"), (USER_HOME, "<home>")):
        for form in (str(real), json.dumps(str(real))[1:-1], str(real).replace("\\", "/")):
            text = text.replace(form, alias)
            text = text.replace(form.lower(), alias)
    # Claude Code names a project folder by its path with every separator made a hyphen.
    text = re.sub(re.escape(re.sub(r"[^A-Za-z0-9]", "-", str(RIG))), "<rig>", text, flags=re.IGNORECASE)
    text = re.sub(re.escape(re.sub(r"[^A-Za-z0-9]", "-", str(USER_HOME))), "<home>", text, flags=re.IGNORECASE)
    text = text.replace(MACHINE, "<machine>").replace(MACHINE.lower(), "<machine>")
    text = re.sub(r"(?i)[a-z0-9-]+[.]tail[0-9a-f]+[.]ts[.]net", "<tailnet address>", text)
    text = re.sub(r"(?i)soren", "<user>", text)
    for fancy, plain in (("\u2018", "'"), ("\u2019", "'"), ("\u201c", '"'), ("\u201d", '"'), ("\u2014", "-"),
                         ("\u2013", "-"), ("\u2026", "..."), ("\u276f", ">"), ("\u2192", "->")):
        text = text.replace(fancy, plain)
    text = re.sub(r"[\u2500-\u257f]+", "", text)
    return text.encode("ascii", "replace").decode("ascii")


def note(text):
    TRANSCRIPT.append(f"# {shown(text)}\n")
    print(f"== {text}", flush=True)


def record(title, body):
    TRANSCRIPT.append(f"# {shown(title)}\n{shown(body).rstrip()}\n")


def utc(when=None):
    return (when or datetime.now(timezone.utc)).strftime("%Y-%m-%dT%H:%M:%SZ")


# ===================================================================================================== the fake mailbox


class FakeMailbox:
    """The phase 0 tests' fake Graph, served on a port: the sign-in answers a token, a folder read answers the messages
    received at or after the filter's time, oldest first. It keeps one line per read (folder and time asked from) and
    nothing that names a value: no request body is read for anything but its length, and the sign-in's path - which
    carries the tenant entry's value - is never kept."""

    def __init__(self):
        self.folders = {}
        self.reads = []
        self.lock = threading.Lock()

    def put(self, folder, messages):
        with self.lock:
            self.folders[folder] = list(messages)

    def answer(self, handler):
        url = urllib.parse.urlsplit(handler.path)
        if handler.command == "POST" and url.path.startswith("/sign-in/") and url.path.endswith("/oauth2/v2.0/token"):
            length = int(handler.headers.get("Content-Length") or 0)
            handler.rfile.read(length)       # read and dropped: it holds the client entries' values
            with self.lock:
                self.reads.append(f"{utc()} POST /sign-in/<tenant>/oauth2/v2.0/token")
            return 200, {"access_token": "fake-token", "token_type": "Bearer", "expires_in": 3600}
        m = re.fullmatch(r"/v1\.0/users/([^/]+)/mailFolders/([^/]+)/messages", url.path)
        if handler.command == "GET" and m:
            query = urllib.parse.parse_qs(url.query)
            since = query["$filter"][0].split(" ge ")[1]
            folder = m.group(2)
            with self.lock:
                matching = sorted((x for x in self.folders.get(folder, []) if x["receivedDateTime"] >= since),
                                  key=lambda x: x["receivedDateTime"])
                self.reads.append(f"{utc()} GET {urllib.parse.unquote(m.group(1))} folder={folder} since={since} "
                                  f"-> {len(matching)} message(s)")
            return 200, {"value": matching[:int(query.get("$top", ["50"])[0])]}
        return 404, {"error": f"the fake mailbox has no {handler.command} {url.path}"}


def serve_fake(mailbox):
    class Handler(http.server.BaseHTTPRequestHandler):
        def _go(self):
            status, body = mailbox.answer(self)
            data = json.dumps(body).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        do_GET = _go
        do_POST = _go

        def log_message(self, *args):
            pass                              # the request line of a sign-in carries a value; nothing is logged

    server = http.server.ThreadingHTTPServer(("127.0.0.1", GRAPH_PORT), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


def mail(mid, minutes_ago, sender, subject):
    when = (datetime.now(timezone.utc) - timedelta(minutes=minutes_ago)).strftime("%Y-%m-%dT%H:%M:%SZ")
    return {"id": mid, "receivedDateTime": when, "from": {"emailAddress": {"address": sender}}, "subject": subject}


# ===================================================================================================== the Gateway


def sha(key):
    return hashlib.sha256(key.encode("utf-8")).hexdigest()


def stamp(when):
    return when.strftime("%Y-%m-%d %H:%M:%S")


def wait_for(what, ready, seconds=180, every=1.0, tend=False):
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            if tend:
                tend_sessions()
            value = ready()
            if value:
                return value
        except (urllib.error.URLError, ConnectionError, TimeoutError, OSError):
            pass
        time.sleep(every)
    raise SystemExit(f"{what} within {seconds}s")


def insert(c, table, row):
    """Insert a row, filling every other NOT NULL column that has no default with a neutral value of its type."""
    cols = {r[1]: r for r in c.execute(f"PRAGMA table_info({table})")}
    missing = [k for k in row if k not in cols]
    if missing:
        raise SystemExit(f"{table} has no column(s) {missing}; its columns are {list(cols)}")
    for name, (_, _, kind, notnull, default, pk) in cols.items():
        if name in row or not notnull or default is not None:
            continue
        kind = (kind or "").upper()
        row[name] = 0 if "INT" in kind or "REAL" in kind else stamp(NOW) if "Utc" in name else ""
    c.execute(f"INSERT INTO {table} ({', '.join(row)}) VALUES ({', '.join('?' for _ in row)})", list(row.values()))


def enroll(db):
    """A person (a browser device) and the real Director (a workstation device), each with its own key."""
    c = sqlite3.connect(db)
    for device_id, key, device_type in (("qa-browser", PERSON_KEY, "browser"), ("qa-director7", DIRECTOR_KEY, "workstation")):
        insert(c, "device_credentials", {
            "DeviceId": device_id, "MachineName": MACHINE, "DeviceKeyHash": sha(key), "KeyPrefix": key[:8],
            "KeyLast4": key[-4:], "IssuedAtUtc": stamp(NOW), "Status": "active", "Platform": "windows",
            "DeviceType": device_type, "TenantId": "local"})
    c.commit()
    c.close()


def get(path, key=PERSON_KEY):
    req = urllib.request.Request(GW + path, headers={"Authorization": f"Bearer {key}"})
    with urllib.request.urlopen(req, timeout=15) as res:
        return json.loads(res.read().decode("utf-8"))


def raw(method, path, body, expect_status, key=PERSON_KEY, label="a person"):
    """One request sent as raw JSON, kept exactly as answered."""
    data = None if body is None else body.encode("utf-8")
    req = urllib.request.Request(GW + path, data=data, method=method,
                                 headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=300) as res:
            status, answer = res.status, res.read().decode("utf-8")
    except urllib.error.HTTPError as exc:
        status, answer = exc.code, exc.read().decode("utf-8")
    TRANSCRIPT.append(f"$ {method} {shown(path)}      (raw HTTP, as {label})\n" + (f"  body: {shown(body)}\n" if body else "")
                      + f"  status: {status}\n  answer: {shown(answer)}\n")
    print(f"raw {method} {path}: {status}", flush=True)
    if expect_status is not None and status != expect_status:
        raise SystemExit(f"expected {expect_status} from {method} {path}, got {status}: {answer}")
    return status, (json.loads(answer) if answer.strip().startswith(("{", "[")) else answer)


def cli(args, expect_exit, key=PERSON_KEY, label="a person: the rig's enrolled browser device"):
    """Run this tree's cc-devthrottle and keep exactly what it printed."""
    env = dict(os.environ, CC_GATEWAY_URL=GW, CC_GATEWAY_SESSION_KEY=key, NO_COLOR="1", COLUMNS="200")
    env.pop("CC_SESSION_ID", None)
    launch = ("import sys; sys.path[:0] = [sys.argv[1], sys.argv[2]]; sys.argv = ['cc-devthrottle'] + sys.argv[3:]; "
              "from src.cli import tool_main; tool_main()")
    r = subprocess.run([sys.executable, "-c", launch, str(CLI_DIR), str(CLI_DIR.parent), *args], cwd=str(CLI_DIR),
                       env=env, capture_output=True, text=True, timeout=300)
    line = " ".join(a if " " not in a else f'"{a}"' for a in args)
    TRANSCRIPT.append(f"$ cc-devthrottle {shown(line)}      (as {label})\n{shown(r.stdout)}{shown(r.stderr)}"
                      f"[exit {r.returncode}]\n")
    print(f"cli {shown(line)}: exit {r.returncode}", flush=True)
    if expect_exit is not None and r.returncode != expect_exit:
        raise SystemExit(f"expected exit {expect_exit} from: cc-devthrottle {line}\n{r.stdout}{r.stderr}")
    return r


def cli_json(args):
    return json.loads(cli([*args, "--json"], 0).stdout)


def query(db, sql, args=()):
    c = sqlite3.connect(db)
    try:
        return c.execute(sql, args).fetchall()
    finally:
        c.close()


def rows(db, title, sql, args=()):
    """Rows read straight from the rig's own gateway.db, for what no route serves."""
    c = sqlite3.connect(db)
    cur = c.execute(sql, args)
    names = [d[0] for d in cur.description]
    found = cur.fetchall()
    c.close()
    TRANSCRIPT.append(f"# {shown(title)}\n#   read from the rig's gateway.db: {shown(sql)} {shown(list(args))}\n"
                      + f"  {', '.join(names)}\n" + "".join(f"  {shown(', '.join(str(v) for v in r))}\n" for r in found)
                      + f"  ({len(found)} rows)\n")
    return found


# ===================================================================================================== the Director


def ps(script, check=True):
    r = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", script], capture_output=True,
                       text=True, timeout=180)
    if check and r.returncode != 0:
        raise SystemExit(f"powershell failed ({r.returncode}): {script}\n{r.stdout}{r.stderr}")
    return r.stdout


def set_task(execute, arguments, working):
    """Register this rig's own task with one action and a far-future, on-demand-only trigger (rule 0b's shape)."""
    arg = f" -Argument '{arguments}'" if arguments else ""
    ps(f"$a = New-ScheduledTaskAction -Execute '{execute}'{arg} -WorkingDirectory '{working}'; "
       "$t = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(5); "
       f"Register-ScheduledTask -TaskName '{TASK}' -Action $a -Trigger $t -Force | Out-Null")


def unregister_task():
    ps(f"Unregister-ScheduledTask -TaskName '{TASK}' -Confirm:$false")
    left = ps(f"Get-ScheduledTask -TaskName '{TASK}' -ErrorAction SilentlyContinue | ForEach-Object {{ $_.TaskName }}",
              check=False).strip()
    print(f"{TASK} unregistered" if not left else f"WARNING: {TASK} is still registered", flush=True)


def processes_named(name):
    out = ps(f"Get-Process -Name {name} -ErrorAction SilentlyContinue | ForEach-Object {{ $_.Id.ToString() + '|' + $_.Path }}",
             check=False)
    return [tuple(x.split("|", 1)) for x in out.split()]


def director_processes():
    """The ids of every process running exactly this slot's exe."""
    return [int(pid) for pid, path in processes_named("cc-director7") if path.lower() == str(DIRECTOR_EXE).lower()]


def registration(pid):
    """The instance registration the running Director wrote for itself: its id, read from the file naming its pid."""
    folder = HOME / "config" / "director" / "instances"
    for f in folder.glob("*.json") if folder.exists() else []:
        try:
            j = json.loads(f.read_text(encoding="utf-8-sig"))
        except (json.JSONDecodeError, OSError):
            continue
        if j.get("Pid") == pid and j.get("DirectorId"):
            return j
    return None


def prepare_director(token):
    """The Director's own storage root: the rig Gateway and its key, the agent, the folder, this tree's command."""
    config = HOME / "config"
    (config / "director").mkdir(parents=True, exist_ok=True)
    real = json.loads((Path(os.environ["LOCALAPPDATA"]) / "cc-director" / "instances" / "default" / "config"
                       / "config.json").read_text(encoding="utf-8-sig"))
    claude = [e for e in real["agent"]["entries"] if e.get("type") == "ClaudeCode"]
    if not claude:
        raise SystemExit("the owner's Director knows no Claude Code entry to copy the executable path from")
    (config / "config.json").write_text(json.dumps({
        "onboarding": {"completed": True},
        "autoUpdate": {"enabled": False},
        "gateway": {"url": GW, "token": token, "streamMode": True},
        "agent": dict(real["agent"], entries=claude),
    }, indent=2), encoding="utf-8")
    (config / "director" / "repositories.json").write_text(json.dumps([{"Name": "qa-plain", "Path": str(PLAIN)}],
                                                                      indent=2), encoding="utf-8")
    py = Path(os.environ["LOCALAPPDATA"]) / "cc-director" / "pyenv" / "Scripts" / "python.exe"
    if not py.exists():
        raise SystemExit(f"no python environment at {py} to run this tree's cc-devthrottle with")
    (HOME / "bin").mkdir(parents=True, exist_ok=True)
    (HOME / "bin" / "cc-devthrottle.cmd").write_text(
        f"@echo off\r\nset PYTHONPATH={CLI_DIR};{CLI_DIR.parent}\r\n\"{py}\" -c \"from src.cli import tool_main; "
        f"tool_main()\" %*\r\n", encoding="ascii")
    wrapper = RIG / "start-director7.cmd"
    wrapper.write_text("@echo off\r\n"
                       f"set CC_DIRECTOR_ROOT={DIR_ROOT}\r\n"
                       "set CC_AUTOUPDATE=0\r\n"
                       "set CC_GATEWAY_NO_TAILSCALE=1\r\n"
                       f"\"{DIRECTOR_EXE}\"\r\n", encoding="ascii")
    return wrapper


def rig_processes(but=()):
    """Every process whose command line names the rig folder, other than the ids in `but`."""
    out = ps("Get-CimInstance Win32_Process | ForEach-Object { [string]$_.ProcessId + '|' + $_.Name + '|' + "
             "$_.CommandLine }", check=False)
    found = []
    for line in out.splitlines():
        parts = line.split("|", 2)
        if len(parts) == 3 and str(RIG).lower() in parts[2].lower() and int(parts[0]) not in but:
            found.append((int(parts[0]), parts[1]))
    return found


def wait_for_tool_install(director):
    """A Director on a fresh storage root installs its Python tools with pip in the background. A clean stop does not
    end that install, so the rig waits for it before asking the Director to stop."""
    def pip_done():
        return not [p for p in rig_processes(but=(director,)) if p[1].lower() == "python.exe"]
    wait_for("the slot 7 Director's Python tool install did not finish", pip_done, seconds=600, every=5)


def stop_director(pid):
    """The named shutdown signal, for this Director's own id. Never a kill of anything this run did not start."""
    reg = registration(pid)
    if reg is None:
        raise SystemExit(f"no instance registration names pid {pid}; it cannot be asked to stop")
    signal = f"Local\\cc-director-shutdown-{reg['DirectorId'].lower()}"
    ps(f"$e = [System.Threading.EventWaitHandle]::OpenExisting('{signal}'); $e.Set() | Out-Null; $e.Dispose()")
    wait_for(f"the slot 7 Director (pid {pid}) did not exit after its shutdown signal",
             lambda: pid not in director_processes(), seconds=90)
    print(f"slot 7 Director {pid} stopped by its named signal", flush=True)


def director_log(title, contains):
    """The Director's own log lines naming `contains`."""
    found = []
    for log in sorted((HOME / "logs" / "director").glob("*.log")):
        for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
            if contains in line:
                found.append(line)
    record(f"{title} - the Director's log, lines naming '{contains}'", "\n".join(f"  {x}" for x in found) or "  (none)")
    return found


def folder_listing(title, folder):
    lines = []
    if folder.exists():
        for p in sorted(folder.rglob("*")):
            rel = p.relative_to(folder).as_posix()
            lines.append(f"{rel}/" if p.is_dir() else f"{rel} ({p.stat().st_size} bytes)")
    record(title, "\n".join(f"  {x}" for x in lines) or "  (the folder does not exist)")
    return lines


# ===================================================================================================== sessions

POINTER = "\u276f"
TENDED = {}


def listed_sessions():
    answer = get("/sessions")
    items = answer.get("sessions", answer.get("items", [])) if isinstance(answer, dict) else answer
    return [s for s in items if isinstance(s, dict)]


def buffer_of(sid):
    answer = get(f"/sessions/{sid}/buffer")
    if isinstance(answer, dict):
        for key in ("text", "Text", "buffer", "Buffer"):
            if isinstance(answer.get(key), str):
                return answer[key]
    return answer if isinstance(answer, str) else ""


def keys(sid, text):
    req = urllib.request.Request(GW + f"/sessions/{sid}/prompt", method="POST",
                                 data=json.dumps({"text": text, "appendEnter": False}).encode("utf-8"),
                                 headers={"Authorization": f"Bearer {PERSON_KEY}", "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=30) as res:
        res.read()


def tend_sessions():
    """Claude Code asks whether to trust each folder it has never opened, and a session's first prompt waits behind
    that question. The rig answers it the way a person at the screen would, and records that it did. Nothing else is
    ever typed into a session."""
    for s in listed_sessions():
        sid = s.get("sessionId") or s.get("id")
        if not sid or TENDED.get(sid):
            continue
        try:
            screen = buffer_of(sid)
        except (urllib.error.URLError, OSError):
            continue
        flat = re.sub(r"\s+", "", screen)
        if "Itrustthisfolder" not in flat or "No,exit" not in flat or POINTER not in flat:
            continue
        chosen = flat[flat.rindex(POINTER) + 1:]
        if chosen.startswith("Yes"):
            keys(sid, "\r")
            TENDED[sid] = 1
            start = flat.find("Quicksafetycheck")
            record(f"the rig answered Claude Code's folder-trust question in session {sid}",
                   f"  the screen, blanks removed: {flat[start:start + 200]}\n"
                   "  answered: Enter, with the pointer on 'Yes, I trust this folder'")
            time.sleep(2)
        elif chosen.startswith("No"):
            keys(sid, "\u001b[B")
            time.sleep(1)


def find_transcript(marker, since, seconds=300):
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
    return wait_for(f"no Claude Code transcript carrying '{marker}' appeared", found, seconds=seconds, every=2, tend=True)


def entries(path):
    out = []
    for line in path.read_text(encoding="utf-8").splitlines():
        try:
            out.append(json.loads(line))
        except json.JSONDecodeError:
            pass
    return out


def finished(path):
    last = [e for e in entries(path) if e.get("type") == "assistant"]
    return bool(last) and (last[-1].get("message") or {}).get("stop_reason") == "end_turn"


def wait_answer(marker, since, seconds=420):
    path = find_transcript(marker, since)
    USED_TRANSCRIPTS.add(path)
    wait_for(f"the session carrying '{marker}' did not finish its turn", lambda: finished(path), seconds=seconds,
             every=3, tend=True)
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
    record(f"{title} - read from the session's own Claude Code transcript", "\n".join(f"  {x}" for x in lines))


SESSION_FIELDS = ("sessionId", "name", "repoPath", "factoryKitDirectory", "factory", "factorySeat",
                  "factoryDefinitionVersion", "directorId", "machineName", "agent", "status")


def session_row(title, sid):
    found = next((s for s in listed_sessions() if s.get("sessionId") == sid), None)
    body = json.dumps({k: found.get(k) for k in SESSION_FIELDS}, indent=2) if found else "(not in the list)"
    record(f"{title} - GET /sessions, the fields that say where and by whom it runs", body)
    return found


def stop_session(sid):
    cli(["session", "stop", sid, "--reason", "the phase 5b QA rig is done with this session"], None)


def kit_check_files(title):
    """Every check.json in the Director's kit folders now."""
    base = HOME / "factory-run"
    lines = []
    for f in sorted(base.rglob("check.json")) if base.exists() else []:
        lines.append(f"{f.relative_to(base).as_posix()}:")
        lines += [f"    {x}" for x in f.read_text(encoding="utf-8-sig").splitlines()]
    record(f"{title} - every check.json in the Director's kit folders (<rig>/director-root/instances/default/factory-run)",
           "\n".join(f"  {x}" for x in lines) or "  (none)")


# ===================================================================================================== the definition

MAIL_PROMPT = ("QA-P5B-MAIL. The check counted {count} new mail. What it found:\n{context}\n"
               "Do exactly one step, then stop: run  cat \"$CC_FACTORY_KIT_DIR/check.json\"  with Bash and quote what it "
               "printed.")
TIMED_PROMPT = ("QA-P5B-TIMED. The timed check counted {count}. What it found: {context}\n"
                "Do exactly one step, then stop: run  cat \"$CC_FACTORY_KIT_DIR/check.json\"  with Bash and quote what it "
                "printed.")
PLAIN_PROMPT = "QA-P5B-PLAIN. Do exactly one step, then stop: run pwd -W with Bash and quote what it printed."
QUIET_PROMPT = "QA-P5B-NEVER. This start's check never finds work, so no session should ever read this."


def block(text, indent="      "):
    return "".join(f"{indent}{x}\n" for x in text.splitlines())


def seat(seat_id, start, brief="Act on what the check found."):
    return (f"seat: {seat_id}\nname: {seat_id.capitalize()}\nrole: {seat_id.capitalize()}\nbrief: |\n  {brief}\n"
            f"starts:\n{start}")


def interval_start(check, every="24h", secrets=None, prompt=QUIET_PROMPT, extra=""):
    s = f"  - every: {every}\n    check: checks/{check}\n"
    if secrets:
        s += f"    secrets: [{', '.join(secrets)}]\n"
    return s + extra + "    prompt: |\n" + block(prompt)


def timed_start(check, at, prompt=QUIET_PROMPT):
    return (f"  - cron: \"{at.minute} {at.hour} * * *\"\n    timeZone: UTC\n    check: checks/{check}\n"
            "    prompt: |\n" + block(prompt))


INTERVAL_SEATS = {
    "mail": lambda: interval_start("new_mail.py", "1m", [TENANT_ENTRY, CLIENT_ENTRY, SECRET_ENTRY], MAIL_PROMPT),
    "probe": lambda: interval_start("env_probe.py", "24h", [TENANT_ENTRY]),
    "bigstate": lambda: interval_start("big_state.py", "1m"),
    "failexit": lambda: interval_start("fail_exit.py"),
    "failtimeout": lambda: interval_start("fail_timeout.py"),
    "failnotjson": lambda: interval_start("fail_not_json.py"),
    "failnocount": lambda: interval_start("fail_no_count.py"),
    "failcontext": lambda: interval_start("fail_big_context.py"),
    "failsecret": lambda: interval_start("quiet.py", "24h", [MISSING_ENTRY]),
}
FAILURE_SEATS = ["failexit", "failtimeout", "failnotjson", "failnocount", "failcontext", "failsecret"]


def mail_rules():
    return json.dumps({
        "mailbox": "qa-outreach@example.com", "folders": ["inbox"], "ledger": "files/ledger.json",
        "bounceSubjectMarkers": ["Undeliverable"], "firstRunLookbackHours": 24,
        "secrets": {"tenantId": TENANT_ENTRY, "clientId": CLIENT_ENTRY, "clientSecret": SECRET_ENTRY},
        "graph": f"{FAKE}/v1.0", "signIn": f"{FAKE}/sign-in"}, indent=2) + "\n"


LEDGER = json.dumps({"people": [{"address": "partner@firm-a.example", "sent": "2026-10-07"},
                                {"address": "cto@firm-b.example", "sent": "2026-10-08"}]}, indent=2) + "\n"
PROBE = json.dumps({"undeclaredVariables": ["POSTHOG_PROJECT_ID", "CLOUDFLARE_REGISTRAR_TOKEN", "POSTHOG_PROJECT_KEY"],
                    "selfCallEntry": SELF_CALL_ENTRY, "selfCallVariable": "POSTHOG_PROJECT_KEY"}, indent=2) + "\n"


def write_definition(name, seats, extra_checks=(), extra_files=None):
    """A definition folder: the boss and the given seats (id -> start text), every rig check, the rules, ledger and
    probe files. Recorded in full."""
    folder = RIG / "definitions" / name
    if folder.exists():
        shutil.rmtree(folder)
    files = {
        "factory.yaml": (f"factory: {FACTORY}\ntitle: QA Checks\npurpose: Shows checks from a definition on a real "
                         "Director\ngoalText: |\n  Prove the checks.\ngoalApprovedOn: 2026-10-15\nbossSeat: boss\n"
                         f"defaults:\n  machine: {MACHINE}\n  agent: ClaudeCode\n  model: {MODEL}\n"),
        "seats/boss.yaml": "seat: boss\nname: Boss\nrole: Boss\nbrief: |\n  Read the factory's problems.\n",
        "checks/new_mail.py": EMAIL_CHECK.read_text(encoding="utf-8").replace("\r\n", "\n"),
        "files/mail-rules.json": mail_rules(),
        "files/ledger.json": LEDGER,
        "files/probe.json": PROBE,
    }
    for f in sorted((HERE / "checks").glob("*.py")):
        if f.name != "draft_only.py" or "draft_only.py" in extra_checks:
            files[f"checks/{f.name}"] = f.read_text(encoding="utf-8").replace("\r\n", "\n")
    for seat_id, start in seats.items():
        files[f"seats/{seat_id}.yaml"] = seat(seat_id, start)
    files.update(extra_files or {})
    for rel, text in files.items():
        f = folder / rel
        f.parent.mkdir(parents=True, exist_ok=True)
        f.write_text(text, encoding="utf-8", newline="\n")
    shown_files = {k: v for k, v in files.items() if not k.startswith("checks/") or k in ("checks/new_mail.py",)}
    record(f"the definition folder <rig>/definitions/{name}: every seat and file (each check script is the rig's "
           f"rig/checks/<name>.py; checks/new_mail.py is the phase 0 email check, byte for byte)",
           "\n".join(f"  --- {rel}\n" + ("    (the phase 0 check, docs/qa/factory-definition/phase-0/inventory/email-check/"
                                        "check_new_mail.py)\n" if rel == "checks/new_mail.py" else
                                        "".join(f"    {x}\n" for x in text.splitlines()))
                     for rel, text in sorted(shown_files.items()))
           + "\n  checks: " + ", ".join(sorted(k for k in files if k.startswith("checks/"))))
    return str(folder)


def save(name_or_folder, note_text):
    """Save a draft on top of the newest version; return the new version number, or None when the save was refused."""
    r = cli(["factory", "definition", "save", FACTORY, "--dir", name_or_folder, "--expected-version",
             str(STATE["newest"]), "--note", note_text, "--json"], None)
    if r.returncode != 0:
        return None
    version = json.loads(r.stdout)["version"]
    STATE["newest"] = version
    return version


def publish(version, expect_exit=0):
    r = cli(["factory", "definition", "publish", FACTORY, "--version", str(version), "--replaces", str(STATE["published"])],
            expect_exit)
    if r.returncode == 0:
        STATE["published"] = version
    return r


# ===================================================================================================== readers


def trigger_of(seat_id):
    return next(t for t in get("/triggers")["triggers"] if t.get("factory") == FACTORY and t.get("seat") == seat_id)


def runs_of(db, trigger_id):
    return query(db, "SELECT Id, CheckedUtc, Outcome, Count, SessionId, Reason, DirectorId FROM trigger_runs "
                     "WHERE TriggerId = ? COLLATE NOCASE ORDER BY RecordedUtc", (trigger_id,))


def wait_runs(db, seat_id, count, seconds=240):
    tid = trigger_of(seat_id)["id"]
    return wait_for(f"the {seat_id} trigger did not reach {count} recorded check(s)",
                    lambda: (lambda r: r if len(r) >= count else None)(runs_of(db, tid)), seconds=seconds, every=3,
                    tend=True)


def trigger_runs(db, title, seat_id):
    tid = trigger_of(seat_id)["id"]
    cli(["trigger", "runs", tid], 0)
    return rows(db, f"{title}: every recorded check of the {seat_id} start's trigger",
                "SELECT CheckedUtc, Outcome, Count, SessionId, Reason, DirectorId FROM trigger_runs "
                "WHERE TriggerId = ? COLLATE NOCASE ORDER BY RecordedUtc", (tid,))


def state_rows(db, title, seat_id):
    tid = trigger_of(seat_id)["id"]
    return rows(db, f"{title}: the {seat_id} start's factory_check_state rows (newest last)",
                "SELECT Sequence, LENGTH(Content) AS Bytes, WrittenUtc, WrittenBy, RunRef, Reset, "
                "SUBSTR(Content, 1, 160) AS Starts FROM factory_check_state WHERE OwnerKey = ? COLLATE NOCASE "
                "ORDER BY Sequence", (f"trigger:{tid}",))


def sessions_of(seat_id):
    return [s for s in listed_sessions() if s.get("factory") == FACTORY and s.get("factorySeat") == seat_id]


# ===================================================================================================== the sections


def section_undefined(db):
    note("CASE 13 - qa-plain has NO definition: registered with its folder; two COMMAND triggers (one quiet, one "
         "broken) and a schedule run now. Each runs exactly as before phase 5b: the triggers' checks run in the "
         "folder as a command, the broken one turns its trigger red and feeds no problem, and the run starts a session "
         "in the registered folder.")
    PLAIN.mkdir(parents=True, exist_ok=True)
    (PLAIN / "README.md").write_text("# qa-plain\n\nThe folder of a factory with no definition.\n", encoding="utf-8")
    (PLAIN / "quiet_check.py").write_text('print(\'{"count": 0}\')\n', encoding="utf-8")
    (PLAIN / "broken_check.py").write_text('import sys\nprint("cannot reach the shop", file=sys.stderr)\nsys.exit(2)\n',
                                           encoding="utf-8")
    manifest = RIG / "qa-plain.manifest.json"
    manifest.write_text(json.dumps({"factory": UNDEFINED, "title": "QA Plain", "folder": str(PLAIN),
                                    "computer": MACHINE, "bossSeat": "boss",
                                    "seats": [{"id": "boss", "name": "Boss", "role": "Boss", "briefFile": "BOSS.md",
                                               "schedules": []}]}, indent=2), encoding="utf-8")
    cli(["factory", "register", "--manifest", str(manifest)], 0)
    for name, script in (("qa-plain-quiet", "quiet_check.py"), ("qa-plain-broken", "broken_check.py")):
        cli(["trigger", "add", "--name", name, "--factory", UNDEFINED, "--agent", "boss", "--machine", MACHINE,
             "--repo", str(PLAIN), "--check", f"python {script}", "--every", "1h", "--prompt", "Look: {count}."], 0)
    STATE["plain_job"] = cli_json(["schedule", "create", "--name", "QA Plain boss", "--machine", MACHINE, "--repo",
                                   str(PLAIN), "--cron", "0 5 1 1 *", "--tz", "UTC", "--seed", PLAIN_PROMPT,
                                   "--factory", UNDEFINED, "--seat", "boss"])["id"]


def section_undefined_after(db):
    note("CASE 13, continued - the two command triggers' checks, run by the Director, and the schedule's run now.")
    for name in ("qa-plain-quiet", "qa-plain-broken"):
        tid = next(t["id"] for t in get("/triggers")["triggers"] if t.get("name") == name)
        wait_for(f"{name} recorded no check", lambda: runs_of(db, tid), seconds=120, every=3)
        cli(["trigger", "runs", name], 0)
        rows(db, f"{name}: its recorded checks", "SELECT CheckedUtc, Outcome, Count, Reason, DirectorId FROM "
             "trigger_runs WHERE TriggerId = ? COLLATE NOCASE", (tid,))
    cli(["trigger", "list"], 0)
    since = time.time()
    cli(["schedule", "run", STATE["plain_job"]], 0)
    path = wait_answer("QA-P5B-PLAIN", since)
    summarize("the undefined factory's scheduled run", path)
    run = wait_for("the undefined run recorded no session", lambda: next(
        (r for r in cli_json(["schedule", "runs", STATE["plain_job"]]) if r.get("sessionId")), None), 60)
    rows(db, "the undefined run's session history row", "SELECT SessionId, RepoPath, Factory, FactorySeat, "
         "FactoryDefinitionVersion FROM session_history WHERE SessionId = ? COLLATE NOCASE", (run["sessionId"],))
    director_log("the undefined trigger checks on the Director", "trigger=qa-plain")
    stop_session(run["sessionId"])


def section_publish(db):
    note("PUBLISH - the first definition of qa-checks: the boss and nine seats with an interval start and a check each "
         "(the mail check, two probes, six that break the contract). The publish turns each into a definition "
         "trigger, and asks this machine about each check's Python and secrets.")
    v = save(write_definition("v1", {k: f() for k, f in INTERVAL_SEATS.items()}), "the first definition: interval checks")
    cli(["factory", "definition", "diff", FACTORY], 0)
    publish(v)
    STATE["published_at"] = time.time()
    cli(["trigger", "list"], 0)
    raw("GET", "/triggers", None, 200)
    cli(["factory", "check-state", "show", FACTORY], 0)


def section_mail(db):
    note("CASES 1 to 4 - the email check, every minute, against the fake mailbox. Run 1: an empty inbox. Run 2: mail "
         "from an UNLISTED sender only. Run 3: mail from a LISTED sender (and the unlisted one). Run 4: the same "
         "inbox again, so only the state the third run left tells it the mail was already counted.")
    box = STATE["mailbox"]
    box.put("inbox", [])
    first = wait_runs(db, "mail", 1)
    record("RUN 1 recorded (the inbox was empty)", f"  {first[-1]}")
    box.put("inbox", [mail("m-1", 3, "newsletter@shop.example", "Weekly offers")])
    wait_runs(db, "mail", 2)
    box.put("inbox", [mail("m-1", 3, "newsletter@shop.example", "Weekly offers"),
                      mail("m-2", 1, "Partner@Firm-A.example", "Re: the proposal")])
    since = time.time()
    third = wait_runs(db, "mail", 3)
    record("RUN 3 recorded (a listed sender wrote)", f"  {third[-1]}")
    fourth = wait_runs(db, "mail", 4)
    record("RUN 4 recorded (the same inbox again)", f"  {fourth[-1]}")
    trigger_runs(db, "CASES 1 to 4", "mail")
    state_rows(db, "CASE 4", "mail")
    cli(["factory", "check-state", "show", FACTORY, "mail"], 0)
    record("what the fake mailbox was asked, in order (one line per sign-in and folder read)",
           "\n".join(f"  {x}" for x in box.reads))
    sid = third[-1][4]
    if not sid:
        raise SystemExit(f"run 3 started no session: {third[-1]}")
    path = wait_answer("QA-P5B-MAIL", since)
    summarize("CASE 2 - the session the third run started", path)
    session_row("CASE 2 - the session the third run started", sid)
    kit_check_files("CASE 2 - while the session ran")
    rows(db, "CASE 2 - its session history row", "SELECT SessionId, DirectorId, Factory, FactorySeat, "
         "FactoryDefinitionVersion FROM session_history WHERE SessionId = ? COLLATE NOCASE", (sid,))
    record("CASE 2 - sessions of the mail seat now (one)", json.dumps([s.get("sessionId") for s in sessions_of("mail")]))
    stop_session(sid)


def section_probes(db):
    note("CASES 9 and 10 - the state probe (about 20 KB of state, kept across runs) and the environment probe (what a "
         "check sees, and a secret it did not declare).")
    wait_runs(db, "bigstate", 2)
    trigger_runs(db, "CASE 9", "bigstate")
    state_rows(db, "CASE 9", "bigstate")
    newest = query(db, "SELECT Content FROM factory_check_state WHERE OwnerKey = ? COLLATE NOCASE ORDER BY Sequence "
                       "DESC LIMIT 1", (f"trigger:{trigger_of('bigstate')['id']}",))[0][0]
    found = json.loads(newest)
    record("CASE 9 - the newest stored state, read whole from the row: its run number and what that run found in the "
           "state the previous run left", f"  bytes: {len(newest.encode('utf-8'))}\n  run: {found['run']}\n"
           f"  previousRunFound: {json.dumps(found['previousRunFound'])}")
    wait_runs(db, "probe", 1)
    trigger_runs(db, "CASE 10", "probe")
    cli(["factory", "check-state", "show", FACTORY, "probe"], 0)
    content = query(db, "SELECT Content FROM factory_check_state WHERE OwnerKey = ? COLLATE NOCASE ORDER BY Sequence "
                        "DESC LIMIT 1", (f"trigger:{trigger_of('probe')['id']}",))[0][0]
    record("CASE 10 - what the environment probe wrote into its state (names only, never a value)", content)
    director_log("CASE 10 - how the Director ran the probe", "env_probe")


def section_failures(db):
    note("CASE 5 - each check that breaks the contract, recorded as failed with its reason. The timeout check runs "
         "five minutes (its start is every 24h, so its timeout is the five-minute ceiling).")
    for s in FAILURE_SEATS:
        wait_runs(db, s, 1, seconds=600)
    STATE["failures_recorded"] = datetime.now(timezone.utc)
    for s in FAILURE_SEATS:
        trigger_runs(db, f"CASE 5 ({s})", s)
    cli(["trigger", "list"], 0)
    director_log("CASE 5 - the missing secret, on the Director", MISSING_ENTRY)


def section_draft(db):
    note("CASE 6 - a check in an unpublished draft: version N+1 adds the seat 'drafty' with checks/draft_only.py and is "
         "saved, never published. The Director's download route refuses that version; no trigger and no run exist.")
    seats = {k: f() for k, f in INTERVAL_SEATS.items()}
    seats["drafty"] = interval_start("draft_only.py", "1m")
    v = save(write_definition("v-draft", seats, extra_checks=("draft_only.py",)), "a draft check, never published")
    STATE["draft_version"] = v
    cli(["factory", "definition", "history", FACTORY], 0)
    raw("GET", f"/directors/{STATE['director_id']}/factory-checks/{FACTORY}/{v}", None, 404, key=DIRECTOR_KEY,
        label="the slot 7 Director's own device key")
    raw("GET", f"/directors/{STATE['director_id']}/factory-checks/{FACTORY}/{STATE['published']}", None, 200,
        key=DIRECTOR_KEY, label="the slot 7 Director's own device key (the PUBLISHED version, for comparison)")
    time.sleep(70)
    cli(["trigger", "list"], 0)
    rows(db, "CASE 6 - triggers of the drafty seat (none)", "SELECT Id, Name FROM triggers WHERE Seat = 'drafty'")
    rows(db, "CASE 6 - check state of any drafty start (none)", "SELECT * FROM factory_check_state WHERE OwnerKey "
         "IN (SELECT 'trigger:' || Id FROM triggers WHERE Seat = 'drafty')")
    folder_listing("CASE 6 - the Director's downloaded checks (<rig>/director-root/instances/default/factory-checks)",
                   HOME / "factory-checks")
    found = [p for p in (HOME / "factory-checks").rglob("draft_only.py")] if (HOME / "factory-checks").exists() else []
    record("CASE 6 - draft_only.py anywhere under the Director's factory-checks", f"  {len(found)} file(s) {found}")


def section_refusals(db):
    note("CASE 12 - two definitions refused: an interval start with no check, and a checked start with "
         "preventOverlap: false. Each is saved, then its publish is tried.")
    base = {k: f() for k, f in INTERVAL_SEATS.items()}
    no_check = dict(base, nocheck="  - every: 10m\n    prompt: |\n      Look every ten minutes.\n")
    v = save(write_definition("v-no-check", no_check), "an interval start with no check")
    if v is not None:
        publish(v, 1)
    overlap = dict(base, overlap=interval_start("quiet.py", "1h", extra="    preventOverlap: false\n"))
    v = save(write_definition("v-overlap-off", overlap), "preventOverlap false on a checked start")
    if v is not None:
        publish(v, 1)
    cli(["factory", "definition", "history", FACTORY], 0)


def section_timed(db):
    note("CASES 7, 8 and 11 - timed starts with a check. A new version keeps every interval seat and adds five timed "
         "seats, each firing once a day at a minute a few minutes from now (UTC): 'tquiet' finds nothing, 'twork' finds "
         "work, 'tslow' takes 200 seconds, and 'tlater1'/'tlater2' (quiet) fire one and two minutes after them, while "
         "'tslow' is still running.")
    at = (datetime.now(timezone.utc) + timedelta(minutes=4)).replace(second=0, microsecond=0)
    seats = {k: f() for k, f in INTERVAL_SEATS.items()}
    seats.update({"tquiet": timed_start("quiet.py", at), "twork": timed_start("work.py", at, TIMED_PROMPT),
                  "tslow": timed_start("slow.py", at), "tlater1": timed_start("quiet.py", at + timedelta(minutes=1)),
                  "tlater2": timed_start("quiet.py", at + timedelta(minutes=2))})
    v = save(write_definition("v-timed", seats), "timed starts with checks")
    cli(["factory", "definition", "diff", FACTORY], 0)
    publish(v)
    jobs = {j["seat"]: j["id"] for j in get("/cron/jobs")["jobs"] if j.get("factory") == FACTORY}
    cli(["schedule", "list", "--json"], 0)
    record("CASE 7, 8, 11 - the timed starts fire at (UTC)", f"  tquiet, twork, tslow: {utc(at)}\n"
           f"  tlater1: {utc(at + timedelta(minutes=1))}\n  tlater2: {utc(at + timedelta(minutes=2))}")
    since = time.time()
    cron = "SELECT Id, JobName, Seat, FiredUtc, Result, InfraStatus, ClaimedByDirectorId, CheckCount, CheckedUtc, " \
           "SessionId, ResultReason FROM cron_runs WHERE Factory = ? AND Seat IN ('tquiet','twork','tslow','tlater1'," \
           "'tlater2') ORDER BY FiredUtc, Seat"
    wait_for("the timed starts did not all fire", lambda: len(query(db, cron, (FACTORY,))) >= 5,
             seconds=480, every=5, tend=True)
    wait_for("tlater2 did not fire", lambda: any(r[2] == "tlater2" for r in query(db, cron, (FACTORY,))),
             seconds=240, every=5, tend=True)
    slow = [r for r in query(db, cron, (FACTORY,)) if r[2] == "tslow"]
    rows(db, "CASE 11 - the timed runs when tlater2 has fired: tslow's check is still running (no CheckedUtc)",
         cron, (FACTORY,))
    record("CASE 11 - tslow's check had reported when tlater2 fired", f"  {bool(slow and slow[0][8])}")
    wait_for("tslow did not report", lambda: any(r[2] == "tslow" and r[8] for r in query(db, cron, (FACTORY,))),
             seconds=360, every=5, tend=True)
    path = wait_answer("QA-P5B-TIMED", since)
    rows(db, "CASES 7, 8 and 11 - the timed runs after every check reported", cron, (FACTORY,))
    for seat_id in ("tquiet", "twork"):
        cli(["schedule", "runs", jobs[seat_id]], 0)
    summarize("CASE 8 - the session twork's check started", path)
    sid = next(r[9] for r in query(db, cron, (FACTORY,)) if r[2] == "twork" and r[9])
    session_row("CASE 8 - the session twork's check started", sid)
    kit_check_files("CASE 8 - while the session ran")
    rows(db, "CASE 8 - the Director the session started on, beside the Director that claimed and ran the check",
         "SELECT h.SessionId, h.DirectorId AS SessionDirector, c.ClaimedByDirectorId AS CheckDirector FROM "
         "session_history h JOIN cron_runs c ON c.SessionId = h.SessionId WHERE h.SessionId = ? COLLATE NOCASE", (sid,))
    stop_session(sid)


def email_zone(after):
    """The time zone whose next 08:00 comes soonest after `after` (UTC), with the boundary it gives. Only zones whose
    08:00 falls on a quarter hour of UTC exist, so this is at most an hour away."""
    best = None
    for name in sorted(zoneinfo.available_timezones()):
        if not re.match(r"^(Etc/GMT[+-]\d+|Asia/(Kolkata|Kathmandu|Tehran|Kabul|Yangon)|Australia/(Adelaide|Darwin|"
                        r"Eucla)|America/St_Johns|Pacific/(Chatham|Marquesas))$", name):
            continue
        z = zoneinfo.ZoneInfo(name)
        local = after.astimezone(z)
        eight = local.replace(hour=8, minute=0, second=0, microsecond=0)
        if eight <= local:
            eight += timedelta(days=1)
        boundary = eight.astimezone(timezone.utc)
        if best is None or boundary < best[1]:
            best = (name, boundary)
    return best


def section_problems(db):
    note("CASE 5, continued - the failed checks on the factory's Open problems card (GET /gateway/factories/<factory>, "
         "its problems), in the problems fold, and in the 08:00 factory email. The email is composed by the Gateway's "
         "own sweep: the rig sets the account's time zone to one whose 08:00 comes next after the failures, switches the "
         "factory email on, and reads the email the sweep kept. This Gateway holds no account sign-in, so it is never "
         "sent.")
    raw("GET", f"/cron/problems?factory={FACTORY}", None, 200)
    status, page = raw("GET", f"/gateway/factories/{FACTORY}", None, 200)
    record("CASE 5 - the Open problems card, as the factory page carries it", json.dumps(page.get("problems"), indent=2))
    ready = STATE.get("failures_recorded") or datetime.now(timezone.utc)
    zone, boundary = email_zone(max(ready, datetime.now(timezone.utc)) + timedelta(minutes=1))
    record("the email's time zone", f"  {zone}: its next 08:00 is {utc(boundary)} UTC; the sweep composes from five "
           "minutes after that")
    raw("PUT", "/gateway/time-zone", json.dumps({"timeZone": zone}), 200)
    raw("PUT", "/gateway/factory-email", json.dumps({"enabled": True}), 200)
    wait = (boundary + timedelta(minutes=5) - datetime.now(timezone.utc)).total_seconds()
    print(f"waiting {wait / 60:.1f} minutes for the 08:00 slot in {zone}", flush=True)
    emails = wait_for("the sweep kept no factory email", lambda: (lambda e: e if e.get("items") or e.get("emails")
                                                                  else None)(get("/gateway/factory-emails?state=all")),
                      seconds=int(wait) + 300, every=15)
    raw("GET", "/gateway/factory-emails?state=all", None, 200)
    items = emails.get("items") or emails.get("emails")
    status, email = raw("GET", f"/gateway/factory-emails/{items[0]['id']}", None, 200)
    record("CASE 5 - the 08:00 factory email's text body, as the sweep composed and kept it",
           email.get("text") or email.get("Text") or "(no text)")


def section_tests():
    note("CASE 5, the three failures this machine cannot produce: no Python, packages declared with no uv, and a secret "
         "marked for login only (Python and uv are installed here, and adding a login-only entry is owner-only). They "
         "are shown through the Director's own tests, against a fake process table and a fake cc-secrets list.")
    r = subprocess.run(["dotnet", "test", str(REPO / "src" / "CcDirector.Gateway.UnitTests"), "--nologo", "--filter",
                        "FullyQualifiedName~DefinitionCheckRunnerTests.RunAsync_NoPython|FullyQualifiedName~"
                        "DefinitionCheckRunnerTests.RunAsync_PackagesDeclaredAndNoUv|FullyQualifiedName~"
                        "DefinitionCheckRunnerTests.RunAsync_ASecretAllowedForLoginOnly", "--logger",
                        "console;verbosity=normal"], capture_output=True, text=True, timeout=1800, cwd=str(REPO))
    kept = [x for x in (r.stdout + r.stderr).splitlines() if re.search(r"Passed |Failed |Total tests|Passed!|Failed!", x)]
    record("$ dotnet test src/CcDirector.Gateway.UnitTests --filter <the three tests> --logger console;verbosity=normal",
           "\n".join(f"  {x.strip()}" for x in kept) + f"\n  [exit {r.returncode}]")


SECTIONS = ["tests", "undefined", "publish", "mail", "probes", "failures", "draft", "refusals", "timed", "problems"]


def remove_tree(folder):
    def writable(func, path, _):
        os.chmod(path, 0o700)
        func(path)
    shutil.rmtree(folder, onexc=writable)


def main():
    out = Path(sys.argv[1]).resolve()
    only = SECTIONS
    if "--only" in sys.argv:
        only = sys.argv[sys.argv.index("--only") + 1].split(",")
    out.mkdir(parents=True, exist_ok=True)
    for needed in (EXE, DIRECTOR_EXE, EMAIL_CHECK):
        if not needed.exists():
            raise SystemExit(f"build it first: {needed} does not exist")
    if director_processes():
        raise SystemExit("a cc-director7 from this tree is already running; this rig starts its own and never stacks one")
    if ps(f"Get-ScheduledTask -TaskName '{TASK}' -ErrorAction SilentlyContinue | ForEach-Object {{ $_.TaskName }}",
          check=False).strip():
        raise SystemExit(f"{TASK} is already registered: another run of this rig may be going. It is left untouched.")
    if RIG.exists() and rig_processes():
        raise SystemExit(f"processes still run from {RIG}: {rig_processes()}; wait for them to end")
    if "tests" in only:
        section_tests()
    if only == ["tests"]:
        (out / "transcript.txt").write_text("\n".join(TRANSCRIPT), encoding="utf-8", newline="\n")
        return
    if RIG.exists():
        remove_tree(RIG)
    GW_ROOT.mkdir(parents=True)
    (GW_ROOT / "config").mkdir()
    (GW_ROOT / "config" / "config.json").write_text('{ "factoryAgents": { "enabled": true } }\n', encoding="utf-8")
    mailbox = FakeMailbox()
    server = serve_fake(mailbox)
    STATE.update(mailbox=mailbox, newest=0, published=0)
    env = dict(os.environ, CC_DIRECTOR_ROOT=str(GW_ROOT), CC_GATEWAY_NO_TAILSCALE="1")
    for k in ("CC_GATEWAY_AUTH", "CC_GATEWAY_NO_AUTH", "REPORT_SERVICE_TOKEN", "NOTIFY_OWNER_SERVICE_TOKEN"):
        env.pop(k, None)
    log = open(RIG / "gateway.out.txt", "w", encoding="utf-8")
    gw = subprocess.Popen([str(EXE), "--port", str(PORT)], env=env, stdout=log, stderr=subprocess.STDOUT,
                          cwd=str(EXE.parent))
    director = None
    task_changed = False
    try:
        wait_for("the rig Gateway wrote no gateway.db", lambda: bool(list(GW_ROOT.rglob("gateway.db"))) or gw.poll() is not None)
        if gw.poll() is not None:
            raise SystemExit("the rig Gateway exited; see gateway.out.txt")
        db = list(GW_ROOT.rglob("gateway.db"))[0]
        wait_for("the rig gateway.db did not get its tables", lambda: {"device_credentials", "factory_check_state",
                 "trigger_runs"} <= {r[0] for r in query(db, "SELECT name FROM sqlite_master WHERE type='table'")})
        enroll(db)
        wait_for("the rig Gateway did not answer", lambda: get("/factory-definition-schemas") is not None)

        wrapper = prepare_director(DIRECTOR_KEY)
        set_task("cmd.exe", f'/c "{wrapper}"', str(DIRECTOR_EXE.parent))
        task_changed = True
        ps(f"Start-ScheduledTask -TaskName '{TASK}'")
        director = wait_for("the slot 7 Director did not start from the task", lambda: (director_processes() or [None])[0], 60)
        reg = wait_for("the slot 7 Director wrote no instance registration", lambda: registration(director), 120)
        STATE["director_id"] = reg["DirectorId"]
        wait_for("the slot 7 Director did not connect to the rig Gateway", lambda: [
            d for d in get("/directors") if d.get("directorId") == reg["DirectorId"]], 120)
        note(f"Gateway: {GW}, built from this tree, storage root <rig>\\gateway-root made empty for this run. The slot 7 "
             f"Director (pid {director}, id {reg['DirectorId']}) was started by its own {TASK} scheduled task through "
             f"<rig>\\start-director7.cmd, storage root <rig>\\director-root, and connected on <machine>. The fake "
             f"mailbox answers on {FAKE}.")
        raw("GET", "/directors", None, 200)

        if "undefined" in only:
            section_undefined(db)
        if "publish" in only:
            section_publish(db)
        if "undefined" in only:
            section_undefined_after(db)
        if "mail" in only:
            section_mail(db)
        if "probes" in only:
            section_probes(db)
        if "failures" in only:
            section_failures(db)
        if "draft" in only:
            section_draft(db)
        if "refusals" in only:
            section_refusals(db)
        if "timed" in only:
            section_timed(db)
        if "problems" in only:
            section_problems(db)
        folder_listing("the Director's downloaded checks at the end (<rig>/director-root/instances/default/factory-checks)",
                       HOME / "factory-checks")
    finally:
        try:
            for s in listed_sessions() if gw.poll() is None else []:
                if s.get("factory") in (FACTORY, UNDEFINED):
                    stop_session(s.get("sessionId"))
        except (urllib.error.URLError, OSError) as exc:
            print(f"could not list sessions to stop them: {exc}", flush=True)
        try:
            if director is not None and director in director_processes():
                wait_for_tool_install(director)
                stop_director(director)
        finally:
            if task_changed:
                unregister_task()
            server.shutdown()
            gw.terminate()
            try:
                gw.wait(timeout=30)
            except subprocess.TimeoutExpired:
                gw.kill()
            log.close()
            (out / "transcript.txt").write_text("\n".join(TRANSCRIPT), encoding="utf-8", newline="\n")
            print(f"wrote {out / 'transcript.txt'}")


if __name__ == "__main__":
    main()
