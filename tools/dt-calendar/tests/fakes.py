"""An in-memory CalDAV server for the dt-calendar tests. No network.

It answers the way Zoho answered in the proof (proof\\prover-a\\evidence\\caldav-*.txt): discovery through
/.well-known/caldav, a calendar-query that returns ETags only, calendar-multiget with the bodies, numeric
ETags and none on a PUT answer, 409 for a wrong If-Match, 200 with an empty body for a deleted event, a
15-minute alarm added to every event, attendees inside the organisation given a copy in their own calendar
(SCHEDULE-AGENT=CLIENT) and attendees outside it dropped without an error, an attendee's answer copied to
the organizer, and the organizer's delete removing every copy.
"""

from __future__ import annotations

import base64
import itertools
import re
import urllib.parse
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple

DAV = "{DAV:}"
CAL = "{urn:ietf:params:xml:ns:caldav}"
_etags = itertools.count(1794104890119)


@dataclass
class FakeUser:
    email: str
    password: str
    key: str
    events: Dict[str, Tuple[str, str]] = field(default_factory=dict)  # name -> (ics, etag)

    @property
    def home(self) -> str:
        return f"/caldav/{self.key}/"

    @property
    def calendar(self) -> str:
        return f"/caldav/{self.key}/events/"


class FakeCalDav:
    def __init__(self, org_domain="example.com", redirect_well_known=False, keeps_removed_attendees=False):
        # Zoho keeps an attendee the organizer removes over CalDAV (seen in the live run).
        self.keeps_removed_attendees = keeps_removed_attendees
        self.org_domain = org_domain
        self.redirect_well_known = redirect_well_known
        self.users: Dict[str, FakeUser] = {}
        self.requests: List[Tuple[str, str, Dict[str, str]]] = []

    def add_user(self, email: str, password: str, key: str) -> FakeUser:
        user = FakeUser(email, password, key)
        self.users[email] = user
        return user

    # ----- helpers

    def _user(self, headers) -> Optional[FakeUser]:
        auth = headers.get("Authorization", "")
        if not auth.startswith("Basic "):
            return None
        name, _, password = base64.b64decode(auth[6:]).decode().partition(":")
        user = self.users.get(name)
        return user if user and user.password == password else None

    @staticmethod
    def _ms(responses: List[str]) -> bytes:
        return ('<?xml version="1.0" encoding="UTF-8"?><D:multistatus xmlns:D="DAV:" '
                'xmlns:C="urn:ietf:params:xml:ns:caldav">' + "".join(responses) + "</D:multistatus>").encode()

    @staticmethod
    def _resp(href: str, props: str) -> str:
        return (f"<D:response><D:href>{href}</D:href><D:propstat><D:prop>{props}</D:prop>"
                "<D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>")

    def _owner_of(self, path: str) -> Optional[FakeUser]:
        for u in self.users.values():
            if path.startswith(u.calendar):
                return u
        return None

    def _store(self, user: FakeUser, name: str, ics_text: str) -> str:
        etag = str(next(_etags))
        user.events[name] = (ics_text, etag)
        return etag

    # ----- the server

    def send(self, method, url, headers, body):
        path = urllib.parse.urlparse(url).path
        self.requests.append((method, path, dict(headers)))
        user = self._user(headers)
        if user is None:
            return 401, {}, b"Unauthorized"
        if method == "OPTIONS":
            return 400, {}, b""
        if method == "PROPFIND":
            return self._propfind(user, path, headers.get("Depth", "0"))
        if method == "REPORT":
            return self._report(user, path, body)
        name = path.rsplit("/", 1)[-1]
        owner = self._owner_of(path)
        if owner is None or owner is not user:
            return 403, {}, b"Forbidden"
        if method == "GET":
            if name not in user.events:
                return 200, {}, b""  # Zoho: a deleted event answers 200 with nothing in it
            text, etag = user.events[name]
            return 200, {"etag": etag}, text.encode()
        if method == "PUT":
            return self._put(user, name, headers, body.decode("utf-8"))
        if method == "DELETE":
            if name not in user.events:
                return 404, {}, b""
            text, _ = user.events.pop(name)
            uid = re.search(r"^UID:(.+)$", text, re.M).group(1).strip()
            for other in self.users.values():
                if other is not user:
                    other.events = {n: v for n, v in other.events.items() if f"UID:{uid}" not in v[0]}
            return 204, {}, b""
        return 405, {}, b""

    def _propfind(self, user: FakeUser, path: str, depth: str):
        if path == "/.well-known/caldav":
            if self.redirect_well_known:
                return 301, {"location": "/dav/"}, b""
            return 207, {}, self._ms([self._resp(path, f"<D:current-user-principal><D:href>/caldav/{user.key}/user/"
                                                       "</D:href></D:current-user-principal>")])
        if path == "/dav/":
            return 207, {}, self._ms([self._resp(path, f"<D:current-user-principal><D:href>/caldav/{user.key}/user/"
                                                       "</D:href></D:current-user-principal>")])
        if path == f"/caldav/{user.key}/user/":
            return 207, {}, self._ms([self._resp(path, f"<C:calendar-home-set><D:href>{user.home}</D:href>"
                                                       "</C:calendar-home-set>")])
        if path == user.home and depth == "1":
            return 207, {}, self._ms([
                self._resp(user.home, "<D:resourcetype><D:collection/><D:principal/></D:resourcetype>"),
                self._resp(user.calendar, "<D:displayname>soren</D:displayname><D:resourcetype><C:calendar/>"
                                          "<D:collection/></D:resourcetype><C:supported-calendar-component-set>"
                                          '<C:comp name="VEVENT"/></C:supported-calendar-component-set>'),
                self._resp(user.home + "inbox/", "<D:resourcetype><D:collection/><C:schedule-inbox/></D:resourcetype>"),
            ])
        return 404, {}, b""

    def _report(self, user: FakeUser, path: str, body: bytes):
        if path != user.calendar:
            return 403, {}, b""
        root = ET.fromstring(body)
        if root.tag == f"{CAL}calendar-query":
            tr = root.find(f".//{CAL}time-range")
            start, end = tr.get("start"), tr.get("end")
            out = [self._resp(user.calendar, "")]
            for name, (text, etag) in user.events.items():
                s = re.search(r"^DTSTART[^:]*:(\d{8})(T\d{6}Z?)?", text, re.M)
                stamp = s.group(1) + (s.group(2) or "T000000Z")
                if not stamp.endswith("Z"):
                    stamp += "Z"
                if start <= stamp < end:
                    out.append(self._resp(user.calendar + name, f"<D:getetag>{etag}</D:getetag>"))  # never the data
            return 207, {}, self._ms(out)
        if root.tag == f"{CAL}calendar-multiget":
            out = []
            for href in root.findall(f"{DAV}href"):
                name = href.text.rsplit("/", 1)[-1]
                if name in user.events:
                    text, etag = user.events[name]
                    data = text.replace("&", "&amp;").replace("<", "&lt;")
                    out.append(self._resp(href.text, f"<D:getetag>{etag}</D:getetag>"
                                                     f"<C:calendar-data>{data}</C:calendar-data>"))
            return 207, {}, self._ms(out)
        return 400, {}, b""

    def _put(self, user: FakeUser, name: str, headers, text: str):
        text = re.sub(r"\r\n[ \t]", "", text).rstrip("\r\n")  # unfold, as a real server does
        exists = name in user.events
        if headers.get("If-None-Match") == "*" and exists:
            return 412, {}, b""
        if "If-Match" in headers and (not exists or user.events[name][1] != headers["If-Match"]):
            return 409, {}, b""  # Zoho answers a wrong If-Match with 409, not 412
        organizer = re.search(r"^ORGANIZER[^:\r\n]*:mailto:([^\s]+)", text, re.M | re.I)
        is_organizer = organizer is None or organizer.group(1).lower() == user.email
        lines = text.split("\r\n")
        if is_organizer:
            # Drop attendees outside the organisation, silently (Zoho over CalDAV).
            kept = [l for l in lines if not (l.startswith("ATTENDEE") and
                                             not l.lower().endswith("@" + self.org_domain))]
            lines = kept
            if exists and self.keeps_removed_attendees:
                before = [l for l in user.events[name][0].split("\r\n") if l.startswith("ATTENDEE")]
                missing = [l for l in before if l not in lines]
                if missing:
                    at = lines.index("END:VEVENT")
                    lines[at:at] = missing
            if not exists and "BEGIN:VALARM" not in text:
                end = lines.index("END:VEVENT")
                lines[end:end] = ["BEGIN:VALARM", "ACTION:EMAIL", f"ATTENDEE:mailto:{user.email}",
                                  "TRIGGER;VALUE=DURATION;RELATED=START:-PT15M", "END:VALARM"]
            lines = ["PRODID:-//Zoho Corporation//Zoho Calendar-CA//EN" if l.startswith("PRODID") else l for l in lines]
        text = "\r\n".join(lines)
        self._store(user, name, text)
        uid = re.search(r"^UID:([^\r\n]+)", text, re.M).group(1).strip()
        if is_organizer:
            for addr in re.findall(r"^ATTENDEE[^\r\n]*:mailto:([^\s]+?)\r?$", text, re.M | re.I):
                other = self.users.get(addr.lower())
                if other is not None and other is not user:
                    copy = re.sub(r"^ATTENDEE;", "ATTENDEE;SCHEDULE-AGENT=CLIENT;", text, flags=re.M)
                    self._store(other, name, copy)
        else:
            org = self.users.get(organizer.group(1).lower())
            mine = re.search(rf"^ATTENDEE[^\r\n]*PARTSTAT=([\w-]+)[^\r\n]*:mailto:{re.escape(user.email)}\r?$", text,
                             re.M | re.I)
            if org is not None and mine:
                for n, (t, _) in list(org.events.items()):
                    if f"UID:{uid}" in t:
                        t = re.sub(rf"^(ATTENDEE[^\r\n]*PARTSTAT=)[\w-]+([^\r\n]*:mailto:{re.escape(user.email)})(?=\r?$)",
                                   rf"\g<1>{mine.group(1)}\g<2>", t, flags=re.M | re.I)
                        self._store(org, n, t)
        return 201, {}, b""  # no ETag on a PUT answer (Zoho)
