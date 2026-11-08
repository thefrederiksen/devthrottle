"""The CalDAV side of dt-calendar (RFC 4791, with RFC 6764 discovery), over plain HTTPS and Basic sign-in.

Things learned on a real server (Zoho) that this code is shaped around:
- OPTIONS answers 400 with no DAV header, so discovery uses PROPFIND on /.well-known/caldav only.
- A calendar-query REPORT returns ETags only, never calendar-data; bodies come from calendar-multiget.
- A wrong If-Match answers 409, not 412. Both mean "changed since you read it".
- A GET of a deleted event answers 200 with an empty body. An empty body means gone.
- A PUT answer carries no ETag; the event is read back to learn it.
- Attendees outside the server's own organisation may be dropped without an error, so every write is
  read back and compared.
"""

from __future__ import annotations

import base64
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape as xml_escape
from dataclasses import dataclass
from typing import Dict, List, Optional, Tuple

from .config import Account
from .errors import DtCalendarError

DAV = "{DAV:}"
CAL = "{urn:ietf:params:xml:ns:caldav}"
TIMEOUT_SECONDS = 60
MAX_REDIRECTS = 5


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


_OPENER = urllib.request.build_opener(_NoRedirect)


def send(method: str, url: str, headers: Dict[str, str], body: Optional[bytes]) -> Tuple[int, Dict[str, str], bytes]:
    """One HTTP request: (status, headers, body). Tests replace this function with a fake server."""
    request = urllib.request.Request(url, data=body, method=method, headers=headers)
    try:
        with _OPENER.open(request, timeout=TIMEOUT_SECONDS) as response:
            return response.status, {k.lower(): v for k, v in response.headers.items()}, response.read()
    except urllib.error.HTTPError as exc:
        return exc.code, {k.lower(): v for k, v in exc.headers.items()}, exc.read()


@dataclass
class Calendar:
    href: str
    name: str
    components: List[str]


@dataclass
class EventResource:
    href: str
    etag: Optional[str]
    data: str

    @property
    def id(self) -> str:
        return urllib.parse.unquote(self.href.rstrip("/").rsplit("/", 1)[-1])


def _xml(text: bytes, what: str) -> ET.Element:
    try:
        return ET.fromstring(text)
    except ET.ParseError as exc:
        raise DtCalendarError(f"the server's answer to {what} is not XML dt-calendar can read: {exc}") from exc


def _propfind_body(props: str) -> bytes:
    return (f'<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:" '
            f'xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop>{props}</d:prop></d:propfind>').encode()


class CalDavClient:
    def __init__(self, account: Account, password: str):
        self.account = account
        token = base64.b64encode(f"{account.user}:{password}".encode()).decode()
        self._auth = f"Basic {token}"
        self._base = account.url.rstrip("/")
        self._home: Optional[str] = None

    # ----- plumbing

    def _url(self, href: str) -> str:
        return href if href.startswith("http") else urllib.parse.urljoin(self._base + "/", href)

    def request(self, method: str, href: str, body: Optional[bytes] = None, headers: Optional[Dict[str, str]] = None,
                ok: Tuple[int, ...] = (200, 201, 204, 207)) -> Tuple[int, Dict[str, str], bytes]:
        url = self._url(href)
        all_headers = {"Authorization": self._auth, **(headers or {})}
        for _ in range(MAX_REDIRECTS + 1):
            status, response_headers, data = send(method, url, all_headers, body)
            if status in (301, 302, 307, 308) and "location" in response_headers:
                url = urllib.parse.urljoin(url, response_headers["location"])
                continue
            if status == 401:
                raise DtCalendarError(
                    f"the CalDAV server {self._base} refused the sign-in for {self.account.user} (401). Check the "
                    f"password held in cc-secrets entry '{self.account.secret_entry}'.")
            if status not in ok:
                text = data.decode("utf-8", "replace").strip()[:300]
                raise DtCalendarError(f"the CalDAV server answered {method} {url} with {status}. {text}".rstrip())
            return status, response_headers, data
        raise DtCalendarError(f"the CalDAV server redirected {method} more than {MAX_REDIRECTS} times.")

    def _propfind(self, href: str, props: str, depth: str) -> ET.Element:
        _, _, data = self.request("PROPFIND", href, _propfind_body(props),
                                  {"Depth": depth, "Content-Type": "application/xml; charset=utf-8"})
        return _xml(data, f"PROPFIND {href}")

    @staticmethod
    def _href_in(root: ET.Element, prop: str) -> Optional[str]:
        node = root.find(f".//{prop}/{DAV}href")
        return node.text.strip() if node is not None and node.text else None

    # ----- discovery

    def home(self) -> str:
        """The calendar home: /.well-known/caldav -> principal -> calendar-home-set."""
        if self._home is None:
            root = self._propfind("/.well-known/caldav", "<d:current-user-principal/>", "0")
            principal = self._href_in(root, f"{DAV}current-user-principal")
            if principal is None:
                raise DtCalendarError(
                    f"{self._base} did not name a principal for {self.account.user} at /.well-known/caldav. "
                    "Check --url: it must be the CalDAV server.")
            root = self._propfind(principal, "<c:calendar-home-set/>", "0")
            home = self._href_in(root, f"{CAL}calendar-home-set")
            if home is None:
                raise DtCalendarError(f"{self._base} named no calendar home for {self.account.user}.")
            self._home = home
        return self._home

    def calendars(self) -> List[Calendar]:
        root = self._propfind(self.home(), "<d:displayname/><d:resourcetype/><c:supported-calendar-component-set/>",
                              "1")
        found: List[Calendar] = []
        for response in root.findall(f"{DAV}response"):
            href = response.findtext(f"{DAV}href", "").strip()
            if response.find(f".//{DAV}resourcetype/{CAL}calendar") is None:
                continue
            name = (response.findtext(f".//{DAV}displayname") or "").strip()
            comps = [c.get("name", "") for c in response.findall(f".//{CAL}supported-calendar-component-set/{CAL}comp")]
            found.append(Calendar(href=href, name=name or href.rstrip("/").rsplit("/", 1)[-1], components=comps))
        return found

    def calendar(self, name: Optional[str]) -> Calendar:
        """The calendar with this display name or path, or the only event calendar there is."""
        cals = [c for c in self.calendars() if not c.components or "VEVENT" in c.components]
        if name is None:
            if len(cals) == 1:
                return cals[0]
            raise DtCalendarError(
                f"this account has {len(cals)} event calendars ({', '.join(c.name for c in cals)}). "
                "Pass --calendar <name>. List them with: dt-calendar calendars")
        matches = [c for c in cals if name in (c.name, c.href, c.href.rstrip("/").rsplit("/", 1)[-1])]
        if len(matches) != 1:
            raise DtCalendarError(
                f"no single calendar named '{name}'. Calendars: {', '.join(c.name for c in cals)}. "
                "List them with: dt-calendar calendars", exit_code=2)
        return matches[0]

    # ----- events

    def event_hrefs(self, cal: Calendar, start_utc: str, end_utc: str) -> List[Tuple[str, Optional[str]]]:
        """(href, etag) of events overlapping the range. Some servers (Zoho) send no bodies here."""
        body = (f'<?xml version="1.0" encoding="utf-8"?><c:calendar-query xmlns:d="DAV:" '
                f'xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><d:getetag/></d:prop><c:filter>'
                f'<c:comp-filter name="VCALENDAR"><c:comp-filter name="VEVENT">'
                f'<c:time-range start="{start_utc}" end="{end_utc}"/></c:comp-filter></c:comp-filter></c:filter>'
                f'</c:calendar-query>').encode()
        _, _, data = self.request("REPORT", cal.href, body, {"Depth": "1", "Content-Type": "application/xml; charset=utf-8"})
        found = []
        for response in _xml(data, "calendar-query").findall(f"{DAV}response"):
            href = response.findtext(f"{DAV}href", "").strip()
            if href.rstrip("/") == cal.href.rstrip("/") or not href:
                continue
            found.append((href, (response.findtext(f".//{DAV}getetag") or "").strip() or None))
        return found

    def multiget(self, cal: Calendar, hrefs: List[str]) -> List[EventResource]:
        if not hrefs:
            return []
        refs = "".join(f"<d:href>{xml_escape(h)}</d:href>" for h in hrefs)
        body = (f'<?xml version="1.0" encoding="utf-8"?><c:calendar-multiget xmlns:d="DAV:" '
                f'xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><d:getetag/><c:calendar-data/></d:prop>{refs}'
                f'</c:calendar-multiget>').encode()
        _, _, data = self.request("REPORT", cal.href, body, {"Depth": "1", "Content-Type": "application/xml; charset=utf-8"})
        found = []
        for response in _xml(data, "calendar-multiget").findall(f"{DAV}response"):
            text = response.findtext(f".//{CAL}calendar-data")
            if not text or not text.strip():
                continue
            found.append(EventResource(href=response.findtext(f"{DAV}href", "").strip(),
                                       etag=(response.findtext(f".//{DAV}getetag") or "").strip() or None, data=text))
        return found

    def event_href(self, cal: Calendar, event_id: str) -> str:
        return cal.href.rstrip("/") + "/" + urllib.parse.quote(event_id)

    def get(self, cal: Calendar, event_id: str) -> EventResource:
        """One event by its id (the last part of its address). Gone means 404, or 200 with an empty body (Zoho)."""
        href = self.event_href(cal, event_id)
        status, headers, data = self.request("GET", href, ok=(200, 404))
        if status == 404 or not data.strip():
            raise DtCalendarError(f"there is no event '{event_id}' in calendar '{cal.name}'. "
                                  f"List events with: dt-calendar events --calendar \"{cal.name}\"")
        return EventResource(href=href, etag=headers.get("etag"), data=data.decode("utf-8"))

    def put(self, href: str, ics_text: str, create: bool, etag: Optional[str]) -> None:
        headers = {"Content-Type": "text/calendar; charset=utf-8"}
        if create:
            headers["If-None-Match"] = "*"
        elif etag:
            headers["If-Match"] = etag
        status, _, data = self.request("PUT", href, ics_text.encode("utf-8"), headers, ok=(200, 201, 204, 409, 412))
        if status in (409, 412):
            what = "an event with this id already exists" if create else "the event changed on the server since it was read"
            raise DtCalendarError(f"the server refused the write ({status}): {what}. Nothing was changed. "
                                  "Read it again and repeat the command.")

    def delete(self, href: str) -> None:
        self.request("DELETE", href, ok=(200, 204))
        status, _, data = self.request("GET", href, ok=(200, 404, 410))
        if status == 200 and data.strip():
            raise DtCalendarError(f"the server answered the delete, but {href} is still there.")
