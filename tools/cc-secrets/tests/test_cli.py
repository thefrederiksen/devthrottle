import getpass
import json
import sys

from typer.testing import CliRunner

from conftest import add_entry, new_secret
from src import cli, paths
from src.audit import AuditLog
from src.redact import REDACTED

runner = CliRunner()
PY = sys.executable


def _all_text(result):
    return result.output + (result.stderr if result.stderr_bytes is not None else "")


def _audit_lines():
    return AuditLog(paths.audit_path()).read(1000)


def test_List_AsAgent_ShowsOnlyAgentEntries_NeverTheSecret(store, plain):
    shared = add_entry(store, name="shared")
    kept = add_entry(store, name="kept-back", agents=False)

    result = runner.invoke(cli.app, ["list"])

    text = plain(_all_text(result))
    assert result.exit_code == 0
    assert "shared" in text and "kept-back" not in text
    assert shared not in text and kept not in text


def test_ListJson_CarriesNoSecretField(store):
    secret = add_entry(store)

    result = runner.invoke(cli.app, ["list", "--json"])

    entries = json.loads(result.stdout)["entries"]
    assert [e["name"] for e in entries] == ["devlinux"]
    assert "secret" not in entries[0]
    assert secret not in result.stdout


def test_ListAll_InsideSession_Refused(store, monkeypatch):
    add_entry(store, name="kept-back", agents=False)
    monkeypatch.setenv("CC_SESSION_ID", "some-session")

    result = runner.invoke(cli.app, ["list", "--all"])

    assert result.exit_code == cli.EXIT_REFUSED
    assert "kept-back" not in result.output


def test_Add_InsideSession_Refused_NothingSaved(store, monkeypatch):
    monkeypatch.setenv("CC_SESSION_ID", "some-session")

    result = runner.invoke(cli.app, ["add", "web", "--username", "u", "--domains", "example.com", "--agents"],
                           input=new_secret() + "\n")

    assert result.exit_code == cli.EXIT_REFUSED
    assert store.get("web") is None


def test_Add_PipedSecret_IsSaved_AndNeverEchoed(store):
    secret = new_secret()

    result = runner.invoke(cli.app, ["add", "web", "--username", "u", "--domains", "example.com", "--agents"],
                           input=secret + "\n")

    assert result.exit_code == 0, _all_text(result)
    assert store.get("web").secret.reveal() == secret
    assert secret not in _all_text(result)
    assert _audit_lines()[-1]["command"] == "add"


def test_Add_PipedSecretWithCarriageReturn_IsTrimmed(store):
    secret = new_secret()

    runner.invoke(cli.app, ["add", "web", "--username", "u", "--domains", "", "--no-agents"], input=secret + "\r\n")

    assert store.get("web").secret.reveal() == secret


def test_Add_PipedWithoutTheOtherFields_Refused(store):
    result = runner.invoke(cli.app, ["add", "web"], input=new_secret() + "\n")

    assert result.exit_code == cli.EXIT_FAILED
    assert "--username" in _all_text(result)
    assert store.get("web") is None


def test_Add_PipedMultipleLines_Refused(store):
    result = runner.invoke(cli.app, ["add", "web", "--username", "u", "--domains", "", "--agents"],
                           input="line-one\nline-two\n")

    assert result.exit_code == cli.EXIT_FAILED
    assert store.get("web") is None


def test_Add_OffersNoWayToPassTheSecretAsAnArgument(store, plain):
    help_text = plain(runner.invoke(cli.app, ["add", "--help"]).output)

    result = runner.invoke(cli.app, ["add", "web", "--secret", "abcdefgh"])

    assert "--secret" not in help_text and "--password" not in help_text
    assert result.exit_code == 2
    assert store.get("web") is None


def test_Add_HiddenPrompt_TypedTwice_IsSaved(store, monkeypatch):
    secret = new_secret()
    prompts = []
    monkeypatch.setattr(cli, "_stdin_is_tty", lambda: True)
    monkeypatch.setattr(getpass, "getpass", lambda prompt="": prompts.append(prompt) or secret)

    result = runner.invoke(cli.app, ["add", "web", "--username", "u", "--domains", "example.com",
                                     "--agents", "--notes", ""])

    assert result.exit_code == 0, _all_text(result)
    assert len(prompts) == 2
    assert store.get("web").secret.reveal() == secret


def test_Add_HiddenPrompt_Mismatch_NothingSaved(store, monkeypatch):
    answers = iter([new_secret(), new_secret()])
    monkeypatch.setattr(cli, "_stdin_is_tty", lambda: True)
    monkeypatch.setattr(getpass, "getpass", lambda prompt="": next(answers))

    result = runner.invoke(cli.app, ["add", "web", "--username", "u", "--domains", "", "--agents", "--notes", ""])

    assert result.exit_code == cli.EXIT_FAILED
    assert "did not match" in _all_text(result)
    assert store.get("web") is None


def test_Add_PromptThatCannotHideInput_IsRefused(store, monkeypatch):
    import warnings

    def visible_prompt(prompt=""):
        warnings.warn("Can not control echo on the terminal.", getpass.GetPassWarning)
        return "typed-visibly"

    monkeypatch.setattr(cli, "_stdin_is_tty", lambda: True)
    monkeypatch.setattr(getpass, "getpass", visible_prompt)

    result = runner.invoke(cli.app, ["add", "web", "--username", "u", "--domains", "", "--agents", "--notes", ""])

    assert result.exit_code == cli.EXIT_FAILED
    assert store.get("web") is None


def test_Add_ExistingWithoutReplace_Refused(store):
    original = add_entry(store, name="web")

    result = runner.invoke(cli.app, ["add", "web", "--username", "u", "--domains", "", "--agents"],
                           input=new_secret() + "\n")

    assert result.exit_code == cli.EXIT_FAILED
    assert store.get("web").secret.reveal() == original


def test_Remove_InsideSession_Refused(store, monkeypatch):
    add_entry(store, name="web")
    monkeypatch.setenv("CC_SESSION_ID", "some-session")

    result = runner.invoke(cli.app, ["remove", "web", "--yes"])

    assert result.exit_code == cli.EXIT_REFUSED
    assert store.get("web") is not None


def test_Run_KeptBackEntry_RefusedAndAudited(store, monkeypatch):
    add_entry(store, name="kept-back", agents=False)
    monkeypatch.setenv("CC_SESSION_ID", "agent-session")

    result = runner.invoke(cli.app, ["run", "kept-back", "--", PY, "-c", "print(1)"])

    assert result.exit_code == cli.EXIT_REFUSED
    line = _audit_lines()[-1]
    assert (line["entry"], line["outcome"], line["session"]) == ("kept-back", "refused", "agent-session")


def test_Run_LoginOnlyEntry_Refused(store):
    add_entry(store, name="website", uses=("login",))

    result = runner.invoke(cli.app, ["run", "website", "--", PY, "-c", "print(1)"])

    assert result.exit_code == cli.EXIT_REFUSED
    assert "not allowed for 'run'" in _all_text(result)


def test_Run_ScrubsOutput_AndAuditsTheExitCode(store, monkeypatch):
    secret = add_entry(store)
    monkeypatch.setenv("CC_SESSION_ID", "agent-session")

    result = runner.invoke(cli.app, ["run", "devlinux", "--", PY, "-c", "import sys; print('got ' + sys.stdin.readline().strip())"])

    assert result.exit_code == 0
    assert f"got {REDACTED}" in result.output
    assert secret not in _all_text(result)
    line = _audit_lines()[-1]
    assert (line["outcome"], line["detail"], line["session"]) == ("ok", "exit 0", "agent-session")
    assert secret not in json.dumps(line)


def test_Run_NonZeroExit_IsPassedThrough(store):
    add_entry(store)

    result = runner.invoke(cli.app, ["run", "devlinux", "--via", "env", "--", PY, "-c", "raise SystemExit(5)"])

    assert result.exit_code == 5
    assert _audit_lines()[-1]["outcome"] == "failed"


def test_Login_KeptBackEntry_Refused(store):
    add_entry(store, name="kept-back", agents=False)

    result = runner.invoke(cli.app, ["login", "kept-back", "--browser", "agent-browser"])

    assert result.exit_code == cli.EXIT_REFUSED


def test_Login_WithoutDirector_FailsWithReason(store, monkeypatch):
    add_entry(store)
    monkeypatch.delenv("CC_DIRECTOR_ID", raising=False)

    result = runner.invoke(cli.app, ["login", "devlinux", "--browser", "agent-browser"])

    assert result.exit_code == cli.EXIT_FAILED
    assert "CC_DIRECTOR_ID" in _all_text(result)
    assert _audit_lines()[-1]["outcome"] == "failed"


def test_Log_ShowsAuditLines(store):
    add_entry(store)
    runner.invoke(cli.app, ["run", "devlinux", "--", PY, "-c", "print(1)"])

    result = runner.invoke(cli.app, ["log", "--json"])

    lines = json.loads(result.stdout)["lines"]
    assert lines[-1]["entry"] == "devlinux"
    assert lines[-1]["command"].startswith("run ")
