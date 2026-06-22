"""Tests for the cc-cron Gateway client.

These cover the consumer-layer contract that issue #621 cares about:
  - endpoint discovery resolves a base URL with no hard-coded port literal, from gateway.url
    or the documented loopback default (AC3);
  - a 400 from the Gateway surfaces the Gateway's own message, not a stack trace (AC4);
  - an unreachable Gateway raises a clear, handled error (AC3);
  - create/list/runs/run/delete map to the right HTTP verb + path (AC1/AC2);
  - enable/disable do the read-modify-write PUT the Gateway contract requires.
"""

import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest
import requests

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src.gateway import (  # noqa: E402
    LOOPBACK_DEFAULT,
    CronClient,
    GatewayError,
    resolve_base_url,
)


def _fake_response(status_code: int, json_body=None, text: str = "") -> MagicMock:
    resp = MagicMock(spec=requests.Response)
    resp.status_code = status_code
    resp.content = b"x" if (json_body is not None or text) else b""
    resp.text = text
    if json_body is not None:
        resp.json.return_value = json_body
    else:
        resp.json.side_effect = ValueError("no json")
    return resp


class TestResolveBaseUrl:
    """Endpoint discovery (AC3) - no hard-coded port literal in the call path."""

    def test_uses_configured_gateway_url(self):
        cfg = MagicMock()
        cfg.gateway.url = "https://gw.example.ts.net"
        with patch("src.gateway.CCDirectorConfig") as ctor:
            ctor.return_value.load.return_value = cfg
            assert resolve_base_url() == "https://gw.example.ts.net"

    def test_strips_trailing_slash(self):
        cfg = MagicMock()
        cfg.gateway.url = "https://gw.example.ts.net/"
        with patch("src.gateway.CCDirectorConfig") as ctor:
            ctor.return_value.load.return_value = cfg
            assert resolve_base_url() == "https://gw.example.ts.net"

    def test_falls_back_to_loopback_default_when_unset(self):
        cfg = MagicMock()
        cfg.gateway.url = ""
        with patch("src.gateway.CCDirectorConfig") as ctor:
            ctor.return_value.load.return_value = cfg
            assert resolve_base_url() == LOOPBACK_DEFAULT


class TestErrorHandling:
    """Friendly errors: 400 message echoed; not-reachable is clear (AC3/AC4)."""

    def _client(self) -> CronClient:
        return CronClient(base_url="http://127.0.0.1:7878")

    def test_400_surfaces_gateway_message_not_stack_trace(self):
        client = self._client()
        bad = _fake_response(400, {"error": "invalid cron expression: not-a-cron"})
        with patch("src.gateway.requests.request", return_value=bad):
            with pytest.raises(GatewayError) as ex:
                client.list_jobs()
        assert "invalid cron expression: not-a-cron" in str(ex.value)
        assert "Traceback" not in str(ex.value)

    def test_404_surfaces_gateway_message(self):
        client = self._client()
        missing = _fake_response(404, {"error": "no such cron job", "id": "x"})
        with patch("src.gateway.requests.request", return_value=missing):
            with pytest.raises(GatewayError) as ex:
                client.get_job("x")
        assert "no such cron job" in str(ex.value)

    def test_connection_error_is_clear(self):
        client = self._client()
        with patch(
            "src.gateway.requests.request",
            side_effect=requests.exceptions.ConnectionError(),
        ):
            with pytest.raises(GatewayError) as ex:
                client.list_jobs()
        assert "not reachable" in str(ex.value).lower()

    def test_timeout_is_clear(self):
        client = self._client()
        with patch(
            "src.gateway.requests.request",
            side_effect=requests.exceptions.Timeout(),
        ):
            with pytest.raises(GatewayError) as ex:
                client.list_jobs()
        assert "did not respond" in str(ex.value).lower()


class TestRouteMapping:
    """Each command maps to the right HTTP verb + path (AC1/AC2)."""

    def _client(self) -> CronClient:
        return CronClient(base_url="http://127.0.0.1:7878")

    def test_list_jobs_gets_jobs(self):
        client = self._client()
        ok = _fake_response(200, {"jobs": [{"id": "a"}, {"id": "b"}]})
        with patch("src.gateway.requests.request", return_value=ok) as req:
            jobs = client.list_jobs()
        req.assert_called_once()
        method, url = req.call_args.args
        assert method == "GET"
        assert url == "http://127.0.0.1:7878/cron/jobs"
        assert len(jobs) == 2

    def test_create_posts_job_and_returns_created(self):
        client = self._client()
        created = _fake_response(201, {"id": "new-1", "nextRunUtc": "2026-06-28T22:00:00Z"})
        with patch("src.gateway.requests.request", return_value=created) as req:
            result = client.create_job({"name": "x"})
        method, url = req.call_args.args
        assert method == "POST"
        assert url == "http://127.0.0.1:7878/cron/jobs"
        assert req.call_args.kwargs["json"] == {"name": "x"}
        assert result["id"] == "new-1"
        assert result["nextRunUtc"] == "2026-06-28T22:00:00Z"

    def test_run_now_posts_to_run_route(self):
        client = self._client()
        ok = _fake_response(200, {"firedUtc": "2026-06-21T00:00:00Z", "infraStatus": "started"})
        with patch("src.gateway.requests.request", return_value=ok) as req:
            client.run_now("job-7")
        method, url = req.call_args.args
        assert method == "POST"
        assert url == "http://127.0.0.1:7878/cron/jobs/job-7/run"

    def test_list_runs_gets_runs_route(self):
        client = self._client()
        ok = _fake_response(200, {"jobId": "job-7", "runs": [{"infraStatus": "started"}]})
        with patch("src.gateway.requests.request", return_value=ok) as req:
            history = client.list_runs("job-7")
        method, url = req.call_args.args
        assert method == "GET"
        assert url == "http://127.0.0.1:7878/cron/jobs/job-7/runs"
        assert len(history) == 1

    def test_delete_deletes_route(self):
        client = self._client()
        ok = _fake_response(200, {"id": "job-7", "deleted": True})
        with patch("src.gateway.requests.request", return_value=ok) as req:
            client.delete_job("job-7")
        method, url = req.call_args.args
        assert method == "DELETE"
        assert url == "http://127.0.0.1:7878/cron/jobs/job-7"


class TestEnableDisable:
    """enable/disable is a read-modify-write PUT (the only Gateway contract path)."""

    def test_set_enabled_reads_then_puts_flipped_flag(self):
        client = CronClient(base_url="http://127.0.0.1:7878")
        get_resp = _fake_response(200, {"id": "job-9", "name": "n", "enabled": True})
        put_resp = _fake_response(200, {"id": "job-9", "name": "n", "enabled": False})

        responses = [get_resp, put_resp]

        def fake_request(method, url, **kwargs):
            return responses.pop(0)

        with patch("src.gateway.requests.request", side_effect=fake_request) as req:
            result = client.set_enabled("job-9", False)

        # First call is the GET, second is the PUT with enabled flipped.
        first_method, first_url = req.call_args_list[0].args
        second_method, second_url = req.call_args_list[1].args
        assert first_method == "GET"
        assert second_method == "PUT"
        assert second_url == "http://127.0.0.1:7878/cron/jobs/job-9"
        assert req.call_args_list[1].kwargs["json"]["enabled"] is False
        assert result["enabled"] is False
