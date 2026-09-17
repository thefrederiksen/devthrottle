"""The pooled-worktree commands run cc-worktrees and never answer for it.

`cc-devthrottle worktree get`, `return`, `lease`, `destroy` and `list --pool` exist so that a session
does not have to know a second command name - not so that there is a second implementation of the
landed-work rule. The rule decides whether a worktree can be reset without throwing away a commit
that never reached the remote, and cc-worktrees is the one place it lives. These tests pin the four
properties that keep it that way:

  * the arguments this command is given are the arguments cc-worktrees receives, unchanged;
  * cc-worktrees' exit code is this command's exit code, unchanged - including 3 (held) and 4 (pool
    full), which a caller acts on;
  * `worktree list --pool --json` is character for character what `cc-worktrees list --json` printed,
    proven against a real pool on a real repository rather than against a stub;
  * cc-worktrees missing is a plain refusal that names it, never a local answer instead.

`worktree list` WITHOUT --pool is the fleet view from the Gateway. It is a different question with a
different answer and it is unchanged; a test here holds it to that.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import repo_ops, worktree_pool_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

TOOLS_DIRECTORY = Path(__file__).resolve().parents[2]
CC_WORKTREES_MAIN = TOOLS_DIRECTORY / "cc-worktrees" / "main.py"

GIT_ENVIRONMENT = {
    "GIT_TERMINAL_PROMPT": "0",
    "GCM_INTERACTIVE": "never",
    "GIT_AUTHOR_NAME": "cc-devthrottle test",
    "GIT_AUTHOR_EMAIL": "test@example.invalid",
    "GIT_COMMITTER_NAME": "cc-devthrottle test",
    "GIT_COMMITTER_EMAIL": "test@example.invalid",
}

# A stand-in for cc-worktrees that reports what it was handed and exits with whatever code it is
# told to. It makes the argument list and the exit code observable; it proves nothing about the
# tool itself, which is why the equality tests below use the real one.
STUB = (
    "import json\n"
    "import os\n"
    "import sys\n"
    "\n"
    'sys.stdout.write(json.dumps({"argv": sys.argv[1:]}) + "\\n")\n'
    'sys.exit(int(os.environ.get("STUB_EXIT_CODE", "0")))\n'
)


@pytest.fixture
def stub_tool(tmp_path, monkeypatch):
    """Point the commands at the stub and return a function that runs one and reads it back."""
    path = tmp_path / "stub_cc_worktrees.py"
    path.write_text(STUB, encoding="utf-8")
    monkeypatch.setenv(worktree_pool_ops.EXECUTABLE_ENVIRONMENT_VARIABLE, str(path))

    def invoke(arguments, exit_code=0):
        monkeypatch.setenv("STUB_EXIT_CODE", str(exit_code))
        return runner.invoke(app, arguments)

    return invoke


def _argv(result):
    return json.loads(result.output)["argv"]


def git(cwd: Path, *arguments: str) -> str:
    environment = {**os.environ, **GIT_ENVIRONMENT}
    finished = subprocess.run(
        ["git", *arguments], cwd=str(cwd), env=environment, capture_output=True, text=True
    )
    assert finished.returncode == 0, f"git {' '.join(arguments)} failed: {finished.stderr.strip()}"
    return finished.stdout.strip()


@pytest.fixture
def real_pool(tmp_path, monkeypatch):
    """A real repository with a real cc-worktrees pool, and the real tool pointed at it.

    Everything is in a temporary directory: its own remote (a local bare repository), its own clone,
    and its own CC_WORKTREES_HOME, so the test never sees or touches a pool on this machine.
    """
    remote = tmp_path / "remote.git"
    remote.mkdir()
    git(remote, "init", "--quiet", "--bare")

    repository = tmp_path / "repo"
    git(tmp_path, "clone", "--quiet", str(remote), str(repository))
    (repository / "a.txt").write_text("hello\n", encoding="utf-8", newline="\n")
    git(repository, "add", "--", "a.txt")
    git(repository, "commit", "--quiet", "-m", "first")
    git(repository, "push", "--quiet", "origin", "HEAD")

    home = tmp_path / "cc-worktrees-home"
    monkeypatch.setenv("CC_WORKTREES_HOME", str(home))
    monkeypatch.setenv(worktree_pool_ops.EXECUTABLE_ENVIRONMENT_VARIABLE, str(CC_WORKTREES_MAIN))
    for name, value in GIT_ENVIRONMENT.items():
        monkeypatch.setenv(name, value)

    taken = subprocess.run(
        [sys.executable, str(CC_WORKTREES_MAIN), "get", "--repo", str(repository),
         "--holder", "a-test", "--json"],
        capture_output=True, text=True, env={**os.environ},
    )
    assert taken.returncode == 0, f"cc-worktrees get failed: {taken.stdout} {taken.stderr}"
    return repository


def run_cc_worktrees(*arguments: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, str(CC_WORKTREES_MAIN), *arguments],
        capture_output=True, text=True, encoding="utf-8", env={**os.environ},
    )


# ===== the arguments and the exit code cross unchanged =====


def test_get_hands_its_arguments_to_cc_worktrees_untouched(stub_tool):
    result = stub_tool(["worktree", "get", "--repo", r"D:\a repo", "--holder", "session-7",
                        "--pool-size", "6", "--json"])

    assert result.exit_code == 0
    assert _argv(result) == ["get", "--repo", r"D:\a repo", "--holder", "session-7",
                             "--pool-size", "6", "--json"]


def test_return_hands_its_arguments_to_cc_worktrees_untouched(stub_tool):
    result = stub_tool(["worktree", "return", "wt03", "--lease", "abc123", "--repo", r"D:\repo"])

    assert _argv(result) == ["return", "wt03", "--lease", "abc123", "--repo", r"D:\repo"]


def test_lease_hands_its_arguments_to_cc_worktrees_untouched(stub_tool):
    result = stub_tool(["worktree", "lease", "wt02", "--holder", "session-7", "--reclaim-held"])

    assert _argv(result) == ["lease", "wt02", "--holder", "session-7", "--reclaim-held"]


def test_destroy_adds_nothing_so_it_stays_a_dry_run(stub_tool):
    # cc-worktrees' destroy is a dry run unless --yes. If this command ever supplied a flag of its
    # own, a caller reading "dry run" in the documentation would be removing a worktree.
    result = stub_tool(["worktree", "destroy", "wt01", "--repo", r"D:\repo"])

    assert _argv(result) == ["destroy", "wt01", "--repo", r"D:\repo"]
    assert "--yes" not in _argv(result)


def test_destroy_passes_its_flags_exactly_as_given(stub_tool):
    result = stub_tool(["worktree", "destroy", "wt01", "--yes", "--allow-held",
                        "--allow-in-use", "--repo", r"D:\repo"])

    assert _argv(result) == ["destroy", "wt01", "--yes", "--allow-held", "--allow-in-use",
                             "--repo", r"D:\repo"]


def test_an_argument_with_a_shell_character_arrives_as_it_was_written(stub_tool):
    # The reason the resolution below reaches the executable and not the .cmd shim: cmd.exe would
    # re-parse this holder name.
    holder = 'a & b ^ c "quoted"'
    result = stub_tool(["worktree", "get", "--repo", r"D:\repo", "--holder", holder])

    assert _argv(result) == ["get", "--repo", r"D:\repo", "--holder", holder]


@pytest.mark.parametrize("code", [0, 1, 2, 3, 4])
def test_the_tools_exit_code_is_this_commands_exit_code(stub_tool, code):
    # 3 is "held, with the reason" and 4 is "pool full". A caller branches on them, so collapsing
    # either into a generic failure would turn a worktree that must not be touched into one that
    # looks like an ordinary error.
    result = stub_tool(["worktree", "get", "--repo", r"D:\repo", "--holder", "x"], exit_code=code)

    assert result.exit_code == code


def test_an_argument_cc_worktrees_does_not_know_is_refused_by_cc_worktrees(real_pool):
    result = runner.invoke(app, ["worktree", "list", "--pool", "--repo", str(real_pool),
                                 "--fields", "slot,nonesuch"])

    assert result.exit_code == 2
    assert "nonesuch" in result.output


# ===== the pool listing IS cc-worktrees' listing =====


def test_pool_list_json_is_exactly_what_cc_worktrees_printed(real_pool):
    through_cc_devthrottle = runner.invoke(
        app, ["worktree", "list", "--pool", "--repo", str(real_pool), "--json"]
    )
    direct = run_cc_worktrees("list", "--repo", str(real_pool), "--json")

    assert through_cc_devthrottle.exit_code == 0
    assert direct.returncode == 0
    assert through_cc_devthrottle.output == direct.stdout
    # Not a comparison of two empty answers: there is a pool, with a slot in it.
    assert json.loads(direct.stdout)["count"] == 1


def test_pool_list_text_is_exactly_what_cc_worktrees_printed(real_pool):
    through_cc_devthrottle = runner.invoke(app, ["worktree", "list", "--pool", "--repo", str(real_pool)])
    direct = run_cc_worktrees("list", "--repo", str(real_pool))

    assert through_cc_devthrottle.output == direct.stdout
    assert "wt01" in direct.stdout


def test_pool_list_without_a_repository_asks_cc_worktrees_for_every_pool(real_pool):
    through_cc_devthrottle = runner.invoke(app, ["worktree", "list", "--pool", "--json"])
    direct = run_cc_worktrees("list", "--json")

    assert through_cc_devthrottle.output == direct.stdout
    assert json.loads(direct.stdout)["count"] >= 1


# ===== the fleet listing is a different question and is unchanged =====


def test_without_pool_the_fleet_listing_is_used_and_cc_worktrees_is_never_run(monkeypatch):
    seen = {}

    def fake_list_worktrees(json_output, repo=None, state=None):
        seen.update({"json": json_output, "repo": repo, "state": state})

    def refuse(arguments):
        raise AssertionError(f"the fleet listing must not run cc-worktrees, but it ran {arguments}")

    monkeypatch.setattr(repo_ops, "list_worktrees", fake_list_worktrees)
    monkeypatch.setattr(worktree_pool_ops, "run", refuse)

    result = runner.invoke(app, ["worktree", "list", "--repo", "devthrottle", "--state", "in-use"])

    assert result.exit_code == 0
    assert seen == {"json": False, "repo": "devthrottle", "state": "in-use"}


def test_state_is_refused_with_pool_rather_than_quietly_ignored():
    # A filter that is accepted and not applied answers a question the caller did not ask, and the
    # caller has no way to tell. cc-worktrees' list has no --state, so this command must say so.
    result = runner.invoke(app, ["worktree", "list", "--pool", "--state", "in-use"])

    assert result.exit_code == 2
    assert "--state" in result.output


def test_fields_is_refused_without_pool_rather_than_quietly_ignored(monkeypatch):
    monkeypatch.setattr(repo_ops, "list_worktrees", lambda *a, **k: None)

    result = runner.invoke(app, ["worktree", "list", "--fields", "slot,state"])

    assert result.exit_code == 2
    assert "--fields" in result.output


def test_a_usage_refusal_is_machine_readable_under_json():
    result = runner.invoke(app, ["worktree", "list", "--pool", "--state", "in-use", "--json"])

    payload = json.loads(result.output)
    assert result.exit_code == 2
    assert payload["code"] == "usage"
    assert payload["help"]


# ===== cc-worktrees missing is a refusal, never a local answer =====


@pytest.fixture
def no_tool_anywhere(tmp_path, monkeypatch):
    """No override, no installed executable, and nothing named cc-worktrees on PATH."""
    monkeypatch.delenv(worktree_pool_ops.EXECUTABLE_ENVIRONMENT_VARIABLE, raising=False)
    monkeypatch.setenv("CC_DIRECTOR_ROOT", str(tmp_path / "no-install"))
    empty = tmp_path / "empty-path"
    empty.mkdir()
    monkeypatch.setenv("PATH", str(empty))


def test_a_missing_tool_names_it_and_says_how_to_install_it(no_tool_anywhere):
    result = runner.invoke(app, ["worktree", "get", "--repo", r"D:\repo", "--holder", "x"])

    assert result.exit_code == 1
    assert "cc-worktrees" in result.output
    assert "cc-devthrottle setup update" in result.output


def test_a_missing_tool_is_machine_readable_under_json(no_tool_anywhere):
    result = runner.invoke(app, ["worktree", "list", "--pool", "--json"])

    payload = json.loads(result.output)
    assert result.exit_code == 1
    assert payload["code"] == "cc-worktrees-not-found"
    assert "cc-worktrees" in payload["error"]
    assert "cc-devthrottle setup update" in payload["help"]


def test_an_override_that_is_not_there_says_so_rather_than_looking_elsewhere(tmp_path, monkeypatch):
    monkeypatch.setenv(worktree_pool_ops.EXECUTABLE_ENVIRONMENT_VARIABLE, str(tmp_path / "gone.py"))

    result = runner.invoke(app, ["worktree", "list", "--pool", "--json"])

    payload = json.loads(result.output)
    assert result.exit_code == 1
    assert worktree_pool_ops.EXECUTABLE_ENVIRONMENT_VARIABLE in payload["error"]


# ===== which cc-worktrees is run =====


def test_the_installed_executable_is_preferred_over_the_shim_on_path(tmp_path, monkeypatch):
    """The Windows installer puts the real console script in pyenv\\Scripts and a .cmd shim on PATH.

    The shim runs through cmd.exe, which re-parses the command line, so an argument holding `&`, `^`
    or a newline - a holder name, a path, a held reason - would not arrive as it was written. The
    resolution must reach the executable itself.
    """
    monkeypatch.delenv(worktree_pool_ops.EXECUTABLE_ENVIRONMENT_VARIABLE, raising=False)
    root = tmp_path / "install"
    monkeypatch.setenv("CC_DIRECTOR_ROOT", str(root))

    windows = os.name == "nt"
    scripts = root / "pyenv" / ("Scripts" if windows else "bin")
    scripts.mkdir(parents=True)
    executable = scripts / ("cc-worktrees.exe" if windows else "cc-worktrees")
    executable.write_text("", encoding="utf-8")

    shim_directory = tmp_path / "bin"
    shim_directory.mkdir()
    shim = shim_directory / ("cc-worktrees.cmd" if windows else "cc-worktrees")
    shim.write_text("", encoding="utf-8")
    if not windows:
        os.chmod(shim, 0o755)
        os.chmod(executable, 0o755)
    monkeypatch.setenv("PATH", str(shim_directory) + os.pathsep + os.environ.get("PATH", ""))

    assert shutil.which("cc-worktrees") is not None      # the shim really is findable on PATH
    assert worktree_pool_ops.resolve_tool() == [str(executable)]


def test_a_checkout_main_py_is_run_with_this_interpreter(tmp_path, monkeypatch):
    # How a developer, and the tests above, point at a cc-worktrees that is not installed: a .py file
    # cannot be started on its own, so the interpreter running this command runs it.
    script = tmp_path / "main.py"
    script.write_text("", encoding="utf-8")
    monkeypatch.setenv(worktree_pool_ops.EXECUTABLE_ENVIRONMENT_VARIABLE, str(script))

    assert worktree_pool_ops.resolve_tool() == [sys.executable, str(script)]
