"""Tests for what the separate review of pull request 4772 found. Each was red before its fix."""

import json
import re
from datetime import date, timedelta

from conftest import ME, OTHER, OUTSIDER

TOMORROW = (date.today() + timedelta(days=1)).isoformat()
STAMP = (date.today() + timedelta(days=1)).strftime("%Y%m%d")


def _put_raw(env, user, name, vevent_lines):
    text = "\r\n".join(["BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//test//EN", "BEGIN:VEVENT"] + vevent_lines
                       + ["END:VEVENT", "END:VCALENDAR"])
    env.server._store(user, name, text)


# 1. the password never follows a redirect to another host or to plain http

def test_discovery_RedirectToAnotherHost_RefusedAndNothingSentThere(env):
    env.server.redirect_well_known = "https://evil.example.net/dav/"
    result = env.run("calendars")
    assert result.exit_code == 1 and "not the account's server" in result.output
    assert not [u for u in env.server.urls if "evil.example.net" in u]


def test_discovery_RedirectToPlainHttp_Refused(env):
    env.server.redirect_well_known = "http://cal.example.com/dav/"
    result = env.run("calendars")
    assert result.exit_code == 1
    assert not [u for u in env.server.urls if u.startswith("http://")]


# 2. no ETag means no change, never a blind overwrite

def test_change_ServerSendsNoEtag_RefusedWithoutWriting(env):
    created = json.loads(env.ok("create", "--summary", "x", "--start", f"{TOMORROW} 14:00", "--json").stdout)
    env.server.send_etags = False
    before = len(env.writes())
    result = env.run("change", created["created"], "--summary", "y")
    assert result.exit_code == 1 and "no ETag" in result.output
    assert len(env.writes()) == before


# 3. unknown time zone or DURATION: the right flags work, the message is right

def test_change_UnknownTimeZoneWithStartAndEnd_Works(env):
    _put_raw(env, env.me, "tz.ics", ["UID:tz", f"DTSTART;TZID=Eastern Standard Time:{STAMP}T100000",
                                     f"DTEND;TZID=Eastern Standard Time:{STAMP}T110000", "SUMMARY:tz"])
    env.ok("change", "tz.ics", "--start", f"{TOMORROW} 10:00", "--end", f"{TOMORROW} 11:00")


def test_change_DurationEventWithStartAndDuration_Works(env):
    _put_raw(env, env.me, "dur.ics", ["UID:dur", f"DTSTART:{STAMP}T150000Z", "DURATION:PT1H", "SUMMARY:dur"])
    result = env.run("change", "dur.ics", "--start", f"{TOMORROW} 17:00")
    assert result.exit_code == 2 and "no end time" in result.output
    env.ok("change", "dur.ics", "--start", f"{TOMORROW} 16:00", "--duration", "45")
    assert "DURATION" not in env.me.events["dur.ics"][0]


# 4. a recurring series keeps its time zone

def test_change_RecurringSeriesInAZone_KeepsTheZone(env):
    _put_raw(env, env.me, "rec.ics", ["UID:rec", f"DTSTART;TZID=America/Toronto:{STAMP}T100000",
                                      f"DTEND;TZID=America/Toronto:{STAMP}T110000",
                                      "RRULE:FREQ=WEEKLY;COUNT=20", "SUMMARY:weekly"])
    env.ok("change", "rec.ics", "--start", f"{TOMORROW}T10:30-05:00")
    stored = env.me.events["rec.ics"][0]
    assert re.search(rf"^DTSTART;TZID=America/Toronto:{STAMP}T\d{{6}}\r?$", stored, re.M)
    assert re.search(rf"^DTEND;TZID=America/Toronto:{STAMP}T\d{{6}}\r?$", stored, re.M)


def test_change_RecurringFloatingSeries_TimeChangeRefused(env):
    _put_raw(env, env.me, "flt.ics", ["UID:flt", f"DTSTART:{STAMP}T100000", f"DTEND:{STAMP}T110000",
                                      "RRULE:FREQ=WEEKLY;UNTIL=20270301T100000", "SUMMARY:floating"])
    result = env.run("change", "flt.ics", "--start", f"{TOMORROW} 10:30")
    assert result.exit_code == 2 and "recurring" in result.output


# 5. an all-day end is shown as the last day, the same way --end takes it

def test_allDay_EndShownAsLastDay_RoundTripIsStable(env):
    day2 = (date.today() + timedelta(days=2)).isoformat()
    created = json.loads(env.ok("create", "--summary", "trip", "--start", TOMORROW, "--end", day2, "--all-day",
                                "--json").stdout)
    assert created["end"] == day2
    before = env.me.events[created["created"]][0]
    changed = json.loads(env.ok("change", created["created"], "--end", created["end"], "--json").stdout)
    assert changed["end"] == day2
    assert re.search(r"^DTEND[^\r\n]*", before, re.M).group(0) == re.search(
        r"^DTEND[^\r\n]*", env.me.events[created["created"]][0], re.M).group(0)


# 6. test mode covers every write that mails someone

def test_respond_OrganizerNotAllowed_Refused(env):
    _put_raw(env, env.me, "inv.ics", ["UID:inv", f"DTSTART:{STAMP}T150000Z", f"DTEND:{STAMP}T160000Z",
                                      "SUMMARY:from outside", f"ORGANIZER:mailto:{OUTSIDER}",
                                      f"ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:{ME}"])
    env.ok("allowed-recipients", "set", OTHER)
    before = len(env.writes())
    result = env.run("respond", "inv.ics", "--as", "accepted")
    assert result.exit_code == 1 and OUTSIDER in result.output and len(env.writes()) == before


def test_change_ExistingAttendeeNotAllowed_Refused(env):
    _put_raw(env, env.me, "ex.ics", ["UID:ex", f"DTSTART:{STAMP}T150000Z", f"DTEND:{STAMP}T160000Z",
                                     "SUMMARY:old", f"ORGANIZER:mailto:{ME}",
                                     f"ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:{OUTSIDER}"])
    env.ok("allowed-recipients", "set", OTHER)
    before = len(env.writes())
    result = env.run("change", "ex.ics", "--summary", "new")
    assert result.exit_code == 1 and OUTSIDER in result.output and len(env.writes()) == before


# 7. a time dt-calendar cannot read is an error line naming the event, never a traceback or a misreading

def test_events_MalformedTime_ErrorNamingTheEvent(env):
    _put_raw(env, env.me, "bad.ics", ["UID:bad-one", f"DTSTART:{STAMP}T1500", f"DTEND:{STAMP}T160000Z",
                                      "SUMMARY:bad"])
    result = env.run("events")
    assert result.exit_code == 1 and "event bad.ics" in result.output and "Traceback" not in result.output


# noted: --fields is checked under --json too; invitations are changed only by the organizer

def test_events_UnknownFieldWithJson_UsageError(env):
    assert env.run("events", "--fields", "id,nonsense", "--json").exit_code == 2


def test_change_InviteOnSomeoneElsesEvent_Refused(env):
    _put_raw(env, env.me, "theirs.ics", ["UID:theirs", f"DTSTART:{STAMP}T150000Z", f"DTEND:{STAMP}T160000Z",
                                         "SUMMARY:theirs", f"ORGANIZER:mailto:{OTHER}",
                                         f"ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:{ME}"])
    result = env.run("change", "theirs.ics", "--invite", OTHER)
    assert result.exit_code == 2 and "organizer" in result.output
