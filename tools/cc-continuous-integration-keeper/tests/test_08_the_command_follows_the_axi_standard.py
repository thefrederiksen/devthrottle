"""The command surface, against docs/axi-standard.md.

The rules that do not bend there: --json keeps its shape and every filter applies to it too; an
identifier or a name is never cut short; an empty result says so; an unknown flag or an unknown
field fails loudly with the valid values; the output is ASCII only; and every subcommand has a
short help.
"""

from __future__ import annotations

import io
import json

import pytest
from cc_shared import axi_output

import cli
from conftest import REAL_WEEK, REPOSITORY_BUDGET

RED_TEST = (
    "CcDirector.Gateway.Tests.FleetManagerRoutesHostTests."
    "The_session_list_pins_the_Fleet_Manager_and_offers_each_row_its_change_of_owner"
)


def run(*arguments):
    out, err = io.StringIO(), io.StringIO()
    code = cli.main(list(arguments), out, err)
    return code, out.getvalue(), err.getvalue()


def check_the_real_week(*extra):
    return run("check", "--budget", str(REPOSITORY_BUDGET), "--records", str(REAL_WEEK), *extra)


def test_every_finding_can_be_read_back_out_of_the_default_output():
    """The recoverability test the standard asks for: the full name of every test raised is in the
    default output, uncut, and reads back exactly. A name an agent cannot read back is a name it
    cannot act on."""
    code, text, _ = check_the_real_week()
    assert code == 3
    fields, rows = axi_output.parse_list(text, name="findings")
    assert fields == ["condition", "subject", "measured"]
    assert len(rows) == 10
    subjects = [row["subject"] for row in rows]
    assert RED_TEST in subjects
    assert all(subject and "..." not in subject for subject in subjects)


def test_the_conditions_and_the_readings_read_back_too():
    _, text, _ = check_the_real_week()
    _, conditions = axi_output.parse_list(text, name="conditions")
    assert [row["condition"] for row in conditions] == [
        "budget",
        "suite-growth",
        "repeated-red-test",
        "skipped-job",
    ]
    assert [row["status"] for row in conditions] == ["raised", "raised", "raised", "clear"]
    _, readings = axi_output.parse_list(text, name="readings")
    assert dict((row["what"], row["value"]) for row in readings)["finished runs"] == "231"


def test_the_count_line_adds_up_and_names_every_condition():
    _, text, _ = check_the_real_week()
    assert "count: 10 (budget 1, suite-growth 1, repeated-red-test 8, skipped-job 0)" in text


def test_long_evidence_is_shortened_with_a_hint_and_full_gives_it_back():
    _, short, _ = check_the_real_week()
    _, whole, _ = check_the_real_week("--full")
    assert "use --full" in short
    assert "use --full" not in whole
    assert len(whole) > len(short)


def test_a_filter_applies_to_json_as_well_as_to_the_text():
    """A filter that is silently ignored under --json is a defect: an agent combines them at once
    and pays for the whole fleet coming back."""
    code, text, _ = check_the_real_week("--condition", "repeated-red-test", "--json")
    assert code == 3
    answer = json.loads(text)
    assert [condition["condition"] for condition in answer["conditions"]] == ["repeated-red-test"]
    assert answer["raised"] == 8
    assert any(finding["subject"] == RED_TEST for finding in answer["conditions"][0]["findings"])


def test_json_without_a_filter_carries_every_condition_and_every_field():
    _, text, _ = check_the_real_week("--json")
    answer = json.loads(text)
    assert [condition["condition"] for condition in answer["conditions"]] == [
        "budget",
        "suite-growth",
        "repeated-red-test",
        "skipped-job",
    ]
    assert answer["repository"] == "thefrederiksen/devthrottle"
    assert answer["window"] == {"from": "2026-09-16T00:00:00Z", "to": "2026-09-20T00:00:00Z"}
    finding = answer["conditions"][0]["findings"][0]
    assert sorted(finding) == ["condition", "measured", "proposal", "subject", "threshold"]
    # Nothing is shortened under --json. It is a machine format.
    assert "use --full" not in text
    assert [reading["what"] for reading in answer["readings"]][0] == "runs in the window"


def test_more_fields_can_be_asked_for_by_name():
    _, text, _ = check_the_real_week("--fields", "condition,subject,threshold,proposal")
    fields, rows = axi_output.parse_list(text, name="findings")
    assert fields == ["condition", "subject", "threshold", "proposal"]
    assert rows[0]["proposal"]


def test_an_unknown_field_fails_with_the_fields_that_do_exist():
    code, _, err = check_the_real_week("--fields", "condition,when")
    assert code == 2
    assert "unknown field for --fields: when" in err
    assert "Valid fields: condition, subject, measured, threshold, proposal" in err


def test_an_unknown_condition_fails_with_the_conditions_that_do_exist():
    code, _, err = check_the_real_week("--condition", "speed")
    assert code == 2
    assert "budget, suite-growth, repeated-red-test, skipped-job" in err


def test_an_unknown_flag_fails_rather_than_being_ignored():
    code, _, err = check_the_real_week("--quick")
    assert code == 2
    assert "unrecognized arguments: --quick" in err


def test_records_and_repository_together_are_a_usage_error():
    code, _, err = run(
        "check", "--budget", str(REPOSITORY_BUDGET), "--records", str(REAL_WEEK),
        "--repository", "an-owner/a-repository",
    )
    assert code == 2
    assert "exactly one of --records" in err


def test_neither_records_nor_repository_is_a_usage_error():
    code, _, err = run("check", "--budget", str(REPOSITORY_BUDGET))
    assert code == 2
    assert "exactly one of --records" in err


def test_no_command_at_all_shows_what_the_commands_are():
    code, _, err = run()
    assert code == 2
    assert "check --budget" in err
    assert "collect --repository" in err


def test_the_version_is_answered_the_way_the_tools_page_asks_for_it():
    code, text, _ = run("--version")
    assert code == 0
    assert text.startswith("cc-continuous-integration-keeper ")
    code, text, _ = run("--version", "--json")
    assert code == 0
    assert json.loads(text)["tool"] == "cc-continuous-integration-keeper"


def test_every_subcommand_has_a_short_help():
    for command in ("check", "collect"):
        with pytest.raises(SystemExit) as caught:
            cli.build_parser().parse_args([command, "--help"])
        assert caught.value.code == 0


def test_the_output_is_ascii_only():
    for arguments in ((), ("--json",), ("--full",)):
        _, text, _ = check_the_real_week(*arguments)
        text.encode("ascii")


def test_the_help_lines_offer_real_next_commands_with_placeholders():
    _, text, _ = check_the_real_week()
    assert "help[3]:" in text
    assert "--records <file> --condition <name>" in text
    assert "collect --repository thefrederiksen/devthrottle" in text
