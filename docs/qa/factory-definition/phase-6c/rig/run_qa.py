"""QA for Factory Definition, phase 6c: a session in no factory creates and publishes a factory from the command line.

The owner's ruling of 16 October 2026: "Factories are created by agents as long as they have user approval." So a plain
session - one in no factory - takes Eric's folder and, with cc-devthrottle alone, creates the factory, reads what
publishing it would do, and publishes it; the factory then stands on the list with its daily 08:00 schedule. Beside it,
the refusals the ruling keeps: a session of a factory may not create a factory or publish one, and a second create of
an id that is taken is refused with the command to run instead.

It reuses the phase 5b rig (../../phase-5b/rig/run_qa.py) for the Gateway and the person, on its own port and its own
storage root: a Gateway (Debug build of THIS tree) on port 7989, storage root under %TEMP%, Tailscale off,
authentication ON, factory agents on in the rig's OWN config.json. No Director is started: a publish of Eric's factory
asks no machine anything (it has no secret and no repository), and "No Director on <machine> is connected now" is a
notice, never a refusal. So nothing is launched, and no scheduled task is read or changed.

Who is asking, each written into the rig Gateway's own gateway.db as the phase 1 rig does:
- A PERSON: an enrolled browser device. It only READS here: the Factories list and the schedules, at the end.
- THE BUILDER: a session in no factory (a session_keys row and a session_history row with no factory), named
  "QA - builder, in no factory". Every write of the flow is its.
- A SESSION OF ERIC: the same, with session_history naming the factory eric.
- A SESSION OF ANOTHER FACTORY: the same, naming qa-other.

The folder is NOT in this repository (it is kept with the mission's private briefs); the rig takes its path.

Usage (from the repository root, after building the Gateway):
    dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
    uv run --no-project --with "typer>=0.16.1" --with "click>=8.2.1" --with "rich>=13.0.0" --with "requests>=2.31.0" \\
        python docs/qa/factory-definition/phase-6c/rig/run_qa.py <eric folder> <out dir>
"""

import hashlib
import importlib.util
import json
import os
import sqlite3
import subprocess
import sys
import traceback
import urllib.error
import uuid
from pathlib import Path

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("p5b", HERE.parents[1] / "phase-5b" / "rig" / "run_qa.py")
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)

FOLDER = Path(sys.argv[1]).resolve()
OUT = Path(sys.argv[2]).resolve()
p.PORT = 7989
p.GW = f"http://127.0.0.1:{p.PORT}"
p.FACTORY = "eric"
p.RIG = Path(os.environ["TEMP"]) / "factory-definition-p6c-agents"
p.GW_ROOT = p.RIG / "gateway-root"

BUILDER_ID = str(uuid.uuid4())
BUILDER_KEY = "qa-session-" + uuid.uuid4().hex
BUILDER_NAME = "QA - builder, in no factory"
ERIC_SEAT_ID = str(uuid.uuid4())
ERIC_SEAT_KEY = "qa-session-" + uuid.uuid4().hex
OTHER_SEAT_ID = str(uuid.uuid4())
OTHER_SEAT_KEY = "qa-session-" + uuid.uuid4().hex

_shown = p.shown


def shown(text):
    """The phase 5b masking, and the private folder's path as <eric folder>."""
    text = str(text)
    for form in (str(FOLDER), str(FOLDER).replace("\\", "/"), json.dumps(str(FOLDER))[1:-1]):
        text = text.replace(form, "<eric folder>")
    return _shown(text)


p.shown = shown


def enroll_sessions(db):
    """Three sessions, each a session_keys row (the hash of a fresh key, as a Director registers it) and a
    session_history row - the record the Gateway reads to know which factory a session is in, and its name."""
    c = sqlite3.connect(db)
    expires = p.NOW.replace(year=p.NOW.year + 1)
    for sid, key, factory, name in ((BUILDER_ID, BUILDER_KEY, None, BUILDER_NAME),
                                    (ERIC_SEAT_ID, ERIC_SEAT_KEY, "eric", "QA - a session of eric"),
                                    (OTHER_SEAT_ID, OTHER_SEAT_KEY, "qa-other", "QA - a session of qa-other")):
        p.insert(c, "session_keys", {"SessionId": sid, "TenantId": "local", "DirectorId": "qa-director",
                                     "KeyHash": p.sha(key), "IssuedAtUtc": p.stamp(p.NOW), "ExpiresAtUtc": p.stamp(expires)})
        history = {"tenant_id": "local", "SessionId": sid, "DirectorId": "qa-director", "SessionName": name,
                   "StartedAtUtc": p.stamp(p.NOW), "LastSeenUtc": p.stamp(p.NOW)}
        if factory is not None:
            history["Factory"] = factory
        p.insert(c, "session_history", history)
    c.commit()
    c.close()


def gateway_serving():
    try:
        p.get("/factory-definition-schemas", key="qa-not-enrolled")
        return True
    except urllib.error.HTTPError as exc:
        return exc.code in (401, 403)


def read(title, path):
    """One read as the person, kept as answered."""
    answer = p.get(path)
    p.record(title, f"  GET {path}\n" + "".join(f"  {line}\n" for line in json.dumps(answer, indent=2).splitlines()))
    return answer


def check(ok, what, seen):
    p.record(("PASS " if ok else "FAIL ") + what, f"  seen: {seen}")
    if not ok:
        raise SystemExit(f"FAIL {what}: {seen}")


def drive():
    d = ["factory", "definition"]
    builder = dict(key=BUILDER_KEY, label=f"the builder: session {BUILDER_ID}, in no factory")
    eric_seat = dict(key=ERIC_SEAT_KEY, label=f"session {ERIC_SEAT_ID}, of factory eric")
    other_seat = dict(key=OTHER_SEAT_KEY, label=f"session {OTHER_SEAT_ID}, of factory qa-other")

    p.note("STEP 1 - a session of another factory may not create a factory.")
    refused = p.cli([*d, "create", str(FOLDER)], 1, **other_seat)
    check("may not create a new factory" in refused.stderr, "a factory's own session is refused create in words",
          refused.stderr.strip().splitlines()[0])

    p.note("STEP 2 - the builder, a session in no factory, creates Eric's factory from the folder.")
    created = json.loads(p.cli([*d, "create", str(FOLDER), "--note", "Eric's factory, made by a session", "--json"], 0,
                               **builder).stdout)
    check(created.get("version") == 1 and created.get("state") == "draft" and created.get("created") is True,
          "version 1 stored as a draft, the factory's first", {k: created.get(k) for k in ("version", "state", "created")})

    p.note("STEP 3 - what publishing it would do.")
    p.cli([*d, "diff", "eric", "--to", "1"], 0, **builder)

    p.note("STEP 4 - a session of eric may save a draft of its own factory but not publish it.")
    p.cli([*d, "publish", "eric", "--version", "1", "--replaces", "0"], 1, **eric_seat)

    p.note("STEP 5 - the builder publishes it.")
    published = json.loads(p.cli([*d, "publish", "eric", "--version", "1", "--replaces", "0", "--json"], 0,
                                 **builder).stdout)
    check(published.get("publishedVersion") == 1 and published.get("publishedByKind") == "session"
          and published.get("publishedBy") == BUILDER_ID and published.get("publishedByName") == BUILDER_NAME,
          "version 1 published, recorded as the builder's by its id and name",
          {k: published.get(k) for k in ("publishedVersion", "publishedByKind", "publishedBy", "publishedByName",
                                         "publishedByText")})
    p.record("what the publish applied, as the Gateway wrote it", "".join(f"  {s}\n" for s in published.get("applied") or []))

    p.note("STEP 6 - the history and the published version say who published it.")
    p.cli([*d, "history", "eric"], 0, **builder)
    p.cli([*d, "get", "eric"], 0, **builder)

    p.note("STEP 7 - a second create of the same id is refused with what to run instead.")
    taken = p.cli([*d, "create", str(FOLDER)], 1, **builder)
    check("already exists" in taken.stderr and "Nothing was created" in taken.stderr,
          "a create of an id that is taken is refused in words", taken.stderr.strip().splitlines()[0])

    p.note("STEP 8 - a session of eric still saves a draft of its own factory, and still may not publish it.")
    p.cli([*d, "save", "eric", "--dir", str(FOLDER), "--expected-version", "1", "--note", "a draft from eric's own session"],
          0, **eric_seat)
    refused = p.cli([*d, "publish", "eric", "--version", "2"], 1, **eric_seat)
    check("may not publish a factory's definition" in refused.stderr, "a factory's own session is refused publish in words",
          refused.stderr.strip().splitlines()[0])

    p.note("STEP 9 - the factory on the list, with its schedule, read by the builder from the command line.")
    p.cli(["factory", "list"], 0, **builder)
    p.cli(["schedule", "list"], 0, **builder)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for needed in (p.EXE, FOLDER / "factory.yaml"):
        if not needed.exists():
            raise SystemExit(f"build it first, or name the folder: {needed} does not exist")
    if p.RIG.exists() and p.rig_processes():
        raise SystemExit(f"processes still run from {p.RIG}: {p.rig_processes()}; wait for them to end")
    if p.RIG.exists():
        p.remove_tree(p.RIG)
    p.GW_ROOT.mkdir(parents=True)
    (p.GW_ROOT / "config").mkdir()
    (p.GW_ROOT / "config" / "config.json").write_text('{ "factoryAgents": { "enabled": true } }\n', encoding="utf-8")
    files = sorted(f for f in FOLDER.rglob("*") if f.is_file())
    p.record("the folder (private; its files' sizes and hashes only)", "".join(
        f"  {f.relative_to(FOLDER).as_posix()}  {f.stat().st_size} bytes  sha256 {hashlib.sha256(f.read_bytes()).hexdigest()}\n"
        for f in files))
    env = dict(os.environ, CC_DIRECTOR_ROOT=str(p.GW_ROOT), CC_GATEWAY_NO_TAILSCALE="1")
    for k in ("CC_GATEWAY_AUTH", "CC_GATEWAY_NO_AUTH", "REPORT_SERVICE_TOKEN", "NOTIFY_OWNER_SERVICE_TOKEN"):
        env.pop(k, None)
    log = open(p.RIG / "gateway.out.txt", "w", encoding="utf-8")
    gw = subprocess.Popen([str(p.EXE), "--port", str(p.PORT)], env=env, stdout=log, stderr=subprocess.STDOUT,
                          cwd=str(p.EXE.parent))
    try:
        p.wait_for("the rig Gateway wrote no gateway.db",
                   lambda: bool(list(p.GW_ROOT.rglob("gateway.db"))) or gw.poll() is not None)
        if gw.poll() is not None:
            raise SystemExit("the rig Gateway exited; see gateway.out.txt")
        db = list(p.GW_ROOT.rglob("gateway.db"))[0]
        p.wait_for("the rig gateway.db did not get its tables", lambda: {"device_credentials", "session_keys", "session_history"} <=
                   {r[0] for r in p.query(db, "SELECT name FROM sqlite_master WHERE type='table'")})
        p.wait_for("the rig Gateway did not finish starting", gateway_serving, seconds=600, every=2)
        p.enroll(db)
        enroll_sessions(db)
        p.wait_for("the rig Gateway did not answer", lambda: p.get("/factory-definition-schemas") is not None)
        p.note(f"Gateway: {p.GW}, built from this tree, storage root <rig>\\gateway-root made empty for this run. No Director.")
        read("before: the Factories list", "/gateway/factories")
        drive()
        factories = read("after: the Factories list, as the person's screen reads it", "/gateway/factories")
        jobs = read("after: the schedules", "/cron/jobs")
        listed = [(r.get("id"), r.get("title"), r.get("statusWord")) for r in factories.get("rows", [])]
        check(("eric", "Eric", "RUNNING") in listed, "eric is on the Factories list, running", listed)
        eric_jobs = [j for j in (jobs if isinstance(jobs, list) else jobs.get("jobs", [])) if j.get("factory") == "eric"]
        check(len(eric_jobs) == 1 and eric_jobs[0].get("cronExpression", eric_jobs[0].get("cron")) == "0 8 * * *",
              "eric has one schedule, daily at 08:00",
              [{k: j.get(k) for k in ("name", "cronExpression", "cron", "timeZone", "enabled")} for j in eric_jobs])
        p.rows(db, "the definition's versions, as stored (who wrote and who published each)",
               "SELECT Version, State, AuthorKind, AuthorId, PublishedByKind, PublishedBy, PublishedByName "
               "FROM factory_definition_versions WHERE Factory = 'eric' ORDER BY Version")
    except BaseException:
        traceback.print_exc()
        raise
    finally:
        gw.terminate()
        try:
            gw.wait(timeout=30)
        except subprocess.TimeoutExpired:
            gw.kill()
        log.close()
        (OUT / "transcript.txt").write_text("\n".join(p.TRANSCRIPT), encoding="utf-8", newline="\n")
        print(f"wrote {OUT / 'transcript.txt'}")


if __name__ == "__main__":
    main()
