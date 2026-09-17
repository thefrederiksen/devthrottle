"""Run git. Every call fails fast instead of prompting for credentials."""

from __future__ import annotations

import os
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
        lines = [line.strip() for line in self.stderr.splitlines() if line.strip()]
        text = lines[0] if lines else f"exit code {self.code}"
        for line in lines:
            if line.lower().startswith(("fatal:", "error:")):
                text = line
                break
        return text if len(text) <= 200 else text[:197] + "..."


def run(cwd: Path | str, *args: str, check: bool = True, timeout: float | None = None) -> subprocess.CompletedProcess:
    """Run git. With `timeout`, a git that has not finished by then is stopped and GitError is raised,
    whatever `check` says: a timeout is never an answer.

    Output goes to temporary files, not pipes. A remote helper git started can outlive git itself, and
    on Windows it would hold a pipe open and make the wait for output as long as the hang.
    """
    env = {**os.environ, **_NO_PROMPT_ENV, **_REAL_OBJECTS_ENV}
    with tempfile.TemporaryFile() as out_file, tempfile.TemporaryFile() as err_file:
        proc = subprocess.Popen(["git", *args], cwd=str(cwd), env=env, stdin=subprocess.DEVNULL,
                                stdout=out_file, stderr=err_file)
        try:
            code = proc.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            # Our own git child, started a moment ago by this call; nothing else is touched.
            proc.kill()
            proc.wait()
            raise GitError(list(args), -1, f"timed out after {timeout:g} seconds") from None
        out_file.seek(0)
        err_file.seek(0)
        stdout = out_file.read().decode("utf-8", errors="replace")
        stderr = err_file.read().decode("utf-8", errors="replace")
    result = subprocess.CompletedProcess(proc.args, code, stdout, stderr)
    if check and code != 0:
        raise GitError(list(args), code, stderr)
    return result


def out(cwd: Path | str, *args: str) -> str:
    return run(cwd, *args).stdout.strip()
