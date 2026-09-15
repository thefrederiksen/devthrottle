import base64
import html
import re
import socket
import sys
from urllib.parse import quote, quote_plus

import pytest

from conftest import add_entry
from src.redact import REDACTED, Scrubber, output_encodings
from src.runner import _AskpassListener, run_with_secret

PY = sys.executable
WINDOWS = sys.platform == "win32"
# "Passwoerd" with an a-umlaut and an o-umlaut, spelled with chr() so this file stays plain ASCII.
ACCENTED = "P" + chr(0xE4) + "ssw" + chr(0xF6) + "rd-4242"


def _entry(store):
    secret = add_entry(store)
    return store.get("devlinux"), secret


def _accented(store):
    add_entry(store, name="enc", secret=ACCENTED)
    return store.get("enc")


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


def test_EncodedEchoes_AreScrubbed(store):
    entry, secret = _entry(store)
    script = ("import sys,base64,urllib.parse; s=sys.stdin.readline().strip();"
              "print(base64.b64encode(s.encode()).decode()); print(base64.b64encode(('leak-user:'+s).encode()).decode());"
              "print(urllib.parse.quote(s))")

    result = run_with_secret(entry, [PY, "-c", script], "stdin")

    assert base64.b64encode(secret.encode()).decode() not in result.stdout
    assert result.stdout.count(REDACTED) == 3


@pytest.mark.skipif(not WINDOWS, reason="cmd.exe")
def test_CmdEcho_InTheConsoleCodePage_IsScrubbed(store):
    result = run_with_secret(_accented(store), ["cmd", "/c", "echo %CC_SECRET%"], "env")

    assert REDACTED in result.stdout
    assert "rd-4242" not in result.stdout


@pytest.mark.skipif(not WINDOWS, reason="cmd.exe")
def test_CmdUnicodeEcho_Utf16_IsScrubbed(store):
    result = run_with_secret(_accented(store), ["cmd", "/u", "/c", "echo %CC_SECRET%"], "env")

    assert REDACTED in result.stdout
    assert "rd-4242" not in result.stdout.replace("\x00", "")


@pytest.mark.parametrize("encoding", sorted({"cp1252", "latin-1", "utf-16-le", "utf-16-be"} | set(output_encodings())))
def test_OutputInAnyOutputEncoding_IsScrubbed(store, encoding):
    script = ("import os,sys; sys.stdout.buffer.write(('before ' + os.environ['CC_SECRET'] + ' after')"
              f".encode({encoding!r}))")

    result = run_with_secret(_accented(store), [PY, "-c", script], "env")

    assert result.stdout == f"before {REDACTED} after"


def test_CommonEncodedForms_AreScrubbed():
    secret = "p@ss word!*&<Q>~"
    scrubber = Scrubber()
    scrubber.add(secret, "user")
    raw = secret.encode("utf-8")
    lower = lambda text: re.sub(r"%[0-9A-F]{2}", lambda m: m.group(0).lower(), text)
    forms = {
        "encodeURIComponent": quote(secret, safe="-_.!~*'()"),
        "form encoding": quote_plus(secret, safe="*-._"),
        "lowercase escapes": lower(quote(secret, safe="")),
        "base64 without padding": base64.b64encode(raw).decode().rstrip("="),
        "base64url": base64.urlsafe_b64encode(raw).decode(),
        "base64url without padding": base64.urlsafe_b64encode(raw).decode().rstrip("="),
        "html entities": html.escape(secret),
        "hex": raw.hex(),
        "upper hex": raw.hex().upper(),
        "basic authorization without padding": base64.b64encode(b"user:" + raw).decode().rstrip("="),
    }

    for label, form in forms.items():
        assert scrubber.scrub(f"a {form} b") == f"a {REDACTED} b", label


def test_ExitCode_IsPassedThrough(store):
    entry, _ = _entry(store)

    assert run_with_secret(entry, [PY, "-c", "raise SystemExit(7)"], "env").exit_code == 7


def test_Timeout_StopsTheCommand(store):
    entry, _ = _entry(store)

    result = run_with_secret(entry, [PY, "-c", "import time; time.sleep(30)"], "env", timeout_seconds=1)

    assert result.timed_out is True


def test_NoCommand_Raises(store):
    from src.errors import InputError

    entry, _ = _entry(store)
    with pytest.raises(InputError, match="No command"):
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
