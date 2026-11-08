"""The one place dt-zoho talks to the network. Tests replace `send` with the fake Zoho in tests/fakes.py.

Redirects are never followed: a redirect would carry the access token in the Authorization header to wherever
it points, so a 3xx answer is returned to the caller, which refuses it with the address it pointed to.
"""

from __future__ import annotations

import socket
import ssl
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from typing import Dict, Optional

from .errors import DtZohoError

TIMEOUT_SECONDS = 90


@dataclass
class Response:
    status: int
    body: bytes
    headers: Dict[str, str] = field(default_factory=dict)   # names in lower case


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


_opener = urllib.request.build_opener(_NoRedirect)


def send(method: str, url: str, headers: Dict[str, str], body: Optional[bytes] = None) -> Response:
    """One HTTPS request. Any HTTP status comes back as a Response; only an unreachable server raises."""
    request = urllib.request.Request(url, data=body, method=method, headers=headers)
    try:
        with _opener.open(request, timeout=TIMEOUT_SECONDS) as answer:
            return Response(answer.status, answer.read(), {k.lower(): v for k, v in answer.headers.items()})
    except urllib.error.HTTPError as exc:
        return Response(exc.code, exc.read(), {k.lower(): v for k, v in (exc.headers or {}).items()})
    except ssl.SSLError as exc:
        raise DtZohoError(f"the secure connection to {url.split('/')[2]} failed: {exc}.") from exc
    except (urllib.error.URLError, ConnectionError, TimeoutError, socket.timeout, socket.gaierror) as exc:
        reason = getattr(exc, "reason", exc)
        raise DtZohoError(f"could not reach {url.split('/')[2]}: {reason}. Check the network and the data centre "
                          "(dt-zoho settings show).") from exc
