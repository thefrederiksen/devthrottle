"""QA for Factory Definition, phase 5: the run kit, on a throwaway Gateway and a REAL Director built from this tree.

Runs in the foreground, owns everything it starts, and stops it when the run ends, pass or fail:
- a Gateway (CcDirector.Gateway, Debug build of THIS tree) on port 7996, with its own storage root under %TEMP%
  (CC_DIRECTOR_ROOT), Tailscale off, authentication ON and factory agents switched on in the rig's OWN config.json, as
  phase 4's rig. It can touch neither the owner's Gateway nor any factory.
- a real Director: scripts\\local-build\\cc-director6.exe (SLOT 6 ONLY), launched ONLY through the `cc-director-launch`
  scheduled task (CLAUDE.md rule 0b) and stopped ONLY by its named shutdown signal. The task is pointed at a wrapper
  that sets the Director's own storage root under the rig, and is put back exactly as it was found when the run ends.
  The Director reads the rig Gateway's address and a workstation device key the rig enrolled from its instance home.
- the stand-in Director of the Factory Manager rig, on machine QA-OLD: a Director that does not advertise
  BuildsFactoryKit, so it is the "older Director" of case 6.
- a scratch repository with an origin of its own (a bare repository in the rig), registered on the Director as
  qa-scratch with its pooled-worktree setting OFF, and cc-worktrees' state kept in the rig (CC_WORKTREES_HOME).

Sessions are real Claude Code seats. Every prompt is tiny; what an agent did is read from its own transcript in the
user's Claude Code projects folder, found by the marker its prompt carries.

Usage (from the repository root, after building the Gateway, the slot 6 Director and the stand-in):
    dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
    powershell -NoProfile -File scripts\\local-build-avalonia.ps1 -Slot 6 -OutputDir "<repo>\\scripts\\local-build"
    dotnet build docs/qa/factory-control/factory-manager/rig/standin-director/StandinDirector.csproj
    set QA_HELD_SECRET=<the name of a setting-kind cc-secrets entry this machine holds>
    uv run --no-project --with "typer>=0.16.1" --with "click>=8.2.1" --with "rich>=13.0.0" --with "requests>=2.31.0" \\
        python docs/qa/factory-definition/phase-5/rig/run_qa.py <out dir> [--only section,section]
The held entry's name is given to the rig, not written in it, and the record calls it <held setting>: the report is
public, and the name of an entry says what this machine holds. Only its name is used; its value is never read.
Sections, in order: undefined, publish, refusals, worker, home.
"""

import hashlib
import json
import os
import re
import shutil
import sqlite3
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[5]
HERE = Path(__file__).resolve().parent
EXE = REPO / "src" / "CcDirector.Gateway" / "bin" / "Debug" / "net10.0" / "CcDirector.Gateway.exe"
DIRECTOR_EXE = REPO / "scripts" / "local-build" / "cc-director6.exe"
STANDIN = (REPO / "docs" / "qa" / "factory-control" / "factory-manager" / "rig" / "standin-director" / "bin" / "Debug"
           / "net10.0" / "StandinDirector.exe")
CLI_DIR = REPO / "tools" / "cc-devthrottle"
TASK = "cc-director-launch"
PORT = 7996
GW = f"http://127.0.0.1:{PORT}"
MACHINE = os.environ["COMPUTERNAME"]
OLD_MACHINE = "QA-OLD"
STANDIN_ID = "qa-standin-director"
FACTORY = "qa-kit"
UNDEFINED = "qa-plain"
HELD_SECRET = os.environ.get("QA_HELD_SECRET", "")   # a SETTING-kind entry that exists here; its value is never read
MISSING_SECRET = "qa-p6-no-such-secret"
SKILL_WORD = "HERON-5520"
NOTES_LINE = "NOTES-LINE-7310: the first line of notes.txt"
MODEL = "claude-sonnet-5-5"

RIG = Path(os.environ["TEMP"]) / "factory-definition-p5-qa"
GW_ROOT = RIG / "gateway-root"
DIR_ROOT = RIG / "director-root"
HOME = DIR_ROOT / "instances" / "default"
SCRATCH = RIG / "repos" / "qa-scratch"
ORIGIN = RIG / "repos" / "qa-scratch-origin.git"
PLAIN = RIG / "repos" / "qa-plain"
WT_HOME = RIG / "cc-worktrees-state"
CREATES = RIG / "standin-creates"
USER_HOME = Path(os.environ["USERPROFILE"])
CLAUDE_PROJECTS = USER_HOME / ".claude" / "projects"
SKILL_FOLDERS = [USER_HOME / ".claude" / "skills", USER_HOME / ".agents" / "skills"]
NOW = datetime.now(timezone.utc).replace(tzinfo=None)

PERSON_KEY = "qa-person-" + uuid.uuid4().hex
DIRECTOR_KEY = "qa-director-" + uuid.uuid4().hex
STANDIN_KEY = "qa-standin-" + uuid.uuid4().hex

TRANSCRIPT = []
STARTED_SESSIONS = []
USED_TRANSCRIPTS = set()


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
    text = re.sub(re.escape(os.environ["USERNAME"]), "<user>", text, flags=re.IGNORECASE)
    if HELD_SECRET:
        text = text.replace(HELD_SECRET, "<held setting>")
    # The owner's own checkout, which the cc-director-launch task points at: only its slot folder is named.
    text = re.sub(r"[A-Za-z]:[\\/][^\s\"'|]*?[\\/]scripts[\\/]local-build",
                  lambda m: "<checkout>\\scripts\\local-build", text)
    # The record is ASCII: a terminal's box-drawing and an agent's typographic quotes are written plainly.
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
                observe_machine()
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
    """A person (a browser device), the real Director and the stand-in (workstation devices), each with its own key."""
    c = sqlite3.connect(db)
    for device_id, key, device_type, machine in (("qa-browser", PERSON_KEY, "browser", MACHINE),
                                                 ("qa-director6", DIRECTOR_KEY, "workstation", MACHINE),
                                                 ("qa-standin", STANDIN_KEY, "workstation", OLD_MACHINE)):
        insert(c, "device_credentials", {
            "DeviceId": device_id, "MachineName": machine, "DeviceKeyHash": sha(key), "KeyPrefix": key[:8],
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
    TRANSCRIPT.append(f"$ {method} {path}      (raw HTTP, as {label})\n" + (f"  body: {shown(body)}\n" if body else "")
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


def history(db, title, session_id):
    """The session's history row: its folder, factory and every factory birth fact the row has a column for."""
    c = sqlite3.connect(db)
    cols = [r[1] for r in c.execute("PRAGMA table_info(session_history)")]
    c.close()
    wanted = [n for n in cols if n in ("SessionId", "SessionName", "RepoPath", "Agent", "Factory")
              or n.startswith("Factory") or n == "Model"]
    return rows(db, title, f"SELECT {', '.join(wanted)} FROM session_history WHERE SessionId = ? COLLATE NOCASE",
                (session_id,))


# ===================================================================================================== the Director


def ps(script, check=True):
    r = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", script], capture_output=True,
                       text=True, encoding="utf-8", errors="replace", timeout=180)
    if check and r.returncode != 0:
        raise SystemExit(f"powershell failed ({r.returncode}): {script}\n{r.stdout}{r.stderr}")
    return r.stdout


def task_action():
    """What the cc-director-launch task runs, as Execute|Arguments|WorkingDirectory."""
    out = ps(f"$t = Get-ScheduledTask -TaskName '{TASK}'; $a = $t.Actions[0]; "
             "Write-Output ($a.Execute + '|' + $a.Arguments + '|' + $a.WorkingDirectory)").strip()
    execute, arguments, working = out.split("|")
    return {"execute": execute, "arguments": arguments, "working": working}


def set_task(execute, arguments, working):
    """Re-register cc-director-launch with one action and its far-future, on-demand-only trigger (rule 0b's shape)."""
    arg = f" -Argument '{arguments}'" if arguments else ""
    ps(f"$a = New-ScheduledTaskAction -Execute '{execute}'{arg} -WorkingDirectory '{working}'; "
       "$t = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(5); "
       f"Register-ScheduledTask -TaskName '{TASK}' -Action $a -Trigger $t -Force | Out-Null")


def director_processes():
    """The ids of every process running exactly this slot's exe."""
    out = ps("Get-Process -Name cc-director6 -ErrorAction SilentlyContinue | "
             f"Where-Object {{ $_.Path -ieq '{DIRECTOR_EXE}' }} | ForEach-Object {{ $_.Id }}", check=False)
    return [int(x) for x in out.split()]


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
    """The Director's own storage root: the rig Gateway and its key, the agent, the repository, this tree's command."""
    config = HOME / "config"
    (config / "director").mkdir(parents=True, exist_ok=True)
    real = json.loads((Path(os.environ["LOCALAPPDATA"]) / "cc-director" / "instances" / "default" / "config"
                       / "config.json").read_text(encoding="utf-8-sig"))
    claude = [e for e in real["agent"]["entries"] if e.get("type") == "ClaudeCode"]
    if not claude:
        raise SystemExit("the owner's Director knows no Claude Code entry to copy the executable path from")
    agent = dict(real["agent"], entries=claude)
    (config / "config.json").write_text(json.dumps({
        "onboarding": {"completed": True},
        "autoUpdate": {"enabled": False},
        "gateway": {"url": GW, "token": token, "streamMode": True},
        "agent": agent,
        "worktreePool": {"repoDefaults": {str(SCRATCH).lower(): {"enabled": False, "poolSize": 4}}},
    }, indent=2), encoding="utf-8")
    (config / "director" / "repositories.json").write_text(json.dumps(
        [{"Name": "qa-scratch", "Path": str(SCRATCH)}, {"Name": "qa-plain", "Path": str(PLAIN)}], indent=2),
        encoding="utf-8")
    # A session's PATH starts with its Director's instance bin, so this tree's cc-devthrottle goes there.
    py = Path(os.environ["LOCALAPPDATA"]) / "cc-director" / "pyenv" / "Scripts" / "python.exe"
    if not py.exists():
        raise SystemExit(f"no python environment at {py} to run this tree's cc-devthrottle with")
    (HOME / "bin").mkdir(parents=True, exist_ok=True)
    (HOME / "bin" / "cc-devthrottle.cmd").write_text(
        f"@echo off\r\nset PYTHONPATH={CLI_DIR};{CLI_DIR.parent}\r\n\"{py}\" -c \"from src.cli import tool_main; "
        f"tool_main()\" %*\r\n", encoding="ascii")
    wrapper = RIG / "start-director6.cmd"
    wrapper.write_text("@echo off\r\n"
                       f"set CC_DIRECTOR_ROOT={DIR_ROOT}\r\n"
                       f"set CC_WORKTREES_HOME={WT_HOME}\r\n"
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
    """A Director on a fresh storage root installs its Python tools with pip in the background (about three minutes).
    A clean stop does not end that install, so the rig waits for it before asking the Director to stop."""
    def pip_done():
        return not [p for p in rig_processes(but=(director,)) if p[1].lower() == "python.exe"]
    wait_for("the slot 6 Director's Python tool install did not finish", pip_done, seconds=600, every=5)


def stop_director(pid):
    """The named shutdown signal, for this Director's own id. Never a kill of anything this run did not start."""
    reg = registration(pid)
    if reg is None:
        raise SystemExit(f"no instance registration names pid {pid}; it cannot be asked to stop")
    signal = f"Local\\cc-director-shutdown-{reg['DirectorId'].lower()}"
    ps(f"$e = [System.Threading.EventWaitHandle]::OpenExisting('{signal}'); $e.Set() | Out-Null; $e.Dispose()")
    wait_for(f"the slot 6 Director (pid {pid}) did not exit after its shutdown signal",
             lambda: pid not in director_processes(), seconds=60)
    print(f"slot 6 Director {pid} stopped by its named signal", flush=True)


# ===================================================================================================== sessions

POINTER = "\u276f"          # the marker Claude Code draws beside the chosen line of a menu
TENDED = {}
TRUST_SEEN = set()    # every session whose screen ever drew Claude Code's folder-trust question
# What the rig saw while sessions ran. A scheduled run closes itself when it is done and its kit goes with it, so the
# kits, the sessions' rows, the agents' command lines and the worktrees' status are read on every turn of the wait.
OBSERVED = {"sessions": {}, "kits": {}, "processes": {}, "git": {}, "lock": {}}
CLAUDE_LOCK = Path.home() / ".claude.json.lock"   # the lock Claude Code takes to write its own configuration
LAST_JOB = []   # the schedule the rig fired last, whose runs are recorded if the wait after it fails


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
    that question. The rig answers it the way a person at the screen would: it moves to "Yes" and presses Enter, and
    records that it did. Nothing else is ever typed into a session."""
    listed = listed_sessions()
    (RIG / "screens").mkdir(exist_ok=True)
    (RIG / "screens" / "sessions.json").write_text(json.dumps(listed, indent=2), encoding="utf-8")
    for s in listed:
        sid = s.get("sessionId") or s.get("id") or s.get("SessionId")
        if not sid:
            continue
        try:
            screen = buffer_of(sid)
        except (urllib.error.URLError, OSError) as exc:
            print(f"could not read the screen of {sid}: {exc}", flush=True)
            continue
        (RIG / "screens" / f"{sid}.txt").write_text(screen, encoding="utf-8")
        observe_session(s)
        # The screen can arrive with its spaces and line breaks drawn as cursor moves, so it is read with every
        # blank removed, and the choice is the word right after the LAST pointer drawn.
        flat = re.sub(r"\s+", "", screen)
        if "Itrustthisfolder" in flat:
            TRUST_SEEN.add(sid)
        if TENDED.get(sid):     # answered once; the old question stays in the screen's history
            continue
        if "Itrustthisfolder" not in flat or "No,exit" not in flat or POINTER not in flat:
            continue
        chosen = flat[flat.rindex(POINTER) + 1:]
        if chosen.startswith("Yes"):
            keys(sid, "\r")
            TENDED[sid] = TENDED.get(sid, 0) + 1
            start = flat.find("Quicksafetycheck")
            record(f"the rig answered Claude Code's folder-trust question in session {sid}",
                   f"  the screen, blanks removed: {flat[start:start + 260]}\n"
                   "  answered: Enter, with the pointer on 'Yes, I trust this folder'")
            time.sleep(2)
        elif chosen.startswith("No"):
            keys(sid, "\u001b[B")
            time.sleep(1)


def observe_session(s):
    """Keep what this session's row says, and the git status of its folder when that is a pooled slot."""
    sid = s.get("sessionId")
    OBSERVED["sessions"][sid] = {k: s.get(k) for k in SESSION_FIELDS}
    folder = s.get("repoPath") or ""
    if s.get("pooledWorktree") and Path(folder).is_dir():
        r = subprocess.run(["git", "status", "--short", "--untracked-files=all"], cwd=folder, capture_output=True,
                           text=True, timeout=60)
        OBSERVED["git"][folder] = r.stdout + r.stderr
        for f in sorted((Path(folder) / ".temp").glob("input_*.txt")):
            head = f.read_text(encoding="utf-8", errors="replace").splitlines()[:6]
            OBSERVED["git"][f"{folder} -> .temp/{f.name}, its first lines"] = "\n".join(head)


def observe_machine():
    """Keep every kit folder the Director holds now and every rig agent's command line."""
    base = HOME / "factory-run"
    for kit in base.iterdir() if base.exists() else []:
        try:
            meta = json.loads((kit / "kit.json").read_text(encoding="utf-8-sig")) if (kit / "kit.json").exists() else {}
            files = sorted(f"{p.relative_to(kit).as_posix()} ({p.stat().st_size} bytes)" for p in kit.rglob("*")
                           if p.is_file())
        except (OSError, json.JSONDecodeError):
            continue
        OBSERVED["kits"][kit.name] = {"kind": meta.get("kind"), "seat": meta.get("seat"),
                                      "version": meta.get("version"), "files": files}
    try:
        age = time.time() - CLAUDE_LOCK.stat().st_mtime
        OBSERVED["lock"][datetime.now(timezone.utc).strftime("%H:%M:%S")] = f"present, last modified {age:.1f}s before"
    except OSError:
        pass
    out = ps("Get-CimInstance Win32_Process -Filter \"Name='claude.exe'\" | ForEach-Object { $_.CommandLine }",
             check=False)
    for line in out.splitlines():
        if str(RIG).lower() in line.lower():
            OBSERVED["processes"].setdefault(line.strip(), datetime.now(timezone.utc).strftime("%H:%M:%S"))
    return OBSERVED["processes"]


def record_observed(title):
    """Record what was seen while the sessions ran, then start a fresh record for the next run."""
    lines = ["the sessions (GET /sessions, the fields that say where and as what each runs):"]
    lines += [f"  {json.dumps(row)}" for row in OBSERVED["sessions"].values()]
    lines.append("the kit folders the Director held (<rig>/director-root/instances/default/factory-run):")
    for name, kit in OBSERVED["kits"].items():
        lines.append(f"  {name}: kit.json kind={kit['kind']}, seat={kit['seat']}, version={kit['version']}")
        lines += [f"      {f}" for f in kit["files"]]
    lines.append("the command lines of the running claude.exe processes that name the rig:")
    lines += [f"  {cmd}" for cmd in OBSERVED["processes"]]
    lines.append("git status --short --untracked-files=all of each pooled slot, read while its session ran:")
    for folder, status in OBSERVED["git"].items():
        lines.append(f"  {folder}:")
        lines += [f"      {x}" for x in status.splitlines()] or ["      (clean)"]
    lines.append("<home>/.claude.json.lock, each time it was seen (it is absent the rest of the time):")
    lines += [f"  {t}: {v}" for t, v in OBSERVED["lock"].items()] or ["  (never seen)"]
    record(f"{title} - seen while the sessions ran", "\n".join(f"  {x}" for x in lines))
    for key in OBSERVED:
        OBSERVED[key] = {}


def transcript_files(since):
    """Transcripts of sessions whose folder is inside the rig. Only those: any other session on this machine - the one
    that wrote this rig, for one - can hold the same marker words."""
    rig_projects = re.sub(r"[^A-Za-z0-9]", "-", str(RIG)).lower()
    return [p for p in CLAUDE_PROJECTS.glob("*/*.jsonl")
            if p.parent.name.lower().startswith(rig_projects) and p.stat().st_mtime >= since]


def find_transcript(marker, since, seconds=300):
    """The Claude Code transcript whose first prompt carries the marker, written after `since`."""
    def found():
        for p in sorted(transcript_files(since), key=lambda p: p.stat().st_mtime, reverse=True):
            try:
                text = p.read_text(encoding="utf-8")
            except OSError:
                continue
            if marker in text and p not in USED_TRANSCRIPTS:
                return p
        return None
    return wait_for(f"no Claude Code transcript carrying '{marker}' appeared", found, seconds=seconds, every=2,
                    tend=True)


def entries(path):
    out = []
    for line in path.read_text(encoding="utf-8").splitlines():
        try:
            out.append(json.loads(line))
        except json.JSONDecodeError:
            pass
    return out


def finished(path):
    """True once the agent's last message ended its turn (and not on a tool call)."""
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
    parts = []
    for c in content or []:
        if isinstance(c, dict) and c.get("type") == "text":
            parts.append(c.get("text", ""))
        elif isinstance(c, str):
            parts.append(c)
    return "\n".join(parts)


def summarize(title, path):
    """What the agent did, from its own transcript: its folder, each tool call and result, and its last words."""
    lines = [f"transcript: <home>/.claude/projects/{path.parent.name}/{path.name}"]
    cwd = next((e.get("cwd") for e in entries(path) if e.get("cwd")), None)
    lines.append(f"cwd: {cwd}")
    seen = set()    # Claude Code's transcript can carry one call or result more than once; each is shown once
    for e in entries(path):
        msg = e.get("message") or {}
        content = msg.get("content")
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
                body = text_of(c.get("content"))
                lines.append(f"tool result{' (error)' if c.get('is_error') else ''}: {body[:1200]}")
            elif c.get("type") == "text" and e.get("type") == "assistant":
                lines.append(f"agent: {c.get('text', '')[:1500]}")
    kept = [x for i, x in enumerate(lines) if i == 0 or x != lines[i - 1]]
    record(f"{title} - read from the session's own Claude Code transcript", "\n".join(f"  {x}" for x in kept))
    return cwd


def sessions():
    """The sessions the rig Gateway holds now, read without recording (the whole list is long)."""
    return listed_sessions()


SESSION_FIELDS = ("sessionId", "name", "repoPath", "pooledWorktree", "factoryKitDirectory", "factoryHome", "factory",
                  "factorySeat", "factoryDefinitionVersion", "factoryKit", "factoryStartOverride", "launchModel",
                  "parentSessionId", "agent", "status")


def session_row(title, sid):
    """The fields of one session that say where and as what it runs, from the Gateway's session list."""
    found = next((s for s in sessions() if s.get("sessionId") == sid), None)
    body = json.dumps({k: found.get(k) for k in SESSION_FIELDS}, indent=2) if found else "(not in the list)"
    record(f"{title} - GET /sessions, the fields that say where and as what it runs", body)
    return found


def session_named(prefix):
    for s in sessions():
        if (s.get("name") or s.get("Name") or "").startswith(prefix):
            return s
    return None


def stop_session(sid):
    cli(["session", "stop", sid, "--reason", "the phase 5 QA rig is done with this session"], None)




def trust_lines(title):
    """The Director's own log lines for Claude Code's folder trust (P7), from its log files: what it recorded, what it
    found already trusted, and any start it refused because the folder could not be marked trusted."""
    found = []
    for log in sorted((HOME / "logs" / "director").glob("*.log")):
        for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
            if "[ClaudeFolderTrust]" in line or "could not be marked trusted" in line:
                found.append(line)
    record(f"{title} - the Director's log, lines '[ClaudeFolderTrust]' and any 'could not be marked trusted'",
           "\n".join(f"  {x}" for x in found) or "  (none)")
    return found


def launch_lines(title, contains):
    """The Director's own launch log lines for agent processes, from its log files."""
    found = []
    for log in sorted((HOME / "logs" / "director").glob("*.log")):
        for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
            if "[ProcessHost] Start:" in line and contains in line:
                found.append(line)
    record(f"{title} - the Director's log, lines '[ProcessHost] Start:' naming '{contains}'",
           "\n".join(f"  {x}" for x in found) or "  (none)")
    return found




FIRST_LOOK = {}


def listing(title, folders, word=None):
    """How many entries each folder holds, which entries were added or removed since the rig first looked at it, and
    every file in it holding `word` (which must be none). The entries' own names are written only when they changed:
    a user's skills folder holds the user's own skills."""
    lines = []
    for folder in folders:
        names = sorted(p.name for p in folder.iterdir()) if folder.exists() else []
        first = FIRST_LOOK.setdefault(str(folder), names)
        added = sorted(set(names) - set(first))
        removed = sorted(set(first) - set(names))
        lines.append(f"{folder}: {len(names)} entries; since the first look: added {added}, removed {removed}")
        if word:
            holding = []
            for p in folder.rglob("*"):
                try:
                    if p.is_file() and p.stat().st_size < 2_000_000 and word in p.read_text(encoding="utf-8",
                                                                                           errors="ignore"):
                        holding.append(str(p))
                except OSError:
                    pass
            lines.append(f"  files holding '{word}': {len(holding)} {holding}")
    record(title, "\n".join(f"  {x}" for x in lines))
    return lines


def git(args, cwd, title=None):
    r = subprocess.run(["git", *args], cwd=str(cwd), capture_output=True, text=True, timeout=120)
    if title:
        record(title, f"  $ git {' '.join(args)}   (in {cwd})\n" + "".join(f"  {x}\n" for x in (r.stdout + r.stderr)
                                                                            .splitlines()) + f"  [exit {r.returncode}]")
    if r.returncode != 0 and not title:
        raise SystemExit(f"git {args} failed in {cwd}: {r.stdout}{r.stderr}")
    return r.stdout


def worktrees(title):
    env = dict(os.environ, CC_WORKTREES_HOME=str(WT_HOME))
    r = subprocess.run(["cmd", "/c", "cc-worktrees", "list", "--repo", str(SCRATCH)], capture_output=True, text=True,
                       env=env, timeout=120)
    record(title, f"  $ cc-worktrees list --repo {SCRATCH}   (CC_WORKTREES_HOME=<rig>\\cc-worktrees-state)\n"
           + "".join(f"  {x}\n" for x in (r.stdout + r.stderr).splitlines()) + f"  [exit {r.returncode}]")
    return r.stdout


# ===================================================================================================== the definition


def factory_yaml(repositories):
    return (f"factory: {FACTORY}\ntitle: QA Kit\npurpose: Shows a seat started from its definition on a real Director\n"
            "goalText: |\n  Prove the run kit.\ngoalApprovedOn: 2026-10-15\nbossSeat: boss\n"
            f"defaults:\n  machine: {MACHINE}\n  agent: ClaudeCode\n  model: {MODEL}\n"
            f"repositories: [{', '.join(repositories)}]\n")


WORKER_PROMPT = (
    "QA-P6-WORKER. Do exactly these four steps and nothing else, then stop.\n"
    "1. Use your Read tool (never Bash) on the file notes.txt in your current folder and quote its first line. If the "
    "Read tool is refused, write REFUSED and quote the refusal word for word. Do not try any other way to read it.\n"
    "2. Use the Skill tool to load the skill factory-kit:qa-word and write the word it gives.\n"
    "3. Run git status --short with Bash and quote what it printed.\n"
    "4. Run this one command with Bash and quote what it printed:\n"
    "cc-devthrottle session spawn \"$(pwd -W)\" --controlled-by self --name \"QA kit child\" --prompt "
    "'QA-P6-CHILD. Do exactly two steps, then stop. 1. Use your Read tool (never Bash) on notes.txt and quote its "
    "first line. If refused, write REFUSED and quote the refusal word for word. Do not try any other way. 2. Use the "
    "Skill tool to load factory-kit:qa-word. If it is unknown, write NO-SKILL and quote the answer.'\n")

SECOND_PROMPT = ("QA-P6-SECOND. Do exactly two steps, then stop. 1. Use the Skill tool to load factory-kit:qa-word. "
                 "If it is unknown, write NO-SKILL and quote the answer. 2. Run cc-devthrottle session whoami with "
                 "Bash and quote what it printed.")

ASK_PROMPT = ("QA-P6-ASK. Do exactly one step, then stop. Run the command git --version with Bash, alone, and run no "
              "other command first. Quote what it printed, or what you were told if it did not run.")

HOME_PROMPT = ("QA-P6-HOME. Do exactly two steps, then stop. 1. Run pwd -W with Bash and quote what it printed. "
               "2. Run ls -A \"$CC_FACTORY_KIT_DIR\" with Bash and quote what it printed.")

PLAIN_PROMPT = ("QA-P6-PLAIN. Do exactly two steps, then stop. 1. Run pwd -W with Bash and quote what it printed. "
                "2. Use the Skill tool to load factory-kit:qa-word. If it is unknown, write NO-SKILL and quote the "
                "answer.")


def write_definition(folder, deny):
    """The QA definition: five seats, one rule set, one factory skill. `deny` is the rule set's deny list."""
    if folder.exists():
        remove_tree(folder)
    files = {
        "factory.yaml": factory_yaml(["qa-scratch", "qa-not-here"]),
        "rules/base.json": json.dumps({"permissions": {"deny": deny, "ask": []}}, indent=2) + "\n",
        "rules/asks.json": json.dumps({"permissions": {"deny": [], "ask": ["Bash(git --version)"]}}, indent=2) + "\n",
        "seats/asker.yaml": ("seat: asker\nname: Asker\nrole: Asker\nbrief: |\n  Run one command.\nrules: [asks]\n"
                             "starts:\n  - cron: \"0 5 1 1 *\"\n    timeZone: UTC\n    prompt: |\n"
                             + "".join(f"      {x}\n" for x in ASK_PROMPT.splitlines())),
        "skills/qa-word/SKILL.md": ("---\nname: qa-word\ndescription: Gives the QA factory's word. Use when asked for "
                                    "the factory's word.\n---\n\n"
                                    f"The QA factory's word is {SKILL_WORD}. Answer with that word.\n"),
        "seats/boss.yaml": ("seat: boss\nname: Boss\nrole: Boss\nbrief: |\n  Work in your own folder. Touch nothing "
                            "else.\nstarts:\n  - cron: \"0 5 1 1 *\"\n    timeZone: UTC\n    prompt: |\n"
                            + "".join(f"      {x}\n" for x in HOME_PROMPT.splitlines())),
        "seats/worker.yaml": ("seat: worker\nname: Worker\nrole: Worker\nrepository: qa-scratch\nbrief: |\n  Read, "
                              "report, and start one child. Change no file.\nrules: [base]\n"
                              f"secrets: [{HELD_SECRET}]\nskills:\n  factory: [qa-word]\nstarts:\n"
                              "  - cron: \"0 5 1 1 *\"\n    timeZone: UTC\n    prompt: |\n"
                              + "".join(f"      {x}\n" for x in WORKER_PROMPT.splitlines())),
        "seats/keeper.yaml": ("seat: keeper\nname: Keeper\nrole: Keeper\nbrief: |\n  Keep the keys.\n"
                              f"secrets: [{MISSING_SECRET}]\n"),
        "seats/stray.yaml": ("seat: stray\nname: Stray\nrole: Stray\nrepository: qa-not-here\nbrief: |\n  Work in a "
                             "repository this machine does not have.\n"),
        "seats/oldie.yaml": (f"seat: oldie\nname: Oldie\nrole: Oldie\nmachine: {OLD_MACHINE}\nbrief: |\n  Run on the "
                             f"older Director.\nsecrets: [{HELD_SECRET}]\n"),
    }
    for rel, text in files.items():
        f = folder / rel
        f.parent.mkdir(parents=True, exist_ok=True)
        f.write_text(text, encoding="utf-8", newline="\n")
    record(f"the definition folder <rig>/{folder.relative_to(RIG).as_posix()}, every file",
           "\n".join(f"  --- {rel}\n" + "".join(f"  {x}\n" for x in text.splitlines()) for rel, text in files.items()))
    return str(folder)


# ===================================================================================================== the sections


def section_undefined(db, since):
    note("SECTION 12 - qa-plain has NO definition: registered with its folder, a schedule, a run now and a talk. Each "
         "starts as before phase 5: no seat fields, the registered folder as the start folder.")
    manifest = RIG / "qa-plain.manifest.json"
    manifest.write_text(json.dumps({"factory": UNDEFINED, "title": "QA Plain", "folder": str(PLAIN),
                                    "computer": MACHINE, "bossSeat": "boss",
                                    "seats": [{"id": "boss", "name": "Boss", "role": "Boss", "briefFile": "BOSS.md",
                                               "schedules": []}]}, indent=2), encoding="utf-8")
    cli(["factory", "register", "--manifest", str(manifest)], 0)
    job = cli_json(["schedule", "create", "--name", "QA Plain boss", "--machine", MACHINE, "--repo", str(PLAIN),
                    "--cron", "0 5 1 1 *", "--tz", "UTC", "--seed", PLAIN_PROMPT, "--factory", UNDEFINED,
                    "--seat", "boss"])["id"]
    run_now(job)
    path = wait_answer("QA-P6-PLAIN", since)
    summarize("the undefined factory's scheduled run", path)
    record_observed("the undefined factory's scheduled run")
    run = wait_for("the undefined run recorded no session",
                   lambda: next((r for r in cli_json(["schedule", "runs", job]) if r.get("sessionId")), None), 60)
    sid = run["sessionId"]
    history(db, "the undefined run's history row", sid)
    launch_lines("the undefined run's launch", str(PLAIN))
    stop_session(sid)

    status, talk = raw("POST", f"/gateway/factory-agents/factories/{UNDEFINED}/seats/boss/talk", None, 201)
    tid = talk["sessionId"]
    wait_for("the undefined talk's session did not appear", lambda: any(
        (s.get("sessionId") or s.get("id")) == tid for s in sessions()), 120)
    history(db, "the undefined talk's history row", tid)
    wait_for("the undefined talk's agent did not start", lambda: len(observe_machine()) > 0, 60)
    session_row("the undefined talk", tid)
    launch_lines("the undefined talk's launch", str(PLAIN))
    record_observed("the undefined talk")
    stop_session(tid)


def section_publish(since):
    note("SECTION 11 - the first definition of qa-kit, saved and published. The publish asks each seat's machine: "
         f"the worker's held secret '{HELD_SECRET}', the keeper's missing '{MISSING_SECRET}', the registered "
         "qa-scratch, the unregistered qa-not-here, and the oldie's machine QA-OLD, whose Director cannot be asked.")
    v1 = write_definition(RIG / "definition-v1", ["Bash(git push:*)"])
    cli(["factory", "definition", "save", FACTORY, "--dir", v1, "--expected-version", "0", "--note",
         "the first definition"], 0)
    cli(["factory", "definition", "diff", FACTORY], 0)
    cli(["factory", "definition", "publish", FACTORY, "--version", "1", "--replaces", "0"], 0)
    note("Finding 5 (fixed in P8) - the plain schedule list, whose folder column a defined factory's schedule leaves "
         "empty on purpose.")
    cli(["schedule", "list"], None)
    schedules()


def section_refusals(db):
    note("SECTIONS 4, 5 and 6 - three talks refused before anything starts: a missing secret, a repository this "
         "machine has not registered, and a seat on an older Director.")
    listing("before the three talks: the folder the stand-in Director writes each create it receives into", [CREATES])
    for seat in ("keeper", "stray", "oldie"):
        raw("POST", f"/gateway/factory-agents/factories/{FACTORY}/seats/{seat}/talk", None, None)
    listing("after the three talks: the folder the stand-in Director writes each create it receives into", [CREATES])
    rows(db, "every session history row of qa-kit so far (a refused start leaves none)",
         "SELECT SessionId, SessionName, Factory FROM session_history WHERE Factory = ?", (FACTORY,))


def schedules():
    """Each schedule's id, name, factory, seat and folder, from schedule list --json (recorded in full)."""
    jobs = cli_json(["schedule", "list"])
    record("the schedules, from the answer above", "\n".join(
        f"  {j.get('id')}, {j.get('name')}, factory={j.get('factory')}, seat={j.get('seat')}, "
        f"cron={j.get('cronExpression')}, machine={(j.get('target') or {}).get('machine')}, "
        f"action.repoPath={json.dumps((j.get('action') or {}).get('repoPath'))}" for j in jobs))
    return jobs


def run_now(job):
    """Fire a schedule now, by the Gateway route the command line calls, waiting as long as the start takes. The
    command line gives up after 10 seconds, and the Gateway passes that request's cancellation down into the
    Director command: a start slower than 10 seconds is then recorded as not started while the seat runs (the
    finding in the report). So the rig waits on the route itself, and the run record says how the start went."""
    LAST_JOB[:] = [job]
    raw("POST", f"/cron/jobs/{job}/run", None, 200)


def job_of(seat):
    """The id of the seat's schedule, read from the Gateway (the list itself was recorded after the publish)."""
    return next(j["id"] for j in get("/cron/jobs")["jobs"] if j.get("factory") == FACTORY and j.get("seat") == seat)


def worker_run(db, label, since):
    job = job_of("worker")
    run_now(job)
    path = wait_answer("QA-P6-WORKER", since)
    child = wait_answer("QA-P6-CHILD", since)
    cwd = summarize(f"{label}: the worker's run", path)
    summarize(f"{label}: the worker's child", child)
    record_observed(label)
    run = wait_for("the worker run recorded no session",
                   lambda: next((r for r in cli_json(["schedule", "runs", job])
                                 if r.get("sessionId") and r.get("sessionId") not in STARTED_SESSIONS), None), 60)
    sid = run["sessionId"]
    STARTED_SESSIONS.append(sid)
    history(db, f"{label}: the worker run's history row", sid)
    launch_lines(f"{label}: the Director's launches of kit sessions so far", "factory-run")
    worktrees(f"{label}: the pool of qa-scratch")
    listing(f"{label}: the user's skills folders after the run", SKILL_FOLDERS, SKILL_WORD)
    listing(f"{label}: the scratch repository's shared checkout after the run", [SCRATCH], SKILL_WORD)
    git(["status", "--short", "--untracked-files=all"], SCRATCH, f"{label}: the shared checkout, git status")
    for s in sessions():
        if s.get("factory") == FACTORY:
            stop_session(s.get("sessionId"))
    return cwd


def section_second():
    note("A second session on the machine, not a seat: a person's spawn in the folder qa-plain, asked for the "
         "factory skill. It also runs this tree's cc-devthrottle from inside a rig session, as the worker's child "
         "spawn does.")
    t = time.time()
    cli(["session", "spawn", str(PLAIN), "--machine", MACHINE, "--name", "QA plain second session", "--prompt",
         SECOND_PROMPT], 0)
    path = wait_answer("QA-P6-SECOND", t)
    summarize("the second session on the machine", path)
    record_observed("the second session on the machine")
    for s in sessions():
        if (s.get("name") or "").startswith("QA plain second session"):
            stop_session(s.get("sessionId"))


def section_worker(db, since):
    note("SECTIONS 1, 2, 3, 7, 9 and 10 - the worker, a repository seat, run now twice: before and after a publish "
         "that adds a deny on reading notes.txt. Each run reads notes.txt, uses its factory skill, prints git status "
         "and starts a child; each child reads notes.txt and asks for the skill.")
    listing("before any seat run: the user's skills folders", SKILL_FOLDERS, SKILL_WORD)
    listing("before any seat run: the scratch repository's shared checkout", [SCRATCH], SKILL_WORD)
    worktrees("before any seat run: the pool of qa-scratch")
    first = worker_run(db, "RUN A (definition version 1, no deny on notes.txt)", time.time())

    section_second()

    note("The rule tightened: version 2 is version 1 with one change, rules/base.json denies Read(./notes.txt).")
    v2 = write_definition(RIG / "definition-v2", ["Bash(git push:*)", "Read(./notes.txt)"])
    cli(["factory", "definition", "save", FACTORY, "--dir", v2, "--expected-version", "1", "--note",
         "deny reading notes.txt"], 0)
    cli(["factory", "definition", "diff", FACTORY], 0)
    cli(["factory", "definition", "publish", FACTORY, "--version", "2", "--replaces", "1"], 0)
    second = worker_run(db, "RUN B (definition version 2, Read(./notes.txt) denied)", time.time())
    record("the two runs' folders", f"  run A: {first}\n  run B: {second}")


def section_ask(db, since):
    note("SECTION 13 - the asker: its only rule set asks before 'Bash(git --version)' and denies nothing. It runs from "
         "its schedule under the flag this Director launches Claude Code with. Asked, or run without asking?")
    job = job_of("asker")
    run_now(job)
    path = find_transcript("QA-P6-ASK", since)
    USED_TRANSCRIPTS.add(path)
    asked = {}

    def answered_or_asked():
        if finished(path):
            return "finished"
        for s in sessions():
            if s.get("factorySeat") == "asker":
                screen = buffer_of(s["sessionId"])
                # Only Claude Code's own permission question counts - never the prompt's words on the screen. The
                # screen is read with its blanks removed, as it can arrive drawn with cursor moves.
                flat = re.sub(r"\s+", "", screen)
                if "Doyouwanttoproceed" not in flat[-1500:]:
                    continue
                asked["screen"] = screen
                return ("asked-by-the-rule" if "PermissionruleBash(git--version)requiresconfirmation" in flat
                        else "asked-for-another-reason")
        return None
    outcome = wait_for("the asker neither finished nor was asked", answered_or_asked, seconds=420, every=3, tend=True)
    summarize("the asker's scheduled run", path)
    if outcome.startswith("asked"):
        screen = asked["screen"]
        start = max(screen.rfind("Bash command"), screen.rfind("Do you want to proceed") - 600, 0)
        record("the asker's screen at the question: Claude Code stopped to ask", "\n".join(
            f"  {x.rstrip()}" for x in screen[start:].splitlines() if x.strip()))
    record("the asker's outcome", f"  {outcome}")
    record_observed("the asker's scheduled run")
    launch_lines("the asker run's launch", "factory-run")
    for s in sessions():
        if s.get("factory") == FACTORY:
            stop_session(s.get("sessionId"))


def section_home(db, since):
    note("SECTION 8 - the boss, a seat with no repository, run from its schedule: it runs in its kit's work folder.")
    job = job_of("boss")
    cli(["schedule", "get", job], 0)
    run_now(job)
    path = wait_answer("QA-P6-HOME", since)
    summarize("the boss's scheduled run", path)
    record_observed("the boss's scheduled run")
    run = wait_for("the boss run recorded no session",
                   lambda: next((r for r in cli_json(["schedule", "runs", job]) if r.get("sessionId")), None), 60)
    history(db, "the boss run's history row", run["sessionId"])
    launch_lines("the boss run's launch", "\\work, injectedEnv")
    for s in sessions():
        if s.get("factory") == FACTORY:
            stop_session(s.get("sessionId"))


SECTIONS = ["undefined", "publish", "refusals", "worker", "home", "ask"]   # "second" alone runs only the second session


def tree_and_binaries():
    """The commit and the state of the tree the run is from, and when each executable it runs was last written."""
    lines = ["  $ git rev-parse HEAD   (in <repo>)", f"  {git(['rev-parse', 'HEAD'], REPO).strip()}",
             f"  committed {git(['log', '-1', '--format=%cI', 'HEAD'], REPO).strip()}",
             "  $ git status --short   (in <repo>)"]
    lines += [f"  {x}" for x in git(["status", "--short"], REPO).splitlines()] or ["  (clean)"]
    for exe in (EXE, DIRECTOR_EXE, STANDIN):
        written = datetime.fromtimestamp(exe.stat().st_mtime, timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")
        lines.append(f"  {exe}: last written {written}")
    record("the tree this run is from, and its executables", "\n".join(lines))


def remove_tree(folder):
    """Remove a folder of the rig's own, including git's read-only object files."""
    def writable(func, path, _):
        os.chmod(path, 0o700)
        func(path)
    shutil.rmtree(folder, onexc=writable)


def make_repositories():
    (RIG / "repos").mkdir(parents=True)
    git(["init", "--bare", "--initial-branch=main", str(ORIGIN)], RIG)
    git(["init", "--initial-branch=main", str(SCRATCH)], RIG)
    (SCRATCH / "notes.txt").write_text(NOTES_LINE + "\nA second line.\n", encoding="utf-8", newline="\n")
    (SCRATCH / "README.md").write_text("# qa-scratch\n\nA throwaway repository for the phase 5 QA rig.\n",
                                       encoding="utf-8", newline="\n")
    for args in (["add", "-A"], ["-c", "user.name=QA Rig", "-c", "user.email=qa@example.com", "commit", "-m",
                                 "the scratch repository"], ["remote", "add", "origin", str(ORIGIN)],
                 ["push", "-u", "origin", "main"]):
        git(args, SCRATCH)
    PLAIN.mkdir()
    (PLAIN / "README.md").write_text("# qa-plain\n\nThe folder of a factory with no definition.\n", encoding="utf-8")


def main():
    out = Path(sys.argv[1]).resolve()
    only = SECTIONS
    if "--only" in sys.argv:
        only = sys.argv[sys.argv.index("--only") + 1].split(",")
    out.mkdir(parents=True, exist_ok=True)
    if not HELD_SECRET:
        raise SystemExit("QA_HELD_SECRET is not set: set it to the name of a setting-kind entry in `cc-secrets list` "
                         "(only its name is used), then run again")
    for needed in (EXE, DIRECTOR_EXE, STANDIN):
        if not needed.exists():
            raise SystemExit(f"build it first: {needed} does not exist")
    if director_processes():
        raise SystemExit("a cc-director6 from this tree is already running; this rig starts its own and never "
                         "stacks a second")
    if RIG.exists() and rig_processes():
        raise SystemExit(f"processes still run from {RIG}: {rig_processes()}; wait for them to end")
    before_task = task_action()
    print(f"cc-director-launch before this run: {before_task}", flush=True)
    if RIG.exists():
        remove_tree(RIG)
    GW_ROOT.mkdir(parents=True)
    CREATES.mkdir()
    WT_HOME.mkdir()
    make_repositories()
    (GW_ROOT / "config").mkdir()
    (GW_ROOT / "config" / "config.json").write_text('{ "factoryAgents": { "enabled": true } }\n', encoding="utf-8")
    env = dict(os.environ, CC_DIRECTOR_ROOT=str(GW_ROOT), CC_GATEWAY_NO_TAILSCALE="1")
    for k in ("CC_GATEWAY_AUTH", "CC_GATEWAY_NO_AUTH"):
        env.pop(k, None)
    log = open(RIG / "gateway.out.txt", "w", encoding="utf-8")
    gw = subprocess.Popen([str(EXE), "--port", str(PORT)], env=env, stdout=log, stderr=subprocess.STDOUT,
                          cwd=str(EXE.parent))
    standin = None
    director = None
    task_changed = False
    since = time.time()
    try:
        wait_for("the rig Gateway wrote no gateway.db", lambda: bool(list(GW_ROOT.rglob("gateway.db"))) or gw.poll() is not None)
        if gw.poll() is not None:
            raise SystemExit("the rig Gateway exited; see gateway.out.txt")
        db = list(GW_ROOT.rglob("gateway.db"))[0]
        wait_for("the rig gateway.db did not get its tables", lambda: {"device_credentials", "session_history",
                 "factory_definition_versions"} <= {r[0] for r in sqlite3.connect(db).execute(
                     "SELECT name FROM sqlite_master WHERE type='table'")})
        enroll(db)
        wait_for("the rig Gateway did not answer", lambda: get("/factory-definition-schemas") is not None)

        wrapper = prepare_director(DIRECTOR_KEY)
        set_task("cmd.exe", f'/c "{wrapper}"', str(DIRECTOR_EXE.parent))
        task_changed = True
        ps(f"Start-ScheduledTask -TaskName '{TASK}'")
        director = wait_for("the slot 6 Director did not start from the task", lambda: (director_processes() or [None])[0], 60)
        reg = wait_for("the slot 6 Director wrote no instance registration", lambda: registration(director), 120)
        wait_for("the slot 6 Director did not connect to the rig Gateway", lambda: [
            d for d in get("/directors") if d.get("directorId") == reg["DirectorId"]], 120)
        standin = subprocess.Popen([str(STANDIN), GW, OLD_MACHINE, str(CREATES), STANDIN_KEY], stdin=subprocess.PIPE,
                                   stdout=open(RIG / "standin.out.txt", "w"), stderr=subprocess.STDOUT)
        wait_for("the stand-in Director did not connect", lambda: any(
            d.get("directorId") == STANDIN_ID for d in get("/directors")), 60)
        note(f"Gateway: {GW}, built from this tree, storage root <rig>\\gateway-root made empty for this run. "
             f"The slot 6 Director (pid {director}, id {reg['DirectorId']}) was started by the {TASK} scheduled task "
             f"through <rig>\\start-director6.cmd, storage root <rig>\\director-root, and connected on <machine>. "
             f"The stand-in Director '{STANDIN_ID}' is connected on {OLD_MACHINE}.")
        tree_and_binaries()
        record(f"the {TASK} scheduled task's action as the rig found it (Execute|Arguments|WorkingDirectory)",
               f"  {before_task['execute']}|{before_task['arguments']}|{before_task['working']}")
        raw("GET", "/directors", None, 200)
        record("the slot 6 Director's instance configuration (written by the rig before launch)",
               (HOME / "config" / "config.json").read_text(encoding="utf-8").replace(DIRECTOR_KEY, "<director key>"))
        record("its registered repositories", (HOME / "config" / "director" / "repositories.json")
               .read_text(encoding="utf-8"))

        if "undefined" in only:
            section_undefined(db, since)
        if "publish" in only:
            section_publish(since)
        if "refusals" in only:
            section_refusals(db)
        if "worker" in only:
            section_worker(db, since)
        if "home" in only:
            section_home(db, time.time())
        if "ask" in only:
            section_ask(db, time.time())
        if only == ["second"]:
            section_second()
        record("Claude Code's folder-trust question over the whole run",
               f"  sessions whose screen drew it: {len(TRUST_SEEN)}; answered by the rig: {sum(TENDED.values())}")
    except SystemExit as stopped:
        print(f"the run stopped: {stopped}", flush=True)
        note(f"the run stopped: {stopped}")
        if LAST_JOB:
            cli(["schedule", "runs", LAST_JOB[0], "--json"], None)
            record_observed("the wait that failed")
        raise
    finally:
        try:
            if director is not None and director in director_processes():
                wait_for_tool_install(director)
                stop_director(director)
            if director is not None:
                trust_lines("the whole run")
        finally:
            if task_changed:
                set_task(before_task["execute"], before_task["arguments"], before_task["working"])
                after = task_action()
                print(f"cc-director-launch put back: {after}", flush=True)
                record(f"the {TASK} scheduled task's action after the rig put it back "
                       "(Execute|Arguments|WorkingDirectory; re-registered with a far-future, on-demand trigger)",
                       f"  {after['execute']}|{after['arguments']}|{after['working']}\n"
                       f"  the same as found: {after == before_task}")
                if after != before_task:
                    print(f"WARNING: the task reads {after}, not {before_task}", flush=True)
            if standin is not None:
                standin.stdin.close()
                try:
                    standin.wait(timeout=30)
                except subprocess.TimeoutExpired:
                    standin.kill()
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
