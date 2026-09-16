"""Fix round 1, item 10: a remote that hangs is "cannot verify" after a timeout, never a wait without end,
and a hanging fetch for one repository does not block commands that need no network."""

from __future__ import annotations

import os
import subprocess
import sys
import time

from conftest import GIT_ENV, MAIN, git

EXIT_HELD = 3


def _hang_remote(w, seconds: int) -> None:
    """Point origin at a remote that never answers, through git's ext transport."""
    # In an ext:: URL a plain space separates arguments and "% " is a space inside one.
    args = [sys.executable, "-c", f"import time;time.sleep({seconds})"]
    command = " ".join(arg.replace("%", "%%").replace(" ", "% ") for arg in args)
    git(w.repo, "config", "protocol.ext.allow", "always")
    git(w.repo, "remote", "set-url", "origin", f"ext::{command}")


def test_hanging_remote_times_out_and_holds(local_world):
    w = local_world
    got = w.get()
    _hang_remote(w, 60)

    started = time.monotonic()
    res = w.run("return", got["path"], "--lease", got["lease"], "--json",
                env_extra={"CC_WORKTREES_NETWORK_TIMEOUT": "3"})
    elapsed = time.monotonic() - started

    assert res.code == EXIT_HELD, res.out + res.err
    assert elapsed < 30, f"return took {elapsed:.1f}s"
    reason = w.slot(got["slot"])["reason"]
    assert reason.startswith("cannot verify") and "timed out" in reason


def test_a_hanging_fetch_does_not_hold_the_machine_wide_lock(local_world):
    w = local_world
    got = w.get()
    _hang_remote(w, 60)
    env = {**os.environ, **GIT_ENV, "CC_WORKTREES_HOME": str(w.home), "CC_WORKTREES_NETWORK_TIMEOUT": "20"}
    hanging = subprocess.Popen([sys.executable, str(MAIN), "return", got["path"], "--lease", got["lease"], "--json"],
                               cwd=str(w.tmp), env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        time.sleep(3)
        assert hanging.poll() is None, "the hanging return finished too early to prove anything"
        started = time.monotonic()
        res = w.run("list", "--repo", str(w.repo), "--json")
        elapsed = time.monotonic() - started
        assert res.code == 0, res.out + res.err
        assert elapsed < 10, f"list waited {elapsed:.1f}s behind a hanging fetch"
    finally:
        hanging.wait(timeout=120)
