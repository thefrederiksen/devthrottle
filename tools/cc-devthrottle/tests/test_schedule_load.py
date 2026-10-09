"""Tests for `schedule load`, which prints the Gateway's 24-hour load forecast (GET /cron/load), and for the overload
warning the Gateway stamps on a schedule just created, linked or enabled (the owner, 2026-10-09)."""

import json
import sys
from pathlib import Path
from unittest.mock import patch

from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared.axi_output import parse_list  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()


def _machine(name, peak_hour=7, peak=7):
    hours = [
        {"startUtc": f"2026-10-09T{i:02d}:00:00Z", "label": f"{i:02d}:00", "concurrent": peak if i == peak_hour else 1,
         "starts": 1, "over": i == peak_hour and peak > 6, "jobIds": ["cj_a"]}
        for i in range(24)
    ]
    return {
        "machine": name,
        "timeZoneId": "America/Toronto",
        "schedules": 12,
        "peak": peak,
        "hoursOver": 1 if peak > 6 else 0,
        "quietest": hours[0],
        "summary": f"peak {peak} at {peak_hour:02d}:00 - 1 hour over 6 - quietest 00:00 (1 open)",
        "hours": hours,
        "estimatedJobIds": [],
        "estimateNote": "",
        "unplacedJobIds": [],
        "unplacedNote": "",
    }


def _load(machines):
    return {"generatedUtc": "2026-10-09T04:10:00Z", "capacity": 6, "machines": machines}


def _run(args, load):
    with patch("src.schedule_ops.ScheduleClient") as client_cls:
        client_cls.return_value.get_load.return_value = load
        return runner.invoke(app, ["schedule", "load", *args])


def test_load_prints_each_machines_summary_and_its_24_hours_with_the_over_hour_marked():
    result = _run([], _load([_machine("SOREN_NORTH"), _machine("devlinux", peak_hour=9, peak=2)]))

    assert result.exit_code == 0, result.output
    assert "capacity: 6 scheduled sessions open at once per machine" in result.output
    assert "SOREN_NORTH (America/Toronto), 12 active schedules: peak 7 at 07:00" in result.output
    assert "devlinux (America/Toronto), 12 active schedules: peak 2 at 09:00" in result.output
    _, hours = parse_list(result.output.split("devlinux")[0], "hours")
    assert len(hours) == 24
    assert hours[7] == {"hour": "07:00", "open": "7", "starts": "1", "over": "OVER"}
    assert hours[8]["over"] == ""


def test_load_for_one_machine_shows_only_that_machine_whatever_its_case():
    result = _run(["--machine", "soren_north"], _load([_machine("SOREN_NORTH"), _machine("devlinux")]))

    assert result.exit_code == 0, result.output
    assert "SOREN_NORTH (" in result.output
    assert "devlinux (" not in result.output


def test_load_for_a_machine_with_no_active_schedules_fails_and_names_the_ones_that_have_some():
    result = _run(["--machine", "nowhere"], _load([_machine("SOREN_NORTH")]))

    assert result.exit_code != 0
    assert "no active schedules run on 'nowhere'; machines with some: SOREN_NORTH." in result.output


def test_load_prints_the_gateways_notes_about_guessed_and_unplaced_schedules():
    m = _machine("SOREN_NORTH")
    m["estimateNote"] = "2 schedules have never finished a run, so they are counted at 30 min each"
    m["unplacedNote"] = "1 schedule is not counted: its time zone is not known on this host"

    result = _run([], _load([m]))

    assert "note: 2 schedules have never finished a run, so they are counted at 30 min each" in result.output
    assert "note: 1 schedule is not counted: its time zone is not known on this host" in result.output


def test_load_answer_without_a_list_of_machines_is_never_read_as_nothing_scheduled():
    result = _run([], {"capacity": 6})

    assert result.exit_code != 0
    assert "no list of machines" in result.output


def test_load_json_is_the_gateways_answer():
    load = _load([_machine("SOREN_NORTH")])

    result = _run(["--json"], load)

    assert result.exit_code == 0, result.output
    assert json.loads(result.output) == load


_CREATE = [
    "schedule", "create",
    "--name", "Morning digest",
    "--machine", "SOREN_NORTH",
    "--repo", r"D:\ReposFred\devthrottle",
    "--cron", "0 7 * * *",
    "--tz", "America/Toronto",
    "--seed", "write the digest",
]

_WARNING = ("SOREN_NORTH will have 7 scheduled sessions open at 07:00 with this one, over its capacity of 6. "
            "The quietest hour is 21:00 (0 open) - see cc-devthrottle schedule load --machine SOREN_NORTH.")


def _create(load_warning):
    with patch("src.schedule_ops.ScheduleClient") as client_cls:
        client_cls.return_value.create_job.return_value = {
            "id": "cj_new", "name": "Morning digest", "scheduleKind": "recurring", "cronExpression": "0 7 * * *",
            "nextRunUtc": "2026-10-09T11:00:00Z", "loadWarning": load_warning,
        }
        return runner.invoke(app, _CREATE)


def test_create_into_a_full_hour_is_saved_and_prints_the_gateways_warning():
    result = _create(_WARNING)

    assert result.exit_code == 0, result.output
    assert "Created schedule." in result.output
    assert f"WARNING:   {_WARNING}" in result.output


def test_create_into_an_hour_that_fits_prints_no_warning():
    result = _create(None)

    assert result.exit_code == 0, result.output
    assert "WARNING" not in result.output


def test_enable_into_a_full_hour_prints_the_warning():
    with patch("src.schedule_ops.ScheduleClient") as client_cls:
        client_cls.return_value.set_enabled.return_value = {"id": "cj_a", "name": "Digest", "loadWarning": _WARNING}
        result = runner.invoke(app, ["schedule", "enable", "cj_a"])

    assert result.exit_code == 0, result.output
    assert f"WARNING:   {_WARNING}" in result.output
