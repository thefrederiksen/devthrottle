"""The calendar commands of dt-zoho: calendars and sharing, events, invitations, reminders, repeats and Zoho Meeting.
They register on the one Typer app in cli.py, which imports this module at its end."""

from __future__ import annotations

from datetime import date, datetime, timedelta, timezone
from typing import List, Optional
from zoneinfo import ZoneInfo

import typer

from . import config, output, write, zcalendar
from .cli import FIELDS_WITH_JSON, JsonOpt, _client, _command, _report, app
from .errors import DtZohoError

calendar_app = typer.Typer(no_args_is_help=True, help="Create, rename, delete and share calendars.")
event_app = typer.Typer(no_args_is_help=True, help="List, read, create, change and delete events, with invitations, "
                                                   "reminders, repeats and Zoho Meeting.")
app.add_typer(calendar_app, name="calendar")
app.add_typer(event_app, name="event")

CalendarOpt = typer.Option(None, "--calendar", "-c", help="The calendar: name or uid (dt-zoho calendars). Default: "
                                                          "your default calendar.")
CalendarArg = typer.Argument(..., help="The calendar: name or uid (dt-zoho calendars).")
EventArg = typer.Argument(..., help="The event uid (dt-zoho event list).")
EVENT_FIELDS = ("uid", "start", "end", "title", "time_zone", "location", "organizer", "repeats", "occurrence",
                "attendees", zcalendar.JOIN_KEY)
EVENT_DEFAULT = ("uid", "start", "end", "title")
DEFAULT_MINUTES = 60


def _fields(fields: Optional[str], json_output: bool) -> List[str]:
    from cc_shared.axi_output import FieldsError, parse_fields

    if fields is not None and json_output:
        raise DtZohoError(FIELDS_WITH_JSON, 2)
    try:
        return parse_fields(fields, EVENT_FIELDS, EVENT_DEFAULT)
    except FieldsError as exc:
        raise DtZohoError(str(exc), 2) from exc


def _wall_time(zoho_value: str, time_zone: str) -> datetime:
    """Zoho's '20261110T100400-0500' or a UTC '...Z' as the wall-clock time in `time_zone`."""
    for fmt in ("%Y%m%dT%H%M%S%z", zcalendar.ZOHO_UTC):
        try:
            parsed = datetime.strptime(zoho_value, fmt)
        except ValueError:
            continue
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=timezone.utc)
        return parsed.astimezone(ZoneInfo(time_zone)).replace(tzinfo=None)
    raise DtZohoError(f"Zoho gave the time '{zoho_value}', which dt-zoho cannot read.")


def _times(start: Optional[str], end: Optional[str], duration: Optional[int],
           current: Optional[tuple] = None) -> tuple:
    """(start, end) from the options, keeping the current length when only the start moves."""
    if end is not None and duration is not None:
        raise DtZohoError("pass --end or --duration, not both.", exit_code=2)
    old_start, old_end = current if current else (None, None)
    new_start = zcalendar.parse_local(start, "--start") if start is not None else old_start
    if end is not None:
        new_end = zcalendar.parse_local(end, "--end")
    elif duration is not None:
        new_end = new_start + timedelta(minutes=duration)
    elif current:
        new_end = new_start + (old_end - old_start)
    else:
        new_end = new_start + timedelta(minutes=DEFAULT_MINUTES)
    if new_end <= new_start:
        raise DtZohoError("the event must end after it starts.", exit_code=2)
    return new_start, new_end


def _print_event(record: dict, json_output: bool) -> None:
    if json_output:
        output.emit_json(record)
        return
    shown = dict(record)
    shown["attendees"] = ", ".join(f"{a['email']} ({a['status']})" for a in record["attendees"]) or None
    shown["reminders"] = ", ".join(f"{r['action']} {r['minutes_before']} minutes before"
                                   for r in record["reminders"]) or None
    for key, label in ((zcalendar.JOIN_KEY, zcalendar.JOIN_LABEL), (zcalendar.START_KEY, zcalendar.START_LABEL)):
        if key in shown:
            shown[label] = shown.pop(key)
    _report({k: v for k, v in shown.items() if v not in (None, "", [])}, False)


def _check_dropped(invited: List[str], stored: dict, uid: str, cal: dict) -> None:
    dropped = zcalendar.dropped_invitees(invited, stored)
    if dropped:
        raise DtZohoError(f"Zoho dropped {', '.join(dropped)} from the event without saying so, so they got no "
                          "invitation: it keeps only invitees inside its organisation, and drops the organizer's own "
                          f"aliases. The event {uid} was kept with the "
                          f"others. Delete it with: dt-zoho event delete {uid} --calendar {cal['uid']}")


# --------------------------------------------------------------------------------------------- calendars


@app.command("calendars")
@_command
def calendars_command(json_output: bool = JsonOpt) -> None:
    """List the calendars: your own and those shared with you."""
    records = [zcalendar.calendar_record(c) for c in zcalendar.calendars(_client())]
    if json_output:
        output.emit_json({"count": len(records), "calendars": records})
        return
    output.emit_list("calendars", ["uid", "name", "privilege", "default", "time_zone"], records, None,
                     ['dt-zoho event list --calendar "<name>"'] if records else [])


def _color(color: str) -> str:
    hex_part = color[1:] if color.startswith("#") else ""
    if len(hex_part) != 6 or any(c not in "0123456789abcdefABCDEF" for c in hex_part):
        raise DtZohoError(f"--color must be #RRGGBB, not '{color}'.", exit_code=2)
    return color


@calendar_app.command("create")
@_command
def calendar_create(name: str = typer.Argument(..., help="The new calendar's name."),
                    color: str = typer.Option("#8A2BE2", "--color", help="Its colour, as #RRGGBB."),
                    time_zone: Optional[str] = typer.Option(None, "--time-zone", help="Its time zone. Default: your "
                                                                                      "default calendar's."),
                    description: str = typer.Option("", "--description"),
                    json_output: bool = JsonOpt) -> None:
    """Create a calendar."""
    _color(color)
    if time_zone is not None:
        zcalendar.check_time_zone(time_zone)
    client = _client()
    zone = time_zone or zcalendar.resolve_calendar(client, None)["time_zone"]
    created = zcalendar.calendar_record(zcalendar.create_calendar(client, name, color, zone, description))
    _report(dict(created, created=True), json_output)


@calendar_app.command("rename")
@_command
def calendar_rename(calendar: str = CalendarArg, name: Optional[str] = typer.Option(None, "--name", help="New name."),
                    color: Optional[str] = typer.Option(None, "--color", help="New colour, as #RRGGBB."),
                    json_output: bool = JsonOpt) -> None:
    """Rename a calendar or change its colour."""
    if name is None and color is None:
        raise DtZohoError("pass --name, --color or both.", exit_code=2)
    if color is not None:
        _color(color)
    client = _client()
    cal = zcalendar.resolve_calendar(client, calendar)
    new_name, new_color = name or cal["name"], color or cal["color"]
    zcalendar.update_calendar(client, cal["uid"], new_name, new_color)
    _report({"uid": cal["uid"], "name": new_name, "color": new_color}, json_output)


@calendar_app.command("delete")
@_command
def calendar_delete(calendar: str = CalendarArg, json_output: bool = JsonOpt) -> None:
    """Delete a calendar and every event in it. Your default calendar is refused."""
    client = _client()
    cal = zcalendar.resolve_calendar(client, calendar)
    if cal["default"]:
        raise DtZohoError(f"{cal['name']} is your default calendar; dt-zoho does not delete it.", exit_code=2)
    zcalendar.delete_calendar(client, cal["uid"])
    _report({"deleted": True, "uid": cal["uid"], "name": cal["name"]}, json_output)


@calendar_app.command("shares")
@_command
def calendar_shares(calendar: str = CalendarArg, json_output: bool = JsonOpt) -> None:
    """List who a calendar is shared with."""
    client = _client()
    cal = zcalendar.resolve_calendar(client, calendar)
    records = zcalendar.shares(client, cal["uid"])
    if json_output:
        output.emit_json({"count": len(records), "calendar": cal["name"], "shares": records})
        return
    output.emit_list("shares", ["email", "name", "privilege"], records, None)


@calendar_app.command("share")
@_command
def calendar_share(calendar: str = CalendarArg,
                   with_: str = typer.Option(..., "--with", help="The person to share it with, who may view it."),
                   json_output: bool = JsonOpt) -> None:
    """Share a calendar for viewing. (Other levels were not proven, so view is the one offered.)"""
    email = config.normalise_address(with_)
    config.check_recipients(config.load(), [email])
    client = _client()
    cal = zcalendar.resolve_calendar(client, calendar)
    zcalendar.share(client, cal["uid"], email, add=True)
    _report({"calendar": cal["name"], "shared_with": email, "privilege": "view"}, json_output)


@calendar_app.command("unshare")
@_command
def calendar_unshare(calendar: str = CalendarArg,
                     with_: str = typer.Option(..., "--with", help="The person to stop sharing it with."),
                     json_output: bool = JsonOpt) -> None:
    """Stop sharing a calendar with someone."""
    email = config.normalise_address(with_)
    client = _client()
    cal = zcalendar.resolve_calendar(client, calendar)
    zcalendar.share(client, cal["uid"], email, add=False)
    _report({"calendar": cal["name"], "unshared": email}, json_output)


# --------------------------------------------------------------------------------------------- reading events


@event_app.command("list")
@_command
def event_list(calendar: Optional[str] = CalendarOpt,
               from_: Optional[str] = typer.Option(None, "--from", help="First day, YYYY-MM-DD. Default: today."),
               to: Optional[str] = typer.Option(None, "--to", help="Last day, included. Default: 30 days after --from."),
               instances: bool = typer.Option(False, "--instances", help="List each occurrence of a repeating event "
                                                                         "on its own, with its occurrence id."),
               fields: Optional[str] = typer.Option(None, "--fields", help="Fields: " + ",".join(EVENT_FIELDS) + "."),
               json_output: bool = JsonOpt) -> None:
    """List events, earliest first. Zoho answers 31 days per call; a longer span is asked for in parts."""
    chosen = _fields(fields, json_output)
    first = zcalendar.parse_day(from_, "--from") if from_ else date.today()
    last = zcalendar.parse_day(to, "--to") if to else first + timedelta(days=30)
    if last < first:
        raise DtZohoError("--to is before --from.", exit_code=2)
    client = _client()
    cal = zcalendar.resolve_calendar(client, calendar)
    records = [zcalendar.event_record(e) for e in
               zcalendar.list_events(client, cal["uid"], first, last, cal["time_zone"], instances)]
    records.sort(key=lambda r: r["start"] or "")
    if json_output:
        output.emit_json({"count": len(records), "calendar": cal["name"], "from": first.isoformat(),
                          "to": last.isoformat(), "events": records})
        return
    rows = [dict(r, attendees=len(r["attendees"])) for r in records]
    output.emit_list("events", chosen, rows, None, ["dt-zoho event show <uid>"] if records else [])


@event_app.command("show")
@_command
def event_show(uid: str = EventArg, calendar: Optional[str] = CalendarOpt, json_output: bool = JsonOpt) -> None:
    """One event: its times, invitees and their answers, reminders, repeat rule and meeting links."""
    client = _client()
    cal = zcalendar.resolve_calendar(client, calendar)
    _print_event(zcalendar.event_record(zcalendar.event(client, cal["uid"], uid)), json_output)


# --------------------------------------------------------------------------------------------- writing events


InviteOpt = typer.Option(None, "--invite", help="Invite this person (repeat, or comma separated). Zoho keeps only "
                                                "people inside its organisation.")
ReminderOpt = typer.Option(None, "--reminder", help="A reminder, ACTION:MINUTES before: email:30, popup:10 or "
                                                    "notification:5 (repeat).")


@event_app.command("create")
@_command
def event_create(title: str = typer.Option(..., "--title", "-t"),
                 start: str = typer.Option(..., "--start", help="YYYY-MM-DD HH:MM in --time-zone."),
                 end: Optional[str] = typer.Option(None, "--end", help="YYYY-MM-DD HH:MM."),
                 duration: Optional[int] = typer.Option(None, "--duration", min=1, help="Minutes. Default 60."),
                 time_zone: Optional[str] = typer.Option(None, "--time-zone", help="Default: the calendar's."),
                 location: Optional[str] = typer.Option(None, "--location"),
                 description: Optional[str] = typer.Option(None, "--description"),
                 invite: Optional[List[str]] = InviteOpt, reminder: Optional[List[str]] = ReminderOpt,
                 repeat: Optional[str] = typer.Option(None, "--repeat", help="An iCalendar rule, for example "
                                                                             "FREQ=DAILY;COUNT=3 or "
                                                                             "FREQ=WEEKLY;BYDAY=MO;COUNT=10."),
                 meeting: bool = typer.Option(False, "--meeting", help="Add a Zoho Meeting: prints the join link "
                                                                       "for guests and the start link (owner "
                                                                       "only; only it starts the meeting)."),
                 calendar: Optional[str] = CalendarOpt, json_output: bool = JsonOpt) -> None:
    """Create an event, optionally inviting people, with reminders, a repeat rule or a Zoho Meeting."""
    begin, finish = _times(start, end, duration)
    invitees = write.split_addresses(invite)
    reminders = zcalendar.parse_reminders(reminder) if reminder else None
    if time_zone is not None:
        zcalendar.check_time_zone(time_zone)
    if repeat is not None and "FREQ=" not in repeat.upper():
        raise DtZohoError(f"--repeat must be an iCalendar rule with FREQ=, such as FREQ=DAILY;COUNT=3, not '{repeat}'.",
                          exit_code=2)
    zcalendar.check_no_start_link({"--title": title, "--location": location, "--description": description})
    config.check_recipients(config.load(), invitees)
    client = _client()
    cal = zcalendar.resolve_calendar(client, calendar)
    data = zcalendar.event_data(title, begin, finish, time_zone or cal["time_zone"], location, description, invitees,
                                reminders, repeat.upper() if repeat else None, meeting)
    created = zcalendar.create_event(client, cal["uid"], data)
    stored = zcalendar.event(client, cal["uid"], created["uid"]) if invitees else created
    record = zcalendar.event_record(stored)
    record["calendar"] = cal["name"]
    _print_event(record, json_output)
    if meeting and not (record[zcalendar.JOIN_KEY] and record[zcalendar.START_KEY]):
        raise DtZohoError(f"Zoho created the event {created['uid']} but gave no meeting link with it.")
    _check_dropped(invitees, stored, created["uid"], cal)


def _invitees(current: dict) -> List[str]:
    """The event's invitees, without its organizer."""
    organizer = (current.get("organizer") or "").lower()
    return [a["email"] for a in current.get("attendees") or [] if a.get("email") and a["email"].lower() != organizer]


def _need_etag(uid: str, current: dict) -> str:
    if current.get("etag") is None:
        raise DtZohoError(f"Zoho answered event {uid} without its etag, which a change or delete needs. Nothing "
                          "was changed.")
    return str(current["etag"])


@event_app.command("change")
@_command
def event_change(uid: str = EventArg,
                 occurrence: Optional[str] = typer.Option(None, "--occurrence", help="Change only this occurrence of "
                                                          "a repeating event (its id from event list --instances)."),
                 title: Optional[str] = typer.Option(None, "--title", "-t"),
                 start: Optional[str] = typer.Option(None, "--start", help="New start, YYYY-MM-DD HH:MM in the "
                                                                           "event's own time zone (event show); the "
                                                                           "length is kept unless --end or --duration "
                                                                           "is given."),
                 end: Optional[str] = typer.Option(None, "--end"),
                 duration: Optional[int] = typer.Option(None, "--duration", min=1, help="Minutes."),
                 location: Optional[str] = typer.Option(None, "--location", help="New location ('' removes it)."),
                 description: Optional[str] = typer.Option(None, "--description", help="New description ('' removes "
                                                                                       "it)."),
                 invite: Optional[List[str]] = InviteOpt,
                 uninvite: Optional[List[str]] = typer.Option(None, "--uninvite", help="Remove this invitee (repeat)."),
                 reminder: Optional[List[str]] = ReminderOpt,
                 no_reminders: bool = typer.Option(False, "--no-reminders", help="Remove every reminder."),
                 calendar: Optional[str] = CalendarOpt, json_output: bool = JsonOpt) -> None:
    """Change an event. Zoho replaces the whole event, so dt-zoho sends every field again with your changes."""
    if reminder and no_reminders:
        raise DtZohoError("pass --reminder or --no-reminders, not both.", exit_code=2)
    added = write.split_addresses(invite)
    removed = {a.lower() for a in write.split_addresses(uninvite)}
    new_reminders = zcalendar.parse_reminders(reminder) if reminder else ([] if no_reminders else None)
    for value, option in ((start, "--start"), (end, "--end")):
        if value is not None:
            zcalendar.parse_local(value, option)
    client = _client()
    cal = zcalendar.resolve_calendar(client, calendar)
    current = zcalendar.event(client, cal["uid"], uid)
    if occurrence is not None and not current.get("rrule") and current.get("etag") is not None:
        raise DtZohoError(f"event {uid} does not repeat, so it has no occurrences; leave out --occurrence.",
                          exit_code=2)
    if current.get("rrule") and occurrence is None:
        raise DtZohoError("this is a repeating event. Change one occurrence with --occurrence <id> (dt-zoho event "
                          "list --instances); changing the whole series was not proven, so dt-zoho does not offer it.",
                          exit_code=2)
    if occurrence in current["_deleted"]:
        raise DtZohoError(f"occurrence {occurrence} was deleted; there is nothing to change.", exit_code=2)
    if current.get("isallday"):
        raise DtZohoError("this is an all-day event; changing one was not proven, so dt-zoho does not offer it. "
                          "Delete it and create it again, or change it in Zoho Calendar.", exit_code=2)
    # A changed occurrence starts from its own current values, so a second change keeps the first one, and it
    # needs its own etag: the series' etag drew 409 CONFLICT_EVENT (8 November 2026).
    base = current["_changed"].get(occurrence, current) if occurrence else current
    etag = _need_etag(uid, base)
    when = base.get("dateandtime") or {}
    series_when = current.get("dateandtime") or {}
    zone = when.get("timezone") or series_when.get("timezone") or cal["time_zone"]
    if occurrence and occurrence not in current["_changed"]:
        old_start = _wall_time(occurrence, zone)
        old_end = old_start + (_wall_time(series_when["end"], zone) - _wall_time(series_when["start"], zone))
    else:
        old_start, old_end = _wall_time(when["start"], zone), _wall_time(when["end"], zone)
    begin, finish = _times(start, end, duration, (old_start, old_end))
    invited = _invitees(base)
    kept = [a for a in invited if a.lower() not in removed]
    attendees = kept + [a for a in added if a.lower() not in {k.lower() for k in kept}]
    # Zoho mails everyone still invited again, and a "Cancelled" notice to anyone removed (8 November 2026).
    config.check_recipients(config.load(), attendees + [a for a in invited if a.lower() in removed])
    reminders = new_reminders if new_reminders is not None else [
        {"action": r.get("action"), "minutes": int(r.get("minutes"))} for r in base.get("reminders") or []
        if r.get("minutes") not in (None, "")]
    data = zcalendar.event_data(title if title is not None else base.get("title") or "", begin, finish, zone,
                                location if location is not None else base.get("location"),
                                description if description is not None else base.get("description"),
                                attendees, reminders, None, False)
    zcalendar.check_no_start_link({"--title": data.get("title"), "--location": data.get("location"),
                                   "--description": data.get("description")},
                                  zcalendar.meeting_links(current)[zcalendar.START_KEY], changing=True)
    data["etag"] = etag
    if occurrence:
        data.update({"isrep": True, "recurrenceid": occurrence, "recurrence_edittype": "only"})
    zcalendar.change_event(client, cal["uid"], uid, data)
    stored = zcalendar.event(client, cal["uid"], uid)
    shown = stored["_changed"].get(occurrence, stored) if occurrence else stored
    record = zcalendar.event_record(shown)
    record.update(calendar=cal["name"], changed=True, occurrence=occurrence)
    _print_event(record, json_output)
    _check_dropped(added, shown, uid, cal)


@event_app.command("delete")
@_command
def event_delete(uid: str = EventArg,
                 occurrence: Optional[str] = typer.Option(None, "--occurrence", help="Delete only this occurrence of "
                                                          "a repeating event (its id from event list --instances)."),
                 calendar: Optional[str] = CalendarOpt, json_output: bool = JsonOpt) -> None:
    """Delete an event, a whole repeating series, or one occurrence of it."""
    client = _client()
    cal = zcalendar.resolve_calendar(client, calendar)
    current = zcalendar.event(client, cal["uid"], uid)
    # Deleting an event mails its invitees a "Cancelled" notice (8 November 2026), so test mode checks them.
    config.check_recipients(config.load(), _invitees(current))
    etag = _need_etag(uid, current["_changed"].get(occurrence, current) if occurrence else current)
    if occurrence:
        zcalendar.delete_occurrence(client, cal["uid"], uid, etag, occurrence)
    else:
        zcalendar.delete_event(client, cal["uid"], uid, etag)
    _report({"deleted": True, "uid": uid, "occurrence": occurrence, "calendar": cal["name"]}, json_output)


@event_app.command("respond", context_settings={"allow_extra_args": True, "ignore_unknown_options": True})
@_command
def event_respond(ctx: typer.Context) -> None:
    """Not possible through Zoho's API: says so and names the tool that can."""
    raise DtZohoError(zcalendar.RESPOND_REFUSAL, exit_code=2)
