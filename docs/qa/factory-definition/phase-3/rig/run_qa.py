"""QA for Factory Definition, phase 3: export and import, against a throwaway Gateway.

Runs in the foreground and owns everything it starts, and stops it when the run ends, pass or fail:
- a Gateway (CcDirector.Gateway, Debug build of THIS tree) on port 7996 with its own storage root under %TEMP%
  (CC_DIRECTOR_ROOT), Tailscale off, authentication ON. It can touch neither the owner's Gateway nor any factory.

Who is asking is made exactly as the phase 1 rig makes it (docs/qa/factory-definition/phase-1/rig/run_qa.py): a
person is a browser device row, a session is a session_keys row plus a session_history row naming its factory.

What it shows, every command the real cc-devthrottle from this tree, its output and exit code in transcript.txt:
- a factory with NO definition (registered, with a published brief, schedules and a trigger) exported from what is
  live, with what could not be written listed; its folder imported and refused by the check with file and line;
  fixed and imported as the factory's first draft
- export, export again, import into a new id, export again - with a SHA-256 of every file of every folder
- an import never publishes: the published version read before and after
- a draft save and an import that map a path to null, refused with 400 naming the path (sent as raw JSON, because
  the command line never sends a null)
- the schemas served

Usage (from the repository root, after `dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj`):
    uv run --no-project --with "typer>=0.16.1" --with "click>=8.2.1" --with "rich>=13.0.0" --with "requests>=2.31.0" \
        python docs/qa/factory-definition/phase-3/rig/run_qa.py <out dir>
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
CLI_DIR = REPO / "tools" / "cc-devthrottle"
PORT = 7996
GW = f"http://127.0.0.1:{PORT}"
FACTORY = "qa-outreach"
COPY = "qa-outreach-copy"
LIVE = "qa-live"
RIG = Path(os.environ["TEMP"]) / "factory-definition-p3-qa"
ROOT = RIG / "root"
NOW = datetime.now(timezone.utc).replace(tzinfo=None)

PERSON_KEY = "qa-person-" + uuid.uuid4().hex
SESSION_ID = str(uuid.uuid4())
SESSION_KEY = "qa-session-" + uuid.uuid4().hex
OUTSIDER_ID = str(uuid.uuid4())
OUTSIDER_KEY = "qa-session-" + uuid.uuid4().hex

TRANSCRIPT = []


def sha(key):
    return hashlib.sha256(key.encode("utf-8")).hexdigest()


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
        row[name] = 0 if "INT" in kind or "REAL" in kind else NOW.strftime("%Y-%m-%d %H:%M:%S") if "Utc" in name else ""
    c.execute(f"INSERT INTO {table} ({', '.join(row)}) VALUES ({', '.join('?' for _ in row)})", list(row.values()))


def enroll(db):
    c = sqlite3.connect(db)
    insert(c, "device_credentials", {
        "DeviceId": "qa-browser", "MachineName": "QA", "DeviceKeyHash": sha(PERSON_KEY), "KeyPrefix": PERSON_KEY[:8],
        "KeyLast4": PERSON_KEY[-4:], "IssuedAtUtc": NOW.strftime("%Y-%m-%d %H:%M:%S"), "Status": "active",
        "Platform": "windows", "DeviceType": "browser", "TenantId": "local"})
    expires = (NOW + timedelta(hours=6)).strftime("%Y-%m-%d %H:%M:%S")
    for sid, key, factory in ((SESSION_ID, SESSION_KEY, FACTORY), (OUTSIDER_ID, OUTSIDER_KEY, "invoice-factory")):
        insert(c, "session_keys", {"SessionId": sid, "TenantId": "local", "DirectorId": "qa-director",
                                   "KeyHash": sha(key), "IssuedAtUtc": NOW.strftime("%Y-%m-%d %H:%M:%S"),
                                   "ExpiresAtUtc": expires})
        insert(c, "session_history", {"tenant_id": "local", "SessionId": sid, "DirectorId": "qa-director",
                                      "Factory": factory, "SessionName": f"QA - {factory} session",
                                      "StartedAtUtc": NOW.strftime("%Y-%m-%d %H:%M:%S"),
                                      "LastSeenUtc": NOW.strftime("%Y-%m-%d %H:%M:%S")})
    c.commit()
    c.close()


def wait_for_gateway():
    deadline = time.time() + 180
    while time.time() < deadline:
        try:
            req = urllib.request.Request(GW + "/factory-definition-schemas", headers={"Authorization": f"Bearer {PERSON_KEY}"})
            with urllib.request.urlopen(req, timeout=5) as res:
                if res.status == 200:
                    return
        except (urllib.error.URLError, ConnectionError, TimeoutError):
            pass
        time.sleep(1)
    raise SystemExit("the rig Gateway did not answer the schemas route within 180s")


def shown(text):
    """The two folders a command names are written <repo> and <rig>, so the transcript names no one's machine."""
    return text.replace(str(RIG), "<rig>").replace(str(REPO), "<repo>")


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


def raw(method, path, body, expect_status):
    """One request sent as raw JSON with the person's key, kept exactly as answered."""
    data = body.encode("utf-8")
    req = urllib.request.Request(GW + path, data=data, method=method,
                                 headers={"Authorization": f"Bearer {PERSON_KEY}", "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=30) as res:
            status, answer = res.status, res.read().decode("utf-8")
    except urllib.error.HTTPError as exc:
        status, answer = exc.code, exc.read().decode("utf-8")
    TRANSCRIPT.append(f"$ {method} {path}      (raw HTTP, as a person)\n  body: {body}\n  status: {status}\n  answer: {answer}\n")
    print(f"raw {method} {path}: {status}", flush=True)
    if status != expect_status:
        raise SystemExit(f"expected {expect_status} from {method} {path}, got {status}: {answer}")


def hashes(title, folder):
    """A SHA-256 of every file of a folder, in path order."""
    root = Path(folder)
    rows = {p.relative_to(root).as_posix(): (hashlib.sha256(p.read_bytes()).hexdigest(), p.stat().st_size)
            for p in sorted(root.rglob("*")) if p.is_file()}
    TRANSCRIPT.append(f"# SHA-256 of every file in {shown(str(root))} ({title})\n" +
                      "".join(f"  {digest}  {size:>5}  {path}\n" for path, (digest, size) in rows.items()))
    return rows


def compare(a_title, a, b_title, b):
    same = sorted(p for p in a if p in b and a[p] == b[p])
    differ = sorted(p for p in set(a) | set(b) if a.get(p) != b.get(p))
    TRANSCRIPT.append(f"# {a_title} against {b_title}: {len(same)} files identical, {len(differ)} differ"
                      + (f" ({', '.join(differ)})" if differ else "") + "\n")
    return differ


def copy_with(name, source, edits=()):
    """A copy of a folder with exact text edits ((path, old, new), each old must occur once)."""
    target = RIG / "variants" / name
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(source, target)
    for path, old, new in edits:
        f = target / path
        data = f.read_bytes().decode("utf-8")
        if data.count(old) != 1:
            raise SystemExit(f"variant {name}: '{old}' does not occur exactly once in {path}")
        f.write_bytes(data.replace(old, new).encode("utf-8"))
    TRANSCRIPT.append(f"# folder '{name}': {shown(str(source))} with " +
                      ("; ".join(f"{p}: {o!r} -> {n!r}" for p, o, n in edits) or "no edit") + "\n")
    return str(target)


def main():
    out = Path(sys.argv[1]).resolve()
    out.mkdir(parents=True, exist_ok=True)
    if not EXE.exists():
        raise SystemExit(f"build the Gateway first: {EXE} does not exist")
    if RIG.exists():
        shutil.rmtree(RIG)
    ROOT.mkdir(parents=True)
    # Factory agents switched on in the rig's OWN config.json, so the registry, schedules with a factory and triggers
    # are served: the export of a factory with no definition reads them.
    (ROOT / "config").mkdir()
    (ROOT / "config" / "config.json").write_text('{ "factoryAgents": { "enabled": true } }\n', encoding="utf-8")
    exports = RIG / "exports"
    env = dict(os.environ, CC_DIRECTOR_ROOT=str(ROOT), CC_GATEWAY_NO_TAILSCALE="1")
    for k in ("CC_GATEWAY_AUTH", "CC_GATEWAY_NO_AUTH"):
        env.pop(k, None)
    log = open(RIG / "gateway.out.txt", "w", encoding="utf-8")
    gw = subprocess.Popen([str(EXE), "--port", str(PORT)], env=env, stdout=log, stderr=subprocess.STDOUT,
                          cwd=str(EXE.parent))
    try:
        deadline = time.time() + 180
        while not list(ROOT.rglob("gateway.db")):
            if time.time() > deadline or gw.poll() is not None:
                raise SystemExit("the rig Gateway wrote no gateway.db")
            time.sleep(1)
        db = list(ROOT.rglob("gateway.db"))[0]
        wait_for_tables(db, ["device_credentials", "session_keys", "session_history", "factory_definition_versions"])
        enroll(db)
        wait_for_gateway()
        person = (PERSON_KEY, "a person: the rig's enrolled browser device")
        session = (SESSION_KEY, f"session {SESSION_ID}, of factory {FACTORY}")
        outsider = (OUTSIDER_KEY, f"session {OUTSIDER_ID}, of factory invoice-factory")
        TRANSCRIPT.append(f"# Gateway: {GW}, built from this tree; its storage root is <rig>\\root, made empty for this run\n")
        d = ["factory", "definition"]

        # ---- 1. The schemas, served.
        cli(person, [*d, "schemas", "--out", str(RIG / "schemas")], 0)

        # ---- 2. Nothing to export: no definition, not registered.
        cli(person, ["factory", "export", FACTORY, "--out", str(exports / "nothing")], 1)

        # ---- 3. A factory with NO definition, exported from what is live.
        folder = RIG / "qa-live-folder"
        folder.mkdir(parents=True)
        manifest = RIG / "qa-live.manifest.json"
        manifest.write_text(json.dumps({
            "factory": LIVE, "title": "QA Live", "folder": str(folder), "computer": "SOREN_NORTH", "bossSeat": "boss",
            "seats": [{"id": "boss", "name": "Boss", "role": "Boss", "briefFile": "BOSS.md", "schedules": []},
                      {"id": "writer", "name": "Writer", "role": "Writer", "briefFile": "WRITER.md", "schedules": []}],
        }, indent=2), encoding="utf-8")
        cli(person, ["factory", "register", "--manifest", str(manifest)], 0)
        brief = RIG / "boss-brief.md"
        brief.write_bytes(b"Run the factory.\nRead what the writer made and decide what is next.\n")
        cli(person, ["factory", "brief", "set", "--factory", LIVE, "--seat", "boss", "--file", str(brief)], 0)
        cli(person, ["factory", "brief", "publish", "--factory", LIVE, "--seat", "boss", "--version", "1"], 0)
        cli(person, ["schedule", "create", "--name", "QA Live boss morning", "--machine", "SOREN_NORTH",
                     "--repo", str(folder), "--cron", "0 8 * * 1-5", "--tz", "Europe/Copenhagen", "--seed", "Morning round.",
                     "--factory", LIVE, "--seat", "boss"], 0)
        cli(person, ["schedule", "create", "--name", "QA Live writer at random", "--machine", "SOREN_NORTH",
                     "--repo", str(folder), "--random", "09:00-17:00", "--per-day", "2", "--min-gap", "30",
                     "--tz", "Europe/Copenhagen", "--seed", "Write a post.", "--factory", LIVE, "--seat", "writer"], 0)
        cli(person, ["trigger", "add", "--name", "qa-live-new-mail", "--factory", LIVE, "--agent", "writer",
                     "--machine", "SOREN_NORTH", "--repo", str(folder), "--check", "python check_mail.py",
                     "--every", "10m", "--prompt", "Answer the {count} new mails.", "--paused"], 0)
        live_out = exports / "qa-live"
        cli(person, ["factory", "export", LIVE, "--out", str(live_out)], 0)
        live_once = hashes("qa-live, first export from what is live", live_out)
        cli(person, ["factory", "export", LIVE, "--out", str(exports / "qa-live-again")], 0)
        compare("qa-live first export", live_once, "qa-live second export",
                hashes("qa-live, second export from what is live", exports / "qa-live-again"))
        # Its folder fails the check: the writer has no published brief. Refused with file and line, nothing stored.
        cli(person, ["factory", "import", str(live_out)], 1)
        cli(person, [*d, "history", LIVE], 0)
        fixed = copy_with("qa-live-with-writer-brief", live_out,
                          [("seats/writer.yaml", 'brief: ""', "brief: |\n  Write one post a day.")])
        cli(person, ["factory", "import", fixed, "--note", "recreated from what was live"], 0)

        # ---- 4. Export, import into a new id, export again: the same bytes.
        cli(person, ["factory", "import", str(EXAMPLE), "--note", "the example"], 0)
        cli(person, [*d, "publish", FACTORY, "--version", "1"], 0)
        a = exports / "a"
        cli(person, ["factory", "export", FACTORY, "--out", str(a)], 0)
        ha = hashes(f"{FACTORY} version 1, first export", a)
        cli(person, ["factory", "export", FACTORY, "--out", str(exports / "a-again"), "--version", "1"], 0)
        compare("first export", ha, "second export", hashes(f"{FACTORY} version 1, second export", exports / "a-again"))
        compare("the example folder", hashes("the folder first imported", EXAMPLE), "first export", ha)
        renamed = copy_with("into-a-new-id", a, [("factory.yaml", f"factory: {FACTORY}\r\n" if b"\r\n" in (a / "factory.yaml").read_bytes() else f"factory: {FACTORY}\n",
                                                        f"factory: {COPY}\r\n" if b"\r\n" in (a / "factory.yaml").read_bytes() else f"factory: {COPY}\n")])
        h_renamed = hashes("the folder imported into the new id", renamed)
        cli(person, ["factory", "import", renamed], 0)
        b = exports / "b"
        cli(person, ["factory", "export", COPY, "--out", str(b)], 0)
        hb = hashes(f"{COPY} version 1, exported after the import", b)
        compare("the folder imported into the new id", h_renamed, f"the export of {COPY}", hb)
        compare(f"the first export of {FACTORY}", ha, f"the export of {COPY}", hb)

        # ---- 5. An import never publishes: the published version read before and after.
        cli(person, [*d, "get", FACTORY], 0)
        changed = copy_with("writer-renamed", a, [("seats/writer.yaml", "name: Writer", "name: Author")])
        cli(person, ["factory", "import", changed, "--note", "renamed the writer"], 0)
        cli(person, [*d, "get", FACTORY], 0)
        cli(person, [*d, "history", FACTORY], 0)

        # ---- 6. Who may import: a session of the factory may (a draft); a session of another factory may not.
        cli(session, ["factory", "import", str(a), "--note", "proposed by a session"], 0)
        cli(outsider, ["factory", "import", str(a)], 1)
        cli(person, [*d, "get", FACTORY], 0)

        # ---- 7. Refusals with file and line.
        cli(person, ["factory", "import", copy_with("unknown-key", a, [("seats/writer.yaml", "role: Writer", "role: Writer\ncolour: red")])], 1)
        # A session of qa-outreach importing a folder whose factory.yaml names another factory: refused by the caller gate.
        cli(session, ["factory", "import", renamed], 1)

        # ---- 8. A path mapped to null: a 400 naming the path, never a 500.
        raw("PUT", f"/factory-definitions/{FACTORY}/draft",
            '{"expectedVersion": 3, "content": {"factory.yaml": null, "seats/boss.yaml": "seat: boss"}}', 400)
        raw("POST", "/factory-definitions/import",
            '{"factory": "qa-outreach", "content": {"factory.yaml": "factory: qa-outreach", "seats/writer.yaml": null}}', 400)
        cli(person, [*d, "history", FACTORY, "--count", "1"], 0)
    finally:
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
