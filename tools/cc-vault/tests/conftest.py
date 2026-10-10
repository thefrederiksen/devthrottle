"""Shared fixtures for the cc-vault tests."""

import pytest


@pytest.fixture(autouse=True)
def tool_error_reports_stay_in_the_test(tmp_path_factory, monkeypatch):
    """Keep every error report a test causes inside the test (issue #3642).

    The tools report their failures to the Gateway, and these tests make them fail on purpose - in this
    process and in the tool processes they start, which inherit this environment. So there is no session
    credential, the storage root is the test's own (it holds no machine credential, and the outbox a report
    is kept in is under it), and the hosted Gateway address fails at once. A report a test causes is kept
    in that throwaway root; none reaches a real Gateway or this machine's real outbox. Before this, the
    deliberate failures of these suites were sent to the live Gateway under the machine's credential.
    """
    monkeypatch.delenv("CC_GATEWAY_URL", raising=False)
    monkeypatch.delenv("CC_GATEWAY_SESSION_KEY", raising=False)
    monkeypatch.setenv("CC_DIRECTOR_ROOT", str(tmp_path_factory.mktemp("director-root")))
    monkeypatch.setenv("DEVTHROTTLE_HOSTED_GATEWAY_URL", "http://127.0.0.1:0")
