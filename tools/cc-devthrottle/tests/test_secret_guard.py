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


def _spawn_with_prompt(prompt):
    """spawn_session with every option at its default and only the first prompt set."""
    import inspect

    params = inspect.signature(session_ops.spawn_session).parameters
    kwargs = {name: (p.default if p.default is not inspect.Parameter.empty else None) for name, p in params.items()}
    kwargs.update(repo="D:/repos/example", prompt=prompt)
    return session_ops.spawn_session(**kwargs)


@pytest.mark.parametrize("what, call", [
    ("prompt", lambda: session_ops.prompt_session("abc12345", "the value")),
    ("reason", lambda: session_ops.raise_hand("the value")),
    ("report", lambda: session_ops.report_to_parent("the value")),
    ("first prompt", lambda: _spawn_with_prompt("the value")),
])
def test_EveryOtherTextPath_IsCheckedBeforeAnythingIsReadOrSent(cc_secrets, monkeypatch, capsys, what, call):
    # A handback, a typed prompt, a raised hand's reason and a new session's first prompt all carry free text to
    # another session or the owner, exactly as a message does.
    cc_secrets["answer"] = Ran(json.dumps({"containsSecret": True}) + "\n")
    touched = []
    monkeypatch.setattr(session_ops.gateway, "post_json", lambda *a, **k: touched.append(("post", a)) or {})
    monkeypatch.setattr(session_ops.gateway, "get_json", lambda *a, **k: touched.append(("get", a)) or {})

    code, err = _refused(capsys, call)

    assert code == 1
    assert f"this {what} contains a value stored in cc-secrets" in err
    assert touched == []
    assert cc_secrets["calls"][0][1] == "the value"


def test_AScheduleSeed_And_ATriggerPrompt_AreChecked_BeforeTheyAreStored(cc_secrets, monkeypatch, capsys):
    # Both are the first prompt of sessions started later: a message delivered on a timer.
    from src import schedule_ops, trigger_ops

    cc_secrets["answer"] = Ran(json.dumps({"containsSecret": True}) + "\n")
    stored = []
    monkeypatch.setattr(schedule_ops.gateway, "post_json", lambda *a, **k: stored.append(a) or {}, raising=False)
    monkeypatch.setattr(trigger_ops, "_client", lambda: (_ for _ in ()).throw(AssertionError("reached the Gateway")))

    seed_code, seed_err = _refused(capsys, lambda: schedule_ops.create_job(
        "nightly", "SOREN_NORTH", "D:/repos/example", None, "0 3 * * *", "UTC", "the value", None, "none", None,
        False))
    prompt_code, prompt_err = _refused(capsys, lambda: trigger_ops.add(
        "watch", "factory-a", "agent-a", "SOREN_NORTH", "D:/repos/example", "true", "10m", "the value", False, False))

    assert (seed_code, prompt_code) == (1, 1)
    assert "this seed contains a value stored in cc-secrets" in seed_err
    assert "this prompt contains a value stored in cc-secrets" in prompt_err
    assert stored == []
