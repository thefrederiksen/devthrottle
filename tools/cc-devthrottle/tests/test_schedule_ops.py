"""Tests for the cc-devthrottle Gateway schedule client."""

import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest
import requests

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared import gateway  # noqa: E402
from src.schedule_ops import (  # noqa: E402
    GatewayError,
    ScheduleClient,
    _auth_token,
    resolve_base_url,
)


def _fake_response(status_code: int, json_body=None, text: str = "") -> MagicMock:
    """A response WITH headers: the client asserts the promised content type on success bodies
    (issue #2486), and a fake without headers would let that guard go untested."""
    resp = MagicMock(spec=requests.Response)
    resp.status_code = status_code
    resp.content = b"x" if (json_body is not None or text) else b""
    resp.text = text
    resp.headers = {"Content-Type": "application/json" if json_body is not None else "text/plain"}
    if json_body is not None:
        resp.json.return_value = json_body
    else:
        resp.json.side_effect = ValueError("no json")
    return resp


class TestResolveBaseUrl:
    """Remove-the-network-port mission, phase 2: the address and the credential come from the SESSION.

    These tests used to pin the opposite - a base URL read out of config.json with a loopback default,
    and a token exemption for a Gateway that happened to be on this machine. Both are gone on purpose.
    The command line no longer holds its own opinion about where the Gateway is, and it no longer
    presents the account-wide token; a session is TOLD both at launch, as a pair.
    """

    def test_uses_the_address_this_session_was_given(self, monkeypatch):
        monkeypatch.setenv("CC_GATEWAY_URL", "https://gw.example.ts.net")
        assert resolve_base_url() == "https://gw.example.ts.net"

    def test_strips_trailing_slash(self, monkeypatch):
        monkeypatch.setenv("CC_GATEWAY_URL", "https://gw.example.ts.net/")
        assert resolve_base_url() == "https://gw.example.ts.net"

    def test_there_is_no_loopback_default_to_fall_back_to(self, monkeypatch):
        """NO FALLBACK. A default address would be the second door wearing the first one's clothes.

        A machine that happened to run a Gateway on loopback would work and one that did not would
        fail with a connection error instead of the sentence written for it - and neither user would
        learn what is actually required.
        """
        monkeypatch.delenv("CC_GATEWAY_URL", raising=False)
        with pytest.raises(gateway.GatewayError) as ex:
            resolve_base_url()
        assert "self-hosted gateway" in str(ex.value)

    def test_the_credential_is_this_sessions_key_not_the_accounts_token(self, monkeypatch):
        """The hole this closes: every agent running a schedule command used to hold the account.

        _auth_token read gateway.token from config.json - the shared machine credential, with
        authority over the whole account on every machine - and presented it to the Gateway.
        """
        monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "this-sessions-key")
        assert _auth_token() == "this-sessions-key"

    def test_a_session_with_no_key_is_refused_rather_than_sent_unauthenticated(self, monkeypatch):
        monkeypatch.delenv("CC_GATEWAY_SESSION_KEY", raising=False)
        with pytest.raises(gateway.GatewayError) as ex:
            _auth_token()
        assert "CC_GATEWAY_SESSION_KEY is not set" in str(ex.value)


class TestErrorHandling:
    def _client(self) -> ScheduleClient:
        return ScheduleClient(base_url="http://127.0.0.1:7878")

    def test_400_surfaces_gateway_message_not_stack_trace(self):
        client = self._client()
        bad = _fake_response(400, {"error": "invalid cron expression: not-a-cron"})
        with patch("src.schedule_ops.requests.request", return_value=bad):
            with pytest.raises(GatewayError) as ex:
                client.list_jobs()
        assert "invalid cron expression: not-a-cron" in str(ex.value)
        assert "Traceback" not in str(ex.value)

    def test_404_surfaces_gateway_message(self):
        client = self._client()
        missing = _fake_response(404, {"error": "no such cron job", "id": "x"})
        with patch("src.schedule_ops.requests.request", return_value=missing):
            with pytest.raises(GatewayError) as ex:
                client.get_job("x")
        assert "no such cron job" in str(ex.value)

    def test_connection_error_is_clear(self):
        client = self._client()
        with patch(
            "src.schedule_ops.requests.request",
            side_effect=requests.exceptions.ConnectionError(),
        ):
            with pytest.raises(GatewayError) as ex:
                client.list_jobs()
        assert "not reachable" in str(ex.value).lower()

    def test_timeout_is_clear(self):
        client = self._client()
        with patch(
            "src.schedule_ops.requests.request",
            side_effect=requests.exceptions.Timeout(),
        ):
            with pytest.raises(GatewayError) as ex:
                client.list_jobs()
        assert "did not respond" in str(ex.value).lower()


class TestRouteMapping:
    def _client(self) -> ScheduleClient:
        return ScheduleClient(base_url="http://127.0.0.1:7878")

    def test_list_jobs_gets_jobs(self):
        client = self._client()
        ok = _fake_response(200, {"jobs": [{"id": "a"}, {"id": "b"}]})
        with patch("src.schedule_ops.requests.request", return_value=ok) as req:
            jobs = client.list_jobs()
        method, url = req.call_args.args
        assert method == "GET"
        # The opt-in that lets the Gateway list random schedules to this tool (issue #3622).
        assert url == "http://127.0.0.1:7878/cron/jobs?include=random,window"
        assert len(jobs) == 2

    def test_create_posts_job_and_returns_created(self):
        client = self._client()
        created = _fake_response(
            201, {"id": "new-1", "nextRunUtc": "2026-06-28T22:00:00Z"}
        )
        with patch("src.schedule_ops.requests.request", return_value=created) as req:
            result = client.create_job({"name": "x"})
        method, url = req.call_args.args
        assert method == "POST"
        assert url == "http://127.0.0.1:7878/cron/jobs"
        assert req.call_args.kwargs["json"] == {"name": "x"}
        assert result["id"] == "new-1"

    def test_run_now_posts_to_run_route(self):
        client = self._client()
        ok = _fake_response(200, {"firedUtc": "2026-06-21T00:00:00Z"})
        with patch("src.schedule_ops.requests.request", return_value=ok) as req:
            client.run_now("job-7")
        method, url = req.call_args.args
        assert method == "POST"
        assert url == "http://127.0.0.1:7878/cron/jobs/job-7/run"

    def test_list_runs_gets_runs_route(self):
        client = self._client()
        ok = _fake_response(200, {"jobId": "job-7", "runs": [{"infraStatus": "started"}]})
        with patch("src.schedule_ops.requests.request", return_value=ok) as req:
            history = client.list_runs("job-7")
        method, url = req.call_args.args
        assert method == "GET"
        assert url == "http://127.0.0.1:7878/cron/jobs/job-7/runs"
        assert len(history) == 1

    def test_delete_deletes_route(self):
        client = self._client()
        ok = _fake_response(200, {"id": "job-7", "deleted": True})
        with patch("src.schedule_ops.requests.request", return_value=ok) as req:
            client.delete_job("job-7")
        method, url = req.call_args.args
        assert method == "DELETE"
        assert url == "http://127.0.0.1:7878/cron/jobs/job-7"


class TestEnableDisable:
    def test_set_enabled_reads_then_puts_flipped_flag(self):
        client = ScheduleClient(base_url="http://127.0.0.1:7878")
        get_resp = _fake_response(200, {"id": "job-9", "name": "n", "enabled": True})
        put_resp = _fake_response(200, {"id": "job-9", "name": "n", "enabled": False})
        responses = [get_resp, put_resp]

        def fake_request(method, url, **kwargs):
            return responses.pop(0)

        with patch("src.schedule_ops.requests.request", side_effect=fake_request) as req:
            result = client.set_enabled("job-9", False)

        first_method, _ = req.call_args_list[0].args
        second_method, second_url = req.call_args_list[1].args
        assert first_method == "GET"
        assert second_method == "PUT"
        assert second_url == "http://127.0.0.1:7878/cron/jobs/job-9"
        assert req.call_args_list[1].kwargs["json"]["enabled"] is False
        assert result["enabled"] is False


# The issue #2201 TestScopeGuard class is gone with its subject: the guard read CC_DIRECTOR_API
# (no longer stamped anywhere - the Remove-the-network-port mission deleted the Director listener
# it addressed) and the port reservation files (deleted with the port allocator), and its hazard
# ended when the schedule client moved to the session's own CC_GATEWAY_URL / CC_GATEWAY_SESSION_KEY
# pair - both halves of the tool now read one environment, so the mismatch cannot be expressed.
