"""The small part of iCalendar (RFC 5545) dt-calendar needs: read an event, change some of its properties
and write it back WITHOUT disturbing anything else in it (the server's own properties, alarms, time
zones). Lines are unfolded on read and folded at 75 octets on write; text values are escaped."""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from datetime import date, datetime, timezone
from typing import List, Optional, Tuple
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from .errors import DtCalendarError


@dataclass
class Prop:
    name: str
    params: List[Tuple[str, str]]
    value: str

    def param(self, key: str) -> Optional[str]:
        for k, v in self.params:
            if k.upper() == key.upper():
                return v
        return None

    def set_param(self, key: str, value: str) -> None:
        for i, (k, _) in enumerate(self.params):
            if k.upper() == key.upper():
                self.params[i] = (k, value)
                return
        self.params.append((key, value))


@dataclass
class Component:
    name: str
    props: List[Prop] = field(default_factory=list)
    children: List["Component"] = field(default_factory=list)

    def get(self, name: str) -> Optional[Prop]:
        for p in self.props:
            if p.name == name.upper():
                return p
        return None

    def all(self, name: str) -> List[Prop]:
        return [p for p in self.props if p.name == name.upper()]

    def set(self, name: str, value: str, params: Optional[List[Tuple[str, str]]] = None) -> None:
        """Replace every NAME property with one, kept where the first one was."""
        name = name.upper()
        new = Prop(name, params or [], value)
        for i, p in enumerate(self.props):
            if p.name == name:
                self.props[i] = new
                self.props = [q for j, q in enumerate(self.props) if j == i or q.name != name]
                return
        self.props.append(new)

    def remove(self, name: str) -> None:
        self.props = [p for p in self.props if p.name != name.upper()]


# --------------------------------------------------------------------------------------------- reading


def _unfold(text: str) -> List[str]:
    lines: List[str] = []
    for raw in re.split(r"\r\n|\n|\r", text):
        if raw[:1] in (" ", "\t") and lines:
            lines[-1] += raw[1:]
        elif raw:
            lines.append(raw)
    return lines


def _split_line(line: str) -> Prop:
    """NAME;P1=V1;P2="V;2":VALUE - colons and semicolons inside quotes do not split."""
    parts: List[str] = []
    current = ""
    in_quotes = False
    value = None
    for i, ch in enumerate(line):
        if ch == '"':
            in_quotes = not in_quotes
            current += ch
        elif ch == ";" and not in_quotes:
            parts.append(current)
            current = ""
        elif ch == ":" and not in_quotes:
            parts.append(current)
            value = line[i + 1:]
            break
        else:
            current += ch
    if value is None:
        raise DtCalendarError(f"the server sent an iCalendar line dt-calendar cannot read: {line[:120]!r}")
    params = []
    for p in parts[1:]:
        key, _, val = p.partition("=")
        params.append((key.upper(), val))
    return Prop(parts[0].upper(), params, value)


def parse(text: str) -> Component:
    """The VCALENDAR in `text`."""
    stack: List[Component] = []
    root: Optional[Component] = None
    for line in _unfold(text):
        prop = _split_line(line)
        if prop.name == "BEGIN":
            comp = Component(prop.value.upper())
            if stack:
                stack[-1].children.append(comp)
            else:
                root = comp
            stack.append(comp)
        elif prop.name == "END":
            if not stack or stack[-1].name != prop.value.upper():
                raise DtCalendarError(f"the server sent iCalendar with an unmatched END:{prop.value}.")
            stack.pop()
        elif stack:
            stack[-1].props.append(prop)
    if root is None or root.name != "VCALENDAR" or stack:
        raise DtCalendarError("the server sent something that is not a complete VCALENDAR.")
    return root


def master_event(cal: Component) -> Component:
    """The event itself: the VEVENT without a RECURRENCE-ID (a recurring series' exceptions carry one)."""
    events = [c for c in cal.children if c.name == "VEVENT"]
    masters = [e for e in events if e.get("RECURRENCE-ID") is None]
    if not events:
        raise DtCalendarError("this calendar object holds no event (VEVENT).")
    return masters[0] if masters else events[0]


# --------------------------------------------------------------------------------------------- writing


def _fold(line: str) -> str:
    out: List[str] = []
    data = line.encode("utf-8")
    while len(data) > 75:
        cut = 75 if not out else 74
        while cut > 0 and (data[cut] & 0xC0) == 0x80:  # never split a UTF-8 character
            cut -= 1
        out.append(data[:cut].decode("utf-8"))
        data = data[cut:]
    out.append(data.decode("utf-8"))
    return "\r\n ".join(out)


def _line(prop: Prop) -> str:
    params = "".join(f";{k}={v}" for k, v in prop.params)
    return _fold(f"{prop.name}{params}:{prop.value}")


def serialize(comp: Component) -> str:
    lines = [f"BEGIN:{comp.name}"] + [_line(p) for p in comp.props]
    body = "\r\n".join(lines)
    for child in comp.children:
        body += "\r\n" + serialize(child).rstrip("\r\n")
    return body + f"\r\nEND:{comp.name}\r\n"


def escape_text(text: str) -> str:
    return text.replace("\\", "\\\\").replace(";", "\\;").replace(",", "\\,").replace("\r\n", "\n").replace("\n", "\\n")


def unescape_text(text: str) -> str:
    return re.sub(r"\\([\\;,nN])", lambda m: "\n" if m.group(1) in "nN" else m.group(1), text)


# --------------------------------------------------------------------------------------------- values


def utc_stamp(moment: datetime) -> str:
    return moment.astimezone(timezone.utc).strftime("%Y%m%dT%H%M%SZ")


def date_stamp(day: date) -> str:
    return day.strftime("%Y%m%d")


def zone_of(prop: Optional[Prop]) -> Optional[ZoneInfo]:
    """The time zone named by a DTSTART's TZID, when this machine knows it; None for UTC, floating or unknown."""
    tzid = prop.param("TZID") if prop is not None else None
    if not tzid:
        return None
    try:
        return ZoneInfo(tzid.strip('"'))
    except (ZoneInfoNotFoundError, ValueError):
        return None


def time_value(prop: Optional[Prop], owner: str = "this event") -> Tuple[Optional[str], Optional[datetime]]:
    """(text to show, the moment in UTC for sorting) of a DTSTART or DTEND.

    UTC times are shown in this machine's time zone; a time with a TZID in that zone; an all-day date as
    the date. A TZID this machine does not know is shown as written, with its name, never guessed. A value
    in any other form is an error naming `owner`, never a guess."""
    if prop is None:
        return None, None
    value = prop.value.strip()
    if not re.fullmatch(r"\d{8}(T\d{6}Z?)?", value):
        raise DtCalendarError(f"{owner}: {prop.name} value '{value}' is not a date or time form dt-calendar reads.")
    try:
        if prop.param("VALUE") == "DATE" or len(value) == 8:
            day = datetime.strptime(value[:8], "%Y%m%d")
            return day.date().isoformat(), day.replace(tzinfo=timezone.utc)
        if value.endswith("Z"):
            moment = datetime.strptime(value, "%Y%m%dT%H%M%SZ").replace(tzinfo=timezone.utc)
            return moment.astimezone().isoformat(timespec="minutes"), moment
        naive = datetime.strptime(value, "%Y%m%dT%H%M%S")
    except ValueError as exc:
        raise DtCalendarError(f"{owner}: {prop.name} value '{value}' is not a real date or time ({exc}).") from exc
    tzid = prop.param("TZID")
    if tzid:
        zone = zone_of(prop)
        if zone is None:
            return f"{naive.isoformat(timespec='minutes')} ({tzid.strip(chr(34))})", None
        moment = naive.replace(tzinfo=zone)
        return moment.isoformat(timespec="minutes"), moment.astimezone(timezone.utc)
    return naive.isoformat(timespec="minutes"), naive.astimezone(timezone.utc)


def local_stamp(moment: datetime, zone: ZoneInfo) -> str:
    """The wall time in `zone`, written for a property that carries TZID."""
    return moment.astimezone(zone).strftime("%Y%m%dT%H%M%S")


def address(value: str) -> str:
    """mailto:x@y -> x@y (any case of MAILTO)."""
    return re.sub(r"^mailto:", "", value.strip(), flags=re.I)


def attendees(event: Component) -> List[dict]:
    return [{"address": address(p.value), "status": (p.param("PARTSTAT") or "NEEDS-ACTION").upper(),
             "name": (p.param("CN") or "").strip('"') or None} for p in event.all("ATTENDEE")]


def attendee_prop(addr: str) -> Prop:
    return Prop("ATTENDEE", [("CUTYPE", "INDIVIDUAL"), ("ROLE", "REQ-PARTICIPANT"), ("PARTSTAT", "NEEDS-ACTION"),
                             ("RSVP", "TRUE")], f"mailto:{addr}")


def new_calendar(uid: str, summary: str, start: str, end: str, all_day: bool, location: Optional[str],
                 description: Optional[str], organizer: Optional[str], invitees: List[str]) -> Component:
    event = Component("VEVENT")
    event.set("UID", uid)
    event.set("DTSTAMP", utc_stamp(datetime.now(timezone.utc)))
    event.set("DTSTART", start, [("VALUE", "DATE")] if all_day else None)
    event.set("DTEND", end, [("VALUE", "DATE")] if all_day else None)
    event.set("SUMMARY", escape_text(summary))
    event.set("SEQUENCE", "0")
    if location:
        event.set("LOCATION", escape_text(location))
    if description:
        event.set("DESCRIPTION", escape_text(description))
    if invitees:
        event.set("ORGANIZER", f"mailto:{organizer}")
        for addr in invitees:
            event.props.append(attendee_prop(addr))
    cal = Component("VCALENDAR", [Prop("VERSION", [], "2.0"), Prop("PRODID", [], "-//DevThrottle//dt-calendar//EN"),
                                  Prop("CALSCALE", [], "GREGORIAN")], [event])
    return cal
