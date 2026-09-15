"""A crash must not print a secret or the store, and a dropped browser connection must not leave the typed
password in the page (reviews of pull request 2891).

These run the installed entry point in a fresh process, exactly as the command runs, because Typer's
test runner catches exceptions and so can never show what a crash prints. Each is a reviewer's
reproduction: a store written by a newer version, a malformed record, and a browser connection that
drops while the password is being typed.
"""

import json
import sys

from conftest import add_entry, run_entry_point
from src import paths
from src.redact import variants_for

# The debug protocol a fake tab answers. Both fakes below close the FIRST connection while the password is
# being typed - as when the tab or the socket drops during a login.
_FAKE_TAB_COMMON = r'''
import json, sys, threading
from websockets.sync.server import serve
from src import browser_login
PAGE = "http://127.0.0.1:8123/login"
connections = []
def answer(ws, number, m, cleaned):
    method = m["method"]; p = m.get("params", {})
    expression = p.get("expression", "")
    if method == "Page.getFrameTree":
        return {"frameTree": {"frame": {"id": "F", "loaderId": "L", "url": PAGE}}}
    if method == "Page.createIsolatedWorld":
        return {"executionContextId": 7}
    if method == "Runtime.evaluate":
        if p.get("returnByValue"):
            return {"result": {"value": {"password": True, "username": True, "verification": False}}}
        return {"result": {"objectId": "obj-p" if "('password')" in expression else "obj-u"}}
    if method == "Runtime.callFunctionOn":
        declaration = p["functionDeclaration"]
        if "requestSubmit" in declaration:
            return {"result": {"value": "form"}}
        if "formMethod" in declaration:
            return {"result": {"value": {"target": "http://127.0.0.1:8123/session", "method": "post"}}}
        if "formAction" in declaration:
            return {"result": {"value": "http://127.0.0.1:8123/session"}}
        return {"result": {"value": True}}
    return {}
'''

# A tab that can be cleaned up over a fresh connection, and says so on standard error.
RECONNECTING_TAB = _FAKE_TAB_COMMON + r'''
def handler(ws):
    connections.append(ws)
    number = len(connections)
    cleaned = False
    for raw in ws:
        m = json.loads(raw); p = m.get("params", {})
        expression = p.get("expression", "")
        if number == 1 and m["method"] == "Runtime.callFunctionOn" and p.get("arguments") \
                and p["objectId"] == "obj-p" and "requestSubmit" not in p["functionDeclaration"]:
            ws.close()
            return
        if m["method"] == "Runtime.evaluate" and "setter.call(e, '')" in expression:
            cleaned = True
            sys.stderr.write(f"FAKE-TAB: password fields cleared on connection {number}\n")
            res = {"result": {"value": 1}}
        elif m["method"] == "Runtime.evaluate" and "some(e => e.value" in expression:
            res = {"result": {"value": not cleaned}}
        elif m["method"] == "Page.resetNavigationHistory":
            sys.stderr.write(f"FAKE-TAB: history reset on connection {number}\n")
            res = {}
        elif m["method"] == "Page.getNavigationHistory":
            res = {"currentIndex": 0, "entries": [{"url": PAGE}]}
        else:
            res = answer(ws, number, m, cleaned)
        ws.send(json.dumps({"id": m["id"], "result": res}))
server = serve(handler, "127.0.0.1", 0)
port = server.socket.getsockname()[1]
threading.Thread(target=server.serve_forever, daemon=True).start()
_cli._browser_port = lambda name: port
browser_login.list_page_targets = lambda p: [{"id": "T1", "type": "page", "url": PAGE, "webSocketDebuggerUrl": f"ws://127.0.0.1:{port}/"}]
'''

# A tab that cannot be cleaned up on any connection, and cannot be closed.
UNSAFE_TAB = _FAKE_TAB_COMMON + r'''
def handler(ws):
    connections.append(ws)
    number = len(connections)
    for raw in ws:
        m = json.loads(raw); p = m.get("params", {})
        if number == 1 and m["method"] == "Runtime.callFunctionOn" and p.get("arguments") \
                and p["objectId"] == "obj-p" and "requestSubmit" not in p["functionDeclaration"]:
            ws.close()
            return
        ws.send(json.dumps({"id": m["id"], "result": answer(ws, number, m, False)}))
server = serve(handler, "127.0.0.1", 0)
port = server.socket.getsockname()[1]
threading.Thread(target=server.serve_forever, daemon=True).start()
_cli._browser_port = lambda name: port
browser_login.list_page_targets = lambda p: [{"id": "T1", "type": "page", "url": PAGE, "webSocketDebuggerUrl": f"ws://127.0.0.1:{port}/"}]
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


def test_Login_ConnectionDropsWhileTyping_PasswordIsClearedOverAFreshConnection(store, home):
    secret = add_entry(store, name="site", domains=("http://127.0.0.1:8123",))

    result = run_entry_point(["login", "site", "--browser", "agent-browser", "--timeout", "10"], _env(home),
                             prelude=RECONNECTING_TAB)

    output = result.stdout + result.stderr
    assert b"FAKE-TAB: password fields cleared on connection 2" in result.stderr, output
    assert b"FAKE-TAB: history reset on connection 2" in result.stderr
    assert result.returncode == 1
    assert _leaks(output, [secret]) == []
    assert b"Traceback" not in output


def test_Login_TabThatCannotBeMadeSafe_TellsThePersonToCloseIt(store, home):
    secret = add_entry(store, name="site", domains=("http://127.0.0.1:8123",))

    result = run_entry_point(["login", "site", "--browser", "agent-browser", "--timeout", "10"], _env(home),
                             prelude=UNSAFE_TAB)

    output = result.stdout + result.stderr
    assert result.returncode == 1, output
    assert _leaks(output, [secret]) == []
    assert b"Traceback" not in output
    assert b"Close that browser tab now" in result.stderr
