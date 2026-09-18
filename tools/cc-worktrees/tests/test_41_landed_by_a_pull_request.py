"""Work landed through a REAL pull request, on a real host, comes back free - and one commit more than the
pull request holds the slot.

Nothing here is simulated. Each test pushes a uniquely named branch to a throwaway testbed, opens a pull
request on the host, has the HOST merge it in one of the three ways that give the work new commit ids, and
then asks cc-worktrees to return the slot:

  * GitHub.com, squash merge - the whole branch becomes one commit nobody has seen before. No commit in the
    slot is the same patch as anything on the default branch, so git alone can prove nothing at all.
  * GitHub.com, rebase merge - every patch is replayed under a new id.
  * Azure DevOps, semi-linear merge (rebaseMerge) - the branch is replayed on to the default branch and a
    merge commit is added on top. The testbed's default branch is `develop`, which is also why nothing here
    may assume `main`.

Each branch carries a merge commit as well as ordinary commits, because that is what a branch looks like
after a developer brings the default branch into it - and because `git cherry` never lists a merge commit,
so git on its own cannot prove one landed however it was merged. That is the case the host layer exists for.

Then the same thing again with ONE extra commit made in the slot after the merge: the host's answer covers
the pull request's commits and not that one, so the whole slot is held. And once more with `gh` and `az`
taken off PATH, where the answer must be "checked by git only" and the work must be held.

The tests create the branches and the pull requests, and delete only the branches they created. The
commits the host lands on the testbed's default branch stay there: these are private throwaway testbeds and
their history is never rewritten.
"""

from __future__ import annotations

import json
import base64
import os
import shutil
import subprocess
import urllib.request
from pathlib import Path

import host
from conftest import commit_file, git, path_without

EXIT_HELD = 3

# A host command that has not answered by now is a broken testbed, not a slow one.
HOST_TOOL_TIMEOUT = 180


def _tool(exe: str, *args: str, cwd: Path) -> str:
    found = shutil.which(exe)
    assert found, f"{exe} is not on PATH, so this testbed cannot be driven"
    proc = subprocess.run([found, *args], cwd=str(cwd), capture_output=True, text=True,
                          timeout=HOST_TOOL_TIMEOUT, env={**os.environ, "GH_PAGER": "cat", "NO_COLOR": "1"})
    if proc.returncode != 0:
        raise AssertionError(f"{exe} {' '.join(args)} failed ({proc.returncode}): "
                             f"{(proc.stderr or proc.stdout).strip()[:500]}")
    return proc.stdout


def _branch_with_a_merge_commit(w, path: Path, branch: str, back: int = 0) -> list[str]:
    """Two ordinary commits and a merge commit, on `branch`, pushed. Returns them newest first.

    `back` starts the branch that many commits before the slot's HEAD, so the default branch has moved on
    since the branch began. A host that rebases only rewrites commit ids when it has something to rebase
    on to: with `back=0` an Azure DevOps semi-linear merge is a fast-forward that keeps every id, and the
    landing is then proven by plain git ancestry instead of by the host."""
    start = git(path, "rev-parse", f"HEAD~{back}" if back else "HEAD")
    git(path, "checkout", "-q", "--detach", start)
    git(path, "checkout", "-q", "-b", branch)
    first = commit_file(path, f"{branch}/a.txt", "one\n", "feature part 1")
    # A second line of work brought in with a real merge commit, the way the default branch is merged into
    # a branch before its pull request goes up.
    git(path, "checkout", "-q", "-b", f"{branch}-side", start)
    side = commit_file(path, f"{branch}/side.txt", "side\n", "work from beside the branch")
    git(path, "checkout", "-q", branch)
    git(path, "merge", "-q", "--no-ff", "-m", "merge the side work in", f"{branch}-side")
    merge = git(path, "rev-parse", "HEAD")
    git(path, "push", "-q", "-u", "origin", branch)
    return [merge, side, first]


def _github(w):
    return host.parse_remote(w.remote_url)


def _github_pull_request(w, branch: str) -> str:
    url = _tool("gh", "pr", "create", "--repo", _github(w).name, "--head", branch, "--base",
                w.default_branch, "--title", f"cc-worktrees test {branch}",
                "--body", "Opened by the cc-worktrees suite.", cwd=w.tmp).strip()
    return url.rsplit("/", 1)[-1]


def _github_merge(w, number: str, how: str) -> None:
    _tool("gh", "pr", "merge", number, "--repo", _github(w).name, how, "--delete-branch", cwd=w.tmp)


def _azure_pull_request(w, branch: str) -> str:
    remote = host.parse_remote(w.remote_url)
    out = _tool("az", "repos", "pr", "create", "--org", remote.org_url, "--project", remote.project,
                "--repository", remote.repo, "--source-branch", branch, "--target-branch", w.default_branch,
                "--title", f"cc-worktrees test {branch}", "--output", "json", "--only-show-errors",
                cwd=w.tmp)
    return str(json.loads(out)["pullRequestId"])


def _azure_complete_semi_linear(w, number: str, source_commit: str) -> None:
    """Complete an Azure DevOps pull request with the SEMI-LINEAR strategy.

    `az repos pr update` can only choose squash or a plain merge commit, so the one call that names
    `rebaseMerge` goes straight to the host's own interface, with the same credential the `az` commands
    here already use and that the shell exported. The tool under test never does this: it only ever READS,
    with `az`. The credential is read from the environment and never appears in this file."""
    remote = host.parse_remote(w.remote_url)
    token = os.environ.get("AZURE_DEVOPS_EXT_PAT")
    assert token, "AZURE_DEVOPS_EXT_PAT is not set, so the pull request cannot be completed"
    url = (f"{remote.org_url}/{remote.project}/_apis/git/repositories/{remote.repo}"
           f"/pullrequests/{number}?api-version=7.1")
    body = json.dumps({"status": "completed",
                       "lastMergeSourceCommit": {"commitId": source_commit},
                       "completionOptions": {"mergeStrategy": "rebaseMerge", "deleteSourceBranch": True}})
    auth = base64.b64encode(f":{token}".encode("ascii")).decode("ascii")
    request = urllib.request.Request(url, data=body.encode("utf-8"), method="PATCH", headers={
        "Content-Type": "application/json", "Authorization": f"Basic {auth}"})
    with urllib.request.urlopen(request, timeout=HOST_TOOL_TIMEOUT) as answer:
        assert answer.status == 200, answer.status
    _azure_wait_for_completion(w, number)


def _azure_wait_for_completion(w, number: str) -> None:
    """Azure DevOps queues the merge. Read the pull request back until it is completed."""
    remote = host.parse_remote(w.remote_url)
    for _ in range(60):
        out = _tool("az", "repos", "pr", "show", "--org", remote.org_url, "--id", number,
                    "--output", "json", "--only-show-errors", cwd=w.tmp)
        shown = json.loads(out)
        if shown.get("status") == "completed" and (shown.get("lastMergeCommit") or {}).get("commitId"):
            return
        if shown.get("status") == "abandoned" or shown.get("mergeStatus") in ("conflicts", "failure",
                                                                              "rejectedByPolicy"):
            raise AssertionError(f"the pull request did not complete: {shown.get('mergeStatus')}")
    raise AssertionError("the pull request was still not completed after the wait")


def _assert_freed_by_the_host(answer: dict, commits: list[str]) -> None:
    assert answer["proved_by"] == "git and the host", answer
    freed = {entry["commit"]: entry["detail"] for entry in answer["host_proof"]}
    for commit in commits:
        assert commit in freed, (commit, answer["host_proof"])
        assert "pull request" in freed[commit]


def _assert_held_with(w, got: dict, path: Path, res, *phrases: str) -> None:
    assert res.code == EXIT_HELD, res.out + res.err
    slot = w.slot(got["slot"])
    assert slot["state"] == "held"
    for phrase in phrases:
        assert phrase in slot["reason"], slot["reason"]


# ---------------------------------------------------------------------------------------------------
# GitHub.com
# ---------------------------------------------------------------------------------------------------


def test_github_squash_merged_work_comes_back_free(github_world):
    w = github_world
    got = w.get()
    path = Path(got["path"])
    branch = w.unique_branch()
    commits = _branch_with_a_merge_commit(w, path, branch)
    _github_merge(w, _github_pull_request(w, branch), "--squash")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == 0, res.out + res.err
    assert res.data["base"] == w.default_branch
    _assert_freed_by_the_host(res.data, commits)
    assert w.slot(got["slot"])["state"] == "free"


def test_github_rebase_merged_work_comes_back_free(github_world):
    w = github_world
    got = w.get()
    path = Path(got["path"])
    branch = w.unique_branch()
    commits = _branch_with_a_merge_commit(w, path, branch)
    _github_merge(w, _github_pull_request(w, branch), "--rebase")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == 0, res.out + res.err
    _assert_freed_by_the_host(res.data, [commits[0]])
    assert w.slot(got["slot"])["state"] == "free"


def test_github_one_commit_made_after_the_merge_holds_the_slot(github_world):
    w = github_world
    got = w.get()
    path = Path(got["path"])
    branch = w.unique_branch()
    _branch_with_a_merge_commit(w, path, branch)
    _github_merge(w, _github_pull_request(w, branch), "--squash")
    after = commit_file(path, f"{branch}/after.txt", "later\n", "one more, after the merge")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    _assert_held_with(w, got, path, res, "on no remote", after[:12],
                      "has no merged pull request that accounts for all of them")
    assert git(path, "rev-parse", "HEAD") == after
    assert (path / branch / "after.txt").exists()


def test_github_with_gh_off_the_path_the_work_is_held_and_checked_by_git_only(github_world):
    w = github_world
    got = w.get()
    path = Path(got["path"])
    branch = w.unique_branch()
    commits = _branch_with_a_merge_commit(w, path, branch)
    _github_merge(w, _github_pull_request(w, branch), "--squash")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json",
                env_extra={"PATH": path_without("gh")})

    _assert_held_with(w, got, path, res, "checked by git only", "gh is not on PATH")
    assert git(path, "rev-parse", "HEAD") == commits[0]


# ---------------------------------------------------------------------------------------------------
# Azure DevOps
# ---------------------------------------------------------------------------------------------------


def test_azure_semi_linear_merged_work_comes_back_free(azure_world):
    w = azure_world
    # The testbed's default branch is not `main`, and the tool read it from the remote.
    assert w.default_branch != "main"
    got = w.get()
    assert got["base"] == w.default_branch
    path = Path(got["path"])
    branch = w.unique_branch()
    commits = _branch_with_a_merge_commit(w, path, branch, back=1)
    _azure_complete_semi_linear(w, _azure_pull_request(w, branch), commits[0])

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == 0, res.out + res.err
    assert res.data["base"] == w.default_branch
    _assert_freed_by_the_host(res.data, [commits[0]])
    assert w.slot(got["slot"])["state"] == "free"


def test_azure_one_commit_made_after_the_merge_holds_the_slot(azure_world):
    w = azure_world
    got = w.get()
    path = Path(got["path"])
    branch = w.unique_branch()
    commits = _branch_with_a_merge_commit(w, path, branch, back=1)
    _azure_complete_semi_linear(w, _azure_pull_request(w, branch), commits[0])
    after = commit_file(path, f"{branch}/after.txt", "later\n", "one more, after the merge")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    _assert_held_with(w, got, path, res, "on no remote", after[:12],
                      "has no merged pull request that accounts for all of them")
    assert git(path, "rev-parse", "HEAD") == after


def test_azure_with_az_off_the_path_the_work_is_held_and_checked_by_git_only(azure_world):
    w = azure_world
    got = w.get()
    path = Path(got["path"])
    branch = w.unique_branch()
    commits = _branch_with_a_merge_commit(w, path, branch, back=1)
    _azure_complete_semi_linear(w, _azure_pull_request(w, branch), commits[0])

    res = w.run("return", got["path"], "--lease", got["lease"], "--json",
                env_extra={"PATH": path_without("az")})

    _assert_held_with(w, got, path, res, "checked by git only", "az is not on PATH")
    assert git(path, "rev-parse", "HEAD") == commits[0]


def test_github_with_gh_not_signed_in_the_work_is_held_and_checked_by_git_only(github_world, tmp_path):
    """A tool that is there but cannot answer is the same answer as no tool: checked by git only, held.
    `gh` is pointed at an empty configuration directory with no token, so it is not signed in; the testbed
    is private, so an unauthenticated `gh` cannot read it."""
    w = github_world
    got = w.get()
    path = Path(got["path"])
    branch = w.unique_branch()
    commits = _branch_with_a_merge_commit(w, path, branch)
    _github_merge(w, _github_pull_request(w, branch), "--squash")
    empty = tmp_path / "no-gh-config"
    empty.mkdir()

    res = w.run("return", got["path"], "--lease", got["lease"], "--json",
                env_extra={"GH_CONFIG_DIR": str(empty), "GH_TOKEN": "", "GITHUB_TOKEN": ""})

    _assert_held_with(w, got, path, res, "checked by git only", "gh pr list failed")
    assert git(path, "rev-parse", "HEAD") == commits[0]


def test_azure_with_az_not_signed_in_the_work_is_held_and_checked_by_git_only(azure_world, tmp_path):
    w = azure_world
    got = w.get()
    path = Path(got["path"])
    branch = w.unique_branch()
    commits = _branch_with_a_merge_commit(w, path, branch, back=1)
    _azure_complete_semi_linear(w, _azure_pull_request(w, branch), commits[0])
    empty = tmp_path / "no-az-config"
    empty.mkdir()

    res = w.run("return", got["path"], "--lease", got["lease"], "--json",
                env_extra={"AZURE_CONFIG_DIR": str(empty), "AZURE_DEVOPS_EXT_PAT": ""})

    _assert_held_with(w, got, path, res, "checked by git only", "az repos pr list failed")
    assert git(path, "rev-parse", "HEAD") == commits[0]


def test_github_the_plain_output_names_the_pull_request_that_freed_each_commit(github_world):
    """The plain AXI output says what proved the work, not only `--json`. A person reading a returned
    slot must be able to see that a HOST freed it, and which pull request did."""
    w = github_world
    got = w.get()
    path = Path(got["path"])
    branch = w.unique_branch()
    commits = _branch_with_a_merge_commit(w, path, branch)
    _github_merge(w, _github_pull_request(w, branch), "--squash")

    res = w.run("return", got["path"], "--lease", got["lease"])

    assert res.code == 0, res.out + res.err
    assert "proved_by: git and the host" in res.out, res.out
    assert f"host_proof[{len(commits)}]{{commit,host,detail}}:" in res.out, res.out
    for commit in commits:
        row = [line for line in res.out.splitlines() if line.strip().startswith(commit)]
        assert len(row) == 1, (commit, res.out)
        assert ",github," in row[0], row[0]
        assert "pull request #" in row[0] and "merged as" in row[0], row[0]
