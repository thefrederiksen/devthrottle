"""Step 1c, item 1: a stash holds the slot it was made in, not every slot in the repository.

Step 1b made a moved `refs/stash` hold the slot, which is why nothing is lost. But `refs/stash` is
repository-wide, so the comparison held EVERY slot whose record predated the stash - including one
made by the developer in their own checkout, or in another slot. The only way out was a manual
`release`, and in a fleet with several slots that is an ordinary week.

The rule this file pins down: a stash entry belongs to a slot when the repository can SHOW it does,
and to no slot when it cannot.

- A stash commit's first parent is the HEAD it was made from. An added entry whose first parent is a
  commit this slot was at - its current HEAD, or a commit in its own HEAD reflog - is this slot's,
  and holds it.
- An added entry that belongs to another slot or to the developer's own checkout does not hold it.
- An entry that cannot be attributed HOLDS the slot: an unreadable first parent, a stash log that
  does not agree with `refs/stash`, a recorded entry that is no longer in the stack. Never free on an
  unknown.

The remaining cost is asserted too, in the last test: two checkouts sitting on the SAME commit cannot
be told apart, and the stash then holds the slot.
"""

from __future__ import annotations

from pathlib import Path

from conftest import commit_file, git

EXIT_HELD = 3


def _dirty_stash(path: Path, name: str, message: str) -> str:
    """Make a stash in `path`, the way anyone would: change a file, then push it."""
    (path / name).write_text(f"{message}\n", encoding="utf-8", newline="\n")
    git(path, "add", "--", name)
    git(path, "stash", "push", "-m", message)
    return git(path, "rev-parse", "refs/stash")


def _own_commit(path: Path, name: str, message: str) -> str:
    """Put this checkout on a commit of its own, so what it stashes is plainly its own."""
    return commit_file(path, name, f"{message}\n", message)


def _stash_log(repo: Path) -> Path:
    common = git(repo, "rev-parse", "--path-format=absolute", "--git-common-dir")
    return Path(common) / "logs" / "refs" / "stash"


def _entry_with_no_first_parent(repo: Path) -> str:
    """Put an entry nothing can attribute on top of the stash: a root commit has no first parent."""
    root = git(repo, "rev-list", "--max-parents=0", "HEAD")
    line = (f"{git(repo, 'rev-parse', 'refs/stash')} {root} cc-worktrees test "
            f"<test@example.invalid> 1700000000 +0000\tWIP on main: an entry with no first parent\n")
    with open(_stash_log(repo), "a", encoding="ascii", newline="") as f:
        f.write(line)
    git(repo, "update-ref", "refs/stash", root)
    return root


# ---------------------------------------------------------------------------------------------------
# The false holds this step removes
# ---------------------------------------------------------------------------------------------------


def test_a_stash_in_the_developers_own_checkout_does_not_hold_the_slot(local_world):
    w = local_world
    got = w.get()
    own = _own_commit(w.repo, "notes.txt", "work in the developers own checkout")
    stash = _dirty_stash(w.repo, "notes.txt", "the developers own stash")
    assert git(w.repo, "rev-parse", f"{stash}^1") == own, "the stash was not made from the checkouts own commit"

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == 0, f"the slot was held for a stash that is not its own: {res.out}{res.err}"
    assert w.slot(got["slot"])["state"] == "free"
    assert git(w.repo, "rev-parse", "refs/stash") == stash, "the tool must never touch the stash"


def test_a_stash_in_another_slot_does_not_hold_this_one(local_world):
    w = local_world
    mine = w.get(holder="mine")
    other = w.get(holder="other")
    other_path = Path(other["path"])
    _own_commit(other_path, "other.txt", "work in the other slot")
    _dirty_stash(other_path, "other.txt", "the other slots stash")

    res = w.run("return", mine["path"], "--lease", mine["lease"], "--json")

    assert res.code == 0, f"one slots stash held another slot: {res.out}{res.err}"
    assert w.slot(mine["slot"])["state"] == "free"
    assert w.slot(other["slot"])["state"] == "in-use"


def test_destroy_is_not_blocked_by_a_stash_that_is_not_the_slots(local_world):
    w = local_world
    got = w.get()
    _own_commit(w.repo, "notes.txt", "work in the developers own checkout")
    _dirty_stash(w.repo, "notes.txt", "the developers own stash")

    res = w.run("destroy", got["path"], "--yes", "--allow-in-use", "--json")

    assert res.code == 0, f"destroy was blocked by a stash that is not the slots: {res.out}{res.err}"
    assert not Path(got["path"]).is_dir()


# ---------------------------------------------------------------------------------------------------
# The true holds, which must not loosen
# ---------------------------------------------------------------------------------------------------


def test_a_stash_made_in_the_slot_still_holds_it(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    _dirty_stash(path, "README.md", "work stashed in the slot")
    assert git(path, "status", "--porcelain") == "", "a stash leaves the tree clean, which is the trap"

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    reason = w.slot(got["slot"])["reason"]
    assert "stash" in reason and "work stashed in the slot" in reason, reason


def test_a_stash_made_in_the_slot_before_it_moved_on_still_holds_it(local_world):
    """The slot's HEAD reflog, not only its current HEAD: the stash was made before the slot moved on."""
    w = local_world
    got = w.get()
    path = Path(got["path"])
    _dirty_stash(path, "README.md", "stashed before the slot moved on")
    _own_commit(path, "later.txt", "a commit made after the stash")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    reason = w.slot(got["slot"])["reason"]
    assert "stash" in reason and "stashed before the slot moved on" in reason, reason


def test_destroy_still_refuses_a_slot_whose_own_stash_moved(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    _dirty_stash(path, "README.md", "work destroy must not walk past")

    res = w.run("destroy", got["path"], "--yes", "--allow-in-use", "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert path.is_dir(), "the slot was removed although its stashed work had landed nowhere"
    assert "stash" in w.slot(got["slot"])["reason"]


# ---------------------------------------------------------------------------------------------------
# An entry that cannot be attributed holds the slot
# ---------------------------------------------------------------------------------------------------


def test_an_entry_whose_first_parent_cannot_be_read_holds_the_slot(local_world):
    """A root commit has no first parent, so nothing can say which worktree the entry came from."""
    w = local_world
    got = w.get()
    _own_commit(w.repo, "notes.txt", "work in the developers own checkout")
    _dirty_stash(w.repo, "notes.txt", "the developers own stash")
    _entry_with_no_first_parent(w.repo)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, f"an entry nothing could attribute freed the slot: {res.out}{res.err}"
    assert "stash" in w.slot(got["slot"])["reason"], w.slot(got["slot"])["reason"]


def test_a_stash_log_that_does_not_agree_with_refs_stash_holds_the_slot(local_world):
    """`git stash list` reads the stash LOG. A log git cannot read comes back empty and exit 0, which
    would otherwise read as "no entries were added" while refs/stash plainly moved."""
    w = local_world
    got = w.get()
    _own_commit(w.repo, "notes.txt", "work in the developers own checkout")
    _dirty_stash(w.repo, "notes.txt", "the developers own stash")
    _stash_log(w.repo).write_text("this is not a reflog line at all\n", encoding="ascii", newline="")
    assert git(w.repo, "stash", "list") == "", "the crafted log still lists entries; the case is not staged"

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, f"an unreadable stash log freed the slot: {res.out}{res.err}"
    assert "stash" in w.slot(got["slot"])["reason"], w.slot(got["slot"])["reason"]


def test_dropping_the_entry_the_tool_recorded_holds_the_slot(local_world):
    """With the recorded entry gone, which entries are new cannot be said at all."""
    w = local_world
    _own_commit(w.repo, "notes.txt", "work in the developers own checkout")
    _dirty_stash(w.repo, "notes.txt", "a stash made before the slot was handed out")
    got = w.get()
    _dirty_stash(w.repo, "notes.txt", "a second stash of the developers own")
    git(w.repo, "stash", "drop", "stash@{1}")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, f"a rewritten stash stack freed the slot: {res.out}{res.err}"
    assert "stash" in w.slot(got["slot"])["reason"], w.slot(got["slot"])["reason"]


# ---------------------------------------------------------------------------------------------------
# release keeps pinning what the slot owns
# ---------------------------------------------------------------------------------------------------


def test_release_pins_the_stash_the_slot_owns(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    stash = _dirty_stash(path, "README.md", "work the release must keep")
    assert w.run("return", got["path"], "--lease", got["lease"], "--json").code == EXIT_HELD

    res = w.run("release", got["slot"], "--repo", str(w.repo), "--confirm-abandon", "--json")

    assert res.code == 0, res.out + res.err
    pinned = [p["commit"] for p in res.data["pins"]]
    assert stash in pinned, f"the slots own stash was not pinned: {res.data}"
    git(w.repo, "gc", "--prune=now", "--quiet")
    assert git(w.repo, "rev-parse", "--verify", f"{stash}^{{commit}}") == stash


def test_release_does_not_pin_a_stash_that_belongs_to_the_developers_checkout(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    abandoned = _own_commit(path, "mine.txt", "work only this slot has")
    _own_commit(w.repo, "notes.txt", "work in the developers own checkout")
    theirs = _dirty_stash(w.repo, "notes.txt", "the developers own stash")
    assert w.run("return", got["path"], "--lease", got["lease"], "--json").code == EXIT_HELD

    res = w.run("release", got["slot"], "--repo", str(w.repo), "--confirm-abandon", "--json")

    assert res.code == 0, res.out + res.err
    pinned = [p["commit"] for p in res.data["pins"]]
    assert abandoned in pinned, f"the slots own commit was not pinned: {res.data}"
    assert theirs not in pinned, "the developers own stash was pinned to a slot it never belonged to"


def test_release_pins_an_entry_it_cannot_attribute(local_world):
    """Held on an unknown, so the unknown is kept: the release pins it rather than leave it to gc."""
    w = local_world
    got = w.get()
    _own_commit(w.repo, "notes.txt", "work in the developers own checkout")
    _dirty_stash(w.repo, "notes.txt", "the developers own stash")
    root = _entry_with_no_first_parent(w.repo)
    assert w.run("return", got["path"], "--lease", got["lease"], "--json").code == EXIT_HELD

    res = w.run("release", got["slot"], "--repo", str(w.repo), "--confirm-abandon", "--json")

    assert res.code == 0, res.out + res.err
    assert root in [p["commit"] for p in res.data["pins"]], f"the unattributable entry was not kept: {res.data}"


# ---------------------------------------------------------------------------------------------------
# The stated cost of the rule
# ---------------------------------------------------------------------------------------------------


def test_two_checkouts_on_the_same_commit_cannot_be_told_apart(local_world):
    """The remaining cost, asserted so it cannot be softened without a test going red.

    A stash made in the developer's own checkout while it sits on the SAME commit the slot was reset
    to has the slot's own HEAD as its first parent. The repository cannot show whose it is, so it
    holds the slot - the tool never guesses in the direction that frees work.
    """
    w = local_world
    got = w.get()
    assert git(w.repo, "rev-parse", "HEAD") == git(Path(got["path"]), "rev-parse", "HEAD")
    _dirty_stash(w.repo, "README.md", "a stash made from the very same commit")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "stash" in w.slot(got["slot"])["reason"]
