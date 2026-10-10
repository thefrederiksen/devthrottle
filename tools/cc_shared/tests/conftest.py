"""Shared fixtures for the cc_shared tests."""

import pytest


@pytest.fixture(autouse=True)
def tool_error_reports_stay_in_the_test(tmp_path_factory, monkeypatch):
    """Keep every error report a test causes inside the test (issue #3642).

    The failure reporter's own tests run tools through the entry hook and make them fail on purpose. With no
    session credential, a storage root of the test's own (no machine credential, and the outbox under it) and a
    hosted Gateway address that fails at once, a report that a test does not point at its own loopback server is
    kept in that throwaway root and never reaches a real Gateway. Tests that need a session set one themselves.
    """
    monkeypatch.delenv("CC_GATEWAY_URL", raising=False)
    monkeypatch.delenv("CC_GATEWAY_SESSION_KEY", raising=False)
    monkeypatch.setenv("CC_DIRECTOR_ROOT", str(tmp_path_factory.mktemp("director-root")))
    monkeypatch.setenv("DEVTHROTTLE_HOSTED_GATEWAY_URL", "http://127.0.0.1:0")
