"""dt-zoho's calendar commands driven through the real command line, with only the network replaced
(fakes.py and fake_calendar.py)."""

import json

import pytest

from fake_calendar import DEFAULT_CAL

ME = "soren@devthrottle.com"
OTHER = "email@devthrottle.com"
OUTSIDE = "soren@duksrevo.com"


def _create(env, *extra) -> dict:
    out = env.ok("event", "create", "--title", "Plan", "--start", "2026-11-20 10:00", "--json", *extra).stdout
    return json.loads(out)


def _calls(env, method):
    return [c for c in env.zoho.calendar.calls if c[0] == method]


def _sent_event(env, method) -> dict:
    return json.loads(_calls(env, method)[-1][2]["eventdata"])


# --------------------------------------------------------------------------------------------- calendars


def test_calendars_ListsThem(env):
    data = json.loads(env.ok("calendars", "--json").stdout)
    assert data["calendars"][0] == {"uid": DEFAULT_CAL, "name": "soren", "privilege": "owner", "default": True,
                                    "time_zone": "America/Toronto", "color": "#8cbf40", "category": "own"}


def test_calendar_CreateRenameShareUnshareDelete(env):
    created = json.loads(env.ok("calendar", "create", "Team", "--color", "#112233", "--json").stdout)
    assert created["time_zone"] == "America/Toronto"
    env.ok("calendar", "rename", "Team", "--name", "Crew")
    cal = next(c for c in env.zoho.calendar.calendars if c["uid"] == created["uid"])
    assert (cal["name"], cal["color"]) == ("Crew", "#112233")
    env.ok("calendar", "share", "Crew", "--with", OTHER)
    assert json.loads(env.ok("calendar", "shares", "Crew", "--json").stdout)["shares"] == \
           [{"email": OTHER, "name": "email", "privilege": "view"}]
    env.ok("calendar", "unshare", "Crew", "--with", OTHER)
    assert json.loads(env.ok("calendar", "shares", "Crew", "--json").stdout)["count"] == 0
    env.ok("calendar", "delete", "Crew")
    assert all(c["uid"] != created["uid"] for c in env.zoho.calendar.calendars)


def test_calendar_DeleteDefault_IsRefused(env):
    result = env.run("calendar", "delete", "soren")
    assert result.exit_code == 2 and _calls(env, "DELETE") == []


def test_calendar_ShareWithSomeoneNotAllowed_IsRefusedBeforeAnyCall(env):
    env.ok("allowed-recipients", "set", ME, OTHER)
    result = env.run("calendar", "share", "soren", "--with", "stranger@example.org")
    assert result.exit_code == 1 and "stranger@example.org" in result.output and env.zoho.calendar.calls == []


# --------------------------------------------------------------------------------------------- create


def test_create_SendsLocalTimeWithTheCalendarsTimeZone(env):
    data = _create(env, "--duration", "30", "--location", "Room 1", "--description", "agenda")
    sent = _sent_event(env, "POST")
    assert sent["dateandtime"] == {"start": "20261120T100000", "end": "20261120T103000",
                                   "timezone": "America/Toronto"}
    assert (sent["location"], sent["description"]) == ("Room 1", "agenda")
    assert data["start"] == "2026-11-20T10:00:00-05:00" and data["end"] == "2026-11-20T10:30:00-05:00"
    assert data["uid"] and data["etag"]


def test_create_DefaultLengthIsAnHour_AndEndOrTimeZoneAreUsed(env):
    _create(env)
    assert _sent_event(env, "POST")["dateandtime"]["end"] == "20261120T110000"
    _create(env, "--end", "2026-11-20 12:15", "--time-zone", "Europe/Copenhagen")
    assert _sent_event(env, "POST")["dateandtime"] == {"start": "20261120T100000", "end": "20261120T121500",
                                                       "timezone": "Europe/Copenhagen"}


@pytest.mark.parametrize("args", [("--start", "tomorrow"), ("--end", "2026-11-20 09:00"),
                                  ("--end", "2026-11-20 11:00", "--duration", "5"), ("--time-zone", "Mars/Base"),
                                  ("--reminder", "sms:5"), ("--reminder", "email:soon"), ("--repeat", "daily"),
                                  ("--invite", "nobody")])
def test_create_BadInput_IsUsageErrorBeforeAnyCall(env, args):
    base = {"--title": "x", "--start": "2026-11-20 10:00"}
    if args[0] == "--start":
        base["--start"] = args[1]
        args = ()
    flat = [v for kv in base.items() for v in kv]
    result = env.run("event", "create", *flat, *args)
    assert result.exit_code == 2 and env.zoho.calendar.calls == [] and env.zoho.token_requests == 0


def test_create_Invite_NotifiesAndReadsTheAnswersBack(env):
    data = _create(env, "--invite", OTHER)
    sent = _sent_event(env, "POST")
    assert sent["attendees"] == [{"email": OTHER, "permission": 1, "attendance": 1}] and sent["notify_attendee"] == 1
    assert {"email": OTHER, "status": "NEEDS-ACTION"} in data["attendees"]


def test_create_InviteeZohoDrops_ExitsOneNamingThemAndKeepsTheEvent(env):
    result = env.run("event", "create", "--title", "x", "--start", "2026-11-20 10:00", "--invite",
                     f"{OTHER},{OUTSIDE}")
    assert result.exit_code == 1
    assert f"Zoho dropped {OUTSIDE}" in result.output and "dt-zoho event delete" in result.output
    assert len(env.zoho.calendar.events[DEFAULT_CAL]) == 1


def test_create_InviteeNotAllowed_IsRefusedBeforeAnyCall(env):
    env.ok("allowed-recipients", "set", ME, OTHER)
    result = env.run("event", "create", "--title", "x", "--start", "2026-11-20 10:00", "--invite", "x@example.org")
    assert result.exit_code == 1 and "x@example.org" in result.output and env.zoho.calendar.calls == []


def test_create_Reminders(env):
    data = _create(env, "--reminder", "email:30", "--reminder", "popup:10")
    assert _sent_event(env, "POST")["reminders"] == [{"action": "email", "minutes": -30},
                                                     {"action": "popup", "minutes": -10}]
    assert data["reminders"] == [{"action": "email", "minutes_before": 30}, {"action": "popup", "minutes_before": 10}]


def test_create_Repeat_SendsTheRuleWithATimeZone(env):
    data = _create(env, "--repeat", "freq=daily;count=3")
    sent = _sent_event(env, "POST")
    assert sent["isrep"] is True and sent["rrule"] == "FREQ=DAILY;COUNT=3" and sent["dateandtime"]["timezone"]
    assert data["repeats"] == "FREQ=DAILY;COUNT=3"


def test_create_Meeting_PrintsTheJoinAndStartLinks(env):
    data = _create(env, "--meeting")
    assert _sent_event(env, "POST")["conference"] == "zmeeting"
    assert data["meeting_join_link"].startswith("https://meet.zohocloud.ca/")
    assert data["meeting_start_link"].startswith("https://meeting.zohocloud.ca/meeting/meeting-start")
    text = env.ok("event", "create", "--title", "y", "--start", "2026-11-21 10:00", "--meeting").stdout
    assert "meeting_join_link: https://meet.zohocloud.ca/" in text and "meeting_start_link: https://" in text


def test_create_MeetingWithInvitee_KeepsTheLinks(env):
    data = _create(env, "--meeting", "--invite", OTHER)
    assert data["meeting_join_link"] and data["meeting_start_link"]


# --------------------------------------------------------------------------------------------- read


def test_list_EarliestFirst_AndLongSpansAskedForInParts(env):
    _create(env)
    env.ok("event", "create", "--title", "Early", "--start", "2026-11-19 08:00")
    env.ok("event", "create", "--title", "Far", "--start", "2027-01-10 08:00")
    data = json.loads(env.ok("event", "list", "--from", "2026-11-01", "--to", "2027-01-31", "--json").stdout)
    assert [e["title"] for e in data["events"]] == ["Early", "Plan", "Far"]
    windows = [json.loads(c[2]["range"]) for c in _calls(env, "GET") if "range" in c[2]]
    assert len(windows) == 3


def test_list_TextShowsAttendeeCount_AndFieldsWithJsonIsRefused(env):
    _create(env, "--invite", OTHER)
    text = env.ok("event", "list", "--from", "2026-11-20", "--to", "2026-11-20", "--fields", "title,attendees").stdout
    assert "Plan,2" in text
    assert env.run("event", "list", "--fields", "title", "--json").exit_code == 2


def test_list_ToBeforeFrom_IsUsageError(env):
    assert env.run("event", "list", "--from", "2026-11-20", "--to", "2026-11-01").exit_code == 2


def test_show_ReadsInviteesRemindersAndLink(env):
    data = _create(env, "--invite", OTHER, "--reminder", "email:15")
    shown = json.loads(env.ok("event", "show", data["uid"], "--json").stdout)
    assert shown["attendees"] == data["attendees"] and shown["reminders"] == [{"action": "email", "minutes_before": 15}]


def test_respond_SaysZohoCannotAndNamesDtCalendar(env):
    result = env.run("event", "respond", "x", "--as", "accepted")
    assert result.exit_code == 2 and "dt-calendar respond" in result.output and env.zoho.calendar.calls == []


# --------------------------------------------------------------------------------------------- change


def test_change_SendsEveryFieldAgainWithTheEtag(env):
    data = _create(env, "--duration", "30", "--location", "Room 1", "--invite", OTHER, "--reminder", "email:30")
    changed = json.loads(env.ok("event", "change", data["uid"], "--title", "Plan B", "--start", "2026-11-20 11:00",
                                "--json").stdout)
    sent = _sent_event(env, "PUT")
    assert sent["etag"] == data["etag"]
    assert sent["dateandtime"] == {"start": "20261120T110000", "end": "20261120T113000",
                                   "timezone": "America/Toronto"}
    assert (sent["title"], sent["location"]) == ("Plan B", "Room 1")
    assert sent["attendees"] == [{"email": OTHER, "permission": 1, "attendance": 1}]
    assert sent["reminders"] == [{"action": "email", "minutes": -30}]
    assert changed["title"] == "Plan B" and changed["start"] == "2026-11-20T11:00:00-05:00"


def test_change_RemoveLocationAndReminders_AndUninvite(env):
    data = _create(env, "--location", "Room 1", "--invite", OTHER, "--reminder", "email:30")
    env.ok("event", "change", data["uid"], "--location", "", "--no-reminders", "--uninvite", OTHER)
    sent = _sent_event(env, "PUT")
    assert "location" not in sent and sent["reminders"] == [] and "attendees" not in sent


def test_change_InviteSomeoneNotAllowed_IsRefusedAndNothingChanged(env):
    data = _create(env)
    env.ok("allowed-recipients", "set", ME, OTHER)
    result = env.run("event", "change", data["uid"], "--invite", "x@example.org")
    assert result.exit_code == 1 and _calls(env, "PUT") == []


def test_change_ExistingInviteeNotAllowedAnyMore_IsRefused(env):
    # A change notifies every invitee again, so all of them are checked, not only the new ones.
    data = _create(env, "--invite", OTHER)
    env.ok("allowed-recipients", "set", ME)
    result = env.run("event", "change", data["uid"], "--title", "again")
    assert result.exit_code == 1 and OTHER in result.output and _calls(env, "PUT") == []


def test_change_RepeatingWithoutOccurrence_IsRefused(env):
    data = _create(env, "--repeat", "FREQ=DAILY;COUNT=3")
    result = env.run("event", "change", data["uid"], "--title", "x")
    assert result.exit_code == 2 and "--occurrence" in result.output and _calls(env, "PUT") == []


def test_change_MeetingEvent_IsRefused(env):
    data = _create(env, "--meeting")
    result = env.run("event", "change", data["uid"], "--title", "x")
    assert result.exit_code == 2 and "--meeting" in result.output and _calls(env, "PUT") == []


def test_occurrence_ChangeOneThenDeleteOneThenTheSeries(env):
    data = _create(env, "--repeat", "FREQ=DAILY;COUNT=3", "--duration", "30")
    listed = json.loads(env.ok("event", "list", "--from", "2026-11-19", "--to", "2026-11-25", "--instances",
                               "--json").stdout)["events"]
    assert [e["occurrence"] for e in listed] == ["20261120T150000Z", "20261121T150000Z", "20261122T150000Z"]
    env.ok("event", "change", data["uid"], "--occurrence", "20261121T150000Z", "--title", "Moved",
           "--start", "2026-11-21 12:00")
    sent = _sent_event(env, "PUT")
    assert (sent["recurrenceid"], sent["recurrence_edittype"], sent["isrep"]) == ("20261121T150000Z", "only", True)
    assert sent["dateandtime"] == {"start": "20261121T120000", "end": "20261121T123000",
                                   "timezone": "America/Toronto"}
    after = json.loads(env.ok("event", "list", "--from", "2026-11-19", "--to", "2026-11-25", "--instances",
                              "--json").stdout)["events"]
    assert [(e["title"], e["start"]) for e in after] == [("Plan", "2026-11-20T10:00:00-05:00"),
                                                        ("Moved", "2026-11-21T12:00:00-05:00"),
                                                        ("Plan", "2026-11-22T10:00:00-05:00")]
    # Zoho now answers the event's own GET with no etag: the list call carries it.
    refused = env.run("event", "delete", data["uid"], "--occurrence", "20261122T150000Z")
    assert refused.exit_code == 2 and "--date" in refused.output
    env.ok("event", "delete", data["uid"], "--occurrence", "20261122T150000Z", "--date", "2026-11-20")
    left = json.loads(env.ok("event", "list", "--from", "2026-11-19", "--to", "2026-11-25", "--instances",
                             "--json").stdout)["events"]
    assert [e["title"] for e in left] == ["Plan", "Moved"]
    env.ok("event", "delete", data["uid"], "--date", "2026-11-20")
    assert env.zoho.calendar.events[DEFAULT_CAL] == {}


def test_change_OccurrenceTitleOnly_KeepsThatOccurrencesTime(env):
    data = _create(env, "--repeat", "FREQ=DAILY;COUNT=3", "--duration", "30")
    env.ok("event", "change", data["uid"], "--occurrence", "20261122T150000Z", "--title", "Third")
    assert _sent_event(env, "PUT")["dateandtime"] == {"start": "20261122T100000", "end": "20261122T103000",
                                                      "timezone": "America/Toronto"}


# --------------------------------------------------------------------------------------------- delete


def test_delete_SendsTheEtagHeader(env):
    data = _create(env)
    env.ok("event", "delete", data["uid"])
    assert _calls(env, "DELETE")[-1][3]["etag"] == data["etag"]
    assert env.zoho.calendar.events[DEFAULT_CAL] == {}


def test_delete_UnknownEvent_IsOneSentence(env):
    result = env.run("event", "delete", "nope@zohocloud.ca")
    assert result.exit_code == 1 and "EVENT_NOTFOUND" in result.output and "Traceback" not in result.output
