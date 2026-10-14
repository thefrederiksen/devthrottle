"""QA for Factory Definition, phase 1: the definition routes and the command line, against a throwaway Gateway.

Runs in the foreground and owns everything it starts, and stops it when the run ends, pass or fail:
- a Gateway (CcDirector.Gateway, Debug build of THIS tree) on port 7994 with its own storage root under %TEMP%
  (CC_DIRECTOR_ROOT), Tailscale off, authentication ON. It can touch neither the owner's Gateway nor any factory.

Who is asking, and how each caller was made (written into the rig Gateway's own gateway.db, the way the Factory
Manager rig in docs/qa/factory-control/factory-manager does):
- A PERSON: a browser device enrolled as a device_credentials row (a SHA-256 of its key). The command line sends that
  key as its bearer, so the Gateway reads the caller as a person's device.
- A SESSION OF THE FACTORY: a session_keys row (the hash of a fresh key, as a Director registers it) and a
  session_history row naming the factory qa-outreach - the record FactoryNaming.TryAct reads. The command line sends
  that session key exactly as a session's own cc-devthrottle does.
- A SESSION OF ANOTHER FACTORY: the same, with session_history naming invoice-factory.
No Director is connected, so every machine the definition names is reported as a notice, as the check is written to.

Every command is the real cc-devthrottle from this tree. What each printed, and its exit code, is written to
transcript.txt beside this script's output folder, exactly as printed.

Usage (from the repository root, after `dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj`):
    uv run --no-project --with "typer>=0.16.1" --with "click>=8.2.1" --with "rich>=13.0.0" --with "requests>=2.31.0" \
        python docs/qa/factory-definition/phase-1/rig/run_qa.py <out dir> [--phase0]
--phase0 adds the two refusals the phase 0 follow-up (pull request 4003) brings: a Codex seat naming a factory skill,
and a Codex seat carrying a rule set.
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
EXAMPLE = HERE.parent / "example"
EXE = REPO / "src" / "CcDirector.Gateway" / "bin" / "Debug" / "net10.0" / "CcDirector.Gateway.exe"
CLI_DIR = REPO / "tools" / "cc-devthrottle"
PORT = 7994
GW = f"http://127.0.0.1:{PORT}"
FACTORY = "qa-outreach"
RIG = Path(os.environ["TEMP"]) / "factory-definition-p1-qa"
ROOT = RIG / "root"
NOW = datetime.now(timezone.utc).replace(tzinfo=None)

PERSON_KEY = "qa-person-" + uuid.uuid4().hex
SESSION_ID = str(uuid.uuid4())
SESSION_KEY = "qa-session-" + uuid.uuid4().hex
OUTSIDER_ID = str(uuid.uuid4())
OUTSIDER_KEY = "qa-session-" + uuid.uuid4().hex


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
    tenant = "tenant_id"
    insert(c, "device_credentials", {
        "DeviceId": "qa-browser", "MachineName": "QA", "DeviceKeyHash": sha(PERSON_KEY), "KeyPrefix": PERSON_KEY[:8],
        "KeyLast4": PERSON_KEY[-4:], "IssuedAtUtc": NOW.strftime("%Y-%m-%d %H:%M:%S"), "Status": "active",
        "Platform": "windows", "DeviceType": "browser", "TenantId": "local"})
    expires = (NOW + timedelta(hours=6)).strftime("%Y-%m-%d %H:%M:%S")
    for sid, key, factory in ((SESSION_ID, SESSION_KEY, FACTORY), (OUTSIDER_ID, OUTSIDER_KEY, "invoice-factory")):
        insert(c, "session_keys", {"SessionId": sid, "TenantId": "local", "DirectorId": "qa-director",
                                   "KeyHash": sha(key), "IssuedAtUtc": NOW.strftime("%Y-%m-%d %H:%M:%S"),
                                   "ExpiresAtUtc": expires})
        insert(c, "session_history", {tenant: "local", "SessionId": sid, "DirectorId": "qa-director",
                                      "Factory": factory, "SessionName": f"QA - {factory} session",
                                      "StartedAtUtc": NOW.strftime("%Y-%m-%d %H:%M:%S"),
                                      "LastSeenUtc": NOW.strftime("%Y-%m-%d %H:%M:%S")})
    c.commit()
    c.close()


def wait_for_gateway():
    deadline = time.time() + 180
    while time.time() < deadline:
        try:
            req = urllib.request.Request(GW + f"/factory-definitions/{FACTORY}/history",
                                         headers={"Authorization": f"Bearer {PERSON_KEY}"})
            with urllib.request.urlopen(req, timeout=5) as res:
                if res.status == 200:
                    return
        except (urllib.error.URLError, ConnectionError, TimeoutError):
            pass
        time.sleep(1)
    raise SystemExit("the rig Gateway did not answer the definition history route within 180s")


TRANSCRIPT = []


def cli(who, args, expect_exit):
    """Run this tree's cc-devthrottle as `who` and keep exactly what it printed."""
    key, label = who
    env = dict(os.environ, CC_GATEWAY_URL=GW, CC_GATEWAY_SESSION_KEY=key, NO_COLOR="1", COLUMNS="200")
    env.pop("CC_SESSION_ID", None)
    launch = ("import sys; sys.path[:0] = [sys.argv[1], sys.argv[2]]; sys.argv = ['cc-devthrottle'] + sys.argv[3:]; "
              "from src.cli import tool_main; tool_main()")
    r = subprocess.run([sys.executable, "-c", launch, str(CLI_DIR), str(CLI_DIR.parent), *args], cwd=str(CLI_DIR),
                       env=env, capture_output=True, text=True, timeout=120)
    shown = " ".join(a if " " not in a else f'"{a}"' for a in args)
    TRANSCRIPT.append(f"$ cc-devthrottle {shown}      (as {label})\n{r.stdout}{r.stderr}[exit {r.returncode}]\n")
    print(f"cli {shown} as {label}: exit {r.returncode}", flush=True)
    if r.returncode != expect_exit:
        raise SystemExit(f"expected exit {expect_exit} from: cc-devthrottle {shown}\n{r.stdout}{r.stderr}")
    return r


def variant(name, edits=(), add=None):
    """A copy of the example with exact text edits ((path, old, new), each old must occur once) and added files."""
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
    TRANSCRIPT.append(f"# variant '{name}': the example with " + "; ".join(
        [f"{p}: {o!r} -> {n!r}" for p, o, n in edits] + [f"{p} added: {t!r}" for p, t in (add or {}).items()]) + "\n")
    return str(target)


def main():
    out = Path(sys.argv[1]).resolve()
    phase0 = "--phase0" in sys.argv[2:]
    out.mkdir(parents=True, exist_ok=True)
    if not EXE.exists():
        raise SystemExit(f"build the Gateway first: {EXE} does not exist")
    if RIG.exists():
        shutil.rmtree(RIG)
    ROOT.mkdir(parents=True)
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
        TRANSCRIPT.append(f"# Gateway: {GW}, built from this tree; storage root {ROOT}\n")

        d = "factory definition"
        cli(person, [*d.split(), "history", FACTORY], 0)
        # Draft, publish.
        cli(person, [*d.split(), "save", FACTORY, "--dir", str(EXAMPLE), "--expected-version", "0",
                     "--note", "the first definition"], 0)
        cli(person, [*d.split(), "publish", FACTORY, "--version", "1"], 0)
        # A second version: the writer is renamed and its brief changes, the boss's start moves an hour.
        v2 = variant("second-version", [
            ("seats/writer.yaml", "name: Writer", "name: Author"),
            ("seats/writer.yaml", "Write one post from the template.", "Write one post from the template, and link the last one."),
            ("seats/boss.yaml", '"0 8 * * 1-5"', '"0 9 * * 1-5"'),
        ])
        cli(person, [*d.split(), "save", FACTORY, "--dir", v2, "--expected-version", "1", "--note", "the second definition"], 0)
        cli(person, [*d.split(), "diff", FACTORY], 0)
        cli(person, [*d.split(), "publish", FACTORY, "--version", "2"], 0)
        # Restore the first.
        cli(person, [*d.split(), "restore", FACTORY, "--version", "1", "--expected-version", "2"], 0)
        cli(person, [*d.split(), "history", FACTORY], 0)
        cli(person, [*d.split(), "get", FACTORY], 0)
        cli(person, [*d.split(), "get", FACTORY, "--file", "seats/writer.yaml"], 0)
        cli(person, [*d.split(), "diff", FACTORY, "--from", "2", "--to", "3"], 0)

        # A stale save: the writer read version 1, the definition is at 3.
        cli(person, [*d.split(), "save", FACTORY, "--dir", v2, "--expected-version", "1"], 1)

        # A session of the factory may save a draft, and may not publish or restore.
        cli(session, [*d.split(), "save", FACTORY, "--dir", v2, "--expected-version", "3", "--note", "proposed by a session"], 0)
        cli(session, [*d.split(), "publish", FACTORY, "--version", "4"], 1)
        cli(session, [*d.split(), "restore", FACTORY, "--version", "2", "--expected-version", "4"], 1)
        # A session of another factory reaches nothing.
        cli(outsider, [*d.split(), "get", FACTORY, "--draft"], 1)
        cli(outsider, [*d.split(), "save", FACTORY, "--dir", str(EXAMPLE), "--expected-version", "4"], 1)

        # The check refuses, with file and line, and nothing is stored.
        cli(person, [*d.split(), "save", FACTORY, "--dir", variant("unknown-key", [
            ("seats/writer.yaml", "role: Writer\n", "role: Writer\ncolour: red\n")]), "--expected-version", "4"], 1)
        cli(person, [*d.split(), "save", FACTORY, "--dir", variant("dangling-reference", [
            ("seats/writer.yaml", "files: [files/post-template.md]", "files: [files/post-template.md, files/missing.md]")]),
            "--expected-version", "4"], 1)
        cli(person, [*d.split(), "save", FACTORY, "--dir", variant("skill-name-clash", add={
            "skills/dev-throttle/SKILL.md": "---\nname: dev-throttle\ndescription: Our own notes on the product.\n---\nRead these.\n"}),
            "--expected-version", "4"], 1)
        cli(person, [*d.split(), "save", FACTORY, "--dir", variant("factory-skill-on-codex", [
            ("seats/writer.yaml", "role: Writer\n", "role: Writer\nagent: Codex\n"),
            ("seats/writer.yaml", "  fleet: [dev-throttle]\n", "  fleet: [dev-throttle]\n  factory: [house-style]\n")],
            add={"skills/house-style/SKILL.md": "---\nname: house-style\ndescription: How this factory writes.\n---\nWrite plainly.\n"}),
            "--expected-version", "4"], 1)
        if phase0:
            cli(person, [*d.split(), "save", FACTORY, "--dir", variant("rules-on-codex", [
                ("seats/boss.yaml", "role: Boss\n", "role: Boss\nagent: Codex\n")]), "--expected-version", "4"], 1)
        cli(person, [*d.split(), "history", FACTORY, "--count", "2"], 0)
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
