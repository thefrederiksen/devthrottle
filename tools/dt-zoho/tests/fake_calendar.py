"""The in-memory Zoho Calendar behind FakeZoho, copying what Zoho answered in the proof of 8 November 2026
(proof\\prover-a\\evidence\\zoho-cal-*.txt and the Zoho Meeting run at 16:37Z):
- answers wrap lists as {"calendars": [...]}, {"events": [...]}, {"share": [...]}; errors as
  {"error": [{"error_code": ..., "description": ...}]};
- an event's times come back as "20261110T100400-0500" with the time zone beside them;
- a change needs the current etag and replaces the event; a repeating event without a time zone is refused
  with TIMEZONE_MISSING;
- invitees outside the organisation are dropped without an error;
- once an occurrence changed or was deleted, the event's own GET lists each changed occurrence (own values and
  etag), each deleted one (a status entry), then the series (zoho-cal-recur.txt);
- `conference: zmeeting` adds conference_data and app_data meeting links;
- an invitee's PATCH answer is refused with 403 OPERATION_NOT_PERMITTED.
"""

from __future__ import annotations

import json
from datetime import datetime, timedelta, timezone
from typing import Dict, List, Optional
from zoneinfo import ZoneInfo

from src.transport import Response

CAL = "https://calendar.zohocloud.ca"
DEFAULT_CAL = "b679fff2063d41cc9c769ad433ae1483"
ORGANISATION = {"soren@devthrottle.com", "email@devthrottle.com"}
ME = "soren@devthrottle.com"


def _ok(data: dict) -> Response:
    return Response(200, json.dumps(data).encode())


def _error(status: int, code: str) -> Response:
    return Response(status, json.dumps({"error": [{"description": code.replace("_", " ").title(),
                                                   "error_code": code, "message": code}]}).encode())


def _to_utc(local: str, zone: str) -> datetime:
    return datetime.strptime(local, "%Y%m%dT%H%M%S").replace(tzinfo=ZoneInfo(zone)).astimezone(timezone.utc)


def _shown(moment: datetime, zone: str) -> str:
    return moment.astimezone(ZoneInfo(zone)).strftime("%Y%m%dT%H%M%S%z")


class FakeCalendar:
    def __init__(self):
        self.calendars: List[dict] = [{"uid": DEFAULT_CAL, "name": "soren", "privilege": "owner", "isdefault": True,
                                       "timezone": "America/Toronto", "color": "#8cbf40", "category": "own"}]
        self.events: Dict[str, Dict[str, dict]] = {DEFAULT_CAL: {}}
        self.share_list: Dict[str, List[dict]] = {}
        self.calls: List[tuple] = []                 # (method, path, query, headers)
        self.counter = 0

    def _next(self) -> int:
        self.counter += 1
        return self.counter

    # ----- the wire

    def handle(self, method: str, path: str, query: dict, headers: Dict[str, str]) -> Response:
        self.calls.append((method, path, query, {k.lower(): v for k, v in headers.items()}))
        p = path.split("/")[3:]            # after /api/v1
        if p == ["calendars"]:
            if method == "GET":
                return _ok({"calendars": self.calendars})
            if method == "POST":
                data = json.loads(query["calendarData"])
                uid = f"cal{self._next():028d}"
                cal = {"uid": uid, "name": data["name"], "privilege": "owner", "isdefault": False,
                       "timezone": data["timezone"], "color": data["color"], "category": "own"}
                self.calendars.append(cal)
                self.events[uid] = {}
                return _ok({"calendars": [cal]})
        cal = next((c for c in self.calendars if len(p) > 1 and c["uid"] == p[1]), None)
        if cal is None:
            return _error(404, "CALENDAR_NOTFOUND")
        if len(p) == 2:
            if method == "PUT":
                cal.update({k: v for k, v in json.loads(query["calendarData"]).items() if k in ("name", "color")})
                return _ok({"calendars": [cal]})
            if method == "DELETE":
                self.calendars.remove(cal)
                del self.events[cal["uid"]]
                return _ok({"calendars": [{"uid": cal["uid"], "calstatus": "deleted"}]})
        if p[2:] == ["share"]:
            listed = self.share_list.setdefault(cal["uid"], [])
            if method == "GET":
                return _ok({"share": listed})
            data = json.loads(query["shareData"])
            if data["mode"] == "add":
                listed.append({"name": data["mailid"].split("@")[0], "emailid": data["mailid"],
                               "privilege": data["privilege"]})
            else:
                listed[:] = [s for s in listed if s["emailid"] != data["mailid"]]
            return _ok({"share": [{"message": "Updated Successfully", "status": "success"}]})
        events = self.events[cal["uid"]]
        if p[2:] == ["events"] and method == "GET":
            found = self._list(events, json.loads(query["range"]), query.get("byinstance") == "true")
            return _ok({"events": found or [{"message": "No events found."}]})
        if p[2:] == ["events"] and method == "POST":
            return self._create(events, json.loads(query["eventdata"]))
        if len(p) == 4 and p[2] == "events":
            event = events.get(p[3])
            if event is None:
                return _error(404, "EVENT_NOTFOUND")
            if method == "GET":
                entries = []
                for rid, over in (event.get("overrides") or {}).items():
                    if over == "deleted":
                        entries.append({"uid": event["uid"], "recurrenceid": rid, "estatus": "deleted"})
                    else:
                        entries.append(dict(self._public(event, None, rid, over), etag=over["etag"]))
                return _ok({"events": entries + [self._public(event)]})
            if method == "PUT":
                return self._change(event, json.loads(query["eventdata"]))
            if method == "DELETE":
                return self._delete(events, event, query, {k.lower(): v for k, v in headers.items()})
            if method == "PATCH":
                return _error(403, "OPERATION_NOT_PERMITTED")
        return _error(404, "URL_RULE_NOT_CONFIGURED")

    # ----- events

    def _create(self, events: Dict[str, dict], data: dict) -> Response:
        when = data["dateandtime"]
        if data.get("isrep") and not when.get("timezone"):
            return _error(400, "TIMEZONE_MISSING")
        uid = f"ev{self._next():030d}@zohocloud.ca"
        event = {"uid": uid, "organizer": ME, "etag": str(1794150000000 + self._next())}
        self._fill(event, data)
        if data.get("conference") == "zmeeting":
            event["conference"] = "zmeeting"
            event["conference_data"] = {"meetingdata": {"meeting_link": f"https://meet.zohocloud.ca/{uid[:8]}",
                                                        "conferenceType": "video"}}
            event["app_data"] = {"meetingdata": {"meetinglink": "https://meeting.zohocloud.ca/meeting/meeting-start"
                                                                f"?key={uid[:8]}"}}
        events[uid] = event
        return _ok({"events": [self._public(event)]})

    def _fill(self, event: dict, data: dict) -> None:
        event["title"] = data.get("title", "")
        event["dateandtime"] = dict(data["dateandtime"])
        event["timezone"] = data["dateandtime"].get("timezone") or "UTC"
        for key in ("location", "description"):
            if data.get(key):
                event[key] = data[key]
            else:
                event.pop(key, None)
        invited = [a["email"] for a in data.get("attendees") or []]
        kept = [a for a in invited if a.lower() in ORGANISATION and a.lower() != ME]
        event["attendees"] = ([{"email": a, "status": "NEEDS-ACTION", "privilege": 1} for a in kept] +
                              [{"email": ME, "status": "ACCEPTED", "privilege": 4}]) if invited else []
        event["reminders"] = [{"minutes": str(r["minutes"]), "action": r["action"]} for r in data.get("reminders") or []]
        if data.get("isrep") and data.get("rrule"):
            # Zoho answers with the rule alone, INTERVAL=1 added, and no isrep.
            parts = data["rrule"].split(";")
            if not any(p.startswith("INTERVAL=") for p in parts):
                parts.insert(1, "INTERVAL=1")
            event["rrule"] = ";".join(parts)
            event.setdefault("overrides", {})

    def _public(self, event: dict, start: Optional[datetime] = None, occurrence: Optional[str] = None,
                over: Optional[dict] = None) -> dict:
        zone = event["timezone"]
        first = _to_utc(event["dateandtime"]["start"], zone)
        length = _to_utc(event["dateandtime"]["end"], zone) - first
        begin = start or first
        shown = {k: v for k, v in event.items() if not k.startswith("_") and k != "overrides"}
        if over:
            shown["title"] = over["title"]
            for key in ("location", "description"):
                if over.get(key):
                    shown[key] = over[key]
                else:
                    shown.pop(key, None)
            begin = _to_utc(over["dateandtime"]["start"], zone)
            length = _to_utc(over["dateandtime"]["end"], zone) - begin
        shown["dateandtime"] = {"timezone": zone, "start": _shown(begin, zone), "end": _shown(begin + length, zone)}
        if occurrence:
            shown["recurrenceid"] = occurrence
        return shown

    def _occurrences(self, event: dict) -> List[tuple]:
        """(occurrence id, start in UTC) for a daily rule with a COUNT, the only kind the proof ran."""
        rule = dict(part.split("=") for part in event["rrule"].split(";"))
        first = _to_utc(event["dateandtime"]["start"], event["timezone"])
        step = {"DAILY": timedelta(days=1), "WEEKLY": timedelta(days=7)}[rule["FREQ"]]
        return [((first + step * i).strftime("%Y%m%dT%H%M%SZ"), first + step * i) for i in range(int(rule["COUNT"]))]

    def _list(self, events: Dict[str, dict], window: dict, by_instance: bool) -> List[dict]:
        lo = datetime.strptime(window["start"], "%Y%m%dT%H%M%SZ").replace(tzinfo=timezone.utc)
        hi = datetime.strptime(window["end"], "%Y%m%dT%H%M%SZ").replace(tzinfo=timezone.utc)
        found = []
        for event in events.values():
            if event.get("rrule"):
                for rid, begin in self._occurrences(event):
                    over = event["overrides"].get(rid)
                    if over == "deleted":
                        continue
                    shown = self._public(event, begin, rid, over if isinstance(over, dict) else None)
                    if isinstance(over, dict):
                        shown["etag"] = over["etag"]
                    moment = datetime.strptime(shown["dateandtime"]["start"], "%Y%m%dT%H%M%S%z")
                    if lo <= moment <= hi:
                        found.append(shown if by_instance else self._public(event))
                        if not by_instance:
                            break
            else:
                moment = _to_utc(event["dateandtime"]["start"], event["timezone"])
                if lo <= moment <= hi:
                    found.append(self._public(event))
        return found

    @staticmethod
    def _expected_etag(event: dict, occurrence: Optional[str]) -> str:
        # A changed occurrence answers to its own etag; the series' draws 409 CONFLICT_EVENT (8 November 2026).
        over = (event.get("overrides") or {}).get(occurrence) if occurrence else None
        return over["etag"] if isinstance(over, dict) else event["etag"]

    def _change(self, event: dict, data: dict) -> Response:
        occurrence = data.get("recurrenceid") if data.get("recurrence_edittype") == "only" else None
        if str(data.get("etag")) != self._expected_etag(event, occurrence):
            return _error(409, "CONFLICT_EVENT")
        old_etag = event["etag"]
        if occurrence:
            # As Zoho did on 8 November 2026: the changed occurrence gets its own etag, the series a new one.
            event["overrides"][occurrence] = {"title": data["title"], "dateandtime": data["dateandtime"],
                                              "location": data.get("location"),
                                              "description": data.get("description"),
                                              "etag": str(int(old_etag) + 1)}
            event["etag"] = str(int(old_etag) + 7)
            return _ok({"events": [{"uid": event["uid"], "etag": event["overrides"][data["recurrenceid"]]["etag"]}]})
        else:
            conference = {k: event[k] for k in ("conference", "conference_data", "app_data") if k in event}
            keep = {k: event[k] for k in ("uid", "organizer", "overrides") if k in event}
            event.clear()
            event.update(keep)
            self._fill(event, data)
            event.update(conference)
        event["etag"] = str(int(old_etag) + 1)
        return _ok({"events": [{"uid": event["uid"], "etag": event["etag"]}]})

    def _delete(self, events: Dict[str, dict], event: dict, query: dict, headers: Dict[str, str]) -> Response:
        if "eventdata" in query:
            data = json.loads(query["eventdata"])
            if str(data.get("etag")) != self._expected_etag(event, data["recurrenceid"]):
                return _error(409, "CONFLICT_EVENT")
            # The series keeps its etag (8 November 2026).
            event["overrides"][data["recurrenceid"]] = "deleted"
            return _ok({"events": [{"uid": event["uid"], "estatus": "deleted"}]})
        if headers.get("etag") != event["etag"]:
            return _error(400, "ETAG_MISMATCH")
        del events[event["uid"]]
        return _ok({"events": [{"uid": event["uid"], "estatus": "deleted"}]})
