"""Fix round 2, finding 5: the proof and the reset use the tracking ref as it is under the machine-wide
lock, never a tip returned before it, and the repository's fetch lock is held until the act is done."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

from conftest import TOOL_DIR, commit_file, git


def _advance_remote_and_fetch_after(w, landed, monkeypatch):
    """Wrap the fetch so that, once it has answered, the remote moves on and a plain git fetch records it."""
    original = landed.fetch_default
    moved: list[str] = []

    def fetch_then_the_remote_moves(cwd):
        answer = original(cwd)
        other = w.second_clone()
        moved.append(commit_file(other, f"later-{len(moved)}.txt", "later\n", "the default branch moves on"))
        git(other, "push", "-q", "origin", w.default_branch)
        git(w.repo, "fetch", "-q", "origin")
        return answer

    monkeypatch.setattr(landed, "fetch_default", fetch_then_the_remote_moves)
    return moved


def test_get_hands_out_the_tracking_ref_read_under_the_lock(in_process, monkeypatch):
    w, pool, landed = in_process
    moved = _advance_remote_and_fetch_after(w, landed, monkeypatch)

    got = pool.get(str(w.repo), "owner", 4)

    assert moved
    current = git(w.repo, "rev-parse", f"origin/{w.default_branch}")
    assert current == moved[-1]
    assert got["commit"] == current
    assert git(Path(got["path"]), "rev-parse", "HEAD") == current


def test_return_resets_to_the_tracking_ref_read_under_the_lock(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    moved = _advance_remote_and_fetch_after(w, landed, monkeypatch)

    returned = pool.return_slot(got["path"], got["lease"], None)

    current = git(w.repo, "rev-parse", f"origin/{w.default_branch}")
    assert current == moved[-1]
    assert returned["commit"] == current
    assert git(Path(got["path"]), "rev-parse", "HEAD") == current


def test_the_fetch_lock_is_held_through_the_act(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    lock = pool.repo_lock_file(w.home, w.repo)
    original_check = landed.check
    probes: list[str] = []

    def check_while_another_process_tries_the_fetch_lock(*args, **kwargs):
        script = (
            "import sys; sys.path.insert(0, sys.argv[1])\n"
            "from pathlib import Path\n"
            "from errors import ToolError\n"
            "from statelock import file_lock\n"
            "try:\n"
            "    with file_lock(Path(sys.argv[2]), 'probe', timeout=0.5):\n"
            "        print('taken')\n"
            "except ToolError:\n"
            "    print('busy')\n")
        out = subprocess.run([sys.executable, "-c", script, str(TOOL_DIR / "src"), str(lock)],
                             capture_output=True, text=True, check=True).stdout.strip()
        probes.append(out)
        return original_check(*args, **kwargs)

    monkeypatch.setattr(landed, "check", check_while_another_process_tries_the_fetch_lock)
    pool.return_slot(got["path"], got["lease"], None)

    assert probes == ["busy"]
