"""Zoho Calendar through Zoho's own API: calendars, sharing, events, invitations, reminders, repeats, Zoho Meeting.

Every call here was run against soren@devthrottle.com on 8 November 2026 (proof\\prover-a.md, the dt-zoho
calendar rows and section 3 of "the exact call for every cell"):
- an event is sent as the `eventdata` query parameter, URL-encoded JSON, on POST (create) and PUT (change);
- a change REPLACES the whole event and needs its `etag`, so every field to keep is sent again;
- a delete sends the `etag` as a header; one occurrence of a series is changed or deleted with
  `recurrenceid` and `recurrence_edittype: only`;
- a repeating event needs `dateandtime.timezone`, or Zoho answers TIMEZONE_MISSING;
- Zoho takes `isrep` with `rrule` on a create but answers with `rrule` alone (no `isrep`), the rule rewritten
  with INTERVAL=1 added; `rrule` is what marks a repeating event in an answer (run 8 November 2026);
- once an occurrence changed or was deleted, the event's own GET answers several entries: each changed
  occurrence (its `recurrenceid`, its own values and etag), each deleted one (a status entry with
  `recurrenceid` and `estatus: deleted`), and the series itself (no `recurrenceid`), whose etag a series change
  or delete needs - taking the first entry's etag is refused with 409 CONFLICT_EVENT, and so is the series' etag
  on a second change to an already-changed occurrence, which answers only to its own (run 8 November 2026;
  proof evidence zoho-cal-recur.txt); deleting an occurrence leaves the series' etag as it was;
- a change that leaves out the location, the description or the attendees removes them (run 8 November 2026:
  each was gone on the read-back, and a removed invitee is mailed a "Cancelled" notice), while one that leaves
  out the meeting keeps it; an invitee's answer (ACCEPTED) survives a change;
- Zoho keeps only invitees inside its organisation and drops the others without an error, so dt-zoho reads
  the event back and names who was dropped;
- `conference: zmeeting` creates a Zoho Meeting: `conference_data.meetingdata.meeting_link` is the guest join
  link and `app_data.meetingdata.meetinglink` the host's start link (run 8 November 2026, 16:37Z); the event's
  GET carries both, and a change that does not mention the meeting keeps the same meeting and links (run 8
  November 2026: title and time changed, both links unchanged);
- an invitee cannot answer an invitation through the API (403 OPERATION_NOT_PERMITTED); CalDAV can.
- the events list answers at most 31 days per call, and an empty range as {"events": [{"message": "No events
  found."}]} - an entry with no uid is that message, not an event.
"""

from __future__ import annotations

import json
from datetime import date, datetime, timedelta, timezone
from typing import Any, Dict, List, Optional
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from .client import ZohoClient
from .errors import DtZohoError

INPUT_FORMAT = "%Y-%m-%d %H:%M"
ZOHO_LOCAL = "%Y%m%dT%H%M%S"
ZOHO_UTC = "%Y%m%dT%H%M%SZ"
MAX_DAYS_PER_CALL = 31
REMINDER_ACTIONS = ("email", "popup", "notification")
RESPOND_REFUSAL = ("Zoho's API cannot answer an invitation: it answers 403 OPERATION_NOT_PERMITTED to the "
                   "invitee. Answer it over CalDAV instead: dt-calendar respond <id> --as accepted|declined|tentative")


# --------------------------------------------------------------------------------------------- calendars


def calendars(client: ZohoClient) -> List[dict]:
    return client.calendar("GET", "/api/v1/calendars", query={"category": "all"}).get("calendars") or []


def calendar_record(raw: dict) -> dict:
    return {"uid": raw.get("uid"), "name": raw.get("name"), "privilege": raw.get("privilege"),
            "default": bool(raw.get("isdefault")), "time_zone": raw.get("timezone"), "color": raw.get("color"),
            "category": raw.get("category")}


def resolve_calendar(client: ZohoClient, calendar: Optional[str]) -> dict:
    """A calendar by uid or name; without one, the default calendar the account owns."""
    records = [calendar_record(c) for c in calendars(client)]
    if calendar is None:
        owned = [r for r in records if r["default"] and r["privilege"] == "owner"]
        if len(owned) == 1:
            return owned[0]
        raise DtZohoError("no single default calendar; pass --calendar <name or uid> (dt-zoho calendars).",
                          exit_code=2)
    for record in records:
        if calendar == record["uid"]:
            return record
    by_name = [r for r in records if (r["name"] or "").lower() == calendar.strip().lower()]
    if len(by_name) == 1:
        return by_name[0]
    if len(by_name) > 1:
        raise DtZohoError(f"several calendars are named '{calendar}'; pass its uid (dt-zoho calendars).", exit_code=2)
    raise DtZohoError(f"no calendar '{calendar}'. See them with: dt-zoho calendars", exit_code=2)


def create_calendar(client: ZohoClient, name: str, color: str, time_zone: str, description: str) -> dict:
    data = {"name": name, "color": color, "timezone": time_zone, "include_infreebusy": True, "description": description}
    answer = client.calendar("POST", "/api/v1/calendars", query={"calendarData": json.dumps(data)})
    found = answer.get("calendars") or []
    if not found:
        raise DtZohoError("Zoho answered the new calendar with no calendar in its answer; nothing to report back.")
    return found[0]


def update_calendar(client: ZohoClient, uid: str, name: str, color: str) -> None:
    client.calendar("PUT", f"/api/v1/calendars/{uid}", query={"calendarData": json.dumps({"name": name, "color": color})})


def delete_calendar(client: ZohoClient, uid: str) -> None:
    client.calendar("DELETE", f"/api/v1/calendars/{uid}")


def shares(client: ZohoClient, uid: str) -> List[dict]:
    found = client.calendar("GET", f"/api/v1/calendars/{uid}/share").get("share") or []
    return [{"email": s.get("emailid"), "name": s.get("name"), "privilege": s.get("privilege")} for s in found]


def share(client: ZohoClient, uid: str, email: str, add: bool) -> None:
    data: Dict[str, Any] = {"type": "user", "mailid": email, "mode": "add" if add else "delete"}
    if add:
        data["privilege"] = "view"
    client.calendar("PUT", f"/api/v1/calendars/{uid}/share", query={"shareData": json.dumps(data)})


# --------------------------------------------------------------------------------------------- times


def check_time_zone(name: str) -> str:
    try:
        ZoneInfo(name)
    except (ZoneInfoNotFoundError, ValueError) as exc:
        raise DtZohoError(f"'{name}' is not a time zone name; use one such as America/Toronto.", exit_code=2) from exc
    return name


def parse_local(value: str, option: str) -> datetime:
    try:
        return datetime.strptime(value.strip(), INPUT_FORMAT)
    except ValueError as exc:
        raise DtZohoError(f"{option} must be a date and time as YYYY-MM-DD HH:MM, not '{value}'.", exit_code=2) from exc


def parse_day(value: str, option: str) -> date:
    try:
        return datetime.strptime(value.strip(), "%Y-%m-%d").date()
    except ValueError as exc:
        raise DtZohoError(f"{option} must be a day as YYYY-MM-DD, not '{value}'.", exit_code=2) from exc


def zoho_time(value: Optional[str]) -> Optional[str]:
    """Zoho's '20261110T100400-0500' (or a UTC '...Z', or a date) as ISO 8601."""
    if not value:
        return value
    for fmt in ("%Y%m%dT%H%M%S%z", ZOHO_UTC, "%Y%m%d"):
        try:
            parsed = datetime.strptime(value, fmt)
        except ValueError:
            continue
        if fmt == ZOHO_UTC:
            parsed = parsed.replace(tzinfo=timezone.utc)
        return parsed.date().isoformat() if fmt == "%Y%m%d" else parsed.isoformat()
    return value


def ranges(first: date, last: date, time_zone: str) -> List[dict]:
    """Zoho's range parameter for every window of at most 31 days covering first to last, both included."""
    zone = ZoneInfo(time_zone)
    windows = []
    start = first
    while start <= last:
        end = min(start + timedelta(days=MAX_DAYS_PER_CALL - 1), last)
        begin = datetime.combine(start, datetime.min.time(), zone).astimezone(timezone.utc)
        finish = datetime.combine(end + timedelta(days=1), datetime.min.time(), zone).astimezone(timezone.utc)
        windows.append({"start": begin.strftime(ZOHO_UTC), "end": (finish - timedelta(seconds=1)).strftime(ZOHO_UTC)})
        start = end + timedelta(days=1)
    return windows


# --------------------------------------------------------------------------------------------- events


def list_events(client: ZohoClient, cal_uid: str, first: date, last: date, time_zone: str,
                instances: bool) -> List[dict]:
    found: List[dict] = []
    seen = set()
    for window in ranges(first, last, time_zone):
        query = {"range": json.dumps(window)}
        if instances:
            query["byinstance"] = "true"
        for event in client.calendar("GET", f"/api/v1/calendars/{cal_uid}/events", query=query).get("events") or []:
            if not event.get("uid"):
                continue        # Zoho's "No events found." message
            key = (event.get("uid"), event.get("recurrenceid"), (event.get("dateandtime") or {}).get("start"))
            if key not in seen:
                seen.add(key)
                found.append(event)
    return found


def event(client: ZohoClient, cal_uid: str, uid: str) -> dict:
    """The event itself: for a series whose occurrences changed, the series entry, never an occurrence's.
    The changed occurrences' own entries ride along under `_changed`, keyed by occurrence id, and the deleted
    occurrences' ids under `_deleted`."""
    found = client.calendar("GET", f"/api/v1/calendars/{cal_uid}/events/{uid}").get("events") or []
    series = [e for e in found if not e.get("recurrenceid")]
    if not series:
        raise DtZohoError(f"Zoho has no event {uid} in this calendar.", exit_code=2)
    deleted = [e["recurrenceid"] for e in found if e.get("recurrenceid") and e.get("estatus") == "deleted"]
    changed = {e["recurrenceid"]: e for e in found if e.get("recurrenceid") and e.get("estatus") != "deleted"}
    return dict(series[0], _changed=changed, _deleted=deleted)


# The two links, labelled for what they do. Only the host link starts the meeting - Zoho Calendar's own start
# button left guests "waiting for the host" (the owner, 8 November 2026) - so it must never reach a guest.
JOIN_KEY, JOIN_LABEL = "join_link_for_guests", "join link for guests"
START_KEY, START_LABEL = "start_link_owner_only", "start the meeting (owner only)"
START_LINK_MARK = "/meeting/meeting-start"


def meeting_links(raw: dict) -> dict:
    guest = ((raw.get("conference_data") or {}).get("meetingdata") or {}).get("meeting_link")
    host = ((raw.get("app_data") or {}).get("meetingdata") or {}).get("meetinglink")
    return {JOIN_KEY: guest, START_KEY: host}


def check_no_start_link(texts: dict, host_link: Optional[str] = None) -> None:
    """Refuse text bound for the invitation that carries a meeting's start link: whoever has it starts the
    meeting as its owner."""
    for option, text in texts.items():
        if text and (START_LINK_MARK in text or (host_link and host_link in text)):
            raise DtZohoError(f"{option} carries a Zoho Meeting start link, which starts the meeting as its owner and "
                              "would reach every invitee. Give guests the join link instead. If the event already "
                              f"holds it, drop it with {option} \"\" or {option} \"<the text without it>\". "
                              "Nothing was sent.", exit_code=2)


def event_record(raw: dict) -> dict:
    when = raw.get("dateandtime") or {}
    record = {"uid": raw.get("uid"), "title": raw.get("title"), "start": zoho_time(when.get("start")),
              "end": zoho_time(when.get("end")), "time_zone": when.get("timezone"),
              "location": raw.get("location"), "description": raw.get("description"),
              "organizer": raw.get("organizer"), "repeats": raw.get("rrule") or None,
              "occurrence": raw.get("recurrenceid"), "all_day": bool(raw.get("isallday")),
              "attendees": [{"email": a.get("email"), "status": a.get("status")} for a in raw.get("attendees") or []],
              "reminders": [{"action": r.get("action"), "minutes_before": -int(r.get("minutes"))}
                            for r in raw.get("reminders") or [] if r.get("minutes") not in (None, "")],
              "etag": str(raw["etag"]) if raw.get("etag") is not None else None, "link": raw.get("viewEventURL")}
    if raw.get("_changed"):
        record["changed_occurrences"] = sorted(raw["_changed"])
    if raw.get("_deleted"):
        record["deleted_occurrences"] = sorted(raw["_deleted"])
    record.update(meeting_links(raw))
    return record


def parse_reminders(values: Optional[List[str]]) -> List[dict]:
    """--reminder email:30 (30 minutes before) as Zoho's {"action": "email", "minutes": -30}."""
    found = []
    for value in values or []:
        action, _, minutes = value.partition(":")
        if action not in REMINDER_ACTIONS or not minutes.isdigit():
            raise DtZohoError(f"--reminder must be ACTION:MINUTES with ACTION one of {', '.join(REMINDER_ACTIONS)}, "
                              f"for example email:30, not '{value}'.", exit_code=2)
        found.append({"action": action, "minutes": -int(minutes)})
    return found


def event_data(title: str, start: datetime, end: datetime, time_zone: str, location: Optional[str],
               description: Optional[str], attendees: List[str], reminders: Optional[List[dict]],
               repeat: Optional[str], meeting: bool) -> dict:
    data: Dict[str, Any] = {"title": title, "dateandtime": {"start": start.strftime(ZOHO_LOCAL),
                                                            "end": end.strftime(ZOHO_LOCAL), "timezone": time_zone}}
    if location:
        data["location"] = location
    if description:
        data["description"] = description
    if attendees:
        data["attendees"] = [{"email": a, "permission": 1, "attendance": 1} for a in attendees]
        data["notify_attendee"] = 1
    if reminders is not None:
        data["reminders"] = reminders
    if repeat:
        data["isrep"] = True
        data["rrule"] = repeat
    if meeting:
        data["conference"] = "zmeeting"
    return data


def create_event(client: ZohoClient, cal_uid: str, data: dict) -> dict:
    answer = client.calendar("POST", f"/api/v1/calendars/{cal_uid}/events", query={"eventdata": json.dumps(data)})
    found = answer.get("events") or []
    if not found:
        raise DtZohoError("Zoho answered the new event with no event in its answer.")
    return found[0]


def change_event(client: ZohoClient, cal_uid: str, uid: str, data: dict) -> dict:
    answer = client.calendar("PUT", f"/api/v1/calendars/{cal_uid}/events/{uid}", query={"eventdata": json.dumps(data)})
    found = answer.get("events") or []
    return found[0] if found else {}


def delete_event(client: ZohoClient, cal_uid: str, uid: str, etag: str) -> None:
    client.calendar("DELETE", f"/api/v1/calendars/{cal_uid}/events/{uid}", headers={"etag": etag})


def delete_occurrence(client: ZohoClient, cal_uid: str, uid: str, etag: str, occurrence: str) -> None:
    data = {"uid": uid, "etag": etag, "recurrenceid": occurrence, "recurrence_edittype": "only"}
    client.calendar("DELETE", f"/api/v1/calendars/{cal_uid}/events/{uid}", query={"eventdata": json.dumps(data)})


def dropped_invitees(invited: List[str], stored: dict) -> List[str]:
    kept = {(a.get("email") or "").lower() for a in stored.get("attendees") or []}
    return [a for a in invited if a.lower() not in kept]
