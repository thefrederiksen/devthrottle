"""Close-out: a replace ref (`git replace`) makes git read another commit wherever the one it replaces is named.
Pointing an unlanded commit at a landed one made rev-list, cherry and for-each-ref --contains answer "landed", so
`return` freed the slot and `destroy` removed it. Every git call the tool makes ignores replace refs, so the commit
the slot actually holds is the one that is checked."""

from __future__ import annotations

from pathlib import Path

import pytest

from conftest import commit_file, git, on_any_ref

EXIT_HELD = 3


def _pins(repo: Path, slot: str) -> list[str]:
    return git(repo, "for-each-ref", "--format=%(objectname)", f"refs/cc-worktrees/{slot}/").split()


def _replaced_commit(w, abandon: bool) -> tuple[dict, str]:
    got = w.get()
    path = Path(got["path"])
    base = git(path, "rev-parse", "HEAD")
    commit = commit_file(path, "x.txt", "the only copy\n", "X")
    if abandon:
        git(path, "reset", "-q", "--hard", base)
    git(w.repo, "replace", "-f", commit, base)
    return got, commit


@pytest.mark.parametrize("abandon", [True, False], ids=["abandoned-in-reflog", "at-head"])
def test_return_holds_a_replaced_commit_naming_it_and_pins_it(local_world, abandon):
    w = local_world
    got, commit = _replaced_commit(w, abandon)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, f"return did not hold ({res.code}): {res.out} {res.err}"
    reason = w.slot(got["slot"])["reason"]
    assert commit[:12] in reason, reason
    assert _pins(w.repo, got["slot"]) == [commit]


@pytest.mark.parametrize("abandon", [True, False], ids=["abandoned-in-reflog", "at-head"])
def test_destroy_refuses_a_slot_with_a_replaced_commit_and_the_commit_survives(local_world, abandon):
    w = local_world
    got, commit = _replaced_commit(w, abandon)

    res = w.run("destroy", got["slot"], "--repo", str(w.repo), "--yes", "--allow-held", "--allow-in-use", "--json")

    assert res.code == EXIT_HELD, f"destroy did not refuse ({res.code}): {res.out} {res.err}"
    assert commit[:12] in res.out + res.err
    assert Path(got["path"]).is_dir()
    git(w.repo, "replace", "-d", commit)
    assert on_any_ref(w.repo, commit)
