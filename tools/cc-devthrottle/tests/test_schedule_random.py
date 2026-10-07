"""Tests for the random schedule kind on the command line (issue #3622): `schedule create --random`, and
`schedule plan`, which prints the Gateway's own plan."""

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

_BASE = [
    "schedule", "create",
    "--name", "Reddit manager - reply",
    "--machine", "SOREN_NORTH",
    "--repo", r"D:\ReposFred\devthrottle_internal",
    "--tz", "America/Toronto",
    "--seed", "do one reply run",
]


def _create(extra):
    with patch("src.schedule_ops.ScheduleClient") as client_cls:
        instance = client_cls.return_value
        instance.create_job.return_value = {
            "id": "cj_abc123",
            "name": "Reddit manager - reply",
            "scheduleKind": "random",
            "cronExpression": "window=07:00-01:00 perDay=4 minGap=45 shape=human",
            "nextRunUtc": "2026-10-08T13:12:00Z",
        }
        result = runner.invoke(app, _BASE + extra)
        posted = instance.create_job.call_args.args[0] if instance.create_job.call_args else None
    return result, posted


def test_create_random_posts_the_random_kind_and_its_settings_text():
    result, posted = _create(["--random", "07:00-01:00", "--per-day", "4", "--min-gap", "45"])

    assert result.exit_code == 0, result.output
    assert posted["scheduleKind"] == "random"
    assert posted["cronExpression"] == "window=07:00-01:00 perDay=4 minGap=45 shape=human"
    assert posted["runAt"] is None
    assert "random window=07:00-01:00 perDay=4 minGap=45 shape=human" in result.output
    assert "cc-devthrottle schedule plan cj_abc123" in result.output


def test_create_random_with_a_custom_shape_sends_the_weights():
    weights = ",".join(["1"] * 12 + ["3"] * 12)

    result, posted = _create(["--random", "07:00-01:00", "--per-day", "4", "--min-gap", "45", "--shape", weights])

    assert result.exit_code == 0, result.output
    assert posted["cronExpression"].endswith(f"shape={weights}")


def test_create_with_two_timings_is_a_usage_error_naming_all_three():
    result, posted = _create(["--cron", "0 7 * * *", "--random", "07:00-01:00", "--per-day", "4", "--min-gap", "45"])

    assert result.exit_code == 2
    assert posted is None
    assert "exactly one of --at (one-off), --cron (recurring) or --random" in result.stderr


def test_create_with_no_timing_is_a_usage_error():
    result, posted = _create([])

    assert result.exit_code == 2
    assert posted is None
    assert "exactly one of --at" in result.stderr


def test_create_random_without_per_day_or_gap_is_a_usage_error():
    result, posted = _create(["--random", "07:00-01:00", "--per-day", "4"])

    assert result.exit_code == 2
    assert posted is None
    assert "--random needs --per-day" in result.stderr


def test_random_flags_without_random_are_a_usage_error():
    result, posted = _create(["--cron", "0 7 * * *", "--per-day", "4", "--shape", "human"])

    assert result.exit_code == 2
    assert posted is None
    assert "--per-day, --shape only go with --random" in result.stderr


_PLAN = {
    "id": "cj_abc123",
    "timeZoneId": "America/Toronto",
    "description": "About 4 times a day at random, 07:00 to 01:00 (at least 45 min apart)",
    "days": 1,
    "generatedUtc": "2026-10-08T11:00:00Z",
    "fires": [
        {"windowDate": "2026-10-08", "utc": "2026-10-08T13:12:00Z", "local": "2026-10-08 09:12"},
        {"windowDate": "2026-10-08", "utc": "2026-10-08T20:47:00Z", "local": "2026-10-08 16:47"},
    ],
}


def _plan(args, answer=_PLAN):
    with patch("src.schedule_ops.ScheduleClient") as client_cls:
        instance = client_cls.return_value
        instance.get_plan.return_value = answer
        result = runner.invoke(app, ["schedule", "plan", "cj_abc123", *args])
        called = instance.get_plan.call_args
    return result, called


def test_plan_prints_every_fire_with_its_local_and_utc_time():
    result, called = _plan(["--days", "3"])

    assert result.exit_code == 0, result.output
    assert called.args == ("cj_abc123", 3)
    assert "About 4 times a day at random, 07:00 to 01:00 (at least 45 min apart) (America/Toronto)" in result.output
    fields, records = parse_list(result.output, "fires")
    assert fields == ["window-date", "local", "utc"]
    assert [r["local"] for r in records] == ["2026-10-08 09:12", "2026-10-08 16:47"]
    assert [r["utc"] for r in records] == ["2026-10-08T13:12:00Z", "2026-10-08T20:47:00Z"]


def test_plan_json_is_the_gateways_answer():
    result, _ = _plan(["--json"])

    assert result.exit_code == 0
    assert json.loads(result.output) == _PLAN


def test_plan_with_nothing_left_says_so():
    result, _ = _plan([], answer={**_PLAN, "fires": []})

    assert result.exit_code == 0
    assert "count: 0" in result.output
    assert "No fires left in the next 1 day(s)." in result.output


def test_plan_with_no_list_of_fires_fails_rather_than_reading_as_empty():
    result, _ = _plan([], answer={"id": "cj_abc123"})

    assert result.exit_code == 1
    assert "no list of fires" in result.stderr


def test_plan_days_out_of_range_is_a_usage_error_before_any_call():
    result, called = _plan(["--days", "15"])

    assert result.exit_code == 2
    assert called is None
    assert "--days must be from 1 to 14" in result.stderr
