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
