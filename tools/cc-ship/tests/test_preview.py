"""The Vercel preview bypass for the verifier (issue 2935)."""

import json
import subprocess
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
import preview  # noqa: E402

PREVIEW = "https://x-preview.vercel.app"
CLI = "C:/tools/bin/cc-secrets.exe"


@pytest.fixture(autouse=True)
def cc_secrets_on_path(monkeypatch):
    """cc-secrets need not be installed where these tests run: its lookup answers a made-up path."""
    monkeypatch.setattr(preview.shutil, "which", lambda name: CLI if name == "cc-secrets" else None)


def test_bypass_command_VercelPreview_RunsCurlThroughCcSecretsWithNoSecretInIt():
    args = preview.bypass_command(PREVIEW)

    assert args[:6] == [CLI, "run", preview.SECRET_ENTRY, "--via", "stdin", "--"]
    assert args[6] == "curl"
    assert args[-1] == PREVIEW
    # curl reads the secret cc-secrets writes to its standard input and expands it into the header itself.
    assert args[args.index("--variable") + 1] == "bypass@-"
    assert args[args.index("--expand-header") + 1] == "x-vercel-protection-bypass: {{bypass:trim}}"


def test_bypass_command_CcSecretsIsACmdFile_NoArgumentCmdWouldMisread(monkeypatch):
    # On Windows cc-secrets is a .cmd file; a percent sign in any argument would be re-read by cmd.exe.
    monkeypatch.setattr(preview.shutil, "which", lambda name: "C:/bin/cc-secrets.cmd")
    args = preview.bypass_command(PREVIEW)
    assert not any(preview.fleet._cmd_misreads(a) for a in args[1:])


def test_bypass_command_CmdFileAndAnAddressCmdWouldSplit_Refuses(monkeypatch):
    monkeypatch.setattr(preview.shutil, "which", lambda name: "C:/bin/cc-secrets.cmd")
    with pytest.raises(preview.PreviewError, match="cmd.exe would misread"):
        preview.bypass_command(PREVIEW + "/?a=1&b=2")


def test_bypass_command_CcSecretsNotInstalled_RaisesSayingSo(monkeypatch):
    monkeypatch.setattr(preview.shutil, "which", lambda name: None)
    with pytest.raises(preview.PreviewError, match="cc-secrets is not on PATH"):
        preview.bypass_command(PREVIEW)


@pytest.mark.parametrize("url", [
    "https://example.com",
    "http://x-preview.vercel.app",
    "https://vercel.app.example.com",
    "https://x-preview.vercel.app.example.com",
])
def test_bypass_command_NotAVercelPreview_RefusesToSendTheSecret(url):
    with pytest.raises(preview.PreviewError, match="Refusing to send"):
        preview.bypass_command(url)


def test_write_bypass_state_CookieReturned_WritesStateWithTheCookieOnly(tmp_path, monkeypatch):
    seen = {}

    def fake_run(args, **_kwargs):
        seen["args"] = args
        return subprocess.CompletedProcess(args, 0, stdout=(
            "HTTP/2 307\r\nset-cookie: _vercel_jwt=abc.def; Max-Age=604800; Path=/\r\n"), stderr="")

    monkeypatch.setattr(preview.subprocess, "run", fake_run)
    state_file = tmp_path / "state.json"
    preview.write_bypass_state(PREVIEW, state_file)

    state = json.loads(state_file.read_text(encoding="ascii"))
    assert state["cookies"][0]["value"] == "abc.def"
    assert state["cookies"][0]["domain"] == "x-preview.vercel.app"
    assert seen["args"] == preview.bypass_command(PREVIEW)


def test_write_bypass_state_CcSecretsRefuses_RaisesNamingTheEntry(tmp_path, monkeypatch):
    monkeypatch.setattr(preview.subprocess, "run", lambda args, **_k: subprocess.CompletedProcess(
        args, 2, stdout="", stderr="refused: No secret named 'vercel-automation-bypass-secret' is available."))
    with pytest.raises(preview.PreviewError, match="cc-secrets add vercel-automation-bypass-secret"):
        preview.write_bypass_state(PREVIEW, tmp_path / "state.json")
    assert not (tmp_path / "state.json").exists()


def test_write_bypass_state_NoCookie_Raises(tmp_path, monkeypatch):
    monkeypatch.setattr(preview.subprocess, "run", lambda args, **_k: subprocess.CompletedProcess(
        args, 0, stdout="HTTP/2 302\r\nlocation: https://vercel.com/sso-api\r\n", stderr=""))
    with pytest.raises(preview.PreviewError, match="no _vercel_jwt"):
        preview.write_bypass_state(PREVIEW, tmp_path / "state.json")


def test_find_preview_url_OnlyFailedStatus_ReturnsNone(monkeypatch):
    # Observed live: a preview whose build failed still carries an environment_url.
    responses = iter([json.dumps([{"id": 7}]),
                      json.dumps([{"state": "failure", "environment_url": "https://x"}])])
    monkeypatch.setattr(preview.subprocess, "run", lambda args, **_k: subprocess.CompletedProcess(
        args, 0, stdout=next(responses), stderr=""))
    assert preview.find_preview_url("o/r", "abc") is None


def test_find_preview_url_LatestFailedOverOlderSuccess_ReturnsNone(monkeypatch):
    # Inspection finding 3: statuses are newest first; only the latest one counts.
    responses = iter([json.dumps([{"id": 7}]), json.dumps([
        {"state": "failure", "environment_url": "https://x"},
        {"state": "success", "environment_url": "https://x"},
    ])])
    monkeypatch.setattr(preview.subprocess, "run", lambda args, **_k: subprocess.CompletedProcess(
        args, 0, stdout=next(responses), stderr=""))
    assert preview.find_preview_url("o/r", "abc") is None


def test_find_preview_url_LatestSuccess_ReturnsUrl(monkeypatch):
    responses = iter([json.dumps([{"id": 7}]), json.dumps([
        {"state": "success", "environment_url": "https://ok"},
        {"state": "in_progress", "environment_url": None},
    ])])
    monkeypatch.setattr(preview.subprocess, "run", lambda args, **_k: subprocess.CompletedProcess(
        args, 0, stdout=next(responses), stderr=""))
    assert preview.find_preview_url("o/r", "abc") == "https://ok"


def _fake_cookie_run(args, **_kwargs):
    return subprocess.CompletedProcess(args, 0, stdout="set-cookie: _vercel_jwt=abc; Path=/\r\n",
                                       stderr="")


def test_bypass_state_FailureInsideBlock_FileRemoved(tmp_path, monkeypatch):
    # Inspection finding 4 / re-inspection finding 3: the cookie never outlives the verifier.
    monkeypatch.setattr(preview.subprocess, "run", _fake_cookie_run)
    state = tmp_path / "browser-state.json"
    with pytest.raises(OSError):
        with preview.bypass_state("https://x-preview.vercel.app", state) as path:
            assert path.exists()
            raise OSError("disk full while writing the brief")
    assert not state.exists()


def test_bypass_state_NormalExit_FileRemoved(tmp_path, monkeypatch):
    monkeypatch.setattr(preview.subprocess, "run", _fake_cookie_run)
    state = tmp_path / "browser-state.json"
    with preview.bypass_state("https://x-preview.vercel.app", state):
        pass
    assert not state.exists()
