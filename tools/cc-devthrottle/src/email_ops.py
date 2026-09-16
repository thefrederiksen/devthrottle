"""Owner-email operations for cc-devthrottle (issue #1318 consumer).

'cc-devthrottle email owner' sends ONE email to the account owner on file. It passes only a subject,
body, and optional attachments - there is no recipient argument, so the send is single-recipient by
construction. The escalation channel an unattended or scheduled run uses to reach the human before it
self-reaps, and the way to send yourself a report (e.g. an HTML file) to read offline.

The send is server-side: this verb POSTs to the local Gateway's /account/email relay, which injects the
account token it holds and forwards to the cloud primitive (POST /api/v1/account/notify-owner). No Resend
key ever touches this machine.
"""

from __future__ import annotations

import base64
import json
import mimetypes
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional
from urllib.parse import urlparse

import requests

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import gateway  # noqa: E402
from cc_shared.config import CCDirectorConfig  # noqa: E402

from . import axi_cli  # noqa: E402

LOOPBACK_DEFAULT = "http://127.0.0.1:7878"
TIMEOUT_SECONDS = 45

gateway_override: Optional[str] = None


class GatewayError(Exception):
    """A handled, user-facing failure talking to the Gateway."""


class AttachmentError(Exception):
    """An --attach file is missing or unreadable - the caller's to fix before anything is sent."""


_OWNER_USAGE = 'cc-devthrottle email owner --subject "<subject>" --body "<text>"'


def set_gateway_override(value: Optional[str]) -> None:
    global gateway_override
    gateway_override = value.rstrip("/") if value else None


def resolve_base_url() -> str:
    config = CCDirectorConfig().load()
    url = (config.gateway.url or "").strip()
    return url.rstrip("/") if url else LOOPBACK_DEFAULT


def _auth_token() -> str:
    """The ACCOUNT's Gateway credential, deliberately NOT this session's key.

    Remove-the-network-port mission, phase 2: the skill, workflow, schedule and mission clients all
    moved off this token and onto the session key, which closed a real hole - every agent that ran one
    of those commands used to hold authority over the whole account. This client did NOT move, and the
    reason is the session-key guard's own ruling rather than an oversight: it draws the line at the
    ACCOUNT surface, and names the diagnostics and account routes as the owner's. A session key is
    REFUSED here, so moving this client onto one would not narrow the command - it would break it.

    So this stays an owner command, run by the person, on the owner's credential. It never called the
    Director and is unaffected by the Director's agent surface being switched off.
    """
    config = CCDirectorConfig().load()
    return (config.gateway.token or "").strip()


def _is_loopback(url: str) -> bool:
    host = (urlparse(url).hostname or "").lower()
    return host in ("127.0.0.1", "localhost", "::1")


def _read_attachment(path_text: str) -> Dict[str, str]:
    """Read one file into the wire shape { filename, content(base64), contentType }."""
    path = Path(path_text).expanduser()
    if not path.is_file():
        raise AttachmentError(f"attachment not found: {path}")
    try:
        data = path.read_bytes()
    except OSError as exc:
        raise AttachmentError(f"attachment could not be read: {path}: {exc}") from exc
    content_type = mimetypes.guess_type(path.name)[0] or "application/octet-stream"
    return {
        "filename": path.name,
        "content": base64.b64encode(data).decode("ascii"),
        "contentType": content_type,
    }


class EmailClient:
    """Talks to one Gateway's owner-email relay (POST /account/email)."""

    def __init__(self, base_url: Optional[str] = None) -> None:
        self.base_url = (base_url or resolve_base_url()).rstrip("/")
        self._token = _auth_token()
        if not self._token and not _is_loopback(self.base_url):
            raise GatewayError(
                f"Gateway URL {self.base_url} is remote but gateway.token is not set. "
                "Set it with 'cc-devthrottle settings set gateway.token <token>' "
                "(a loopback Gateway on this machine needs no token)."
            )

    def _headers(self) -> Dict[str, str]:
        headers = {"Accept": "application/json"}
        if self._token:
            headers["Authorization"] = f"Bearer {self._token}"
        return headers

    def send_owner(
        self,
        subject: str,
        body_text: Optional[str],
        body_html: Optional[str],
        attachments: List[Dict[str, str]],
    ) -> Dict[str, Any]:
        payload: Dict[str, Any] = {"subject": subject}
        if body_text:
            payload["bodyText"] = body_text
        if body_html:
            payload["bodyHtml"] = body_html
        if attachments:
            payload["attachments"] = attachments

        url = f"{self.base_url}/account/email"
        try:
            resp = requests.post(url, json=payload, headers=self._headers(), timeout=TIMEOUT_SECONDS)
        except requests.exceptions.ConnectionError as exc:
            raise GatewayError(
                f"Gateway not reachable at {self.base_url}. "
                "Is the Gateway running? "
                "If you target a remote Gateway, set gateway.url with "
                "'cc-devthrottle settings set gateway.url <url>'."
            ) from exc
        except requests.exceptions.Timeout as exc:
            raise GatewayError(
                f"Gateway at {self.base_url} did not respond within {TIMEOUT_SECONDS}s."
            ) from exc

        if 200 <= resp.status_code < 300:
            # The shared guard, not a bare resp.json(): a request no endpoint matches falls
            # through to the Gateway's web app and answers HTTP 200 with text/html (issue #2486).
            # This module's own GatewayError travels along so the handlers here still catch it.
            return gateway.parse_json_body(resp, self.base_url, GatewayError)
        raise GatewayError(_gateway_message(resp))


def _gateway_message(resp: requests.Response) -> str:
    try:
        data = resp.json()
        if isinstance(data, dict) and data.get("error"):
            return str(data["error"])
    except ValueError:
        pass
    text = (resp.text or "").strip()
    return text if text else f"Gateway returned HTTP {resp.status_code}"


def _fail(message: str) -> None:
    """The send failed or its outcome is unknown. Report it on standard error with where to look next,
    and exit 1. It does not claim nothing was sent: after a timeout the relay may have sent it."""
    axi_cli.fail(
        message,
        ["cc-devthrottle settings get gateway.url", "cc-devthrottle email --gateway <url> owner --help"],
    )


def send_owner(
    subject: str,
    body: Optional[str],
    html: Optional[str],
    attach: Optional[List[str]],
    json_output: bool,
) -> None:
    """Send one email to the account owner via the Gateway relay."""
    if not subject or not subject.strip():
        axi_cli.usage_error("--subject must not be blank.", [_OWNER_USAGE])
    if not (body and body.strip()) and not (html and html.strip()) and not attach:
        axi_cli.usage_error(
            "provide a body: --body <text>, --html <html>, and/or --attach <file>.", [_OWNER_USAGE]
        )

    try:
        attachments = [_read_attachment(p) for p in (attach or [])]
    except AttachmentError as ex:
        axi_cli.usage_error(
            f"{ex}. Nothing was sent.",
            ['cc-devthrottle email owner --subject "<subject>" --attach "<existing-file>"'],
        )
    try:
        result = EmailClient(base_url=gateway_override).send_owner(subject.strip(), body, html, attachments)
    except GatewayError as ex:
        _fail(str(ex))
        return

    if json_output:
        print(json.dumps(result, indent=2))
        return

    provider_id = result.get("providerId")
    count = len(attach or [])
    suffix = f" with {count} attachment(s)" if count else ""
    if provider_id:
        axi_cli.write_lines(f"Sent email to the account owner{suffix} (id {provider_id}).")
    else:
        axi_cli.write_lines(f"Sent email to the account owner{suffix}.")
    # An email is the escalation an unattended run sends before it winds itself down.
    axi_cli.print_next(["cc-devthrottle session done", "cc-devthrottle session whoami"])
