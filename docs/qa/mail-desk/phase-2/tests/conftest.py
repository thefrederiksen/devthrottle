"""The shared rig for the tests of src/CcDirector.Gateway/MailDesk/Template/checks/screen.py.

- screen.py is loaded fresh from its path for every test.
- Every CC_ variable is removed, HOME is a scratch folder, and the "account record" home is another scratch folder:
  the real saved sign-in is never read, and no real token exists anywhere.
- The network is a fake Graph that stands in for urllib.request.urlopen, so screen.py's own transport (its HTTP error,
  unreachable and not-JSON handling) runs for real. It is the phase 5b rig's FakeMailbox (folders of messages, a
  receive-time filter, oldest or newest first, one log line per request), made in-process because a gate test may not
  open a real port. It records EVERY request that reaches it, so "nothing reached the network" is its log being empty.
- MSAL's PublicClientApplication is replaced by a fake that never touches the network; MSAL's real token cache is
  kept, so the write-back of a renewed sign-in is the real serialize().

    uv run --no-project --with pytest --with "msal>=1.28" python -m pytest docs/qa/mail-desk/phase-2/tests -v
"""

import importlib.util
import io
import json
import os
import re
import sys
import urllib.error
import urllib.parse
from datetime import datetime, timezone
from pathlib import Path

import pytest

sys.dont_write_bytecode = True  # a __pycache__ beside the check would end up in the published template

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[4]
SCREEN = REPO / "src" / "CcDirector.Gateway" / "MailDesk" / "Template" / "checks" / "screen.py"
AUTH = REPO / "tools" / "cc-outlook" / "src" / "auth.py"

MAILBOX = "email@devthrottle.com"
SECOND = "second@example.org"
NOW = datetime(2026, 10, 18, 12, 0, 0, tzinfo=timezone.utc)
MACHINE = "testmachine"

# What uv writes to standard error ahead of the check whenever it builds the script's environment (uv 0.12.23 on
# devlinux, warm cache). The Director keeps it with the check's own output (DefinitionCheckRunner runs uv run --script).
UV_LINE = "Installed 10 packages in 95ms"


def gateway_reason(err, exit_code=1, uv_line=UV_LINE):
    """The reason the Gateway stores for a failed check, by its own rule on origin/main: TriggerCheckContract.Read takes
    ALL of standard error, OneLine splits it on carriage return, line feed and tab, trims each piece, drops the empty
    ones, joins them with one space and cuts at QuotedOutputLimit (200), adding "..." when it cut."""
    pieces = [p.strip() for p in re.split(r"[\r\n\t]", (uv_line + "\n" if uv_line else "") + err)]
    flat = " ".join(p for p in pieces if p)
    said = flat if len(flat) <= 200 else flat[:200] + "..."
    return f"exit code {exit_code}: {said}" if said else f"exit code {exit_code}"


def load_screen():
    spec = importlib.util.spec_from_file_location("screen", SCREEN)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def msg(mid, received, sender="someone@example.com", subject="Hello", name="Some One", preview="A short note.",
        attachments=False):
    return {"id": mid, "receivedDateTime": received, "subject": subject, "hasAttachments": attachments,
            "bodyPreview": preview, "from": {"emailAddress": {"address": sender, "name": name}}}


def sent(to, when="2026-10-10T08:00:00Z"):
    return {"id": f"sent-{to}-{when}", "sentDateTime": when,
            "toRecipients": [{"emailAddress": {"address": to}}], "ccRecipients": [], "bccRecipients": []}


class Response:
    def __init__(self, raw):
        self.raw = raw

    def read(self):
        return self.raw

    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False


class FakeGraph:
    """One fake Microsoft Graph for every mailbox, told apart by the token (the fake sign-in hands out
    "token-for-<address>"). A request it does not know fails the test."""

    def __init__(self):
        self.mailboxes = {}
        self.log = []          # (method, url, body text or None, token) for EVERY request that reached the network
        self.fail = None       # None, an HTTP status, "unreachable" or "not-json"
        self.fail_for = {}     # address -> the same, for that mailbox only, or "no-value" (a page without "value")

    def box(self, address):
        return self.mailboxes.setdefault(address, {"inbox": [], "sentitems": [], "archive": [], "junkemail": []})

    def posts(self):
        return [entry for entry in self.log if entry[0] != "GET"]

    def urlopen(self, request, timeout=None):
        method = request.get_method()
        url = request.full_url
        token = (request.get_header("Authorization") or "").removeprefix("Bearer ")
        body = request.data.decode("utf-8") if request.data is not None else None
        self.log.append((method, url, body, token))
        failure = self.fail_for.get(token.removeprefix("token-for-"), self.fail)
        if failure == "no-value":
            return Response(b'{"items": []}')
        if failure == "unreachable":
            raise urllib.error.URLError("connection refused")
        if isinstance(failure, int):
            raise urllib.error.HTTPError(url, failure, "error", {}, io.BytesIO(b'{"error": {"code": "x"}}'))
        if failure == "not-json":
            return Response(b"<html>a proxy page</html>")
        status, answer = self.answer(method, url, body, token)
        if status >= 400:
            raise urllib.error.HTTPError(url, status, "error", {}, io.BytesIO(json.dumps(answer).encode("utf-8")))
        return Response(json.dumps(answer).encode("utf-8"))

    def answer(self, method, url, body, token):
        assert token.startswith("token-for-"), f"a request without the fake sign-in's token: {method} {url}"
        box = self.box(token.removeprefix("token-for-"))
        parts = urllib.parse.urlsplit(url)
        assert parts.netloc == "graph.microsoft.com", url
        query = {k: v[0] for k, v in urllib.parse.parse_qs(parts.query).items()}
        segments = parts.path.split("/")
        if method == "GET" and parts.path.startswith("/v1.0/me/mailFolders/") and parts.path.endswith("/messages"):
            folder = segments[4]
            messages = list(box[folder])
            if "$filter" in query:
                field, op, since = query["$filter"].split(" ")
                assert op == "ge"
                messages = [m for m in messages if m[field] >= since]
            field, direction = query["$orderby"].split(" ")
            messages.sort(key=lambda m: m[field], reverse=(direction == "desc"))
            top, skip = int(query["$top"]), int(query.get("$skip", "0"))
            answer = {"value": messages[skip:skip + top]}
            if skip + top < len(messages):
                answer["@odata.nextLink"] = url.split("&%24skip=")[0] + f"&%24skip={skip + top}"
            return 200, answer
        if method == "POST" and len(segments) == 6 and segments[:4] == ["", "v1.0", "me", "messages"] \
                and segments[5] == "move":
            mid, destination = segments[4], json.loads(body)["destinationId"]
            for folder in box.values():
                for m in folder:
                    if m.get("id") == mid:
                        folder.remove(m)
                        moved = dict(m, id=f"moved-{mid}")
                        box[destination].append(moved)
                        return 201, moved
            return 404, {"error": {"code": "ErrorItemNotFound"}}
        raise AssertionError(f"the fake Graph does not know {method} {parts.path}")


class FakeSignIn:
    """Stands in for msal.PublicClientApplication. Per address: whether the cache holds the account, whether the
    silent renewal works, and whether it changed the cache (a renewal)."""

    def __init__(self):
        self.accounts = {}          # address -> True
        self.renew_fails = set()
        self.renews = set()
        self.scopes_asked = []
        self.authorities = []
        self.construct_raises = None   # what MSAL's constructor raises (its authority discovery is a network call)
        self.renew_raises = {}         # address -> what acquire_token_silent raises (the renewal is a network call)

    def factory(self, client_id, authority, token_cache):
        rig = self
        rig.authorities.append((client_id, authority))
        if rig.construct_raises is not None:
            raise rig.construct_raises

        class App:
            def get_accounts(self, username=None):
                return [{"username": username}] if rig.accounts.get(username) else []

            def acquire_token_silent(self, scopes, account):
                rig.scopes_asked.append(list(scopes))
                address = account["username"]
                if address in rig.renew_raises:
                    raise rig.renew_raises[address]
                if address in rig.renew_fails:
                    return {"error": "invalid_grant", "error_description": "the refresh token has expired"}
                if address in rig.renews:
                    token_cache.has_state_changed = True
                return {"access_token": f"token-for-{address}"}

        return App()


class World:
    def __init__(self, screen, graph, sign_in, home, state, capsys):
        self.screen, self.graph, self.sign_in, self.home, self.state = screen, graph, sign_in, home, state
        self.capsys = capsys

    @property
    def base(self):
        return self.home / ".local" / "share" / "cc-director"

    def plant(self, address=MAILBOX, instance="default", content="{}"):
        """A saved sign-in where cc-outlook keeps one, and an account in it."""
        folder = self.base / "instances" / instance / "config" / "outlook" / "tokens"
        folder.mkdir(parents=True, exist_ok=True)
        path = folder / (address.replace("@", "_").replace(".", "_") + "_msal.json")
        path.write_text(content, encoding="utf-8")
        self.sign_in.accounts[address] = True
        return path

    def register(self, profiles, instance="default"):
        """cc-outlook's account list in one instance, in its own shape (tools/cc-outlook/src/auth.py, PROFILES_FILE):
        {"profiles": {<email>: {"client_id": ..., "nickname"?: ...}}, "default": <email>}."""
        folder = self.base / "instances" / instance / "config" / "outlook"
        folder.mkdir(parents=True, exist_ok=True)
        path = folder / "profiles.json"
        path.write_text(json.dumps({"profiles": profiles, "default": next(iter(profiles), None)}), encoding="utf-8")
        return path

    def memory(self, *channels, **extra):
        self.state.write_text(json.dumps(dict({"channels": list(channels)}, **extra)), encoding="utf-8")

    def read_memory(self):
        return json.loads(self.state.read_text(encoding="utf-8"))

    def run(self):
        """One run of the check. Returns (exit code, the output object or None, the context object or None,
        standard error)."""
        self.capsys.readouterr()
        code = self.screen.main()
        captured = self.capsys.readouterr()
        self.last_err = captured.err   # the run's standard error, for a test that cannot take it from run() itself
        if code != 0:
            assert captured.out == "", "a failed check printed an output"
            return code, None, None, captured.err
        output = json.loads(captured.out)
        assert set(output) == {"count", "context"}
        return code, output, json.loads(output["context"]), captured.err


def channel(address=MAILBOX, mode="observe", provider="outlook", **fields):
    return dict({"address": address, "provider": provider, "mode": mode}, **fields)


@pytest.fixture
def world(tmp_path, monkeypatch, capsys):
    for name in [n for n in os.environ if n.startswith("CC_")]:
        monkeypatch.delenv(name)
    home = tmp_path / "account-home"
    home.mkdir()
    monkeypatch.setenv("HOME", str(tmp_path / "scratch-home"))
    state = tmp_path / "memory.json"
    state.write_text("", encoding="utf-8")
    monkeypatch.setenv("CC_CHECK_STATE_FILE", str(state))

    screen = load_screen()
    graph, sign_in = FakeGraph(), FakeSignIn()
    monkeypatch.setattr(screen.urllib.request, "urlopen", graph.urlopen)
    monkeypatch.setattr(screen.msal, "PublicClientApplication", sign_in.factory)
    monkeypatch.setattr(screen, "account_record_home", lambda: str(home))
    monkeypatch.setattr(screen.socket, "gethostname", lambda: MACHINE)
    monkeypatch.setattr(screen, "utc_now", lambda: NOW)
    return World(screen, graph, sign_in, home, state, capsys)


def all_messages(context):
    return [m for section in context["channels"] for m in section["messages"]]


def entry_text(memory, address):
    """One channel's entry in the memory, written the way the check writes the memory (compact, UTF-8 as is)."""
    entry = next(c for c in memory["channels"] if c["address"].strip().lower() == address)
    return json.dumps(entry, ensure_ascii=False, separators=(",", ":"))


def channel_failed(world, address=MAILBOX):
    """Phase 3 (docs/qa/mail-desk/phase-3/CONTRACT.md 6.1): a failure that belongs to one channel fails that channel
    only. The run exits 0 with the failure in the channel's section, nothing reported or filed for it, and the
    channel's entry in the memory written back exactly as it was. Returns the sentence and the detail as two lines -
    where phase 2 put them on standard error - so the assertions on them read the same."""
    before = entry_text(world.read_memory(), address)
    code, output, context, _ = world.run()
    assert code == 0 and output is not None
    assert context["version"] == 2
    section = next(s for s in context["channels"] if s["address"] == address)
    assert section["messages"] == [] and section["filing"] == [] and section["more"] is False
    assert "filingNotCarriedOut" not in section
    assert set(section["failure"]) == {"sentence", "detail"}
    assert entry_text(world.read_memory(), address) == before
    return "\n".join([section["failure"]["sentence"], section["failure"]["detail"]])
