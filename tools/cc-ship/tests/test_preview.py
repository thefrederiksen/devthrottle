"""The Vercel preview bypass for the verifier (issue 2935)."""

import json
import subprocess
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
import preview  # noqa: E402

SECRET = "s3cret-value-for-tests"


@pytest.fixture
def creds(tmp_path, monkeypatch):
    path = tmp_path / "credentials.env"
    monkeypatch.setattr(preview, "credentials_file", lambda: path)
    return path


def test_read_bypass_secret_Present_ReturnsIt(creds):
    creds.write_text(f"OTHER=1\n{preview.SECRET_NAME}={SECRET}\n", encoding="ascii")
    assert preview.read_bypass_secret() == SECRET


def test_read_bypass_secret_Missing_RaisesWithFix(creds):
    creds.write_text("OTHER=1\n", encoding="ascii")
    with pytest.raises(preview.PreviewError, match="Protection Bypass for Automation"):
        preview.read_bypass_secret()


def test_write_bypass_state_CookieReturned_WritesStateWithoutSecret(creds, tmp_path, monkeypatch):
    creds.write_text(f"{preview.SECRET_NAME}={SECRET}\n", encoding="ascii")
    seen = {}

    def fake_run(args, **_kwargs):
        seen["args"] = args
        headers = Path(args[args.index("-H") + 1][1:]).read_text(encoding="ascii")
        seen["headers"] = headers
        return subprocess.CompletedProcess(args, 0, stdout=(
            "HTTP/2 307\r\nset-cookie: _vercel_jwt=abc.def; Max-Age=604800; Path=/\r\n"), stderr="")

    monkeypatch.setattr(preview.subprocess, "run", fake_run)
    state_file = tmp_path / "state.json"
    preview.write_bypass_state("https://x-preview.vercel.app", state_file)

    state = json.loads(state_file.read_text(encoding="ascii"))
    assert state["cookies"][0]["value"] == "abc.def"
    assert state["cookies"][0]["domain"] == "x-preview.vercel.app"
    assert SECRET not in " ".join(seen["args"])  # never on the command line
    assert SECRET in seen["headers"]
    assert SECRET not in state_file.read_text(encoding="ascii")


def test_write_bypass_state_NoCookie_Raises(creds, tmp_path, monkeypatch):
    creds.write_text(f"{preview.SECRET_NAME}={SECRET}\n", encoding="ascii")
    monkeypatch.setattr(preview.subprocess, "run", lambda args, **_k: subprocess.CompletedProcess(
        args, 0, stdout="HTTP/2 302\r\nlocation: https://vercel.com/sso-api\r\n", stderr=""))
    with pytest.raises(preview.PreviewError, match="no _vercel_jwt"):
        preview.write_bypass_state("https://x-preview.vercel.app", tmp_path / "state.json")


def test_find_preview_url_OnlyFailedStatus_ReturnsNone(monkeypatch):
    # Observed live: a preview whose build failed still carries an environment_url.
    responses = iter([json.dumps([{"id": 7}]),
                      json.dumps([{"state": "failure", "environment_url": "https://x"}])])
    monkeypatch.setattr(preview.subprocess, "run", lambda args, **_k: subprocess.CompletedProcess(
        args, 0, stdout=next(responses), stderr=""))
    assert preview.find_preview_url("o/r", "abc") is None
