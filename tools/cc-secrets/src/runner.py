"""Running a command with a secret supplied, and handing back only its scrubbed result.

Three ways to supply the secret, chosen by the caller:

- stdin:   the secret and a newline are written to the command's standard input (for `sudo -S`).
- env:     the secret is placed in one named environment variable of the command only. Several entries can
           be supplied to one command this way, each in its own variable (an API key and its secret, say).
- askpass: SUDO_ASKPASS, SSH_ASKPASS and GIT_ASKPASS point at a helper that fetches the secret from a
           one-shot loopback listener guarded by a random per-run token.

The command's output is captured in full and scrubbed as bytes, in every encoding it may be in, before it
is decoded and returned. It is not streamed: a secret split across two reads would slip past a scrub of
each read on its own.
"""

from __future__ import annotations

import hmac
import os
import secrets
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
from dataclasses import dataclass
from pathlib import Path
from typing import List, Optional, Tuple

from . import filelog
from .errors import InputError
from .redact import SCRUBBER
from .store import Entry

VIA_CHOICES = ("stdin", "env", "askpass")
DEFAULT_ENV_NAME = "CC_SECRET"
ASKPASS_MAX_REQUESTS = 3


@dataclass
class RunResult:
    exit_code: int
    stdout: str
    stderr: str
    timed_out: bool


class _AskpassListener:
    """A loopback listener that answers at most ASKPASS_MAX_REQUESTS token-bearing requests."""

    def __init__(self, secret: str) -> None:
        self._secret = secret
        self.token = secrets.token_urlsafe(32)
        self._sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._sock.bind(("127.0.0.1", 0))
        self._sock.listen(4)
        self._sock.settimeout(0.5)
        self.port = self._sock.getsockname()[1]
        self._stop = threading.Event()
        self.served = 0
        self._thread = threading.Thread(target=self._serve, daemon=True)
        self._thread.start()

    def _serve(self) -> None:
        while not self._stop.is_set() and self.served < ASKPASS_MAX_REQUESTS:
            try:
                conn, _ = self._sock.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            with conn:
                conn.settimeout(5)
                try:
                    presented = conn.makefile("rb").readline().strip().decode("ascii", "replace")
                except OSError:
                    continue
                if hmac.compare_digest(presented, self.token):
                    self.served += 1
                    conn.sendall(b"OK\n" + self._secret.encode("utf-8"))
                else:
                    conn.sendall(b"NO\n")

    def close(self) -> None:
        self._stop.set()
        self._sock.close()
        self._thread.join(timeout=2)


def _askpass_script(folder: Path) -> Path:
    helper = Path(__file__).with_name("askpass_helper.py")
    if sys.platform == "win32":
        script = folder / "askpass.cmd"
        script.write_text(f'@"{sys.executable}" "{helper}" %*\r\n', encoding="ascii")
    else:
        script = folder / "askpass.sh"
        script.write_text(f'#!/bin/sh\nexec "{sys.executable}" "{helper}" "$@"\n', encoding="utf-8")
        os.chmod(script, 0o700)
    return script


def _resolve_program(command: List[str]) -> List[str]:
    found = shutil.which(command[0])
    if found is None:
        raise InputError(f"Command not found: {command[0]}")
    return [found] + command[1:]


def run_with_secret(entry: Entry, command: List[str], via: str, env_name: str = DEFAULT_ENV_NAME,
                    timeout_seconds: float = 600) -> RunResult:
    """Run `command` with the entry's secret supplied `via` stdin, env or askpass."""
    return run_with_secrets([(entry, env_name)], command, via, timeout_seconds)


def run_with_secrets(supplied: List[Tuple[Entry, str]], command: List[str], via: str,
                     timeout_seconds: float = 600) -> RunResult:
    """Run `command` with each (entry, variable name) supplied. Several entries are only supplied by env, each in
    its own variable. A timeout of 0 means no limit."""
    if not command:
        raise InputError("No command was given. Put it after '--', for example: run devlinux -- sudo -S true")
    if via not in VIA_CHOICES:
        raise InputError(f"--via must be one of: {', '.join(VIA_CHOICES)}")
    if not supplied:
        raise InputError("No entry was given.")
    if len(supplied) > 1 and via != "env":
        raise InputError("Several entries can only be supplied with --via env, each in its own variable.")
    names = [env for _, env in supplied]
    # Windows environment variable names ignore letter case: FOO_BAR and foo_bar are one variable there.
    compared = [n.upper() for n in names] if sys.platform == "win32" else names
    if len(set(compared)) != len(compared):
        raise InputError(f"Two entries would go into the same variable ({', '.join(names)}). Give each its own.")
    label = ",".join(entry.name for entry, _ in supplied)
    filelog.write(f"[runner] run_with_secrets: entries={label}, via={via}, program={Path(command[0]).name}")

    for entry, _ in supplied:
        if not entry.is_setting:
            SCRUBBER.add(entry.secret.reveal(), entry.username)
    secret = supplied[0][0].secret.reveal()
    argv = _resolve_program(command)
    env = dict(os.environ)
    stdin_data = None
    listener = None
    temp_dir = None
    timeout: Optional[float] = None if timeout_seconds == 0 else timeout_seconds
    try:
        if via == "stdin":
            stdin_data = (secret + "\n").encode("utf-8")
        elif via == "env":
            for entry, variable in supplied:
                env[variable] = entry.secret.reveal()
        else:
            listener = _AskpassListener(secret)
            temp_dir = tempfile.TemporaryDirectory(prefix="cc-secrets-askpass-")
            script = str(_askpass_script(Path(temp_dir.name)))
            env.update({
                "CC_SECRETS_ASKPASS_PORT": str(listener.port),
                "CC_SECRETS_ASKPASS_TOKEN": listener.token,
                "SUDO_ASKPASS": script,
                "SSH_ASKPASS": script,
                "SSH_ASKPASS_REQUIRE": "force",
                "GIT_ASKPASS": script,
            })

        timed_out = False
        process = subprocess.Popen(
            argv,
            stdin=subprocess.PIPE if stdin_data is not None else subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=env,
        )
        try:
            out, err = process.communicate(input=stdin_data, timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            process.kill()
            out, err = process.communicate()
        result = RunResult(
            exit_code=process.returncode,
            stdout=SCRUBBER.decode_scrubbed(out),
            stderr=SCRUBBER.decode_scrubbed(err),
            timed_out=timed_out,
        )
        filelog.write(f"[runner] run_with_secrets: entries={label}, exit={result.exit_code}, timedOut={timed_out}")
        return result
    finally:
        if listener is not None:
            listener.close()
        if temp_dir is not None:
            temp_dir.cleanup()
