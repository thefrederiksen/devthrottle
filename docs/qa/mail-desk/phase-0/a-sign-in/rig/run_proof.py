# /// script
# requires-python = ">=3.10"
# dependencies = []
# ///
"""Mail Desk phase 0, proof (a): the sign-in premise, on a throwaway Gateway and devlinux's real (installed) Director.

Runs in the foreground, owns everything it starts, and stops everything it started, pass or fail:
- a Gateway built from this tree (src/CcDirector.Gateway, Debug), on a free port of 127.0.0.1, with its own empty
  storage root under the rig folder, Tailscale off, authentication on and factory agents switched on in that root's
  OWN config.json. A person (a browser device row) and the Director (a workstation device row) each get their own key.
  It holds no account sign-in and talks to nothing outside this machine.
- the INSTALLED Director (~/.local/share/cc-director/app/cc-director, the build the owner's Director runs), started
  as a separate process with a scratch CC_DIRECTOR_ROOT and a scratch HOME, pointed at the throwaway Gateway with the
  workstation key, auto-update off. Every CC_ variable of this shell is removed from its environment first, so it
  cannot reach the hosted Gateway. It is stopped only by its own lifecycle request file, never killed.
- the definition in ../definition: one seat whose start runs checks/sign_in_probe.py every two minutes. It is saved
  and published with cc-devthrottle as the person, and the rig waits for the check to run three times.

The rig never touches the real mailbox itself; only the check does, with GET requests. It never prints a token or
the cache's contents: of the saved sign-in it records only the file's time, size and mode, and a hash prefix.

Usage, from the repository root, after building the Gateway:
    export PATH=$HOME/.local/bin:$HOME/.local/share/node/bin:$HOME/.dotnet:$PATH
    dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
    uv run --script docs/qa/mail-desk/phase-0/a-sign-in/rig/run_proof.py docs/qa/mail-desk/phase-0/a-sign-in \
        [--runs 3] [--live-path]
--live-path gives the Director the PATH the owner's live Director was started with (no ~/.local/bin), to see whether
it still finds uv; without it the Director's PATH starts with ~/.local/bin, where uv is.
"""

import hashlib
import json
import os
import pwd
import re
import shutil
import signal
import socket
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[6]
HERE = Path(__file__).resolve().parent
DEFINITION = HERE.parent / "definition"
GATEWAY_EXE = REPO / "src" / "CcDirector.Gateway" / "bin" / "Debug" / "net10.0" / "CcDirector.Gateway"
USER_HOME = Path(pwd.getpwuid(os.getuid()).pw_dir)
DIRECTOR_EXE = USER_HOME / ".local" / "share" / "cc-director" / "app" / "cc-director"
OWNER_INSTANCE = USER_HOME / ".local" / "share" / "cc-director" / "instances" / "default"
USER_BIN = USER_HOME / ".local" / "bin"
CC_DEVTHROTTLE = USER_BIN / "cc-devthrottle"
CC_OUTLOOK = USER_BIN / "cc-outlook"
MAILBOX = "email@devthrottle.com"
CACHE = OWNER_INSTANCE / "config" / "outlook" / "tokens" / "email_devthrottle_com_msal.json"
FACTORY = "qa-mail-sign-in"
SEAT = "signin"
MACHINE = socket.gethostname()
# The PATH the owner's live Director process was started with on devlinux (read from /proc/<pid>/environ, 2026-10-18).
LIVE_DIRECTOR_PATH = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/usr/games:/usr/local/games:/snap/bin"

RIG = Path(tempfile.gettempdir()) / "mail-desk-p0-a-rig"
GW_ROOT = RIG / "gateway-root"
DIR_ROOT = RIG / "director-root"
DIR_HOME = RIG / "home"
DIR_INSTANCE = DIR_ROOT / "instances" / "default"

PERSON_KEY = "qa-person-" + uuid.uuid4().hex
DIRECTOR_KEY = "qa-director-" + uuid.uuid4().hex

TRANSCRIPT = []
STATE = {}


# ============================================================================================= the record


def shown(text):
    """What the transcript may carry: the rig, the tree and the home as <rig>, <repo>, <home>; no key; no address but
    the mailbox's own."""
    text = str(text)
    for real, alias in ((RIG, "<rig>"), (REPO, "<repo>"), (USER_HOME, "<home>")):
        text = text.replace(str(real), alias)
    for key in (PERSON_KEY, DIRECTOR_KEY):
        text = text.replace(key, "<key>")
    text = re.sub(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}",
                  lambda m: m.group(0) if m.group(0).lower() == MAILBOX else "<other address>", text)
    text = re.sub(r"(?i)soren", "<user>", text)
    text = re.sub(r"eyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", "<token>", text)
    return text.encode("ascii", "replace").decode("ascii")


def note(text):
    TRANSCRIPT.append(f"# {shown(text)}\n")
    print(f"== {shown(text)}", flush=True)


def record(title, body):
    TRANSCRIPT.append(f"# {shown(title)}\n{shown(body).rstrip()}\n")
    print(f"-- {shown(title)}", flush=True)


def utc():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


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


# ============================================================================================= the saved sign-in


def cache_facts(title):
    """Time, size, mode and a hash prefix of the saved sign-in - never its contents."""
    if not CACHE.exists():
        record(title, f"  {CACHE}: does not exist")
        return None
    st = CACHE.stat()
    facts = {"mtimeNs": st.st_mtime_ns, "mtimeUtc": datetime.fromtimestamp(st.st_mtime, timezone.utc).isoformat(),
             "bytes": st.st_size, "mode": oct(st.st_mode & 0o777),
             "sha256Prefix": hashlib.sha256(CACHE.read_bytes()).hexdigest()[:12]}
    record(title, f"  {CACHE}\n" + "\n".join(f"  {k}: {v}" for k, v in facts.items()))
    return facts


def outlook_accounts(title):
    env = dict(os.environ, NO_COLOR="1", COLUMNS="200")
    r = subprocess.run([str(CC_OUTLOOK), "accounts", "list"], env=env, capture_output=True, text=True, timeout=120)
    # Only the table's frame, its header and the mailbox's own row: no other account is recorded.
    kept = [line for line in r.stdout.splitlines() if "|" not in line or "Account" in line or "devthrottle" in line]
    record(f"{title}: cc-outlook accounts list (exit {r.returncode}; other accounts' rows left out)",
           "\n".join(kept) + r.stderr)
    row = next((line for line in r.stdout.splitlines() if "devthrottle" in line and "|" in line), "")
    cells = [c.strip() for c in row.split("|")]
    return "Yes" in cells


def user_bin_links(title):
    lines = []
    for p in sorted(USER_BIN.iterdir()):
        if p.is_symlink():
            lines.append(f"{p.name} -> {os.readlink(p)}")
    record(title, "\n".join(f"  {x}" for x in lines))
    return lines


# ============================================================================================= the Gateway


def sha(key):
    return hashlib.sha256(key.encode("utf-8")).hexdigest()


def query(db, sql, args=()):
    c = sqlite3.connect(db)
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


def enroll(db):
    """A person (a browser device) and the Director (a workstation device), each with its own key."""
    c = sqlite3.connect(db)
    now = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
    for device_id, key, device_type in (("qa-browser", PERSON_KEY, "browser"),
                                        ("qa-director", DIRECTOR_KEY, "workstation")):
        insert(c, "device_credentials", {
            "DeviceId": device_id, "MachineName": MACHINE, "DeviceKeyHash": sha(key), "KeyPrefix": key[:8],
            "KeyLast4": key[-4:], "IssuedAtUtc": now, "Status": "active", "Platform": "linux",
            "DeviceType": device_type, "TenantId": "local"})
    c.commit()
    c.close()


def get(path, key=None):
    req = urllib.request.Request(STATE["gw"] + path, headers={"Authorization": f"Bearer {key or PERSON_KEY}"})
    with urllib.request.urlopen(req, timeout=15) as res:
        return json.loads(res.read().decode("utf-8"))


def cli(args, expect_exit=0):
    """The installed cc-devthrottle, as the rig's person, against the throwaway Gateway only."""
    gw = STATE["gw"]
    if not gw.startswith("http://127.0.0.1:"):
        raise SystemExit(f"refusing to run cc-devthrottle against {gw}: only the throwaway Gateway")
    env = dict(without_cc(os.environ), CC_GATEWAY_URL=gw, CC_GATEWAY_SESSION_KEY=PERSON_KEY, NO_COLOR="1",
               COLUMNS="200")
    r = subprocess.run([str(CC_DEVTHROTTLE), *args], env=env, capture_output=True, text=True, timeout=300)
    line = " ".join(a if " " not in a else f'"{a}"' for a in args)
    TRANSCRIPT.append(f"$ cc-devthrottle {shown(line)}      (as the rig's person)\n{shown(r.stdout)}{shown(r.stderr)}"
                      f"[exit {r.returncode}]\n")
    print(f"cli {shown(line)}: exit {r.returncode}", flush=True)
    if expect_exit is not None and r.returncode != expect_exit:
        raise SystemExit(f"expected exit {expect_exit} from: cc-devthrottle {line}\n{r.stdout}{r.stderr}")
    return r


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


# ============================================================================================= the Director


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
    real = json.loads((OWNER_INSTANCE / "config" / "config.json").read_text(encoding="utf-8-sig"))
    claude = [e for e in real.get("agent", {}).get("entries", []) if e.get("type") == "ClaudeCode"]
    (config / "config.json").write_text(json.dumps({
        "onboarding": {"completed": True},
        "autoUpdate": {"enabled": False},
        "gateway": {"url": STATE["gw"], "token": DIRECTOR_KEY, "streamMode": True},
        "agent": dict(real.get("agent", {}), entries=claude),
    }, indent=2), encoding="utf-8")
    DIR_HOME.mkdir(parents=True, exist_ok=True)


def director_env(live_path):
    env = without_cc(os.environ)
    env.update(CC_DIRECTOR_ROOT=str(DIR_ROOT), CC_AUTOUPDATE="0", CC_GATEWAY_NO_TAILSCALE="1", HOME=str(DIR_HOME))
    env["PATH"] = LIVE_DIRECTOR_PATH if live_path else f"{USER_BIN}:{LIVE_DIRECTOR_PATH}"
    return env


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


def alive(pid):
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    stat = Path(f"/proc/{pid}/stat")
    return stat.exists() and ") Z " not in stat.read_text()


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


def director_log(title, contains):
    found = []
    for log in sorted((DIR_INSTANCE / "logs" / "director").glob("*.log")):
        for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
            if any(c in line for c in contains):
                found.append(line)
    record(f"{title} - the test Director's log, lines naming {contains}", "\n".join(f"  {x}" for x in found) or "  (none)")
    return found


# ============================================================================================= the proof


def trigger_id():
    return next(t["id"] for t in get("/triggers")["triggers"] if t.get("factory") == FACTORY and t.get("seat") == SEAT)


def runs(db, tid):
    return query(db, "SELECT CheckedUtc, Outcome, Count, SessionId, Reason, DirectorId FROM trigger_runs "
                     "WHERE TriggerId = ? COLLATE NOCASE ORDER BY RecordedUtc", (tid,))


def state_rows(db, tid):
    return query(db, "SELECT Sequence, LENGTH(Content), WrittenUtc, WrittenBy, Content FROM factory_check_state "
                     "WHERE OwnerKey = ? COLLATE NOCASE ORDER BY Sequence", (f"trigger:{tid}",))


def publish():
    folder = RIG / "definition"
    shutil.copytree(DEFINITION, folder, ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    factory_yaml = folder / "factory.yaml"
    factory_yaml.write_text(factory_yaml.read_text(encoding="utf-8").replace("machine: devlinux", f"machine: {MACHINE}"),
                            encoding="utf-8")
    record("the definition, as published (../definition; checks/sign_in_probe.py is committed beside this rig)",
           "\n".join(f"  --- {p.relative_to(folder).as_posix()}\n" + "".join(f"    {x}\n" for x in p.read_text().splitlines())
                     for p in sorted(folder.rglob("*.yaml"))))
    r = cli(["factory", "definition", "save", FACTORY, "--dir", str(folder), "--expected-version", "0", "--note",
             "Mail Desk phase 0 proof (a): the sign-in probe", "--json"])
    version = json.loads(r.stdout)["version"]
    cli(["factory", "definition", "publish", FACTORY, "--version", str(version), "--replaces", "0"])
    cli(["trigger", "list"])
    return version


def evidence(db, tid, wanted):
    note(f"waiting for {wanted} recorded checks of the {SEAT} start (every two minutes)")
    got = wait_for(f"the {SEAT} trigger did not reach {wanted} recorded checks",
                   lambda: (lambda r: r if len(r) >= wanted else None)(runs(db, tid)), seconds=wanted * 150 + 120, every=5)
    record("every recorded check of the start's trigger (CheckedUtc, Outcome, Count, SessionId, Reason, DirectorId)",
           "\n".join(f"  {row}" for row in got))
    cli(["trigger", "runs", tid])
    cli(["factory", "check-state", "show", FACTORY, SEAT])
    stored = state_rows(db, tid)
    record("the start's factory_check_state rows on the throwaway Gateway (Sequence, Bytes, WrittenUtc, WrittenBy)",
           "\n".join(f"  {row[:4]}" for row in stored) or "  (none)")
    newest = json.loads(stored[-1][4]) if stored else {"runs": []}
    record("the evidence the check kept in its memory, read back from the throwaway Gateway (newest state row)",
           json.dumps(newest, indent=1))
    return got, newest


def main():
    out = Path(sys.argv[1]).resolve()
    wanted = int(sys.argv[sys.argv.index("--runs") + 1]) if "--runs" in sys.argv else 3
    live_path = "--live-path" in sys.argv
    for needed in (GATEWAY_EXE, DIRECTOR_EXE, CC_DEVTHROTTLE, CC_OUTLOOK):
        if not needed.exists():
            raise SystemExit(f"{needed} does not exist (build the Gateway first)")
    if RIG.exists() and rig_processes():
        raise SystemExit(f"processes still run from {RIG}: {rig_processes()}; wait for them to end")
    if RIG.exists():
        shutil.rmtree(RIG)

    note(f"Mail Desk phase 0 proof (a), {utc()}, tree {subprocess.run(['git', '-C', str(REPO), 'rev-parse', '--short', 'HEAD'], capture_output=True, text=True).stdout.strip()}, "
         f"machine {MACHINE}, Director PATH: {'the live Director PATH (no ~/.local/bin)' if live_path else '~/.local/bin + the live Director PATH'}")
    links_before = user_bin_links("~/.local/bin links BEFORE the run")
    before = cache_facts("the saved sign-in BEFORE the run")
    if before is None:
        raise SystemExit(f"{MAILBOX} is not signed in yet: no {CACHE}")
    authenticated_before = outlook_accounts("BEFORE the run")

    GW_ROOT.mkdir(parents=True)
    (GW_ROOT / "config").mkdir()
    (GW_ROOT / "config" / "config.json").write_text('{ "factoryAgents": { "enabled": true } }\n', encoding="utf-8")
    port = free_port()
    STATE["gw"] = f"http://127.0.0.1:{port}"
    genv = without_cc(os.environ)
    genv.update(CC_DIRECTOR_ROOT=str(GW_ROOT), CC_GATEWAY_NO_TAILSCALE="1", DOTNET_USE_POLLING_FILE_WATCHER="1",
                DOTNET_ROOT=str(USER_HOME / ".dotnet"))  # .NET 10 lives there; /usr/lib/dotnet is 8.0
    for k in ("REPORT_SERVICE_TOKEN", "NOTIFY_OWNER_SERVICE_TOKEN"):
        genv.pop(k, None)
    glog = open(RIG / "gateway.out.txt", "w", encoding="utf-8")
    gw = subprocess.Popen([str(GATEWAY_EXE), "--port", str(port)], env=genv, stdout=glog, stderr=subprocess.STDOUT,
                          cwd=str(GATEWAY_EXE.parent))
    director = None
    director_id = None
    result = {}
    try:
        wait_for("the throwaway Gateway wrote no gateway.db",
                 lambda: bool(list(GW_ROOT.rglob("gateway.db"))) or gw.poll() is not None, 120)
        if gw.poll() is not None:
            raise SystemExit("the throwaway Gateway exited; see <rig>/gateway.out.txt")
        db = list(GW_ROOT.rglob("gateway.db"))[0]
        wait_for("the throwaway gateway.db did not get its tables", lambda: {"device_credentials", "factory_check_state",
                 "trigger_runs"} <= {r[0] for r in query(db, "SELECT name FROM sqlite_master WHERE type='table'")})
        enroll(db)
        wait_for("the throwaway Gateway did not answer", lambda: get("/factory-definition-schemas") is not None, 120)

        prepare_director()
        dlog = open(RIG / "director.out.txt", "w", encoding="utf-8")
        director = subprocess.Popen([str(DIRECTOR_EXE)], env=director_env(live_path), stdout=dlog,
                                    stderr=subprocess.STDOUT, cwd=str(DIRECTOR_EXE.parent))
        reg = wait_for("the test Director wrote no instance registration", lambda: registration(director.pid), 120)
        director_id = reg["DirectorId"]
        wait_for("the test Director did not join the throwaway Gateway",
                 lambda: [d for d in get("/directors") if d.get("directorId") == director_id], 120, 2)
        note(f"Gateway {STATE['gw']} built from this tree, storage root <rig>/gateway-root (empty for this run). The "
             f"installed Director (pid {director.pid}, id {director_id}, version {reg.get('Version', reg.get('version', '?'))}) "
             f"started with CC_DIRECTOR_ROOT=<rig>/director-root and HOME=<rig>/home, and joined it.")
        record("the test Director's instance registration", json.dumps(
            {k: v for k, v in reg.items() if k.lower() not in ("token", "key")}, indent=1))

        publish()
        tid = trigger_id()
        got, newest = evidence(db, tid, wanted)
        director_log("the check runs", ("DefinitionCheckRunner", "FindOnPath"))
        result.update(runs=got, memory=newest)
    finally:
        if director is not None and director.poll() is None:
            try:
                wait_for("the test Director's own tool install did not finish",
                         lambda: not [p for p in rig_processes(but=(director.pid,)) if "pip" in p[1] or "uv " in p[1]],
                         600, 5)
            except SystemExit as exc:
                record("the test Director's tool install was still running; asking it to stop anyway", f"  {exc}")
            if director_id:
                stop_director(director, director_id)
            else:
                record("the test Director never registered, so it cannot be asked to stop; it was NOT killed",
                       f"  pid {director.pid} - stop it by hand")
        if gw.poll() is None:
            gw.send_signal(signal.SIGTERM)  # the one process this rig started on its own root
            try:
                gw.wait(timeout=30)
            except subprocess.TimeoutExpired:
                record("the throwaway Gateway did not stop on SIGTERM within 30 seconds", f"  pid {gw.pid}")
        glog.close()
        record("processes still running from the rig folder after the stop", "\n".join(f"  {p}" for p in rig_processes()) or "  (none)")

        after = cache_facts("the saved sign-in AFTER the run")
        authenticated_after = outlook_accounts("AFTER the run")
        links_after = user_bin_links("~/.local/bin links AFTER the run")
        record("~/.local/bin unchanged by the run", f"  {links_before == links_after}")
        record("summary of the saved sign-in", f"  Authenticated before: {authenticated_before}, after: {authenticated_after}\n"
               f"  modification time changed: {before and after and before['mtimeNs'] != after['mtimeNs']}\n"
               f"  hash prefix changed: {before and after and before['sha256Prefix'] != after['sha256Prefix']}\n"
               f"  mode after: {after and after['mode']}")
        name = "transcript-live-path.txt" if live_path else "transcript.txt"
        (out / name).write_text(
            "\n".join(TRANSCRIPT), encoding="utf-8", newline="\n")
        print(f"wrote the transcript into {out}", flush=True)


if __name__ == "__main__":
    main()
