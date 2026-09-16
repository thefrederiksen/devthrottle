"""Test fixtures for cc-worktrees.

Every test runs real git against a real remote in a temp directory. The remote is a local bare
repository by default. When CC_WORKTREES_TEST_REMOTE_GITHUB or CC_WORKTREES_TEST_REMOTE_AZURE holds
a clone URL, the scenarios marked for hosted remotes run against that remote too, using the
developer's own git credentials. A hosted run pushes only to uniquely named throwaway branches and
deletes only the branches it created.

A hosted run that is skipped is NOT a pass: the terminal summary says so in capitals.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import uuid
from dataclasses import dataclass, field
from pathlib import Path

import pytest

TOOL_DIR = Path(__file__).resolve().parents[1]
MAIN = TOOL_DIR / "main.py"
sys.path.insert(0, str(TOOL_DIR / "src"))
sys.path.insert(0, str(TOOL_DIR.parent))

HOSTED_ENV = {
    "github": "CC_WORKTREES_TEST_REMOTE_GITHUB",
    "azure": "CC_WORKTREES_TEST_REMOTE_AZURE",
}

_SKIPPED_HOSTED: set[str] = set()

GIT_ENV = {
    "GIT_TERMINAL_PROMPT": "0",
    "GCM_INTERACTIVE": "never",
    "GIT_AUTHOR_NAME": "cc-worktrees test",
    "GIT_AUTHOR_EMAIL": "test@example.invalid",
    "GIT_COMMITTER_NAME": "cc-worktrees test",
    "GIT_COMMITTER_EMAIL": "test@example.invalid",
}


def git(cwd: Path, *args: str, check: bool = True) -> str:
    env = {**os.environ, **GIT_ENV}
    proc = subprocess.run(["git", *args], cwd=str(cwd), env=env, capture_output=True, text=True)
    if check and proc.returncode != 0:
        raise AssertionError(f"git {' '.join(args)} failed in {cwd}: {proc.stderr.strip()}")
    return proc.stdout.strip()


def commit_file(cwd: Path, name: str, content: str, message: str) -> str:
    path = cwd / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8", newline="\n")
    git(cwd, "add", "--", name)
    git(cwd, "commit", "-q", "-m", message)
    return git(cwd, "rev-parse", "HEAD")


@dataclass
class Result:
    code: int
    out: str
    err: str

    @property
    def data(self) -> dict:
        return json.loads(self.out)


@dataclass
class World:
    """One remote, one local clone of it, and a private state home for the tool."""

    kind: str
    tmp: Path
    home: Path
    remote_url: str
    repo: Path
    default_branch: str
    created_branches: list[str] = field(default_factory=list)

    def run(self, *args: str, cwd: Path | None = None, env_extra: dict | None = None) -> Result:
        env = {**os.environ, **GIT_ENV, "CC_WORKTREES_HOME": str(self.home), **(env_extra or {})}
        proc = subprocess.run([sys.executable, str(MAIN), *args], cwd=str(cwd or self.tmp), env=env,
                              capture_output=True, text=True)
        return Result(proc.returncode, proc.stdout, proc.stderr)

    def get(self, holder: str = "test-holder", pool_size: int = 4) -> dict:
        res = self.run("get", "--repo", str(self.repo), "--holder", holder, "--pool-size",
                       str(pool_size), "--json")
        assert res.code == 0, f"get failed ({res.code}): {res.out} {res.err}"
        return res.data

    def list(self) -> list[dict]:
        res = self.run("list", "--repo", str(self.repo), "--json")
        assert res.code == 0, f"list failed ({res.code}): {res.out} {res.err}"
        return res.data["slots"]

    def slot(self, name: str) -> dict:
        matches = [s for s in self.list() if s["slot"] == name]
        assert len(matches) == 1, f"slot {name} not listed exactly once: {self.list()}"
        return matches[0]

    def unique_branch(self) -> str:
        name = f"cc-worktrees-test/{uuid.uuid4().hex[:12]}"
        self.created_branches.append(name)
        return name

    def state_file(self) -> Path:
        files = list((self.home / "pools").glob("*.json"))
        assert len(files) == 1, f"expected one pool state file, found {files}"
        return files[0]

    def push_from_other_clone(self, files: dict[str, str], message: str, force_add: bool = False) -> Path:
        """Land a commit on the default branch from a second clone, the way someone else would."""
        other = self.second_clone()
        for name, content in files.items():
            path = other / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding="utf-8", newline="\n")
            git(other, "add", *(["-f"] if force_add else []), "--", name)
        git(other, "commit", "-q", "-m", message)
        git(other, "push", "-q", "origin", self.default_branch)
        return other

    def second_clone(self) -> Path:
        """Another clone of the remote, standing in for work landed from somewhere else."""
        path = self.tmp / f"other-{uuid.uuid4().hex[:6]}"
        git(self.tmp, "clone", "-q", self.remote_url, str(path))
        return path


def _make_local_remote(tmp: Path, default_branch: str = "main") -> str:
    bare = tmp / "remote.git"
    git(tmp, "init", "-q", "--bare", "-b", default_branch, str(bare))
    seed = tmp / "seed"
    git(tmp, "init", "-q", "-b", default_branch, str(seed))
    commit_file(seed, ".gitignore", "bin/\n", "ignore build output")
    commit_file(seed, "README.md", "hello\n", "first")
    git(seed, "remote", "add", "origin", str(bare))
    git(seed, "push", "-q", "origin", default_branch)
    shutil.rmtree(seed, ignore_errors=True)
    return str(bare)


def make_world(tmp: Path, kind: str, default_branch: str = "main") -> World:
    home = tmp / "home"
    home.mkdir()
    if kind == "local":
        url = _make_local_remote(tmp, default_branch)
    else:
        url = os.environ[HOSTED_ENV[kind]]
    repo = tmp / "repo"
    git(tmp, "clone", "-q", url, str(repo))
    head = git(repo, "ls-remote", "--symref", "origin", "HEAD")
    branch = head.splitlines()[0].split()[1].removeprefix("refs/heads/")
    return World(kind, tmp, home, url, repo, branch)


def _remote_params():
    return ["local", "github", "azure"]


@pytest.fixture(params=_remote_params())
def world(request, tmp_path):
    """A world on every configured remote: local always, hosted when its URL is set."""
    kind = request.param
    if kind != "local" and not os.environ.get(HOSTED_ENV[kind]):
        _SKIPPED_HOSTED.add(kind)
        pytest.skip(f"HOSTED RUN SKIPPED: {HOSTED_ENV[kind]} is not set")
    w = make_world(tmp_path, kind)
    yield w
    for branch in w.created_branches:
        if kind != "local":
            git(w.repo, "push", "-q", "origin", "--delete", branch, check=False)


@pytest.fixture
def local_world(tmp_path):
    return make_world(tmp_path, "local")


def pytest_terminal_summary(terminalreporter):
    if _SKIPPED_HOSTED:
        names = ", ".join(sorted(HOSTED_ENV[k] for k in _SKIPPED_HOSTED))
        terminalreporter.write_sep("!", "HOSTED REMOTE RUNS WERE SKIPPED - A SKIP IS NOT A PASS")
        terminalreporter.write_line(f"Not set: {names}")
