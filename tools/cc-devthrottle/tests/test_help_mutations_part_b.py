"""AXI step 6, part B (#2922): next steps after mutations, short help, and errors an agent can act on.

Covers the command groups schedule, workflow, skill, settings, setup, email, diag, autostart and
browser, and the top-level `actions` command. The rules are in docs/axi-standard.md:

- A command that changes something ends its plain output with `help[N]:` next commands. A value is
  filled in only when the command's own result supplied it; everything else is a placeholder.
- Every command and group has a one-line `--help` summary that fits one row of its group's list.
- An error goes to standard error as `Error: ...` plus `help[N]:` next steps, and exits non-zero.
- `--json` output never changes.
"""

import json
import sys
from pathlib import Path
from unittest.mock import patch

import pytest
import typer
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared import axi_output  # noqa: E402
from cc_shared.config import CCDirectorConfig  # noqa: E402
from src import axi_cli, browser_ops, setup_ops, settings_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()


# ---------------------------------------------------------------------------------------------------
# Reading the output back
# ---------------------------------------------------------------------------------------------------


def _help_block(text: str) -> list:
    """The commands of the `help[N]:` block that ENDS `text`. Fails when there is none, when the count
    is wrong, or when anything follows the block."""
    lines = text.rstrip("\n").split("\n")
    headers = [i for i, line in enumerate(lines) if line.startswith("help[") and line.endswith("]:")]
    assert headers, f"no help block in:\n{text}"
    start = headers[-1]
    count = int(lines[start][len("help["):-2])
    commands = lines[start + 1:]
    assert len(commands) == count, f"help[{count}] is followed by {len(commands)} lines:\n{text}"
    for command in commands:
        assert command.startswith("  ") and not command.startswith("   "), command
    return [command[2:] for command in commands]


def _assert_error(result, *expected_help, exit_code=1):
    """An error: nothing on standard output, `Error: ...` and next steps on standard error."""
    assert result.exit_code == exit_code, (result.exit_code, result.stdout, result.stderr)
    assert result.stdout == "", result.stdout
    # A progress note (setup's "Delegating to setup engine") may come first; the error comes last.
    errors = [line for line in result.stderr.split("\n") if line.startswith("Error: ")]
    assert len(errors) == 1, result.stderr
    assert result.stderr.split("\n")[0] == errors[0] or result.stderr.startswith("Delegating "), result.stderr
    assert "Traceback" not in result.stderr
    assert result.stderr.isascii()
    commands = _help_block(result.stderr)
    for wanted in expected_help:
        assert wanted in commands, (wanted, commands)
    return result.stderr


def _assert_plain_with_help(result, *expected_help):
    assert result.exit_code == 0, (result.stdout, result.stderr)
    assert result.stdout.isascii(), result.stdout
    commands = _help_block(result.stdout)
    assert commands == list(expected_help), commands
    return commands


def _assert_json_unchanged(result, payload):
    """`--json` prints exactly what it always printed: the answer, indented, and nothing else."""
    assert result.exit_code == 0, (result.stdout, result.stderr)
    assert result.stdout == json.dumps(payload, indent=2) + "\n"
    assert "help[" not in result.stdout


# ---------------------------------------------------------------------------------------------------
# Short help for every command and group
# ---------------------------------------------------------------------------------------------------

# The groups this part owns. Part A (branch axi/help-mutations-a) adds session, message, mission,
# repo, worktree, director, machine and the root app; when both have landed this becomes the whole tree.
SHORT_HELP_GROUPS = (
    "actions",
    "schedule",
    "workflow",
    "skill",
    "settings",
    "setup",
    "email",
    "diag",
    "autostart",
    "browser",
)

# An 80-column terminal gives a command list this much room, less the longest name beside it.
_LIST_ROW_ROOM = 74


def _summary(command) -> str:
    return (command.help or "").strip().split("\n\n")[0]


def _short_help_problems(group, path, only=None):
    problems = []
    limit = _LIST_ROW_ROOM - max(len(name) for name in group.commands)
    for name, sub in group.commands.items():
        if only is not None and name not in only:
            continue
        where = " ".join(path + [name])
        summary = _summary(sub)
        if not summary:
            problems.append(f"{where}: no help")
        elif "\n" in summary:
            problems.append(f"{where}: the first paragraph runs over more than one line")
        elif len(summary) > limit:
            problems.append(f"{where}: {len(summary)} characters, more than the {limit} that fit one row")
        elif not summary.isascii() or not summary.endswith((".", "?")):
            problems.append(f"{where}: not one ASCII sentence: {summary!r}")
        if hasattr(sub, "commands"):
            problems.extend(_short_help_problems(sub, path + [name]))
    return problems


def test_every_command_and_group_has_a_short_help():
    root = typer.main.get_command(app)
    missing = [name for name in SHORT_HELP_GROUPS if name not in root.commands]
    assert not missing, f"groups not found: {missing}"
    problems = _short_help_problems(root, [], only=set(SHORT_HELP_GROUPS))
    assert not problems, "\n".join(problems)


def test_short_help_check_catches_a_long_or_missing_summary():
    long_line = typer.Typer()

    @long_line.command("fine")
    def _fine() -> None:
        """Short."""

    @long_line.command("wordy")
    def _wordy() -> None:
        """This summary is far too long to fit on a single row of an eighty column command list at all."""

    @long_line.command("bare")
    def _bare() -> None:
        pass

    problems = _short_help_problems(typer.main.get_command(long_line), ["x"])
    assert any(p.startswith("x wordy:") for p in problems), problems
    assert any(p.startswith("x bare: no help") for p in problems), problems
    assert not any(p.startswith("x fine") for p in problems), problems


# ---------------------------------------------------------------------------------------------------
# The helper
# ---------------------------------------------------------------------------------------------------


class TestAxiCli:
    def test_bare_keeps_identifiers_and_refuses_anything_a_shell_would_read(self):
        assert axi_cli.bare("cj_abc-123.x", "<id>") == "cj_abc-123.x"
        for unsafe in ["", "a b", "a;b", "$(x)", "a'b", "-rf", None, 7, "caf\u00e9"]:
            assert axi_cli.bare(unsafe, "<id>") == "<id>"

    def test_quoted_wraps_names_and_refuses_what_double_quotes_do_not_protect(self):
        assert axi_cli.quoted("Center Consulting", "<name>") == '"Center Consulting"'
        for unsafe in ["", " padded", 'a"b', "a$b", "a`b", "a\\b", "a!b", "line\nbreak", "caf\u00e9", None]:
            assert axi_cli.quoted(unsafe, "<name>") == '"<name>"'

    def test_fail_refuses_an_error_without_a_next_step(self):
        with pytest.raises(ValueError):
            axi_cli.fail("broken", [])
        with pytest.raises(ValueError):
            axi_cli.fail("broken", ["cc-devthrottle x"], exit_code=0)

    def test_fail_writes_markup_and_non_ascii_verbatim_but_escaped(self, capsys):
        with pytest.raises(typer.Exit) as exc:
            axi_cli.fail("bad [bold]thing[/bold] caf\u00e9", ["cc-devthrottle schedule list"])
        assert exc.value.exit_code == 1
        captured = capsys.readouterr()
        assert captured.out == ""
        assert captured.err == (
            "Error: bad [bold]thing[/bold] caf\\u00e9\nhelp[1]:\n  cc-devthrottle schedule list\n"
        )

    def test_confirm_asks_a_person_at_a_terminal(self, monkeypatch):
        class _Terminal:
            def isatty(self):
                return True

        monkeypatch.setattr(axi_cli.sys, "stdin", _Terminal())
        monkeypatch.setattr(axi_cli.typer, "confirm", lambda prompt: False)
        assert axi_cli.confirm_or_fail("Sure?", False, "--yes", "cc-devthrottle x --yes") is False
        assert axi_cli.confirm_or_fail("Sure?", True, "--yes", "cc-devthrottle x --yes") is True


# ---------------------------------------------------------------------------------------------------
# actions
# ---------------------------------------------------------------------------------------------------


def test_actions_lists_every_id_and_command_in_full():
    from src.cli import _ACTIONS

    result = runner.invoke(app, ["actions"])
    assert result.exit_code == 0
    assert result.stdout.isascii()
    changing = sum(1 for a in _ACTIONS if a["mutatesState"])
    assert result.stdout.startswith(
        f"count: {len(_ACTIONS)} (changes-state {changing}, read-only {len(_ACTIONS) - changing})\n"
    )
    fields, records = axi_output.parse_list(result.stdout, "actions")
    assert fields == ["id", "command", "changes-state"]
    assert [(r["id"], r["command"], r["changes-state"]) for r in records] == [
        (a["id"], a["command"], "yes" if a["mutatesState"] else "no") for a in _ACTIONS
    ]
    assert _help_block(result.stdout) == ["cc-devthrottle actions --json", "cc-devthrottle <group> <command> --help"]


def test_actions_json_is_unchanged():
    from src.cli import _ACTIONS

    _assert_json_unchanged(runner.invoke(app, ["actions", "--json"]), {"actions": _ACTIONS})


# ---------------------------------------------------------------------------------------------------
# schedule
# ---------------------------------------------------------------------------------------------------

_CREATE = [
    "schedule", "create", "--name", "nightly", "--machine", "m1", "--repo", "/r",
    "--cron", "0 0 * * *", "--tz", "UTC", "--seed", "/help",
]
_JOB = {"id": "cj_abc123", "name": "nightly", "nextRunUtc": "2026-06-28T05:00:00Z", "enabled": True}
_RUN = {"firedUtc": "2026-06-28T05:00:00Z", "targetDirectorId": "d1", "sessionId": "s-1",
        "infraStatus": "ok", "taskStatus": "running"}


@pytest.fixture
def schedule_client():
    with patch("src.schedule_ops.ScheduleClient") as client_cls:
        instance = client_cls.return_value
        instance.base_url = "http://gateway.invalid"
        instance.create_job.return_value = dict(_JOB)
        instance.run_now.return_value = dict(_RUN)
        instance.set_enabled.return_value = dict(_JOB)
        instance.delete_job.return_value = {}
        yield instance


class TestScheduleMutations:
    def test_create_ends_with_next_steps_for_the_new_id(self, schedule_client):
        result = runner.invoke(app, _CREATE)
        assert "Created schedule." in result.stdout
        assert "Id:        cj_abc123" in result.stdout
        _assert_plain_with_help(
            result,
            "cc-devthrottle schedule get cj_abc123",
            "cc-devthrottle schedule run cj_abc123",
            "cc-devthrottle schedule disable cj_abc123",
        )

    def test_create_with_an_unsafe_id_uses_the_placeholder(self, schedule_client):
        schedule_client.create_job.return_value = {"id": "bad id; rm", "name": "n"}
        result = runner.invoke(app, _CREATE)
        commands = _assert_plain_with_help(
            result,
            "cc-devthrottle schedule get <schedule-id>",
            "cc-devthrottle schedule run <schedule-id>",
            "cc-devthrottle schedule disable <schedule-id>",
        )
        assert all("rm" not in c for c in commands)

    def test_create_json_is_unchanged(self, schedule_client):
        _assert_json_unchanged(runner.invoke(app, _CREATE + ["--json"]), _JOB)

    def test_run_names_the_schedule_and_the_session_it_started(self, schedule_client):
        result = runner.invoke(app, ["schedule", "run", "cj_abc123"])
        assert "Fired the schedule." in result.stdout
        _assert_plain_with_help(
            result, "cc-devthrottle schedule runs cj_abc123", "cc-devthrottle session buffer s-1"
        )

    def test_run_without_a_session_uses_the_placeholder(self, schedule_client):
        schedule_client.run_now.return_value = dict(_RUN, sessionId=None)
        result = runner.invoke(app, ["schedule", "run", "cj_abc123"])
        _assert_plain_with_help(
            result, "cc-devthrottle schedule runs cj_abc123", "cc-devthrottle session buffer <session-id>"
        )

    def test_run_json_is_unchanged(self, schedule_client):
        _assert_json_unchanged(runner.invoke(app, ["schedule", "run", "cj_abc123", "--json"]), _RUN)

    def test_enable(self, schedule_client):
        result = runner.invoke(app, ["schedule", "enable", "cj_abc123"])
        assert result.stdout.startswith("Enabled nightly (cj_abc123).\n")
        _assert_plain_with_help(
            result, "cc-devthrottle schedule get cj_abc123", "cc-devthrottle schedule disable cj_abc123"
        )

    def test_disable(self, schedule_client):
        result = runner.invoke(app, ["schedule", "disable", "cj_abc123"])
        assert result.stdout.startswith("Disabled nightly (cj_abc123).\n")
        _assert_plain_with_help(
            result, "cc-devthrottle schedule enable cj_abc123", "cc-devthrottle schedule delete cj_abc123"
        )

    def test_delete(self, schedule_client):
        result = runner.invoke(app, ["schedule", "delete", "cj_abc123"])
        assert result.stdout.startswith("Deleted schedule cj_abc123.\n")
        commands = _help_block(result.stdout)
        assert commands[0] == "cc-devthrottle schedule list"
        assert commands[1].startswith('cc-devthrottle schedule create --name "<name>"')


class TestScheduleErrors:
    @pytest.mark.parametrize(
        "args",
        [
            ["schedule", "list"],
            ["schedule", "get", "x"],
            ["schedule", "runs", "x"],
            ["schedule", "run", "x"],
            ["schedule", "enable", "x"],
            ["schedule", "disable", "x"],
            ["schedule", "delete", "x"],
            _CREATE,
        ],
    )
    def test_a_gateway_failure_is_an_actionable_error(self, schedule_client, args):
        from src.schedule_ops import GatewayError

        for method in ("list_jobs", "get_job", "list_runs", "run_now", "set_enabled", "delete_job", "create_job"):
            getattr(schedule_client, method).side_effect = GatewayError("job [x] not found")
        stderr = _assert_error(runner.invoke(app, args), "cc-devthrottle schedule endpoint")
        assert stderr.startswith("Error: job [x] not found\n")

    @pytest.mark.parametrize(
        "extra, message",
        [
            (["--at", "2026-01-01T00:00"], "exactly one of --at"),
            (["--worklist", "w"], "only one of --seed or --worklist"),
            (["--notify-on", "sometimes"], "--notify-on must be one of none, always, failure, not 'sometimes'"),
        ],
    )
    def test_create_flag_mistakes_are_usage_errors(self, schedule_client, extra, message):
        stderr = _assert_error(
            runner.invoke(app, _CREATE + extra), "cc-devthrottle schedule create --help", exit_code=2
        )
        assert message in stderr
        schedule_client.create_job.assert_not_called()

    def test_create_without_anything_to_run_is_a_usage_error(self, schedule_client):
        args = [a for a in _CREATE if a not in ("--seed", "/help")]
        stderr = _assert_error(runner.invoke(app, args), exit_code=2)
        assert "--seed <text> or --worklist <name>" in stderr

    def test_endpoint_without_a_gateway_is_a_sentence_not_a_traceback(self, monkeypatch):
        monkeypatch.delenv("CC_GATEWAY_URL")
        stderr = _assert_error(runner.invoke(app, ["schedule", "endpoint"]), "cc-devthrottle setup status")
        assert "CC_GATEWAY_URL is not set" in stderr

    def test_an_unreachable_gateway_names_the_flag_that_is_actually_read(self):
        # The schedule client reads CC_GATEWAY_URL or --gateway; the old message sent people to a
        # config setting this client no longer reads.
        with patch("src.schedule_ops.requests.request", side_effect=__import__("requests").ConnectionError()):
            stderr = _assert_error(runner.invoke(app, ["schedule", "list"]))
        assert "--gateway <url>" in stderr
        assert "settings set gateway.url" not in stderr


# ---------------------------------------------------------------------------------------------------
# workflow and skill
# ---------------------------------------------------------------------------------------------------


@pytest.fixture
def workflow_client():
    with patch("src.workflow_ops.WorkflowClient") as client_cls:
        yield client_cls.return_value


@pytest.fixture
def skill_client():
    with patch("src.skill_ops.SkillClient") as client_cls:
        yield client_cls.return_value


class TestWorkflowMutations:
    def test_push_new_workflow_points_at_publish(self, workflow_client, tmp_path):
        workflow_client.workflow_exists.return_value = False
        workflow_client.create.return_value = {"version": 1, "contentHash": "h1"}
        result = runner.invoke(app, ["workflow", "push", "my-flow", "--dir", str(tmp_path)])
        assert "Created draft v1 of 'my-flow'." in result.stdout
        _assert_plain_with_help(
            result, "cc-devthrottle workflow publish my-flow", "cc-devthrottle workflow versions my-flow"
        )

    def test_publish(self, workflow_client):
        workflow_client.publish.return_value = {"version": 4}
        result = runner.invoke(app, ["workflow", "publish", "my-flow"])
        assert "Published 'my-flow' v4." in result.stdout
        _assert_plain_with_help(
            result, "cc-devthrottle workflow instructions my-flow", "cc-devthrottle workflow versions my-flow"
        )

    def test_clone_uses_the_id_the_gateway_returned(self, workflow_client):
        workflow_client.clone.return_value = {"id": "my-mission", "version": 1}
        result = runner.invoke(app, ["workflow", "clone", "mission", "my-mission"])
        _assert_plain_with_help(
            result,
            'cc-devthrottle workflow pull my-mission --dir "<dir>"',
            "cc-devthrottle workflow show my-mission",
        )

    def test_enable_and_disable(self, workflow_client):
        on = runner.invoke(app, ["workflow", "enable", "my-flow"])
        _assert_plain_with_help(
            on, "cc-devthrottle workflow show my-flow", "cc-devthrottle workflow disable my-flow"
        )
        off = runner.invoke(app, ["workflow", "disable", "my-flow"])
        _assert_plain_with_help(off, "cc-devthrottle workflow enable my-flow", "cc-devthrottle workflow list")

    def test_delete_with_yes(self, workflow_client):
        result = runner.invoke(app, ["workflow", "delete", "my-flow", "--yes"])
        assert result.stdout.startswith("Archived 'my-flow'.\n")
        _assert_plain_with_help(
            result, "cc-devthrottle workflow list", 'cc-devthrottle workflow push <new-id> --dir "<dir>"'
        )

    def test_delete_without_yes_and_no_terminal_refuses_instead_of_prompting(self, workflow_client):
        stderr = _assert_error(
            runner.invoke(app, ["workflow", "delete", "my-flow"]),
            "cc-devthrottle workflow delete my-flow --yes",
            exit_code=2,
        )
        assert "--yes" in stderr
        workflow_client.delete.assert_not_called()

    def test_pull_points_at_push_with_the_directory(self, workflow_client, tmp_path):
        workflow_client.list_versions.return_value = [{"version": 2, "status": "published"}]
        workflow_client.get_version_detail.return_value = {
            "version": 2, "status": "published", "instructionsMarkdown": "# hi", "contentHash": "h2",
            "files": [{"fileName": "a.sh", "content": "echo"}],
        }
        target = tmp_path / "my dir"
        result = runner.invoke(app, ["workflow", "pull", "my-flow", "--dir", str(target)])
        _assert_plain_with_help(result, f'cc-devthrottle workflow push my-flow --dir "{target}"')
        assert (target / "helpers" / "a.sh").read_text() == "echo"

    def test_pull_refuses_an_unsafe_helper_name_before_writing_anything(self, workflow_client, tmp_path):
        workflow_client.get_version_detail.return_value = {
            "version": 2, "files": [{"fileName": "ok.sh", "content": "x"}, {"fileName": "../evil", "content": "x"}],
        }
        target = tmp_path / "pulled"
        stderr = _assert_error(
            runner.invoke(app, ["workflow", "pull", "my-flow", "--dir", str(target), "--version", "2"]),
            "cc-devthrottle workflow show my-flow --version 2",
        )
        assert "unsafe helper file name" in stderr
        assert not target.exists()

    def test_pull_into_a_file_is_an_error_not_a_traceback(self, workflow_client, tmp_path):
        workflow_client.get_version_detail.return_value = {"version": 2, "files": []}
        blocker = tmp_path / "a-file"
        blocker.write_text("x")
        stderr = _assert_error(
            runner.invoke(app, ["workflow", "pull", "my-flow", "--dir", str(blocker), "--version", "2"]),
            'cc-devthrottle workflow pull my-flow --dir "<writable-dir>"',
        )
        assert "could not write the workflow" in stderr

    def test_materialize_points_at_the_instructions(self, workflow_client, tmp_path, monkeypatch):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        workflow_client.get_workflow.return_value = {"version": 3}
        workflow_client.get_version_detail.return_value = {
            "version": 3, "status": "published", "instructionsMarkdown": "x", "contentHash": "h", "files": [],
        }
        result = runner.invoke(app, ["workflow", "materialize", "my-flow"])
        assert "Materialized 'my-flow' v3" in result.stdout
        _assert_plain_with_help(result, "cc-devthrottle workflow instructions my-flow --version 3")

    def test_materialize_refuses_an_unsafe_helper_name(self, workflow_client, tmp_path, monkeypatch):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        workflow_client.get_version_detail.return_value = {
            "version": 3, "status": "published", "files": [{"fileName": "a/b", "content": "x"}],
        }
        _assert_error(
            runner.invoke(app, ["workflow", "materialize", "my-flow", "--version", "3"]),
            "cc-devthrottle workflow show my-flow --version 3",
        )

    def test_instructions_stay_verbatim_with_no_help_block(self, workflow_client):
        workflow_client.get_instructions.return_value = "# Conduct\n"
        result = runner.invoke(app, ["workflow", "instructions", "my-flow"])
        assert result.exit_code == 0
        assert result.stdout == "# Conduct\n"

    def test_draft_materialize_names_publish(self, workflow_client):
        workflow_client.get_version_detail.return_value = {"version": 5, "status": "draft"}
        _assert_error(
            runner.invoke(app, ["workflow", "materialize", "my-flow", "--version", "5"]),
            "cc-devthrottle workflow publish my-flow",
        )


class TestSkillMutations:
    def test_push_existing_skill_points_at_publish(self, skill_client, tmp_path):
        (tmp_path / ".skill-hash").write_text("h0")
        skill_client.skill_exists.return_value = True
        skill_client.update_draft.return_value = {"version": 2, "contentHash": "h1"}
        result = runner.invoke(app, ["skill", "push", "my-skill", "--dir", str(tmp_path)])
        assert "Updated draft v2 of 'my-skill'." in result.stdout
        _assert_plain_with_help(
            result, "cc-devthrottle skill publish my-skill", "cc-devthrottle skill versions my-skill"
        )

    def test_push_without_a_sidecar_names_pull_and_force(self, skill_client, tmp_path):
        skill_client.skill_exists.return_value = True
        stderr = _assert_error(
            runner.invoke(app, ["skill", "push", "my-skill", "--dir", str(tmp_path)]),
            f'cc-devthrottle skill pull my-skill --dir "{tmp_path}"',
            "cc-devthrottle skill push --help",
        )
        assert "--force" in stderr

    def test_publish(self, skill_client):
        skill_client.publish.return_value = {"version": 3}
        result = runner.invoke(app, ["skill", "publish", "my-skill"])
        _assert_plain_with_help(
            result, "cc-devthrottle skill get my-skill", "cc-devthrottle skill versions my-skill"
        )

    def test_clone_with_an_unsafe_returned_id_uses_the_placeholder(self, skill_client):
        skill_client.clone.return_value = {"id": None, "version": 1}
        result = runner.invoke(app, ["skill", "clone", "browsers", "mine"])
        _assert_plain_with_help(
            result, 'cc-devthrottle skill pull <new-id> --dir "<dir>"', "cc-devthrottle skill show <new-id>"
        )

    def test_enable_and_disable(self, skill_client):
        _assert_plain_with_help(
            runner.invoke(app, ["skill", "enable", "my-skill"]),
            "cc-devthrottle skill show my-skill",
            "cc-devthrottle skill disable my-skill",
        )
        _assert_plain_with_help(
            runner.invoke(app, ["skill", "disable", "my-skill"]),
            "cc-devthrottle skill enable my-skill",
            "cc-devthrottle skill list",
        )

    def test_delete_with_yes(self, skill_client):
        result = runner.invoke(app, ["skill", "delete", "my-skill", "-y"])
        _assert_plain_with_help(result, "cc-devthrottle skill list", "cc-devthrottle skill versions my-skill")

    def test_delete_without_yes_and_no_terminal_refuses(self, skill_client):
        _assert_error(
            runner.invoke(app, ["skill", "delete", "my-skill"]),
            "cc-devthrottle skill delete my-skill --yes",
            exit_code=2,
        )
        skill_client.delete.assert_not_called()

    def test_pull_with_an_unsafe_directory_name_uses_the_placeholder(self, skill_client, tmp_path):
        skill_client.get_version_detail.return_value = {"version": 1, "status": "published", "files": []}
        target = tmp_path / "it's $HOME"
        result = runner.invoke(app, ["skill", "pull", "my-skill", "--dir", str(target), "--version", "1"])
        _assert_plain_with_help(result, 'cc-devthrottle skill push my-skill --dir "<dir>"')

    def test_pull_refuses_an_unsafe_path_before_writing_anything(self, skill_client, tmp_path):
        skill_client.get_version_detail.return_value = {
            "version": 1, "files": [{"fileName": "ok.md", "content": "x"}, {"fileName": "/etc/x", "content": "x"}],
        }
        target = tmp_path / "pulled"
        _assert_error(
            runner.invoke(app, ["skill", "pull", "my-skill", "--dir", str(target), "--version", "1"]),
            "cc-devthrottle skill show my-skill --version 1",
        )
        assert not target.exists()

    def test_get_with_undecodable_files_reports_after_the_body(self, skill_client, tmp_path, monkeypatch):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        skill_client.get_skill.return_value = {"version": 7, "fileCount": 1}
        skill_client.get_body.return_value = "# Body\n"
        skill_client.get_version_detail.return_value = {
            "files": [{"fileName": "bin/tool", "content": "!!not base64!!", "encoding": "base64"}],
            "contentHash": "h",
        }
        result = runner.invoke(app, ["skill", "get", "my-skill"])
        assert result.exit_code == 1
        assert result.stdout == "# Body\n"
        assert "Traceback" not in result.stderr
        assert _help_block(result.stderr) == ["cc-devthrottle skill get my-skill --version 7"]

    def test_get_stays_verbatim_with_no_help_block(self, skill_client):
        skill_client.get_skill.return_value = {"version": 7, "fileCount": 0}
        skill_client.get_body.return_value = "# Body"
        result = runner.invoke(app, ["skill", "get", "my-skill"])
        assert result.exit_code == 0
        assert result.stdout == "# Body\n"


@pytest.mark.parametrize(
    "args, expected",
    [
        (["workflow", "list"], "cc-devthrottle setup status"),
        (["workflow", "show", "w"], "cc-devthrottle workflow list"),
        (["workflow", "instructions", "w"], "cc-devthrottle workflow versions w"),
        (["workflow", "versions", "w"], "cc-devthrottle workflow list"),
        (["workflow", "pull", "w", "--dir", "d"], "cc-devthrottle workflow list"),
        (["workflow", "publish", "w"], "cc-devthrottle workflow versions w"),
        (["workflow", "clone", "w", "n"], "cc-devthrottle workflow clone --help"),
        (["workflow", "enable", "w"], "cc-devthrottle workflow list"),
        (["workflow", "disable", "w"], "cc-devthrottle workflow list"),
        (["workflow", "delete", "w", "--yes"], "cc-devthrottle workflow list"),
        (["workflow", "runs"], "cc-devthrottle workflow runs --help"),
        (["workflow", "run", "abc"], "cc-devthrottle workflow runs --json"),
        (["workflow", "materialize", "w"], "cc-devthrottle workflow versions w"),
    ],
)
def test_every_workflow_command_reports_a_gateway_failure_with_a_next_step(workflow_client, args, expected):
    from src.workflow_ops import GatewayError

    for method in ("list_workflows", "get_workflow", "get_instructions", "list_versions", "get_version_detail",
                   "publish", "clone", "set_enabled", "delete", "list_runs", "get_run", "workflow_exists"):
        getattr(workflow_client, method).side_effect = GatewayError("gateway said no")
    stderr = _assert_error(runner.invoke(app, args), expected)
    assert stderr.startswith("Error: gateway said no\n")


@pytest.mark.parametrize(
    "args, expected",
    [
        (["skill", "list"], "cc-devthrottle setup status"),
        (["skill", "get", "s"], "cc-devthrottle skill list"),
        (["skill", "show", "s"], "cc-devthrottle skill versions s"),
        (["skill", "versions", "s"], "cc-devthrottle skill list"),
        (["skill", "pull", "s", "--dir", "d"], "cc-devthrottle skill list"),
        (["skill", "publish", "s"], "cc-devthrottle skill versions s"),
        (["skill", "clone", "s", "n"], "cc-devthrottle skill clone --help"),
        (["skill", "enable", "s"], "cc-devthrottle skill list"),
        (["skill", "disable", "s"], "cc-devthrottle skill list"),
        (["skill", "delete", "s", "--yes"], "cc-devthrottle skill list"),
    ],
)
def test_every_skill_command_reports_a_gateway_failure_with_a_next_step(skill_client, args, expected):
    from src.skill_ops import GatewayError

    for method in ("list_skills", "get_skill", "get_body", "list_versions", "get_version_detail",
                   "publish", "clone", "set_enabled", "delete", "skill_exists"):
        getattr(skill_client, method).side_effect = GatewayError("gateway said no")
    stderr = _assert_error(runner.invoke(app, args), expected)
    assert stderr.startswith("Error: gateway said no\n")


def test_workflow_push_to_a_missing_directory_names_pull(workflow_client, tmp_path):
    missing = tmp_path / "nope"
    stderr = _assert_error(
        runner.invoke(app, ["workflow", "push", "w", "--dir", str(missing)]),
        f'cc-devthrottle workflow pull w --dir "{missing}"',
    )
    assert "Directory not found" in stderr


# ---------------------------------------------------------------------------------------------------
# settings
# ---------------------------------------------------------------------------------------------------


@pytest.fixture
def config_file(tmp_path, monkeypatch):
    path = tmp_path / "config.json"

    def _load():
        config = CCDirectorConfig()
        config._config_path = path
        return config.load()

    monkeypatch.setattr(settings_ops, "load_config", _load)
    return path


class TestSettings:
    def test_set_ends_with_next_steps_for_that_key(self, config_file):
        result = runner.invoke(app, ["settings", "set", "gateway.url", "http://gw.example"])
        assert result.stdout.startswith("Set gateway.url = http://gw.example\n")
        _assert_plain_with_help(
            result, "cc-devthrottle settings get gateway.url", "cc-devthrottle settings show gateway"
        )
        assert json.loads(config_file.read_text())["gateway"]["url"] == "http://gw.example"

    def test_set_keeps_markup_and_escapes_non_ascii_in_the_echo(self, config_file):
        result = runner.invoke(app, ["settings", "set", "gateway.url", "[b]caf\u00e9"])
        assert result.stdout.startswith("Set gateway.url = [b]caf\\u00e9\n")

    def test_set_json_is_unchanged(self, config_file):
        _assert_json_unchanged(
            runner.invoke(app, ["settings", "set", "gateway.url", "http://gw", "--json"]),
            {"key": "gateway.url", "value": "http://gw", "status": "saved"},
        )

    def test_unknown_key_on_set_is_a_usage_error(self, config_file):
        stderr = _assert_error(
            runner.invoke(app, ["settings", "set", "nope.key", "1"]), "cc-devthrottle settings list", exit_code=2
        )
        assert "cannot set key 'nope.key'" in stderr
        assert not config_file.exists()

    def test_wrong_type_is_a_usage_error_naming_get(self, config_file):
        stderr = _assert_error(
            runner.invoke(app, ["settings", "set", "llm.providers.claude_code.enabled", "maybe"]),
            "cc-devthrottle settings get llm.providers.claude_code.enabled",
            exit_code=2,
        )
        assert "expects a boolean" in stderr

    def test_a_corrupt_config_file_is_a_failure_naming_its_path(self, config_file):
        config_file.write_text("{ not json")
        stderr = _assert_error(
            runner.invoke(app, ["settings", "set", "gateway.url", "http://gw"]), "cc-devthrottle settings path"
        )
        assert "gateway.url was not saved" in stderr
        assert config_file.read_text() == "{ not json"

    def test_unknown_key_on_get_is_a_usage_error(self, config_file):
        _assert_error(runner.invoke(app, ["settings", "get", "nope"]), "cc-devthrottle settings list", exit_code=2)

    def test_unknown_section_on_show_lists_the_real_ones(self, config_file):
        stderr = _assert_error(
            runner.invoke(app, ["settings", "show", "nope"]), "cc-devthrottle settings show", exit_code=2
        )
        assert "Available sections:" in stderr and "llm" in stderr


# ---------------------------------------------------------------------------------------------------
# setup and autostart
# ---------------------------------------------------------------------------------------------------


class _Completed:
    def __init__(self, returncode):
        self.returncode = returncode


@pytest.fixture
def setup_engine(monkeypatch):
    calls = []
    outcome = {"returncode": 0, "stdout": ""}

    def _run(args, check=False):
        calls.append(args)
        sys.stdout.write(outcome["stdout"])
        return _Completed(outcome["returncode"])

    monkeypatch.setattr(setup_ops, "_locate_setup_cli", lambda: "/bin/devthrottle-setup-cli")
    monkeypatch.setattr(setup_ops.subprocess, "run", _run)
    return calls, outcome


class TestSetup:
    @pytest.mark.parametrize("verb", ["install", "update", "repair"])
    def test_success_ends_with_next_steps(self, setup_engine, verb):
        result = runner.invoke(app, ["setup", verb])
        _assert_plain_with_help(result, "cc-devthrottle setup status", "cc-devthrottle autostart status")
        assert "Delegating to setup engine" in result.stderr

    def test_dry_run_points_at_the_real_run(self, setup_engine):
        result = runner.invoke(app, ["setup", "install", "--dry-run", "--role", "gateway"])
        _assert_plain_with_help(
            result, "cc-devthrottle setup install --role gateway", "cc-devthrottle setup status"
        )

    def test_json_passes_the_engine_output_through_untouched(self, setup_engine):
        calls, outcome = setup_engine
        outcome["stdout"] = '{"ok": true}\n'
        result = runner.invoke(app, ["setup", "update", "--json"])
        assert result.exit_code == 0
        assert result.stdout == '{"ok": true}\n'
        assert calls[-1] == ["/bin/devthrottle-setup-cli", "update", "--role", "workstation", "--json"]

    def test_engine_failure_keeps_its_exit_code_and_names_doctor(self, setup_engine):
        _, outcome = setup_engine
        outcome["returncode"] = 3
        stderr = _assert_error(
            runner.invoke(app, ["setup", "repair"]), "cc-devthrottle setup doctor", exit_code=3
        )
        assert "exited with code 3" in stderr

    @pytest.mark.parametrize("verb", ["install", "update", "repair"])
    def test_unknown_role_is_one_usage_error(self, setup_engine, verb):
        # install used to catch its own exit (typer.Exit is a RuntimeError) and report "ERROR:" a
        # second time with exit code 1.
        calls, _ = setup_engine
        stderr = _assert_error(
            runner.invoke(app, ["setup", verb, "--role", "server"]),
            f"cc-devthrottle setup {verb} --role workstation",
            exit_code=2,
        )
        assert stderr.count("Error:") == 1
        assert "--role must be one of workstation, gateway, not 'server'" in stderr
        assert calls == []

    def test_engine_that_cannot_start_is_an_error_not_a_traceback(self, monkeypatch):
        monkeypatch.setattr(setup_ops, "_locate_setup_cli", lambda: "/bin/devthrottle-setup-cli")

        def _broken(args, check=False):
            raise OSError("exec format error")

        monkeypatch.setattr(setup_ops.subprocess, "run", _broken)
        for verb in ("install", "update", "repair"):
            stderr = _assert_error(runner.invoke(app, ["setup", verb]), "cc-devthrottle setup doctor")
            assert "exec format error" in stderr


class TestAutostart:
    @pytest.mark.parametrize("verb, other", [("on", "off"), ("off", "on")])
    def test_on_and_off_end_with_next_steps(self, setup_engine, verb, other):
        result = runner.invoke(app, ["autostart", verb])
        _assert_plain_with_help(result, "cc-devthrottle autostart status", f"cc-devthrottle autostart {other}")

    def test_status_and_json_add_nothing(self, setup_engine):
        _, outcome = setup_engine
        outcome["stdout"] = "on\n"
        assert runner.invoke(app, ["autostart", "status"]).stdout == "on\n"
        outcome["stdout"] = '{"enabled": true}\n'
        assert runner.invoke(app, ["autostart", "on", "--json"]).stdout == '{"enabled": true}\n'

    def test_engine_failure_is_an_actionable_error(self, setup_engine):
        _, outcome = setup_engine
        outcome["returncode"] = 5
        _assert_error(runner.invoke(app, ["autostart", "off"]), "cc-devthrottle autostart status", exit_code=5)

    def test_unknown_verb_names_status(self):
        with pytest.raises(typer.Exit) as exc:
            setup_ops.run_autostart("bogus")
        assert exc.value.exit_code == 2


# ---------------------------------------------------------------------------------------------------
# email
# ---------------------------------------------------------------------------------------------------


@pytest.fixture
def email_client():
    with patch("src.email_ops.EmailClient") as client_cls:
        instance = client_cls.return_value
        instance.send_owner.return_value = {"sent": True, "providerId": "resend-1"}
        yield instance


class TestEmail:
    def test_send_ends_with_next_steps(self, email_client):
        result = runner.invoke(app, ["email", "owner", "--subject", "S", "--body", "B"])
        assert result.stdout.startswith("Sent email to the account owner (id resend-1).\n")
        _assert_plain_with_help(result, "cc-devthrottle session done", "cc-devthrottle session whoami")

    def test_json_is_unchanged(self, email_client):
        _assert_json_unchanged(
            runner.invoke(app, ["email", "owner", "--subject", "S", "--body", "B", "--json"]),
            {"sent": True, "providerId": "resend-1"},
        )

    def test_blank_subject_and_missing_body_are_usage_errors(self, email_client):
        _assert_error(runner.invoke(app, ["email", "owner", "--subject", " ", "--body", "B"]), exit_code=2)
        stderr = _assert_error(runner.invoke(app, ["email", "owner", "--subject", "S"]), exit_code=2)
        assert "--body <text>" in stderr
        email_client.send_owner.assert_not_called()

    def test_missing_attachment_is_a_usage_error_and_nothing_is_sent(self, email_client, tmp_path):
        stderr = _assert_error(
            runner.invoke(app, ["email", "owner", "--subject", "S", "--attach", str(tmp_path / "x.html")]),
            'cc-devthrottle email owner --subject "<subject>" --attach "<existing-file>"',
            exit_code=2,
        )
        assert "attachment not found" in stderr and "Nothing was sent." in stderr
        email_client.send_owner.assert_not_called()

    def test_relay_failure_names_where_the_gateway_is_configured(self, email_client):
        from src.email_ops import GatewayError

        email_client.send_owner.side_effect = GatewayError("Gateway at http://x did not respond within 45s.")
        stderr = _assert_error(
            runner.invoke(app, ["email", "owner", "--subject", "S", "--body", "B"]),
            "cc-devthrottle settings get gateway.url",
        )
        # After a timeout the relay may have sent it, so the error must not claim otherwise.
        assert "Nothing was sent" not in stderr


# ---------------------------------------------------------------------------------------------------
# diag
# ---------------------------------------------------------------------------------------------------


@pytest.mark.parametrize("args", [["diag", "network"], ["diag", "results"], ["diag", "results", "--json"]])
def test_diag_failures_are_actionable_errors(args):
    from src.diag_ops import GatewayError

    with patch("src.diag_ops.DiagClient", side_effect=GatewayError("Gateway not reachable at http://x.")):
        stderr = _assert_error(runner.invoke(app, args), "cc-devthrottle settings get gateway.url")
    assert stderr.startswith("Error: Gateway not reachable at http://x.\n")


# ---------------------------------------------------------------------------------------------------
# browser
# ---------------------------------------------------------------------------------------------------

_BROWSER = {"id": "center-consulting", "name": "Center Consulting", "browser": "Chrome",
            "statusLabel": "Ready", "buName": "center-consulting", "buCdpUrl": "http://127.0.0.1:9310"}


@pytest.fixture
def browsers(monkeypatch):
    monkeypatch.setenv("CC_DIRECTOR_ID", "dir-1")
    state = {"list": [dict(_BROWSER)], "answer": dict(_BROWSER), "posted": []}
    monkeypatch.setattr(browser_ops.gateway, "get_json", lambda path: {"browsers": state["list"]})

    def _post(path, body):
        state["posted"].append((path, body))
        return state["answer"]

    monkeypatch.setattr(browser_ops.gateway, "post_json", _post)
    monkeypatch.setattr(browser_ops.gateway, "delete", lambda path: {"removed": True})
    return state


class TestBrowser:
    def test_create(self, browsers):
        result = runner.invoke(app, ["browser", "create", "--name", "Center Consulting"])
        assert result.stdout.startswith('Created browser "Center Consulting" (Chrome).\n')
        _assert_plain_with_help(
            result, 'cc-devthrottle browser signin "Center Consulting"', "cc-devthrottle browser list"
        )

    def test_signin_and_signin_done(self, browsers):
        _assert_plain_with_help(
            runner.invoke(app, ["browser", "signin", "center-consulting"]),
            'cc-devthrottle browser signin "Center Consulting" --done',
        )
        _assert_plain_with_help(
            runner.invoke(app, ["browser", "signin", "center-consulting", "--done"]),
            'cc-devthrottle browser start "Center Consulting"',
        )

    def test_start_names_the_attach_line(self, browsers):
        result = runner.invoke(app, ["browser", "start", "Center Consulting"])
        assert "BU_CDP_URL=http://127.0.0.1:9310" in result.stdout
        _assert_plain_with_help(
            result,
            "eval \"$(cc-devthrottle browser attach 'Center Consulting')\"",
            'cc-devthrottle browser stop "Center Consulting"',
        )

    def test_start_with_a_quote_in_the_name_uses_the_placeholder(self, browsers):
        browsers["answer"] = dict(_BROWSER, name="Soren's")
        result = runner.invoke(app, ["browser", "start", "center-consulting"])
        _assert_plain_with_help(
            result,
            "eval \"$(cc-devthrottle browser attach '<name>')\"",
            'cc-devthrottle browser stop "Soren\'s"',
        )

    def test_stop_rename_remove(self, browsers):
        _assert_plain_with_help(
            runner.invoke(app, ["browser", "stop", "center-consulting"]),
            'cc-devthrottle browser start "Center Consulting"',
            "cc-devthrottle browser list",
        )
        browsers["answer"] = dict(_BROWSER, name="New $Name")
        result = runner.invoke(app, ["browser", "rename", "center-consulting", "--to", "New $Name"])
        assert result.stdout.startswith('Renamed to "New $Name".\n')
        _assert_plain_with_help(result, 'cc-devthrottle browser start "<name>"', "cc-devthrottle browser list")
        result = runner.invoke(app, ["browser", "remove", "center-consulting"])
        _assert_plain_with_help(
            result, "cc-devthrottle browser list", 'cc-devthrottle browser create --name "<name>" --browser chrome'
        )

    @pytest.mark.parametrize(
        "args, payload",
        [
            (["browser", "create", "--name", "Center Consulting", "--json"], _BROWSER),
            (["browser", "signin", "center-consulting", "--json"], _BROWSER),
            (["browser", "start", "center-consulting", "--json"], _BROWSER),
            (["browser", "stop", "center-consulting", "--json"], _BROWSER),
            (["browser", "rename", "center-consulting", "--to", "X", "--json"], _BROWSER),
            (["browser", "remove", "center-consulting", "--json"], {"removed": True}),
        ],
    )
    def test_json_is_unchanged(self, browsers, args, payload):
        _assert_json_unchanged(runner.invoke(app, args), payload)

    def test_attach_prints_only_the_export_lines(self, browsers, monkeypatch):
        monkeypatch.setattr(
            browser_ops.gateway,
            "get_json",
            lambda path: _BROWSER if path.endswith("/attach") else {"browsers": [_BROWSER]},
        )
        result = runner.invoke(app, ["browser", "attach", "center-consulting"])
        assert result.stdout == (
            "export BU_NAME=center-consulting\nexport BU_CDP_URL=http://127.0.0.1:9310\n"
        )

    def test_unknown_browser_is_an_error_naming_list(self, browsers):
        stderr = _assert_error(runner.invoke(app, ["browser", "stop", "nope"]), "cc-devthrottle browser list")
        assert 'No automation browser matching "nope". On this machine: "Center Consulting".' in stderr

    def test_unknown_browser_on_an_empty_machine_names_create(self, browsers):
        browsers["list"] = []
        _assert_error(
            runner.invoke(app, ["browser", "start", "nope"]),
            'cc-devthrottle browser create --name "<name>" --browser chrome',
        )

    def test_no_director_is_a_sentence_on_standard_error(self, monkeypatch):
        monkeypatch.delenv("CC_DIRECTOR_ID", raising=False)
        stderr = _assert_error(runner.invoke(app, ["browser", "list"]), "cc-devthrottle session whoami")
        assert "CC_DIRECTOR_ID is not set" in stderr


# ---------------------------------------------------------------------------------------------------
# Local file failures are errors, not tracebacks
# ---------------------------------------------------------------------------------------------------


def test_workflow_push_with_a_binary_helper_is_an_error(workflow_client, tmp_path):
    (tmp_path / "helpers").mkdir()
    (tmp_path / "helpers" / "tool.bin").write_bytes(b"\xff\xfe\x00")
    workflow_client.workflow_exists.return_value = False
    stderr = _assert_error(
        runner.invoke(app, ["workflow", "push", "w", "--dir", str(tmp_path)]),
        f'cc-devthrottle workflow pull w --dir "{tmp_path}"',
    )
    assert "could not read the workflow files" in stderr
    workflow_client.create.assert_not_called()


def test_skill_push_with_an_unreadable_file_is_an_error(skill_client, tmp_path, monkeypatch):
    (tmp_path / "SKILL.md").write_text("# x")
    (tmp_path / "notes.md").write_text("x")
    real_read_bytes = Path.read_bytes

    def _read_bytes(self):
        if self.name == "notes.md":
            raise PermissionError("permission denied")
        return real_read_bytes(self)

    monkeypatch.setattr(Path, "read_bytes", _read_bytes)
    stderr = _assert_error(
        runner.invoke(app, ["skill", "push", "s", "--dir", str(tmp_path)]),
        f'cc-devthrottle skill pull s --dir "{tmp_path}"',
    )
    assert "permission denied" in stderr and "Nothing was pushed." in stderr
    skill_client.create.assert_not_called()
    skill_client.update_draft.assert_not_called()


def test_workflow_materialize_into_an_unwritable_cache_is_an_error(workflow_client, tmp_path, monkeypatch):
    blocker = tmp_path / "cache-is-a-file"
    blocker.write_text("x")
    monkeypatch.setenv("LOCALAPPDATA", str(blocker))
    workflow_client.get_version_detail.return_value = {
        "version": 3, "status": "published", "instructionsMarkdown": "x", "contentHash": "h", "files": [],
    }
    stderr = _assert_error(
        runner.invoke(app, ["workflow", "materialize", "w", "--version", "3"]),
        "cc-devthrottle workflow instructions w --version 3",
    )
    assert "could not write the workflow cache" in stderr


def test_schedule_list_flag_mistakes_name_the_help(schedule_client):
    stderr = _assert_error(
        runner.invoke(app, ["schedule", "list", "--machine", " "]),
        "cc-devthrottle schedule list --help",
        exit_code=2,
    )
    assert "--machine needs a value." in stderr
    schedule_client.list_jobs.assert_not_called()


def test_schedule_list_refusing_a_broken_row_names_the_raw_view(schedule_client):
    schedule_client.list_jobs.return_value = [{"name": "no id"}]
    stderr = _assert_error(runner.invoke(app, ["schedule", "list"]), "cc-devthrottle schedule list --json")
    assert "a schedule with no id (row 1)" in stderr
