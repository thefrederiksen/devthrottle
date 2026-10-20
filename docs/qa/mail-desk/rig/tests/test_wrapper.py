"""Tests of the Mail Desk rig's wrapper, docs/qa/mail-desk/rig/checks/screen.py.

The wrapper is copied into a scratch rig folder laid out as the rig lays it out (the marker, checks/screen.py beside
checks/real_screen.py - the template's real check, byte for byte - and files/rig.json), loaded from there, and run
in-process. The network is the rig's own FakeGraph (run_qa.py), reached through a stand-in for the real urlopen, so
nothing here opens a port or reaches Microsoft, and the owner's real sign-in is never read.

    uv run --no-project --with pytest --with "msal>=1.28" python -m pytest docs/qa/mail-desk/rig/tests -v
"""

import importlib.util
import io
import json
import shutil
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

import pytest

sys.dont_write_bytecode = True

RIG_DIR = Path(__file__).resolve().parents[1]
REPO = RIG_DIR.parents[3]
WRAPPER = RIG_DIR / "checks" / "screen.py"
REAL = REPO / "src" / "CcDirector.Gateway" / "MailDesk" / "Template" / "checks" / "screen.py"
ONE = "qa-one@example.com"
FAKE = "http://127.0.0.1:47001"


def load(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


RUN_QA = load(RIG_DIR / "run_qa.py", "run_qa")


class Response:
    def __init__(self, raw):
        self.raw = raw

    def read(self):
        return self.raw

    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False


class Network:
    """Stands in for the real urlopen the wrapper hands its rewritten request to: answers from the rig's FakeGraph and
    records every address asked."""

    def __init__(self):
        self.graph = RUN_QA.FakeGraph()
        self.asked = []

    def urlopen(self, request, timeout=None):
        url = urllib.parse.urlsplit(request.full_url)
        self.asked.append((request.get_method(), request.full_url, request.get_header("Authorization")))
        query = {k: v[0] for k, v in urllib.parse.parse_qs(url.query).items()}
        body = request.data.decode("utf-8") if request.data else None
        token = (request.get_header("Authorization") or "").removeprefix("Bearer ")
        status, answer = self.graph.answer(request.get_method(), url.path, query, body, token)
        data = answer if isinstance(answer, bytes) else json.dumps(answer).encode("utf-8")
        if status >= 400:
            raise urllib.error.HTTPError(request.full_url, status, "error", {}, io.BytesIO(data))
        return Response(data)


@pytest.fixture
def rig(tmp_path, monkeypatch, capsys):
    root = tmp_path / "rig"
    (root / "definition" / "checks").mkdir(parents=True)
    (root / "definition" / "files").mkdir()
    (root / "sign-in").mkdir()
    (root / "mail-desk-rig.marker").write_text("x", encoding="utf-8")
    shutil.copyfile(WRAPPER, root / "definition" / "checks" / "screen.py")
    shutil.copyfile(REAL, root / "definition" / "checks" / "real_screen.py")
    state = tmp_path / "memory.json"
    monkeypatch.setenv("CC_CHECK_STATE_FILE", str(state))
    network = Network()
    monkeypatch.setattr(urllib.request, "urlopen", network.urlopen)

    class Rig:
        folder, memory, net = root, state, network

        def settings(self, sign_in=None, fake=FAKE, rig_folder=None):
            (root / "definition" / "files" / "rig.json").write_text(json.dumps({
                "rigFolder": str(rig_folder or root), "signInFolder": str(sign_in or root / "sign-in"),
                "fakeGraph": fake}), encoding="utf-8")

        def sign_in(self, address):
            tokens = root / "sign-in" / "instances" / "default" / "config" / "outlook" / "tokens"
            tokens.mkdir(parents=True, exist_ok=True)
            (tokens / (address.replace("@", "_").replace(".", "_") + "_msal.json")).write_text("{}", encoding="utf-8")

        def channels(self, *entries):
            state.write_text(json.dumps({"channels": list(entries)}), encoding="utf-8")

        def run(self, wrapper=None):
            # Each run replaces urllib.request.urlopen with its own; the next run starts from the stand-in again.
            monkeypatch.setattr(urllib.request, "urlopen", network.urlopen)
            capsys.readouterr()
            module = load(wrapper or root / "definition" / "checks" / "screen.py", "rig_wrapper")
            with pytest.raises(SystemExit) as stopped:
                sys.exit(module.main())
            out = capsys.readouterr()
            return stopped.value.code, out.out, out.err

    return Rig()


def channel(address=ONE, mode="observe", **fields):
    return dict({"address": address, "provider": "outlook", "mode": mode}, **fields)


def test_Run_SignedInMailbox_ReadsTheRealCheckThroughTheFakeGraphOnLoopbackOnly(rig):
    # Arrange
    rig.settings()
    rig.sign_in(ONE)
    rig.channels(channel())
    rig.net.graph.put(ONE, "inbox", RUN_QA.mail("someone@client.example", "Hello there"))

    # Act
    code, out, err = rig.run()

    # Assert
    assert code == 0, err
    output = json.loads(out)
    context = json.loads(output["context"])
    assert output["count"] == 1
    assert [m["subject"] for m in context["channels"][0]["messages"]] == ["Hello there"]
    assert rig.net.asked, "the fake Graph was never asked"
    assert all(url.startswith(FAKE + "/v1.0/me/") for _, url, _ in rig.net.asked)
    assert all(auth == f"Bearer rig-token-for-{ONE}" for _, _, auth in rig.net.asked)
    assert json.loads(rig.memory.read_text(encoding="utf-8"))["channels"][0]["cursor"]


def test_Run_NotSignedIn_TheRealChecksFailureNamesTheFixAndNothingIsAsked(rig):
    # Arrange
    rig.settings()
    rig.channels(channel())

    # Act
    code, out, err = rig.run()

    # Assert
    assert code == 0, err
    failure = json.loads(json.loads(out)["context"])["channels"][0]["failure"]
    assert f"{ONE} is not signed in on" in failure["sentence"]
    assert "cc-outlook accounts add" in failure["sentence"]
    assert str(rig.folder / "sign-in") in failure["detail"]
    assert rig.net.asked == []


def test_Run_LiveFilingQueue_TheMoveGoesToTheFakeGraph(rig):
    # Arrange: a first run to give the channel its cursor and keys, then a queued junk decision
    rig.settings()
    rig.sign_in(ONE)
    rig.channels(channel(mode="live"))
    email = RUN_QA.mail("deals@spam.example.net", "Cheap watches")
    rig.net.graph.put(ONE, "inbox", email)
    assert rig.run()[0] == 0
    memory = json.loads(rig.memory.read_text(encoding="utf-8"))
    key = next(iter(memory["channels"][0]["keys"]))
    memory["channels"][0]["filingQueue"] = [{"messageKey": key, "decision": "junk"}]
    rig.memory.write_text(json.dumps(memory), encoding="utf-8")

    # Act
    code, out, err = rig.run()

    # Assert
    assert code == 0, err
    filing = json.loads(json.loads(out)["context"])["channels"][0]["filing"]
    assert [(f["decision"], f["result"]) for f in filing] == [("junk", "moved")]
    posts = [(m, u) for m, u, _ in rig.net.asked if m == "POST"]
    assert posts == [("POST", f"{FAKE}/v1.0/me/messages/{email['id']}/move")]
    assert rig.net.graph.folder_of(ONE, email["id"]) == "junkemail"


@pytest.mark.parametrize("case", ["sign-in outside", "sign-in is the real Director folder", "fake not loopback",
                                  "fake https", "no marker", "wrapper outside"])
def test_Run_AnyUnsafeSetting_RefusesBeforeReadingAnything(rig, tmp_path, case):
    # Arrange
    rig.channels(channel())
    wrapper = None
    if case == "sign-in outside":
        rig.settings(sign_in=tmp_path / "elsewhere")
    elif case == "sign-in is the real Director folder":
        rig.settings(sign_in=Path.home() / ".local" / "share" / "cc-director")
    elif case == "fake not loopback":
        rig.settings(fake="http://192.0.2.10:8080")
    elif case == "fake https":
        rig.settings(fake="https://127.0.0.1:47001")
    elif case == "no marker":
        rig.settings()
        (rig.folder / "mail-desk-rig.marker").unlink()
    else:
        rig.settings()
        outside = tmp_path / "outside" / "checks"
        outside.mkdir(parents=True)
        shutil.copyfile(WRAPPER, outside / "screen.py")
        shutil.copyfile(REAL, outside / "real_screen.py")
        (outside.parent / "files").mkdir()
        shutil.copyfile(rig.folder / "definition" / "files" / "rig.json", outside.parent / "files" / "rig.json")
        wrapper = outside / "screen.py"

    # Act
    code, out, err = rig.run(wrapper)

    # Assert
    assert code == 1
    assert out == ""
    assert err.startswith("the Mail Desk rig's wrapper refuses to run:")
    assert rig.net.asked == []


def test_FakeModel_AnswersTheScriptedLetterWithLogProbabilitiesAndRefusesAnotherKey():
    # Arrange
    model = RUN_QA.FakeModel()
    model.say("Where is my invoice?", spam="person", route="bookkeeping", confidence=0.93)
    prompt = ("Question: which route should handle this email?\n\nLabels:\nA. customer support questions\n"
              "B. bookkeeping: invoices\nC. none of the above, or you cannot tell\n\nEmail:\nFrom: A <a@client.example>\n"
              "Subject: Where is my invoice?\nStart of the body: x\n\nAnswer with one letter: A, B or C.")

    class Handler:
        command, path = "POST", "/v1/chat/completions"

        def __init__(self, key):
            self.headers = {"Authorization": f"Bearer {key}"}

    body = json.dumps({"model": "devthrottle/wingman-fast", "logprobs": True,
                       "messages": [{"role": "system", "content": "s"}, {"role": "user", "content": prompt}]})

    # Act
    status, answer = model.handle(Handler(RUN_QA.MODEL_KEY), body)
    refused, _ = model.handle(Handler("another-key"), body)

    # Assert
    assert status == 200
    assert answer["choices"][0]["message"]["content"] == "B"
    assert abs(answer["choices"][0]["logprobs"]["content"][0]["logprob"] - __import__("math").log(0.93)) < 1e-9
    assert answer["usage"]["prompt_tokens"] > 0
    assert refused == 401
