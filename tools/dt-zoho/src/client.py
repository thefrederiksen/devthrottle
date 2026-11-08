"""The Zoho API client: every call signed with the access token, every error turned into one sentence.

Zoho answers errors in two shapes. Mail: `{"status": {"code": 404, "description": "Invalid Input"},
"data": {"errorCode": "URL_RULE_NOT_CONFIGURED"}}` (or `"moreInfo"`). Calendar: `{"error": [{"description":
"Event not found", "error_code": "EVENT_NOTFOUND"}]}`. Both are read here.
"""

from __future__ import annotations

import json
import urllib.parse
from typing import Any, Dict, Optional

from . import auth, config, transport
from .errors import DtZohoError


class ZohoClient:
    def __init__(self, settings: config.Settings, creds: config.Credentials):
        self.centre = settings.centre()
        self.creds = creds
        self._token: Optional[auth.Token] = None

    @classmethod
    def from_environment(cls) -> "ZohoClient":
        settings = config.load()
        settings.centre()               # a missing data centre is reported before the key is asked for
        return cls(settings, config.credentials())

    def token(self) -> auth.Token:
        if self._token is None:
            self._token = auth.access_token(self.creds, self.centre)
        return self._token

    # ----- calls

    def mail(self, method: str, path: str, **kwargs) -> Any:
        return self._call(method, self.centre.mail + path, **kwargs)

    def calendar(self, method: str, path: str, **kwargs) -> Any:
        return self._call(method, self.centre.calendar + path, **kwargs)

    def mail_bytes(self, path: str) -> bytes:
        """A GET whose answer is a file (an attachment), returned as the exact bytes Zoho sent."""
        answer = self._send("GET", self.centre.mail + path, {"Accept": "*/*"}, None)
        if answer.status != 200:
            raise DtZohoError(_error_sentence("GET", path, answer))
        return answer.body

    def mail_upload(self, path: str, query: Dict[str, Any], data: bytes) -> Any:
        """A POST whose body is a file's exact bytes (an attachment upload), answered with JSON."""
        return self._call("POST", self.centre.mail + path, query=query, raw_body=data)

    def _call(self, method: str, url: str, query: Optional[Dict[str, Any]] = None,
              json_body: Optional[Any] = None, raw_body: Optional[bytes] = None) -> Any:
        if query:
            url += ("&" if "?" in url else "?") + urllib.parse.urlencode(
                {k: v for k, v in query.items() if v is not None})
        headers = {"Accept": "application/json"}
        body = None
        if json_body is not None:
            body = json.dumps(json_body).encode("utf-8")
            headers["Content-Type"] = "application/json"
        elif raw_body is not None:
            body = raw_body
            headers["Content-Type"] = "application/octet-stream"
        answer = self._send(method, url, headers, body)
        path = url.split("?")[0]
        data = _json(answer)
        if answer.status >= 400 or _status_code(data) >= 400:
            raise DtZohoError(_error_sentence(method, path, answer, data))
        if not isinstance(data, dict):
            raise DtZohoError(f"Zoho answered {method} {path} with something that is not JSON "
                              f"({answer.status}, {len(answer.body)} bytes).")
        return data

    def _send(self, method: str, url: str, headers: Dict[str, str], body: Optional[bytes]) -> transport.Response:
        token = self.token()
        answer = transport.send(method, url, dict(headers, Authorization="Zoho-oauthtoken " + token.value), body)
        if answer.status == 401 and token.source == "cache" and _says_invalid_token(answer):
            # The cached token was revoked before its hour was up (a new key, a sign-out at Zoho). Replace it once.
            auth.forget()
            self._token = auth.access_token(self.creds, self.centre)
            answer = transport.send(method, url,
                                    dict(headers, Authorization="Zoho-oauthtoken " + self._token.value), body)
        if 300 <= answer.status < 400:
            raise DtZohoError(f"Zoho answered {method} {url.split('?')[0]} with a redirect to "
                              f"{answer.headers.get('location', '(no address)')}. dt-zoho never follows a redirect "
                              "with the access token; check the data centre (dt-zoho settings show).")
        return answer


def _json(answer: transport.Response) -> Any:
    try:
        return json.loads(answer.body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return None


def _status_code(data: Any) -> int:
    if isinstance(data, dict) and isinstance(data.get("status"), dict):
        try:
            return int(data["status"].get("code", 0))
        except (TypeError, ValueError):
            return 0
    return 0


def _says_invalid_token(answer: transport.Response) -> bool:
    return b"INVALID_OAUTHTOKEN" in answer.body.upper()


def zoho_error_code(data: Any) -> str:
    """Zoho's own error words from either answer shape, or an empty string."""
    if not isinstance(data, dict):
        return ""
    parts = []
    status = data.get("status")
    if isinstance(status, dict) and status.get("description"):
        parts.append(str(status["description"]))
    inner = data.get("data")
    if isinstance(inner, dict):
        for key in ("errorCode", "moreInfo"):
            if inner.get(key):
                parts.append(str(inner[key]))
    errors = data.get("error")
    if isinstance(errors, list):
        for item in errors:
            if isinstance(item, dict):
                parts.append(" ".join(str(item[k]) for k in ("error_code", "description") if item.get(k)))
    elif isinstance(errors, str):
        parts.append(errors)
    return "; ".join(p for p in parts if p)


def _error_sentence(method: str, path: str, answer: transport.Response, data: Any = None) -> str:
    if data is None:
        data = _json(answer)
    said = zoho_error_code(data) or answer.body.decode("utf-8", errors="replace")[:200]
    code = _status_code(data) or answer.status
    hint = ""
    if "INVALID_OAUTHSCOPE" in said.upper():
        hint = " The Zoho key does not have the scope this call needs; a new key with that scope is needed."
    elif code == 429 or "TOO_MANY" in said.upper():
        hint = " Zoho is limiting the rate of calls; wait a minute and run it again."
    return f"Zoho answered {code} to {method} {path}: {said}.{hint}"
