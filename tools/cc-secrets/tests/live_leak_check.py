"""The live leak check for issue #2889: real Chrome, a real Director-owned profile, a real transcript.

It runs in two separate invocations because the session transcript only records a command's output
after the command has returned:

  1. run     Makes secrets the caller never sees, stores them in a throwaway store, serves a local login
             site, and then uses them through every command an agent has - list, run (stdin, env and
             askpass), and login in the Director-owned browser: a one-page login, a two-step login, a
             wrong password, a form that will not submit (so the typed password has to be cleared), and a
             tab on a domain the entry does not allow. After each login it takes what an agent can take:
             browser-harness page info, the page HTML, the page text, every input's value, the
             accessibility tree, and a screenshot. EVERYTHING it prints is what the agent would see, and
             it all lands in the transcript.
  2. verify  Searches the transcript, the recorded command output, the audit log, the tool log (which is
             where the debug-port traffic is logged), the browser-harness daemon log, the Director logs,
             and the text read out of every screenshot, for every form of every secret.

Neither step prints a secret, and verify refuses to call a search clean unless it first finds a planted
copy of the secret in each kind of file, and unless each file covers this run. Screenshots are read with
Windows' built-in text recognition; the control is a page that shows the secret in plain text, which the
recognition must read back before its silence about the real screenshots counts.

Not covered, stated plainly: network traffic leaving the machine (browser-harness telemetry, which sends
no page content), and Chrome's own "save this password?" prompt, which appears in the browser window
rather than the page and only saves when a person clicks it.

Windows only. Needs a running Director, a registered browser profile, and browser-harness.
"""

from __future__ import annotations

import argparse
import html
import json
import os
import secrets
import shutil
import subprocess
import sys
import threading
import time
import urllib.request
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

TESTS_DIR = Path(__file__).resolve().parent
TOOL_DIR = TESTS_DIR.parent
sys.path.insert(0, str(TOOL_DIR))
sys.path.insert(0, str(TESTS_DIR))

# Letters and digits a text recogniser does not confuse (no 0/O, 1/I/L, 2/Z, 5/S, 8/B).
OCR_SAFE = "ACDEFGHJKMNPQRTUVWXY34679"
USERNAME = "leak-user"


def ocr_safe_secret(length: int = 20) -> str:
    return "".join(secrets.choice(OCR_SAFE) for _ in range(length))


def say(text: str, out) -> None:
    # The console may be a Windows code page; browser-harness marks tabs with an emoji.
    print(text.encode("ascii", "backslashreplace").decode("ascii"), flush=True)
    out.write(text + "\n")
    out.flush()


# ------------------------------------------------------------------------------------------------------
# The local login site
# ------------------------------------------------------------------------------------------------------

def make_site(good_secret: str, marker: str):
    form = """<!doctype html><html><head><title>Leak check {title}</title></head><body style="font:20px sans-serif">
<h1>Leak check {title}</h1><p>{message}</p>
<form method="post" action="{action}" {extra}>
{fields}
<button type="submit">Sign in</button></form></body></html>"""
    user_field = '<p><label>User <input name="username" autocomplete="username"></label></p>'
    pass_field = '<p><label>Password <input type="password" name="password" autocomplete="current-password"></label></p>'

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def _send(self, status, body="", location=None):
            data = body.encode("utf-8")
            self.send_response(status)
            if location:
                self.send_header("Location", location)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def do_GET(self):
            url = urlsplit(self.path)
            query = parse_qs(url.query)
            if url.path == "/login":
                message = "Wrong password." if "error" in query else "Please sign in."
                self._send(200, form.format(title="login", message=message, action="/session", extra="",
                                            fields=user_field + pass_field))
            elif url.path == "/two-step":
                self._send(200, form.format(title="two-step", message="Step one.", action="/two-step-user",
                                            extra="", fields=user_field))
            elif url.path == "/two-step-password":
                hidden = f'<input type="hidden" name="username" value="{html.escape(query.get("u", [""])[0])}">'
                self._send(200, form.format(title="two-step password", message="Step two.", action="/session",
                                            extra="", fields=hidden + pass_field))
            elif url.path == "/stuck":
                self._send(200, form.format(title="stuck", message="This form never submits.", action="/session",
                                            extra='onsubmit="return false"', fields=user_field + pass_field))
            elif url.path == "/welcome":
                self._send(200, f"<!doctype html><title>Leak check welcome</title><h1>Signed in as {USERNAME}</h1><p>{marker}</p>")
            elif url.path == "/planted":
                self._send(200, "<!doctype html><title>Leak check control</title><body style='background:#fff'>"
                                f"<p style='font:bold 56px monospace;letter-spacing:6px;margin:40px'>{good_secret}</p>")
            else:
                self._send(404, "not found")

        def do_POST(self):
            length = int(self.headers.get("Content-Length", "0"))
            fields = parse_qs(self.rfile.read(length).decode("utf-8"))
            username = fields.get("username", [""])[0]
            password = fields.get("password", [""])[0]
            if self.path == "/two-step-user":
                self._send(303, location=f"/two-step-password?u={username}")
            elif self.path == "/session" and username == USERNAME and password == good_secret:
                self._send(303, location="/welcome")
            else:
                self._send(303, location="/login?error=1")

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


# ------------------------------------------------------------------------------------------------------
# run
# ------------------------------------------------------------------------------------------------------

def command(argv, out, env=None, stdin=None, timeout=180):
    say(f"$ {' '.join(argv[:1] + [a if len(a) < 120 else a[:117] + '...' for a in argv[1:]])}", out)
    # On Windows the fleet tools are .cmd shims, which CreateProcess only finds by their full name.
    resolved = shutil.which(argv[0])
    if resolved is None:
        raise SystemExit(f"{argv[0]} is not on PATH.")
    result = subprocess.run([resolved] + list(argv[1:]), input=stdin, capture_output=True, text=True, env=env, timeout=timeout,
                            encoding="utf-8", errors="replace")
    say((result.stdout + result.stderr).rstrip() + f"\n(exit {result.returncode})", out)
    return result


def harness(script: str, out, env) -> subprocess.CompletedProcess:
    exe = shutil.which("browser-harness")
    if exe is None:
        raise SystemExit("browser-harness is not on PATH.")
    return command([exe], out, env=env, stdin=script)


def run(args) -> int:
    workdir = Path(args.workdir).resolve()
    if workdir.exists() and any(workdir.iterdir()):
        raise SystemExit(f"{workdir} is not empty; give run a fresh folder.")
    workdir.mkdir(parents=True, exist_ok=True)
    home = workdir / "secrets-home"
    os.environ["CC_SECRETS_HOME"] = str(home)

    from src import paths
    from src.store import SecretStore, make_entry
    from src.storefile import UserOnlyFile

    run_id = "live-" + secrets.token_hex(6)
    started = datetime.now(timezone.utc)
    good, wrong, kept = ocr_safe_secret(), ocr_safe_secret(), ocr_safe_secret()
    store = SecretStore(UserOnlyFile(paths.store_path()))
    store.put(make_entry("leak-good", USERNAME, good, ["127.0.0.1"], run_id, True, ["login", "run"]))
    store.put(make_entry("leak-wrong", USERNAME, wrong, ["127.0.0.1"], run_id, True, ["login"]))
    store.put(make_entry("leak-kept", USERNAME, kept, ["127.0.0.1"], run_id, False, ["login", "run"]))

    site = make_site(good, run_id)
    port = site.server_address[1]
    base = f"http://127.0.0.1:{port}"
    shots = workdir / "screenshots"
    shots.mkdir()
    output_path = workdir / "run-output.txt"

    cc = shutil.which("cc-secrets", path=str(Path(sys.executable).parent) + os.pathsep + os.environ.get("PATH", ""))
    if cc is None:
        raise SystemExit("cc-secrets is not installed next to this Python or on PATH.")
    env = dict(os.environ)
    py = sys.executable

    with open(output_path, "w", encoding="utf-8") as out:
        say(f"run marker: {run_id}   site: {base}   browser: {args.browser}", out)

        listing = command(["cc-devthrottle", "browser", "list", "--json"], out)
        was_running = any(b.get("id") == args.browser and b.get("status") not in ("Stopped", None)
                          for b in json.loads(listing.stdout or "[]"))
        command(["cc-devthrottle", "browser", "start", args.browser], out, timeout=120)
        attach = subprocess.run([shutil.which("cc-devthrottle"), "browser", "attach", args.browser],
                                capture_output=True, text=True)
        for line in attach.stdout.splitlines():
            if line.startswith("export ") and "=" in line:
                key, value = line[len("export "):].split("=", 1)
                env[key] = value.strip().strip("'\"")
        say(f"attached: BU_NAME={env.get('BU_NAME')} BU_CDP_URL={env.get('BU_CDP_URL')}", out)

        # A tab left on a loopback address by an earlier run would match this run's allowed domain and be
        # picked instead of the tab under test. Close them so each case drives the tab it set up.
        debug = env["BU_CDP_URL"].rstrip("/")
        with urllib.request.urlopen(f"{debug}/json/list", timeout=10) as response:
            for target in json.loads(response.read().decode("utf-8")):
                if target.get("type") == "page" and urlsplit(target.get("url", "")).hostname in ("127.0.0.1", "localhost"):
                    urllib.request.urlopen(f"{debug}/json/close/{target['id']}", timeout=10).read()
                    say(f"closed a leftover loopback tab from an earlier run", out)

        command([cc, "list"], out, env=env)
        command([cc, "list", "--json"], out, env=env)
        command([cc, "run", "leak-good", "--", py, "-c",
                 "import sys,base64; s=sys.stdin.readline().strip(); print('plain', s); print('b64', base64.b64encode(s.encode()).decode())"], out, env=env)
        command([cc, "run", "leak-good", "--via", "env", "--", py, "-c", "import os; print('env', os.environ['CC_SECRET'])"], out, env=env)
        command([cc, "run", "leak-good", "--via", "askpass", "--", py, "-c",
                 "import os,subprocess; print('askpass', subprocess.run(['cmd','/c',os.environ['SUDO_ASKPASS']],capture_output=True,text=True).stdout)"], out, env=env)
        command([cc, "run", "leak-kept", "--", py, "-c", "import sys; print(sys.stdin.read())"], out, env=env)
        command([cc, "run", "leak-wrong", "--", py, "-c", "import sys; print(sys.stdin.read())"], out, env=env)

        snapshot = """
wait_for_load()
print(page_info())
print(js("document.documentElement.outerHTML"))
print(js("document.body.innerText"))
print(js("Array.from(document.querySelectorAll('input')).map(i => i.name + '=' + i.value).join(' ; ')"))
tree = cdp("Accessibility.getFullAXTree")
print(json.dumps(tree)[:200000])
capture_screenshot(path=r"%s")
print("screenshot saved")
"""
        results = {}

        def login_case(label, url, entry):
            harness(f"import json\ngoto_url({url!r})\nwait_for_load()\nprint(page_info())", out, env)
            results[label] = command([cc, "login", entry, "--browser", args.browser, "--json", "--timeout", "20"], out, env=env).stdout
            harness("import json\n" + snapshot % str(shots / f"{label}.png"), out, env)

        harness(f"new_tab({base + '/login'!r})\nwait_for_load()\nprint(page_info())", out, env)
        login_case("one-page", f"{base}/login", "leak-good")
        login_case("two-step", f"{base}/two-step", "leak-good")
        login_case("wrong-password", f"{base}/login", "leak-wrong")
        login_case("stuck-form", f"{base}/stuck", "leak-good")
        login_case("wrong-domain", f"http://localhost:{port}/login", "leak-good")
        login_case("kept-back", f"{base}/login", "leak-kept")

        # The recognition control: a page that shows the secret in plain text. Nothing about it is printed.
        control = subprocess.run([shutil.which("browser-harness")], input=(
            f"goto_url({base + '/planted'!r})\nwait_for_load()\ncapture_screenshot(path=r'{workdir / 'control.png'}')\n"
            f"goto_url({base + '/welcome'!r})\nwait_for_load()\nprint('control saved')"),
            capture_output=True, text=True, env=env, timeout=120)
        say(f"control screenshot taken (exit {control.returncode}); its page text is not printed", out)
        harness("close_tab()\nprint('closed')", out, env)

        command([cc, "log"], out, env=env)
        if not was_running:
            command(["cc-devthrottle", "browser", "stop", args.browser], out, timeout=120)

        say("login outcomes:", out)
        for label, text in results.items():
            try:
                say(f"  {label}: {json.loads(text)['outcome']}", out)
            except (ValueError, KeyError):
                say(f"  {label}: (no JSON result)", out)

    harness_log = subprocess.run(
        [str(Path(shutil.which("browser-harness")).parent.parent / "harness-env" / "Scripts" / "python.exe"), "-c",
         f"from browser_harness import _ipc; print(_ipc.log_path({env.get('BU_NAME', 'default')!r}))"],
        capture_output=True, text=True, env=env).stdout.strip()
    state = {"runId": run_id, "startedUtc": started.isoformat(), "port": port, "browser": args.browser,
             "harnessLog": harness_log, "results": {k: json.loads(v) if v.strip().startswith("{") else None
                                                     for k, v in results.items()}}
    (workdir / "state.json").write_text(json.dumps(state, indent=2), encoding="utf-8")
    site.shutdown()
    print(f"run finished; now run: verify --workdir {workdir} --transcript <this session's transcript>")
    return 0


# ------------------------------------------------------------------------------------------------------
# verify
# ------------------------------------------------------------------------------------------------------

OCR_SCRIPT = r"""
param([string]$ImagePath, [string]$OutPath)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine,Windows.Foundation,ContentType=WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime]
$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, $type) { $t = $asTaskGeneric.MakeGenericMethod($type).Invoke($null, @($op)); $t.Wait(-1) | Out-Null; $t.Result }
$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($ImagePath)) ([Windows.Storage.StorageFile])
$stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
$decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
$bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
$result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
[System.IO.File]::WriteAllText($OutPath, $result.Text)
"""


def ocr(image: Path, work: Path) -> str:
    """Text read out of `image`, with whitespace removed and upper-cased. Never printed."""
    script = work / "ocr.ps1"
    script.write_text(OCR_SCRIPT, encoding="utf-8")
    target = work / (image.stem + ".ocr.txt")
    result = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script),
                             str(image), str(target)], capture_output=True, text=True, timeout=120)
    if result.returncode != 0 or not target.exists():
        raise SystemExit(f"Text recognition failed on {image.name} (exit {result.returncode}).")
    return "".join(target.read_text(encoding="utf-8-sig").split()).upper()


def verify(args) -> int:
    from leak_search import BrokenInstrumentError, SecretSearch

    workdir = Path(args.workdir).resolve()
    state = json.loads((workdir / "state.json").read_text(encoding="utf-8"))
    os.environ["CC_SECRETS_HOME"] = str(workdir / "secrets-home")
    from src import paths
    from src.store import SecretStore
    from src.storefile import UserOnlyFile

    entries = SecretStore(UserOnlyFile(paths.store_path())).entries()
    pairs = [(e.secret.reveal(), e.username) for e in entries]
    search = SecretSearch(pairs)
    work = workdir / "verify-work"
    run_id = state["runId"]
    started = datetime.fromisoformat(state["startedUtc"]).timestamp()
    report = []

    def recent(folder: Path):
        return [p for p in folder.glob("*.log") if p.stat().st_mtime >= started] if folder.exists() else []

    # 1. Plain files, each with a marker proving it covers this run.
    files = [
        (Path(args.transcript), run_id),
        (workdir / "run-output.txt", run_id),
        (paths.audit_path(), "leak-good"),
    ]
    files += [(p, "[cdp] -> Page.getFrameTree") for p in sorted((paths.secrets_home() / "logs").glob("*.log"))]
    harness_log = Path(state["harnessLog"])
    if harness_log.exists() and harness_log.stat().st_mtime >= started:
        files.append((harness_log, ""))
    director_logs = recent(Path(os.environ["LOCALAPPDATA"]) / "cc-director" / "logs" / "director")
    if os.environ.get("CC_DIRECTOR_ROOT"):
        director_logs += recent(Path(os.environ["CC_DIRECTOR_ROOT"]) / "logs")
    files += [(p, "") for p in director_logs]

    try:
        searched, hits = search.search_files(files, work)
        report.append(f"files searched: {searched} (transcript, command output, audit log, tool log, "
                      f"harness log: {'yes' if harness_log.exists() else 'NOT FOUND'}, Director logs: {len(director_logs)})")

        # The search must also find the secrets where they legitimately are: the store itself.
        if not search.hits_in_bytes(paths.store_path().read_bytes(), "store"):
            raise BrokenInstrumentError("The search did not find the secrets in the store file itself.")
        report.append("control: the secrets ARE found in the store file")

        # 2. Screenshots, through text recognition, after the control proves recognition reads the secret.
        good = next(e.secret.reveal() for e in entries if e.name == "leak-good")
        if good not in ocr(workdir / "control.png", work):
            raise BrokenInstrumentError("Text recognition could not read the secret on the control page, "
                                        "so it cannot show that a screenshot is clean.")
        report.append("control: text recognition reads the secret off the control page")
        shots = sorted((workdir / "screenshots").glob("*.png"))
        if len(shots) < 6:
            raise BrokenInstrumentError(f"Only {len(shots)} screenshots were taken; expected 6.")
        for shot in shots:
            text = ocr(shot, work)
            if not text:
                raise BrokenInstrumentError(f"Text recognition read nothing from {shot.name}.")
            if any(secret in text for secret, _ in pairs):
                hits.append(type("Hit", (), {"path": shot.name, "form_index": 0, "encoding": "screenshot"})())
        report.append(f"screenshots read: {len(shots)}")
    except BrokenInstrumentError as exc:
        print(f"BROKEN INSTRUMENT: {exc}")
        return 2

    for line in report:
        print(line)
    print("login outcomes: " + ", ".join(f"{k}={(v or {}).get('outcome')}" for k, v in state["results"].items()))
    if hits:
        for hit in hits:
            print(f"LEAK: a form of a secret was found in {hit.path} ({hit.encoding})")
        return 1
    print(f"secret forms searched: {search.form_count}; hits: 0")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="step", required=True)
    run_parser = sub.add_parser("run")
    run_parser.add_argument("--workdir", required=True)
    run_parser.add_argument("--browser", default="agent-browser")
    verify_parser = sub.add_parser("verify")
    verify_parser.add_argument("--workdir", required=True)
    verify_parser.add_argument("--transcript", required=True)
    args = parser.parse_args()
    return run(args) if args.step == "run" else verify(args)


if __name__ == "__main__":
    sys.exit(main())
