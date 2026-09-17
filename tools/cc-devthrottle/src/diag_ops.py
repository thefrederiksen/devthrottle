"""Network diagnostics operations for cc-devthrottle (Network Diagnostics mission).

Lets an AGENT run the network diagnostic directly - no phone, no app - against the loopback Gateway
(or a configured remote one). Two reads on the Gateway Control API:

  GET /diag/network  - the server-side Tailscale check: per connected device, direct-vs-DERP-relay +
                       latency, plus UDP/NAT health. This is the one signal the phone speed test cannot
                       see (it cannot tell "warming up on the relay" apart from "genuinely slow").
  GET /diag/results  - the recent speed-test results users have submitted from the app/Cockpit.

Mirrors the mission/schedule command style (Gateway Control API, Bearer token from config, loopback
needs none).
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional
from urllib.parse import urlparse

import requests
from rich import box
from rich.console import Console
from rich.table import Table

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import gateway  # noqa: E402
from cc_shared.config import CCDirectorConfig  # noqa: E402

from . import axi_cli  # noqa: E402

console = Console()

LOOPBACK_DEFAULT = "http://127.0.0.1:7878"
TIMEOUT_SECONDS = 25  # /diag/network shells the CLI and pings peers, so allow more than the usual 10s.


class GatewayError(Exception):
    """A handled, user-facing failure talking to the Gateway."""


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


class DiagClient:
    """Talks to one Gateway's /diag surface."""

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

    def _get(self, path: str) -> Any:
        url = f"{self.base_url}{path}"
        try:
            resp = requests.get(url, headers=self._headers(), timeout=TIMEOUT_SECONDS)
        except requests.exceptions.ConnectionError as exc:
            raise GatewayError(
                f"Gateway not reachable at {self.base_url}. Is the Gateway running? A remote Gateway "
                "is set with 'cc-devthrottle settings set gateway.url <url>'."
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

    def network(self) -> Dict[str, Any]:
        return self._get("/diag/network")

    def results(self) -> List[Dict[str, Any]]:
        data = self._get("/diag/results")
        return data if isinstance(data, list) else []


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
    """The diagnostic could not be read. Report it on standard error with where to look, and exit 1."""
    axi_cli.fail(message, ["cc-devthrottle settings get gateway.url", "cc-devthrottle settings get gateway.token"])


def show_network(json_output: bool) -> None:
    """Print the server-side Tailscale diagnostic: per-device direct-vs-relay + latency, plus UDP/NAT."""
    try:
        diag = DiagClient().network()
    except GatewayError as exc:
        _fail(str(exc))

    if json_output:
        console.print_json(json.dumps(diag))
        return

    if not diag.get("tailscaleAvailable", False):
        console.print("[yellow]Tailscale CLI not found on the Gateway machine.[/yellow]")
        for note in diag.get("notes", []) or []:
            console.print(f"  - {note}")
        return

    console.print(
        f"Gateway [bold]{diag.get('selfName') or '?'}[/bold] "
        f"(tailscale {diag.get('selfTailscaleIp') or '?'})  "
        f"backend={diag.get('backendState') or '?'}"
    )
    udp = diag.get("udpOk")
    varies = diag.get("mappingVariesByDestIp")
    console.print(
        f"UDP={_yn(udp)}  hard-NAT(MappingVariesByDestIP)={_yn(varies)}  "
        f"nearest-DERP={diag.get('nearestDerp') or '?'}"
    )

    peers = diag.get("peers", []) or []
    table = Table(box=box.SIMPLE_HEAVY, show_edge=False)
    table.add_column("Device")
    table.add_column("Tailscale IP")
    table.add_column("OS")
    table.add_column("Online")
    table.add_column("Path")
    table.add_column("Latency")
    table.add_column("Note")
    for p in peers:
        direct = p.get("direct")
        if direct is True:
            path = f"[green]direct[/green] {p.get('path') or ''}"
        elif direct is False:
            path = f"[red]RELAY[/red] {p.get('path') or ''}"
        else:
            path = p.get("path") or "-"
        latency = p.get("latencyMs")
        table.add_row(
            str(p.get("name") or "?"),
            str(p.get("tailscaleIp") or "-"),
            str(p.get("os") or "-"),
            _yn(p.get("online")),
            path,
            f"{latency:.0f} ms" if isinstance(latency, (int, float)) else "-",
            str(p.get("note") or ""),
        )
    console.print(table)

    for note in diag.get("notes", []) or []:
        console.print(f"[yellow]note:[/yellow] {note}")

    relayed = [p for p in peers if p.get("online") and p.get("direct") is False]
    if relayed:
        console.print(
            f"[yellow]{len(relayed)} online device(s) are RELAYING through DERP instead of a direct path.[/yellow] "
            "On the same LAN this is the cold-start window or a blocked direct path (check UDP, client isolation, exit node)."
        )


def show_results(json_output: bool) -> None:
    """Print the recent speed-test results users submitted from the app/Cockpit."""
    try:
        results = DiagClient().results()
    except GatewayError as exc:
        _fail(str(exc))

    if json_output:
        console.print_json(json.dumps(results))
        return

    if not results:
        console.print("No speed-test results logged yet.")
        return

    table = Table(box=box.SIMPLE_HEAVY, show_edge=False)
    table.add_column("Received (UTC)")
    table.add_column("Surface")
    table.add_column("Gateway sees")
    table.add_column("Latency")
    table.add_column("Down")
    table.add_column("Up")
    table.add_column("Rating")
    for r in results:
        table.add_row(
            _short_time(r.get("receivedAt")),
            str(r.get("surface") or "-"),
            f"{r.get('clientIp') or '?'} ({r.get('clientPath') or '?'})",
            _ms(r.get("latencyMedianMs")),
            _mbps(r.get("downloadMbps")),
            _mbps(r.get("uploadMbps")),
            str(r.get("rating") or "-"),
        )
    console.print(table)


def _yn(value: Any) -> str:
    if value is True:
        return "yes"
    if value is False:
        return "no"
    return "?"


def _ms(value: Any) -> str:
    return f"{value:.0f} ms" if isinstance(value, (int, float)) else "-"


def _mbps(value: Any) -> str:
    return f"{value:.1f} Mbps" if isinstance(value, (int, float)) else "-"


def _short_time(value: Any) -> str:
    if not value:
        return "-"
    text = str(value)
    return text[:19].replace("T", " ")
