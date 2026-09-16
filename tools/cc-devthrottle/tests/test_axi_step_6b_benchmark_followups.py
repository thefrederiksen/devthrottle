"""AXI step 6b (#2922): the follow-ups the benchmark re-run and the step 6a re-check found.

1. `session list` never said how to message a session, so agents guessed `session message` and
   `session send`. Its help block now names `message send`.
2. The count line names only the states that have sessions, so no agent learned `--state crashed`
   existed and all of them read the whole fleet as JSON instead. The help block now names all five.
3. `--fields` with `--json` was refused with "Drop one of them". It now says which to use.
4. `repo_ops.matches_repo` trimmed whitespace, so `--repo a` also matched the folder `a `. Folder names
   are now compared ignoring case only, in session list, repo list and worktree list alike.
"""

import json
import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared.axi_output import parse_list  # noqa: E402
from src import mission_ops, repo_ops, session_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

ALL_STATES_HELP = "  cc-devthrottle session list --state needs-you|working|ready|snoozed|crashed"
MESSAGE_HELP = '  cc-devthrottle message send <session-id> "<message>"'

PLAIN_ID = "b0000000-0000-4000-8000-000000000001"
SPACED_ID = "b0000000-0000-4000-8000-000000000002"
PLAIN_PATH = "/home/a"
SPACED_PATH = "/home/a "


def _session(sid, *, bucket="active", activity="Working", repo=PLAIN_PATH):
    return {
        "sessionId": sid,
        "number": 100,
        "name": "worker",
        "machineName": "linux-box",
        "repoPath": repo,
        "activityState": activity,
        "triageBucket": bucket,
        "crashed": False,
    }


def _repo(path):
    return {
        "directorId": "d0000000-0000-4000-8000-000000000001",
        "machineName": "linux-box",
        "path": path,
        "name": path.rsplit("/", 1)[-1],
        "remoteUrl": "https://github.com/example/a.git",
        "provider": "GitHub",
        "org": "example",
        "branch": "main",
        "isClean": True,
        "uncommittedCount": 0,
        "dirtySinceUtc": None,
        "aheadCount": 0,
        "behindCount": 0,
        "behindMainCount": -1,
        "worktreeCount": 0,
        "worktreesSafeToReap": 0,
        "worktreesInUse": 0,
        "worktreesNeedAttention": 0,
        "worktreeBytes": 0,
        "provisional": False,
        "worktrees": [],
    }


def _worktree(repo_path):
    return {
        "repoName": repo_path.rsplit("/", 1)[-1],
        "repoPath": repo_path,
        "machineName": "linux-box",
        "directorId": "d0000000-0000-4000-8000-000000000001",
        "path": repo_path + "-wt",
        "branch": "feature",
        "state": "in-use",
        "reason": "",
        "sessionLabels": [],
        "sizeBytes": 0,
        "lastActivityUtc": "2026-09-16T10:00:00Z",
        "dataAgeSeconds": 1.5,
        "provisional": False,
    }


@pytest.fixture
def fleet(monkeypatch):
    def serve(sessions):
        monkeypatch.setattr(session_ops.gateway, "get_fleet", lambda: (sessions, True, None, None))
    return serve


@pytest.fixture
def lists(monkeypatch):
    def serve(repositories=(), worktrees=()):
        answers = {"repositories": list(repositories), "worktrees": list(worktrees)}

        def get_json(path):
            return answers[path]

        monkeypatch.setattr(repo_ops.gateway, "get_json", get_json)
    return serve


@pytest.fixture
def no_gateway(monkeypatch):
    """Every Gateway door raises: a usage error must be decided before anything is asked."""
    def refuse(*_args, **_kwargs):
        raise AssertionError("a usage error must not reach the Gateway")

    for name in ("get_fleet", "get_json", "post_json", "patch_json", "delete"):
        if hasattr(session_ops.gateway, name):
            monkeypatch.setattr(session_ops.gateway, name, refuse)
    monkeypatch.setattr(mission_ops.MissionClient, "list_all", refuse)


def _help_lines(output):
    lines = output.splitlines()
    start = next(i for i, line in enumerate(lines) if line.startswith("help["))
    return lines[start + 1:]


# ===== 1 and 2: the session list help block ======================================================


def test_sessionList_SomeStatesEmpty_HelpNamesAllFiveStatesAndMessageSend(fleet):
    # Only working sessions: the count line cannot mention crashed, so the help block must.
    fleet([_session(PLAIN_ID), _session(SPACED_ID)])

    result = runner.invoke(app, ["session", "list"])

    assert result.exit_code == 0, result.output
    assert result.stdout.splitlines()[0] == "count: 2 (working 2)"
    help_lines = _help_lines(result.stdout)
    assert ALL_STATES_HELP in help_lines
    assert MESSAGE_HELP in help_lines


def test_sessionList_Filtered_HelpStillNamesAllFiveStates(fleet):
    fleet([_session(PLAIN_ID)])

    result = runner.invoke(app, ["session", "list", "--state", "working"])

    assert result.exit_code == 0, result.output
    help_lines = _help_lines(result.stdout)
    assert ALL_STATES_HELP in help_lines
    assert MESSAGE_HELP in help_lines


def test_sessionList_FilterMatchesNothing_HelpNamesTheOtherStates(fleet):
    fleet([_session(PLAIN_ID)])

    result = runner.invoke(app, ["session", "list", "--state", "crashed"])

    assert result.exit_code == 0, result.output
    assert result.stdout.splitlines()[0] == "count: 0 of 1 total"
    assert ALL_STATES_HELP in _help_lines(result.stdout)


def test_sessionList_StateHelpLine_IsEveryValidState():
    # The help line and the --state validator read one tuple, so they cannot name different states.
    assert session_ops.SESSION_LIST_STATE_HELP.split("--state ", 1)[1].split("|") == list(session_ops.SESSION_STATES)


# ===== 3: --fields with --json says which to use =================================================


@pytest.mark.parametrize("args", [
    ["session", "list"],
    ["repo", "list"],
    ["worktree", "list"],
    ["mission", "list"],
    ["director", "list"],
    ["machine", "list"],
    ["schedule", "list"],
])
def test_fieldsWithJson_SaysWhichToUse(no_gateway, args):
    result = runner.invoke(app, [*args, "--json", "--fields", "id"])

    assert result.exit_code == 2, result.output
    assert result.stdout == ""
    first = result.stderr.splitlines()[0]
    assert first == (
        "Error: --fields does not apply to --json, which always carries every field. "
        "Use --fields to show a few fields, or --json to get every field."
    )
    assert "Drop one of them" not in result.stderr


# ===== 4: whitespace is part of a folder name ====================================================


@pytest.mark.parametrize("wanted, expected", [
    ("a", [PLAIN_ID]),
    ("A", [PLAIN_ID]),
    ("a ", [SPACED_ID]),
    (PLAIN_PATH, [PLAIN_ID]),
    (SPACED_PATH, [SPACED_ID]),
])
def test_sessionList_RepoFilter_KeepsWhitespaceInFolderNames(fleet, wanted, expected):
    fleet([_session(PLAIN_ID, repo=PLAIN_PATH), _session(SPACED_ID, repo=SPACED_PATH)])

    as_json = runner.invoke(app, ["session", "list", "--repo", wanted, "--json"])
    as_text = runner.invoke(app, ["session", "list", "--repo", wanted])

    assert as_json.exit_code == 0 and as_text.exit_code == 0, as_json.output + as_text.output
    assert [s["sessionId"] for s in json.loads(as_json.stdout)] == expected
    _, records = parse_list(as_text.stdout, "sessions")
    assert [r["id"] for r in records] == expected


@pytest.mark.parametrize("wanted, expected", [
    ("a", [PLAIN_PATH]),
    ("A", [PLAIN_PATH]),
    ("a ", [SPACED_PATH]),
    (PLAIN_PATH, [PLAIN_PATH]),
    (SPACED_PATH, [SPACED_PATH]),
])
def test_repoList_RepoFilter_KeepsWhitespaceInFolderNames(lists, wanted, expected):
    lists(repositories=[_repo(PLAIN_PATH), _repo(SPACED_PATH)])

    as_json = runner.invoke(app, ["repo", "list", "--repo", wanted, "--json"])
    as_text = runner.invoke(app, ["repo", "list", "--repo", wanted])

    assert as_json.exit_code == 0 and as_text.exit_code == 0, as_json.output + as_text.output
    assert [r["path"] for r in json.loads(as_json.stdout)] == expected
    _, records = parse_list(as_text.stdout, "repositories")
    assert [r["path"] for r in records] == expected


@pytest.mark.parametrize("wanted, expected", [
    ("a", [PLAIN_PATH]),
    ("A", [PLAIN_PATH]),
    ("a ", [SPACED_PATH]),
    (PLAIN_PATH, [PLAIN_PATH]),
    (SPACED_PATH, [SPACED_PATH]),
])
def test_worktreeList_RepoFilter_KeepsWhitespaceInFolderNames(lists, wanted, expected):
    lists(worktrees=[_worktree(PLAIN_PATH), _worktree(SPACED_PATH)])

    as_json = runner.invoke(app, ["worktree", "list", "--repo", wanted, "--json"])
    as_text = runner.invoke(app, ["worktree", "list", "--repo", wanted])

    assert as_json.exit_code == 0 and as_text.exit_code == 0, as_json.output + as_text.output
    assert [w["repoPath"] for w in json.loads(as_json.stdout)] == expected
    _, records = parse_list(as_text.stdout, "worktrees")
    assert [r["path"] for r in records] == [p + "-wt" for p in expected]


def test_matchesRepo_ComparesFolderNamesIgnoringCaseOnly():
    assert repo_ops.matches_repo("Proj", "/x/Proj", "proj")
    assert not repo_ops.matches_repo("proj ", "/x/proj ", "proj")
    assert not repo_ops.matches_repo("proj", "/x/proj", " proj")
