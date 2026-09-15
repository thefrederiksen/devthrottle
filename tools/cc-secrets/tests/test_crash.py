"""A crash must not print a secret or the store (review of pull request 2891, defect 1).

These run the installed entry point in a fresh process, exactly as the command runs, because Typer's
test runner catches exceptions and so can never show what a crash prints. Each is the reviewer's
reproduction: a store written by a newer version, a malformed record, and a browser connection that
drops while the password is being typed.
"""

import json
import sys

from conftest import add_entry, run_entry_point
from src import paths
from src.redact import variants_for

LOGIN_PAGE = "http://127.0.0.1:8123/login"

# A fake debug endpoint that answers like a tab showing a login form, then closes the connection while
# the password is being typed - as when the tab or browser is closed during a login.
FAKE_BROWSER = r'''
import json, threading
from websockets.sync.server import serve
from src import browser_login
PAGE = "http://127.0.0.1:8123/login"
def handler(ws):
    for raw in ws:
        m = json.loads(raw); method = m["method"]; p = m.get("params", {})
        if method == "Page.getFrameTree":
            res = {"frameTree": {"frame": {"id": "F", "loaderId": "L", "url": PAGE}}}
        elif method == "Page.createIsolatedWorld":
            res = {"executionContextId": 7}
        elif method == "Runtime.evaluate":
            if p.get("returnByValue"):
                res = {"result": {"value": {"password": True, "username": True, "verification": False}}}
            else:
                res = {"result": {"objectId": "obj-p" if "('password')" in p["expression"] else "obj-u"}}
        elif method == "Runtime.callFunctionOn":
            declaration = p["functionDeclaration"]
            if p.get("arguments") and p["objectId"] == "obj-p" and "requestSubmit" not in declaration:
                ws.close()
                return
            if "requestSubmit" in declaration:
                res = {"result": {"value": "form"}}
            elif "formAction" in declaration:
                res = {"result": {"value": "http://127.0.0.1:8123/session"}}
            else:
                res = {"result": {"value": True}}
        else:
            res = {}
        ws.send(json.dumps({"id": m["id"], "result": res}))
server = serve(handler, "127.0.0.1", 0)
port = server.socket.getsockname()[1]
threading.Thread(target=server.serve_forever, daemon=True).start()
_cli._browser_port = lambda name: port
browser_login.list_page_targets = lambda p: [{"type": "page", "url": PAGE, "webSocketDebuggerUrl": f"ws://127.0.0.1:{port}/"}]
'''


def _leaks(output: bytes, secrets) -> list:
    found = []
    for secret in secrets:
        for form in variants_for(secret, "leak-user"):
            for encoding in ("utf-8", "utf-16-le"):
                if form.encode(encoding) in output:
                    found.append((form[:4] + "...", encoding))
    return found


def _two_entries(store):
    kept = add_entry(store, name="kept", agents=False, domains=("https://example.com",))
    agent = add_entry(store, name="devlinux", domains=("https://127.0.0.1",))
    return [kept, agent]


def _env(home):
    return {"CC_SECRETS_HOME": str(home), "CC_SESSION_ID": "crash-test-session"}


def _edit_store(change):
    path = paths.store_path()
    document = json.loads(path.read_text(encoding="utf-8"))
    change(document)
    path.write_text(json.dumps(document), encoding="utf-8")


def test_Run_StoreWrittenByANewerVersion_PrintsNoSecretAndNoTraceback(store, home):
    secrets = _two_entries(store)
    _edit_store(lambda d: d.update(version=2))

    result = run_entry_point(["run", "devlinux", "--", sys.executable, "-c", "print(1)"], _env(home))

    output = result.stdout + result.stderr
    assert result.returncode != 0
    assert _leaks(output, secrets) == []
    assert b"Traceback" not in output
    assert b"version 2" in result.stderr


def test_Run_MalformedRecord_PrintsNoSecretAndNoTraceback(store, home):
    secrets = _two_entries(store)
    _edit_store(lambda d: d["entries"][0].pop("name"))

    result = run_entry_point(["run", "devlinux", "--", sys.executable, "-c", "print(1)"], _env(home))

    output = result.stdout + result.stderr
    assert result.returncode != 0
    assert _leaks(output, secrets) == []
    assert b"Traceback" not in output
    assert b"unexpected KeyError" in result.stderr


def test_List_StoreThatIsNotJson_PrintsNoStoreContent(store, home):
    secrets = _two_entries(store)
    path = paths.store_path()
    path.write_text(path.read_text(encoding="utf-8") + "\n}}} not json", encoding="utf-8")

    result = run_entry_point(["list"], _env(home))

    output = result.stdout + result.stderr
    assert result.returncode != 0
    assert _leaks(output, secrets) == []
    assert b"Traceback" not in output


def test_ErrorThatEscapesACommand_IsShownByItsTypeOnly(store, home):
    secret = add_entry(store)
    # The audit log fails while a failure is being recorded, with the secret in the error's message: the
    # error escapes the command's own handling and reaches the entry point.
    prelude = (f"SECRET = {secret!r}\n"
               "def _broken_audit():\n"
               "    raise RuntimeError('store content ' + SECRET)\n"
               "_cli._audit = _broken_audit\n")

    result = run_entry_point(["run", "devlinux", "--", "no-such-command-for-cc-secrets"], _env(home), prelude=prelude)

    output = result.stdout + result.stderr
    assert result.returncode == 1, output
    assert _leaks(output, [secret]) == []
    assert b"Traceback" not in output
    assert b"cc-secrets stopped: an unexpected RuntimeError" in result.stderr


def test_Login_BrowserConnectionDropsWhileTyping_PrintsNoSecret(store, home):
    secret = add_entry(store, name="site", domains=("http://127.0.0.1:8123",))

    result = run_entry_point(["login", "site", "--browser", "agent-browser", "--timeout", "10"], _env(home),
                             prelude=FAKE_BROWSER)

    output = result.stdout + result.stderr
    assert result.returncode == 1, output
    assert _leaks(output, [secret]) == []
    assert b"Traceback" not in output
    assert b"unexpected" in result.stderr
