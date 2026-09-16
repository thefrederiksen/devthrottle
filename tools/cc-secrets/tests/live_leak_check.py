"""The live leak check for issue #2889: real Chrome, a real Director-owned profile, a real transcript.

It runs in two separate invocations because the session transcript only records a command's output
after the command has returned:

  1. run     Makes secrets the caller never sees, stores them in a throwaway store, serves a local login
             site (and a second "agent" site on another port), and then uses them through every command
             an agent has - list, run (stdin, env and askpass), and login in the Director-owned browser.
             The login cases: a one-page form, a two-step form, a wrong password, a form that will not
             submit, a tab on a host the entry does not allow, an entry kept back from agents, the first
             review's cases (pressing Back after logging in and after a wrong password, a single-page app
             that hides its form, an agent rewriting the form's action to its own server, a tab on another
             port of the allowed host) and the second review's cases (a form that sends by GET, and the
             browser connection dropping right after the password was typed). After each login it takes what
             an agent can take: browser-harness page info, the page HTML, the page text, every input's value
             (hidden ones included), the accessibility tree, and a screenshot. EVERYTHING it prints is what
             the agent would see, and it all lands in the transcript.
  2. verify  Checks every login had the expected outcome, then searches the transcript, the recorded
             command output, the audit log, the tool log (where the debug-port traffic is logged), the
             browser-harness daemon log, the Director logs, and the text read out of every screenshot,
             for every form of every secret, in every output encoding.

Neither step prints a secret, and verify refuses to call a search clean unless it first finds a planted
copy of the secret in each kind of file, and unless each file covers this run. Screenshots are read with
Windows' built-in text recognition; the control is a page that shows the secret in plain text, which the
recognition must read back before its silence about the real screenshots counts.

Where a check needs to know whether something got hold of the secret - the agent's own server, the page
address, the page after a dropped connection, a listener the agent attached to the page - the comparison is
made here, privately, and only "yes" or "no" is printed.

Known limitation, checked and reported rather than hidden: JavaScript already running in the page receives
the password, because the site needs it, so an input listener an agent attaches to the page before calling
login can copy it. That case is expected to say "yes".

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

# Runs the installed command with the browser connection dropping right after the password is typed - the
# second review's reproduction. Only the timing of the failure is injected; the page and browser are real.
DROP_AFTER_PASSWORD = """
import sys
import cc_secrets.browser_login as bl
from cc_secrets.cli import main
original = bl.CdpConnection.call
fills = 0
def drop_after_second_fill(self, method, params=None):
    global fills
    result = original(self, method, params)
    if method == 'Runtime.callFunctionOn' and (params or {}).get('functionDeclaration') == bl._SET_VALUE:
        fills += 1
        if fills == 2:
            self._ws.close()
            raise ConnectionResetError('injected disconnect after password delivery')
    return result
bl.CdpConnection.call = drop_after_second_fill
sys.argv[0] = 'cc-secrets'
main()
"""


def ocr_safe_secret(length: int = 20) -> str:
    return "".join(secrets.choice(OCR_SAFE) for _ in range(length))


def say(text: str, out) -> None:
    # The console may be a Windows code page; browser-harness marks tabs with an emoji.
    print(text.encode("ascii", "backslashreplace").decode("ascii"), flush=True)
    out.write(text + "\n")
    out.flush()


# ------------------------------------------------------------------------------------------------------
# The local sites
# ------------------------------------------------------------------------------------------------------

FORM = """<!doctype html><html><head><title>Leak check {title}</title></head><body style="font:20px sans-serif">
<h1 id="heading">Leak check {title}</h1><p>{message}</p>
<form method="post" action="{action}" {extra}>
{fields}
<button type="submit">Sign in</button></form>{script}</body></html>"""
USER_FIELD = '<p><label>User <input name="username" autocomplete="username"></label></p>'
PASS_FIELD = '<p><label>Password <input type="password" name="password" autocomplete="current-password"></label></p>'
GET_FORM = ('<!doctype html><html><head><title>Leak check get form</title></head><body style="font:20px sans-serif">'
            '<h1>Leak check get form</h1><form action="/welcome">' + USER_FIELD + PASS_FIELD +
            '<button type="submit">Sign in</button></form></body></html>')


def handler_form(title, form_extra="", button_extra=""):
    """A POST form whose own submit handler changes where or how it sends - the review's cases."""
    return ('<!doctype html><html><head><title>Leak check ' + title + '</title></head>'
            '<body style="font:20px sans-serif"><h1>Leak check ' + title + '</h1>'
            '<form method="post" action="/session" ' + form_extra + '>' + USER_FIELD + PASS_FIELD +
            '<button type="submit" ' + button_extra + '>Sign in</button></form></body></html>')
SPA_SCRIPT = """<script>
document.forms[0].addEventListener('submit', e => {
  e.preventDefault();
  fetch('/api', {method: 'POST', body: new URLSearchParams(new FormData(e.target))}).then(r => {
    if (r.ok) { e.target.style.display = 'none'; document.getElementById('heading').textContent = 'Welcome'; }
  });
});
</script>"""


def _serve(handler_class):
    server = ThreadingHTTPServer(("127.0.0.1", 0), handler_class)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


class _Handler(BaseHTTPRequestHandler):
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

    def _fields(self):
        length = int(self.headers.get("Content-Length", "0"))
        return parse_qs(self.rfile.read(length).decode("utf-8"))


def make_site(good_secret: str, marker: str, get_received: list, requests_seen: list):
    class Handler(_Handler):
        def do_GET(self):
            url = urlsplit(self.path)
            query = parse_qs(url.query)
            requests_seen.append({"method": "GET", "host": self.headers.get("Host", ""), "secret": good_secret in self.path})
            if url.path == "/login":
                message = "Wrong password." if "error" in query else "Please sign in."
                self._send(200, FORM.format(title="login", message=message, action="/session", extra="",
                                            fields=USER_FIELD + PASS_FIELD, script=""))
            elif url.path == "/get-login":
                self._send(200, GET_FORM)
            elif url.path == "/submit-method":
                self._send(200, handler_form("submit-method", form_extra='onsubmit="this.method=' + chr(39) + "get" + chr(39) + '"'))
            elif url.path == "/submit-action":
                other = "http://localhost:" + str(self.server.server_port) + "/session"
                self._send(200, handler_form("submit-action", form_extra='onsubmit="this.action=' + chr(39) + other + chr(39) + '"'))
            elif url.path == "/stop-method":
                self._send(200, handler_form("stop-method", form_extra='onsubmit="this.method=' + chr(39) + "get" + chr(39) + '; event.stopPropagation()"'))
            elif url.path == "/stop-action":
                other = "http://localhost:" + str(self.server.server_port) + "/session"
                self._send(200, handler_form("stop-action", form_extra='onsubmit="this.action=' + chr(39) + other + chr(39) + '; event.stopPropagation()"'))
            elif url.path == "/redirect-login":
                self._send(200, handler_form("redirect-login").replace('action="/session"', 'action="/redirect307"'))
            elif url.path == "/submit-button":
                self._send(200, handler_form("submit-button", form_extra='onsubmit="event.submitter.formMethod=' + chr(39) + "get" + chr(39) + '"'))
            elif url.path == "/two-step":
                self._send(200, FORM.format(title="two-step", message="Step one.", action="/two-step-user",
                                            extra="", fields=USER_FIELD, script=""))
            elif url.path == "/two-step-password":
                hidden = f'<input type="hidden" name="username" value="{html.escape(query.get("u", [""])[0])}">'
                self._send(200, FORM.format(title="two-step password", message="Step two.", action="/session",
                                            extra="", fields=hidden + PASS_FIELD, script=""))
            elif url.path == "/stuck":
                self._send(200, FORM.format(title="stuck", message="This form never submits.", action="/session",
                                            extra='onsubmit="return false"', fields=USER_FIELD + PASS_FIELD, script=""))
            elif url.path == "/spa":
                self._send(200, FORM.format(title="single-page app", message="Sign in.", action="/api", extra="",
                                            fields=USER_FIELD + PASS_FIELD, script=SPA_SCRIPT))
            elif url.path == "/welcome":
                get_received.append(query.get("password", [""])[0])
                requests_seen.append({"method": "GET", "host": self.headers.get("Host", ""),
                                      "secret": query.get("password", [""])[0] == good_secret})
                self._send(200, f"<!doctype html><title>Leak check welcome</title><h1>Signed in as {USERNAME}</h1><p>{marker}</p>")
            elif url.path == "/planted":
                self._send(200, "<!doctype html><title>Leak check control</title><body style='background:#fff'>"
                                f"<p style='font:bold 56px monospace;letter-spacing:6px;margin:40px'>{good_secret}</p>")
            else:
                self._send(404, "not found")

        def do_POST(self):
            fields = self._fields()
            username = fields.get("username", [""])[0]
            password = fields.get("password", [""])[0]
            requests_seen.append({"method": "POST", "host": self.headers.get("Host", ""),
                                  "secret": password == good_secret})
            if self.path == "/redirect307":
                self._send(307, location="http://localhost:" + str(self.server.server_port) + "/session")
            elif self.path == "/two-step-user":
                self._send(303, location=f"/two-step-password?u={username}")
            elif self.path == "/api":
                self._send(200 if password == good_secret else 401, "ok")
            elif self.path == "/session" and username == USERNAME and password == good_secret:
                self._send(303, location="/welcome")
            else:
                self._send(303, location="/login?error=1")

    return _serve(Handler)


def make_agent_site(received: list):
    """A server an agent controls, on another port of the same host. Records every password sent to it."""
    class Handler(_Handler):
        def do_GET(self):
            if urlsplit(self.path).path == "/login":
                self._send(200, FORM.format(title="login", message="Please sign in.", action="/session", extra="",
                                            fields=USER_FIELD + PASS_FIELD, script=""))
            else:
                self._send(404, "not found")

        def do_POST(self):
            received.append(self._fields().get("password", [""])[0])
            self._send(200, "<!doctype html><title>Leak check agent</title><h1>thanks</h1>")

    return _serve(Handler)


# ------------------------------------------------------------------------------------------------------
# run
# ------------------------------------------------------------------------------------------------------

def command(argv, out, env=None, stdin=None, timeout=180):
    say(f"$ {' '.join(argv[:1] + [a if len(a) < 120 else a[:117] + '...' for a in argv[1:]])}", out)
    # On Windows the fleet tools are .cmd shims, which CreateProcess only finds by their full name.
    resolved = shutil.which(argv[0])
    if resolved is None:
        raise SystemExit(f"{argv[0]} is not on PATH.")
    result = subprocess.run([resolved] + list(argv[1:]), input=stdin, capture_output=True, text=True, env=env,
                            timeout=timeout, encoding="utf-8", errors="replace")
    say((result.stdout + result.stderr).rstrip() + f"\n(exit {result.returncode})", out)
    return result


def harness(script: str, out, env) -> subprocess.CompletedProcess:
    exe = shutil.which("browser-harness")
    if exe is None:
        raise SystemExit("browser-harness is not on PATH.")
    return command([exe], out, env=env, stdin=script)


def harness_private(script: str, env) -> str:
    """Run a harness script whose output may hold a secret. Its output is returned, never printed."""
    return subprocess.run([shutil.which("browser-harness")], input=script, capture_output=True, text=True,
                          env=env, timeout=120, encoding="utf-8", errors="replace").stdout


def run(args) -> int:
    workdir = Path(args.workdir).resolve()
    if workdir.exists() and any(workdir.iterdir()):
        raise SystemExit(f"{workdir} is not empty; give run a fresh folder.")
    workdir.mkdir(parents=True, exist_ok=True)
    os.environ["CC_SECRETS_HOME"] = str(workdir / "secrets-home")

    from src import paths
    from src.store import SecretStore, make_entry
    from src.storefile import UserOnlyFile

    run_id = "live-" + secrets.token_hex(6)
    started = datetime.now(timezone.utc)
    good, wrong, kept = ocr_safe_secret(), ocr_safe_secret(), ocr_safe_secret()
    received, get_received, requests_seen = [], [], []
    site = make_site(good, run_id, get_received, requests_seen)
    agent_site = make_agent_site(received)
    port = site.server_address[1]
    agent_port = agent_site.server_address[1]
    base = f"http://127.0.0.1:{port}"
    agent_base = f"http://127.0.0.1:{agent_port}"

    store = SecretStore(UserOnlyFile(paths.store_path()))
    store.put(make_entry("leak-good", USERNAME, good, [base], run_id, True, ["login", "run"]))
    store.put(make_entry("leak-wrong", USERNAME, wrong, [base], run_id, True, ["login"]))
    store.put(make_entry("leak-kept", USERNAME, kept, [base], run_id, False, ["login", "run"]))

    shots = workdir / "screenshots"
    shots.mkdir()
    output_path = workdir / "run-output.txt"

    cc = shutil.which("cc-secrets", path=str(Path(sys.executable).parent) + os.pathsep + os.environ.get("PATH", ""))
    if cc is None:
        raise SystemExit("cc-secrets is not installed next to this Python or on PATH.")
    env = dict(os.environ)
    py = sys.executable
    results, expected, agent_checks = {}, {}, {}
    # Screenshots of a tab the login's clean-up left on about:blank, recorded when taken: such a page has no
    # text to recognise, and only a page positively recorded as blank may come back from recognition empty.
    blank_pages = []

    with open(output_path, "w", encoding="utf-8") as out:
        say(f"run marker: {run_id}   site: {base}   agent site: {agent_base}   browser: {args.browser}", out)

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

        # A tab left on a loopback address by an earlier run would match this run's allowed address and be
        # picked instead of the tab under test. Close them so each case drives the tab it set up.
        debug = env["BU_CDP_URL"].rstrip("/")
        with urllib.request.urlopen(f"{debug}/json/list", timeout=10) as response:
            for target in json.loads(response.read().decode("utf-8")):
                if target.get("type") == "page" and urlsplit(target.get("url", "")).hostname in ("127.0.0.1", "localhost"):
                    urllib.request.urlopen(f"{debug}/json/close/{target['id']}", timeout=10).read()
                    say("closed a leftover loopback tab from an earlier run", out)

        command([cc, "list"], out, env=env)
        command([cc, "list", "--json"], out, env=env)
        command([cc, "run", "leak-good", "--", py, "-c",
                 "import sys,base64; s=sys.stdin.readline().strip(); print('plain', s); print('b64', base64.b64encode(s.encode()).decode())"], out, env=env)
        command([cc, "run", "leak-good", "--via", "env", "--", py, "-c", "import os; print('env', os.environ['CC_SECRET'])"], out, env=env)
        command([cc, "run", "leak-good", "--via", "env", "--", py, "-c", "import os; print({'password': os.environ['CC_SECRET']})"], out, env=env)
        command([cc, "run", "leak-good", "--via", "env", "--", py, "-c",
                 "import os; print({'password': os.environ['CC_SECRET'].encode('utf-16-le')})"], out, env=env)
        command([cc, "run", "leak-good", "--via", "env", "--", py, "-c",
                 "import os,sys; sys.stdout.buffer.write(str([os.environ['CC_SECRET'].encode('cp1252')]).encode('cp1252'))"], out, env=env)
        command([cc, "run", "leak-good", "--via", "env", "--", "cmd", "/c", "echo cmd %CC_SECRET%"], out, env=env)
        command([cc, "run", "leak-good", "--via", "env", "--", "cmd", "/u", "/c", "echo utf16 %CC_SECRET%"], out, env=env)
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

        def login_case(label, url, entry, outcome, before="", after=""):
            expected[label] = outcome
            harness(f"import json\ngoto_url({url!r})\nwait_for_load()\n{before}\nprint(page_info())", out, env)
            results[label] = command([cc, "login", entry, "--browser", args.browser, "--json", "--timeout", "20"], out, env=env).stdout
            harness("import json\n" + after + "\n" + snapshot % str(shots / f"{label}.png"), out, env)
            if harness_private("print(page_info()['url'])", env).strip() == "about:blank":
                blank_pages.append(label)

        go_back = "js('history.back()')\nwait(2)\nwait_for_load()\nprint('pressed Back:', page_info()['url'])"

        harness(f"new_tab({base + '/login'!r})\nwait_for_load()\nprint(page_info())", out, env)
        login_case("one-page", f"{base}/login", "leak-good", "logged in")
        login_case("two-step", f"{base}/two-step", "leak-good", "logged in")
        login_case("wrong-password", f"{base}/login", "leak-wrong", "failed")
        login_case("stuck-form", f"{base}/stuck", "leak-good", "failed")
        login_case("wrong-host", f"http://localhost:{port}/login", "leak-good", "refused")
        login_case("kept-back", f"{base}/login", "leak-kept", "refused")
        login_case("back-after-login", f"{base}/login", "leak-good", "logged in", after=go_back)
        login_case("back-after-wrong-password", f"{base}/login", "leak-wrong", "failed", after=go_back)
        login_case("single-page-app-hidden-form", f"{base}/spa", "leak-good", "logged in")

        received.clear()
        login_case("agent-rewrites-form-action", f"{base}/login", "leak-good", "refused",
                   before=f"js(\"document.forms[0].action = '{agent_base}/steal'\")")
        agent_checks["agent-rewrites-form-action: agent server received the secret"] = good in received
        received.clear()
        login_case("tab-on-another-port", f"{agent_base}/login", "leak-good", "refused")
        agent_checks["tab-on-another-port: agent server received the secret"] = good in received

        # The second review's cases.
        get_received.clear()
        login_case("get-form", f"{base}/get-login", "leak-good", "refused")
        address = harness_private("import json\nprint(page_info()['url'])\nprint(json.dumps(cdp('Page.getNavigationHistory')))", env)
        agent_checks["get-form: the secret reached the page address, the history or the site"] = \
            good in address or good in get_received

        for case, path in (("submit-handler-changes-method", "/submit-method"),
                           ("submit-handler-changes-action", "/submit-action"),
                           ("submit-handler-changes-button-method", "/submit-button")):
            requests_seen.clear()
            login_case(case, f"{base}{path}", "leak-good", "refused")
            seen = harness_private("import json\nprint(page_info()['url'])\nprint(json.dumps(cdp('Page.getNavigationHistory')))", env)
            agent_checks[f"{case}: the secret reached the address, the history or the site"] = (
                good in seen or any(r["secret"] for r in requests_seen))

        allowed_host = f"127.0.0.1:{port}"
        for case, path in (("submit-handler-stops-the-event-and-changes-method", "/stop-method"),
                           ("submit-handler-stops-the-event-and-changes-action", "/stop-action"),
                           ("login-answers-307-to-another-origin", "/redirect-login")):
            requests_seen.clear()
            login_case(case, f"{base}{path}", "leak-good", "refused")
            seen = harness_private("import json\nprint(page_info()['url'])\nprint(json.dumps(cdp('Page.getNavigationHistory')))", env)
            agent_checks[f"{case}: the secret reached the address, the history, a GET, or another host"] = (
                good in seen or any(r["secret"] and (r["method"] == "GET" or r["host"] != allowed_host)
                                    for r in requests_seen))

        expected["connection-drops-while-typing"] = "exit 1"
        harness(f"import json\ngoto_url({base + '/stuck'!r})\nwait_for_load()\nprint(page_info())", out, env)
        dropped = command([py, "-c", DROP_AFTER_PASSWORD, "login", "leak-good", "--browser", args.browser, "--json",
                           "--timeout", "20"], out, env=env)
        results["connection-drops-while-typing"] = json.dumps({"outcome": f"exit {dropped.returncode}"})
        left = harness_private("print(js(\"Array.from(document.querySelectorAll('input[type=password]')).map(e => e.value).join('|')\"))", env)
        agent_checks["connection-drops-while-typing: the password was left readable in the page"] = good in left
        harness("import json\n" + snapshot % str(shots / "connection-drops-while-typing.png"), out, env)

        # The known limitation: a listener the agent attached to the page before login.
        login_case("agent-input-listener", f"{base}/login", "leak-good", "logged in",
                   before="js(\"document.querySelector('input[type=password]').addEventListener('input', e => localStorage.setItem('grab', e.target.value))\")")
        grabbed = harness_private("print(js(\"localStorage.getItem('grab') || ''\"))", env)
        agent_checks["agent-input-listener (known limitation): agent read the secret"] = good in grabbed
        harness("js(\"localStorage.removeItem('grab')\")\nprint('cleared the agent listener storage')", out, env)
        for label, recovered in agent_checks.items():
            say(f"{label}: {'yes' if recovered else 'no'}", out)

        # The recognition control: a page that shows the secret in plain text. Nothing about it is printed.
        control = subprocess.run([shutil.which("browser-harness")], input=(
            f"goto_url({base + '/planted'!r})\nwait_for_load()\ncapture_screenshot(path=r'{workdir / 'control.png'}')\n"
            f"goto_url({base + '/welcome'!r})\nwait_for_load()\nprint('control saved')"),
            capture_output=True, text=True, env=env, timeout=120)
        say(f"control screenshot taken (exit {control.returncode}); its page text is not printed", out)
        harness("close_tab()\nprint('closed')", out, env)

        command([cc, "log"], out, env=env)
        # Asked of browser-harness itself, while the browser still runs, so it holds however the harness is
        # installed (a uv tool, a separate virtual environment).
        harness_log = harness_private(
            f"from browser_harness import _ipc\nprint(_ipc.log_path({env.get('BU_NAME', 'default')!r}))", env).strip()
        if not harness_log:
            raise SystemExit("browser-harness did not report its log path, so its log cannot be searched.")
        if not was_running:
            command(["cc-devthrottle", "browser", "stop", args.browser], out, timeout=120)

        say("login outcomes:", out)
        for label, text in results.items():
            try:
                say(f"  {label}: {json.loads(text)['outcome']} (expected {expected[label]})", out)
            except (ValueError, KeyError):
                say(f"  {label}: (no JSON result)", out)

    state = {"runId": run_id, "startedUtc": started.isoformat(), "port": port, "browser": args.browser,
             "harnessLog": harness_log, "expected": expected, "agentChecks": agent_checks,
             "screenshots": len(expected), "blankPages": blank_pages,
             "results": {k: json.loads(v) if v.strip().startswith("{") else None for k, v in results.items()}}
    (workdir / "state.json").write_text(json.dumps(state, indent=2), encoding="utf-8")
    site.shutdown()
    agent_site.shutdown()
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
    work.mkdir(exist_ok=True)
    run_id = state["runId"]
    started = datetime.fromisoformat(state["startedUtc"]).timestamp()
    failures = 0

    print("login outcomes:")
    for label, want in state["expected"].items():
        got = (state["results"].get(label) or {}).get("outcome")
        ok = got == want
        failures += 0 if ok else 1
        print(f"  {'ok  ' if ok else 'WRONG'} {label}: {got} (expected {want})")
    print("agent-side checks:")
    for label, recovered in state["agentChecks"].items():
        known = "known limitation" in label
        wrong = recovered and not known
        failures += 1 if wrong else 0
        print(f"  {'WRONG' if wrong else 'ok  '} {label}: {'yes' if recovered else 'no'}")

    def recent(folder: Path):
        return [p for p in folder.glob("*.log") if p.stat().st_mtime >= started] if folder.exists() else []

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
        print(f"files searched: {searched} (transcript, command output, audit log, tool log, "
              f"harness log: {'yes' if harness_log.exists() else 'NOT FOUND'}, Director logs: {len(director_logs)})")

        if not search.hits_in_bytes(paths.store_path().read_bytes(), "store"):
            raise BrokenInstrumentError("The search did not find the secrets in the store file itself.")
        print("control: the secrets ARE found in the store file")

        good = next(e.secret.reveal() for e in entries if e.name == "leak-good")
        if good not in ocr(workdir / "control.png", work):
            raise BrokenInstrumentError("Text recognition could not read the secret on the control page, "
                                        "so it cannot show that a screenshot is clean.")
        print("control: text recognition reads the secret off the control page")
        shots = sorted((workdir / "screenshots").glob("*.png"))
        if len(shots) < state["screenshots"]:
            raise BrokenInstrumentError(f"Only {len(shots)} screenshots were taken; expected {state['screenshots']}.")
        screenshot_hits = 0
        for shot in shots:
            text = ocr(shot, work)
            if not text and shot.stem in state["blankPages"]:
                print(f"screenshot {shot.name}: the tab was on about:blank when it was taken, so it has no text")
                continue
            if not text:
                raise BrokenInstrumentError(f"Text recognition read nothing from {shot.name}.")
            if any(secret in text for secret, _ in pairs):
                screenshot_hits += 1
                print(f"LEAK: a secret is readable in screenshot {shot.name}")
        print(f"screenshots read: {len(shots)}")
    except BrokenInstrumentError as exc:
        print(f"BROKEN INSTRUMENT: {exc}")
        return 2

    for hit in hits:
        print(f"LEAK: a form of a secret was found in {hit.path} ({hit.encoding})")
    print(f"secret forms searched: {search.form_count}; hits: {len(hits) + screenshot_hits}; wrong outcomes or agent checks: {failures}")
    return 1 if hits or screenshot_hits or failures else 0


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
