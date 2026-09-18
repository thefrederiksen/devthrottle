"""Round 4, finding 1: `git stash` left a slot's work landed nowhere and the slot was freed anyway.

A stash leaves the tree clean, HEAD where it was and the reflog untouched, so every cleanliness answer
the check reads says the slot is empty. The work lives on `refs/stash`, which is a ref of the MAIN
repository, shared with the developer's own checkout and with every other worktree: its mere existence
says nothing. What says something is that it MOVED while the slot was held, so the tool records
`refs/stash` when it hands a slot out and compares it when it resets or removes one.
"""

from __future__ import annotations

from pathlib import Path

from conftest import git

EXIT_HELD = 3


def _stash_in(path: Path, message: str) -> str:
    (path / "README.md").write_text(f"{message}\n", encoding="utf-8", newline="\n")
    git(path, "stash", "push", "-m", message)
    return git(path, "rev-parse", "refs/stash")


def test_a_stash_made_in_the_slot_holds_it_instead_of_freeing_it(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    stash = _stash_in(path, "work stashed in the slot")
    assert git(path, "status", "--porcelain") == "", "a stash leaves the tree clean, which is the trap"

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    reason = w.slot(got["slot"])["reason"]
    assert "stash" in reason and "work stashed in the slot" in reason, reason
    assert git(w.repo, "rev-parse", "refs/stash") == stash
    assert w.slot(got["slot"])["state"] == "held"


def test_destroy_refuses_a_slot_whose_stash_moved_while_it_was_held(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    _stash_in(path, "work destroy must not walk past")

    res = w.run("destroy", got["path"], "--yes", "--allow-in-use", "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert path.is_dir(), "the slot was removed although its stashed work had landed nowhere"
    assert "stash" in w.slot(got["slot"])["reason"]


def test_a_stash_that_was_there_before_the_slot_was_handed_out_does_not_hold_it(local_world):
    w = local_world
    (w.repo / "README.md").write_text("the developer's own work\n", encoding="utf-8", newline="\n")
    git(w.repo, "stash", "push", "-m", "the developer's own stash")
    before = git(w.repo, "rev-parse", "refs/stash")

    got = w.get()
    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == 0, res.out + res.err
    assert w.slot(got["slot"])["state"] == "free"
    assert git(w.repo, "rev-parse", "refs/stash") == before, "the tool must never touch the stash"


def test_dropping_the_stash_made_in_the_slot_lets_the_return_through(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    _stash_in(path, "work that is given up")
    assert w.run("return", got["path"], "--lease", got["lease"], "--json").code == EXIT_HELD

    again = w.run("lease", got["slot"], "--repo", str(w.repo), "--holder", "again", "--reclaim-held", "--json")
    assert again.code == 0, again.out + again.err
    git(path, "stash", "drop")

    res = w.run("return", got["path"], "--lease", again.data["lease"], "--json")

    assert res.code == 0, res.out + res.err
    assert w.slot(got["slot"])["state"] == "free"


def test_reclaiming_a_held_slot_does_not_forget_the_stash_it_was_held_for(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    _stash_in(path, "work a reclaim must not erase")
    assert w.run("return", got["path"], "--lease", got["lease"], "--json").code == EXIT_HELD

    again = w.run("lease", got["slot"], "--repo", str(w.repo), "--holder", "again", "--reclaim-held", "--json")
    assert again.code == 0, again.out + again.err

    res = w.run("return", got["path"], "--lease", again.data["lease"], "--json")

    assert res.code == EXIT_HELD, "the reclaim re-recorded the stash and the hold was lost"
    assert "stash" in w.slot(got["slot"])["reason"]


def test_a_stash_made_between_the_check_and_the_reset_is_caught_under_the_lock(in_process, monkeypatch):
    """The check is an observation of a moment. The reset takes HEAD.lock and proves everything again
    under it, the stash included - and a stash made in the developer's own checkout leaves the slot's
    own HEAD reflog untouched, so nothing else in the recheck would see it."""
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    original = landed.check

    def check_then_stash_in_the_main_checkout(*args, **kwargs):
        answer = original(*args, **kwargs)
        (w.repo / "README.md").write_text("stashed between the check and the reset\n",
                                          encoding="utf-8", newline="\n")
        git(w.repo, "stash", "push", "-m", "stashed between the check and the reset")
        return answer

    monkeypatch.setattr(landed, "check", check_then_stash_in_the_main_checkout)
    try:
        pool.return_slot(got["path"], got["lease"], None)
        code = 0
    except Exception as ex:
        code = getattr(ex, "exit_code", None)
        assert code is not None, f"escaped {type(ex).__name__}: {ex}"
    monkeypatch.undo()

    assert code == EXIT_HELD
    assert "stash" in w.slot(got["slot"])["reason"], w.slot(got["slot"])["reason"]


def test_a_stash_anywhere_in_the_repository_holds_a_free_slot_at_the_next_get(local_world):
    """The stated cost of the rule, asserted so it cannot be softened without a test going red.

    `refs/stash` is repository-wide. The tool cannot tell a stash made in a slot from one the
    developer made in their own checkout, and it never guesses in the direction that frees work.
    """
    w = local_world
    first = w.get()
    assert w.run("return", first["path"], "--lease", first["lease"], "--json").code == 0
    (w.repo / "README.md").write_text("the developer's own work\n", encoding="utf-8", newline="\n")
    git(w.repo, "stash", "push", "-m", "a stash in the developer's own checkout")

    second = w.get()

    assert second["slot"] != first["slot"], "the free slot was reset although the stash had moved"
    assert w.slot(first["slot"])["state"] == "held"
    assert "stash" in w.slot(first["slot"])["reason"]
