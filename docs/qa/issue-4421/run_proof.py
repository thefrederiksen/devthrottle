"""Issue #4421 proof: a schedule's folder is checked on its machine when it is saved - on a throwaway Gateway and a test
Director built from this tree, talking over the real tunnel.

Runs on devlinux or on a Mac, in the foreground, owns everything it starts and stops everything it started, pass or fail:

- A GATEWAY built from this tree (src/CcDirector.Gateway, Debug) on a free port of 127.0.0.1, with its own empty storage
  root under the rig folder, Tailscale off and authentication on. One account (`local`), one person key and one
  Director key, made for this run.
- A TEST DIRECTOR built from this tree, slot 5 (local_builds/linux/cc-director-linux5, made by
  scripts/local-build-linux.sh --slot 5 --framework-dependent; on a Mac local_builds/mac/cc-director-mac5, made by
  scripts/local-build-mac.sh --slot 5), with a scratch CC_DIRECTOR_ROOT and a scratch HOME under
  the rig folder, every CC_ variable of this shell removed, and auto-update off. It is stopped only by its own lifecycle
  request file, never killed. Afterwards the rig checks that ~/.local/bin still points at the owner's tools.
- THIS TREE's cc-devthrottle, against the throwaway Gateway only.

No session is started: the only run the rig asks for is one whose folder is missing, which the Director refuses before
any agent starts.

Usage, from the repository root, on devlinux:
    export PATH=$HOME/.dotnet:$HOME/.local/bin:$PATH
    dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj -p:EnableWindowsTargeting=true
    bash scripts/local-build-linux.sh --slot 5 --framework-dependent
    python3 docs/qa/issue-4421/run_proof.py
on a Mac:
    dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj -p:EnableWindowsTargeting=true
    bash scripts/local-build-mac.sh --slot 5
    python3 docs/qa/issue-4421/run_proof.py
It writes docs/qa/issue-4421/transcript.txt on Linux and docs/qa/issue-4421/transcript-mac.txt on a Mac.
"""

import hashlib
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

RIG = Path(tempfile.gettempdir()) / "issue-4421-rig"
GW_ROOT = RIG / "gateway-root"
GW_HOME = RIG / "gateway-home"
DIR_ROOT = RIG / "director-root"
DIR_HOME = RIG / "home"
DIR_INSTANCE = DIR_ROOT / "instances" / "default"
PROJECTS = RIG / "projects"
THERE = PROJECTS / "alpha"            # a folder the machine has, registered as one of its repositories
NOTES = PROJECTS / "notes.txt"        # a file, not a folder
MISSING = PROJECTS / "elsewhere"      # a folder the machine does not have
GONE = PROJECTS / "gone"              # saved while the machine is offline; never made

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


# ===================================================================================================== the proof


def prove():
    section("1. The machine, as the Gateway knows it", f"machine: {MACHINE}")
    status, choices = raw("GET", f"/cron/folder-choices?machine={urllib.parse.quote(MACHINE)}")
    check(status == 200 and choices["os"] == OS_ID and choices["osName"] == OS_NAME,
          f"the folder choices name the machine's system, {OS_NAME}, as its Director reported it")
    check(choices["online"] is True, "the folder choices say the machine is online")
    check(choices["placeholder"] == EXAMPLE + "my-project", f"the placeholder is an example in the {OS_NAME} shape")

    section(f"2. A Windows path for {A_COMPUTER} is refused in plain words", "")
    r = cli(create_args("windows path", r"C:\repos\project"))
    check(r.returncode != 0 and f"'C:\\repos\\project' is a Windows path, but {MACHINE} is {A_COMPUTER}. "
          f"Use a path like {EXAMPLE}project." in r.stdout + r.stderr,
          f"schedule create refuses a Windows path for {A_COMPUTER}, naming the machine and its system")
    r = cli(create_args("the customer's path", r"\\documents\\devthrottle"))
    check(r.returncode != 0 and f"is a Windows path, but {MACHINE} is {A_COMPUTER}" in r.stdout + r.stderr
          and f"{EXAMPLE}devthrottle" in r.stdout + r.stderr,
          "the path from the issue is refused the same way, with an example that keeps its folder name")

    section("3. A folder of the right shape that the machine does not have is refused", "")
    r = cli(create_args("missing folder", MISSING))
    check(r.returncode != 0 and f"The folder '{MISSING}' does not exist on {MACHINE} ({A_COMPUTER}). "
          f"Choose a folder that exists on {MACHINE}." in r.stdout + r.stderr,
          "schedule create refuses a missing folder, naming the machine and its system - answered by the real Director")
    status, answer = raw("POST", "/cron/jobs", {
        "name": "a file", "scheduleKind": "recurring", "cronExpression": "0 3 1 1 *", "timeZoneId": "UTC",
        "target": {"machine": MACHINE}, "action": {"repoPath": str(NOTES), "seed": "/help"}})
    check(status == 400 and answer["error"] == f"'{NOTES}' on {MACHINE} is a file, not a folder. Choose the folder the session should open in.",
          "a path that is a file is refused")
    check(get("/cron/jobs") == {"jobs": []} or get("/cron/jobs").get("jobs") == [], "nothing refused was stored")

    section("4. A folder the machine has is saved, with no warning", "")
    r = cli(create_args("folder that is there", THERE))
    check(r.returncode == 0 and "Created schedule." in r.stdout and "WARNING" not in r.stdout,
          "schedule create saves a folder the machine has, with no folder warning")
    director_log("the Director answered the folder checks", ["FolderCheckExecutor"])
    gateway_log("the Gateway's folder checks", ["ScheduleFolderCheck"])

    section("5. The Schedule form's repository list for the machine", "")
    known = wait_for("the Gateway did not learn the test Director's repositories",
                     lambda: get(f"/cron/folder-choices?machine={urllib.parse.quote(MACHINE)}")["repositories"], 60, 2)
    status, choices = raw("GET", f"/cron/folder-choices?machine={urllib.parse.quote(MACHINE)}")
    check(any(repo["path"] == str(THERE) for repo in known), "the folder choices offer the machine's known repository")

    section("6. The machine goes offline", "")
    stop_director()
    wait_for("the throwaway Gateway still sees the test Director online",
             lambda: not get(f"/cron/folder-choices?machine={urllib.parse.quote(MACHINE)}")["online"], 120, 2)
    status, choices = raw("GET", f"/cron/folder-choices?machine={urllib.parse.quote(MACHINE)}")
    check(choices["note"] == f"{MACHINE} is offline, so the folder cannot be checked when you save. It is checked when the schedule first runs.",
          "the folder choices say the machine is offline and when the folder is checked instead")
    r = cli(create_args("saved while offline", GONE))
    check(r.returncode == 0 and f"WARNING:   {MACHINE} is offline, so the folder '{GONE}' could not be checked. "
          "It is checked again when the schedule first runs." in r.stdout,
          "an offline machine's schedule is saved, with a warning that its folder was not checked")
    offline_id = next(j["id"] for j in get("/cron/jobs?include=random,window")["jobs"] if j["name"] == "saved while offline")
    STATE["offline_id"] = offline_id
    r = cli(create_args("windows path while offline", r"C:\repos\project"))
    check(r.returncode != 0 and "is a Windows path, but" in r.stdout + r.stderr,
          f"a Windows path for {A_COMPUTER} is refused even while it is offline, from the system it last reported")

    section("7. The schedule saved offline first runs: its missing folder reads plainly", "")
    start_director()
    r = cli(["schedule", "run", offline_id])
    plain = (f"did not start a session: The folder '{GONE}' does not exist on {MACHINE} ({A_COMPUTER}). "
             f"Choose a folder that exists on {MACHINE}. Then edit the schedule.")
    check(f"  Ending:  {plain}" in r.stdout, "schedule run prints the plain ending, not the Director's BadRequest")
    r = cli(["schedule", "runs", offline_id])
    check("does not exist on" in r.stdout and "BadRequest" not in r.stdout,
          "schedule runs shows what was wrong and how to fix it, and not 'BadRequest'")
    status, answer = raw("GET", f"/cron/jobs/{offline_id}/runs")
    check(answer["runs"][0]["endingText"] == plain, "the run table's ending text is the plain sentence the Cockpit shows")

    section("8. Switching off a schedule whose folder is missing always lands", "")
    r = cli(["schedule", "disable", offline_id])
    check(r.returncode == 0, "schedule disable switches off a schedule whose folder is missing")
    check(get(f"/cron/jobs/{offline_id}")["enabled"] is False, "the schedule is switched off")
    r = cli(["schedule", "enable", offline_id])
    check(r.returncode != 0 and "does not exist on" in r.stdout + r.stderr,
          "switching it back on is refused while its folder is missing")


def main():
    for exe in (GATEWAY_EXE, DIRECTOR_EXE):
        if not exe.exists():
            raise SystemExit(f"build it first: {exe}")
    if RIG.exists():
        shutil.rmtree(RIG)
    THERE.mkdir(parents=True)
    NOTES.write_text("a file, not a folder\n", encoding="utf-8")
    before = user_bin_links()
    signal.signal(signal.SIGTERM, lambda *_: (_ for _ in ()).throw(SystemExit("stopped by SIGTERM")))
    started = utc()
    try:
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
        header = (f"Issue #4421 proof - a throwaway Gateway and a test Director built from this tree, on {MACHINE} ({OS_NAME})\n"
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
