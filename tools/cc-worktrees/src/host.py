"""The host layer: which host a repository's remote is on, and what that host can prove about landed work.

Git alone cannot recognise work that landed by a squash merge: the squashed commit is not the same patch as
any one of the branch's commits, so every one of them stays unproven and the slot is held. The host still
knows: it keeps the pull request, the commits that went into it, and the commit it landed as. This module
asks it, with `gh` for GitHub.com and `az` for Azure DevOps.

The host's answer may only ever turn HELD into FREE, and only under the whole of this rule:

  (a) the pull request is merged (GitHub) or completed (Azure DevOps);
  (b) the commit it landed as is in this repository and is an ancestor of the default branch tip that was
      read under the machine-wide lock - not the tip of a fetch made earlier, and not a tip on some other
      branch;
  (c) every commit the slot cannot otherwise prove is one of that ONE pull request's source commits, and
      none of them is outside the ancestry of its last source commit, so nothing committed after the merge
      can ride along.

Anything else is no proof: a missing `gh` or `az`, one that is not signed in, one that times out, an answer
that cannot be read, a pull request that is still open or was abandoned. Each of those is reported as
"checked by git only" and the commits stay held. The host is never asked to say "no" - only to say "yes".

The remote URL shapes are the ones the product already recognises in
src/CcDirector.Core/Utilities/GitHubUrls.cs; a remote on neither host is the "other" host, checked by git
only.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import unquote

import gitrun
from gitrun import GitError, ToolMissing

GITHUB = "github"
AZURE_DEVOPS = "azure-devops"
OTHER = "other"

# The tool a host is asked with, for the messages.
HOST_TOOL = {GITHUB: "gh", AZURE_DEVOPS: "az"}

# At most this many branch names are put to the host for one answer. A slot with more candidate branches
# than this is checked by git only: a long list is a sign the commits are not one branch's work, and an
# unbounded list would turn one return into an unbounded number of network calls.
MAX_BRANCHES = 10

# At most this many pull requests are read back for one branch.
MAX_PULL_REQUESTS = 20

_SHA = re.compile(r"[0-9a-f]{40}|[0-9a-f]{64}")

_GITHUB = re.compile(r"github\.com[:/](?P<owner>[^/\s]+)/(?P<repo>[^/\s]+?)(?:\.git)?/?$", re.IGNORECASE)
_AZURE_HTTPS = re.compile(
    r"dev\.azure\.com/(?P<org>[^/\s]+)/(?P<project>[^/\s]+)/_git/(?P<repo>[^/\s]+?)(?:\.git)?/?$",
    re.IGNORECASE)
_AZURE_SSH = re.compile(
    r"ssh\.dev\.azure\.com:(?:v3/)?(?P<org>[^/\s]+)/(?P<project>[^/\s]+)/(?P<repo>[^/\s]+?)(?:\.git)?/?$",
    re.IGNORECASE)
_AZURE_LEGACY = re.compile(
    r"(?P<org>[^/@\s.]+)\.visualstudio\.com[:/](?:.*/)?(?P<project>[^/\s]+)/_git/(?P<repo>[^/\s]+?)(?:\.git)?/?$",
    re.IGNORECASE)


class Unavailable(Exception):
    """The host could not be asked. The message says why, in plain words."""


@dataclass(frozen=True)
class Remote:
    """A repository's origin remote, read as a host address."""

    host: str
    url: str
    owner: str = ""         # GitHub: the account or organisation; Azure DevOps: the organisation
    project: str = ""       # Azure DevOps: the project the repository is in
    repo: str = ""          # the repository's own name, on either host
    org_url: str = ""       # Azure DevOps: the organisation URL `az` is given

    @property
    def name(self) -> str:
        if self.host == GITHUB:
            return f"{self.owner}/{self.repo}"
        if self.host == AZURE_DEVOPS:
            return f"{self.owner}/{self.project}/{self.repo}"
        return self.url


@dataclass(frozen=True)
class PullRequest:
    """One pull request, as the host describes it. Every field is the host's own answer."""

    identifier: str                   # what a person would type to find it again
    merged: bool
    target_branch: str                # the branch it landed on, without refs/heads/
    merge_commit: str                 # the commit it landed as, "" when the host names none
    last_source_commit: str           # the head of its source branch when it landed
    source_commits: frozenset[str]    # every commit the host says went into it


@dataclass(frozen=True)
class Proof:
    """A host answer that freed commits, and the plain sentence naming it."""

    commits: tuple[str, ...]
    host: str
    detail: str


def parse_remote(url: str) -> Remote:
    """The host address in a remote URL. A URL on neither host is the "other" host, which proves nothing."""
    text = (url or "").strip()
    if not text:
        return Remote(OTHER, text)
    match = _GITHUB.search(text)
    if match:
        return Remote(GITHUB, text, owner=unquote(match.group("owner")), repo=unquote(match.group("repo")))
    for pattern in (_AZURE_HTTPS, _AZURE_SSH):
        match = pattern.search(text)
        if match:
            org = unquote(match.group("org"))
            return Remote(AZURE_DEVOPS, text, owner=org, project=unquote(match.group("project")),
                          repo=unquote(match.group("repo")), org_url=f"https://dev.azure.com/{org}")
    match = _AZURE_LEGACY.search(text)
    if match:
        org = unquote(match.group("org"))
        return Remote(AZURE_DEVOPS, text, owner=org, project=unquote(match.group("project")),
                      repo=unquote(match.group("repo")), org_url=f"https://{org}.visualstudio.com")
    return Remote(OTHER, text)


def remote_of(cwd: Path, remote: str = "origin") -> Remote:
    """The host address of a repository's remote. A remote that cannot be read is no host at all."""
    try:
        url = gitrun.out(cwd, "remote", "get-url", remote)
    except GitError as ex:
        raise Unavailable(f"the remote '{remote}' has no URL that could be read: {ex.short()}") from ex
    return parse_remote(url)


# ---------------------------------------------------------------------------------------------------
# Asking the host
# ---------------------------------------------------------------------------------------------------


def _json(text: str, what: str):
    try:
        return json.loads(text)
    except ValueError as ex:
        raise Unavailable(f"{what} gave an answer that is not JSON: {ex}") from ex


def _sha(value) -> str:
    """A commit id from the host's answer, or "" when the field is absent or is not a commit id."""
    return value if isinstance(value, str) and _SHA.fullmatch(value) else ""


def _short_ref(name: str) -> str:
    return name[len("refs/heads/"):] if name.startswith("refs/heads/") else name


def _failed(what: str, result) -> Unavailable:
    return Unavailable(f"{what} failed: {gitrun.first_line(result.stderr) or f'exit {result.returncode}'}")


def _github_pull_requests(cwd: Path, remote: Remote, branch: str, default_branch: str,
                          timeout: float) -> list[PullRequest]:
    result = gitrun.run_tool("gh", cwd, [
        "pr", "list", "--repo", remote.name, "--state", "merged", "--head", branch,
        "--base", default_branch, "--limit", str(MAX_PULL_REQUESTS), "--json",
        "number,state,baseRefName,headRefName,headRefOid,mergeCommit,commits",
    ], timeout, env_extra={"GH_PAGER": "cat", "NO_COLOR": "1", "GH_PROMPT_DISABLED": "1"})
    if result.returncode != 0:
        raise _failed("gh pr list", result)
    items = _json(result.stdout or "[]", "gh pr list")
    if not isinstance(items, list):
        raise Unavailable("gh pr list did not give a list of pull requests")
    found = []
    for item in items:
        if not isinstance(item, dict):
            raise Unavailable("gh pr list gave a pull request that could not be read")
        commits = item.get("commits")
        if not isinstance(commits, list):
            raise Unavailable(f"gh pr list did not list the commits of pull request {item.get('number')}")
        merge = item.get("mergeCommit")
        found.append(PullRequest(
            identifier=f"#{item.get('number')}",
            merged=item.get("state") == "MERGED",
            target_branch=str(item.get("baseRefName") or ""),
            merge_commit=_sha((merge or {}).get("oid") if isinstance(merge, dict) else None),
            last_source_commit=_sha(item.get("headRefOid")),
            source_commits=frozenset(
                _sha(c.get("oid")) for c in commits if isinstance(c, dict) and _sha(c.get("oid"))),
        ))
    return found


def _azure_source_commits(cwd: Path, remote: Remote, pull_request_id: str, timeout: float) -> frozenset[str]:
    """The commits Azure DevOps says went into one pull request. `az repos pr list` leaves that field empty,
    so the pull request's own commits are asked for by name."""
    result = gitrun.run_tool("az", cwd, [
        "devops", "invoke", "--org", remote.org_url, "--area", "git", "--resource", "pullRequestCommits",
        "--route-parameters", f"project={remote.project}", f"repositoryId={remote.repo}",
        f"pullRequestId={pull_request_id}", "--api-version", "7.1", "--output", "json", "--only-show-errors",
    ], timeout)
    if result.returncode != 0:
        raise _failed("az devops invoke (the pull request's commits)", result)
    payload = _json(result.stdout or "{}", "az devops invoke")
    values = payload.get("value") if isinstance(payload, dict) else None
    if not isinstance(values, list):
        raise Unavailable(f"az did not list the commits of pull request {pull_request_id}")
    return frozenset(_sha(v.get("commitId")) for v in values if isinstance(v, dict) and _sha(v.get("commitId")))


def _azure_pull_requests(cwd: Path, remote: Remote, branch: str, default_branch: str,
                         timeout: float) -> list[PullRequest]:
    result = gitrun.run_tool("az", cwd, [
        "repos", "pr", "list", "--org", remote.org_url, "--project", remote.project,
        "--repository", remote.repo, "--status", "completed", "--source-branch", branch,
        "--target-branch", default_branch, "--top", str(MAX_PULL_REQUESTS), "--output", "json",
        "--only-show-errors",
    ], timeout)
    if result.returncode != 0:
        raise _failed("az repos pr list", result)
    items = _json(result.stdout or "[]", "az repos pr list")
    if not isinstance(items, list):
        raise Unavailable("az repos pr list did not give a list of pull requests")
    found = []
    for item in items:
        if not isinstance(item, dict):
            raise Unavailable("az repos pr list gave a pull request that could not be read")
        identifier = item.get("pullRequestId")
        if identifier is None:
            raise Unavailable("az repos pr list gave a pull request with no id")
        merge = item.get("lastMergeCommit")
        source = item.get("lastMergeSourceCommit")
        found.append(PullRequest(
            identifier=f"!{identifier}",
            merged=item.get("status") == "completed",
            target_branch=_short_ref(str(item.get("targetRefName") or "")),
            merge_commit=_sha((merge or {}).get("commitId") if isinstance(merge, dict) else None),
            last_source_commit=_sha((source or {}).get("commitId") if isinstance(source, dict) else None),
            source_commits=_azure_source_commits(cwd, remote, str(identifier), timeout),
        ))
    return found


def pull_requests(cwd: Path, remote: Remote, branch: str, default_branch: str,
                  timeout: float) -> list[PullRequest]:
    """Every merged pull request the host has for one source branch into the default branch."""
    try:
        if remote.host == GITHUB:
            return _github_pull_requests(cwd, remote, branch, default_branch, timeout)
        if remote.host == AZURE_DEVOPS:
            return _azure_pull_requests(cwd, remote, branch, default_branch, timeout)
    except ToolMissing as ex:
        raise Unavailable(str(ex)) from ex
    raise Unavailable("this repository's remote is on no host cc-worktrees can ask")


# ---------------------------------------------------------------------------------------------------
# Deciding what the answer proves
# ---------------------------------------------------------------------------------------------------


def _is_ancestor(cwd: Path, commit: str, of: str) -> bool:
    return gitrun.run(cwd, "merge-base", "--is-ancestor", commit, of, check=False).returncode == 0


def _present(cwd: Path, commit: str) -> bool:
    return gitrun.run(cwd, "cat-file", "-e", f"{commit}^{{commit}}", check=False).returncode == 0


def branch_names(cwd: Path, commits: list[str]) -> list[str]:
    """The names a pull request's source branch could have had for these commits: every local branch that
    contains one of them, plus the name the remote knows it by when an upstream says so.

    A commit on no local branch names no branch, so nothing is asked and the commit stays held."""
    wanted: set[str] = set()
    for i in range(0, len(commits), 50):
        contains = [arg for c in commits[i:i + 50] for arg in ("--contains", c)]
        listing = gitrun.out(cwd, "for-each-ref", "--format=%(refname:short)%09%(upstream)",
                             *contains, "refs/heads/")
        for line in listing.splitlines():
            parts = line.split("\t")
            local = parts[0].strip()
            upstream = parts[1].strip() if len(parts) > 1 else ""
            if local:
                wanted.add(local)
            if upstream.startswith("refs/remotes/"):
                rest = upstream[len("refs/remotes/"):]
                if "/" in rest:
                    wanted.add(rest.split("/", 1)[1])
    return sorted(wanted)


def covered_by(cwd: Path, pull_request: PullRequest, default_branch: str, default_tip: str,
               commits: list[str]) -> bool:
    """Whether ONE pull request proves EVERY one of `commits` landed - the whole rule, and nothing less.

    All of them together or none of them: work split across two pull requests is held, because a rule that
    took each pull request's word for its own part would have nothing left that says the slot holds no
    commit made after the last of them landed."""
    if not pull_request.merged:
        return False
    if pull_request.target_branch != default_branch:
        return False
    if not pull_request.merge_commit or not pull_request.last_source_commit:
        return False
    if not _present(cwd, pull_request.merge_commit):
        return False
    if not _is_ancestor(cwd, pull_request.merge_commit, default_tip):
        return False
    for commit in commits:
        if commit not in pull_request.source_commits:
            return False
        if not _is_ancestor(cwd, commit, pull_request.last_source_commit):
            return False
    return True


def prove(cwd: Path, default_branch: str, default_tip: str, commits: list[str],
          timeout: float) -> tuple[tuple[Proof, ...], str | None]:
    """Ask the host whether ONE merged pull request accounts for every commit git could not prove.

    Returns either the proof that frees them and no note, or no proof and the plain note to add to the hold
    reason. The host is asked only about commits git already could not prove, so a repository on no host, a
    missing tool and an unreadable answer all cost nothing but the note."""
    if not commits:
        return (), None
    try:
        remote = remote_of(cwd)
        if remote.host == OTHER:
            return (), ("checked by git only: this repository's remote is on no host cc-worktrees can ask "
                        "(it knows github.com and Azure DevOps)")
        try:
            names = branch_names(cwd, commits)
        except GitError as ex:
            raise Unavailable(f"the branches holding these commits could not be listed: {ex.short()}") from ex
        if not names:
            return (), ("checked by git only: these commits are on no local branch, so there is no source "
                        f"branch to ask {HOST_TOOL[remote.host]} about")
        if len(names) > MAX_BRANCHES:
            return (), (f"checked by git only: these commits are on {len(names)} local branches, more than "
                        f"the {MAX_BRANCHES} cc-worktrees will put to the host")
        for name in names:
            for pull_request in pull_requests(cwd, remote, name, default_branch, timeout):
                if covered_by(cwd, pull_request, default_branch, default_tip, commits):
                    return (Proof(tuple(commits), remote.host,
                                  f"pull request {pull_request.identifier} on {remote.name}, merged as "
                                  f"{pull_request.merge_commit[:12]} into {default_branch}"),), None
    except Unavailable as ex:
        return (), f"checked by git only: {ex}"
    return (), "the host was asked and has no merged pull request that accounts for all of them"
