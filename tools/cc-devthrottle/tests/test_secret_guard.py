"""A fleet message never carries a stored secret (the Secret Handoff mission, plan section 4.7)."""

import json
import subprocess

import pytest
import typer

from src import secret_guard, session_ops

pytestmark = pytest.mark.real_secret_guard


class Ran:
    def __init__(self, stdout="", returncode=0):
        self.stdout, self.returncode, self.stderr = stdout, returncode, ""


@pytest.fixture
def cc_secrets(monkeypatch):
    """Stand in for the cc-secrets executable; records what it was given."""
    state = {"answer": Ran(json.dumps({"containsSecret": False}) + "\n"), "calls": []}
    monkeypatch.setattr(secret_guard.shutil, "which", lambda name: "C:/tools/cc-secrets.exe")

    def run(args, input=None, **kwargs):
        state["calls"].append((args, input))
        if isinstance(state["answer"], Exception):
            raise state["answer"]
        return state["answer"]

    monkeypatch.setattr(secret_guard.subprocess, "run", run)
    return state


def _refused(capsys, call):
    with pytest.raises((SystemExit, typer.Exit)) as stopped:
        call()
    code = getattr(stopped.value, "exit_code", getattr(stopped.value, "code", None))
    return code, capsys.readouterr().err


def test_ACleanMessage_PassesTheCheck_AndTheTextGoesOnStandardInputOnly(cc_secrets):
    secret_guard.refuse_if_it_carries_a_secret("deploy is done", "message")

    args, given = cc_secrets["calls"][0]
    assert args == ["C:/tools/cc-secrets.exe", "check-text"]
    assert given == "deploy is done"


def test_AMessageCarryingAStoredValue_IsNotSent(cc_secrets, capsys):
    cc_secrets["answer"] = Ran(json.dumps({"containsSecret": True}) + "\n")

    code, err = _refused(capsys, lambda: secret_guard.refuse_if_it_carries_a_secret("pw is hunter2-made-up", "message"))

    assert code == 1
    assert "contains a value stored in cc-secrets" in err and "cc-secrets request" in err
    assert "hunter2-made-up" not in err


@pytest.mark.parametrize("answer", [Ran("", 2), Ran("Usage: cc-secrets [OPTIONS] COMMAND\n", 2), Ran("{}\n"),
                                    subprocess.TimeoutExpired("cc-secrets", 30)])
def test_WhenCcSecretsCannotSay_TheMessageIsNotSent(cc_secrets, capsys, answer):
    cc_secrets["answer"] = answer

    code, err = _refused(capsys, lambda: secret_guard.refuse_if_it_carries_a_secret("hello", "reply"))

    assert code == 1
    assert "could not check this reply" in err


def test_WithoutCcSecrets_TheMessageIsNotSent(monkeypatch, capsys):
    monkeypatch.setattr(secret_guard.shutil, "which", lambda name: None)

    code, err = _refused(capsys, lambda: secret_guard.refuse_if_it_carries_a_secret("hello", "message"))

    assert code == 1
    assert "cc-secrets is not installed" in err


def test_MessageSend_And_Reply_AreCheckedBeforeAnythingIsQueued(cc_secrets, monkeypatch, capsys):
    cc_secrets["answer"] = Ran(json.dumps({"containsSecret": True}) + "\n")
    posted = []
    monkeypatch.setattr(session_ops.gateway, "post_json", lambda *a, **k: posted.append(a) or {})

    send_code, _ = _refused(capsys, lambda: session_ops.send_message("abc12345", "the value"))
    reply_code, _ = _refused(capsys, lambda: session_ops.send_reply("0123456789abcdef", "the value"))

    assert (send_code, reply_code) == (1, 1)
    assert posted == []
