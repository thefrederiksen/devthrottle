import base64
import socket
import sys

import pytest

from conftest import add_entry
from src.redact import REDACTED
from src.runner import _AskpassListener, run_with_secret

PY = sys.executable


def _entry(store):
    secret = add_entry(store)
    return store.get("devlinux"), secret


def test_ViaStdin_CommandReceivesSecret_OutputIsScrubbed(store):
    entry, secret = _entry(store)
    script = "import sys; s=sys.stdin.readline().rstrip('\\n'); print('length', len(s)); print('echo', s)"

    result = run_with_secret(entry, [PY, "-c", script], "stdin")

    assert result.exit_code == 0
    assert f"length {len(secret)}" in result.stdout
    assert f"echo {REDACTED}" in result.stdout
    assert secret not in result.stdout + result.stderr


def test_ViaEnv_CommandReceivesSecret_OutputIsScrubbed(store):
    entry, secret = _entry(store)
    script = "import os,sys; v=os.environ['CC_SECRET']; print(len(v)); sys.stderr.write(v)"

    result = run_with_secret(entry, [PY, "-c", script], "env")

    assert result.stdout.strip() == str(len(secret))
    assert result.stderr == REDACTED


def test_ViaAskpass_HelperDeliversSecret_OutputIsScrubbed(store):
    entry, secret = _entry(store)
    script = (
        "import os,subprocess,sys\n"
        "helper=os.environ['SUDO_ASKPASS']\n"
        "argv=['cmd','/c',helper] if sys.platform=='win32' else [helper]\n"
        "got=subprocess.run(argv,capture_output=True,text=True).stdout.strip()\n"
        "print('length', len(got))\n"
        "print('echo', got)\n"
    )

    result = run_with_secret(entry, [PY, "-c", script], "askpass")

    assert f"length {len(secret)}" in result.stdout
    assert secret not in result.stdout
    assert "CC_SECRET" not in result.stdout


def test_EncodedEchoes_AreScrubbed(store):
    entry, secret = _entry(store)
    script = ("import sys,base64,urllib.parse; s=sys.stdin.readline().rstrip('\\n');"
              "print(base64.b64encode(s.encode()).decode()); print(base64.b64encode(('leak-user:'+s).encode()).decode());"
              "print(urllib.parse.quote(s))")

    result = run_with_secret(entry, [PY, "-c", script], "stdin")

    assert base64.b64encode(secret.encode()).decode() not in result.stdout
    assert base64.b64encode(f"leak-user:{secret}".encode()).decode() not in result.stdout
    assert result.stdout.count(REDACTED) == 3


def test_ExitCode_IsPassedThrough(store):
    entry, _ = _entry(store)

    assert run_with_secret(entry, [PY, "-c", "raise SystemExit(7)"], "env").exit_code == 7


def test_Timeout_StopsTheCommand(store):
    entry, _ = _entry(store)

    result = run_with_secret(entry, [PY, "-c", "import time; time.sleep(30)"], "env", timeout_seconds=1)

    assert result.timed_out is True


def test_NoCommand_Raises(store):
    entry, _ = _entry(store)
    with pytest.raises(ValueError, match="No command"):
        run_with_secret(entry, [], "stdin")


def test_AskpassListener_WrongToken_IsRefused(store):
    listener = _AskpassListener("not-for-you")
    try:
        with socket.create_connection(("127.0.0.1", listener.port), timeout=5) as conn:
            conn.sendall(b"wrong-token\n")
            reply = conn.recv(100)
    finally:
        listener.close()

    assert reply == b"NO\n"
    assert listener.served == 0
