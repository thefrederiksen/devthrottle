"""Gateway cron REST client for cc-cron.

A thin, typed wrapper over the Gateway's cron surface (epic #479):

    POST   /cron/jobs            -> 201 CronJobDto | 400
    GET    /cron/jobs            -> { jobs: [ CronJobDto ] }
    GET    /cron/jobs/{id}       -> CronJobDto | 404
    PUT    /cron/jobs/{id}       -> 200 CronJobDto | 400 | 404
    DELETE /cron/jobs/{id}       -> { id, deleted } | 404
    POST   /cron/jobs/{id}/run   -> 200 CronRunRecord | 409 | 404
    GET    /cron/jobs/{id}/runs  -> { jobId, runs: [ CronRunRecord ] }

This module owns NO job state and runs NO scheduler. It only translates command
arguments into HTTP calls against the running Gateway and translates the Gateway's
own responses (including its 400 validation message) back to the caller.

Endpoint discovery (no hard-coded port): the Gateway base URL is the single
configured source of truth, the same one the desktop and the Cockpit use - the
`gateway.url` block of config.json (read via cc_shared.config). When no gateway
URL is configured at all, the loopback default is used (correct for a same-machine
setup). This mirrors C# CockpitUrlResolver.ResolveCockpitBase / LocalhostDefault.
"""

import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

import requests

# Make the shared tools packages importable the same way the other cc-* tools do
# (cc-settings/src/settings.py uses this exact bootstrap).
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared.config import CCDirectorConfig  # noqa: E402

# The loopback Gateway base used ONLY when no gateway.url is configured. Mirrors the C#
# CcDirector.Avalonia.CockpitUrlResolver.LocalhostDefault constant so the CLI, the desktop
# button, and the Cockpit all agree on the same-machine default. This is the documented
# well-known Gateway address, not a per-call port literal: there is no other place in the
# CLI that names a port.
LOOPBACK_DEFAULT = "http://127.0.0.1:7878"

# How long to wait for the Gateway before declaring it unreachable.
_TIMEOUT_SECONDS = 10


class GatewayError(Exception):
    """A handled, user-facing failure talking to the Gateway.

    Carries a message that is already suitable to print as-is (no stack trace): either
    the Gateway's own error text (e.g. a 400 validation message) or a clear
    not-reachable explanation. The CLI prints this and exits non-zero.
    """


def resolve_base_url() -> str:
    """Resolve the Gateway base URL with no hard-coded port literal in the call path.

    Selection by a single configured source of truth (gateway.url), NOT a fallback chain:
    a configured gateway URL is used everywhere; the loopback default is used only when no
    gateway URL is configured at all. Trailing slashes are stripped so route joins never
    yield a double slash.
    """
    config = CCDirectorConfig().load()
    url = (config.gateway.url or "").strip()
    if url:
        return url.rstrip("/")
    return LOOPBACK_DEFAULT


def _auth_token() -> str:
    """The configured Gateway token, sent as a Bearer header. Empty for the local default.

    The Gateway runs with auth disabled on the same-machine loopback path, so a local
    install needs no token. A configured remote Gateway carries gateway.token, which the
    Gateway accepts as `Authorization: Bearer <token>` (see Gateway AuthMiddleware).
    """
    config = CCDirectorConfig().load()
    return (config.gateway.token or "").strip()


class CronClient:
    """Talks to one Gateway's cron surface. Construct once per command invocation."""

    def __init__(self, base_url: Optional[str] = None) -> None:
        self.base_url = (base_url or resolve_base_url()).rstrip("/")
        self._token = _auth_token()

    def _headers(self) -> Dict[str, str]:
        headers = {"Accept": "application/json"}
        if self._token:
            headers["Authorization"] = f"Bearer {self._token}"
        return headers

    def _request(self, method: str, path: str, json_body: Optional[Dict[str, Any]] = None) -> requests.Response:
        url = f"{self.base_url}{path}"
        try:
            return requests.request(
                method,
                url,
                json=json_body,
                headers=self._headers(),
                timeout=_TIMEOUT_SECONDS,
            )
        except requests.exceptions.ConnectionError:
            raise GatewayError(
                f"Gateway not reachable at {self.base_url}. "
                "Is the Gateway tray app running on this machine? "
                "If you target a remote Gateway, set gateway.url with "
                "'cc-settings set gateway.url <url>'."
            )
        except requests.exceptions.Timeout:
            raise GatewayError(
                f"Gateway at {self.base_url} did not respond within {_TIMEOUT_SECONDS}s."
            )

    @staticmethod
    def _gateway_message(resp: requests.Response) -> str:
        """Pull the Gateway's own error text out of a non-2xx body.

        The Gateway returns { "error": "<message>" } on a 400/404/409. We echo that
        message verbatim rather than a stack trace, so an invalid schedule reads like a
        validation message ("invalid cron expression: ...") and not a Python traceback.
        """
        try:
            data = resp.json()
            if isinstance(data, dict) and data.get("error"):
                return str(data["error"])
        except ValueError:
            pass
        text = (resp.text or "").strip()
        return text if text else f"Gateway returned HTTP {resp.status_code}"

    def _ok_or_raise(self, resp: requests.Response) -> Dict[str, Any]:
        if 200 <= resp.status_code < 300:
            if not resp.content:
                return {}
            return resp.json()
        raise GatewayError(self._gateway_message(resp))

    # ---- cron job CRUD ----

    def list_jobs(self) -> List[Dict[str, Any]]:
        data = self._ok_or_raise(self._request("GET", "/cron/jobs"))
        return list(data.get("jobs", []))

    def get_job(self, job_id: str) -> Dict[str, Any]:
        return self._ok_or_raise(self._request("GET", f"/cron/jobs/{job_id}"))

    def create_job(self, job: Dict[str, Any]) -> Dict[str, Any]:
        return self._ok_or_raise(self._request("POST", "/cron/jobs", job))

    def update_job(self, job_id: str, job: Dict[str, Any]) -> Dict[str, Any]:
        return self._ok_or_raise(self._request("PUT", f"/cron/jobs/{job_id}", job))

    def delete_job(self, job_id: str) -> Dict[str, Any]:
        return self._ok_or_raise(self._request("DELETE", f"/cron/jobs/{job_id}"))

    # ---- firing + history ----

    def run_now(self, job_id: str) -> Dict[str, Any]:
        return self._ok_or_raise(self._request("POST", f"/cron/jobs/{job_id}/run"))

    def list_runs(self, job_id: str) -> List[Dict[str, Any]]:
        data = self._ok_or_raise(self._request("GET", f"/cron/jobs/{job_id}/runs"))
        return list(data.get("runs", []))

    # ---- enable / disable (read-modify-write, the only contract path) ----

    def set_enabled(self, job_id: str, enabled: bool) -> Dict[str, Any]:
        """Toggle a job's enabled flag.

        The Gateway has no dedicated enable/disable route - the enabled state is a field
        on CronJobDto - so this reads the current job, flips Enabled, and PUTs it back.
        This is exactly how the Cockpit Schedule page's toggle works, so the two agree.
        """
        job = self.get_job(job_id)
        job["enabled"] = enabled
        return self.update_job(job_id, job)
