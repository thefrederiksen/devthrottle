"""Registering a secret with the scrubber happens on every store read, so it must cost nothing the second time -
and must still hide exactly what it hid before (owner's report 2026-10-10: two seconds a click with 77 secrets)."""

from conftest import new_secret
from src import redact
from src.redact import REDACTED, Scrubber


def test_AddingTheSameSecretAgain_DoesNoWork(monkeypatch):
    scrubber = Scrubber()
    secret = new_secret()
    scrubber.add(secret, "soren")
    calls = []
    real = redact._needle_pairs
    monkeypatch.setattr(redact, "_needle_pairs", lambda *a: calls.append(1) or real(*a))

    scrubber.add(secret, "soren")

    assert calls == []


def test_ManySecrets_AreAllStillHidden_InTextAndInBytes():
    scrubber = Scrubber()
    secrets = [new_secret() for _ in range(40)]
    for _ in range(3):  # three store reads
        for secret in secrets:
            scrubber.add(secret, "soren")

    text = " ".join(f"[{s}]" for s in secrets)
    assert not any(s in scrubber.scrub(text) for s in secrets)
    for encoding in ("utf-8", "utf-16-le", "utf-16-be"):
        cleaned = scrubber.scrub_bytes(text.encode(encoding)).decode(encoding, errors="replace")
        assert not any(s in cleaned for s in secrets)
        assert REDACTED in cleaned


def test_ANewOutputEncoding_HidesTheSecretsAlreadyHeld_InItToo(monkeypatch):
    """The secret's own bytes are hidden in every codec anyway; its other forms (here the web-address-encoded
    one) are hidden only in the output encodings, so a new encoding must cover the secrets registered before it."""
    scrubber = Scrubber()
    first, second = new_secret() + "+/x", new_secret()
    scrubber.add(first)
    form = redact.quote(first, safe="")
    assert form != first and form in redact.variants_for(first)
    assert form.encode("utf-32-le") in scrubber.scrub_bytes(form.encode("utf-32-le"))  # not an encoding held yet
    encodings = redact.output_encodings() + ["utf-32-le"]
    monkeypatch.setattr(redact, "output_encodings", lambda: list(encodings))

    scrubber.add(second)

    assert form.encode("utf-32-le") not in scrubber.scrub_bytes(form.encode("utf-32-le"))


def test_Clear_ForgetsWhatWasRegistered():
    scrubber = Scrubber()
    secret = new_secret()
    scrubber.add(secret)
    scrubber.clear()

    assert scrubber.scrub(secret) == secret
    scrubber.add(secret)
    assert scrubber.scrub(secret) == REDACTED
