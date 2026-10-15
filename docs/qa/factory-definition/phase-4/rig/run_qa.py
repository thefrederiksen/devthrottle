"""QA for Factory Definition, phase 4: a publish drives the factory, against a throwaway Gateway.

Runs in the foreground and owns everything it starts, and stops it when the run ends, pass or fail:
- a Gateway (CcDirector.Gateway, Debug build of THIS tree) on port 7997 with its own storage root under %TEMP%
  (CC_DIRECTOR_ROOT), Tailscale off, authentication ON, factory agents switched on in the rig's OWN config.json. It can
  touch neither the owner's Gateway nor any factory.
- the stand-in Director of the Factory Manager rig (docs/qa/factory-control/factory-manager/rig/standin-director),
  connected on the rig machine QA-RIG. It answers the session create a talk or a run sends and writes down exactly what
  the Gateway sent - the session's name and its first prompt. It never starts a real session.

Who is asking is made exactly as the phase 1 rig makes it (docs/qa/factory-definition/phase-1/rig/run_qa.py): a person
is a browser device row, a session is a session_keys row plus a session_history row naming its factory. The stand-in
Director signs in with a workstation device row, and the rig reports a trigger's check as that Director does.

Every command is the real cc-devthrottle from this tree, or a raw HTTP request where the command line has no command
for the route; what each printed or answered, and its exit code or status, is in transcript.txt.

Usage (from the repository root, after building the Gateway and the stand-in Director):
    dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
    dotnet build docs/qa/factory-control/factory-manager/rig/standin-director/StandinDirector.csproj
    uv run --no-project --with "typer>=0.16.1" --with "click>=8.2.1" --with "rich>=13.0.0" --with "requests>=2.31.0" \
        python docs/qa/factory-definition/phase-4/rig/run_qa.py <out dir> [--briefs]
--briefs adds section 7, brief through the definition (pull request 4008).
"""

import hashlib
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[5]
HERE = Path(__file__).resolve().parent
EXAMPLE = REPO / "docs" / "qa" / "factory-definition" / "phase-1" / "example"
EXE = REPO / "src" / "CcDirector.Gateway" / "bin" / "Debug" / "net10.0" / "CcDirector.Gateway.exe"
STANDIN = (REPO / "docs" / "qa" / "factory-control" / "factory-manager" / "rig" / "standin-director" / "bin" / "Debug"
           / "net10.0" / "StandinDirector.exe")
CLI_DIR = REPO / "tools" / "cc-devthrottle"
PORT = 7997
GW = f"http://127.0.0.1:{PORT}"
FACTORY = "qa-outreach"
UNDEFINED = "qa-undefined"
MACHINE = "QA-RIG"
DIRECTOR_ID = "qa-standin-director"
RIG = Path(os.environ["TEMP"]) / "factory-definition-p4-qa"
ROOT = RIG / "root"
TALKS = RIG / "creates"
NOW = datetime.now(timezone.utc).replace(tzinfo=None)

PERSON_KEY = "qa-person-" + uuid.uuid4().hex
DIRECTOR_KEY = "qa-director-" + uuid.uuid4().hex
SESSION_ID = str(uuid.uuid4())
SESSION_KEY = "qa-session-" + uuid.uuid4().hex
OUTSIDER_ID = str(uuid.uuid4())
OUTSIDER_KEY = "qa-session-" + uuid.uuid4().hex

TRANSCRIPT = []


def sha(key):
    return hashlib.sha256(key.encode("utf-8")).hexdigest()


def stamp(when):
    return when.strftime("%Y-%m-%d %H:%M:%S")


def wait_for_tables(db, tables):
    deadline = time.time() + 180
    while time.time() < deadline:
        if db.exists():
            c = sqlite3.connect(db)
            have = {r[0] for r in c.execute("SELECT name FROM sqlite_master WHERE type='table'")}
            c.close()
            if all(t in have for t in tables):
                return
        time.sleep(1)
    raise SystemExit(f"the rig gateway.db did not get the tables {tables} within 180s")


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
    c = sqlite3.connect(db)
    for device_id, key, device_type in (("qa-browser", PERSON_KEY, "browser"), ("qa-director", DIRECTOR_KEY, "workstation")):
        insert(c, "device_credentials", {
            "DeviceId": device_id, "MachineName": MACHINE, "DeviceKeyHash": sha(key), "KeyPrefix": key[:8],
            "KeyLast4": key[-4:], "IssuedAtUtc": stamp(NOW), "Status": "active", "Platform": "windows",
            "DeviceType": device_type, "TenantId": "local"})
    expires = stamp(NOW + timedelta(hours=6))
    for sid, key, factory in ((SESSION_ID, SESSION_KEY, FACTORY), (OUTSIDER_ID, OUTSIDER_KEY, "invoice-factory")):
        insert(c, "session_keys", {"SessionId": sid, "TenantId": "local", "DirectorId": "qa-director",
                                   "KeyHash": sha(key), "IssuedAtUtc": stamp(NOW), "ExpiresAtUtc": expires})
        insert(c, "session_history", {"tenant_id": "local", "SessionId": sid, "DirectorId": "qa-director",
                                      "Factory": factory, "SessionName": f"QA - {factory} session",
                                      "StartedAtUtc": stamp(NOW), "LastSeenUtc": stamp(NOW)})
    c.commit()
    c.close()


def get(path, key=PERSON_KEY):
    req = urllib.request.Request(GW + path, headers={"Authorization": f"Bearer {key}"})
    with urllib.request.urlopen(req, timeout=10) as res:
        return json.loads(res.read().decode("utf-8"))


def wait_for(what, ready, seconds=180):
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            if ready():
                return
        except (urllib.error.URLError, ConnectionError, TimeoutError):
            pass
        time.sleep(1)
    raise SystemExit(f"{what} within {seconds}s")


def shown(text):
    """The folders a command names are written <repo> and <rig>, so the transcript names no one's machine."""
    return text.replace(str(RIG), "<rig>").replace(str(REPO), "<repo>").replace(json.dumps(str(RIG))[1:-1], "<rig>")


def note(text):
    TRANSCRIPT.append(f"# {text}\n")


def cli(who, args, expect_exit):
    """Run this tree's cc-devthrottle as `who` and keep exactly what it printed."""
    key, label = who
    env = dict(os.environ, CC_GATEWAY_URL=GW, CC_GATEWAY_SESSION_KEY=key, NO_COLOR="1", COLUMNS="200")
    env.pop("CC_SESSION_ID", None)
    launch = ("import sys; sys.path[:0] = [sys.argv[1], sys.argv[2]]; sys.argv = ['cc-devthrottle'] + sys.argv[3:]; "
              "from src.cli import tool_main; tool_main()")
    r = subprocess.run([sys.executable, "-c", launch, str(CLI_DIR), str(CLI_DIR.parent), *args], cwd=str(CLI_DIR),
                       env=env, capture_output=True, text=True, timeout=120)
    line = shown(" ".join(a if " " not in a else f'"{a}"' for a in args))
    TRANSCRIPT.append(f"$ cc-devthrottle {line}      (as {label})\n{shown(r.stdout)}{shown(r.stderr)}[exit {r.returncode}]\n")
    print(f"cli {line} as {label}: exit {r.returncode}", flush=True)
    if r.returncode != expect_exit:
        raise SystemExit(f"expected exit {expect_exit} from: cc-devthrottle {line}\n{r.stdout}{r.stderr}")
    return r


def cli_json(who, args):
    """A command run with --json whose answer the rig reads (an id); kept in the transcript like any other."""
    return json.loads(cli(who, [*args, "--json"], 0).stdout)


def raw(method, path, body, expect_status, key=PERSON_KEY, label="a person"):
    """One request sent as raw JSON, kept exactly as answered."""
    data = None if body is None else body.encode("utf-8")
    req = urllib.request.Request(GW + path, data=data, method=method,
                                 headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=60) as res:
            status, answer = res.status, res.read().decode("utf-8")
    except urllib.error.HTTPError as exc:
        status, answer = exc.code, exc.read().decode("utf-8")
    TRANSCRIPT.append(f"$ {method} {path}      (raw HTTP, as {label})\n" + (f"  body: {shown(body)}\n" if body else "")
                      + f"  status: {status}\n  answer: {shown(answer)}\n")
    print(f"raw {method} {path}: {status}", flush=True)
    if status != expect_status:
        raise SystemExit(f"expected {expect_status} from {method} {path}, got {status}: {answer}")
    return json.loads(answer) if answer.strip().startswith(("{", "[")) else answer


def rows(db, title, sql, args=()):
    """Rows read straight from the rig's own gateway.db, for what no route serves."""
    c = sqlite3.connect(db)
    cur = c.execute(sql, args)
    names = [d[0] for d in cur.description]
    found = cur.fetchall()
    c.close()
    TRANSCRIPT.append(f"# {title}\n#   read from the rig's gateway.db: {sql} {list(args)}\n" +
                      f"  {', '.join(names)}\n" + "".join(f"  {', '.join(str(v) for v in r)}\n" for r in found) +
                      f"  ({len(found)} rows)\n")
    return found


def creates_since(seen):
    """Each session create the stand-in Director received after `seen`, as it wrote them down."""
    files = sorted(TALKS.glob("create-*.json"), key=lambda p: int(p.stem.split("-")[1]))
    for f in files[seen:]:
        n = f.stem.split("-")[1]
        prompt = (TALKS / f"create-{n}-first-prompt.txt").read_text(encoding="utf-8")
        TRANSCRIPT.append(f"# the session create the stand-in Director received ({f.name}), exactly as the Gateway sent it\n"
                          + shown(f.read_text(encoding="utf-8")) + "\n# its first prompt:\n" + shown(prompt) + "\n")
    return len(files)


def wait_for_creates(count):
    wait_for(f"the stand-in Director did not receive create number {count}",
             lambda: len(list(TALKS.glob("create-*.json"))) >= count, seconds=60)


def folder_with(name, edits=(), add=None):
    """A copy of the phase 1 example with exact text edits ((path, old, new), each old must occur once)."""
    target = RIG / "variants" / name
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(EXAMPLE, target)
    for path, old, new in edits:
        f = target / path
        text = f.read_text(encoding="utf-8")
        if text.count(old) != 1:
            raise SystemExit(f"variant {name}: '{old}' does not occur exactly once in {path}")
        f.write_text(text.replace(old, new), encoding="utf-8", newline="\n")
    for path, text in (add or {}).items():
        f = target / path
        f.parent.mkdir(parents=True, exist_ok=True)
        f.write_text(text, encoding="utf-8", newline="\n")
    TRANSCRIPT.append(f"# folder '{name}': the phase 1 example with " + "; ".join(
        [f"{p}: {o!r} -> {n!r}" for p, o, n in edits] + [f"{p} added: {t!r}" for p, t in (add or {}).items()]) + "\n")
    return str(target)


# The definition this run publishes: the phase 1 example on the rig's machine, the boss naming a secret, and a
# timed start for the writer, so the writer's name is on a schedule.
BASE_EDITS = [
    ("factory.yaml", "machine: SOREN_NORTH", f"machine: {MACHINE}"),
    ("seats/boss.yaml", "rules: [base]", "rules: [base]\nsecrets: [qa-mail-password]"),
    ("seats/writer.yaml", "files: [files/post-template.md]",
     'files: [files/post-template.md]\nstarts:\n  - cron: "0 10 * * 2,4"\n    timeZone: Europe/Copenhagen\n'
     "    prompt: Write this week's post."),
]


def manifest(factory, title, folder, seats):
    path = RIG / f"{factory}.manifest.json"
    path.write_text(json.dumps({
        "factory": factory, "title": title, "folder": str(folder), "computer": MACHINE, "bossSeat": "boss",
        "seats": [{"id": s, "name": s.capitalize(), "role": s.capitalize(), "briefFile": f"{s.upper()}.md", "schedules": []}
                  for s in seats],
    }, indent=2), encoding="utf-8")
    return str(path)


def main():
    out = Path(sys.argv[1]).resolve()
    briefs = "--briefs" in sys.argv[2:]
    out.mkdir(parents=True, exist_ok=True)
    for needed in (EXE, STANDIN):
        if not needed.exists():
            raise SystemExit(f"build it first: {needed} does not exist")
    if RIG.exists():
        shutil.rmtree(RIG)
    ROOT.mkdir(parents=True)
    TALKS.mkdir()
    (ROOT / "config").mkdir()
    (ROOT / "config" / "config.json").write_text('{ "factoryAgents": { "enabled": true } }\n', encoding="utf-8")
    env = dict(os.environ, CC_DIRECTOR_ROOT=str(ROOT), CC_GATEWAY_NO_TAILSCALE="1")
    for k in ("CC_GATEWAY_AUTH", "CC_GATEWAY_NO_AUTH"):
        env.pop(k, None)
    log = open(RIG / "gateway.out.txt", "w", encoding="utf-8")
    gw = subprocess.Popen([str(EXE), "--port", str(PORT)], env=env, stdout=log, stderr=subprocess.STDOUT,
                          cwd=str(EXE.parent))
    standin = None
    try:
        wait_for("the rig Gateway wrote no gateway.db", lambda: bool(list(ROOT.rglob("gateway.db"))) or gw.poll() is not None)
        if gw.poll() is not None:
            raise SystemExit("the rig Gateway exited; see gateway.out.txt")
        db = list(ROOT.rglob("gateway.db"))[0]
        wait_for_tables(db, ["device_credentials", "session_keys", "session_history", "factory_definition_versions"])
        enroll(db)
        wait_for("the rig Gateway did not answer", lambda: get("/factory-definition-schemas") is not None)
        standin = subprocess.Popen([str(STANDIN), GW, MACHINE, str(TALKS), DIRECTOR_KEY], stdin=subprocess.PIPE,
                                   stdout=open(RIG / "standin.out.txt", "w"), stderr=subprocess.STDOUT)
        wait_for("the stand-in Director did not appear in GET /directors",
                 lambda: any(d.get("directorId") == DIRECTOR_ID for d in get("/directors")), seconds=60)
        person = (PERSON_KEY, "a person: the rig's enrolled browser device")
        session = (SESSION_KEY, f"session {SESSION_ID}, of factory {FACTORY}")
        note(f"Gateway: {GW}, built from this tree; its storage root is <rig>\\root, made empty for this run. "
             f"The stand-in Director '{DIRECTOR_ID}' is connected on machine {MACHINE}.")
        d = ["factory", "definition"]
        seen = 0

        # ================= 0. Before: a registered factory with NO definition, its schedules, a trigger, their runs.
        note("SECTION 0 - qa-outreach before any definition: registered with seats boss, writer and scout; a schedule "
             "for the boss and one for the scout; a trigger; one run of the scout's schedule and one of the trigger.")
        folder = RIG / "qa-outreach-folder"
        folder.mkdir()
        cli(person, ["factory", "register", "--manifest", manifest(FACTORY, "QA Outreach", folder, ["boss", "writer", "scout"])], 0)
        boss_job = cli_json(person, ["schedule", "create", "--name", "QA Outreach boss morning", "--machine", MACHINE,
                                     "--repo", str(folder), "--cron", "0 8 * * 1-5", "--tz", "Europe/Copenhagen",
                                     "--seed", "Morning round.", "--factory", FACTORY, "--seat", "boss"])["id"]
        scout_job = cli_json(person, ["schedule", "create", "--name", "QA Outreach scout at noon", "--machine", MACHINE,
                                      "--repo", str(folder), "--cron", "0 12 * * *", "--tz", "Europe/Copenhagen",
                                      "--seed", "Scout round.", "--factory", FACTORY, "--seat", "scout"])["id"]
        trigger = cli_json(person, ["trigger", "add", "--name", "qa-outreach-new-mail", "--factory", FACTORY,
                                    "--agent", "scout", "--machine", MACHINE, "--repo", str(folder),
                                    "--check", "python check_mail.py", "--every", "1h",
                                    "--prompt", "Answer the {count} new mails."])
        trigger_id = trigger["id"]
        cli(person, ["schedule", "run", scout_job], 0)
        wait_for_creates(1)
        seen = creates_since(seen)
        cli(person, ["schedule", "runs", scout_job], 0)
        # The trigger's check, reported as the stand-in Director reports it: nothing to do.
        raw("POST", f"/directors/{DIRECTOR_ID}/triggers/{trigger_id}/checks",
            json.dumps({"checkedAtUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ"), "exitCode": 0, "timedOut": False,
                        "output": '{"count": 0}'}), 200, key=DIRECTOR_KEY, label="the stand-in Director")
        cli(person, ["trigger", "runs", "qa-outreach-new-mail"], 0)
        cli(person, ["schedule", "list", "--fields", "id,name,enabled,cron,machine"], 0)

        # ================= 5 and 8. The plan names what it will delete; the publish; the secret question; runs kept.
        note("SECTION 5 and 8 - the first definition: what a publish would do (the plan names the scout's schedule and "
             "the trigger it will delete), the publish, and the runs of both still there after.")
        v1 = folder_with("first-definition", BASE_EDITS)
        cli(person, [*d, "save", FACTORY, "--dir", v1, "--expected-version", "0", "--note", "the first definition"], 0)
        cli(person, [*d, "diff", FACTORY], 0)
        cli(person, [*d, "publish", FACTORY, "--version", "1", "--replaces", "0"], 0)
        cli(person, ["schedule", "list", "--fields", "id,name,enabled,cron,machine"], 0)
        cli(person, ["schedule", "runs", scout_job], 0)
        cli(person, ["trigger", "list"], 0)
        raw("GET", f"/triggers/{trigger_id}/runs", None, 404)
        rows(db, f"the trigger's runs after the publish deleted the trigger {trigger_id}",
             "SELECT * FROM trigger_runs WHERE TriggerId = ? COLLATE NOCASE", (trigger_id,))
        rows(db, "the scout schedule's runs after the publish deleted the schedule",
             "SELECT Id, JobId, Factory, Seat, JobName, InfraStatus, BriefVersion FROM cron_runs WHERE JobId = ?", (scout_job,))

        # ================= 1. One edit and one publish rename a seat everywhere it shows.
        note("SECTION 1 - the writer renamed Author by one edit and one publish: the seat page, the schedule, a run "
             "and a talk.")
        v2 = folder_with("writer-renamed", [*BASE_EDITS, ("seats/writer.yaml", "name: Writer", "name: Author")])
        cli(person, [*d, "save", FACTORY, "--dir", v2, "--expected-version", "1", "--note", "the writer is Author now"], 0)
        cli(person, [*d, "diff", FACTORY], 0)
        cli(person, [*d, "publish", FACTORY, "--version", "2", "--replaces", "1"], 0)
        raw("GET", f"/gateway/factories/{FACTORY}/seats/writer", None, 200)
        jobs = cli_json(person, ["schedule", "list"])
        writer_job = next(j["id"] for j in jobs if j.get("factory") == FACTORY and j.get("seat") == "writer")
        cli(person, ["schedule", "list", "--fields", "id,name,enabled,cron,machine"], 0)
        cli(person, ["schedule", "run", writer_job], 0)
        wait_for_creates(seen + 1)
        seen = creates_since(seen)
        cli(person, ["schedule", "runs", writer_job], 0)
        raw("POST", f"/gateway/factory-agents/factories/{FACTORY}/seats/writer/talk", None, 201)
        wait_for_creates(seen + 1)
        seen = creates_since(seen)

        # ================= 3. A direct edit refused; the operating routes still work.
        note("SECTION 3 - qa-outreach is defined: every way to define it another way is refused; enable, disable, run "
             "now and goal numbers still work.")
        cli(person, ["schedule", "create", "--name", "A second boss start", "--machine", MACHINE, "--repo", str(folder),
                     "--cron", "0 15 * * 1-5", "--tz", "Europe/Copenhagen", "--seed", "Afternoon round.",
                     "--factory", FACTORY, "--seat", "boss"], 1)
        stored = get(f"/cron/jobs/{boss_job}")
        edited = dict(stored, cronExpression="0 7 * * 1-5")
        raw("PUT", f"/cron/jobs/{boss_job}", json.dumps(edited), 409)
        cli(person, ["schedule", "delete", boss_job], 1)
        cli(person, ["schedule", "unlink", boss_job], 1)
        cli(person, ["trigger", "add", "--name", "qa-outreach-another", "--factory", FACTORY, "--agent", "boss",
                     "--machine", MACHINE, "--repo", str(folder), "--check", "python check.py", "--every", "1h",
                     "--prompt", "Look."], 1)
        cli(person, ["factory", "register", "--manifest", str(RIG / f"{FACTORY}.manifest.json")], 1)
        cli(person, ["factory", "purpose", FACTORY, "A purpose set by hand"], 1)
        cli(person, ["schedule", "disable", boss_job], 0)
        cli(person, ["schedule", "enable", boss_job], 0)
        cli(person, ["schedule", "run", boss_job], 0)
        wait_for_creates(seen + 1)
        seen = creates_since(seen)
        cli(person, ["factory", "goal-number", "post", "--factory", FACTORY, "--value", "2", "--unit", "posts this week",
                     "--date", NOW.strftime("%Y-%m-%d"), "--link", "https://example.com/qa-outreach/posts",
                     "--by", "boss"], 0)
        cli(person, ["schedule", "get", boss_job], 0)

        # ================= 4. A factory with no definition: the same edits accepted.
        note("SECTION 4 - qa-undefined has no definition: the same edits are accepted.")
        other = RIG / "qa-undefined-folder"
        other.mkdir()
        cli(person, ["factory", "register", "--manifest", manifest(UNDEFINED, "QA Undefined", other, ["boss"])], 0)
        undefined_job = cli_json(person, ["schedule", "create", "--name", "QA Undefined boss", "--machine", MACHINE,
                                          "--repo", str(other), "--cron", "0 8 * * 1-5", "--tz", "Europe/Copenhagen",
                                          "--seed", "Morning round.", "--factory", UNDEFINED, "--seat", "boss"])["id"]
        stored = get(f"/cron/jobs/{undefined_job}")
        raw("PUT", f"/cron/jobs/{undefined_job}", json.dumps(dict(stored, cronExpression="0 7 * * 1-5")), 200)
        cli(person, ["schedule", "unlink", undefined_job], 0)
        cli(person, ["schedule", "link", undefined_job, "--factory", UNDEFINED, "--seat", "boss"], 0)
        cli(person, ["trigger", "add", "--name", "qa-undefined-new-mail", "--factory", UNDEFINED, "--agent", "boss",
                     "--machine", MACHINE, "--repo", str(other), "--check", "python check.py", "--every", "1h",
                     "--prompt", "Look."], 0)
        cli(person, ["trigger", "pause", "qa-undefined-new-mail"], 0)
        cli(person, ["trigger", "resume", "qa-undefined-new-mail"], 0)
        cli(person, ["factory", "register", "--manifest", str(RIG / f"{UNDEFINED}.manifest.json")], 0)
        cli(person, ["factory", "purpose", UNDEFINED, "A purpose set by hand"], 0)
        cli(person, ["schedule", "delete", undefined_job], 0)

        # ================= 6. A restore puts an older version's seats and schedules back; a stale replaces refused.
        note("SECTION 6 - restore version 1: its seats and schedules come back as a new newest version. Then a publish "
             "and a restore whose --replaces is no longer the published version, refused.")
        cli(person, [*d, "restore", FACTORY, "--version", "1", "--expected-version", "2", "--replaces", "2"], 0)
        cli(person, ["schedule", "list", "--fields", "id,name,enabled,cron,machine"], 0)
        raw("GET", f"/gateway/factories/{FACTORY}/seats/writer", None, 200)
        cli(person, [*d, "save", FACTORY, "--dir", v2, "--expected-version", "3", "--note", "Author again, as a draft"], 0)
        cli(person, [*d, "publish", FACTORY, "--version", "4", "--replaces", "2"], 1)
        cli(person, [*d, "restore", FACTORY, "--version", "2", "--expected-version", "4", "--replaces", "2"], 1)
        cli(person, [*d, "history", FACTORY], 0)

        if briefs:
            brief_section(person, session, db)
    finally:
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


def brief_section(person, session, db):
    """Section 7, brief through the definition (pull request 4008). Written once 4008 has merged."""
    raise SystemExit("section 7 is written once pull request 4008 has merged")


if __name__ == "__main__":
    main()
