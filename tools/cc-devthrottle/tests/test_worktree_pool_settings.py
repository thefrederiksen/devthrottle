"""`cc-devthrottle worktree pool status / on / off` - the switch a person can reach.

Step 4 built the Director side of the pooled-worktree setting and left no way to turn it on: the
only way was to edit config.json by hand. These three commands are that way, and the property that
matters most about them is not their output - it is that they write the SAME place the Director
reads, under the SAME key. A command that wrote a setting the Director never finds would print
exactly what a working one prints.

So these tests hold the store, the key and the defaults, and the refusals that stop a setting being
stored somewhere it would never be read. That the Director's own `WorktreePoolSettings` reads what
this writes is proven from the other side, in C#, by
`src/CcDirector.Core.UnitTests/Git/WorktreePoolSettingsAgreeWithTheCommandLineTests.cs` - a Python
test cannot run the Director's reader, and a hand-written copy of its key rule here would be exactly
the second implementation this whole design refuses.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import worktree_pool_settings  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

GIT_ENVIRONMENT = {
    "GIT_TERMINAL_PROMPT": "0",
    "GCM_INTERACTIVE": "never",
    "GIT_AUTHOR_NAME": "cc-devthrottle test",
    "GIT_AUTHOR_EMAIL": "test@example.invalid",
    "GIT_COMMITTER_NAME": "cc-devthrottle test",
    "GIT_COMMITTER_EMAIL": "test@example.invalid",
}


def git(cwd: Path, *arguments: str) -> str:
    finished = subprocess.run(["git", *arguments], cwd=str(cwd), env={**os.environ, **GIT_ENVIRONMENT},
                              capture_output=True, text=True)
    assert finished.returncode == 0, f"git {' '.join(arguments)} failed: {finished.stderr.strip()}"
    return finished.stdout.strip()


@pytest.fixture
def home(tmp_path, monkeypatch):
    """A throwaway cc-director root, so nothing here can see or touch this machine's real settings."""
    root = tmp_path / "cc-director-root"
    root.mkdir()
    monkeypatch.setenv("CC_DIRECTOR_ROOT", str(root))
    return root


@pytest.fixture
def repository(tmp_path):
    """A real git repository, because the commands refuse anything that is not one."""
    path = tmp_path / "a repo"
    path.mkdir()
    git(path, "init", "--quiet")
    return path


def status(path, *extra):
    return runner.invoke(app, ["worktree", "pool", "status", "--repo", str(path), *extra])


def as_json(result):
    assert result.exit_code == 0, result.output
    return json.loads(result.output)


# ===== the default is OFF =====


def test_a_repository_nobody_configured_is_off(home, repository):
    answer = as_json(status(repository, "--json"))

    assert answer["pooled"] is False
    assert answer["pool_size"] == worktree_pool_settings.DEFAULT_POOL_SIZE == 4
    # Off is the default AND the answer when there is no settings file at all: nothing about this
    # setting fails open, and "I could not find it" must never read as "yes".
    assert not (home / "config" / "config.json").exists()


def test_on_then_status_says_on(home, repository):
    assert runner.invoke(app, ["worktree", "pool", "on", "--repo", str(repository)]).exit_code == 0

    answer = as_json(status(repository, "--json"))
    assert answer["pooled"] is True
    assert answer["pool_size"] == 4


def test_the_size_defaults_to_four_and_a_named_size_is_kept(home, repository):
    as_json(runner.invoke(app, ["worktree", "pool", "on", "--repo", str(repository), "--json"]))
    assert as_json(status(repository, "--json"))["pool_size"] == 4

    as_json(runner.invoke(app, ["worktree", "pool", "on", "--repo", str(repository),
                                "--size", "9", "--json"]))
    assert as_json(status(repository, "--json"))["pool_size"] == 9

    # Turning it on again is not an instruction to forget how big the pool was.
    as_json(runner.invoke(app, ["worktree", "pool", "on", "--repo", str(repository), "--json"]))
    assert as_json(status(repository, "--json"))["pool_size"] == 9


def test_a_size_below_one_is_refused_rather_than_stored(home, repository):
    result = runner.invoke(app, ["worktree", "pool", "on", "--repo", str(repository),
                                 "--size", "0", "--json"])

    # A stored zero would be a pool that can never hand anything out, which reads as a broken tool.
    assert result.exit_code == 2
    assert json.loads(result.output)["code"] == "usage"
    assert as_json(status(repository, "--json"))["pooled"] is False


def test_off_puts_it_back(home, repository):
    runner.invoke(app, ["worktree", "pool", "on", "--repo", str(repository), "--size", "6"])
    assert as_json(status(repository, "--json"))["pooled"] is True

    assert runner.invoke(app, ["worktree", "pool", "off", "--repo", str(repository)]).exit_code == 0

    answer = as_json(status(repository, "--json"))
    assert answer["pooled"] is False
    # Back to the DEFAULT size, not to the six that was stored: off means never configured.
    assert answer["pool_size"] == 4


def test_off_says_that_sessions_already_running_are_untouched(home, repository):
    runner.invoke(app, ["worktree", "pool", "on", "--repo", str(repository)])
    result = runner.invoke(app, ["worktree", "pool", "off", "--repo", str(repository), "--json"])

    note = json.loads(result.output)["note"]
    assert "already running" in note
    assert "gives its slot back" in note


# ===== one repository at a time, and one entry per repository =====


def test_one_repository_does_not_change_another(home, repository, tmp_path):
    other = tmp_path / "other repo"
    other.mkdir()
    git(other, "init", "--quiet")

    runner.invoke(app, ["worktree", "pool", "on", "--repo", str(repository), "--size", "5"])

    assert as_json(status(other, "--json"))["pooled"] is False
    assert as_json(status(repository, "--json"))["pool_size"] == 5


def test_how_the_path_was_typed_does_not_matter(home, repository):
    runner.invoke(app, ["worktree", "pool", "on", "--repo", str(repository), "--size", "3"])

    typed_differently = str(repository).replace("\\", "/") + ("/" if os.name == "nt" else "")
    if os.name == "nt":
        typed_differently = typed_differently.upper()
        assert as_json(status(typed_differently, "--json"))["pool_size"] == 3
    else:
        assert as_json(status(typed_differently.rstrip("/"), "--json"))["pool_size"] == 3


def test_writing_the_setting_keeps_every_other_section_of_the_file(home, repository):
    config = home / "config" / "config.json"
    config.parent.mkdir(parents=True)
    config.write_text(json.dumps({"gateway": {"url": "https://example.invalid"},
                                  "worktreePool": {"somethingElse": 1}}), encoding="utf-8")

    runner.invoke(app, ["worktree", "pool", "on", "--repo", str(repository)])

    document = json.loads(config.read_text(encoding="utf-8"))
    # config.json is shared with the Director, with `cc-devthrottle settings` and with the Gateway
    # settings route. A writer that serialised its own model over the top would drop all of this.
    assert document["gateway"]["url"] == "https://example.invalid"
    assert document["worktreePool"]["somethingElse"] == 1
    assert document["worktreePool"]["repoDefaults"]


# ===== the refusals =====


def test_a_path_that_is_not_a_git_repository_is_refused(home, tmp_path):
    plain = tmp_path / "just a folder"
    plain.mkdir()

    result = runner.invoke(app, ["worktree", "pool", "on", "--repo", str(plain), "--json"])

    assert result.exit_code == 1
    assert json.loads(result.output)["code"] == "not-a-repository"


def test_a_path_that_does_not_exist_is_refused(home, tmp_path):
    result = runner.invoke(app, ["worktree", "pool", "status", "--repo",
                                 str(tmp_path / "nothing here"), "--json"])

    assert result.exit_code == 1
    assert json.loads(result.output)["code"] == "not-a-repository"


def test_a_linked_worktree_is_refused_and_names_the_repository(home, repository, tmp_path):
    # The quiet one. The setting is keyed on the path, and the Director opens sessions at the
    # REPOSITORY, so a setting stored under one of its worktrees is one the Director never reads -
    # and the command would have looked like it worked.
    (repository / "a.txt").write_text("hello\n", encoding="utf-8", newline="\n")
    git(repository, "add", "--", "a.txt")
    git(repository, "commit", "--quiet", "-m", "first")
    linked = tmp_path / "linked"
    git(repository, "worktree", "add", "--quiet", "-b", "side", str(linked))

    result = runner.invoke(app, ["worktree", "pool", "on", "--repo", str(linked), "--json"])

    assert result.exit_code == 1
    payload = json.loads(result.output)
    assert payload["code"] == "not-the-repository-root"
    assert str(repository) in payload["help"][0]


def test_a_settings_file_that_cannot_be_read_is_not_overwritten(home, repository):
    config = home / "config" / "config.json"
    config.parent.mkdir(parents=True)
    config.write_text("{ this is not json", encoding="utf-8")

    result = runner.invoke(app, ["worktree", "pool", "on", "--repo", str(repository), "--json"])

    assert result.exit_code == 1
    assert json.loads(result.output)["code"] == "unreadable-settings"
    # The user's settings are still there to repair. Resetting them would destroy recoverable data
    # to hide a problem.
    assert config.read_text(encoding="utf-8") == "{ this is not json"


# ===== the help lines are commands, not values =====


def test_a_help_line_is_a_command_that_can_be_pasted(home, tmp_path):
    plain = tmp_path / "just a folder"
    plain.mkdir()

    result = runner.invoke(app, ["worktree", "pool", "on", "--repo", str(plain)])

    # A help line is a COMMAND. Running it through the value escape doubles every backslash, and a
    # Windows path then comes out as `D:\\repo`, which nobody can paste.
    assert "\\\\" not in result.output.split("help")[-1]


# ===== the setting says when it takes effect, and states what is true =====


def test_the_answer_says_when_the_change_is_seen(home, repository):
    note = as_json(status(repository, "--json"))["note"]

    assert "next session" in note
    assert "not moved" in note


def test_no_director_running_is_said_as_that_and_not_as_a_promise(home, repository):
    # The throwaway root has no Director registrations at all.
    assert worktree_pool_settings.local_directors() == 0
    assert "No Director is running" in as_json(status(repository, "--json"))["note"]


def test_a_registration_whose_process_is_gone_does_not_count(home, repository):
    instances = home / "config" / "director" / "instances"
    instances.mkdir(parents=True)
    # A registration outlives the process that wrote it, so the file alone proves nothing. 2 is the
    # idle process on Windows and init on Unix, so a plainly impossible id is used instead.
    (instances / "dead.json").write_text(json.dumps({"DirectorId": "dead", "Pid": 999999999}),
                                         encoding="utf-8")

    assert worktree_pool_settings.local_directors() == 0
