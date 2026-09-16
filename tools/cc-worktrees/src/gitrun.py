"""Run git. Every call fails fast instead of prompting for credentials."""

from __future__ import annotations

import os
import subprocess
from pathlib import Path

# A credential problem must fail at once as "cannot verify", never sit waiting at a prompt.
_NO_PROMPT_ENV = {"GIT_TERMINAL_PROMPT": "0", "GCM_INTERACTIVE": "never"}


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


def run(cwd: Path | str, *args: str, check: bool = True) -> subprocess.CompletedProcess:
    env = {**os.environ, **_NO_PROMPT_ENV}
    proc = subprocess.run(["git", *args], cwd=str(cwd), env=env, capture_output=True,
                          stdin=subprocess.DEVNULL)
    stdout = proc.stdout.decode("utf-8", errors="replace")
    stderr = proc.stderr.decode("utf-8", errors="replace")
    result = subprocess.CompletedProcess(proc.args, proc.returncode, stdout, stderr)
    if check and proc.returncode != 0:
        raise GitError(list(args), proc.returncode, stderr)
    return result


def out(cwd: Path | str, *args: str) -> str:
    return run(cwd, *args).stdout.strip()
