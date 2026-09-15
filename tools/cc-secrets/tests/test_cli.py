import getpass
import json
import sys

import pytest

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


def test_Add_WithoutUses_DefaultsToBothLoginAndRun(store):
    runner.invoke(cli.app, ["add", "web", "--username", "u", "--domains", "example.com", "--agents"],
                  input=new_secret() + "\n")

    assert store.get("web").uses == ["login", "run"]


def test_Help_SaysWhatTheProtectionCovers(plain):
    text = " ".join(plain(runner.invoke(cli.app, ["--help"]).output).split())

    assert "accidental exposure" in text
    assert "not against a hostile program running as the same user" in text


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


BACKSLASH = chr(92)


def test_IsMsysPtyPipeName_RecognisesOnlyAGitBashTerminal():
    assert cli.is_msys_pty_pipe_name(BACKSLASH + "msys-dd50a72ab4668b33-pty0-from-master")
    assert cli.is_msys_pty_pipe_name(BACKSLASH + "cygwin-e022582115c10879-pty3-from-master")
    assert not cli.is_msys_pty_pipe_name(BACKSLASH + "msys-dd50a72ab4668b33-5836-pipe-0x1")
    assert not cli.is_msys_pty_pipe_name("")


def test_Add_FromAGitBashTerminal_RefusedBeforeReadingAnything(store, monkeypatch, plain):
    monkeypatch.setattr(cli, "_stdin_is_mintty", lambda: True)

    result = runner.invoke(cli.app, ["add", "web", "--username", "u", "--domains", "example.com", "--agents"],
                           input="typed-visibly\n")

    assert result.exit_code == cli.EXIT_REFUSED
    assert "PowerShell or cmd" in " ".join(plain(_all_text(result)).split())
    assert store.get("web") is None


@pytest.mark.skipif(sys.platform != "win32", reason="Windows pipe names")
@pytest.mark.parametrize("pipe_name,expected", [
    ("msys-{hex}-pty7-from-master", "True"),
    ("cc-secrets-ordinary-pipe-{hex}", "False"),
])
def test_StdinIsMintty_ReadsTheNameOfTheRealPipeOnStandardInput(pipe_name, expected):
    import ctypes
    import msvcrt
    import os
    import secrets as token
    import subprocess
    from ctypes import wintypes

    from conftest import TOOL_DIR

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateNamedPipeW.restype = wintypes.HANDLE
    kernel32.CreateNamedPipeW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD,
                                          wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p]
    kernel32.CreateFileW.restype = wintypes.HANDLE
    kernel32.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                                     wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    invalid = ctypes.c_void_p(-1).value
    full_name = BACKSLASH * 2 + "." + BACKSLASH + "pipe" + BACKSLASH + pipe_name.format(hex=token.token_hex(8))
    PIPE_ACCESS_OUTBOUND, GENERIC_READ, OPEN_EXISTING = 0x2, 0x80000000, 3
    server = kernel32.CreateNamedPipeW(full_name, PIPE_ACCESS_OUTBOUND, 0, 1, 4096, 4096, 0, None)
    assert server not in (None, invalid)
    client = kernel32.CreateFileW(full_name, GENERIC_READ, 0, None, OPEN_EXISTING, 0, None)
    assert client not in (None, invalid)
    descriptor = msvcrt.open_osfhandle(client, os.O_RDONLY)
    try:
        code = f"import sys; sys.path.insert(0, {str(TOOL_DIR)!r}); import src.cli as c; print(c._stdin_is_mintty())"
        result = subprocess.run([sys.executable, "-c", code], stdin=descriptor, capture_output=True, text=True, timeout=60)
    finally:
        os.close(descriptor)
        kernel32.CloseHandle(server)

    assert result.stdout.strip() == expected, result.stderr


def test_Log_ShowsAuditLines(store):
    add_entry(store)
    runner.invoke(cli.app, ["run", "devlinux", "--", PY, "-c", "print(1)"])

    result = runner.invoke(cli.app, ["log", "--json"])

    lines = json.loads(result.stdout)["lines"]
    assert lines[-1]["entry"] == "devlinux"
    assert lines[-1]["command"].startswith("run ")
