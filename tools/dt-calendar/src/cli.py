"""Command line for dt-calendar: calendar over standard CalDAV, for any provider.

Every command that touches a calendar reads the account's password from the environment variable named
in its account settings, supplied by cc-secrets:

    cc-secrets run zoho-mail-admin -- dt-calendar events
"""

from __future__ import annotations

import functools
import socket
import ssl
import sys
import urllib.error
import uuid
from datetime import date, datetime, timedelta, timezone
from email.utils import getaddresses
from typing import Callable, List, Optional, Tuple

import typer

from . import __version__
from . import caldav, config, ics, output
from .errors import DtCalendarError

TOOL = "dt-calendar"

app = typer.Typer(name=TOOL, add_completion=False, no_args_is_help=True,
                  help="Calendar over standard CalDAV, for any provider. The password comes from cc-secrets: "
                       "cc-secrets run <entry> -- dt-calendar <command>.")
account_app = typer.Typer(no_args_is_help=True, help="Set up the accounts dt-calendar signs in to (no secret is stored).")
recipients_app = typer.Typer(no_args_is_help=True,
                             help="Test mode: when a list is set, inviting anyone else is refused before sending.")
app.add_typer(account_app, name="account")
app.add_typer(recipients_app, name="allowed-recipients")

AccountOpt = typer.Option(None, "--account", "-a", help="Account name. Default: the default account.")
CalendarOpt = typer.Option(None, "--calendar", "-c", help="Calendar name. Default: the only event calendar.")
JsonOpt = typer.Option(False, "--json", help="Print JSON with every field.")

EVENT_FIELDS = ["id", "start", "end", "summary", "location", "uid", "organizer", "attendees", "recurring", "etag",
                "calendar"]
EVENT_DEFAULT = ["id", "start", "summary"]


# --------------------------------------------------------------------------------------------- plumbing


def _fail(message: str, code: int = 1) -> None:
    """Print the error, name it for the failure reporter, and exit."""
    from cc_shared.tool_errors import note_failure

    note_failure(message)
    sys.stderr.write(f"Error: {output.ascii_text(message)}\n")
    raise typer.Exit(code)


def _command(func: Callable) -> Callable:
    """Turn every expected failure of a command into one clear error line and an exit code."""
    @functools.wraps(func)
    def wrapper(*args, **kwargs):
        try:
            return func(*args, **kwargs)
        except DtCalendarError as exc:
            _fail(exc.message, exc.exit_code)
        except ssl.SSLError as exc:
            _fail(f"the secure connection failed: {exc}. Check the account's --url (dt-calendar account list).")
        except (urllib.error.URLError, ConnectionError, TimeoutError, socket.gaierror) as exc:
            reason = exc.reason if isinstance(exc, urllib.error.URLError) else exc
            _fail(f"could not reach the CalDAV server: {reason}. Check the account's --url (dt-calendar account list) "
                  "and the network.")
    return wrapper


def _client(account_name: Optional[str]) -> Tuple[config.Config, config.Account, caldav.CalDavClient]:
    cfg = config.load()
    acct = cfg.account(account_name)
    return cfg, acct, caldav.CalDavClient(acct, acct.password())


def _addresses(values: Optional[List[str]]) -> List[str]:
    if not values:
        return []
    found = [a.strip().lower() for _, a in getaddresses(values) if a]
    for a in found:
        if "@" not in a:
            raise DtCalendarError(f"'{a}' is not an email address.", exit_code=2)
    return list(dict.fromkeys(found))


def _report(data: dict, json_output: bool, extra: Optional[List[str]] = None) -> None:
    if json_output:
        output.emit_json(data)
        return
    flat = [(k, (", ".join(v) or "none") if isinstance(v, list) else v) for k, v in data.items()]
    output.emit([output.kv_block(flat)] + (extra or []))


# ----- times


def _moment(value: str, label: str) -> datetime:
    """A time the person typed: '2026-11-10 14:00' in this machine's time zone, or with an offset or Z."""
    try:
        moment = datetime.fromisoformat(value.strip().replace(" ", "T", 1))
    except ValueError as exc:
        raise DtCalendarError(f"{label} must be a time like '2026-11-10 14:00' (this machine's time zone) or "
                              f"'2026-11-10T14:00-05:00', not '{value}'.", exit_code=2) from exc
    return moment if moment.tzinfo else moment.astimezone()


def _day(value: str, label: str) -> date:
    try:
        return date.fromisoformat(value.strip())
    except ValueError as exc:
        raise DtCalendarError(f"{label} must be a date like 2026-11-10, not '{value}'.", exit_code=2) from exc


def _times(start: str, end: Optional[str], duration: Optional[int], all_day: bool) -> Tuple[str, str]:
    """(DTSTART, DTEND) values. Timed events are written in UTC; all-day events as dates, end exclusive."""
    if end is not None and duration is not None:
        raise DtCalendarError("pass --end or --duration, not both.", exit_code=2)
    if all_day:
        if duration is not None:
            raise DtCalendarError("an all-day event takes --end (a date), not --duration.", exit_code=2)
        first = _day(start, "--start")
        last = _day(end, "--end") if end is not None else first
        if last < first:
            raise DtCalendarError("--end is before --start.", exit_code=2)
        return ics.date_stamp(first), ics.date_stamp(last + timedelta(days=1))
    begin = _moment(start, "--start")
    finish = _moment(end, "--end") if end is not None else begin + timedelta(minutes=duration or 60)
    if finish <= begin:
        raise DtCalendarError("the event must end after it starts.", exit_code=2)
    return ics.utc_stamp(begin), ics.utc_stamp(finish)


# ----- event records


def _record(resource: caldav.EventResource, calendar_name: str) -> dict:
    event = ics.master_event(ics.parse(resource.data))
    start, start_utc = ics.time_value(event.get("DTSTART"))
    end, _ = ics.time_value(event.get("DTEND"))
    organizer = event.get("ORGANIZER")
    people = ics.attendees(event)
    summary = event.get("SUMMARY")
    location = event.get("LOCATION")
    description = event.get("DESCRIPTION")
    return {
        "id": resource.id,
        "start": start,
        "end": end,
        "summary": ics.unescape_text(summary.value) if summary else "",
        "location": (ics.unescape_text(location.value) or None) if location else None,
        "description": (ics.unescape_text(description.value) or None) if description else None,
        "uid": event.get("UID").value if event.get("UID") else None,
        "organizer": ics.address(organizer.value) if organizer else None,
        "attendees": "; ".join(f"{p['address']} {p['status'].lower()}" for p in people) or None,
        "attendee_list": people,
        "recurring": event.get("RRULE") is not None,
        "etag": resource.etag,
        "calendar": calendar_name,
        "_sort": start_utc.isoformat() if start_utc else "",
    }


def _public(record: dict) -> dict:
    return {k: v for k, v in record.items() if not k.startswith("_")}


def _read_back(client: caldav.CalDavClient, cal: caldav.Calendar, event_id: str) -> dict:
    return _record(client.get(cal, event_id), cal.name)


def _dropped(asked: List[str], record: dict) -> List[str]:
    kept = {p["address"].lower() for p in record["attendee_list"]}
    return [a for a in asked if a.lower() not in kept]


def _fail_if_dropped(dropped: List[str]) -> None:
    """The write is saved, but someone asked for was not invited: that is a failure the caller must see."""
    if dropped:
        _fail(f"the server saved the event but did NOT keep {', '.join(dropped)} as attendees, so they were not "
              "invited. Some servers (Zoho) invite only people inside their own organisation over CalDAV.")


# --------------------------------------------------------------------------------------------- top level


def _version(value: bool) -> None:
    if value:
        typer.echo(f"{TOOL} {__version__}")
        raise typer.Exit()


@app.callback()
def main(version: bool = typer.Option(False, "--version", callback=_version, is_eager=True,
                                      help="Print the version and exit.")) -> None:
    """Calendar over standard CalDAV, for any provider."""


# ----- accounts


@account_app.command("add")
@_command
def account_add(
    name: str = typer.Argument(..., help="A short name for the account, used with --account."),
    email_address: str = typer.Option(..., "--email", help="The account's email address (the calendar user)."),
    url: str = typer.Option(..., "--url", help="The CalDAV server, for example https://calendar.zohocloud.ca."),
    secret_entry: str = typer.Option(..., "--secret-entry", help="The cc-secrets entry holding the password."),
    password_env: str = typer.Option(..., "--password-env", help="The variable cc-secrets puts the password in."),
    user: Optional[str] = typer.Option(None, "--user", help="Sign-in name. Default: the --email address."),
    make_default: bool = typer.Option(False, "--default", help="Make this the default account."),
    json_output: bool = JsonOpt,
) -> None:
    """Add or replace an account. Stores the server and WHICH cc-secrets entry holds the password, never the
    password."""
    if not url.startswith("https://"):
        raise DtCalendarError(f"--url must start with https:// (a password is sent to it), not '{url}'.", exit_code=2)
    cfg = config.load()
    acct = config.Account(name=name, email=config.normalise_address(email_address), user=user or email_address,
                          url=url.rstrip("/"), secret_entry=secret_entry, password_env=password_env)
    cfg.accounts[name] = acct
    if make_default or cfg.default is None:
        cfg.default = name
    path = config.save(cfg)
    data = {"account": name, **acct.to_dict(), "default": cfg.default == name, "config": str(path)}
    _report(data, json_output, None if json_output else [
        output.format_help([f"cc-secrets run {secret_entry} -- dt-calendar account check --account {name}"])])


@account_app.command("list")
@_command
def account_list(json_output: bool = JsonOpt) -> None:
    """List the accounts set up on this machine."""
    cfg = config.load()
    records = [{"name": a.name, "email": a.email, "url": a.url, "secret_entry": a.secret_entry,
                "password_env": a.password_env, "default": a.name == cfg.default}
               for a in sorted(cfg.accounts.values(), key=lambda a: a.name)]
    if json_output:
        output.emit_json({"accounts": records, "allowed_recipients": cfg.allowed_recipients})
        return
    output.emit_list("accounts", ["name", "email", "url", "secret_entry", "default"], records, None,
                     [] if records else ["dt-calendar account add <name> --email <address> --url <CalDAV server> "
                                         "--secret-entry <entry> --password-env <VARIABLE>"])


@account_app.command("remove")
@_command
def account_remove(name: str = typer.Argument(...), json_output: bool = JsonOpt) -> None:
    """Remove an account's settings. The password in cc-secrets is not touched."""
    cfg = config.load()
    if name not in cfg.accounts:
        raise DtCalendarError(f"no account named '{name}'. List them with: dt-calendar account list")
    del cfg.accounts[name]
    if cfg.default == name:
        cfg.default = None
    config.save(cfg)
    _report({"removed": name}, json_output)


@account_app.command("default")
@_command
def account_default(name: str = typer.Argument(...), json_output: bool = JsonOpt) -> None:
    """Make an account the default."""
    cfg = config.load()
    cfg.account(name)
    cfg.default = name
    config.save(cfg)
    _report({"default": name}, json_output)


@account_app.command("check")
@_command
def account_check(account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Sign in, find the calendar home and count the calendars. Changes nothing."""
    _, acct, client = _client(account)
    home = client.home()
    cals = client.calendars()
    _report({"account": acct.name, "signed_in": True, "calendar_home": home, "calendars": len(cals)}, json_output)


# ----- allowed recipients


@recipients_app.command("show")
@_command
def recipients_show(json_output: bool = JsonOpt) -> None:
    """Show the allowed invitees. 'off' means no restriction."""
    cfg = config.load()
    if json_output:
        output.emit_json({"allowed_recipients": cfg.allowed_recipients})
    elif cfg.allowed_recipients is None:
        output.emit(["allowed_recipients: off (anyone may be invited)"])
    else:
        output.emit_list("allowed_recipients", ["address"], [{"address": a} for a in cfg.allowed_recipients], None)


@recipients_app.command("set")
@_command
def recipients_set(addresses: List[str] = typer.Argument(..., help="Every address that may be invited."),
                   json_output: bool = JsonOpt) -> None:
    """Turn test mode on: only these addresses may be invited. Replaces any earlier list."""
    cfg = config.load()
    cfg.allowed_recipients = sorted({config.normalise_address(a) for a in addresses})
    config.save(cfg)
    if json_output:
        output.emit_json({"allowed_recipients": cfg.allowed_recipients})
    else:
        output.emit_list("allowed_recipients", ["address"], [{"address": a} for a in cfg.allowed_recipients], None)


@recipients_app.command("clear")
@_command
def recipients_clear(json_output: bool = JsonOpt) -> None:
    """Turn test mode off: anyone may be invited."""
    cfg = config.load()
    cfg.allowed_recipients = None
    config.save(cfg)
    if json_output:
        output.emit_json({"allowed_recipients": None})
    else:
        output.emit(["allowed_recipients: off (anyone may be invited)"])


# ----- reading


@app.command("calendars")
@_command
def calendars_command(account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """List the account's calendars."""
    _, _, client = _client(account)
    records = [{"name": c.name, "components": " ".join(c.components), "href": c.href} for c in client.calendars()]
    if json_output:
        output.emit_json({"count": len(records), "calendars": records})
        return
    output.emit_list("calendars", ["name", "components", "href"], records, None,
                     ['dt-calendar events --calendar "<name>"'] if records else [])


@app.command("events")
@_command
def events_command(calendar: Optional[str] = CalendarOpt,
                   from_: Optional[str] = typer.Option(None, "--from", help="First day (2026-11-07). Default: today."),
                   to: Optional[str] = typer.Option(None, "--to", help="Last day, included. Default: 30 days after --from."),
                   count: int = typer.Option(50, "--count", "-n", min=1, help="Show at most this many, earliest first."),
                   fields: Optional[str] = typer.Option(None, "--fields", help="Fields: " + ",".join(EVENT_FIELDS) + "."),
                   account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """List events in a date range, earliest first. A recurring event is listed once, at its first start."""
    first = _day(from_, "--from") if from_ else date.today()
    last = _day(to, "--to") if to else first + timedelta(days=30)
    if last < first:
        raise DtCalendarError("--to is before --from.", exit_code=2)
    start = datetime.combine(first, datetime.min.time()).astimezone()
    end = datetime.combine(last + timedelta(days=1), datetime.min.time()).astimezone()
    _, _, client = _client(account)
    cal = client.calendar(calendar)
    hrefs = [h for h, _ in client.event_hrefs(cal, ics.utc_stamp(start), ics.utc_stamp(end))]
    records = sorted((_record(r, cal.name) for r in client.multiget(cal, hrefs)), key=lambda r: r["_sort"])
    total = len(records)
    shown = [_public(r) for r in records[:count]]
    if json_output:
        output.emit_json({"count": len(shown), "total": total, "calendar": cal.name, "from": first.isoformat(),
                          "to": last.isoformat(), "events": shown})
        return
    from cc_shared.axi_output import parse_fields_or_exit

    chosen = parse_fields_or_exit(fields, EVENT_FIELDS, EVENT_DEFAULT)
    output.emit_list("events", chosen, shown, total,
                     [f'dt-calendar event <id> --calendar "{cal.name}"'] if shown else [])


@app.command("event")
@_command
def event_command(event_id: str = typer.Argument(..., help="The event id, from dt-calendar events."),
                  calendar: Optional[str] = CalendarOpt, account: Optional[str] = AccountOpt,
                  json_output: bool = JsonOpt) -> None:
    """Show one event with everyone invited and their answers."""
    _, _, client = _client(account)
    cal = client.calendar(calendar)
    record = _public(_read_back(client, cal, event_id))
    if json_output:
        output.emit_json(record)
        return
    blocks = [output.kv_block((k, record[k]) for k in ("id", "calendar", "start", "end", "summary", "location",
                                                        "description", "organizer", "recurring", "uid"))]
    people = [{"address": p["address"], "status": p["status"].lower(), "name": p["name"]} for p in record["attendee_list"]]
    blocks.append(output.render_list("attendees", ["address", "status", "name"], people))
    output.emit(blocks)


# ----- writing


InviteOpt = typer.Option(None, "--invite", help="Invite this person (repeat, or comma separated).")


@app.command("create")
@_command
def create_command(summary: str = typer.Option(..., "--summary", "-s", help="The title."),
                   start: str = typer.Option(..., "--start", help="'2026-11-10 14:00' (this machine's time zone), "
                                                                   "or a date with --all-day."),
                   end: Optional[str] = typer.Option(None, "--end", help="End time, or last day with --all-day."),
                   duration: Optional[int] = typer.Option(None, "--duration", min=1, help="Minutes. Default 60."),
                   all_day: bool = typer.Option(False, "--all-day", help="An all-day event; --start and --end are dates."),
                   location: Optional[str] = typer.Option(None, "--location"),
                   description: Optional[str] = typer.Option(None, "--description"),
                   invite: Optional[List[str]] = InviteOpt, calendar: Optional[str] = CalendarOpt,
                   account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Create an event. With --invite the account is the organizer and the server sends the invitations."""
    dtstart, dtend = _times(start, end, duration, all_day)
    invitees = _addresses(invite)
    cfg, acct, client = _client(account)
    config.check_invitees(cfg, invitees)
    cal = client.calendar(calendar)
    uid = f"dt-{uuid.uuid4().hex}"
    event_id = f"{uid}.ics"
    body = ics.serialize(ics.new_calendar(uid, summary, dtstart, dtend, all_day, location, description, acct.email,
                                          invitees))
    client.put(client.event_href(cal, event_id), body, create=True, etag=None)
    record = _read_back(client, cal, event_id)
    dropped = _dropped(invitees, record)
    data = {"created": event_id, **{k: record[k] for k in ("calendar", "start", "end", "summary", "attendees", "etag")},
            "dropped_attendees": dropped}
    _report(data, json_output)
    _fail_if_dropped(dropped)


@app.command("change")
@_command
def change_command(event_id: str = typer.Argument(..., help="The event id, from dt-calendar events."),
                   summary: Optional[str] = typer.Option(None, "--summary", "-s"),
                   start: Optional[str] = typer.Option(None, "--start", help="New start; the length is kept unless "
                                                                            "--end or --duration is given."),
                   end: Optional[str] = typer.Option(None, "--end"),
                   duration: Optional[int] = typer.Option(None, "--duration", min=1, help="Minutes."),
                   location: Optional[str] = typer.Option(None, "--location", help="New location ('' removes it)."),
                   description: Optional[str] = typer.Option(None, "--description", help="New description ('' removes it)."),
                   invite: Optional[List[str]] = InviteOpt,
                   uninvite: Optional[List[str]] = typer.Option(None, "--uninvite", help="Remove this attendee (repeat)."),
                   calendar: Optional[str] = CalendarOpt, account: Optional[str] = AccountOpt,
                   json_output: bool = JsonOpt) -> None:
    """Change an event. Only what is given changes; everything else in it, including what the server added,
    is kept. Refused if the event changed on the server since it was read."""
    added = _addresses(invite)
    removed = _addresses(uninvite)
    if not any(v is not None for v in (summary, start, end, duration, location, description)) and not added and not removed:
        raise DtCalendarError("nothing to change: pass --summary, --start, --end, --duration, --location, "
                              "--description, --invite or --uninvite.", exit_code=2)
    cfg, acct, client = _client(account)
    config.check_invitees(cfg, added)
    cal = client.calendar(calendar)
    current = client.get(cal, event_id)
    calendar_obj = ics.parse(current.data)
    event = ics.master_event(calendar_obj)
    if len([c for c in calendar_obj.children if c.name == "VEVENT"]) > 1:
        raise DtCalendarError("this recurring event has changed occurrences; dt-calendar changes only simple "
                              "events and whole series without exceptions.", exit_code=2)
    if summary is not None:
        event.set("SUMMARY", ics.escape_text(summary))
    for name, value in (("LOCATION", location), ("DESCRIPTION", description)):
        if value == "":
            event.remove(name)
        elif value is not None:
            event.set(name, ics.escape_text(value))
    if start is not None or end is not None or duration is not None:
        _change_times(event, start, end, duration)
    known = {p["address"].lower() for p in ics.attendees(event)}
    missing = [a for a in removed if a not in known]
    if missing:
        raise DtCalendarError(f"{', '.join(missing)} is not invited to this event; nothing was changed.", exit_code=2)
    event.props = [p for p in event.props if not (p.name == "ATTENDEE" and ics.address(p.value).lower() in removed)]
    for addr in added:
        if addr not in known:
            event.props.append(ics.attendee_prop(addr))
    if added and event.get("ORGANIZER") is None:
        event.set("ORGANIZER", f"mailto:{acct.email}")
    sequence = event.get("SEQUENCE")
    event.set("SEQUENCE", str(int(sequence.value) + 1 if sequence and sequence.value.isdigit() else 1))
    event.set("DTSTAMP", ics.utc_stamp(datetime.now(timezone.utc)))
    client.put(current.href, ics.serialize(calendar_obj), create=False, etag=current.etag)
    record = _read_back(client, cal, event_id)
    dropped = _dropped([a for a in added if a not in known], record)
    still_there = {p["address"].lower() for p in record["attendee_list"]}
    kept_anyway = [a for a in removed if a in still_there]
    data = {"changed": event_id, **{k: record[k] for k in ("calendar", "start", "end", "summary", "location",
                                                           "attendees", "etag")},
            "dropped_attendees": dropped, "kept_attendees": kept_anyway}
    _report(data, json_output)
    _fail_if_dropped(dropped)
    if kept_anyway:
        _fail(f"the server saved the change but still has {', '.join(kept_anyway)} as attendees, so they are still "
              "invited. Some servers (Zoho) do not let an organizer remove an attendee over CalDAV.")


def _change_times(event: ics.Component, start: Optional[str], end: Optional[str], duration: Optional[int]) -> None:
    all_day = (event.get("DTSTART") is not None and event.get("DTSTART").param("VALUE") == "DATE")
    if all_day:
        old_start = _day(ics.time_value(event.get("DTSTART"))[0], "the event's start")
        new_start = start or old_start.isoformat()
        if end is None and duration is None and start is not None and event.get("DTEND") is not None:
            days = (_day(ics.time_value(event.get("DTEND"))[0], "the event's end") - old_start).days
            end = (_day(new_start, "--start") + timedelta(days=days - 1)).isoformat()
        dtstart, dtend = _times(new_start, end, duration, True)
        event.set("DTSTART", dtstart, [("VALUE", "DATE")])
        event.set("DTEND", dtend, [("VALUE", "DATE")])
        return
    _, old_start = ics.time_value(event.get("DTSTART"))
    _, old_end = ics.time_value(event.get("DTEND"))
    if old_start is None or (start is not None and end is None and duration is None and old_end is None):
        raise DtCalendarError("this event's times use a time zone this machine does not know; pass both --start "
                              "and --end.", exit_code=2)
    new_start = _moment(start, "--start") if start is not None else old_start
    if end is not None:
        new_end = _moment(end, "--end")
    elif duration is not None:
        new_end = new_start + timedelta(minutes=duration)
    else:
        new_end = new_start + (old_end - old_start)
    if new_end <= new_start:
        raise DtCalendarError("the event must end after it starts.", exit_code=2)
    event.remove("DURATION")
    event.set("DTSTART", ics.utc_stamp(new_start))
    event.set("DTEND", ics.utc_stamp(new_end))


@app.command("delete")
@_command
def delete_command(event_id: str = typer.Argument(..., help="The event id, from dt-calendar events."),
                   calendar: Optional[str] = CalendarOpt, account: Optional[str] = AccountOpt,
                   json_output: bool = JsonOpt) -> None:
    """Delete an event. When this account organised it, the server tells those invited it is cancelled."""
    _, _, client = _client(account)
    cal = client.calendar(calendar)
    current = client.get(cal, event_id)
    client.delete(current.href)
    _report({"deleted": event_id, "calendar": cal.name}, json_output)


ANSWERS = {"accepted": "ACCEPTED", "declined": "DECLINED", "tentative": "TENTATIVE"}


@app.command("respond")
@_command
def respond_command(event_id: str = typer.Argument(..., help="The invitation's id in this account's calendar."),
                    as_: str = typer.Option(..., "--as", help="accepted, declined or tentative."),
                    calendar: Optional[str] = CalendarOpt, account: Optional[str] = AccountOpt,
                    json_output: bool = JsonOpt) -> None:
    """Accept, decline or tentatively accept an invitation in this account's calendar. The server passes the
    answer on to the organizer."""
    if as_ not in ANSWERS:
        raise DtCalendarError(f"--as must be one of {', '.join(ANSWERS)}, not '{as_}'.", exit_code=2)
    _, acct, client = _client(account)
    cal = client.calendar(calendar)
    current = client.get(cal, event_id)
    calendar_obj = ics.parse(current.data)
    mine = [p for c in calendar_obj.children if c.name == "VEVENT" for p in c.all("ATTENDEE")
            if ics.address(p.value).lower() == acct.email.lower()]
    if not mine:
        raise DtCalendarError(f"{acct.email} is not invited to event '{event_id}', so there is nothing to answer.",
                              exit_code=2)
    for prop in mine:
        prop.set_param("PARTSTAT", ANSWERS[as_])
        prop.params = [(k, v) for k, v in prop.params if k.upper() != "RSVP"]
    for event in (c for c in calendar_obj.children if c.name == "VEVENT"):
        event.set("DTSTAMP", ics.utc_stamp(datetime.now(timezone.utc)))
    client.put(current.href, ics.serialize(calendar_obj), create=False, etag=current.etag)
    record = _read_back(client, cal, event_id)
    status = next((p["status"] for p in record["attendee_list"] if p["address"].lower() == acct.email.lower()), None)
    if status != ANSWERS[as_]:
        raise DtCalendarError(f"the server kept the answer as {status}, not {ANSWERS[as_]}.")
    _report({"answered": event_id, "as": as_, "organizer": record["organizer"], "summary": record["summary"]},
            json_output)


def tool_main() -> None:
    """The console-script entry point, through the shared failure reporter."""
    from cc_shared.tool_errors import run_tool

    run_tool(lambda: app(prog_name=TOOL), TOOL, app=app)
