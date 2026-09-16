"""Tests for `cc-devthrottle repo list` and `worktree list` in the AXI shape (issue #2922,
docs/axi-standard.md). They pin the same things the `session list` tests pin:

- Recoverability: every row's full name (and path) and state read back EXACTLY from the default output
  with the `parse_list` the helper ships. The same check against the old Rich tables fails, which is
  what proves the check can fail.
- `--json` is byte-for-byte the Gateway's answer when no filter is given, and a filter narrows the same
  bare array without changing its shape.
- An empty answer says `count: 0`, and `count: 0 of N total` when a filter matched nothing.
- An unknown state, an unknown field or an unknown flag exits 2 and lists the valid values.
- A Gateway answer this tool cannot read fails with exit 1 instead of being guessed around.
"""

import io
import json
import sys
from pathlib import Path

import pytest
import typer
from rich import box
from rich.console import Console
from rich.table import Table
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared import axi_output  # noqa: E402
from cc_shared.axi_output import ListParseError, parse_list  # noqa: E402
from src import repo_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

DIRECTOR_A = "6d4523e2-ed03-4ae6-ac1c-71d00a37bad1"
DIRECTOR_B = "61640aab-061d-4d2d-a91e-2160d16cec00"
DIRECTOR_MAC = "4fbad29d-6baa-4cdd-bbee-cef6b0b50978"


def _repo(name, path, *, clean, machine="SOREN_NORTH", director=DIRECTOR_A, **extra):
    row = {
        "directorId": director,
        "machineName": machine,
        "path": path,
        "name": name,
        "remoteUrl": f"https://github.com/thefrederiksen/{name}.git",
        "provider": "GitHub",
        "org": "thefrederiksen",
        "branch": "main",
        "isClean": clean,
        "uncommittedCount": 0 if clean else 3,
        "dirtySinceUtc": None,
        "aheadCount": 0,
        "behindCount": 0,
        "behindMainCount": -1,
        "worktreeCount": 2,
        "worktreesSafeToReap": 1,
        "worktreesInUse": 0,
        "worktreesNeedAttention": 1,
        "worktreeBytes": 1433083843,
        "provisional": False,
        "worktrees": [],
    }
    row.update(extra)
    return row


# Names and paths that break a naive list or an 80 column table: a comma, quotes, non-ASCII, spaces, a
# path far longer than any column, Windows and macOS paths, and the same repository reported twice by
# two Directors on one machine.
REPOS = [
    _repo("devthrottle", r"D:\ReposFred\devthrottle", clean=False),
    _repo("devthrottle", r"D:\ReposFred\devthrottle", clean=False, director=DIRECTOR_B),
    _repo("devthrottle", "/Users/soren/ReposFred/devthrottle", clean=True,
          machine="devthrottle-mac-mini", director=DIRECTOR_MAC),
    _repo("notes, drafts", r"C:\Users\soren\Documents\notes, drafts", clean=True, machine="SORENLAPTOP"),
    _repo('the "quoted" repo', r"D:\ReposOther\the \"quoted\" repo", clean=False),
    _repo("S\u00f8rens-caf\u00e9", "/Users/soren/Repos/S\u00f8rens-caf\u00e9 \U0001f680",
          clean=True, machine="devthrottle-mac-mini", director=DIRECTOR_MAC),
    _repo("a-repository-with-a-name-far-longer-than-any-eighty-column-table-could-show",
          "D:/ReposProcess/deeply/nested/folders/a-repository-with-a-name-far-longer-than-any-eighty-column-table-could-show",
          clean=True),
]
EXPECTED_REPO_STATES = ["dirty", "dirty", "clean", "clean", "dirty", "clean", "clean"]


def _worktree(path, repo_name, repo_path, state, *, machine="SOREN_NORTH", director=DIRECTOR_A,
              branch="feature", sessions=(), size=804076644, **extra):
    row = {
        "repoName": repo_name,
        "repoPath": repo_path,
        "machineName": machine,
        "directorId": director,
        "path": path,
        "branch": branch,
        "state": state,
        "reason": "Origin branch deleted after merge.",
        "sessionLabels": list(sessions),
        "sizeBytes": size,
        "lastActivityUtc": "2026-09-06T15:47:11.2616833Z",
        "dataAgeSeconds": 4.6196622,
        "provisional": False,
    }
    row.update(extra)
    return row


WORKTREES = [
    _worktree("D:/ReposFred/devthrottle-standby-review", "devthrottle", r"D:\ReposFred\devthrottle",
              "needs-attention", branch=None,
              sessions=["Standby slot - Reviewer - Codex round 2, pull request 2946 (#106)", "second; session"]),
    _worktree("D:/ReposFred/devthrottle-standby-review", "devthrottle", r"D:\ReposFred\devthrottle",
              "needs-attention", branch=None, director=DIRECTOR_B),
    _worktree("/Users/soren/ReposFred/devthrottle-axi-list-repo-worktree", "devthrottle",
              "/Users/soren/ReposFred/devthrottle", "in-use", machine="devthrottle-mac-mini",
              director=DIRECTOR_MAC, sessions=["AXI Tools - Worker - repo and worktree list"]),
    _worktree("C:/ReposFred/cc-director-linux", "cc-director", r"C:\ReposFred\cc-director", "safe-to-reap",
              machine="SORENLAPTOP"),
    _worktree("D:/ReposFred/notes, \"old\" drafts", "notes, drafts", r"D:\ReposFred\notes, drafts",
              "safe-to-reap", size=None),
    _worktree("/Users/soren/Repos/S\u00f8rens-caf\u00e9-wt", "S\u00f8rens-caf\u00e9",
              "/Users/soren/Repos/S\u00f8rens-caf\u00e9", "verifying", machine="devthrottle-mac-mini",
              director=DIRECTOR_MAC, provisional=True),
]
EXPECTED_WORKTREE_STATES = ["needs-attention", "needs-attention", "in-use", "safe-to-reap", "safe-to-reap", "verifying"]


@pytest.fixture
def serve(monkeypatch):
    """Serve chosen /repositories and /worktrees answers, with no real HTTP."""

    def serve_(repositories=None, worktrees=None):
        answers = {"repositories": repositories, "worktrees": worktrees}

        def get_json(path):
            assert path in answers, path
            return answers[path]

        monkeypatch.setattr(repo_ops.gateway, "get_json", get_json)

    return serve_


def _check_repos_recoverable(output, repos):
    _, records = parse_list(output, "repositories")
    got = [(r["name"], r["path"], r["machine"], r["state"]) for r in records]
    want = [(r["name"], r["path"], r["machineName"], repo_ops.repo_state(r)) for r in repos]
    assert got == want


def _check_worktrees_recoverable(output, worktrees):
    _, records = parse_list(output, "worktrees")
    got = [(r["path"], r["repo"], r["machine"], r["state"]) for r in records]
    want = [(w["path"], w["repoName"], w["machineName"], w["state"]) for w in worktrees]
    assert got == want


def _old_repo_table(rows):
    """`repo list` as it was on main before #2922, frozen here as the negative control. Rendered the
    way an agent read it: stdout a pipe, so Rich lays it out at 80 columns."""
    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    for col in ("REPOSITORY", "MACHINE", "PROVIDER", "BRANCH", "STATE", "WORKTREES", "SIZE(WT)"):
        table.add_column(col)
    for r in rows:
        state = "clean" if r["isClean"] else f"{r['uncommittedCount']} uncommitted"
        table.add_row(r["name"], r["machineName"], r["provider"], r["branch"], state,
                      f"{r['worktreeCount']} ({r['worktreesSafeToReap']} safe)", "1.3G")
    console = Console(width=80, record=True, file=io.StringIO())
    console.print(table)
    console.print(f"{len(rows)} repositories - 7 worktrees safe to reap")
    return console.export_text()


def _old_worktree_table(rows):
    """`worktree list` as it was on main before #2922, frozen here as the negative control."""
    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    for col in ("REPO", "BRANCH", "MACHINE", "STATE", "SESSION", "SIZE", "REASON"):
        table.add_column(col)
    for w in rows:
        table.add_row(w["repoName"], w["branch"] or "(detached)", w["machineName"], w["state"],
                      ", ".join(w["sessionLabels"]) or "-", "767M", w["reason"])
    console = Console(width=80, record=True, file=io.StringIO())
    console.print(table)
    return console.export_text()


# ---------------------------------------------------------------------------------------------------
# repo list: recoverability
# ---------------------------------------------------------------------------------------------------


def test_list_repositories_DefaultOutput_EveryNamePathMachineAndStateReadBackExactly(serve, capsys):
    serve(repositories=REPOS)

    repo_ops.list_repositories(json_output=False)

    out = capsys.readouterr().out
    assert out.isascii()
    _check_repos_recoverable(out, REPOS)
    fields, records = parse_list(out, "repositories")
    assert fields == ["name", "path", "machine", "state"]
    # Pinned independently of repo_state, so the check above is not the fold agreeing with itself.
    assert [r["state"] for r in records] == EXPECTED_REPO_STATES


def test_repo_recoverability_check_OldRichTable_Fails():
    old = _old_repo_table(REPOS)

    with pytest.raises((ListParseError, AssertionError)):
        _check_repos_recoverable(old, REPOS)
    # And not merely for want of a header: the table loses the facts. It has no path at all, and the
    # long name is cut to fit 80 columns.
    assert REPOS[0]["path"] not in old
    assert REPOS[6]["name"] not in old


def test_repo_recoverability_check_ListThatShortensPaths_Fails():
    records = [
        {"name": r["name"], "path": r["path"][:12] + "...", "machine": r["machineName"],
         "state": repo_ops.repo_state(r)}
        for r in REPOS
    ]
    shortened = axi_output.render_list("repositories", ["name", "path", "machine", "state"], records)

    with pytest.raises(AssertionError):
        _check_repos_recoverable(shortened, REPOS)


def test_list_repositories_Fields_ShowsExactlyTheRequestedFieldsInOrder(serve, capsys):
    serve(repositories=REPOS)

    repo_ops.list_repositories(json_output=False, fields="director,name,uncommitted,provisional,remote")

    fields, records = parse_list(capsys.readouterr().out, "repositories")
    assert fields == ["director", "name", "uncommitted", "provisional", "remote"]
    assert records[4] == {
        "director": DIRECTOR_A,
        "name": 'the "quoted" repo',
        "uncommitted": "3",
        "provisional": "false",
        "remote": 'https://github.com/thefrederiksen/the "quoted" repo.git',
    }


# ---------------------------------------------------------------------------------------------------
# repo list: counts, empty states, help, repeats
# ---------------------------------------------------------------------------------------------------


def test_list_repositories_Unfiltered_CountsByStateSummaryAndHelp(serve, capsys):
    serve(repositories=REPOS)

    repo_ops.list_repositories(json_output=False)

    lines = capsys.readouterr().out.splitlines()
    assert lines[0] == "count: 7 (dirty 3, clean 4)"
    assert "worktrees in these repositories: 14, safe to reap: 7" in lines
    help_index = next(i for i, line in enumerate(lines) if line.startswith("help["))
    assert "  cc-devthrottle repo list --state dirty" in lines[help_index:]
    assert "  cc-devthrottle worktree list --repo <name>" in lines[help_index:]


def test_list_repositories_SamePathFromTwoDirectors_BothListedAndSaid(serve, capsys):
    serve(repositories=REPOS)

    repo_ops.list_repositories(json_output=False)

    out = capsys.readouterr().out
    _, records = parse_list(out, "repositories")
    assert [r["path"] for r in records].count(r"D:\ReposFred\devthrottle") == 2
    assert ("repeated: 1 of these rows name a repository path already listed on the same machine, "
            "reported by another Director there. Add director to --fields to tell them apart.") in out.splitlines()


def test_list_repositories_DirectorFieldShown_RepeatLineDropsTheHint(serve, capsys):
    serve(repositories=REPOS)

    repo_ops.list_repositories(json_output=False, fields="name,path,director")

    out = capsys.readouterr().out
    assert ("repeated: 1 of these rows name a repository path already listed on the same machine, "
            "reported by another Director there.") in out.splitlines()


def test_list_repositories_Filtered_CountSaysOfTotal(serve, capsys):
    serve(repositories=REPOS)

    repo_ops.list_repositories(json_output=False, state="clean", machine="DEVTHROTTLE-mac-mini")

    out = capsys.readouterr().out
    assert out.splitlines()[0] == "count: 2 of 7 total (clean 2)"
    _, records = parse_list(out, "repositories")
    assert [r["path"] for r in records] == [REPOS[2]["path"], REPOS[5]["path"]]


def test_list_repositories_EmptyAnswer_PrintsCountZero(serve, capsys):
    serve(repositories=[])

    repo_ops.list_repositories(json_output=False)

    out = capsys.readouterr().out
    assert out.splitlines()[0] == "count: 0"
    assert "repositories[0]{name,path,machine,state}:" in out.splitlines()
    assert "No repositories were returned." in out
    assert "Only Directors with a recent report are included" in out


def test_list_repositories_FilterMatchesNothing_PrintsCountZeroOfTotal(serve, capsys):
    serve(repositories=REPOS)

    repo_ops.list_repositories(json_output=False, repo="no-such-repo")

    out = capsys.readouterr().out
    assert out.splitlines()[0] == "count: 0 of 7 total"
    assert "No repository matches the filter." in out
    assert "  cc-devthrottle repo list" in out.splitlines()


# ---------------------------------------------------------------------------------------------------
# repo list: filters, including the existing --dirty
# ---------------------------------------------------------------------------------------------------


@pytest.mark.parametrize("repo, expected", [
    ("devthrottle", [0, 1, 2]),
    ("DEVTHROTTLE", [0, 1, 2]),
    (r"D:\ReposFred\devthrottle", [0, 1]),
    ("d:/reposfred/devthrottle/", [0, 1]),
    ("/Users/soren/ReposFred/devthrottle", [2]),
    ("notes, drafts", [3]),
])
def test_list_repositories_RepoFilter_MatchesNameOrFullPath(serve, capsys, repo, expected):
    serve(repositories=REPOS)

    repo_ops.list_repositories(json_output=False, repo=repo)

    _, records = parse_list(capsys.readouterr().out, "repositories")
    assert [(r["name"], r["path"]) for r in records] == [(REPOS[i]["name"], REPOS[i]["path"]) for i in expected]


def test_repo_list_Cli_Dirty_StillOnlyDirtyRows(serve):
    serve(repositories=REPOS)

    result = runner.invoke(app, ["repo", "list", "--dirty"])

    assert result.exit_code == 0
    assert result.stdout.splitlines()[0] == "count: 3 of 7 total (dirty 3)"
    _, records = parse_list(result.stdout, "repositories")
    assert [r["state"] for r in records] == ["dirty"] * 3


def test_repo_list_Cli_DirtyJson_StillTheDirtyRowsInTheSameShape(serve):
    serve(repositories=REPOS)

    result = runner.invoke(app, ["repo", "list", "--dirty", "--json"])

    assert result.exit_code == 0
    assert result.stdout == json.dumps([REPOS[0], REPOS[1], REPOS[4]], indent=2) + "\n"


def test_repo_list_Cli_DirtyWithState_ExitsTwo(serve):
    serve(repositories=REPOS)

    result = runner.invoke(app, ["repo", "list", "--dirty", "--state", "clean"])

    assert result.exit_code == 2
    assert "--dirty is the same as --state dirty" in result.stderr
    assert result.stdout == ""


# ---------------------------------------------------------------------------------------------------
# repo list: --json keeps its shape
# ---------------------------------------------------------------------------------------------------


def test_list_repositories_JsonUnfiltered_ByteForByteTheAnswer(serve, capsys):
    # Includes a row this tool could not name a state for: unfiltered --json never needs it.
    answer = REPOS + [_repo("future", "/x/future", clean="unknown")]
    serve(repositories=answer)

    repo_ops.list_repositories(json_output=True)

    captured = capsys.readouterr()
    assert captured.out == json.dumps(answer, indent=2) + "\n"
    assert captured.err == ""


def test_list_repositories_JsonFiltered_SameBareArrayNarrowed(serve, capsys):
    serve(repositories=REPOS)

    repo_ops.list_repositories(json_output=True, state="dirty", repo="devthrottle", machine="soren_north")

    assert capsys.readouterr().out == json.dumps([REPOS[0], REPOS[1]], indent=2) + "\n"


def test_list_repositories_JsonMachineFilterAlone_ExcludesOtherMachines(serve, capsys):
    serve(repositories=REPOS)

    repo_ops.list_repositories(json_output=True, machine="sorenlaptop")

    assert capsys.readouterr().out == json.dumps([REPOS[3]], indent=2) + "\n"


def test_list_repositories_JsonFilterMatchesNothing_EmptyArrayAndCautionOnStderr(serve, capsys):
    serve(repositories=REPOS)

    repo_ops.list_repositories(json_output=True, repo="no-such-repo")

    captured = capsys.readouterr()
    assert json.loads(captured.out) == []
    assert "Only Directors with a recent report are included" in captured.err


# ---------------------------------------------------------------------------------------------------
# repo list: usage errors exit 2, unreadable answers exit 1
# ---------------------------------------------------------------------------------------------------


def test_repo_list_Cli_UnknownState_ExitsTwoListingStates(serve):
    serve(repositories=REPOS)

    result = runner.invoke(app, ["repo", "list", "--state", "modified"])

    assert result.exit_code == 2
    assert "'modified'" in result.stderr
    assert "Valid states: dirty, clean" in result.stderr
    assert result.stdout == ""


def test_repo_list_Cli_UnknownField_ExitsTwoListingFields(serve):
    serve(repositories=REPOS)

    result = runner.invoke(app, ["repo", "list", "--fields", "name,colour"])

    assert result.exit_code == 2
    assert "colour" in result.stderr
    assert ", ".join(repo_ops.REPO_LIST_FIELDS) in result.stderr


@pytest.mark.parametrize("args, message", [
    (["--json", "--fields", "name"], "--fields does not apply to --json"),
    (["--repo", " "], "--repo needs a value"),
    (["--machine", ""], "--machine needs a value"),
])
def test_repo_list_Cli_BadFlagValues_ExitTwo(serve, args, message):
    serve(repositories=REPOS)

    result = runner.invoke(app, ["repo", "list", *args])

    assert result.exit_code == 2
    assert message in result.stderr


def test_repo_list_Cli_UnknownFlag_ExitsTwo(serve):
    serve(repositories=REPOS)

    result = runner.invoke(app, ["repo", "list", "--status", "dirty"])

    assert result.exit_code == 2


@pytest.mark.parametrize("answer, named", [
    (None, "no list of repositories"),
    ({"repositories": []}, "no list of repositories"),
    ([["not", "an", "object"]], "not an object"),
])
def test_list_repositories_AnswerIsNotAList_ExitsOne(serve, capsys, answer, named):
    serve(repositories=answer)

    for json_output in (True, False):
        with pytest.raises(typer.Exit) as exc:
            repo_ops.list_repositories(json_output=json_output)
        assert exc.value.exit_code == 1
        captured = capsys.readouterr()
        assert captured.out == ""
        assert named in captured.err


def test_list_repositories_GatewayError_ExitsOneOnStderr(serve, capsys, monkeypatch):
    def fail(path):
        raise repo_ops.gateway.GatewayError("Cannot reach the Gateway at http://gateway.invalid")

    monkeypatch.setattr(repo_ops.gateway, "get_json", fail)

    with pytest.raises(typer.Exit) as exc:
        repo_ops.list_repositories(json_output=False)

    assert exc.value.exit_code == 1
    captured = capsys.readouterr()
    assert captured.out == ""
    assert "Cannot reach the Gateway" in captured.err


@pytest.mark.parametrize("broken, named", [
    ({"isClean": None}, "isClean that is null"),
    ({"isClean": "yes"}, "isClean that is 'yes'"),
    ({"name": ""}, "no repository name"),
    ({"path": None}, "no repository path"),
    ({"machineName": "  "}, "no machine name"),
])
def test_list_repositories_UnreadableRow_ExitsOneNamingIt(serve, capsys, broken, named):
    row = _repo("broken", "/x/broken", clean=True)
    row.update(broken)
    serve(repositories=REPOS + [row])

    with pytest.raises(typer.Exit) as exc:
        repo_ops.list_repositories(json_output=False)

    assert exc.value.exit_code == 1
    captured = capsys.readouterr()
    assert captured.out == ""
    assert named in captured.err
    assert captured.err.isascii()


def test_list_repositories_JsonStateFilterWithUnreadableState_ExitsOne(serve, capsys):
    serve(repositories=REPOS + [_repo("broken", "/x/broken", clean=None)])

    with pytest.raises(typer.Exit) as exc:
        repo_ops.list_repositories(json_output=True, state="clean")

    assert exc.value.exit_code == 1
    assert capsys.readouterr().out == ""


def test_list_repositories_NonAsciiInAnError_IsWrittenAsAscii(serve, capsys):
    serve(repositories=[_repo("S\u00f8ren", "/x/S\u00f8ren", clean="\u2014")])

    with pytest.raises(typer.Exit):
        repo_ops.list_repositories(json_output=False)

    err = capsys.readouterr().err
    assert err.isascii()
    assert "\\u2014" in err


# ---------------------------------------------------------------------------------------------------
# worktree list: recoverability
# ---------------------------------------------------------------------------------------------------


def test_list_worktrees_DefaultOutput_EveryPathRepoMachineAndStateReadBackExactly(serve, capsys):
    serve(worktrees=WORKTREES)

    repo_ops.list_worktrees(json_output=False)

    out = capsys.readouterr().out
    assert out.isascii()
    _check_worktrees_recoverable(out, WORKTREES)
    fields, records = parse_list(out, "worktrees")
    assert fields == ["path", "repo", "machine", "state"]
    assert [r["state"] for r in records] == EXPECTED_WORKTREE_STATES


def test_worktree_recoverability_check_OldRichTable_Fails():
    old = _old_worktree_table(WORKTREES)

    with pytest.raises((ListParseError, AssertionError)):
        _check_worktrees_recoverable(old, WORKTREES)
    # The old table never showed the worktree's path, which is the only thing that names it.
    assert WORKTREES[2]["path"] not in old


def test_worktree_recoverability_check_ListThatShortensPaths_Fails():
    records = [
        {"path": "..." + w["path"][-10:], "repo": w["repoName"], "machine": w["machineName"], "state": w["state"]}
        for w in WORKTREES
    ]
    shortened = axi_output.render_list("worktrees", ["path", "repo", "machine", "state"], records)

    with pytest.raises(AssertionError):
        _check_worktrees_recoverable(shortened, WORKTREES)


def test_list_worktrees_Fields_SessionsBranchAndBytes(serve, capsys):
    serve(worktrees=WORKTREES)

    repo_ops.list_worktrees(json_output=False, fields="path,branch,sessions,bytes,provisional")

    fields, records = parse_list(capsys.readouterr().out, "worktrees")
    assert fields == ["path", "branch", "sessions", "bytes", "provisional"]
    assert records[0] == {
        "path": WORKTREES[0]["path"],
        "branch": None,
        "sessions": "Standby slot - Reviewer - Codex round 2, pull request 2946 (#106); second; session",
        "bytes": "804076644",
        "provisional": "false",
    }
    assert records[3]["sessions"] is None
    assert records[4]["bytes"] is None
    assert records[5]["provisional"] == "true"


# ---------------------------------------------------------------------------------------------------
# worktree list: counts, empty states, help
# ---------------------------------------------------------------------------------------------------


def test_list_worktrees_Unfiltered_CountsByStateReclaimableAndHelp(serve, capsys):
    serve(worktrees=WORKTREES)

    repo_ops.list_worktrees(json_output=False)

    lines = capsys.readouterr().out.splitlines()
    assert lines[0] == "count: 6 (needs-attention 2, in-use 1, safe-to-reap 2, verifying 1)"
    # One safe-to-reap worktree has no measured size: it is said, not counted as zero.
    assert ("reclaimable: 767 MB in 2 safe-to-reap worktrees (1 of unknown size). "
            "Reaping runs on the owning Director.") in lines
    assert ("repeated: 1 of these rows name a worktree path already listed on the same machine, "
            "reported by another Director there. Add director to --fields to tell them apart.") in lines
    help_index = next(i for i, line in enumerate(lines) if line.startswith("help["))
    assert "  cc-devthrottle worktree list --state safe-to-reap" in lines[help_index:]


def test_list_worktrees_NoSafeToReap_NoReclaimableLine(serve, capsys):
    serve(worktrees=WORKTREES)

    repo_ops.list_worktrees(json_output=False, state="in-use")

    out = capsys.readouterr().out
    assert out.splitlines()[0] == "count: 1 of 6 total (in-use 1)"
    assert "reclaimable" not in out


def test_list_worktrees_EmptyAnswer_PrintsCountZero(serve, capsys):
    serve(worktrees=[])

    repo_ops.list_worktrees(json_output=False)

    out = capsys.readouterr().out
    assert out.splitlines()[0] == "count: 0"
    assert "worktrees[0]{path,repo,machine,state}:" in out.splitlines()
    assert "No worktrees were returned." in out
    assert "Only Directors with a recent report are included" in out


def test_list_worktrees_FilterMatchesNothing_PrintsCountZeroOfTotal(serve, capsys):
    serve(worktrees=WORKTREES)

    repo_ops.list_worktrees(json_output=False, machine="NO_SUCH_MACHINE")

    out = capsys.readouterr().out
    assert out.splitlines()[0] == "count: 0 of 6 total"
    assert "No worktree matches the filter." in out
    assert "  cc-devthrottle worktree list" in out.splitlines()


# ---------------------------------------------------------------------------------------------------
# worktree list: filters, including the existing --repo and --state
# ---------------------------------------------------------------------------------------------------


@pytest.mark.parametrize("args, expected", [
    # The old behaviour: --repo by name, ignoring case, and one --state, ignoring case.
    (["--repo", "DevThrottle"], [0, 1, 2]),
    (["--state", "Safe-To-Reap"], [3, 4]),
    (["--repo", "devthrottle", "--state", "in-use"], [2]),
    # New: a full repository path, several states, a machine.
    (["--repo", r"D:\ReposFred\devthrottle"], [0, 1]),
    (["--repo", "notes, drafts"], [4]),
    (["--state", "safe-to-reap,verifying"], [3, 4, 5]),
    (["--machine", "devthrottle-MAC-mini"], [2, 5]),
])
def test_worktree_list_Cli_Filters_NarrowTextAndJsonAlike(serve, args, expected):
    serve(worktrees=WORKTREES)

    text = runner.invoke(app, ["worktree", "list", *args])
    as_json = runner.invoke(app, ["worktree", "list", *args, "--json"])

    assert text.exit_code == 0
    _, records = parse_list(text.stdout, "worktrees")
    assert [r["path"] for r in records] == [WORKTREES[i]["path"] for i in expected]
    assert text.stdout.splitlines()[0].startswith(f"count: {len(expected)} of 6 total")
    assert as_json.exit_code == 0
    assert as_json.stdout == json.dumps([WORKTREES[i] for i in expected], indent=2) + "\n"


# ---------------------------------------------------------------------------------------------------
# worktree list: --json keeps its shape
# ---------------------------------------------------------------------------------------------------


def test_list_worktrees_JsonUnfiltered_ByteForByteTheAnswer(serve, capsys):
    answer = WORKTREES + [_worktree("/x/future", "future", "/x", "somethingNew")]
    serve(worktrees=answer)

    repo_ops.list_worktrees(json_output=True)

    captured = capsys.readouterr()
    assert captured.out == json.dumps(answer, indent=2) + "\n"
    assert captured.err == ""


def test_list_worktrees_JsonFilterMatchesNothing_EmptyArray(serve, capsys):
    serve(worktrees=WORKTREES)

    repo_ops.list_worktrees(json_output=True, repo="no-such-repo")

    captured = capsys.readouterr()
    assert json.loads(captured.out) == []
    assert "Only Directors with a recent report are included" in captured.err


# ---------------------------------------------------------------------------------------------------
# worktree list: usage errors exit 2, unreadable answers exit 1
# ---------------------------------------------------------------------------------------------------


def test_worktree_list_Cli_UnknownState_ExitsTwoListingStates(serve):
    serve(worktrees=WORKTREES)

    result = runner.invoke(app, ["worktree", "list", "--state", "orphaned"])

    assert result.exit_code == 2
    assert "'orphaned'" in result.stderr
    assert "Valid states: needs-attention, in-use, safe-to-reap, verifying" in result.stderr
    assert result.stdout == ""


def test_worktree_list_Cli_UnknownField_ExitsTwoListingFields(serve):
    serve(worktrees=WORKTREES)

    result = runner.invoke(app, ["worktree", "list", "--fields", "path,size"])

    assert result.exit_code == 2
    assert "size" in result.stderr
    assert ", ".join(repo_ops.WORKTREE_LIST_FIELDS) in result.stderr


@pytest.mark.parametrize("args, message", [
    (["--json", "--fields", "path"], "--fields does not apply to --json"),
    (["--repo", " "], "--repo needs a value"),
    (["--state", ""], "--state needs a value"),
    (["--machine", " "], "--machine needs a value"),
])
def test_worktree_list_Cli_BadFlagValues_ExitTwo(serve, args, message):
    serve(worktrees=WORKTREES)

    result = runner.invoke(app, ["worktree", "list", *args])

    assert result.exit_code == 2
    assert message in result.stderr


def test_worktree_list_Cli_UnknownFlag_ExitsTwo(serve):
    serve(worktrees=WORKTREES)

    result = runner.invoke(app, ["worktree", "list", "--dirty"])

    assert result.exit_code == 2


def test_worktree_list_Cli_UnknownState_ExitsOneNamingIt(serve):
    serve(worktrees=WORKTREES + [_worktree("/x/future", "future", "/x", "somethingNew")])

    result = runner.invoke(app, ["worktree", "list"])

    assert result.exit_code == 1
    assert "'somethingNew'" in result.stderr
    assert "/x/future" in result.stderr
    assert result.stdout == ""


@pytest.mark.parametrize("broken, named", [
    ({"state": None}, "state that is null"),
    ({"path": ""}, "no worktree path"),
    ({"repoName": None}, "no repository name"),
    ({"sessionLabels": "one session"}, "sessionLabels should be a list"),
    ({"sizeBytes": "big"}, "sizeBytes should be a number"),
])
def test_list_worktrees_UnreadableRow_ExitsOneNamingIt(serve, capsys, broken, named):
    row = _worktree("/x/broken", "broken", "/x", "safe-to-reap")
    row.update(broken)
    serve(worktrees=WORKTREES + [row])

    with pytest.raises(typer.Exit) as exc:
        repo_ops.list_worktrees(json_output=False, fields="path,sessions")

    assert exc.value.exit_code == 1
    captured = capsys.readouterr()
    assert captured.out == ""
    assert named in captured.err


def test_list_worktrees_AnswerIsNotAList_ExitsOne(serve, capsys):
    serve(worktrees={"worktrees": []})

    with pytest.raises(typer.Exit) as exc:
        repo_ops.list_worktrees(json_output=True)

    assert exc.value.exit_code == 1
    captured = capsys.readouterr()
    assert captured.out == ""
    assert "no list of worktrees" in captured.err


# ---------------------------------------------------------------------------------------------------
# Inspection fixes: every field read is checked before any filter, on every path but unfiltered --json
# ---------------------------------------------------------------------------------------------------

_MISSING = object()

# Each field these two commands read, with values the Gateway never sends: the key left out, the wrong
# kind, and a value outside what RepoStatusDto / FleetWorktreeDto can carry.
BAD_REPO_FIELDS = [
    ("name", _MISSING), ("name", ""), ("name", 5),
    ("path", _MISSING), ("path", None), ("path", "  "),
    ("machineName", _MISSING), ("machineName", None),
    ("isClean", _MISSING), ("isClean", None), ("isClean", "yes"),
    ("branch", _MISSING), ("branch", None), ("branch", 3),
    ("uncommittedCount", _MISSING), ("uncommittedCount", None), ("uncommittedCount", "3"),
    ("uncommittedCount", -1), ("uncommittedCount", True), ("uncommittedCount", 1.5),
    ("aheadCount", _MISSING), ("aheadCount", -1),
    ("behindCount", _MISSING), ("behindCount", -1),
    ("behindMainCount", _MISSING), ("behindMainCount", None), ("behindMainCount", -2),
    ("worktreeCount", _MISSING), ("worktreeCount", None), ("worktreeCount", -1),
    ("worktreesSafeToReap", _MISSING), ("worktreesSafeToReap", None), ("worktreesSafeToReap", "1"),
    ("worktreeBytes", _MISSING), ("worktreeBytes", None), ("worktreeBytes", -5),
    ("provider", _MISSING), ("provider", None), ("provider", "Bitbucket"),
    ("org", _MISSING), ("org", 5),
    ("remoteUrl", _MISSING), ("remoteUrl", []),
    ("directorId", _MISSING), ("directorId", ""), ("directorId", None),
    ("provisional", _MISSING), ("provisional", None), ("provisional", "false"),
]

BAD_WORKTREE_FIELDS = [
    ("repoName", _MISSING), ("repoName", None),
    ("repoPath", _MISSING), ("repoPath", None), ("repoPath", ""),
    ("machineName", _MISSING), ("machineName", ""),
    ("directorId", _MISSING), ("directorId", None),
    ("path", _MISSING), ("path", ""),
    ("branch", _MISSING), ("branch", 7),
    ("state", _MISSING), ("state", None), ("state", "somethingNew"), ("state", "Safe-To-Reap"),
    ("reason", _MISSING), ("reason", None),
    ("sessionLabels", _MISSING), ("sessionLabels", None), ("sessionLabels", "one"), ("sessionLabels", [1]),
    ("sizeBytes", _MISSING), ("sizeBytes", "big"), ("sizeBytes", -1), ("sizeBytes", 1.5),
    ("lastActivityUtc", _MISSING), ("lastActivityUtc", "yesterday"), ("lastActivityUtc", 5),
    ("dataAgeSeconds", _MISSING), ("dataAgeSeconds", None), ("dataAgeSeconds", -1),
    ("dataAgeSeconds", "4"), ("dataAgeSeconds", True),
    ("provisional", _MISSING), ("provisional", None),
]

# The three paths that read the rows. The filter is one the broken row would NOT match, so a row that
# is filtered out before it is checked shows up as a false "no match".
REPO_PATHS = [
    ("plain", {}),
    ("filtered", {"machine": "NO_SUCH_MACHINE"}),
    ("filtered-json", {"machine": "NO_SUCH_MACHINE", "json_output": True}),
]


def _broken(row, key, value):
    row = dict(row)
    if value is _MISSING:
        del row[key]
    else:
        row[key] = value
    return row


def _assert_exits_one(call, capsys, named):
    with pytest.raises(typer.Exit) as exc:
        call()
    assert exc.value.exit_code == 1
    captured = capsys.readouterr()
    assert captured.out == ""
    assert named in captured.err
    assert captured.err.isascii()


@pytest.mark.parametrize("path_name, kwargs", REPO_PATHS)
@pytest.mark.parametrize("key, value", BAD_REPO_FIELDS)
def test_list_repositories_FieldTheGatewayNeverSends_ExitsOneOnEveryPath(serve, capsys, key, value, path_name, kwargs):
    serve(repositories=REPOS + [_broken(_repo("broken", "/x/broken", clean=True), key, value)])
    kwargs = {"json_output": False, **kwargs}

    _assert_exits_one(lambda: repo_ops.list_repositories(**kwargs), capsys, key)


@pytest.mark.parametrize("path_name, kwargs", REPO_PATHS)
@pytest.mark.parametrize("key, value", BAD_WORKTREE_FIELDS)
def test_list_worktrees_FieldTheGatewayNeverSends_ExitsOneOnEveryPath(serve, capsys, key, value, path_name, kwargs):
    serve(worktrees=WORKTREES + [_broken(_worktree("/x/broken", "broken", "/x", "safe-to-reap"), key, value)])
    kwargs = {"json_output": False, **kwargs}

    _assert_exits_one(lambda: repo_ops.list_worktrees(**kwargs), capsys, key)


def test_list_repositories_InspectionReproduction_NoMachineName_IsNotANoMatch(serve, capsys):
    row = {"name": "sample", "path": "/repos/sample", "isClean": True}
    serve(repositories=[row])

    for json_output in (False, True):
        _assert_exits_one(
            lambda: repo_ops.list_repositories(json_output, machine="host"), capsys, "machineName"
        )


@pytest.mark.parametrize("path_name, kwargs", REPO_PATHS)
@pytest.mark.parametrize("extra, named", [
    ({"worktreeCount": 2, "worktreesSafeToReap": 3}, "worktreesSafeToReap (3) is more than worktreeCount (2)"),
    ({"provisional": True, "worktreesSafeToReap": 1}, "the Gateway serves it as 0"),
])
def test_list_repositories_CountsThatContradict_ExitOneOnEveryPath(serve, capsys, extra, named, path_name, kwargs):
    serve(repositories=REPOS + [_repo("broken", "/x/broken", clean=True, **extra)])
    kwargs = {"json_output": False, **kwargs}

    _assert_exits_one(lambda: repo_ops.list_repositories(**kwargs), capsys, named)


@pytest.mark.parametrize("path_name, kwargs", REPO_PATHS)
@pytest.mark.parametrize("state, provisional", [("verifying", False), ("safe-to-reap", True)])
def test_list_worktrees_StateThatContradictsProvisional_ExitsOneOnEveryPath(
        serve, capsys, state, provisional, path_name, kwargs):
    serve(worktrees=WORKTREES + [_worktree("/x/broken", "broken", "/x", state, provisional=provisional)])
    kwargs = {"json_output": False, **kwargs}

    _assert_exits_one(
        lambda: repo_ops.list_worktrees(**kwargs), capsys, "verifying exactly when provisional is true"
    )


def test_list_repositories_InspectionReproduction_NoCounts_NotSummedAsZero(serve, capsys):
    row = _repo("sample", "/repos/sample", clean=True)
    del row["worktreeCount"]
    del row["worktreesSafeToReap"]
    serve(repositories=[row])

    _assert_exits_one(lambda: repo_ops.list_repositories(False), capsys, "worktreeCount")


def test_list_repositories_JsonUnfilteredWithBrokenRow_StillTheRawAnswer(serve, capsys):
    answer = REPOS + [_broken(_repo("broken", "/x/broken", clean=True), "worktreeCount", _MISSING)]
    serve(repositories=answer)

    repo_ops.list_repositories(json_output=True)

    assert capsys.readouterr().out == json.dumps(answer, indent=2) + "\n"


def test_list_worktrees_JsonUnfilteredWithBrokenRow_StillTheRawAnswer(serve, capsys):
    answer = WORKTREES + [_broken(_worktree("/x/broken", "broken", "/x", "safe-to-reap"), "sessionLabels", None)]
    serve(worktrees=answer)

    repo_ops.list_worktrees(json_output=True)

    assert capsys.readouterr().out == json.dumps(answer, indent=2) + "\n"


def test_list_repositories_EveryNullOrEdgeValueTheGatewaySends_Lists(serve, capsys):
    row = _repo("edge", "/x/edge", clean=True, org=None, remoteUrl=None, provider="None", branch="",
                behindMainCount=-1, worktreeCount=0, worktreesSafeToReap=0, worktreeBytes=0)
    provisional = _repo("verifying", "/x/verifying", clean=False, provisional=True, worktreesSafeToReap=0)
    serve(repositories=[row, provisional])

    repo_ops.list_repositories(json_output=False, fields=",".join(repo_ops.REPO_LIST_FIELDS))

    out = capsys.readouterr().out
    _, records = parse_list(out, "repositories")
    assert [r["name"] for r in records] == ["edge", "verifying"]
    assert "worktrees in these repositories: 2, safe to reap: 0" in out


def test_list_worktrees_EveryNullOrEdgeValueTheGatewaySends_Lists(serve, capsys):
    row = _worktree("/x/edge", "edge", "/x", "in-use", branch=None, size=None, reason="",
                    lastActivityUtc=None, dataAgeSeconds=0)
    unzoned = _worktree("/x/unzoned", "edge", "/x", "needs-attention", lastActivityUtc="2026-09-06T15:47:11")
    serve(worktrees=[row, unzoned])

    repo_ops.list_worktrees(json_output=False, fields=",".join(repo_ops.WORKTREE_LIST_FIELDS))

    _, records = parse_list(capsys.readouterr().out, "worktrees")
    assert [r["path"] for r in records] == ["/x/edge", "/x/unzoned"]


def test_list_worktrees_PascalCaseRows_ReadTheSame(serve, capsys):
    pascal = [{k[0].upper() + k[1:]: v for k, v in row.items()} for row in WORKTREES]
    serve(worktrees=pascal)

    repo_ops.list_worktrees(json_output=False)

    _check_worktrees_recoverable(capsys.readouterr().out, WORKTREES)


# ---------------------------------------------------------------------------------------------------
# Inspection fixes: a full path ignores case and slash direction only for a Windows path
# ---------------------------------------------------------------------------------------------------

LINUX_UPPER = "/home/A/proj"
LINUX_LOWER = "/home/a/proj"


@pytest.mark.parametrize("row_path, wanted, expected", [
    # The inspection's two Linux paths are two repositories.
    (LINUX_UPPER, LINUX_UPPER, True),
    (LINUX_UPPER, LINUX_LOWER, False),
    (LINUX_LOWER, LINUX_UPPER, False),
    (LINUX_LOWER, LINUX_LOWER, True),
    ("/Users/soren/ReposFred/devthrottle", "/users/soren/reposfred/devthrottle", False),
    # A Windows path ignores case and slash direction, whichever way the caller wrote it.
    (r"D:\ReposFred\devthrottle", "d:/reposfred/DEVTHROTTLE", True),
    (r"D:\ReposFred\devthrottle", "D:\\ReposFred\\devthrottle\\", True),
    ("C:/ReposFred/cc-director", r"c:\reposfred\CC-DIRECTOR", True),
    (r"\\server\share\Repo", "//SERVER/share/repo", True),
    (r"D:\ReposFred\devthrottle", r"D:\ReposFred\other", False),
])
def test_path_matches_WindowsOnlyIgnoresCaseAndSlashes(row_path, wanted, expected):
    assert repo_ops.path_matches(row_path, wanted) is expected


@pytest.mark.parametrize("path, expected", [
    (r"D:\ReposFred", True), ("c:/repos", True), ("C:", True), (r"\\server\share", True),
    (LINUX_UPPER, False), ("/Users/soren/C:/odd", False), ("relative/path", False),
])
def test_is_windows_path_DriveLetterOrBackslash(path, expected):
    assert repo_ops.is_windows_path(path) is expected


def test_repo_list_Cli_LinuxPathsDifferingByCase_AreDistinct(serve):
    upper = _repo("proj", LINUX_UPPER, clean=True, machine="linux-box")
    lower = _repo("proj", LINUX_LOWER, clean=False, machine="linux-box")
    serve(repositories=[upper, lower])

    for wanted, expected in ((LINUX_UPPER, upper), (LINUX_LOWER, lower)):
        result = runner.invoke(app, ["repo", "list", "--repo", wanted, "--json"])
        assert result.exit_code == 0
        assert json.loads(result.stdout) == [expected]
        text = runner.invoke(app, ["repo", "list", "--repo", wanted])
        _, records = parse_list(text.stdout, "repositories")
        assert [r["path"] for r in records] == [wanted]


def test_repo_list_Cli_FolderNameStillIgnoresCase(serve):
    upper = _repo("proj", LINUX_UPPER, clean=True, machine="linux-box")
    lower = _repo("proj", LINUX_LOWER, clean=False, machine="linux-box")
    serve(repositories=[upper, lower])

    result = runner.invoke(app, ["repo", "list", "--repo", "PROJ", "--json"])

    assert json.loads(result.stdout) == [upper, lower]


def test_worktree_list_Cli_LinuxRepoPathsDifferingByCase_AreDistinct(serve):
    upper = _worktree("/home/A/proj-wt", "proj", LINUX_UPPER, "in-use", machine="linux-box")
    lower = _worktree("/home/a/proj-wt", "proj", LINUX_LOWER, "in-use", machine="linux-box")
    serve(worktrees=[upper, lower])

    for wanted, expected in ((LINUX_UPPER, upper), (LINUX_LOWER, lower)):
        result = runner.invoke(app, ["worktree", "list", "--repo", wanted, "--json"])
        assert result.exit_code == 0
        assert json.loads(result.stdout) == [expected]
        text = runner.invoke(app, ["worktree", "list", "--repo", wanted])
        _, records = parse_list(text.stdout, "worktrees")
        assert [r["path"] for r in records] == [expected["path"]]


def test_list_repositories_LinuxPathsDifferingByCase_AreNotCalledRepeats(serve, capsys):
    serve(repositories=[
        _repo("proj", LINUX_UPPER, clean=True, machine="linux-box"),
        _repo("proj", LINUX_LOWER, clean=True, machine="linux-box", director=DIRECTOR_B),
    ])

    repo_ops.list_repositories(json_output=False)

    assert "repeated:" not in capsys.readouterr().out
