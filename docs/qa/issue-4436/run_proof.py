"""Issue #4436 proof: a failed schedule tells its owner by default, in one plain sentence - on a throwaway Gateway and a
test Director built from this tree, talking over the real tunnel.

Runs on devlinux or on a Mac, in the foreground, owns everything it starts and stops everything it started, pass or fail:

- A GATEWAY built from this tree (src/CcDirector.Gateway, Debug) on a free port of 127.0.0.1, with its own empty storage
  root under the rig folder, Tailscale off and authentication on. One account (`local`), one person key and one
  Director key, made for this run. It is self-hosted, so it has no DevThrottle account to email: the email leg says so
  in its log, and the webhook is how the rig reads every notice the owner is sent.
- A TEST DIRECTOR built from this tree, slot 5 (local_builds/linux/cc-director-linux5, made by
  scripts/local-build-linux.sh --slot 5 --framework-dependent; on a Mac local_builds/mac/cc-director-mac5, made by
  scripts/local-build-mac.sh --slot 5), with a scratch CC_DIRECTOR_ROOT and a scratch HOME under the rig folder, every
  CC_ variable of this shell removed, and auto-update off. It is stopped only by its own lifecycle request file, never
  killed. Afterwards the rig checks that ~/.local/bin still points at the owner's tools.
- A WEBHOOK RECEIVER on a free port of 127.0.0.1, owned by the rig: the schedule's notices are POSTed to it.
- THIS TREE's cc-devthrottle, against the throwaway Gateway only.

No session is started: every run the rig causes is one whose folder is missing, which the Director refuses before any
agent starts, or one whose machine is offline.

Usage, from the repository root, on devlinux:
    export PATH=$HOME/.dotnet:$HOME/.local/bin:$PATH
    dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj -p:EnableWindowsTargeting=true
    bash scripts/local-build-linux.sh --slot 5 --framework-dependent
    python3 docs/qa/issue-4436/run_proof.py
on a Mac:
    dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj -p:EnableWindowsTargeting=true
    bash scripts/local-build-mac.sh --slot 5
    python3 docs/qa/issue-4436/run_proof.py
It writes docs/qa/issue-4436/transcript.txt on Linux and docs/qa/issue-4436/transcript-mac.txt on a Mac. It takes
about nine minutes: it lets a schedule that fires every minute fail for four minutes.
"""

import hashlib
import http.server
import threading
import json
import os
import pwd
import shutil
import signal
import socket
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent
GATEWAY_EXE = REPO / "src" / "CcDirector.Gateway" / "bin" / "Debug" / "net10.0" / "CcDirector.Gateway"
SLOT = 5
ON_MAC = sys.platform == "darwin"
DIRECTOR_EXE = (REPO / "local_builds" / "mac" / f"cc-director-mac{SLOT}" if ON_MAC
                else REPO / "local_builds" / "linux" / f"cc-director-linux{SLOT}")
# What the Gateway must say about this machine's system: its id, its name, the computer, and its example folder's start.
OS_ID, OS_NAME, A_COMPUTER, EXAMPLE = (("macos", "Mac", "a Mac", "/Users/<you>/Documents/") if ON_MAC
                                       else ("linux", "Linux", "a Linux computer", "/home/<you>/"))
CLI_DIR = REPO / "tools" / "cc-devthrottle"
USER_HOME = Path(pwd.getpwuid(os.getuid()).pw_dir)
USER_BIN = USER_HOME / ".local" / "bin"
# The owner's DevThrottle data folder: ~/Library/Application Support on a Mac, ~/.local/share on Linux.
OWNER_DATA = (USER_HOME / "Library" / "Application Support" / "cc-director" if sys.platform == "darwin"
              else USER_HOME / ".local" / "share" / "cc-director")
OWNER_TOOLS = OWNER_DATA / "pyenv" / "bin"
OWNER_INSTANCE = OWNER_DATA / "instances" / "default"
PYENV_PYTHON = OWNER_TOOLS / "python"
# The name the Director reports (Environment.MachineName), read once it has joined; the host name until then. On a Mac
# the host name may carry ".local", which the Director's name does not.
MACHINE = socket.gethostname().split(".")[0]
DOTNET_ROOT = USER_HOME / ".dotnet"

RIG = Path(tempfile.gettempdir()) / "issue-4436-rig"
GW_ROOT = RIG / "gateway-root"
GW_HOME = RIG / "gateway-home"
DIR_ROOT = RIG / "director-root"
DIR_HOME = RIG / "home"
DIR_INSTANCE = DIR_ROOT / "instances" / "default"
PROJECTS = RIG / "projects"
THERE = PROJECTS / "alpha"            # a folder the machine has, registered as one of its repositories
SITE = PROJECTS / "site"              # saved while it is there, then taken away: every run of its schedule fails
QUIET = PROJECTS / "quiet"            # the same, for a schedule whose owner turned notifications off

PERSON_KEY = "qa-person-" + uuid.uuid4().hex
DIRECTOR_KEY = "qa-director-" + uuid.uuid4().hex

TRANSCRIPT = []
STATE = {}
FAILURES = []


# ===================================================================================================== the record


def shown(text):
    """Text for the transcript, with the run's keys and the owner's home folder taken out."""
    text = str(text)
    for key, name in ((PERSON_KEY, "<person key>"), (DIRECTOR_KEY, "<director key>")):
        text = text.replace(key, name)
    return text.replace(str(USER_HOME), "~")


def record(title, body):
    TRANSCRIPT.append(f"# {shown(title)}\n{shown(body)}\n")


def section(title, text):
    TRANSCRIPT.append(f"\n{'=' * 100}\n== {title}\n{'=' * 100}\n{text}\n")
    print(f"== {title}", flush=True)


def check(condition, what):
    TRANSCRIPT.append(f"CHECK {'PASS' if condition else 'FAIL'}: {shown(what)}\n")
    print(f"CHECK {'PASS' if condition else 'FAIL'}: {shown(what)}", flush=True)
    if not condition:
        FAILURES.append(what)


def utc(when=None):
    return (when or datetime.now(timezone.utc)).strftime("%Y-%m-%dT%H:%M:%SZ")


def wait_for(what, ready, seconds=180, every=1.0):
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            value = ready()
            if value:
                return value
        except (urllib.error.URLError, ConnectionError, TimeoutError, OSError, sqlite3.Error, json.JSONDecodeError):
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


def sha(key):
    return hashlib.sha256(key.encode("utf-8")).hexdigest()


def query(sql, args=()):
    c = sqlite3.connect(f"file:{STATE['db']}?mode=ro", uri=True, timeout=10)
    try:
        return c.execute(sql, args).fetchall()
    finally:
        c.close()


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
    """The account `local`, a person (a browser device) and the test Director (a workstation device), each with its
    own key."""
    c = sqlite3.connect(STATE["db"], timeout=30)
    now = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
    insert(c, "tenants", {"Id": "local", "AccountSubject": "qa-rig-local", "Email": "qa-owner@example.com", "CreatedAtUtc": now})
    for device_id, key, kind in (("qa-browser", PERSON_KEY, "browser"), ("qa-director5", DIRECTOR_KEY, "workstation")):
        insert(c, "device_credentials", {
            "DeviceId": device_id, "MachineName": MACHINE, "DeviceKeyHash": sha(key), "KeyPrefix": key[:8],
            "KeyLast4": key[-4:], "IssuedAtUtc": now, "Status": "active", "Platform": "linux",
            "DeviceType": kind, "TenantId": "local"})
    c.commit()
    c.close()


def call(method, path, body=None, key=PERSON_KEY):
    data = None if body is None else json.dumps(body).encode("utf-8")
    req = urllib.request.Request(STATE["gw"] + path, data=data, method=method,
                                 headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=60) as res:
            return res.status, res.read().decode("utf-8")
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode("utf-8")


def get(path):
    status, text = call("GET", path)
    if status != 200:
        raise urllib.error.URLError(f"GET {path}: {status} {text[:200]}")
    return json.loads(text)


def raw(method, path, body=None):
    """One request as raw JSON, kept exactly as answered."""
    status, answer = call(method, path, body)
    TRANSCRIPT.append(f"$ {method} {shown(path)}      (raw HTTP, as the rig's person)\n"
                      + (f"  body: {shown(json.dumps(body))}\n" if body else "")
                      + f"  status: {status}\n  answer: {shown(answer) if answer else '(empty)'}\n")
    print(f"raw {method} {shown(path)}: {status}", flush=True)
    return status, (json.loads(answer) if answer.strip().startswith(("{", "[")) else answer)


def cli_python():
    """The owner's tool Python when it is installed here, else a throwaway one with the tool's own pins (as CI runs it)."""
    if PYENV_PYTHON.exists():
        return [str(PYENV_PYTHON)]
    return ["uv", "run", "--no-project", "--with", "typer==0.16.1", "--with", "click==8.2.1", "--with", "rich==13.0.0",
            "--with", "requests==2.31.0", "--with", "cryptography==42.0.0", "python"]


def cli(args):
    """This tree's cc-devthrottle, against the throwaway Gateway only."""
    gw = STATE["gw"]
    if not gw.startswith("http://127.0.0.1:"):
        raise SystemExit(f"refusing to run cc-devthrottle against {gw}: only the throwaway Gateway")
    env = dict(without_cc(os.environ), CC_GATEWAY_URL=gw, CC_GATEWAY_SESSION_KEY=PERSON_KEY, NO_COLOR="1", COLUMNS="250",
               PYTHONPATH=f"{CLI_DIR}{os.pathsep}{CLI_DIR.parent}", HOME=str(DIR_HOME),
               UV_CACHE_DIR=str(USER_HOME / ".cache" / "uv"))
    r = subprocess.run([*cli_python(), "-c", "from src.cli import tool_main; tool_main()", *args],
                       cwd=str(CLI_DIR), env=env, capture_output=True, text=True, timeout=300)
    line = " ".join(a if " " not in a else f'"{a}"' for a in args)
    TRANSCRIPT.append(f"$ cc-devthrottle {shown(line)}\n{shown(r.stdout)}{shown(r.stderr)}[exit {r.returncode}]\n")
    print(f"cli {shown(line)}: exit {r.returncode}", flush=True)
    return r


def create_args(name, folder):
    return ["schedule", "create", "--name", name, "--machine", MACHINE, "--repo", str(folder),
            "--cron", "0 3 1 1 *", "--tz", "UTC", "--seed", "/help"]


# ===================================================================================================== the processes


def start_gateway():
    GW_ROOT.mkdir(parents=True)
    GW_HOME.mkdir(parents=True)
    port = free_port()
    STATE["gw"] = f"http://127.0.0.1:{port}"
    # A minimal environment, not this shell's: nothing of the owner's reaches it.
    env = {"PATH": "/usr/local/bin:/usr/bin:/bin", "HOME": str(GW_HOME), "LANG": "C.UTF-8",
           "DOTNET_ROOT": str(DOTNET_ROOT), "DOTNET_USE_POLLING_FILE_WATCHER": "1", "CC_DIRECTOR_ROOT": str(GW_ROOT),
           "CC_GATEWAY_NO_TAILSCALE": "1", "TZ": "UTC"}
    STATE["gw_log"] = open(RIG / "gateway.out.txt", "w", encoding="utf-8")
    gw = subprocess.Popen([str(GATEWAY_EXE), "--port", str(port)], env=env, stdout=STATE["gw_log"],
                          stderr=subprocess.STDOUT, cwd=str(GATEWAY_EXE.parent), start_new_session=True)
    STATE["gw_proc"] = gw
    wait_for("the throwaway Gateway wrote no gateway.db", lambda: (GW_ROOT / "gateway.db").exists() or gw.poll() is not None, 120)
    if gw.poll() is not None:
        raise SystemExit("the throwaway Gateway exited; see <rig>/gateway.out.txt")
    STATE["db"] = GW_ROOT / "gateway.db"
    wait_for("the throwaway gateway.db did not get its tables", lambda: {"device_credentials", "tenants", "cron_jobs"}
             <= {r[0] for r in query("SELECT name FROM sqlite_master WHERE type='table'")}, 120)
    enroll()
    wait_for("the throwaway Gateway did not answer", lambda: get("/cron/jobs") is not None, 120)


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


def prepare_director():
    config = DIR_INSTANCE / "config"
    (config / "director").mkdir(parents=True, exist_ok=True)
    # No session is ever started, so the owner's agent settings are copied only when they are found here.
    owner_config = OWNER_INSTANCE / "config" / "config.json"
    real = json.loads(owner_config.read_text(encoding="utf-8-sig")) if owner_config.exists() else {}
    claude = [e for e in real.get("agent", {}).get("entries", []) if e.get("type") == "ClaudeCode"]
    (config / "config.json").write_text(json.dumps({
        "onboarding": {"completed": True},
        "autoUpdate": {"enabled": False},
        "gateway": {"url": STATE["gw"], "token": DIRECTOR_KEY, "streamMode": True},
        "agent": dict(real.get("agent", {}), entries=claude),
    }, indent=2), encoding="utf-8")
    # The machine's one repository list holds the rig's project, so the form's folder choices have one to offer.
    (config / "director" / "repositories.json").write_text(json.dumps(
        [{"Name": "alpha", "Path": str(THERE), "LastUsed": utc(), "FoundByScan": False, "CanRemove": True}], indent=2),
        encoding="utf-8")
    DIR_HOME.mkdir(parents=True, exist_ok=True)


def director_env():
    env = without_cc(os.environ)
    for k in ("ADMIN_SERVICE_TOKEN", "REPORT_SERVICE_TOKEN", "NOTIFY_OWNER_SERVICE_TOKEN", "DEVTHROTTLE_API_KEY"):
        env.pop(k, None)
    env.update(TZ="UTC", CC_DIRECTOR_ROOT=str(DIR_ROOT), CC_AUTOUPDATE="0", CC_GATEWAY_NO_TAILSCALE="1", HOME=str(DIR_HOME),
               DOTNET_ROOT=str(DOTNET_ROOT))
    env["PATH"] = f"{USER_BIN}:/usr/local/bin:/usr/bin:/bin"
    return env


def connected_directors():
    return [d for d in get("/directors") if d.get("directorId") == STATE.get("director_id") and not d.get("stoppedAtUtc")]


def start_director():
    prepare_director()
    STATE["dir_log"] = open(RIG / "director.out.txt", "a", encoding="utf-8")
    director = subprocess.Popen([str(DIRECTOR_EXE)], env=director_env(), stdout=STATE["dir_log"],
                                stderr=subprocess.STDOUT, cwd=str(DIRECTOR_EXE.parent), start_new_session=True)
    STATE["dir_proc"] = director
    reg = wait_for("the test Director wrote no instance registration", lambda: registration(director.pid), 120)
    STATE["director_id"] = reg["DirectorId"]
    joined = wait_for("the test Director did not join the throwaway Gateway", connected_directors, 120, 2)
    global MACHINE
    MACHINE = joined[0]["machineName"]
    # The tunnel is up once the folder choices say the machine is online.
    wait_for("the throwaway Gateway does not see the test Director's tunnel",
             lambda: get(f"/cron/folder-choices?machine={urllib.parse.quote(MACHINE)}")["online"], 120, 2)
    return director


def stop_director():
    """The Unix arm of the named signal: a request file the Director polls. Never a kill."""
    proc, director_id = STATE["dir_proc"], STATE["director_id"]
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
    STATE["dir_proc"] = None
    return gone


def director_log(title, contains):
    found = []
    for log in sorted((DIR_INSTANCE / "logs" / "director").glob("*.log")):
        for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
            if any(c in line for c in contains):
                found.append(line)
    record(f"{title} - the test Director's log, lines naming {list(contains)}", "\n".join(f"  {x}" for x in found[-20:]) or "  (none)")
    return found


def gateway_log(title, contains):
    found = []
    for log in sorted((GW_ROOT / "logs").rglob("*.log")):
        for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
            if any(c in line for c in contains):
                found.append(line)
    record(f"{title} - the throwaway Gateway's log, lines naming {list(contains)}", "\n".join(f"  {x}" for x in found[-20:]) or "  (none)")
    return found


def user_bin_links():
    return sorted(f"{p.name} -> {os.readlink(p)}" for p in USER_BIN.iterdir() if p.is_symlink()) if USER_BIN.exists() else []


# ===================================================================================================== the webhook


HOOK = []


class Hook(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        # The Gateway sends the body chunked (PostAsJsonAsync), with no Content-Length.
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            parts = []
            while True:
                size = int(self.rfile.readline().strip().split(b";")[0], 16)
                if size == 0:
                    self.rfile.readline()
                    break
                parts.append(self.rfile.read(size))
                self.rfile.readline()
            body = b"".join(parts).decode("utf-8")
        else:
            body = self.rfile.read(int(self.headers.get("Content-Length") or 0)).decode("utf-8")
        HOOK.append((utc(), json.loads(body)))
        self.send_response(200)
        self.end_headers()

    def log_message(self, *args):
        pass


def start_hook():
    port = free_port()
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), Hook)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    STATE["hook"] = server
    STATE["hook_url"] = f"http://127.0.0.1:{port}/hook"


def notices(job_id):
    return [n for _, n in HOOK if n.get("jobId") == job_id]


def show_notices(title, job_id):
    record(title, "\n".join(f"  {at}: {json.dumps(n)}" for at, n in HOOK if n.get("jobId") == job_id) or "  (none)")


def every_minute_args(name, folder, *extra):
    return ["schedule", "create", "--name", name, "--machine", MACHINE, "--repo", str(folder),
            "--cron", "* * * * *", "--tz", "UTC", "--seed", "/help", *extra]


def away_from_the_minute():
    """Wait until the next fire is at least 15 seconds away, so a folder is taken away before a run can start in it."""
    while datetime.now(timezone.utc).second > 40:
        time.sleep(1)


def restart_gateway_before_the_change():
    """Stop the Gateway, put its database back as it was before this change - the new column and table gone, the
    migration not applied, the schedule on the old default 'none' - and start it again on the same port, so the
    migration runs exactly as it will on a Gateway that is updated."""
    STATE["gw_proc"].send_signal(signal.SIGINT)
    STATE["gw_proc"].wait(timeout=60)
    c = sqlite3.connect(STATE["db"], timeout=30)
    migration = c.execute("SELECT MigrationId FROM __EFMigrationsHistory WHERE MigrationId LIKE '%_AddScheduleFailureNotices'").fetchone()[0]
    c.execute("UPDATE cron_jobs SET NotifyOn = 'none' WHERE Id = ?", (STATE["quiet_id"],))
    c.execute("DROP TABLE cron_failure_notices")
    c.execute("ALTER TABLE cron_jobs DROP COLUMN NotifyDefaultedUtc")
    c.execute("DELETE FROM __EFMigrationsHistory WHERE MigrationId = ?", (migration,))
    c.commit()
    before = c.execute("SELECT Id, NotifyOn FROM cron_jobs ORDER BY Id").fetchall()
    c.close()
    record("the database put back as it was before this change", f"  migration removed: {migration}\n  cron_jobs (Id, NotifyOn): {before}")
    port = int(STATE["gw"].rsplit(":", 1)[1])
    env = {"PATH": "/usr/local/bin:/usr/bin:/bin", "HOME": str(GW_HOME), "LANG": "C.UTF-8",
           "DOTNET_ROOT": str(DOTNET_ROOT), "DOTNET_USE_POLLING_FILE_WATCHER": "1", "CC_DIRECTOR_ROOT": str(GW_ROOT),
           "CC_GATEWAY_NO_TAILSCALE": "1", "TZ": "UTC"}
    STATE["gw_proc"] = subprocess.Popen([str(GATEWAY_EXE), "--port", str(port)], env=env, stdout=STATE["gw_log"],
                                        stderr=subprocess.STDOUT, cwd=str(GATEWAY_EXE.parent), start_new_session=True)
    wait_for("the restarted Gateway did not answer", lambda: get("/cron/jobs") is not None, 120)
    return migration


# ===================================================================================================== the proof


def prove():
    plain = (f"The folder '{SITE}' does not exist on {MACHINE} ({A_COMPUTER}). "
             f"Choose a folder that exists on {MACHINE}. Then edit the schedule.")
    raw_reason = f"it did not start a session: director returned BadRequest: repoPath does not exist: {SITE}"

    section("1. A new schedule notifies its owner on failure without any setting changed", f"machine: {MACHINE}")
    away_from_the_minute()
    r = cli(every_minute_args("morning report", SITE, "--notify-webhook", STATE["hook_url"]))
    check(r.returncode == 0 and "Created schedule." in r.stdout, "schedule create saves a schedule whose folder is there")
    # Taken away at once: every run of this schedule from now on fails because its folder is missing.
    SITE.rmdir()
    job_id = next(j["id"] for j in get("/cron/jobs?include=random,window")["jobs"] if j["name"] == "morning report")
    STATE["job_id"] = job_id
    status, job = raw("GET", f"/cron/jobs/{job_id}")
    check(job["notifyOn"] == "failure", "the schedule was created with no --notify-on and notifies on failure")
    check(job.get("notifyNote") is None, "a schedule made after the change carries no note about the change of default")
    r = cli(["schedule", "get", job_id])
    check("  Notify:     When a run fails + webhook " in r.stdout, "schedule get says, in the Gateway's words, that it notifies on failure")

    section("2. Run now: the run reads as one plain sentence, the Director's words only as the detail", "")
    r = cli(["schedule", "run", job_id])
    check(f"  Ending:  did not start a session: {plain}" in r.stdout.splitlines(),
          "schedule run prints the plain sentence as how the run ended")
    check(f"  Detail:  {raw_reason}" in r.stdout.splitlines(), "schedule run keeps the Director's own words, as the detail")
    check("BadRequest" not in r.stdout.split("  Detail:")[0], "'BadRequest' is not in anything schedule run prints above the detail")

    section("3. The owner is told, once, on the schedule's webhook, in the same plain sentence", "")
    first = wait_for("no notice reached the webhook", lambda: notices(job_id), 120, 2)
    show_notices("the notices the webhook received", job_id)
    n = first[0]
    check(n["reason"] == plain, "the notice's reason is the plain sentence")
    check(n["detail"] == raw_reason, "the notice carries the Director's own words as its detail")
    check(n["cause"] == "folder-missing" and n["succeeded"] is False and n["jobName"] == "morning report",
          "the notice names the schedule, says it failed, and gives the cause")
    gateway_log("the Gateway's notices", ["ScheduleFailureNotifier", "ScheduleFailurePushNotifier"])

    section("4. A schedule firing every minute keeps failing: still one notice inside the hour", "")
    until = time.time() + 240
    while time.time() < until:
        time.sleep(10)
    status, runs = raw("GET", f"/cron/jobs/{job_id}/runs")
    failed = [x for x in runs["runs"] if x.get("failureText")]
    check(len(failed) >= 4, f"the schedule failed {len(failed)} times by now, on its own every-minute fires and the Run now")
    check(all(x["failureText"] == plain for x in failed), "every failed run carries the same plain sentence")
    check(all("BadRequest" not in x["failureText"] and "BadRequest" not in x["endingText"] for x in failed),
          "no failed run's sentence or ending holds 'BadRequest'")
    check(len(notices(job_id)) == 1, f"the webhook received {len(notices(job_id))} notice(s) for those failures: one")
    held = gateway_log("the failures held back inside the hour", ["held back, a notice of this cause went out inside the hour"])
    check(len(held) >= 3, "the Gateway says it held back the later failures of the same cause")
    r = cli(["schedule", "runs", job_id])
    check("What went wrong" in r.stdout and "does not exist on" in r.stdout and "BadRequest" not in r.stdout,
          "schedule runs shows what went wrong in plain words, and not 'BadRequest'")
    r = cli(["schedule", "get", job_id])
    check(f"  Last run:   Last run failed: {plain}" in r.stdout.splitlines(), "schedule get prints the last run's state in words")
    status, listed = raw("GET", "/cron/jobs?include=random,window")
    row = next(j for j in listed["jobs"] if j["id"] == job_id)
    check(row["lastRunText"] == f"Last run failed: {plain}" and row["lastRunTone"] == "err",
          "the schedule's row in the list carries the last run's state in words, for the Cockpit")

    section("5. A schedule whose owner turned notifications off is not told", "")
    QUIET.mkdir()
    r = cli(create_args("quiet one", QUIET) + ["--notify-on", "none", "--notify-webhook", STATE["hook_url"]])
    quiet_id = next(j["id"] for j in get("/cron/jobs?include=random,window")["jobs"] if j["name"] == "quiet one")
    STATE["quiet_id"] = quiet_id
    QUIET.rmdir()
    r = cli(["schedule", "run", quiet_id])
    check("does not exist on" in r.stdout, "its run failed for the same cause")
    time.sleep(70)
    check(notices(quiet_id) == [], "no notice was sent for a schedule whose Notify is off")

    section("6. A second cause of the same schedule is told: its machine goes offline", "")
    stop_director()
    wait_for("the throwaway Gateway still sees the test Director online",
             lambda: not get(f"/cron/folder-choices?machine={urllib.parse.quote(MACHINE)}")["online"], 120, 2)
    second = wait_for("no notice of the offline machine reached the webhook",
                      lambda: [x for x in notices(job_id) if x["cause"] != "folder-missing"], 180, 2)
    show_notices("the notices the webhook received", job_id)
    check(second[0]["cause"] == "machine-offline", "the second notice is for the machine being offline")
    check(second[0]["reason"].startswith(f"{MACHINE} was not reachable: DevThrottle was not running there"),
          "it says so in a plain sentence naming the machine")
    check("launcher" not in second[0]["reason"] and "launcher" in (second[0]["detail"] or ""),
          "the resolver's own words ('the launcher could not start one') are only the detail")
    time.sleep(75)
    check(len([x for x in notices(job_id) if x["cause"] == "machine-offline"]) == 1,
          "the offline machine's later failures inside the hour were held back: one notice for that cause")
    gateway_log("the email leg on a self-hosted Gateway", ["email not sent", "emailed the account owner"])
    status, _ = raw("PUT", f"/cron/jobs/{job_id}", dict(get(f"/cron/jobs/{job_id}"), enabled=False))
    check(status == 200, "the every-minute schedule is switched off")

    section("7. A schedule still on the old default is moved to failure when the Gateway is updated", "")
    migration = restart_gateway_before_the_change()
    gateway_log("the migration", ["AddScheduleFailureNotices", "pending migration"])
    status, quiet = raw("GET", f"/cron/jobs/{quiet_id}")
    check(quiet["notifyOn"] == "failure", f"after {migration}, a schedule that was on 'none' notifies on failure")
    check(quiet["notifyNote"] is not None and quiet["notifyNote"].startswith("Failure notifications were switched on for this schedule on ")
          and quiet["notifyNote"].endswith("when they became the default. To stop them, set Notify to Off."),
          "and says so, with how to turn it off")
    r = cli(["schedule", "get", quiet_id])
    check(any(line.startswith("  Note:       Failure notifications were switched on") for line in r.stdout.splitlines()),
          "schedule get prints the note")


def main():
    for exe in (GATEWAY_EXE, DIRECTOR_EXE):
        if not exe.exists():
            raise SystemExit(f"build it first: {exe}")
    if RIG.exists():
        shutil.rmtree(RIG)
    THERE.mkdir(parents=True)
    SITE.mkdir(parents=True)
    before = user_bin_links()
    signal.signal(signal.SIGTERM, lambda *_: (_ for _ in ()).throw(SystemExit("stopped by SIGTERM")))
    started = utc()
    try:
        start_hook()
        start_gateway()
        start_director()
        prove()
    except BaseException as ex:
        # A run that stopped part way is a failed run, and the transcript says where and why.
        FAILURES.append(f"the run stopped before its last check: {type(ex).__name__}: {ex}")
        TRANSCRIPT.append(f"\nSTOPPED: {shown(type(ex).__name__)}: {shown(ex)}\n")
        raise
    finally:
        if STATE.get("dir_proc") is not None:
            stop_director()
        if STATE.get("gw_proc") is not None:
            STATE["gw_proc"].send_signal(signal.SIGINT)
            try:
                STATE["gw_proc"].wait(timeout=60)
            except subprocess.TimeoutExpired:
                STATE["gw_proc"].kill()
        after = user_bin_links()
        check(before == after, "~/.local/bin still points at the owner's tools")
        header = (f"Issue #4436 proof - a throwaway Gateway and a test Director built from this tree, on {MACHINE} ({OS_NAME})\n"
                  f"started {started}, finished {utc()}\n"
                  f"commit {subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=REPO, capture_output=True, text=True).stdout.strip()}"
                  + (" (with uncommitted changes)" if subprocess.run(["git", "status", "--porcelain"], cwd=REPO,
                     capture_output=True, text=True).stdout.strip() else " (a clean tree)") + "\n"
                  f"result: {'PASS' if not FAILURES else 'FAIL: ' + '; '.join(FAILURES)}\n")
        (HERE / ("transcript-mac.txt" if ON_MAC else "transcript.txt")).write_text(header + "".join(TRANSCRIPT), encoding="utf-8")
        print(f"result: {'PASS' if not FAILURES else 'FAIL'}", flush=True)
    sys.exit(0 if not FAILURES else 1)


if __name__ == "__main__":
    main()
