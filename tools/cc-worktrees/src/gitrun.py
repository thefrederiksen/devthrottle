"""Run git, and run the host's command-line tool. Every call fails fast instead of prompting for credentials."""

from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
from pathlib import Path

# A credential problem must fail at once as "cannot verify", never sit waiting at a prompt.
_NO_PROMPT_ENV = {"GIT_TERMINAL_PROMPT": "0", "GCM_INTERACTIVE": "never"}

# A replace ref (`git replace`) makes git read another commit wherever the replaced one is named, so an unlanded
# commit replaced by a landed one would read as landed. Every call reads the objects as they really are. This
# overrides any value inherited from the caller's environment.
_REAL_OBJECTS_ENV = {"GIT_NO_REPLACE_OBJECTS": "1"}


class GitError(Exception):
    """A git command exited non-zero."""

    def __init__(self, args: list[str], code: int, stderr: str):
        self.git_args = args
        self.code = code
        self.stderr = stderr
        super().__init__(f"git {' '.join(args)} exited {code}: {self.short()}")

    def short(self) -> str:
        """The first meaningful line of git's error, cut to a readable length."""
        return first_line(self.stderr) or f"exit code {self.code}"


class ToolMissing(Exception):
    """A command-line tool the host layer wanted is not on PATH. The message is the plain reason."""


def first_line(text: str) -> str:
    """The first meaningful line of a tool's error output, cut to a readable length."""
    lines = [line.strip() for line in text.splitlines() if line.strip()]
    if not lines:
        return ""
    chosen = lines[0]
    for line in lines:
        if line.lower().startswith(("fatal:", "error:")):
            chosen = line
            break
    return chosen if len(chosen) <= 200 else chosen[:197] + "..."


def _spawn(exe: str, args: list[str], cwd: Path | str, env: dict[str, str],
           timeout: float | None) -> subprocess.CompletedProcess:
    """Start `exe`, wait for it, and return what it said.

    Output goes to temporary files, not pipes. A helper process the tool started can outlive the tool
    itself, and on Windows it would hold a pipe open and make the wait for output as long as the hang.
    """
    with tempfile.TemporaryFile() as out_file, tempfile.TemporaryFile() as err_file:
        proc = subprocess.Popen([exe, *args], cwd=str(cwd), env=env, stdin=subprocess.DEVNULL,
                                stdout=out_file, stderr=err_file)
        try:
            code = proc.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            # Our own child, started a moment ago by this call; nothing else is touched.
            proc.kill()
            proc.wait()
            raise TimeoutError(f"timed out after {timeout:g} seconds") from None
        out_file.seek(0)
        err_file.seek(0)
        stdout = out_file.read().decode("utf-8", errors="replace")
        stderr = err_file.read().decode("utf-8", errors="replace")
    return subprocess.CompletedProcess(proc.args, code, stdout, stderr)


def run(cwd: Path | str, *args: str, check: bool = True, timeout: float | None = None) -> subprocess.CompletedProcess:
    """Run git. With `timeout`, a git that has not finished by then is stopped and GitError is raised,
    whatever `check` says: a timeout is never an answer."""
    env = {**os.environ, **_NO_PROMPT_ENV, **_REAL_OBJECTS_ENV}
    try:
        result = _spawn("git", list(args), cwd, env, timeout)
    except TimeoutError as ex:
        raise GitError(list(args), -1, str(ex)) from None
    if check and result.returncode != 0:
        raise GitError(list(args), result.returncode, result.stderr)
    return result


def out(cwd: Path | str, *args: str) -> str:
    return run(cwd, *args).stdout.strip()


def run_tool(exe: str, cwd: Path | str, args: list[str], timeout: float,
             env_extra: dict[str, str] | None = None) -> subprocess.CompletedProcess:
    """Run a host's command-line tool (gh, az). Raises ToolMissing when it is not on PATH and when it
    does not finish inside `timeout`: in both cases the host was not asked, and the caller must treat
    that as no answer rather than as a "no"."""
    path = shutil.which(exe)
    if path is None:
        raise ToolMissing(f"{exe} is not on PATH")
    env = {**os.environ, **_NO_PROMPT_ENV, **(env_extra or {})}
    try:
        return _spawn(path, list(args), cwd, env, timeout)
    except TimeoutError as ex:
        raise ToolMissing(f"{exe} {ex}") from None
    except OSError as ex:
        raise ToolMissing(f"{exe} could not be started: {ex.strerror or ex}") from None
