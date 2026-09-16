"""A secret the replacement marker itself spells out. "[REDACTED]" put in place of "DACT" still reads "DACT",
so such a secret is refused, and output where a replacement spells a secret out next to the surrounding text
is withheld whole (review of pull request 2891 at 73d8c536)."""

import json
import sys

import pytest
from typer.testing import CliRunner

from conftest import add_entry, new_secret
from src import cli, filelog, paths
from src.errors import InputError
from src.redact import REDACTED, WITHHELD, SCRUBBER, Scrubber, redaction_conflict
from src.runner import run_with_secret
from src.store import EntryNotAvailableError, make_entry
from src.storefile import UserOnlyFile

PY = sys.executable
runner = CliRunner()


@pytest.mark.parametrize("secret", ["REDACTED", "DACT", "ACTE", "withheld this output"])
def test_SecretInsideTheMarkerOrTheNotice_IsAConflict(secret):
    assert redaction_conflict(secret) is not None


@pytest.mark.parametrize("secret", ["]abc", "abc[", "Correct-Horse+Battery9", "Redacted-9"])
def test_OrdinarySecret_IsNotAConflict(secret):
    assert redaction_conflict(secret) is None


@pytest.mark.parametrize("secret,output", [
    ("REDACTED", "value=REDACTED"),
    ("DACT", "value=DACT"),
    ("]abc", "]abcabc"),      # the marker's last character spells the secret out with the text after it
    ("D]xy", "D]xyxy"),
])
def test_NoOutput_StillCarriesTheSecret_AfterScrubbing(secret, output):
    scrubber = Scrubber()
    scrubber.add(secret)

    for scrubbed in (scrubber.scrub(output), scrubber.decode_scrubbed(output.encode("utf-8")),
                     scrubber.decode_scrubbed(output.encode("utf-16-le"))):
        assert secret not in scrubbed


def test_ScrubThatWouldSpellTheSecretOut_WithholdsTheWholeOutput():
    scrubber = Scrubber()
    scrubber.add("]abc")

    assert scrubber.scrub("]abcabc") == WITHHELD
    assert scrubber.scrub("before ]abc after") == f"before {REDACTED} after"


def test_Add_SecretInsideTheMarker_IsRefused_AndNothingStored(store, monkeypatch):
    result = runner.invoke(cli.app, ["add", "marker", "--username", "u", "--domains", "https://127.0.0.1",
                                     "--agents"], input="DACT\n")

    assert result.exit_code != 0
    assert "cannot be stored" in result.output, result.output
    assert "DACT" not in result.output
    assert store.get("marker") is None


def test_MakeEntry_SecretInsideTheMarker_Raises():
    with pytest.raises(InputError):
        make_entry("marker", "u", "REDACTED", ["https://127.0.0.1"], "", True, ["login", "run"])


def _store_directly(store, secret):
    """An entry written before cc-secrets refused such secrets: straight into the file, past make_entry."""
    add_entry(store, name="old")
    document = json.loads(paths.store_path().read_text(encoding="utf-8"))
    document["entries"][0]["secret"] = secret
    UserOnlyFile(paths.store_path()).write((json.dumps(document) + "\n").encode("utf-8"))


def test_UseOfAnOldConflictingEntry_IsRefused_WithoutSayingWhatTheSecretIsPartOf(store):
    _store_directly(store, "DACT")

    with pytest.raises(EntryNotAvailableError) as raised:
        store.entry_for_agent("old", "run")

    assert "could not be hidden" in str(raised.value)
    assert REDACTED not in str(raised.value)


def test_Run_OutputThatWouldSpellTheSecretOut_NeverShowsIt(store):
    secret = "]abc" + new_secret()[:6]
    add_entry(store, name="edge", secret=secret)
    entry = store.get("edge")
    tail = secret[1:]

    result = run_with_secret(entry, [PY, "-c", f"import os; print(os.environ['CC_SECRET'] + {tail!r})"], "env")

    assert secret not in result.stdout + result.stderr


def test_LogLine_ThatWouldSpellTheSecretOut_NeverWritesIt(store):
    secret = "]abc" + new_secret()[:6]
    SCRUBBER.add(secret)

    filelog.write("edge " + secret + secret[1:])

    text = "".join(p.read_text(encoding="utf-8") for p in paths.secrets_home().rglob("*.log"))
    assert text, "the tool log was not written, so this test shows nothing"
    assert secret not in text


def test_ScrubBytes_ThatWouldSpellTheSecretOut_WithholdsTheWholeOutput():
    scrubber = Scrubber()
    scrubber.add("]abc")

    assert scrubber.scrub_bytes("]abcabc".encode("utf-8")) == WITHHELD.encode("ascii")


@pytest.mark.parametrize("encoding", ["utf-8", "utf-16-le", "utf-16-be", "utf-32-le"])
def test_ScrubBytes_LeavesNoFormOfTheSecret(encoding):
    # In UTF-16 a byte form of the secret offset by one byte can match too, so the result need not be the
    # withheld notice - what must hold is that no form of the secret is left.
    scrubber = Scrubber()
    scrubber.add("]abc")
    forms = {"]abc".encode(e) for e in ("utf-8", "utf-16-le", "utf-16-be", "utf-32-le", "utf-32-be")}

    scrubbed = scrubber.scrub_bytes("x ]abcabc ]abc y".encode(encoding))

    assert not any(form in scrubbed for form in forms)


def test_Base64OfTheSecretInASingleByteEncoding_IsScrubbed():
    # A browser's btoa() and HTTP Basic authorization encode as ISO-8859-1, not UTF-8 (review at fbe4293e).
    import base64

    secret = "P" + chr(0xE4) + "ssw" + chr(0xF6) + "rd42"
    scrubber = Scrubber()
    scrubber.add(secret, "user")
    forms = [base64.b64encode(secret.encode("latin-1")).decode(),
             base64.b64encode(f"user:{secret}".encode("latin-1")).decode()]

    for form in forms:
        assert form not in scrubber.scrub(f"Authorization: Basic {form}")


@pytest.mark.parametrize("encoding", ["utf-8", "latin-1"])
def test_Run_BasicHeaderWithAnAccentedUsername_IsScrubbedFromOutput(store, encoding):
    # The review's case (08ee570e): an ASCII password with an accented username. The Base64 of the pair was
    # derived from the codecs of the password alone, which lost every form of it - UTF-8 included.
    import base64

    username = "u" + chr(0xE9) + "s"
    secret = "Password42" + new_secret()[:6]
    add_entry(store, name="accented-user", secret=secret, username=username)
    entry = store.get("accented-user")
    token = base64.b64encode(f"{username}:{secret}".encode(encoding)).decode()
    script = ("import base64, os; print('Authorization: Basic ' + base64.b64encode(("
              + repr(username) + " + ':' + os.environ['CC_SECRET']).encode(" + repr(encoding) + ")).decode())")

    result = run_with_secret(entry, [PY, "-c", script], "env")

    assert result.exit_code == 0, result.stderr
    assert "Authorization: Basic" in result.stdout, "the command printed nothing, so this test shows nothing"
    assert token not in result.stdout + result.stderr
