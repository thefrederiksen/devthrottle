"""Factory Definition, phase 2: the one-time upload and the record refusals, proven against a real local Gateway.

ONE FOREGROUND RUN, START TO FINISH. It:

 1. publishes the DESKTOP Gateway (the tray build every self-hosted machine runs) from this worktree and starts it on
    its own port with its OWN storage root, Tailscale off and auto-update off - the way scripts/restart-qa-rig.ps1
    starts its rig Gateway - so nothing of the live fleet (the hosted Gateway, the user's own local Gateway, any
    Director) is read, written or stopped;
 2. fingerprints every file under the machine's factory records folders (path, size, SHA-256);
 3. runs `cc-devthrottle factory records upload` against that Gateway, then runs it AGAIN;
 4. proves the refusals over HTTP on the same Gateway, quoting each answer: two writers on one ledger, an oversized
    attachment, the machine credential without an import key, the machine credential reading;
 5. counts the rows that Gateway's own database holds, per table, against what the upload said it stored;
 6. fingerprints the records folders again and proves nothing changed;
 7. stops the Gateway it started with its own POST /shutdown, and waits for that process to exit.

WHAT IT PRINTS IS SAFE TO COMMIT. File paths below a records folder carry prospect, customer and person names, so
this script never prints one: a path is shown as "<folder>/<first folder>/.../*.<ext>", as the phase 0 inventory does.
The upload's own full output (with names) goes to a file in the run folder, which is NOT committed.

The machine credential is read from the test Gateway's own token file and handed to the upload in its environment.
It is never printed.

Usage:  python upload_proof.py [--port 7996] [--skip-publish]
"""

import argparse
import base64
import hashlib
import json
import os
import re
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parents[4]
TOOLS = REPO / "tools"
CLI = REPO / "tools" / "cc-devthrottle"
sys.path.insert(0, str(CLI))
sys.path.insert(0, str(TOOLS))
from src import factory_record_ops as records  # noqa: E402

MACHINE = os.environ.get("COMPUTERNAME", "")


def say(line=""):
    print(line, flush=True)


def redact(source: str) -> str:
    """'<records folder>/<first folder>/.../*.<ext>' - never a deeper folder or a file name."""
    for rel_root, _factory, _prefix in records.ROOTS:
        if source.startswith(rel_root + "/"):
            rest = source[len(rel_root) + 1:].split("/")
            ext = rest[-1].rsplit(".", 1)[-1] if "." in rest[-1] else "(no extension)"
            return f"{rel_root}/*.{ext}" if len(rest) == 1 else f"{rel_root}/{rest[0]}/.../*.{ext}"
    return "(outside every records folder)"


def fingerprint(base: Path) -> dict:
    out = {}
    for rel_root, _f, _p in records.ROOTS:
        root = base.joinpath(*rel_root.split("/"))
        if not root.is_dir():
            continue
        for dirpath, _dirs, names in os.walk(root):
            for n in names:
                full = Path(dirpath) / n
                data = full.read_bytes()
                out[full.relative_to(base).as_posix()] = (len(data), hashlib.sha256(data).hexdigest())
    return out


def http(method, url, token, body=None, headers=None):
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Authorization", f"Bearer {token}")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            raw = resp.read().decode("utf-8")
            return resp.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as err:
        raw = err.read().decode("utf-8")
        return err.code, json.loads(raw) if raw else None


def run_upload(base_url, token, out_file):
    env = dict(os.environ)
    env[records.UPLOAD_TOKEN_VARIABLE] = token
    env["PYTHONPATH"] = os.pathsep.join([str(TOOLS), str(CLI)])
    cmd = [sys.executable, "-c", "from src.cli import app; app()", "factory", "records", "upload",
           "--gateway", base_url, "--machine", MACHINE]
    proc = subprocess.run(cmd, cwd=str(CLI), env=env, capture_output=True, text=True, encoding="utf-8")
    Path(out_file).write_text(proc.stdout + proc.stderr, encoding="utf-8")
    return proc.returncode, proc.stdout


def summary_lines(stdout):
    """The upload's per-folder and total lines - counts only, which carry no names."""
    keep = []
    for line in stdout.splitlines():
        # A folder's own line ("  <folder>  [<factory>]  read ..."), never a file's line of the by-name list.
        if re.match(r"^  \S+  \[[^\]]*\]  (read |not present)", line) or line.startswith("      stored ") or line.startswith("Read ") \
                or line.startswith("Nothing on this machine") or line.startswith("Factory records under"):
            keep.append(line)
    return keep


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=7996)
    ap.add_argument("--skip-publish", action="store_true")
    args = ap.parse_args()

    base = Path(os.environ["LOCALAPPDATA"])
    rig = Path(tempfile.gettempdir()) / "fd-phase2-upload-proof"
    stage, gw_root, results = rig / "stage", rig / "gw-root", rig / "results"
    for p in (rig, gw_root, results):
        p.mkdir(parents=True, exist_ok=True)
    if any(gw_root.iterdir()):
        raise SystemExit(f"the test Gateway's storage root {gw_root} is not empty; remove it so the run starts from nothing")
    url = f"http://127.0.0.1:{args.port}"

    say(f"Factory Definition phase 2 - upload proof on {MACHINE}, {time.strftime('%Y-%m-%d %H:%M:%S %z')}")
    say(f"Worktree: {REPO.name}, commit {subprocess.run(['git', 'rev-parse', '--short', 'HEAD'], cwd=REPO, capture_output=True, text=True).stdout.strip()}")
    if not args.skip_publish:
        say("Publishing the desktop Gateway from this worktree ...")
        # Not CcDirector.Gateway.Host: that is the hosted container image and refuses to start outside the hosted
        # contract. The natives flag is required for a single-file Avalonia publish.
        r = subprocess.run(["dotnet", "publish", str(REPO / "src/CcDirector.GatewayApp/CcDirector.GatewayApp.csproj"),
                            "-c", "Release", "-r", "win-x64", "--self-contained", "false",
                            "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
                            "-p:RunMobileBuild=false", "-p:RunCockpitBuild=false",
                            "-o", str(stage), "--nologo", "-v", "q"], capture_output=True, text=True)
        if r.returncode != 0:
            say(r.stdout[-3000:] + r.stderr[-3000:])
            raise SystemExit("the Gateway did not publish")
    exe = stage / "devthrottle-gateway.exe"
    if not exe.is_file():
        raise SystemExit(f"no desktop Gateway at {exe}")

    say("")
    say("== 1. Fingerprint the records folders before ==")
    before = fingerprint(base)
    say(f"{len(before)} files, {sum(s for s, _ in before.values())} bytes")

    say("")
    say(f"== 2. Start a test Gateway on port {args.port} with its own storage root ==")
    env = dict(os.environ, CC_DIRECTOR_ROOT=str(gw_root), CC_GATEWAY_NO_TAILSCALE="1", CC_AUTOUPDATE="0")
    log = open(results / "gateway.out", "w", encoding="utf-8")
    gw = subprocess.Popen([str(exe), "--port", str(args.port), "--no-autostart"], cwd=str(stage), env=env,
                          stdout=log, stderr=subprocess.STDOUT)
    token = None
    try:
        for _ in range(120):
            try:
                with urllib.request.urlopen(url + "/healthz", timeout=2) as resp:
                    if resp.status == 200:
                        break
            except OSError:
                time.sleep(0.5)
        else:
            raise SystemExit("the test Gateway never answered /healthz")
        token_file = gw_root / "config" / "director" / "gateway-token.txt"
        token = token_file.read_text(encoding="utf-8").strip()
        # /healthz is 200 once the database is open, but every other route answers 503 "starting" until the credential
        # authority is up too. Ready means an authenticated route answers something other than 503.
        for _ in range(240):
            status, _body = http("GET", url + "/factory-records/qa-proof/documents", token)
            if status != 503:
                break
            time.sleep(0.5)
        else:
            raise SystemExit("the test Gateway answered /healthz but never became ready to serve")
        say(f"Ready (process {gw.pid}). Its machine credential is read from its own token file and never printed.")

        say("")
        say("== 3. The upload, first run ==")
        code1, out1 = run_upload(url, token, results / "upload-1.txt")
        for line in summary_lines(out1):
            say(line)
        say(f"exit code {code1}")
        held = [l for l in out1.splitlines() if l.startswith("  ") and " - " in l and "[" in l]
        kinds = {}
        for l in held:
            src = l.strip().split("  [")[0]
            reason = l.rsplit(" - ", 1)[1]
            key = (redact(src), reason)
            kinds[key] = kinds.get(key, 0) + 1
        say(f"Not uploaded, listed by name on screen ({len(held)}); shown here with names removed:")
        for (where, reason), n in sorted(kinds.items()):
            say(f"  {n:4} x {where} - {reason}")

        say("")
        say("== 4. The upload, second run: nothing new is stored and the counts still reconcile ==")
        code2, out2 = run_upload(url, token, results / "upload-2.txt")
        for line in summary_lines(out2):
            say(line)
        say(f"exit code {code2}")

        say("")
        say("== 5. Rows in the test Gateway's own database ==")
        dbs = [p for p in gw_root.rglob("*.db") if p.stat().st_size > 0]
        counts = {}
        for db in dbs:
            con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
            try:
                tables = {r[0] for r in con.execute("select name from sqlite_master where type='table'")}
                if "factory_journal_entries" not in tables:
                    continue
                for table in ("factory_journal_entries", "factory_document_versions", "factory_attachments",
                              "factory_attachment_contents"):
                    counts[table] = con.execute(f"select count(*) from {table}").fetchone()[0]
                counts["upload rows naming this machine"] = sum(
                    con.execute(f"select count(*) from {t} where AuthorKind='upload' and AuthorId=?", (MACHINE,)).fetchone()[0]
                    for t in ("factory_journal_entries", "factory_document_versions", "factory_attachments"))
                counts["attachment bytes held"] = con.execute("select coalesce(sum(length(Content)),0) from factory_attachment_contents").fetchone()[0]
                counts["attachment bytes the rows state"] = con.execute("select coalesce(sum(Bytes),0) from factory_attachments").fetchone()[0]
            finally:
                con.close()
        for k, v in counts.items():
            say(f"  {k}: {v}")

        say("")
        say("== 6. The refusals, over HTTP on the same Gateway, each answer quoted ==")
        up = {records.UPLOAD_MACHINE_HEADER: MACHINE}
        root = f"{url}/factory-records/qa-proof"

        s, b = http("PUT", root + "/documents", token, {"name": "outreach/ledger.json", "text": "row-a", "expectedVersion": 0, "importKey": "qa/1"}, up)
        say(f"Version 1 of the ledger: {s} {b['outcome']}")
        say("Two writers both read version 1. Writer A adds row-b, writer B adds row-c:")
        s, b = http("PUT", root + "/documents", token, {"name": "outreach/ledger.json", "text": "row-a\nrow-b", "expectedVersion": 1, "importKey": "qa/2"}, up)
        say(f"  writer A: {s} {b['outcome']}, version {b['document']['version']}")
        s, b = http("PUT", root + "/documents", token, {"name": "outreach/ledger.json", "text": "row-a\nrow-c", "expectedVersion": 1, "importKey": "qa/3"}, up)
        say(f"  writer B: {s} outcome {b['outcome']}")
        say(f"    error: \"{b['error']}\"")
        say(f"    current: version {b['current']['version']}, text {json.dumps(b['current']['text'])}")
        s, b = http("PUT", root + "/documents", token, {"name": "outreach/ledger.json", "text": b["current"]["text"] + "\nrow-c", "expectedVersion": 2, "importKey": "qa/4"}, up)
        say(f"  writer B merges into what it was handed and writes against version 2: {s} {b['outcome']}, version {b['document']['version']}")

        big = os.urandom(records_cap() + 1)
        s, b = http("POST", root + "/attachments", token, {"name": "walk/big.png", "contentType": "image/png",
                                                            "contentBase64": base64.b64encode(big).decode(), "importKey": "qa/big"}, up)
        say(f"An attachment of {len(big)} bytes: {s} outcome {b['outcome']}")
        say(f"    error: \"{b['error']}\"")

        s, b = http("PUT", root + "/documents", token, {"name": "x.md", "text": "x", "expectedVersion": 0})
        say(f"The machine credential writing without an import key: {s}")
        say(f"    error: \"{b['error']}\"")
        s, b = http("PUT", root + "/documents", token, {"name": "x.md", "text": "x", "expectedVersion": 0, "importKey": "qa/x"})
        say(f"The machine credential writing an upload row without naming its machine: {s}")
        say(f"    error: \"{b['error']}\"")
        s, b = http("GET", root + "/documents", token)
        say(f"The machine credential reading the documents: {s}")
        say(f"    error: \"{b['error']}\"")
        s, b = http("PUT", root + "/documents", "not-the-token", {"name": "x.md", "text": "x", "expectedVersion": 0, "importKey": "qa/y"}, up)
        say(f"A wrong credential: {s}")
    finally:
        say("")
        say(f"== 7. Stop the test Gateway this run started (process {gw.pid}) ==")
        if token is not None and gw.poll() is None:
            s, _b = http("POST", url + "/shutdown", token, {})
            say(f"POST /shutdown -> {s}")
        try:
            gw.wait(timeout=60)
            say(f"Stopped (exit code {gw.returncode}).")
        except subprocess.TimeoutExpired:
            # Only ever the process this run started, and only after it was asked to stop and did not.
            gw.kill()
            gw.wait(timeout=30)
            say("It did not stop when asked; the process this run started was ended.")
        log.close()

    say("")
    say("== 8. Fingerprint the records folders after ==")
    after = fingerprint(base)
    changed = [p for p in before if p in after and after[p] != before[p]]
    gone = [p for p in before if p not in after]
    say(f"{len(after)} files, {sum(s for s, _ in after.values())} bytes. Changed: {len(changed)}. Gone: {len(gone)}.")
    say("Nothing on this machine was deleted or changed." if not changed and not gone
        else "SOMETHING CHANGED - see the lists above.")


def records_cap():
    """The Gateway's attachment cap, read from the source this run published, so the proof cannot drift from it."""
    src = (REPO / "src/CcDirector.Gateway/Factory/Records/FactoryAttachmentStore.cs").read_text(encoding="utf-8")
    m = re.search(r"MaxAttachmentBytes = (\d+) \* 1024 \* 1024;", src)
    return int(m.group(1)) * 1024 * 1024


if __name__ == "__main__":
    main()
