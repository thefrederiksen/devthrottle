"""dt-calendar driven through its real command line, with only the network replaced (see fakes.py)."""

import json
import re
from datetime import date, datetime, timedelta

import pytest
from cc_shared.axi_output import parse_list

from conftest import ME, OTHER, OUTSIDER, PASSWORD_ENV
from src import ics

TOMORROW = (date.today() + timedelta(days=1)).isoformat()


def _create(env, *extra, summary="Planning"):
    return json.loads(env.ok("create", "--summary", summary, "--start", f"{TOMORROW} 14:00", "--duration", "30",
                             "--json", *extra).stdout)


# --------------------------------------------------------------------------------------------- accounts


def test_accountAdd_StoresNoPassword(env, tmp_path):
    text = (tmp_path / "dt-calendar" / "config.json").read_text(encoding="utf-8")
    assert "me-entry" in text and '"pw"' not in text and "pw2" not in text


def test_command_PasswordVariableMissing_NamesTheCcSecretsCommand(env, monkeypatch):
    monkeypatch.delenv(PASSWORD_ENV)
    result = env.run("calendars")
    assert result.exit_code == 1 and "cc-secrets run me-entry -- dt-calendar" in result.output
    assert env.server.requests == []


def test_accountAdd_PlainHttp_Refused(env):
    assert env.run("account", "add", "x", "--email", ME, "--url", "http://cal.example.com", "--secret-entry", "e",
                   "--password-env", "V").exit_code == 2


def test_wrongPassword_ClearError(env, monkeypatch):
    monkeypatch.setenv(PASSWORD_ENV, "wrong")
    result = env.run("calendars")
    assert result.exit_code == 1 and "refused the sign-in" in result.output and "me-entry" in result.output


def test_discovery_FollowsAWellKnownRedirect(env):
    env.server.redirect_well_known = True
    assert "count: 1" in env.ok("calendars").stdout


def test_discovery_NeverUsesOptions(env):
    env.ok("account", "check")
    assert not [r for r in env.server.requests if r[0] == "OPTIONS"]


# --------------------------------------------------------------------------------------------- reading


def test_calendars_Listed(env):
    data = json.loads(env.ok("calendars", "--json").stdout)
    assert data["count"] == 1 and data["calendars"][0]["name"] == "soren"


def test_events_UsesMultigetForBodies_SortedWithCount(env):
    env.ok("create", "--summary", "Later, with comma", "--start", f"{TOMORROW} 16:00")
    env.ok("create", "--summary", "Earlier", "--start", f"{TOMORROW} 09:00")
    result = env.ok("events")
    assert "count: 2 of 2 total" in result.stdout
    fields, rows = parse_list(result.stdout.split("help[")[0].split("\n", 1)[1].strip(), "events")
    assert [r["summary"] for r in rows] == ["Earlier", "Later, with comma"]
    reports = [r for r in env.server.requests if r[0] == "REPORT"]
    assert len(reports) == 2  # one query (ETags only), one multiget (the bodies)


def test_events_EmptyRange_SaysCountZero(env):
    assert "count: 0" in env.ok("events").stdout


def test_events_OutsideRange_NotListed(env):
    env.ok("create", "--summary", "Far", "--start", (date.today() + timedelta(days=90)).isoformat() + " 10:00")
    assert json.loads(env.ok("events", "--json").stdout)["count"] == 0
    far = (date.today() + timedelta(days=90)).isoformat()
    assert json.loads(env.ok("events", "--from", far, "--to", far, "--json").stdout)["count"] == 1


def test_events_UnknownField_UsageError(env):
    assert env.run("events", "--fields", "id,nonsense").exit_code == 2


def test_event_ShowsAttendeesAndTimesInLocalZone(env):
    created = _create(env, "--invite", OTHER, "--location", "Room 1")
    result = env.ok("event", created["created"])
    assert "location: Room 1" in result.stdout and f"{OTHER},needs-action" in result.stdout
    expected = datetime.fromisoformat(f"{TOMORROW}T14:00").astimezone().isoformat(timespec="minutes")
    assert f"start: {expected}" in result.stdout


def test_event_Deleted_ClearErrorFromEmptyBody(env):
    created = _create(env)
    env.ok("delete", created["created"])
    result = env.run("event", created["created"])
    assert result.exit_code == 1 and "there is no event" in result.output


# --------------------------------------------------------------------------------------------- writing


def test_create_ReadsBackEtagSinceThePutAnswerHasNone(env):
    created = _create(env)
    assert created["etag"] and created["etag"].isdigit()
    put = [r for r in env.server.requests if r[0] == "PUT"][0]
    assert put[2]["If-None-Match"] == "*"


def test_create_AllDay(env):
    created = json.loads(env.ok("create", "--summary", "Holiday", "--start", TOMORROW, "--all-day", "--json").stdout)
    assert created["start"] == TOMORROW
    stored = env.me.events[created["created"]][0]
    assert re.search(r"^DTSTART;VALUE=DATE:\d{8}\r?$", stored, re.M)


def test_create_EndBeforeStart_UsageError(env):
    assert env.run("create", "--summary", "x", "--start", f"{TOMORROW} 14:00", "--end", f"{TOMORROW} 13:00").exit_code == 2


def test_create_Invite_OrganizerIsAccountAndInviteeGetsCopy(env):
    created = _create(env, "--invite", OTHER)
    stored = env.me.events[created["created"]][0]
    assert f"ORGANIZER:mailto:{ME}" in stored
    assert created["created"] in env.other.events


def test_create_OutsideAttendeeDroppedByServer_ReportedAsFailure(env):
    result = env.run("create", "--summary", "x", "--start", f"{TOMORROW} 14:00", "--invite", OTHER, "--invite", OUTSIDER)
    assert result.exit_code == 1
    assert "did NOT keep someone@elsewhere.org" in result.output
    assert "created:" in result.stdout


def test_change_KeepsWhatTheServerAddedAndUsesIfMatch(env):
    created = _create(env)
    env.ok("change", created["created"], "--summary", "Planning, moved", "--location", "Room 2")
    stored = env.me.events[created["created"]][0]
    assert "BEGIN:VALARM" in stored and "SUMMARY:Planning\\, moved" in stored and "SEQUENCE:1" in stored
    put = [r for r in env.server.requests if r[0] == "PUT"][-1]
    assert put[2]["If-Match"] == created["etag"]


def test_change_StartKeepsLength(env):
    created = _create(env)
    changed = json.loads(env.ok("change", created["created"], "--start", f"{TOMORROW} 15:00", "--json").stdout)
    start = datetime.fromisoformat(changed["start"])
    end = datetime.fromisoformat(changed["end"])
    assert end - start == timedelta(minutes=30)


def test_change_ChangedOnServerMeanwhile_Refused409(env):
    created = _create(env)
    original_put = env.server._put

    def bump_then_put(user, name, headers, text):
        env.server._store(user, name, user.events[name][0])  # someone else saved it in between
        return original_put(user, name, headers, text)

    env.server._put = bump_then_put
    result = env.run("change", created["created"], "--summary", "mine")
    assert result.exit_code == 1 and "(409)" in result.output and "changed on the server" in result.output


def test_change_InviteAndUninvite(env):
    created = _create(env)
    env.ok("change", created["created"], "--invite", OTHER)
    assert created["created"] in env.other.events
    env.ok("change", created["created"], "--uninvite", OTHER)
    assert "ATTENDEE" not in env.me.events[created["created"]][0].replace("ATTENDEE:mailto:", "")


def test_change_NothingGiven_UsageError(env):
    created = _create(env)
    assert env.run("change", created["created"]).exit_code == 2


def test_delete_OrganizerDeleteRemovesInviteeCopy(env):
    created = _create(env, "--invite", OTHER)
    env.ok("delete", created["created"])
    assert created["created"] not in env.me.events and created["created"] not in env.other.events


def test_respond_AcceptAndDecline_OrganizerSeesAnswer(env):
    created = _create(env, "--invite", OTHER)
    env.ok("respond", created["created"], "--as", "accepted", "--account", "other")
    organizer_view = json.loads(env.ok("event", created["created"], "--json").stdout)
    assert organizer_view["attendee_list"][0]["status"] == "ACCEPTED"
    env.ok("respond", created["created"], "--as", "declined", "--account", "other")
    organizer_view = json.loads(env.ok("event", created["created"], "--json").stdout)
    assert organizer_view["attendee_list"][0]["status"] == "DECLINED"


def test_respond_NotInvited_UsageError(env):
    created = _create(env)
    assert env.run("respond", created["created"], "--as", "accepted").exit_code == 2


# --------------------------------------------------------------------------------------------- test mode


def test_create_InviteeNotAllowed_RefusedBeforeAnyRequest(env):
    env.ok("allowed-recipients", "set", OTHER)
    result = env.run("create", "--summary", "x", "--start", f"{TOMORROW} 14:00", "--invite", OUTSIDER)
    assert result.exit_code == 1 and "Nothing was sent" in result.output
    assert env.server.requests == []


def test_change_InviteeNotAllowed_RefusedBeforeAnyWrite(env):
    created = _create(env)
    env.ok("allowed-recipients", "set", OTHER)
    before = len(env.writes())
    result = env.run("change", created["created"], "--invite", OUTSIDER)
    assert result.exit_code == 1 and len(env.writes()) == before


def test_allowedRecipients_Clear(env):
    env.ok("allowed-recipients", "set", OTHER)
    env.ok("allowed-recipients", "clear")
    assert "off" in env.ok("allowed-recipients", "show").stdout


# --------------------------------------------------------------------------------------------- iCalendar


def test_ics_RoundTripKeepsUnknownLinesAndFoldsLongOnes():
    text = ("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nX-WR-CALNAME:soren\r\nBEGIN:VEVENT\r\nUID:a\r\nSUMMARY:x\r\n"
            "X-ZOHO-THING;FOO=\"a:b;c\":keep me\r\nDESCRIPTION:" + "long " * 30 + "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n")
    cal = ics.parse(text)
    out = ics.serialize(cal)
    assert all(len(line.encode()) <= 75 for line in out.split("\r\n"))
    again = ics.master_event(ics.parse(out))
    assert again.get("X-ZOHO-THING").value == "keep me" and again.get("X-ZOHO-THING").param("FOO") == '"a:b;c"'
    assert again.get("DESCRIPTION").value == "long " * 30


def test_ics_TextEscaping():
    assert ics.unescape_text(ics.escape_text("a, b; c\\d\nnext")) == "a, b; c\\d\nnext"


def test_ics_UnknownTimeZone_ShownAsWrittenNotGuessed():
    prop = ics.Prop("DTSTART", [("TZID", "Eastern Standard Time")], "20261109T100000")
    shown, moment = ics.time_value(prop)
    assert shown == "2026-11-09T10:00 (Eastern Standard Time)" and moment is None


def test_ics_KnownTimeZone():
    shown, moment = ics.time_value(ics.Prop("DTSTART", [("TZID", "America/Toronto")], "20261109T100000"))
    assert shown == "2026-11-09T10:00-05:00" and moment.isoformat() == "2026-11-09T15:00:00+00:00"


def test_change_ServerKeepsRemovedAttendee_ReportedAsFailure(env):
    # Found in the live run: Zoho keeps an attendee the organizer removes over CalDAV.
    env.server.keeps_removed_attendees = True
    created = _create(env, "--invite", OTHER)
    result = env.run("change", created["created"], "--uninvite", OTHER)
    assert result.exit_code == 1
    assert "still has other@example.com" in result.output


def test_create_NoneDropped_SaysNone(env):
    assert "dropped_attendees: none" in env.ok("create", "--summary", "x", "--start", f"{TOMORROW} 14:00").stdout
