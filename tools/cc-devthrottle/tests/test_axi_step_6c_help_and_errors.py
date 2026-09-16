"""AXI step 6c (#2922): next steps after mutations, short help, and errors an agent can act on.

Covers the command groups schedule, workflow, skill, settings, setup, email, diag, autostart and
browser, and the top-level `actions` command. The rules are in docs/axi-standard.md:

- A command that changes something ends its plain output with `help[N]:` next commands. A value is
  filled in only when the command's own result supplied it; everything else is a placeholder.
- Every command and group has a one-line `--help` summary that fits one row of its group's list.
- A runtime error goes to standard error as `Error: ...` plus `help[N]:` next steps, and exits 1.
- A usage error goes through the one usage-error formatter (usage_errors): `Error: ...`, the
  command's Usage line, its Valid options and help[1] naming its --help, and exits 2.
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
from src import axi_cli, browser_ops, setup_ops, settings_ops, usage_errors  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()


# Complete version details, shaped like the Gateway's SkillVersionDetailDto and
# WorkflowVersionDetailDto. The pull and cache writers refuse anything less, so a test that wants a
# broken answer starts from one of these and breaks exactly one thing.
def _full_skill(fields=None, **overrides) -> dict:
    detail = {
        "skillId": "my-skill", "version": 1, "status": "published", "name": "My skill",
        "summary": "Does the thing.", "triggers": ["do the thing"], "bodyMarkdown": "new body",
        "files": [], "license": None, "compatibility": None, "allowedTools": None, "metadata": {},
        "contentHash": "new-hash", "authoredBy": "session:test", "changeNote": None,
        "createdUtc": "2026-09-01T00:00:00Z", "publishedUtc": "2026-09-01T00:00:00Z",
    }
    detail.update(fields or {})
    detail.update(overrides)
    if isinstance(detail.get("files"), list):
        detail["files"] = [
            dict({"encoding": "utf8", "executable": False}, **f) if isinstance(f, dict) else f
            for f in detail["files"]
        ]
    return detail


def _full_workflow(fields=None, **overrides) -> dict:
    detail = {
        "workflowId": "my-flow", "version": 2, "status": "published", "name": "My flow",
        "summary": "Runs the thing.", "whenToUse": "When the thing is due.", "humanCheckpoint": "Once.",
        "steps": [{"name": "Do", "description": "d", "doer": "Worker", "reviewer": None, "done": "Done."}],
        "instructionsMarkdown": "new body",
        "outcomeCriteria": [{"criterionId": "done", "description": "It is done.", "proofHint": None}],
        "files": [], "contentHash": "new-hash", "authoredBy": "session:test", "changeNote": None,
        "createdUtc": "2026-09-01T00:00:00Z", "publishedUtc": "2026-09-01T00:00:00Z",
    }
    detail.update(fields or {})
    detail.update(overrides)
    return detail


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


def _assert_error(result, *expected_help):
    """An error: nothing on standard output, `Error: ...` and next steps on standard error."""
    assert result.exit_code == 1, (result.exit_code, result.stdout, result.stderr)
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


def _assert_usage_error(result, command):
    """A usage error, written by the one formatter in usage_errors: the Error line, the Usage line of
    `command`, its Valid options, and help[1] naming its --help. Exit 2, nothing on standard output."""
    assert result.exit_code == 2, (result.exit_code, result.stdout, result.stderr)
    assert result.stdout == "", result.stdout
    assert "Traceback" not in result.stderr
    assert result.stderr.isascii()
    lines = result.stderr.rstrip("\n").split("\n")
    assert lines[0].startswith("Error: "), result.stderr
    assert sum(1 for line in lines if line.startswith("Error: ")) == 1, result.stderr
    assert lines[1].startswith(f"Usage: cc-devthrottle {command} "), result.stderr
    assert lines[2].startswith("Valid options: ") and "--help" in lines[2], result.stderr
    assert _help_block(result.stderr) == [f"cc-devthrottle {command} --help"]
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

# The groups step 6c owns. Step 6b (branch axi/help-mutations-a) adds session, message, mission,
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
        # A Windows path keeps its single backslashes: inside double quotes they are literal.
        assert axi_cli.quoted("C:\\Users\\me\\my dir", "<dir>") == '"C:\\Users\\me\\my dir"'
        for unsafe in ["", " padded", 'a"b', "a$b", "a`b", "a\\\\b", "\\\\server\\share", "ends\\",
                       "a!b", "line\nbreak", "caf\u00e9", None]:
            assert axi_cli.quoted(unsafe, "<name>") == '"<name>"'

    def test_fail_refuses_an_error_without_a_next_step(self):
        with pytest.raises(ValueError):
            axi_cli.fail("broken", [])

    def test_fail_always_exits_1(self, capsys):
        # 2 belongs to usage errors, and they have their own formatter.
        with pytest.raises(typer.Exit) as exc:
            axi_cli.fail("broken", ["cc-devthrottle x"])
        assert exc.value.exit_code == 1

    def test_usage_error_goes_through_the_one_formatter(self):
        # Outside a command there is no context to name; the error is still the shared class, and
        # its message is escaped to one line of ASCII before it gets there.
        with pytest.raises(usage_errors.CommandUsageError) as exc:
            axi_cli.usage_error("bad value 'caf\u00e9\nx'.")
        assert exc.value.format_message() == "bad value 'caf\\u00e9\\nx'."

    def test_ascii_text_escapes_control_characters_and_keeps_backslashes(self):
        assert axi_cli.ascii_text("A\nB") == "A\\nB"
        assert axi_cli.ascii_text("a\rb\tc\x1b[31md\x07") == "a\\rb\\tc\\u001b[31md\\u0007"
        assert axi_cli.ascii_text("caf\u00e9") == "caf\\u00e9"
        assert axi_cli.ascii_text('C:\\Users\\me "x"') == 'C:\\Users\\me "x"'

    def test_write_lines_and_fail_keep_a_newline_in_a_value_on_one_line(self, capsys):
        axi_cli.write_lines("Started 'a\nb'.")
        assert capsys.readouterr().out == "Started 'a\\nb'.\n"
        with pytest.raises(typer.Exit):
            axi_cli.fail("gateway said: first\nsecond", ["cc-devthrottle x"])
        assert capsys.readouterr().err == "Error: gateway said: first\\nsecond\nhelp[1]:\n  cc-devthrottle x\n"

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
        assert axi_cli.confirm_or_fail("Sure?", False, "--yes") is False
        assert axi_cli.confirm_or_fail("Sure?", True, "--yes") is True


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


# `cc-devthrottle actions --json` exactly as origin/main printed it before step 6c, captured byte for
# byte. It is pinned in a file, not rebuilt from `_ACTIONS`, so a changed id, command or flag in the
# registry fails here instead of changing both sides of the comparison.
_ACTIONS_JSON_BEFORE = Path(__file__).parent / "fixtures" / "actions_json_before_step_6c.json"


def test_actions_json_is_unchanged():
    pinned = _ACTIONS_JSON_BEFORE.read_text(encoding="utf-8")
    # The pin itself must be the real payload, not an empty file that anything would match.
    actions = json.loads(pinned)["actions"]
    assert len(actions) == 86
    assert {"session-list", "schedule-create", "browser-start"} <= {a["id"] for a in actions}

    result = runner.invoke(app, ["actions", "--json"])
    assert result.exit_code == 0
    assert result.stdout == pinned


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
        stderr = _assert_usage_error(runner.invoke(app, _CREATE + extra), "schedule create")
        assert message in stderr
        assert "Full form: cc-devthrottle schedule create --name" in stderr
        schedule_client.create_job.assert_not_called()

    def test_create_without_anything_to_run_is_a_usage_error(self, schedule_client):
        args = [a for a in _CREATE if a not in ("--seed", "/help")]
        stderr = _assert_usage_error(runner.invoke(app, args), "schedule create")
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
        stderr = _assert_usage_error(runner.invoke(app, ["workflow", "delete", "my-flow"]), "workflow delete")
        assert "--yes" in stderr
        workflow_client.delete.assert_not_called()

    def test_pull_points_at_push_with_the_directory(self, workflow_client, tmp_path):
        workflow_client.list_versions.return_value = [{"version": 2, "status": "published"}]
        workflow_client.get_version_detail.return_value = _full_workflow({
            "version": 2, "status": "published", "instructionsMarkdown": "# hi", "contentHash": "h2",
            "files": [{"fileName": "a.sh", "content": "echo"}],
        })
        target = tmp_path / "my dir"
        result = runner.invoke(app, ["workflow", "pull", "my-flow", "--dir", str(target)])
        _assert_plain_with_help(result, f'cc-devthrottle workflow push my-flow --dir "{target}"')
        assert (target / "helpers" / "a.sh").read_text() == "echo"

    def test_pull_refuses_an_unsafe_helper_name_before_writing_anything(self, workflow_client, tmp_path):
        workflow_client.get_version_detail.return_value = _full_workflow({
            "version": 2, "instructionsMarkdown": "x", "contentHash": "h",
            "files": [{"fileName": "ok.sh", "content": "x"}, {"fileName": "../evil", "content": "x"}],
        })
        target = tmp_path / "pulled"
        stderr = _assert_error(
            runner.invoke(app, ["workflow", "pull", "my-flow", "--dir", str(target), "--version", "2"]),
            "cc-devthrottle workflow show my-flow --version 2",
        )
        assert "unsafe helper file name" in stderr
        assert not target.exists()

    def test_pull_into_a_file_is_an_error_not_a_traceback(self, workflow_client, tmp_path):
        workflow_client.get_version_detail.return_value = _full_workflow({
            "version": 2, "instructionsMarkdown": "x", "contentHash": "h", "files": [],
        })
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
        workflow_client.get_version_detail.return_value = _full_workflow({
            "version": 3, "status": "published", "instructionsMarkdown": "x", "contentHash": "h", "files": [],
        })
        result = runner.invoke(app, ["workflow", "materialize", "my-flow"])
        assert "Materialized 'my-flow' v3" in result.stdout
        _assert_plain_with_help(result, "cc-devthrottle workflow instructions my-flow --version 3")

    def test_materialize_refuses_an_unsafe_helper_name(self, workflow_client, tmp_path, monkeypatch):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        workflow_client.get_version_detail.return_value = _full_workflow({
            "version": 3, "status": "published", "instructionsMarkdown": "x", "contentHash": "h",
            "files": [{"fileName": "a/b", "content": "x"}],
        })
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
        workflow_client.get_version_detail.return_value = _full_workflow({"version": 5, "status": "draft"})
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
        stderr = _assert_usage_error(runner.invoke(app, ["skill", "delete", "my-skill"]), "skill delete")
        assert "Re-run it with --yes." in stderr
        skill_client.delete.assert_not_called()

    def test_pull_with_an_unsafe_directory_name_uses_the_placeholder(self, skill_client, tmp_path):
        skill_client.get_version_detail.return_value = _full_skill({
            "version": 1, "status": "published", "bodyMarkdown": "x", "contentHash": "h", "files": [],
        })
        target = tmp_path / "it's $HOME"
        result = runner.invoke(app, ["skill", "pull", "my-skill", "--dir", str(target), "--version", "1"])
        _assert_plain_with_help(result, 'cc-devthrottle skill push my-skill --dir "<dir>"')

    def test_pull_refuses_an_unsafe_path_before_writing_anything(self, skill_client, tmp_path):
        skill_client.get_version_detail.return_value = _full_skill({
            "version": 1, "bodyMarkdown": "x", "contentHash": "h",
            "files": [{"fileName": "ok.md", "content": "x"}, {"fileName": "/etc/x", "content": "x"}],
        })
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
        skill_client.get_version_detail.return_value = _full_skill({
            "version": 7,
            "files": [{"fileName": "bin/tool", "content": "!!not base64!!", "encoding": "base64"}],
            "bodyMarkdown": "# Body\n", "contentHash": "h",
        })
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
        stderr = _assert_usage_error(runner.invoke(app, ["settings", "set", "nope.key", "1"]), "settings set")
        assert "cc-devthrottle settings list" in stderr
        assert "cannot set key 'nope.key'" in stderr
        assert not config_file.exists()

    def test_wrong_type_is_a_usage_error_naming_get(self, config_file):
        stderr = _assert_usage_error(
            runner.invoke(app, ["settings", "set", "llm.providers.claude_code.enabled", "maybe"]), "settings set"
        )
        assert "cc-devthrottle settings get llm.providers.claude_code.enabled" in stderr
        assert "expects a boolean" in stderr

    def test_a_corrupt_config_file_is_a_failure_naming_its_path(self, config_file):
        config_file.write_text("{ not json")
        stderr = _assert_error(
            runner.invoke(app, ["settings", "set", "gateway.url", "http://gw"]), "cc-devthrottle settings path"
        )
        assert "gateway.url was not saved" in stderr
        assert config_file.read_text() == "{ not json"

    def test_unknown_key_on_get_is_a_usage_error(self, config_file):
        stderr = _assert_usage_error(runner.invoke(app, ["settings", "get", "nope"]), "settings get")
        assert "cc-devthrottle settings list" in stderr

    def test_unknown_section_on_show_lists_the_real_ones(self, config_file):
        stderr = _assert_usage_error(runner.invoke(app, ["settings", "show", "nope"]), "settings show")
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

    def test_engine_failure_exits_1_keeps_its_code_in_the_text_and_names_doctor(self, setup_engine):
        # 0, 1 and 2 are the whole contract: an engine failure is an ordinary failure, not a new code.
        _, outcome = setup_engine
        outcome["returncode"] = 3
        stderr = _assert_error(runner.invoke(app, ["setup", "repair"]), "cc-devthrottle setup doctor")
        assert "exited with code 3" in stderr

    @pytest.mark.parametrize("verb", ["install", "update", "repair"])
    def test_unknown_role_is_one_usage_error(self, setup_engine, verb):
        # install used to catch its own exit (typer.Exit is a RuntimeError) and report "ERROR:" a
        # second time with exit code 1.
        calls, _ = setup_engine
        stderr = _assert_usage_error(runner.invoke(app, ["setup", verb, "--role", "server"]), f"setup {verb}")
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

    def test_engine_failure_exits_1_and_keeps_its_code_in_the_text(self, setup_engine):
        _, outcome = setup_engine
        outcome["returncode"] = 5
        stderr = _assert_error(runner.invoke(app, ["autostart", "off"]), "cc-devthrottle autostart status")
        assert "exited with code 5" in stderr

    def test_unknown_verb_is_a_usage_error(self):
        # No command reaches it - each verb is its own command - so it is called directly.
        with pytest.raises(usage_errors.CommandUsageError) as exc:
            setup_ops.run_autostart("bogus")
        assert "autostart verb must be one of on, off, status, not 'bogus'" in exc.value.format_message()


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
        _assert_usage_error(runner.invoke(app, ["email", "owner", "--subject", " ", "--body", "B"]), "email owner")
        stderr = _assert_usage_error(runner.invoke(app, ["email", "owner", "--subject", "S"]), "email owner")
        assert "--body <text>" in stderr
        email_client.send_owner.assert_not_called()

    def test_missing_attachment_is_a_usage_error_and_nothing_is_sent(self, email_client, tmp_path):
        stderr = _assert_usage_error(
            runner.invoke(app, ["email", "owner", "--subject", "S", "--attach", str(tmp_path / "x.html")]),
            "email owner",
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
        assert "Attach the harness in Bash or zsh with:" in result.stdout
        assert "  eval \"$(cc-devthrottle browser attach 'Center Consulting')\"" in result.stdout.split("\n")
        _assert_plain_with_help(
            result,
            'cc-devthrottle browser attach "Center Consulting"',
            'cc-devthrottle browser stop "Center Consulting"',
        )

    def test_start_next_steps_hold_no_shell_only_line(self, browsers):
        # PowerShell has neither `eval` nor `export`, so a next step using them cannot be followed there.
        result = runner.invoke(app, ["browser", "start", "Center Consulting"])
        help_block = result.stdout[result.stdout.index("help["):]
        assert "eval" not in help_block and "export" not in help_block and "$(" not in help_block

    def test_start_with_a_quote_in_the_name_uses_the_placeholder(self, browsers):
        browsers["answer"] = dict(_BROWSER, name="Soren's")
        result = runner.invoke(app, ["browser", "start", "center-consulting"])
        assert "  eval \"$(cc-devthrottle browser attach '<name>')\"" in result.stdout.split("\n")
        _assert_plain_with_help(
            result,
            'cc-devthrottle browser attach "Soren\'s"',
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
    workflow_client.get_version_detail.return_value = _full_workflow({
        "workflowId": "w",
        "version": 3, "status": "published", "instructionsMarkdown": "x", "contentHash": "h", "files": [],
    })
    stderr = _assert_error(
        runner.invoke(app, ["workflow", "materialize", "w", "--version", "3"]),
        "cc-devthrottle workflow instructions w --version 3",
    )
    assert "could not write the workflow cache" in stderr


def test_schedule_list_flag_mistakes_name_the_help(schedule_client):
    stderr = _assert_usage_error(runner.invoke(app, ["schedule", "list", "--machine", " "]), "schedule list")
    assert "--machine needs a value." in stderr
    schedule_client.list_jobs.assert_not_called()


def test_schedule_list_refusing_a_broken_row_names_the_raw_view(schedule_client):
    schedule_client.list_jobs.return_value = [{"name": "no id"}]
    stderr = _assert_error(runner.invoke(app, ["schedule", "list"]), "cc-devthrottle schedule list --json")
    assert "a schedule with no id (row 1)" in stderr


# ---------------------------------------------------------------------------------------------------
# Inspection fixes (pull request 2962)
# ---------------------------------------------------------------------------------------------------


def _existing_skill(target: Path) -> None:
    from src import skill_ops

    (target / "helpers").mkdir(parents=True)
    (target / "support.txt").write_text("keep me")
    (target / "helpers" / "run.sh").write_text("echo keep")
    (target / skill_ops.SKILL_MD).write_text("old body")
    (target / skill_ops.HASH_SIDECAR).write_text("old-hash")


def _existing_workflow(target: Path) -> None:
    from src import workflow_ops

    (target / workflow_ops.HELPERS_DIR).mkdir(parents=True)
    (target / workflow_ops.HELPERS_DIR / "run.sh").write_text("echo keep")
    (target / workflow_ops.INSTRUCTIONS_MD).write_text("old body")
    (target / workflow_ops.HASH_SIDECAR).write_text("old-hash")


# Answers that do not say which support files the version has: no field, a null, the wrong type, or
# an entry with no name. None of them may be read as "no files".
_PARTIAL_FILES = [
    {},
    {"files": None},
    {"files": "a.sh"},
    {"files": [{"content": "x"}]},
    {"files": ["a.sh"]},
]


def _without_files_unless(fields: dict, detail: dict) -> dict:
    """Drop the files list from `detail` unless `fields` sets it, so {} means "no files field"."""
    if "files" not in fields:
        detail.pop("files", None)
    return detail


class TestPullNeedsAnExplicitFilesList:
    @pytest.mark.parametrize("partial", _PARTIAL_FILES)
    def test_skill_pull_without_a_files_list_writes_and_deletes_nothing(self, skill_client, tmp_path, partial):
        from src import skill_ops

        target = tmp_path / "pulled"
        _existing_skill(target)
        skill_client.get_version_detail.return_value = _without_files_unless(partial, _full_skill(partial))
        stderr = _assert_error(
            runner.invoke(app, ["skill", "pull", "my-skill", "--dir", str(target), "--version", "1"]),
            "cc-devthrottle skill show my-skill --version 1",
        )
        assert "nothing was written" in stderr
        assert (target / "support.txt").read_text() == "keep me"
        assert (target / "helpers" / "run.sh").read_text() == "echo keep"
        assert (target / skill_ops.SKILL_MD).read_text() == "old body"
        assert (target / skill_ops.HASH_SIDECAR).read_text() == "old-hash"
        assert not (target / skill_ops.SKILL_JSON).exists()

    def test_skill_pull_with_an_explicit_empty_list_removes_the_support_files(self, skill_client, tmp_path):
        from src import skill_ops

        target = tmp_path / "pulled"
        _existing_skill(target)
        skill_client.get_version_detail.return_value = _full_skill({
            "skillId": "my-skill", "version": 1, "status": "published", "bodyMarkdown": "new body",
            "contentHash": "new-hash", "files": [],
        })
        result = runner.invoke(app, ["skill", "pull", "my-skill", "--dir", str(target), "--version", "1"])
        assert result.exit_code == 0, result.stderr
        assert not (target / "support.txt").exists()
        assert not (target / "helpers").exists()
        assert (target / skill_ops.SKILL_MD).read_text() == "new body"
        assert (target / skill_ops.HASH_SIDECAR).read_text() == "new-hash"

    @pytest.mark.parametrize("partial", _PARTIAL_FILES)
    def test_workflow_pull_without_a_files_list_writes_and_deletes_nothing(
        self, workflow_client, tmp_path, partial
    ):
        from src import workflow_ops

        target = tmp_path / "pulled"
        _existing_workflow(target)
        workflow_client.get_version_detail.return_value = _without_files_unless(partial, _full_workflow(partial))
        stderr = _assert_error(
            runner.invoke(app, ["workflow", "pull", "my-flow", "--dir", str(target), "--version", "2"]),
            "cc-devthrottle workflow show my-flow --version 2",
        )
        assert "nothing was written" in stderr
        assert (target / workflow_ops.HELPERS_DIR / "run.sh").read_text() == "echo keep"
        assert (target / workflow_ops.INSTRUCTIONS_MD).read_text() == "old body"
        assert (target / workflow_ops.HASH_SIDECAR).read_text() == "old-hash"
        assert not (target / workflow_ops.WORKFLOW_JSON).exists()

    def test_workflow_pull_with_an_explicit_empty_list_removes_the_helpers(self, workflow_client, tmp_path):
        from src import workflow_ops

        target = tmp_path / "pulled"
        _existing_workflow(target)
        workflow_client.get_version_detail.return_value = _full_workflow({
            "workflowId": "my-flow", "version": 2, "status": "published",
            "instructionsMarkdown": "new body", "contentHash": "new-hash", "files": [],
        })
        result = runner.invoke(app, ["workflow", "pull", "my-flow", "--dir", str(target), "--version", "2"])
        assert result.exit_code == 0, result.stderr
        assert not (target / workflow_ops.HELPERS_DIR).exists()
        assert (target / workflow_ops.INSTRUCTIONS_MD).read_text() == "new body"


def _command_lines(text: str) -> list:
    """Every command `text` shows, whether in a sentence or in the help block: each line from its
    first `cc-devthrottle ` on. A sentence may quote a value before it; the command may not."""
    return [line[line.index("cc-devthrottle "):] for line in text.split("\n") if "cc-devthrottle " in line]


# A value that must never be written into a command: a quote ends the quoting, and the rest runs.
_HOSTILE = "x'\"; touch pwned; echo $(id) `id`"


class TestEveryCommandLineIsSafe:
    def test_browser_start_attach_line_uses_the_placeholder_for_a_quote(self, browsers):
        browsers["answer"] = dict(_BROWSER, name="Soren's")
        result = runner.invoke(app, ["browser", "start", "center-consulting"])
        assert result.exit_code == 0, result.stderr
        placeholder = "eval \"$(cc-devthrottle browser attach '<name>')\""
        assert f"  {placeholder}" in result.stdout.split("\n")
        assert "attach 'Soren" not in result.stdout
        for line in _command_lines(result.stdout):
            assert "attach 'Soren" not in line, line

    @pytest.mark.parametrize(
        "args",
        [
            ["browser", "create", "--name", "x"],
            ["browser", "signin", "center-consulting"],
            ["browser", "signin", "center-consulting", "--done"],
            ["browser", "start", "center-consulting"],
            ["browser", "stop", "center-consulting"],
            ["browser", "rename", "center-consulting", "--to", "x"],
        ],
    )
    def test_browser_never_writes_a_hostile_name_into_a_command(self, browsers, args):
        browsers["answer"] = dict(_BROWSER, name=_HOSTILE)
        result = runner.invoke(app, args)
        assert result.exit_code == 0, result.stderr
        lines = _command_lines(result.stdout)
        assert lines, result.stdout
        for line in lines:
            assert "touch pwned" not in line, line

    def test_skill_pull_sentence_uses_the_placeholder_for_an_unsafe_directory(self, skill_client, tmp_path):
        skill_client.get_version_detail.return_value = _full_skill({
            "version": 1, "status": "published", "bodyMarkdown": "x", "contentHash": "h", "files": [],
        })
        target = tmp_path / "it's $HOME"
        result = runner.invoke(app, ["skill", "pull", "my-skill", "--dir", str(target), "--version", "1"])
        assert result.exit_code == 0, result.stderr
        assert 'Edit the files, then push with: cc-devthrottle skill push my-skill --dir "<dir>"' in result.stdout
        for line in _command_lines(result.stdout):
            assert "$HOME" not in line, line

    def test_workflow_pull_sentence_uses_the_placeholder_for_an_unsafe_directory(self, workflow_client, tmp_path):
        workflow_client.get_version_detail.return_value = _full_workflow({
            "version": 2, "status": "published", "instructionsMarkdown": "x", "contentHash": "h", "files": [],
        })
        target = tmp_path / "it's $HOME"
        result = runner.invoke(app, ["workflow", "pull", "my-flow", "--dir", str(target), "--version", "2"])
        assert result.exit_code == 0, result.stderr
        assert (
            'Edit the files, then push with: cc-devthrottle workflow push my-flow --dir "<dir>"' in result.stdout
        )
        for line in _command_lines(result.stdout):
            assert "$HOME" not in line, line

    @pytest.mark.parametrize("group", ["skill", "workflow"])
    def test_disable_sentence_uses_the_placeholder_for_an_unsafe_id(self, skill_client, workflow_client, group):
        result = runner.invoke(app, [group, "disable", "a;touch pwned"])
        assert result.exit_code == 0, result.stderr
        lines = _command_lines(result.stdout)
        assert any(line.startswith(f"cc-devthrottle {group} enable <{group}-id>") for line in lines), result.stdout
        # Both the sentence and the help block carry it.
        assert result.stdout.count(f"cc-devthrottle {group} enable <{group}-id>") == 2
        for line in lines:
            assert "touch pwned" not in line, line


# ---------------------------------------------------------------------------------------------------
# Re-check fixes (pull request 2962): a writer that replaces local files checks the WHOLE answer
# first, and swaps the new files in only once they are all on disk.
# ---------------------------------------------------------------------------------------------------

# Answers that are wrong somewhere other than the files list itself. Each one used to be written as
# an empty file, an empty body, or a half-deleted directory.
_BROKEN_BUNDLES = [
    pytest.param({"files": [{"fileName": "support.txt"}]}, id="entry-without-content"),
    pytest.param({"files": [{"fileName": "support.txt", "content": None}]}, id="entry-with-null-content"),
    pytest.param({"files": [{"fileName": "support.txt", "content": 7}]}, id="entry-with-number-content"),
    pytest.param({"files": [], "body": None}, id="empty-list-without-body"),
    pytest.param({"files": [], "body": 5}, id="empty-list-with-number-body"),
    pytest.param({"files": [], "contentHash": None}, id="without-hash"),
    pytest.param({"files": [], "contentHash": ""}, id="with-empty-hash"),
    pytest.param(
        {"files": [{"fileName": "a.txt", "content": "x"}, {"fileName": "A.txt", "content": "y"}]},
        id="same-file-twice",
    ),
    pytest.param(
        {"files": [{"fileName": "a.txt", "content": "x"}, {"fileName": "../evil", "content": "y"}]},
        id="unsafe-name-after-a-good-one",
    ),
]

# Skill files may be base64; content that does not decode must be refused before anything moves.
_BROKEN_SKILL_ONLY = [
    pytest.param(
        {"files": [{"fileName": "a.bin", "content": "!!not base64!!", "encoding": "base64"}]},
        id="invalid-base64",
    ),
    pytest.param(
        {"files": [{"fileName": "a.bin", "content": "QUJD", "encoding": "rot13"}]}, id="unknown-encoding"
    ),
]


def _broken(full, body_field: str, broken: dict) -> dict:
    detail = _without_files_unless(broken, full({k: v for k, v in broken.items() if k != "body"}))
    if "body" in broken:
        detail[body_field] = broken["body"]
        if broken["body"] is None:
            del detail[body_field]
    if detail.get("contentHash") is None:
        detail.pop("contentHash", None)
    return detail


def _skill_detail(broken: dict) -> dict:
    return _broken(_full_skill, "bodyMarkdown", broken)


def _workflow_detail(broken: dict) -> dict:
    return _broken(_full_workflow, "instructionsMarkdown", broken)


def _snapshot(root: Path) -> dict:
    return {p.relative_to(root).as_posix(): p.read_bytes() for p in sorted(root.rglob("*")) if p.is_file()}


def _no_leftovers(parent: Path) -> None:
    from src import bundle_swap

    left = [str(p) for p in parent.rglob("*") if p.name == bundle_swap.WORK_DIR]
    assert left == [], left


def _is_move_in(target: Path, src, dst) -> bool:
    from src import bundle_swap

    staging = target.resolve() / bundle_swap.WORK_DIR / "incoming"
    return Path(dst).parent == target.resolve() and Path(src).parent == staging


def _assert_refused(stderr: str) -> None:
    # An unsafe path keeps its own long-standing message; every other broken answer says it wrote nothing.
    assert "nothing was written" in stderr or "unsafe file path" in stderr or "unsafe helper file name" in stderr, stderr


class TestReplacingFilesChecksTheWholeAnswerFirst:
    @pytest.mark.parametrize("broken", _BROKEN_BUNDLES + _BROKEN_SKILL_ONLY)
    def test_skill_pull_keeps_the_old_bytes(self, skill_client, tmp_path, broken):
        target = tmp_path / "pulled"
        _existing_skill(target)
        before = _snapshot(target)
        skill_client.get_version_detail.return_value = _skill_detail(broken)
        stderr = _assert_error(
            runner.invoke(app, ["skill", "pull", "my-skill", "--dir", str(target), "--version", "1"]),
            "cc-devthrottle skill show my-skill --version 1",
        )
        _assert_refused(stderr)
        assert _snapshot(target) == before
        _no_leftovers(tmp_path)

    @pytest.mark.parametrize("broken", _BROKEN_BUNDLES)
    def test_workflow_pull_keeps_the_old_bytes(self, workflow_client, tmp_path, broken):
        target = tmp_path / "pulled"
        _existing_workflow(target)
        before = _snapshot(target)
        workflow_client.get_version_detail.return_value = _workflow_detail(broken)
        stderr = _assert_error(
            runner.invoke(app, ["workflow", "pull", "my-flow", "--dir", str(target), "--version", "2"]),
            "cc-devthrottle workflow show my-flow --version 2",
        )
        _assert_refused(stderr)
        assert _snapshot(target) == before
        _no_leftovers(tmp_path)

    @pytest.mark.parametrize("broken", _BROKEN_BUNDLES + _PARTIAL_FILES)
    def test_workflow_materialize_keeps_the_cached_helpers(self, workflow_client, tmp_path, monkeypatch, broken):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        cache = tmp_path / "cc-director" / "workflows" / "my-flow" / "2"
        _existing_workflow(cache)
        before = _snapshot(cache)
        workflow_client.get_version_detail.return_value = _workflow_detail(broken)
        stderr = _assert_error(
            runner.invoke(app, ["workflow", "materialize", "my-flow", "--version", "2"]),
            "cc-devthrottle workflow show my-flow --version 2",
        )
        _assert_refused(stderr)
        assert _snapshot(cache) == before
        _no_leftovers(cache.parent)

    @pytest.mark.parametrize("broken", _BROKEN_BUNDLES + _BROKEN_SKILL_ONLY + _PARTIAL_FILES)
    def test_skill_cache_keeps_the_cached_files(self, skill_client, tmp_path, monkeypatch, broken):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        versions = tmp_path / "cc-director" / "skills" / "my-skill"
        cache = versions / "7"
        _existing_skill(cache)
        (versions / "7.hash").write_text("old-hash")
        before = _snapshot(versions)
        skill_client.get_skill.return_value = {"version": 7, "fileCount": 1}
        skill_client.get_body.return_value = "# Body\n"
        skill_client.get_version_detail.return_value = _skill_detail(broken)
        result = runner.invoke(app, ["skill", "get", "my-skill"])
        assert result.exit_code == 1
        _assert_refused(result.stderr)
        assert _snapshot(versions) == before
        _no_leftovers(versions)

    def test_skill_pull_that_fails_while_writing_keeps_the_old_bytes(self, skill_client, tmp_path, monkeypatch):
        from src import skill_ops

        target = tmp_path / "pulled"
        _existing_skill(target)
        before = _snapshot(target)
        real = skill_ops._write_bundle_files

        def half_then_fail(root, files):
            real(root, files[:1])
            raise OSError("disk full")

        monkeypatch.setattr(skill_ops, "_write_bundle_files", half_then_fail)
        skill_client.get_version_detail.return_value = _skill_detail(
            {"files": [{"fileName": "a.txt", "content": "x"}, {"fileName": "b.txt", "content": "y"}]}
        )
        stderr = _assert_error(
            runner.invoke(app, ["skill", "pull", "my-skill", "--dir", str(target), "--version", "1"]),
            'cc-devthrottle skill pull my-skill --dir "<writable-dir>"',
        )
        assert "disk full" in stderr
        assert _snapshot(target) == before
        _no_leftovers(tmp_path)

    def test_workflow_materialize_that_fails_while_writing_keeps_the_cached_helpers(
        self, workflow_client, tmp_path, monkeypatch
    ):
        from src import workflow_ops

        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        cache = tmp_path / "cc-director" / "workflows" / "my-flow" / "2"
        _existing_workflow(cache)
        before = _snapshot(cache)
        real = workflow_ops._write_exact

        def fail_on_helper(path, text):
            if path.parent.name == workflow_ops.HELPERS_DIR:
                raise OSError("disk full")
            real(path, text)

        monkeypatch.setattr(workflow_ops, "_write_exact", fail_on_helper)
        workflow_client.get_version_detail.return_value = _workflow_detail(
            {"files": [{"fileName": "new.sh", "content": "echo new"}]}
        )
        stderr = _assert_error(
            runner.invoke(app, ["workflow", "materialize", "my-flow", "--version", "2"]),
            "cc-devthrottle workflow instructions my-flow --version 2",
        )
        assert "disk full" in stderr
        assert _snapshot(cache) == before

    def test_workflow_pull_replaces_only_what_it_owns(self, workflow_client, tmp_path):
        from src import workflow_ops

        target = tmp_path / "pulled"
        _existing_workflow(target)
        (target / "notes.md").write_text("mine")
        workflow_client.get_version_detail.return_value = _workflow_detail(
            {"files": [{"fileName": "new.sh", "content": "echo new"}]}
        )
        result = runner.invoke(app, ["workflow", "pull", "my-flow", "--dir", str(target), "--version", "2"])
        assert result.exit_code == 0, result.stderr
        assert (target / "notes.md").read_text() == "mine"
        assert sorted(p.name for p in (target / workflow_ops.HELPERS_DIR).iterdir()) == ["new.sh"]
        assert (target / workflow_ops.INSTRUCTIONS_MD).read_text() == "new body"
        assert (target / workflow_ops.HASH_SIDECAR).read_text() == "new-hash"
        _no_leftovers(tmp_path)

    def test_skill_pull_with_base64_writes_the_decoded_bytes(self, skill_client, tmp_path):
        target = tmp_path / "pulled"
        _existing_skill(target)
        skill_client.get_version_detail.return_value = _skill_detail(
            {"files": [{"fileName": "bin/a.bin", "content": "AAEC", "encoding": "base64"}]}
        )
        result = runner.invoke(app, ["skill", "pull", "my-skill", "--dir", str(target), "--version", "1"])
        assert result.exit_code == 0, result.stderr
        assert (target / "bin" / "a.bin").read_bytes() == b"\x00\x01\x02"
        assert not (target / "support.txt").exists()
        _no_leftovers(tmp_path)


class TestBundleSwap:
    def test_a_failed_move_puts_the_old_entries_back(self, tmp_path, monkeypatch):
        import os

        from src import bundle_swap

        target = tmp_path / "t"
        target.mkdir()
        (target / "a.txt").write_text("old a")
        (target / "b.txt").write_text("old b")
        real = os.replace
        calls = []

        def fail_on_first_move_in(src, dst):
            calls.append((src, dst))
            if _is_move_in(target, src, dst):
                raise OSError("rename failed")
            real(src, dst)

        monkeypatch.setattr(bundle_swap.os, "replace", fail_on_first_move_in)

        def build(staging):
            (staging / "a.txt").write_text("new a")

        with pytest.raises(OSError, match="rename failed"):
            bundle_swap.replace_directory(target, build)
        assert _snapshot(target) == {"a.txt": b"old a", "b.txt": b"old b"}
        assert calls, "the move was never attempted"
        _no_leftovers(tmp_path)

    def test_a_failed_move_after_some_moved_in_removes_them(self, tmp_path, monkeypatch):
        from src import bundle_swap

        target = tmp_path / "t"
        target.mkdir()
        (target / "a.txt").write_text("old a")
        real = bundle_swap.os.replace
        moved_in = []

        def fail_on_second_move_in(src, dst):
            if _is_move_in(target, src, dst):
                moved_in.append(dst)
                if len(moved_in) == 2:
                    raise OSError("rename failed")
            real(src, dst)

        monkeypatch.setattr(bundle_swap.os, "replace", fail_on_second_move_in)

        def build(staging):
            (staging / "a.txt").write_text("new a")
            (staging / "z").mkdir()
            (staging / "z" / "n.txt").write_text("new z")

        with pytest.raises(OSError, match="rename failed"):
            bundle_swap.replace_directory(target, build)
        assert _snapshot(target) == {"a.txt": b"old a"}
        _no_leftovers(tmp_path)

    def test_build_may_not_write_an_entry_it_does_not_own(self, tmp_path):
        from src import bundle_swap

        target = tmp_path / "t"
        target.mkdir()
        (target / "keep.txt").write_text("keep")
        with pytest.raises(ValueError, match="does not own"):
            bundle_swap.replace_directory(target, lambda s: (s / "keep.txt").write_text("x"), ["other"])
        assert (target / "keep.txt").read_text() == "keep"
        _no_leftovers(tmp_path)


# ---------------------------------------------------------------------------------------------------
# Re-check 2 fixes (pull request 2962): every authored field is required, no supporting file may land
# on a path the writer owns, a killed swap is put right by the next call, an omitted files list or
# file count is never "no files", a failed cache refresh keeps its hash sidecar, and a malformed
# encoding is an actionable error.
# ---------------------------------------------------------------------------------------------------

_OLD_SKILL_JSON = {"id": "my-skill", "name": "Old Name", "summary": "Old summary", "triggers": ["old"]}
_OLD_WORKFLOW_JSON = {
    "id": "my-flow", "name": "Old Name", "summary": "Old summary",
    "steps": [{"name": "Old", "description": "d", "doer": "Worker", "reviewer": None, "done": "x"}],
}


def _existing_skill_with_metadata(target: Path) -> None:
    from src import skill_ops

    _existing_skill(target)
    (target / skill_ops.SKILL_JSON).write_text(json.dumps(_OLD_SKILL_JSON))


def _existing_workflow_with_metadata(target: Path) -> None:
    from src import workflow_ops

    _existing_workflow(target)
    (target / workflow_ops.WORKFLOW_JSON).write_text(json.dumps(_OLD_WORKFLOW_JSON))


def _omit(field: str):
    return pytest.param(field, id=f"without-{field}")


_MISSING = object()

# Each is (field, bad value); _MISSING removes the field. All of these used to be written as an empty
# or default value, and the next push would have sent that back to the Gateway.
_BAD_SKILL_FIELDS = [
    pytest.param(f, _MISSING, id=f"without-{f}")
    for f in ("skillId", "version", "status", "name", "summary", "triggers", "license",
              "compatibility", "allowedTools", "metadata")
] + [
    pytest.param("name", None, id="null-name"),
    pytest.param("summary", 7, id="number-summary"),
    pytest.param("triggers", None, id="null-triggers"),
    pytest.param("triggers", "one", id="text-triggers"),
    pytest.param("triggers", [1], id="number-in-triggers"),
    pytest.param("license", 5, id="number-license"),
    pytest.param("metadata", None, id="null-metadata"),
    pytest.param("metadata", {"author": 1}, id="number-in-metadata"),
    pytest.param("skillId", "other-skill", id="another-skill"),
    pytest.param("version", 2, id="another-version"),
    pytest.param("version", "1", id="text-version"),
    pytest.param("version", True, id="boolean-version"),
]

_BAD_WORKFLOW_FIELDS = [
    pytest.param(f, _MISSING, id=f"without-{f}")
    for f in ("workflowId", "version", "status", "name", "summary", "whenToUse", "humanCheckpoint",
              "steps", "outcomeCriteria")
] + [
    pytest.param("name", None, id="null-name"),
    pytest.param("steps", None, id="null-steps"),
    pytest.param("steps", [{"name": "Do", "description": "d", "reviewer": None, "done": "x"}],
                 id="step-without-doer"),
    pytest.param("steps", [{"name": "Do", "description": "d", "doer": "W", "done": "x"}],
                 id="step-without-reviewer"),
    pytest.param("steps", [{"name": "Do", "description": "d", "doer": "W", "reviewer": 3, "done": "x"}],
                 id="step-with-number-reviewer"),
    pytest.param("steps", ["Do"], id="step-that-is-text"),
    pytest.param("outcomeCriteria", [{"criterionId": "c", "description": "d"}],
                 id="criterion-without-proof-hint"),
    pytest.param("outcomeCriteria", [{"description": "d", "proofHint": None}],
                 id="criterion-without-id"),
    pytest.param("workflowId", "other-flow", id="another-workflow"),
    pytest.param("version", 3, id="another-version"),
]


def _with(detail: dict, field: str, value) -> dict:
    if value is _MISSING:
        detail.pop(field, None)
    else:
        detail[field] = value
    return detail


class TestPullRequiresEveryAuthoredField:
    @pytest.mark.parametrize("field, value", _BAD_SKILL_FIELDS)
    def test_skill_pull_refuses_and_keeps_the_old_metadata(self, skill_client, tmp_path, field, value):
        target = tmp_path / "pulled"
        _existing_skill_with_metadata(target)
        before = _snapshot(target)
        skill_client.get_version_detail.return_value = _with(_full_skill(), field, value)
        stderr = _assert_error(
            runner.invoke(app, ["skill", "pull", "my-skill", "--dir", str(target), "--version", "1"]),
            "cc-devthrottle skill show my-skill --version 1",
        )
        assert "nothing was written" in stderr
        assert _snapshot(target) == before
        _no_leftovers(tmp_path)

    @pytest.mark.parametrize("field, value", _BAD_SKILL_FIELDS)
    def test_skill_cache_refuses_the_same_answers(self, skill_client, tmp_path, monkeypatch, field, value):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        skill_client.get_version_detail.return_value = _with(
            _full_skill(files=[{"fileName": "a.txt", "content": "x"}]), field, value
        )
        skill_client.get_body.return_value = "# Body\n"
        stderr = _assert_error(runner.invoke(app, ["skill", "get", "my-skill", "--version", "1"]))
        assert "nothing was written" in stderr
        assert not (tmp_path / "cc-director").exists()

    def test_skill_write_pulled_directly_refuses_a_detail_without_a_name(self, tmp_path):
        # The inspection's own reproduction: only a body, a hash and an empty files list.
        from src import skill_ops

        target = tmp_path / "pulled"
        _existing_skill_with_metadata(target)
        with pytest.raises(skill_ops.GatewayError, match="nothing was written"):
            skill_ops._write_pulled(
                target, "my-skill", 1, {"bodyMarkdown": "new", "contentHash": "h", "files": []}
            )
        assert json.loads((target / skill_ops.SKILL_JSON).read_text())["name"] == "Old Name"

    def test_skill_pull_writes_every_authored_field(self, skill_client, tmp_path):
        from src import skill_ops

        target = tmp_path / "pulled"
        skill_client.get_version_detail.return_value = _full_skill(
            license="MIT", compatibility="git", allowedTools="Read", metadata={"author": "x"}
        )
        result = runner.invoke(app, ["skill", "pull", "my-skill", "--dir", str(target), "--version", "1"])
        assert result.exit_code == 0, result.stderr
        assert json.loads((target / skill_ops.SKILL_JSON).read_text()) == {
            "id": "my-skill", "name": "My skill", "summary": "Does the thing.",
            "triggers": ["do the thing"], "license": "MIT", "compatibility": "git",
            "allowedTools": "Read", "metadata": {"author": "x"}, "executable": [],
        }

    @pytest.mark.parametrize("field, value", _BAD_WORKFLOW_FIELDS)
    def test_workflow_pull_refuses_and_keeps_the_old_metadata(self, workflow_client, tmp_path, field, value):
        target = tmp_path / "pulled"
        _existing_workflow_with_metadata(target)
        before = _snapshot(target)
        workflow_client.get_version_detail.return_value = _with(_full_workflow(), field, value)
        stderr = _assert_error(
            runner.invoke(app, ["workflow", "pull", "my-flow", "--dir", str(target), "--version", "2"]),
            "cc-devthrottle workflow show my-flow --version 2",
        )
        assert "nothing was written" in stderr
        assert _snapshot(target) == before
        _no_leftovers(tmp_path)

    @pytest.mark.parametrize("field, value", _BAD_WORKFLOW_FIELDS)
    def test_workflow_materialize_refuses_the_same_answers(
        self, workflow_client, tmp_path, monkeypatch, field, value
    ):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        workflow_client.get_version_detail.return_value = _with(_full_workflow(), field, value)
        stderr = _assert_error(
            runner.invoke(app, ["workflow", "materialize", "my-flow", "--version", "2"]),
            "cc-devthrottle workflow show my-flow --version 2",
        )
        assert "nothing was written" in stderr
        assert not (tmp_path / "cc-director").exists()

    def test_workflow_write_pulled_directly_refuses_a_detail_without_steps(self, tmp_path):
        from src import workflow_ops

        target = tmp_path / "pulled"
        _existing_workflow_with_metadata(target)
        with pytest.raises(workflow_ops.GatewayError, match="nothing was written"):
            workflow_ops._write_pulled(
                target, "my-flow", 2,
                {"instructionsMarkdown": "new", "contentHash": "h", "files": []},
            )
        written = json.loads((target / workflow_ops.WORKFLOW_JSON).read_text())
        assert written["name"] == "Old Name"
        assert written["steps"] == _OLD_WORKFLOW_JSON["steps"]

    def test_workflow_pull_writes_every_authored_field(self, workflow_client, tmp_path):
        from src import workflow_ops

        target = tmp_path / "pulled"
        detail = _full_workflow()
        workflow_client.get_version_detail.return_value = detail
        result = runner.invoke(app, ["workflow", "pull", "my-flow", "--dir", str(target), "--version", "2"])
        assert result.exit_code == 0, result.stderr
        assert json.loads((target / workflow_ops.WORKFLOW_JSON).read_text()) == {
            "id": "my-flow", "name": detail["name"], "summary": detail["summary"],
            "whenToUse": detail["whenToUse"], "humanCheckpoint": detail["humanCheckpoint"],
            "steps": detail["steps"], "outcomeCriteria": detail["outcomeCriteria"],
        }

    def test_materialize_without_a_status_is_not_read_as_published(self, workflow_client, tmp_path, monkeypatch):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        workflow_client.get_version_detail.return_value = _with(_full_workflow(), "status", _MISSING)
        _assert_error(runner.invoke(app, ["workflow", "materialize", "my-flow", "--version", "2"]))
        assert not (tmp_path / "cc-director").exists()


_COLLIDING_PATHS = [
    "SKILL.md", "skill.md", "skill.json", "SKILL.JSON", ".skill-hash", ".bundle-swap/lock",
    "SKILL.md/extra.txt", "skill.json/x",
]


class TestNoSupportingFileOnAWriterPath:
    @pytest.mark.parametrize("name", _COLLIDING_PATHS)
    def test_skill_pull_refuses_before_writing(self, skill_client, tmp_path, name):
        target = tmp_path / "pulled"
        _existing_skill_with_metadata(target)
        before = _snapshot(target)
        skill_client.get_version_detail.return_value = _full_skill(
            files=[{"fileName": "ok.txt", "content": "x"}, {"fileName": name, "content": "support collision"}]
        )
        stderr = _assert_error(
            runner.invoke(app, ["skill", "pull", "my-skill", "--dir", str(target), "--version", "1"]),
            "cc-devthrottle skill show my-skill --version 1",
        )
        assert "the skill's own files use" in stderr
        assert _snapshot(target) == before

    @pytest.mark.parametrize("name", _COLLIDING_PATHS)
    def test_skill_cache_refuses_before_writing(self, skill_client, tmp_path, monkeypatch, name):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        skill_client.get_version_detail.return_value = _full_skill(
            files=[{"fileName": name, "content": "support collision"}]
        )
        skill_client.get_body.return_value = "new body"
        _assert_error(runner.invoke(app, ["skill", "get", "my-skill", "--version", "1"]))
        assert not (tmp_path / "cc-director").exists()

    def test_the_inspection_reproduction_keeps_the_body(self, tmp_path):
        from src import skill_ops

        target = tmp_path / "pulled"
        with pytest.raises(skill_ops.GatewayError):
            skill_ops._write_pulled(target, "my-skill", 1, _full_skill(
                bodyMarkdown="new body", contentHash="h",
                files=[{"fileName": "SKILL.md", "content": "support collision"}],
            ))
        assert not (target / skill_ops.SKILL_MD).exists()

    def test_a_path_that_is_both_a_file_and_a_folder_is_refused(self, skill_client, tmp_path):
        target = tmp_path / "pulled"
        _existing_skill_with_metadata(target)
        before = _snapshot(target)
        skill_client.get_version_detail.return_value = _full_skill(
            files=[{"fileName": "docs", "content": "x"}, {"fileName": "Docs/a.md", "content": "y"}]
        )
        stderr = _assert_error(
            runner.invoke(app, ["skill", "pull", "my-skill", "--dir", str(target), "--version", "1"])
        )
        assert "as a file and as a folder" in stderr
        assert _snapshot(target) == before

    def test_a_workflow_helper_named_like_a_workflow_file_stays_inside_helpers(self, workflow_client, tmp_path):
        # A helper is a bare name inside helpers/, so it cannot reach the files beside that folder.
        from src import workflow_ops

        target = tmp_path / "pulled"
        workflow_client.get_version_detail.return_value = _full_workflow(
            files=[{"fileName": n, "content": "helper"} for n in
                   (workflow_ops.INSTRUCTIONS_MD, workflow_ops.WORKFLOW_JSON, workflow_ops.HASH_SIDECAR)]
        )
        result = runner.invoke(app, ["workflow", "pull", "my-flow", "--dir", str(target), "--version", "2"])
        assert result.exit_code == 0, result.stderr
        assert (target / workflow_ops.INSTRUCTIONS_MD).read_text() == "new body"
        assert (target / workflow_ops.HASH_SIDECAR).read_text() == "new-hash"
        assert json.loads((target / workflow_ops.WORKFLOW_JSON).read_text())["name"] == "My flow"
        assert (target / workflow_ops.HELPERS_DIR / workflow_ops.INSTRUCTIONS_MD).read_text() == "helper"

    def test_skill_push_never_sends_the_swap_work_directory(self, tmp_path):
        from src import bundle_swap, skill_ops

        (tmp_path / bundle_swap.WORK_DIR).mkdir()
        (tmp_path / bundle_swap.WORK_DIR / "stray.txt").write_text("x")
        (tmp_path / "real.txt").write_text("y")
        files = skill_ops._read_tree(tmp_path, [])
        assert [f["fileName"] for f in files] == ["real.txt"]


# A child process that replaces a directory and is killed straight after its Nth rename. The parent
# then makes the next call and checks what the directory holds.
_KILLED_SWAP = r'''
import os, sys
from pathlib import Path
sys.path.insert(0, os.getcwd())
from src import bundle_swap

target, stop_after = Path(sys.argv[1]), int(sys.argv[2])
real = os.replace
done = [0]

def replace_then_die(src, dst):
    real(src, dst)
    done[0] += 1
    if done[0] == stop_after:
        os._exit(17)

bundle_swap.os.replace = replace_then_die

def build(staging):
    (staging / "instructions.md").write_text("new instructions")
    (staging / "helpers").mkdir()
    (staging / "helpers" / "new.sh").write_text("echo new")
    (staging / "added.txt").write_text("added")

bundle_swap.replace_directory(target, build)
print("completed")
'''

_OLD_TREE = {"instructions.md": b"old instructions", "helpers/run.sh": b"echo old", "gone.txt": b"gone"}
_NEW_TREE = {"instructions.md": b"new instructions", "helpers/new.sh": b"echo new", "added.txt": b"added"}


def _plant(target: Path, tree: dict) -> None:
    for name, data in tree.items():
        (target / name).parent.mkdir(parents=True, exist_ok=True)
        (target / name).write_bytes(data)


def _kill_swap_after(target: Path, renames: int):
    import subprocess

    return subprocess.run(
        [sys.executable, "-c", _KILLED_SWAP, str(target), str(renames)],
        cwd=str(Path(__file__).parent.parent), capture_output=True, text=True, timeout=60,
    )


# The journal write is rename 1; the three old entries move out as renames 2-4, the three new ones
# move in as 5-7, and the committed journal is rename 8.
_RENAMES_BEFORE_COMMIT = range(1, 8)


class TestAKilledSwapIsPutRight:
    @pytest.mark.parametrize("renames", _RENAMES_BEFORE_COMMIT)
    def test_recover_brings_back_the_whole_old_directory(self, tmp_path, renames):
        from src import bundle_swap

        target = tmp_path / "t"
        _plant(target, _OLD_TREE)
        child = _kill_swap_after(target, renames)
        assert child.returncode == 17, (child.stdout, child.stderr)
        # The kill really did leave the directory incomplete or mixed - otherwise this proves nothing.
        assert (target / bundle_swap.WORK_DIR).is_dir()
        if renames >= 2:
            assert _snapshot_without_work(target) != _OLD_TREE

        bundle_swap.recover(target)

        assert _snapshot(target) == _OLD_TREE
        _no_leftovers(tmp_path)

    def test_the_first_move_out_is_the_inspection_reproduction(self, tmp_path):
        # helpers/run.sh used to stay in a hidden .outgoing directory with nothing to bring it back.
        from src import bundle_swap

        target = tmp_path / "t"
        _plant(target, {"instructions.md": b"old", "helpers/run.sh": b"echo old"})
        child = _kill_swap_after(target, 2)
        assert child.returncode == 17, child.stderr
        assert not (target / "helpers" / "run.sh").exists()

        bundle_swap.recover(target)

        assert (target / "helpers" / "run.sh").read_bytes() == b"echo old"

    @pytest.mark.parametrize("renames", _RENAMES_BEFORE_COMMIT)
    def test_the_next_replace_starts_from_the_whole_old_directory(self, tmp_path, renames):
        from src import bundle_swap

        target = tmp_path / "t"
        _plant(target, _OLD_TREE)
        assert _kill_swap_after(target, renames).returncode == 17
        seen = {}

        def build(staging):
            # What the next replace moves out must be the whole old directory, not what the kill left.
            seen.update(_snapshot_without_work(target))
            (staging / "fresh.txt").write_text("fresh")

        bundle_swap.replace_directory(target, build)

        assert seen == _OLD_TREE
        assert _snapshot(target) == {"fresh.txt": b"fresh"}
        _no_leftovers(tmp_path)

    def test_a_kill_after_the_commit_keeps_the_new_directory(self, tmp_path):
        from src import bundle_swap

        target = tmp_path / "t"
        _plant(target, _OLD_TREE)
        assert _kill_swap_after(target, 8).returncode == 17
        bundle_swap.recover(target)
        assert _snapshot(target) == _NEW_TREE
        _no_leftovers(tmp_path)

    def test_an_uninterrupted_swap_leaves_no_work_directory(self, tmp_path):
        target = tmp_path / "t"
        _plant(target, _OLD_TREE)
        child = _kill_swap_after(target, 10_000)
        assert child.returncode == 0 and "completed" in child.stdout, child.stderr
        assert _snapshot(target) == _NEW_TREE
        _no_leftovers(tmp_path)

    def test_a_rollback_that_itself_fails_is_finished_by_the_next_call(self, tmp_path, monkeypatch):
        from src import bundle_swap

        target = tmp_path / "t"
        _plant(target, _OLD_TREE)
        real = bundle_swap.os.replace

        def refuse_every_move_in(src, dst):
            if _is_move_in(target, src, dst):
                raise OSError("rename failed")
            real(src, dst)

        def refuse_to_roll_back(*_args):
            raise OSError("rollback failed")

        monkeypatch.setattr(bundle_swap.os, "replace", refuse_every_move_in)
        monkeypatch.setattr(bundle_swap, "_roll_back", refuse_to_roll_back)
        with pytest.raises(OSError, match="rollback failed"):
            bundle_swap.replace_directory(target, lambda s: _plant(s, _NEW_TREE))
        monkeypatch.undo()
        assert _snapshot_without_work(target) != _OLD_TREE

        bundle_swap.recover(target)

        assert _snapshot(target) == _OLD_TREE

    def test_old_files_with_no_journal_are_kept_and_reported(self, tmp_path):
        from src import bundle_swap

        target = tmp_path / "t"
        _plant(target, {"keep.txt": b"keep"})
        orphan = target / bundle_swap.WORK_DIR / "outgoing"
        orphan.mkdir(parents=True)
        (orphan / "precious.txt").write_text("only copy")
        with pytest.raises(OSError, match="no journal"):
            bundle_swap.recover(target)
        assert (orphan / "precious.txt").read_text() == "only copy"

    def test_recover_does_not_create_a_missing_directory(self, tmp_path):
        from src import bundle_swap

        bundle_swap.recover(tmp_path / "absent")
        assert not (tmp_path / "absent").exists()

    @pytest.mark.parametrize("renames", [2, 5])
    def test_skill_push_reads_the_recovered_directory(self, skill_client, tmp_path, renames):
        from src import skill_ops

        target = tmp_path / "pulled"
        _plant(target, {"SKILL.md": b"old body", ".skill-hash": b"old-hash", "ref/a.md": b"old ref"})
        assert _kill_swap_after(target, renames).returncode == 17
        skill_client.skill_exists.return_value = True
        skill_client.update_draft.return_value = {"version": 2, "contentHash": "h2"}
        result = runner.invoke(app, ["skill", "push", "my-skill", "--dir", str(target)])
        assert result.exit_code == 0, result.stderr
        sent = skill_client.update_draft.call_args[0][1]
        assert sent["bodyMarkdown"] == "old body"
        assert [f["fileName"] for f in sent["files"]] == ["ref/a.md"]
        assert skill_client.update_draft.call_args[0][2] == "old-hash"

    @pytest.mark.parametrize("renames", [2, 5])
    def test_workflow_push_reads_the_recovered_directory(self, workflow_client, tmp_path, renames):
        target = tmp_path / "pulled"
        _plant(target, _OLD_TREE)
        (target / ".workflow-hash").write_text("old-hash")
        assert _kill_swap_after(target, renames).returncode == 17
        workflow_client.workflow_exists.return_value = True
        workflow_client.update_draft.return_value = {"version": 3, "contentHash": "h3"}
        result = runner.invoke(app, ["workflow", "push", "my-flow", "--dir", str(target)])
        assert result.exit_code == 0, result.stderr
        sent = workflow_client.update_draft.call_args[0][1]
        assert sent["instructionsMarkdown"] == "old instructions"
        assert [f["fileName"] for f in sent["files"]] == ["run.sh"]

    def test_skill_get_judges_the_cache_only_after_recovery(self, skill_client, tmp_path, monkeypatch):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        versions = tmp_path / "cc-director" / "skills" / "my-skill"
        cache = versions / "1"
        _plant(cache, {"SKILL.md": b"new body", "a.txt": b"x"})
        (versions / "1.hash").write_text("new-hash")
        assert _kill_swap_after(cache, 3).returncode == 17
        skill_client.get_version_detail.return_value = _full_skill(files=[{"fileName": "a.txt", "content": "x"}])
        skill_client.get_body.return_value = "new body"
        result = runner.invoke(app, ["skill", "get", "my-skill", "--version", "1"])
        assert result.exit_code == 0, result.stderr
        assert _snapshot(cache) == {"SKILL.md": b"new body", "a.txt": b"x"}
        assert str(cache / "a.txt") in result.stdout

    def test_workflow_materialize_judges_the_cache_only_after_recovery(
        self, workflow_client, tmp_path, monkeypatch
    ):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        cache = tmp_path / "cc-director" / "workflows" / "my-flow" / "2"
        _plant(cache, {"instructions.md": b"new body", ".workflow-hash": b"new-hash", "helpers/a.sh": b"x"})
        assert _kill_swap_after(cache, 3).returncode == 17
        workflow_client.get_version_detail.return_value = _full_workflow(
            files=[{"fileName": "a.sh", "content": "x"}]
        )
        result = runner.invoke(app, ["workflow", "materialize", "my-flow", "--version", "2"])
        assert result.exit_code == 0, result.stderr
        assert "Already materialized" in result.stdout
        assert _snapshot(cache) == {"instructions.md": b"new body", ".workflow-hash": b"new-hash",
                                    "helpers/a.sh": b"x"}


def _snapshot_without_work(root: Path) -> dict:
    from src import bundle_swap

    # Filtered before reading: on Windows the held lock file cannot be read while a replace runs.
    return {
        p.relative_to(root).as_posix(): p.read_bytes()
        for p in sorted(root.rglob("*"))
        if p.is_file() and p.relative_to(root).parts[0] != bundle_swap.WORK_DIR
    }


class TestNoAnswerIsReadAsNoFiles:
    @pytest.mark.parametrize("partial", _PARTIAL_FILES)
    def test_skill_get_with_a_version_refuses_before_printing(self, skill_client, tmp_path, monkeypatch, partial):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        skill_client.get_version_detail.return_value = _without_files_unless(partial, _full_skill(partial))
        skill_client.get_body.return_value = "# Body"
        stderr = _assert_error(
            runner.invoke(app, ["skill", "get", "my-skill", "--version", "1"]),
            "cc-devthrottle skill versions my-skill",
        )
        assert "supporting file" in stderr
        skill_client.get_body.assert_not_called()

    @pytest.mark.parametrize("head", [
        pytest.param({"version": 7}, id="without-file-count"),
        pytest.param({"version": 7, "fileCount": None}, id="null-file-count"),
        pytest.param({"version": 7, "fileCount": "1"}, id="text-file-count"),
        pytest.param({"fileCount": 0}, id="without-version"),
        pytest.param({"version": "7", "fileCount": 0}, id="text-version"),
        pytest.param([], id="not-an-object"),
    ])
    def test_skill_get_with_a_broken_head_is_an_actionable_error(self, skill_client, head):
        skill_client.get_skill.return_value = head
        skill_client.get_body.return_value = "# Body"
        _assert_error(runner.invoke(app, ["skill", "get", "my-skill"]), "cc-devthrottle skill versions my-skill")
        skill_client.get_body.assert_not_called()

    @pytest.mark.parametrize("head", [{}, {"version": None}, {"version": "3"}, []])
    def test_workflow_materialize_with_a_broken_head_is_an_actionable_error(self, workflow_client, head):
        workflow_client.get_workflow.return_value = head
        _assert_error(
            runner.invoke(app, ["workflow", "materialize", "my-flow"]),
            "cc-devthrottle workflow versions my-flow",
        )
        workflow_client.get_version_detail.assert_not_called()

    @pytest.mark.parametrize("group, client_name, rows", [
        ("skill", "skill_client", [{"version": 1}]),
        ("skill", "skill_client", [{"status": "published"}]),
        ("skill", "skill_client", ["1"]),
        ("workflow", "workflow_client", [{"version": 1}]),
        ("workflow", "workflow_client", [{"status": "draft", "version": "4"}]),
    ])
    def test_pull_with_a_broken_version_list_is_an_actionable_error(
        self, request, tmp_path, group, client_name, rows
    ):
        client = request.getfixturevalue(client_name)
        client.list_versions.return_value = rows
        target = tmp_path / "pulled"
        _assert_error(
            runner.invoke(app, [group, "pull", "x", "--dir", str(target)]),
            f"cc-devthrottle {group} versions x",
        )
        client.get_version_detail.assert_not_called()
        assert not target.exists()


def _body_then_error(result) -> str:
    """A checked answer whose files could not be written: the body printed, then the error."""
    assert result.exit_code == 1, (result.stdout, result.stderr)
    assert result.stdout == "new body\n"
    assert result.stderr.startswith("Error: ") and "Traceback" not in result.stderr
    assert _help_block(result.stderr) == ["cc-devthrottle skill get my-skill --version 1"]
    return result.stderr


class TestAFailedCacheRefreshKeepsItsHash:
    def _cache(self, tmp_path):
        versions = tmp_path / "cc-director" / "skills" / "my-skill"
        _plant(versions / "1", {"SKILL.md": b"old body", "old.txt": b"old"})
        (versions / "1.hash").write_text("old-hash")
        return versions

    def test_a_write_that_fails_keeps_every_old_file(self, skill_client, tmp_path, monkeypatch):
        from src import skill_ops

        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        versions = self._cache(tmp_path)
        before = _snapshot(versions)

        def disk_full(root, files):
            raise OSError("disk full")

        monkeypatch.setattr(skill_ops, "_write_bundle_files", disk_full)
        skill_client.get_version_detail.return_value = _full_skill(files=[{"fileName": "new.txt", "content": "n"}])
        skill_client.get_body.return_value = "new body"
        stderr = _body_then_error(runner.invoke(app, ["skill", "get", "my-skill", "--version", "1"]))
        assert "disk full" in stderr
        assert _snapshot(versions) == before
        _no_leftovers(versions)

    def test_a_move_that_fails_keeps_every_old_file(self, skill_client, tmp_path, monkeypatch):
        from src import bundle_swap

        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        versions = self._cache(tmp_path)
        before = _snapshot(versions)
        real = bundle_swap.os.replace

        def fail_moving_in(src, dst):
            if _is_move_in(versions / "1", src, dst):
                raise OSError("rename failed")
            real(src, dst)

        monkeypatch.setattr(bundle_swap.os, "replace", fail_moving_in)
        skill_client.get_version_detail.return_value = _full_skill(files=[{"fileName": "new.txt", "content": "n"}])
        skill_client.get_body.return_value = "new body"
        stderr = _body_then_error(runner.invoke(app, ["skill", "get", "my-skill", "--version", "1"]))
        assert "rename failed" in stderr
        assert _snapshot(versions) == before

    def test_a_successful_refresh_replaces_the_hash(self, skill_client, tmp_path, monkeypatch):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        versions = self._cache(tmp_path)
        skill_client.get_version_detail.return_value = _full_skill(files=[{"fileName": "new.txt", "content": "n"}])
        skill_client.get_body.return_value = "new body"
        result = runner.invoke(app, ["skill", "get", "my-skill", "--version", "1"])
        assert result.exit_code == 0, result.stderr
        assert _snapshot(versions) == {"1.hash": b"new-hash", "1/SKILL.md": b"new body", "1/new.txt": b"n"}


class TestMalformedFileFieldsAreActionable:
    @pytest.mark.parametrize("entry", [
        pytest.param({"encoding": 7}, id="number-encoding"),
        pytest.param({"encoding": None}, id="null-encoding"),
        pytest.param({"encoding": ["utf8"]}, id="list-encoding"),
        pytest.param({"executable": "false"}, id="text-executable"),
        pytest.param({"executable": None}, id="null-executable"),
        pytest.param({"executable": 1}, id="number-executable"),
    ])
    @pytest.mark.parametrize("command", ["pull", "get"])
    def test_refused_with_an_error_line_and_help(self, skill_client, tmp_path, monkeypatch, entry, command):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "cache"))
        target = tmp_path / "pulled"
        _existing_skill_with_metadata(target)
        before = _snapshot(target)
        skill_client.get_version_detail.return_value = _full_skill(
            files=[dict({"fileName": "a.txt", "content": "x"}, **entry)]
        )
        skill_client.get_body.return_value = "new body"
        args = ["skill", command, "my-skill", "--version", "1"]
        if command == "pull":
            args += ["--dir", str(target)]
        stderr = _assert_error(runner.invoke(app, args))
        assert "'a.txt'" in stderr
        assert _help_block(stderr)
        assert _snapshot(target) == before
        assert not (tmp_path / "cache").exists()

    def test_an_omitted_encoding_is_not_guessed(self, skill_client, tmp_path):
        target = tmp_path / "pulled"
        detail = _full_skill(files=[{"fileName": "a.txt", "content": "x"}])
        del detail["files"][0]["encoding"]
        skill_client.get_version_detail.return_value = detail
        stderr = _assert_error(
            runner.invoke(app, ["skill", "pull", "my-skill", "--dir", str(target), "--version", "1"])
        )
        assert "no encoding" in stderr
        assert not target.exists()


class TestAPushWithoutANewHashSaysSo:
    @pytest.mark.parametrize("answer", [
        pytest.param({"version": 2}, id="without-hash"),
        pytest.param({"version": 2, "contentHash": ""}, id="empty-hash"),
        pytest.param({"version": 2, "contentHash": 5}, id="number-hash"),
        pytest.param([], id="not-an-object"),
    ])
    @pytest.mark.parametrize("group, client_name, sidecar", [
        ("skill", "skill_client", ".skill-hash"),
        ("workflow", "workflow_client", ".workflow-hash"),
    ])
    def test_the_old_sidecar_is_not_left_looking_current(
        self, request, tmp_path, answer, group, client_name, sidecar
    ):
        client = request.getfixturevalue(client_name)
        getattr(client, f"{group}_exists").return_value = True
        client.update_draft.return_value = answer
        (tmp_path / sidecar).write_text("old-hash")
        stderr = _assert_error(
            runner.invoke(app, [group, "push", "x", "--dir", str(tmp_path)]),
            f'cc-devthrottle {group} pull x --dir "{tmp_path}"',
        )
        assert "did not include the new content hash" in stderr
        assert "Pull before the next push." in stderr
