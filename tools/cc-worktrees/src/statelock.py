"""File locks: one machine-wide lock around every read and write of pool state, and one lock per
repository around its fetch, so the network never runs under the machine-wide lock.

Portions adapted from treehouse (https://github.com/kunchenguid/treehouse), internal/pool/lock_*.go
and state.go. Copyright (c) 2026 kunchenguid. MIT License - see THIRD_PARTY_NOTICES.md.
"""

from __future__ import annotations

import os
import time
from collections.abc import Iterator
from contextlib import contextmanager
from pathlib import Path

from errors import ToolError

LOCK_TIMEOUT_SECONDS = 300.0

if os.name == "nt":
    import msvcrt

    def _try_lock(fd: int) -> None:
        os.lseek(fd, 0, os.SEEK_SET)
        msvcrt.locking(fd, msvcrt.LK_NBLCK, 1)

    def _unlock(fd: int) -> None:
        os.lseek(fd, 0, os.SEEK_SET)
        msvcrt.locking(fd, msvcrt.LK_UNLCK, 1)
else:
    import fcntl

    def _try_lock(fd: int) -> None:
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)

    def _unlock(fd: int) -> None:
        fcntl.flock(fd, fcntl.LOCK_UN)


def machine_lock(home: Path, timeout: float = LOCK_TIMEOUT_SECONDS):
    return file_lock(home / "lock", "the pool lock", timeout)


@contextmanager
def file_lock(path: Path, what: str, timeout: float = LOCK_TIMEOUT_SECONDS) -> Iterator[None]:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd = os.open(path, os.O_RDWR | os.O_CREAT, 0o644)
    try:
        deadline = time.monotonic() + timeout
        while True:
            try:
                _try_lock(fd)
                break
            except OSError:
                if time.monotonic() >= deadline:
                    raise ToolError("lock-timeout",
                                    f"another cc-worktrees command held {what} for {int(timeout)} seconds",
                                    ["Run the command again once the other cc-worktrees command has finished"])
                time.sleep(0.1)
        try:
            yield
        finally:
            _unlock(fd)
    finally:
        os.close(fd)
