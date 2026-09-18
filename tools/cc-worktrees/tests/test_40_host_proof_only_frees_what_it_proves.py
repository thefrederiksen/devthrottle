"""The host's answer may only ever turn HELD into FREE, and only under the whole rule.

The host is asked ONE question: does one merged pull request account for every commit git could not prove?
Every part of that question is a separate way to lose work if it is skipped, so each one is broken here on
its own and the answer must be "no proof". The scenario underneath is real: work squash-merged elsewhere,
which git cannot prove on its own.

The tests that drive a live GitHub.com and Azure DevOps pull request are in
test_41_landed_by_a_pull_request.py. These hold the rule itself.
"""

from __future__ import annotations

import dataclasses
from pathlib import Path

import pytest

import host
from conftest import commit_file, git


@dataclasses.dataclass
class Landed:
    """A slot whose two commits were squash-merged on to the default branch, as a host would do it."""

    world: object
    got: dict
    path: Path
    commits: list[str]          # the slot's own commits, newest first
    merge_commit: str           # the squashed commit now on the default branch
    default_tip: str
    default_branch: str


@pytest.fixture
def squashed(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    first = commit_file(path, "feature/a.txt", "one\n", "feature part 1")
    second = commit_file(path, "feature/b.txt", "two\n", "feature part 2")

    other = w.second_clone()
    for name in ("feature/a.txt", "feature/b.txt"):
        (other / name).parent.mkdir(parents=True, exist_ok=True)
        (other / name).write_text("one\n" if name.endswith("a.txt") else "two\n", encoding="utf-8",
                                  newline="\n")
        git(other, "add", "--", name)
    git(other, "commit", "-q", "-m", "Feature (squashed)")
    squashed_commit = git(other, "rev-parse", "HEAD")
    git(other, "push", "-q", "origin", w.default_branch)
    git(w.repo, "fetch", "-q", "origin")
    tip = git(w.repo, "rev-parse", f"origin/{w.default_branch}")
    return Landed(w, got, path, [second, first], squashed_commit, tip, w.default_branch)


def _pull_request(state: Landed, **changes) -> host.PullRequest:
    base = host.PullRequest(identifier="#7", merged=True, target_branch=state.default_branch,
                            merge_commit=state.merge_commit, last_source_commit=state.commits[0],
                            source_commits=frozenset(state.commits))
    return dataclasses.replace(base, **changes)


def _covered(state: Landed, pull_request: host.PullRequest) -> bool:
    return host.covered_by(state.path, pull_request, state.default_branch, state.default_tip, state.commits)


def test_the_whole_rule_met_proves_the_work_landed(squashed):
    assert _covered(squashed, _pull_request(squashed)) is True


def test_a_pull_request_that_is_not_merged_proves_nothing(squashed):
    assert _covered(squashed, _pull_request(squashed, merged=False)) is False


def test_a_pull_request_on_another_branch_proves_nothing(squashed):
    assert _covered(squashed, _pull_request(squashed, target_branch="some-other-branch")) is False


def test_a_merge_commit_this_repository_does_not_have_proves_nothing(squashed):
    assert _covered(squashed, _pull_request(squashed, merge_commit="0" * 40)) is False


def test_a_merge_commit_that_is_not_in_the_default_branch_proves_nothing(squashed):
    """The commit exists here - it is the slot's own newest commit - but it never reached the default
    branch. A pull request the host calls merged, whose landing commit is on no branch we can see, is a
    pull request into somewhere else."""
    assert _covered(squashed, _pull_request(squashed, merge_commit=squashed.commits[0])) is False


def test_a_pull_request_the_host_names_no_merge_commit_for_proves_nothing(squashed):
    assert _covered(squashed, _pull_request(squashed, merge_commit="")) is False


def test_a_pull_request_the_host_names_no_source_commit_for_proves_nothing(squashed):
    assert _covered(squashed, _pull_request(squashed, last_source_commit="")) is False


def test_one_commit_missing_from_the_pull_request_proves_nothing_for_any_of_them(squashed):
    """All of them together or none of them. A commit made after the merge is exactly this case, and the
    pull request must not carry it along."""
    only_one = _pull_request(squashed, source_commits=frozenset(squashed.commits[1:]))
    assert _covered(squashed, only_one) is False


def test_a_commit_outside_the_ancestry_of_the_last_source_commit_proves_nothing(squashed):
    """The host says the commit went in, but git says it is not behind the branch head the host merged.
    Two answers that do not fit together are not proof."""
    claimed = _pull_request(squashed, last_source_commit=squashed.commits[1],
                            source_commits=frozenset(squashed.commits))
    assert _covered(squashed, claimed) is False


def test_the_host_answer_frees_the_commits_and_the_answer_names_it(in_process, monkeypatch):
    """With the host's proof, work git cannot prove is returned - and the answer says which pull request
    freed which commit, so nobody has to take the word "free" on trust."""
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    path = Path(got["path"])
    first = commit_file(path, "feature/a.txt", "one\n", "feature part 1")
    second = commit_file(path, "feature/b.txt", "two\n", "feature part 2")
    other = w.second_clone()
    for name, text in (("feature/a.txt", "one\n"), ("feature/b.txt", "two\n")):
        (other / name).parent.mkdir(parents=True, exist_ok=True)
        (other / name).write_text(text, encoding="utf-8", newline="\n")
        git(other, "add", "--", name)
    git(other, "commit", "-q", "-m", "Feature (squashed)")
    git(other, "push", "-q", "origin", w.default_branch)

    asked: list[list[str]] = []

    def answer(worktree, tip, commits):
        asked.append(sorted(commits))
        return (host.Proof(tuple(commits), host.GITHUB, "pull request #7 on an-org/a-repo, merged as "
                           "abcdef123456 into " + tip.branch),), None

    monkeypatch.setattr(landed, "ask_the_host", answer)

    returned = pool.return_slot(got["path"], got["lease"], None)

    assert asked == [sorted([first, second])], asked
    assert returned["proved_by"] == "git and the host"
    assert sorted(entry["commit"] for entry in returned["host_proof"]) == sorted([first, second])
    assert all(entry["detail"].startswith("pull request #7") for entry in returned["host_proof"])
    assert w.slot(got["slot"])["state"] == "free"


def test_the_host_is_not_asked_when_git_proved_everything(in_process, monkeypatch):
    """The host can never turn free into held, because it is never asked about work git has proven. It is
    also the reason an ordinary return costs no call to gh or az at all."""
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    path = Path(got["path"])
    sha = commit_file(path, "landed.txt", "mine\n", "work that really landed")
    other = w.second_clone()
    git(other, "fetch", "-q", str(w.repo), sha)
    git(other, "merge", "-q", "--ff-only", sha)
    git(other, "push", "-q", "origin", w.default_branch)

    def never(worktree, tip, commits):
        raise AssertionError(f"the host was asked about {commits}, which git had already proven")

    monkeypatch.setattr(landed, "ask_the_host", never)

    returned = pool.return_slot(got["path"], got["lease"], None)

    assert returned["proved_by"] == "git"
    assert returned["host_proof"] == []


def test_a_host_that_cannot_be_asked_holds_the_work_and_says_checked_by_git_only(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    path = Path(got["path"])
    sha = commit_file(path, "mine.txt", "mine\n", "work nobody landed")

    def unavailable(worktree, tip, commits):
        return (), "checked by git only: gh is not on PATH"

    monkeypatch.setattr(landed, "ask_the_host", unavailable)

    with pytest.raises(Exception) as raised:
        pool.return_slot(got["path"], got["lease"], None)

    assert "held" in str(raised.value)
    reason = w.slot(got["slot"])["reason"]
    assert "checked by git only: gh is not on PATH" in reason
    assert sha[:12] in reason
    assert git(path, "rev-parse", "HEAD") == sha
