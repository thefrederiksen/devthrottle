"""`--version` answers, and it answers from the SAME string as `pyproject.toml`.

The Director's Tools page attaches a universal "--version responds" check to every tool in its
manifest and fails the row on a non-zero exit, so a shipped tool without the flag shows red. That is
why the flag exists. These tests hold the two things that make the answer worth printing: it exits 0
with a version on it, and the version is the one in `pyproject.toml` rather than a second copy in the
source that drifts away from it.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tomllib
from pathlib import Path

TOOL_DIR = Path(__file__).resolve().parents[1]
MAIN = TOOL_DIR / "main.py"
PYPROJECT = TOOL_DIR / "pyproject.toml"
DIST_NAME = "cc-worktrees"


def pyproject_version() -> str:
    return tomllib.loads(PYPROJECT.read_text(encoding="utf-8"))["project"]["version"]


def run(*args: str) -> subprocess.CompletedProcess:
    """The checkout form: `python tools/cc-worktrees/main.py ...`."""
    return subprocess.run([sys.executable, str(MAIN), *args], capture_output=True, text=True,
                          env={**os.environ})


def test_version_exits_zero_and_prints_a_version():
    """The check the Tools page runs, exactly as it runs it: no subcommand, plain output, exit 0."""
    res = run("--version")

    assert res.returncode == 0, f"exit {res.returncode}: {res.stdout} {res.stderr}"
    assert res.stdout.isascii()
    lines = res.stdout.splitlines()
    assert lines[0] == f"tool: {DIST_NAME}"
    assert lines[1].startswith("version: ")
    assert lines[1].removeprefix("version: ").strip()


def test_the_version_it_prints_is_the_one_in_pyproject():
    """One source of truth. A hard-coded string in the source would pass the test above and still be
    wrong the first time somebody bumps `pyproject.toml` and forgets the copy."""
    res = run("--version")

    assert res.returncode == 0, res.stdout + res.stderr
    printed = res.stdout.splitlines()[1].removeprefix("version: ").strip()
    assert printed == pyproject_version(), (
        f"the tool printed {printed} and pyproject.toml says {pyproject_version()}. A checkout "
        f"answers from pyproject.toml unless a distribution of this name is installed - which a "
        f"left-over {TOOL_DIR / 'cc_worktrees.egg-info'} from a wheel build also counts as. If that "
        f"directory is there and stale, delete it and run again.")


def test_version_json_is_the_same_answer_in_the_json_shape():
    res = run("--version", "--json")

    assert res.returncode == 0, res.stdout + res.stderr
    assert json.loads(res.stdout) == {"tool": DIST_NAME, "version": pyproject_version()}


def test_the_installed_shape_answers_from_its_distribution_metadata(tmp_path):
    """The checkout answers from `pyproject.toml`. An INSTALLED copy has no `pyproject.toml` - the
    wheel ships the package directory alone - so it must answer from its distribution metadata.

    Built here the way an install is laid out: the package directory beside its `.dist-info`, entered
    through the console script's own entry point (`cc_worktrees.cli:main`), with nothing of the
    checkout on the import path. The recorded version is deliberately NOT the one in
    `pyproject.toml`, so an implementation that only ever read `pyproject.toml` could not pass this -
    it would print the checkout's version, or fail on a file that is not there.
    """
    site = tmp_path / "site-packages"
    shutil.copytree(TOOL_DIR / "src", site / "cc_worktrees",
                    ignore=shutil.ignore_patterns("__pycache__"))
    dist = site / "cc_worktrees-9.9.9.dist-info"
    dist.mkdir()
    metadata_lines = ["Metadata-Version: 2.1", f"Name: {DIST_NAME}", "Version: 9.9.9", ""]
    (dist / "METADATA").write_text("\n".join(metadata_lines), encoding="utf-8")
    assert not (site / "cc_worktrees" / "pyproject.toml").exists(), "control: no pyproject.toml here"
    assert pyproject_version() != "9.9.9", "control: the checkout's own version is not 9.9.9"
    console_script = "import sys; from cc_worktrees.cli import main; sys.exit(main())"

    # cwd is the temp directory, so sys.path[0] carries nothing of the checkout with it. The only
    # other entry is tools/, which supplies cc_shared - an installed copy has it in its virtual
    # environment as a real dependency. It holds no cc-worktrees metadata of its own, so the 9.9.9
    # distribution above is the only one this run can find.
    env = {**os.environ, "PYTHONPATH": os.pathsep.join([str(site), str(TOOL_DIR.parent)])}
    res = subprocess.run([sys.executable, "-c", console_script, "--version"], cwd=str(tmp_path),
                         capture_output=True, text=True, env=env)

    assert res.returncode == 0, f"exit {res.returncode}: {res.stdout} {res.stderr}"
    assert res.stdout.splitlines()[:2] == [f"tool: {DIST_NAME}", "version: 9.9.9"]


def test_version_is_a_top_level_flag_only():
    """`--version` belongs to the tool, not to its commands. A subcommand that quietly accepted it
    would swallow a typo instead of failing on it, which the AXI standard calls a defect."""
    res = run("list", "--version")

    assert res.returncode == 2, f"exit {res.returncode}: {res.stdout} {res.stderr}"
    assert "error:" in res.stdout
