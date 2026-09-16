"""AXI step 6b (issue #2922): next steps after a change, short help, and errors an agent can act on.

For the groups session, message, mission, repo, worktree, director and machine, and the root app:

- every command that changes something ends its plain output with a `help[]` block, and the block holds
  placeholders or values the command's own result supplied - never a guessed value;
- `--json` output of those commands is exactly the Gateway's answer, with nothing added;
- every error goes to standard error, names what failed and what to do next, and exits non-zero
  (2 when the caller has to change what they typed);
- every command and group in the tree has a one-line help summary.

The Gateway is replaced by a fake that answers each route, so these run anywhere with no network.
"""

import json
import sys
from pathlib import Path

import pytest
import typer
import typer.main
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import axi_cli, machine_ops, mission_ops, session_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

SID = "9c41e7a2-1111-2222-3333-444444444444"
PARENT = "5a5a5a5a-6666-7777-8888-999999999999"
NEW = "0d0d0d0d-aaaa-bbbb-cccc-dddddddddddd"
MID = "7e7e7e7e-1234-5678-9abc-def012345678"
OTHER_MID = "7e7e0000-1234-5678-9abc-def012345678"
DIRECTOR = "dir-mac-1"

ROSTER = [
    {
        "sessionId": SID, "name": "worker one", "machineName": "MAC", "number": 101,
        "repoPath": "/repos/devthrottle", "triageBucket": "active", "activityState": "Idle",
        "controllerSessionId": PARENT, "hasLiveSupervisor": True,
        "missionId": MID, "missionName": "AXI",
    },
    {
        "sessionId": PARENT, "name": "manager", "machineName": "MAC", "number": 102,
        "repoPath": "/repos/devthrottle", "triageBucket": "active", "activityState": "Working",
        "hasLiveSupervisor": False,
    },
]

MISSION = {"missionId": MID, "missionName": "AXI", "state": "active"}
OTHER_MISSION = {"missionId": OTHER_MID, "missionName": "AXI two", "state": "active"}

STOP_ANSWER = {"verdict": "stopped", "headline": f"stopped {SID}", "details": ["worktree is clean"]}
LAUNCH_ANSWER = {"machine": "MAC", "verb": "launch", "relayStatus": 200, "payload": "{\"started\":true}"}
RESTART_ANSWER = {
    "id": "req-1", "title": "Restart MAC", "askedBySentence": "Asked by a session.",
    "liveSessionsSentence": "2 sessions are live.", "expiresAtUtc": "2026-09-16T20:00:00Z",
    "capability": {"reason": "It can restart.", "guardedRestartReason": "Guarded."},
}


def _default_answers():
    # The shapes the Gateway on origin/main answers with (GatewayEndpoints.cs, MachineEndpoints.cs,
    # SessionWriteExecutor.cs): each change is confirmed from these fields, never from what was asked.
    return {
        ("PATCH", f"sessions/{SID}"): {"sessionId": SID, "name": "new name"},
        ("POST", f"sessions/{SID}/prompt"): {"accepted": True},
        ("POST", f"sessions/{SID}/interrupt"): {"accepted": True},
        ("POST", f"sessions/{SID}/hold"): lambda body: {"onHold": body["onHold"], "pending": False},
        ("POST", f"sessions/{SID}/needs-manager"): lambda body: {"sessionId": SID, "raised": body["raised"]},
        ("POST", f"sessions/{PARENT}/message"): {"accepted": True},
        ("POST", f"sessions/{SID}/message"): {"accepted": True, "output": "the answer", "waitStatus": "idle"},
        ("POST", "fleet/broadcast"): {"results": [{"sessionId": SID}, {"sessionId": PARENT}]},
        ("POST", f"sessions/{SID}/compact-context"): {"submitted": True, "compactionObserved": True, "detail": "Done."},
        ("POST", f"sessions/{SID}/role"): {"sessionId": SID, "explicitRole": "Worker"},
        ("POST", f"sessions/{SID}/request-deletion"): {"pendingDeletion": True},
        ("DELETE", f"sessions/{SID}/request-deletion"): {"pendingDeletion": False},
        ("POST", f"sessions/{SID}/stop"): STOP_ANSWER,
        ("POST", f"directors/{DIRECTOR}/sessions"): {"sessionId": NEW, "name": "spawned"},
        ("POST", f"sessions/{SID}/mission"): lambda body: {
            "session": {"sessionId": SID, "missionId": body.get("missionId")},
            "previousMissionId": OTHER_MID, "previousMissionName": "AXI two",
        },
        ("POST", "machines/MAC/launch"): LAUNCH_ANSWER,
        ("POST", "machines/MAC/director/restart-requests"): RESTART_ANSWER,
        ("GET", "directors"): [{"directorId": DIRECTOR, "displayName": "Mac", "machineName": "MAC"}],
    }


class FakeGateway:
    """Every Gateway route these commands use, answered from a table. A route not in the table fails the test."""

    def __init__(self):
        self.answers = _default_answers()
        self.calls = []
        self.roster = (ROSTER, True, None, None)
        self.missions = [MISSION, OTHER_MISSION]
        self.mission_patch = None  # None: echo the mission back
        self.mission_create = None  # None: the created mission

    def _answer(self, method, path, body):
        self.calls.append((method, path, body))
        key = (method, path)
        if key not in self.answers:
            raise AssertionError(f"unexpected Gateway call {method} {path}")
        answer = self.answers[key]
        if isinstance(answer, Exception):
            raise answer
        if callable(answer):
            return answer(body)
        return answer

    def get_fleet(self):
        if isinstance(self.roster, Exception):
            raise self.roster
        return self.roster


@pytest.fixture
def gw(monkeypatch):
    fake = FakeGateway()
    g = session_ops.gateway
    monkeypatch.setattr(g, "get_fleet", fake.get_fleet)
    monkeypatch.setattr(g, "get_json", lambda path, timeout=30: fake._answer("GET", path, None))
    monkeypatch.setattr(g, "post_json", lambda path, body=None, timeout=30: fake._answer("POST", path, body))
    monkeypatch.setattr(g, "patch_json", lambda path, body, timeout=30: fake._answer("PATCH", path, body))
    monkeypatch.setattr(g, "delete", lambda path, timeout=30: fake._answer("DELETE", path, None))

    def list_all(self, state=None):
        fake.calls.append(("GET", "missions", state))
        return list(fake.missions)

    def create(self, name):
        fake.calls.append(("POST", "missions", name))
        if fake.mission_create is not None:
            return fake.mission_create
        return dict(MISSION, missionName=name.strip())  # the Gateway stores the name trimmed

    def patch(self, mission_id, body):
        fake.calls.append(("PATCH", f"missions/{mission_id}", body))
        if fake.mission_patch is not None:
            return fake.mission_patch
        mission = next(m for m in fake.missions if m["missionId"] == mission_id)
        return {"mission": dict(mission, **body)}

    monkeypatch.setattr(mission_ops.MissionClient, "__init__", lambda self, base_url=None: None)
    monkeypatch.setattr(mission_ops.MissionClient, "list_all", list_all)
    monkeypatch.setattr(mission_ops.MissionClient, "create", create)
    monkeypatch.setattr(mission_ops.MissionClient, "patch", patch)

    monkeypatch.delenv("CC_SESSION_ID", raising=False)
    monkeypatch.setenv("CC_DIRECTOR_ID", DIRECTOR)
    return fake


def _run(argv, env=None, monkeypatch=None):
    if env:
        for key, value in env.items():
            monkeypatch.setenv(key, value)
    return runner.invoke(app, argv)


def _help_block(stdout):
    """The help[] block that ends the output: (count, commands). Fails when the output does not end with one."""
    lines = stdout.rstrip("\n").split("\n")
    for index in range(len(lines) - 1, -1, -1):
        line = lines[index]
        if line.startswith("help[") and line.endswith("]:"):
            count = int(line[len("help["):-2])
            commands = lines[index + 1:]
            assert len(commands) == count, f"help[{count}] is followed by {len(commands)} lines:\n{stdout}"
            assert all(c.startswith("  ") for c in commands), stdout
            return count, [c[2:] for c in commands]
    raise AssertionError(f"no help[] block at the end of:\n{stdout}")


# ===== help[] after every change ================================================================

AS_SID = {"CC_SESSION_ID": SID}

MUTATIONS = [
    ("session rename", ["session", "rename", SID, "new name"], None,
     ["cc-devthrottle session list", f'cc-devthrottle message send {SID} "<message>"']),
    ("session prompt", ["session", "prompt", SID, "hello"], None,
     [f"cc-devthrottle session buffer {SID}", f"cc-devthrottle session interrupt {SID}"]),
    ("session interrupt", ["session", "interrupt", SID], None,
     [f"cc-devthrottle session buffer {SID}", f'cc-devthrottle session prompt {SID} "<text>"']),
    ("session report", ["session", "report", "did the work"], AS_SID,
     [f"cc-devthrottle session buffer {PARENT}", "cc-devthrottle session done"]),
    ("session raise", ["session", "raise", "which branch?"], AS_SID,
     ["cc-devthrottle session raise --clear"]),
    ("session raise --target", ["session", "raise", "which branch?", "--target", SID], None,
     [f"cc-devthrottle session raise --clear --target {SID}"]),
    ("session raise --clear", ["session", "raise", "--clear"], AS_SID,
     ['cc-devthrottle session raise "<what you need>"']),
    ("session hold", ["session", "hold", SID, "--minutes", "5"], None,
     ["cc-devthrottle session list --state snoozed", f"cc-devthrottle session hold {SID} --release"]),
    ("session hold --release", ["session", "hold", SID, "--release"], None,
     ["cc-devthrottle session list --state needs-you", f"cc-devthrottle session hold {SID} --minutes <n>"]),
    ("session compact", ["session", "compact", SID], None,
     [f"cc-devthrottle session buffer {SID}", f'cc-devthrottle message send {SID} "<message>"']),
    ("session compact-continue", ["session", "compact-continue", SID], None,
     [f"cc-devthrottle session buffer {SID}"]),
    ("session role", ["session", "role", SID, "Worker"], None,
     [f"cc-devthrottle session role {SID} none", "cc-devthrottle session list"]),
    ("session stop", ["session", "stop", SID, "--reason", "finished"], None,
     ["cc-devthrottle session list", "cc-devthrottle session spawn <repo> --controlled-by self"]),
    ("session done", ["session", "done", SID], None,
     [f"cc-devthrottle session done {SID} --undo", "cc-devthrottle session list"]),
    ("session done --undo", ["session", "done", SID, "--undo"], None,
     ["cc-devthrottle session list", f"cc-devthrottle session done {SID}"]),
    ("session spawn", ["session", "spawn", "/repos/x", "--controlled-by", "self", "--name", "n", "--mission", "none"],
     AS_SID,
     [f'cc-devthrottle message send {NEW} "<message>"', f"cc-devthrottle session buffer {NEW}"]),
    ("message send", ["message", "send", SID, "hello"], None,
     [f"cc-devthrottle session buffer {SID}", f'cc-devthrottle message ask {SID} "<question>"']),
    ("message send all", ["message", "send", "all", "hello"], AS_SID,
     ["cc-devthrottle session list", 'cc-devthrottle message send <session-id> "<message>"']),
    ("message ask", ["message", "ask", SID, "which branch?"], None,
     [f"cc-devthrottle session buffer {SID}", f'cc-devthrottle message send {SID} "<message>"']),
    ("mission create", ["mission", "create", "AXI"], None,
     [f"cc-devthrottle session spawn <repo> --mission {MID} --controlled-by self",
      f"cc-devthrottle mission attach <session-id> {MID}", "cc-devthrottle mission list"]),
    ("mission rename", ["mission", "rename", MID, "AXI renamed"], None,
     ["cc-devthrottle session list --fields id,name,state,mission",
      f"cc-devthrottle mission attach <session-id> {MID}"]),
    ("mission complete", ["mission", "complete", MID], None,
     ["cc-devthrottle mission list --state complete", f"cc-devthrottle mission reopen {MID}"]),
    ("mission remove", ["mission", "remove", MID], None,
     ["cc-devthrottle mission list --state removed", f"cc-devthrottle mission reopen {MID}"]),
    ("mission reopen", ["mission", "reopen", MID], None,
     ["cc-devthrottle mission list", f"cc-devthrottle mission attach <session-id> {MID}"]),
    ("mission attach", ["mission", "attach", SID, MID], None,
     [f"cc-devthrottle mission detach {SID}", "cc-devthrottle session list --fields id,name,state,mission"]),
    ("mission detach", ["mission", "detach", SID], None,
     [f"cc-devthrottle mission attach {SID} {OTHER_MID}", "cc-devthrottle mission list"]),
    ("machine launch", ["machine", "launch", "MAC", "--app", "Chrome"], None,
     ['cc-devthrottle machine apps MAC "<query>"', 'cc-devthrottle machine files MAC "<name>"']),
    ("machine restart-request", ["machine", "restart-request", "MAC", "--reason", "update it"], None,
     ["cc-devthrottle machine restart-request-status MAC req-1", "cc-devthrottle director list --machine MAC"]),
]


@pytest.mark.parametrize("label,argv,env,expected", MUTATIONS, ids=[m[0] for m in MUTATIONS])
def test_mutation_PlainOutput_EndsWithTheExactHelpBlock(gw, monkeypatch, label, argv, env, expected):
    result = _run(argv, env, monkeypatch)

    assert result.exit_code == 0, result.output
    assert result.stderr == "", result.stderr
    assert result.stdout.isascii(), result.stdout
    _, commands = _help_block(result.stdout)
    # Exactly these: placeholders, or values this command's own result supplied. Nothing guessed.
    assert commands == expected


def test_mutation_EveryChangingCommandInScopeIsCovered():
    """A mutating command added to these groups without a help[] test fails here, not in review."""
    from src.cli import _ACTIONS

    covered = {m[0].split(" --")[0] for m in MUTATIONS} | {"selftest"}
    in_scope = ("session-", "message-", "mission-", "machine-", "fleet-selftest")
    for action in _ACTIONS:
        if not action["mutatesState"] or not action["id"].startswith(in_scope):
            continue
        words = action["command"].split()
        command = "selftest" if words[1] == "selftest" else f"{words[1]} {words[2]}"
        assert command in covered, f"{action['id']} changes state but has no help[] test"


def test_sessionWhoami_ShowsTheFullIdAndEndsWithHelp(gw, monkeypatch, plain):
    result = _run(["session", "whoami"], AS_SID, monkeypatch)

    assert result.exit_code == 0, result.output
    out = " ".join(plain(result.stdout).split())
    assert f'You are session number 101, {SID} ("worker one") on MAC, repo devthrottle.' in out
    assert _help_block(result.stdout)[1] == [
        'cc-devthrottle message send <session-id> "<message>"',
        'cc-devthrottle message send all "<message>"',
        "cc-devthrottle session list",
    ]


def test_sessionWhoami_NoNumber_DoesNotPrintAnEmptyOne(gw, monkeypatch, plain):
    gw.roster = ([{k: v for k, v in ROSTER[0].items() if k != "number"}], True, None, None)

    result = _run(["session", "whoami"], AS_SID, monkeypatch)

    assert result.exit_code == 0, result.output
    assert "number" not in plain(result.stdout).split("help[")[0]


def test_sessionWhoami_NotOnTheRoster_SaysSoWithTheFullId(gw, monkeypatch, plain):
    result = _run(["session", "whoami"], {"CC_SESSION_ID": NEW}, monkeypatch)

    assert result.exit_code == 0, result.output
    assert f"You are session {NEW}." in plain(result.stdout)


def test_sessionWorkers_TableIsAscii(gw, monkeypatch):
    gw.answers[("GET", "sessions")] = {"sessions": [dict(ROSTER[0], needsManager=True, needsManagerReason="which branch?")]}

    result = _run(["session", "workers", "--target", PARENT], None, monkeypatch)

    assert result.exit_code == 0, result.output
    assert result.stdout.isascii(), result.stdout
    assert "which branch?" in result.stdout


def test_textLines_AreNeverWrappedAtTheConsoleWidth(gw, monkeypatch, plain):
    """An id or a name split across two lines cannot be read back or pasted."""
    long_name = "AXI Tools - Worker - step 6b help and errors part A, with a name longer than any terminal"
    gw.answers[("PATCH", f"sessions/{SID}")] = {"sessionId": SID, "name": long_name}

    result = _run(["session", "rename", SID, long_name], None, monkeypatch)

    assert result.exit_code == 0, result.output
    assert f'Renamed {SID} to "{long_name}".' in plain(result.stdout).splitlines()


def test_sessionHold_PendingFalse_ReportsHeldNotQueued(gw, monkeypatch, plain):
    # The Gateway sends pending as a boolean. Read through gateway.field it became the string "False",
    # which is truthy, so every immediate hold was reported as queued.
    gw.answers[("POST", f"sessions/{SID}/hold")] = {"onHold": True, "pending": False}

    result = _run(["session", "hold", SID], None, monkeypatch)

    assert result.exit_code == 0, result.output
    assert f"Held {SID}" in plain(result.stdout)
    assert "queued" not in result.stdout


def test_sessionHold_PendingTrue_ReportsQueued(gw, monkeypatch, plain):
    gw.answers[("POST", f"sessions/{SID}/hold")] = {"onHold": False, "pending": True}

    result = _run(["session", "hold", SID], None, monkeypatch)

    assert result.exit_code == 0, result.output
    assert f"Hold queued {SID}" in plain(result.stdout)


def test_sessionReport_NoParent_SaysSoAndEndsWithHelp(gw, monkeypatch):
    gw.roster = ([dict(ROSTER[1])], True, None, None)

    result = _run(["session", "report", "did it"], {"CC_SESSION_ID": PARENT}, monkeypatch)

    assert result.exit_code == 0, result.output
    assert "No parent" in result.stdout
    assert _help_block(result.stdout)[1] == ["cc-devthrottle session whoami", "cc-devthrottle session done"]
    assert not [c for c in gw.calls if c[0] == "POST"], "a report was sent with no parent to send it to"


def test_sessionRole_Cleared_OffersToSetOne(gw, monkeypatch):
    gw.answers[("POST", f"sessions/{SID}/role")] = {"sessionId": SID, "explicitRole": None}

    result = _run(["session", "role", SID, "none"], None, monkeypatch)

    assert result.exit_code == 0, result.output
    assert gw.calls[-1][2] == {"role": ""}
    assert _help_block(result.stdout)[1] == [f"cc-devthrottle session role {SID} <role>", "cc-devthrottle session list"]


def test_sessionRole_ClearedAsBlank_IsConfirmed(gw, monkeypatch):
    gw.answers[("POST", f"sessions/{SID}/role")] = {"sessionId": SID, "explicitRole": ""}

    result = _run(["session", "role", SID, "none"], None, monkeypatch)

    assert result.exit_code == 0, result.output
    assert "Role cleared" in result.stdout


def test_sessionRename_RequestWithSpaces_ConfirmedByTheTrimmedName(gw, monkeypatch, plain):
    # The Director trims the name it is given, so "  New name  " is confirmed by "New name".
    gw.answers[("PATCH", f"sessions/{SID}")] = {"sessionId": SID, "name": "New name"}

    result = _run(["session", "rename", SID, "  New name  "], None, monkeypatch)

    assert result.exit_code == 0, result.output
    assert gw.calls[-1][2] == {"name": "New name"}
    assert f'Renamed {SID} to "New name".' in plain(result.stdout)


def test_missionCreate_RequestWithSpaces_ConfirmedByTheTrimmedName(gw, monkeypatch, plain):
    result = _run(["mission", "create", "  AXI  "], None, monkeypatch)

    assert result.exit_code == 0, result.output
    assert "Created mission (AXI)." in plain(result.stdout)


def test_sessionSpawn_AnswerWithoutName_NamesTheSessionByIdNotByTheRequest(gw, monkeypatch, plain):
    # The Director composes the name, so the requested --name is not what the session is called.
    gw.answers[("POST", f"directors/{DIRECTOR}/sessions")] = {"sessionId": NEW}

    result = _run(
        ["session", "spawn", "/repos/x", "--controlled-by", "self", "--name", "asked-for", "--mission", "none"],
        AS_SID, monkeypatch,
    )

    assert result.exit_code == 0, result.output
    assert "asked-for" not in result.stdout
    assert f"({NEW[:8]})" in plain(result.stdout)


def test_missionDetach_NotAttached_SaysNothingChangedAndOffersAttach(gw, monkeypatch):
    gw.roster = ([dict(ROSTER[1])], True, None, None)
    gw.answers[("POST", f"sessions/{PARENT}/mission")] = {"session": {"sessionId": PARENT, "missionId": None}}

    result = _run(["mission", "detach", PARENT], None, monkeypatch)

    assert result.exit_code == 0, result.output
    assert "nothing changed" in result.stdout
    assert _help_block(result.stdout)[1] == [f"cc-devthrottle mission attach {PARENT} <mission-id>"]


def test_sessionSpawn_InheritedMission_OffersDetach(gw, monkeypatch):
    result = _run(["session", "spawn", "/repos/x", "--controlled-by", SID, "--name", "n"], None, monkeypatch)

    assert result.exit_code == 0, result.output
    assert gw.calls[-1][2]["missionId"] == MID
    assert _help_block(result.stdout)[1][-1] == f"cc-devthrottle mission detach {NEW}"


def test_output_NamesFromElsewhere_AreAsciiAndNotReadAsMarkup(gw, monkeypatch, plain):
    gw.answers[("PATCH", f"sessions/{SID}")] = {"sessionId": SID, "name": "[bold]caf\u00e9[/bold]"}

    result = _run(["session", "rename", SID, "[bold]caf\u00e9[/bold]"], None, monkeypatch)

    assert result.exit_code == 0, result.output
    assert result.stdout.isascii()
    assert "[bold]caf\\u00e9[/bold]" in plain(result.stdout)


# ===== --json is the Gateway's answer, unchanged =================================================

JSON_MUTATIONS = [
    (["session", "stop", SID, "--reason", "finished", "--json"], STOP_ANSWER),
    (["machine", "launch", "MAC", "--app", "Chrome", "--json"], LAUNCH_ANSWER),
    (["machine", "restart-request", "MAC", "--reason", "update it", "--json"], RESTART_ANSWER),
]


@pytest.mark.parametrize("argv,answer", JSON_MUTATIONS, ids=[" ".join(a[:2]) for a, _ in JSON_MUTATIONS])
def test_mutation_Json_IsExactlyTheGatewayAnswerWithNoHelp(gw, monkeypatch, argv, answer):
    result = _run(argv, None, monkeypatch)

    assert result.exit_code == 0, result.output
    assert result.stdout == json.dumps(answer, indent=2) + "\n"
    assert result.stderr == ""


# ===== errors: standard error, what failed, what to do next, non-zero ============================


def _raise(message, status=None):
    return session_ops.gateway.GatewayError(message, status=status)


def _set(**answers):
    def apply(fake):
        for key, value in answers.items():
            method, path = key.split(" ", 1)
            fake.answers[(method, path)] = value
    return apply


def _roster(value):
    def apply(fake):
        fake.roster = value
    return apply


def _mission_patch(value):
    def apply(fake):
        fake.mission_patch = value
    return apply


USAGE, FAILURE = 2, 1

ERRORS = [
    # (id, argv, env, arrange, exit code, text that must be in standard error)
    ("rename blank", ["session", "rename", SID, "  "], None, None, USAGE, ["blank", "session rename"]),
    ("rename no session", ["session", "rename", "new name"], None, None, USAGE,
     ["CC_SESSION_ID", "cc-devthrottle session list"]),
    ("rename no match", ["session", "rename", "nosuch", "x"], None, None, FAILURE,
     ["No session matches 'nosuch'", "cc-devthrottle session list"]),
    ("rename gateway", ["session", "rename", SID, "x"], None, _set(**{f"PATCH sessions/{SID}": _raise("boom")}),
     FAILURE, ["boom", SID, "cc-devthrottle session list"]),
    ("rename no answer", ["session", "rename", SID, "x"], None, _set(**{f"PATCH sessions/{SID}": None}),
     FAILURE, ["unknown", "cc-devthrottle session list"]),
    ("ambiguous", ["session", "interrupt", "9c41e7a2"], None,
     _roster(([ROSTER[0], dict(ROSTER[1], sessionId="9c41e7a2-0000-0000-0000-000000000000")], True, None, None)),
     FAILURE, ["ambiguous", SID, "9c41e7a2-0000-0000-0000-000000000000", "cc-devthrottle session interrupt"]),
    ("fleet unreadable", ["session", "interrupt", "worker"], None, _roster(_raise("no gateway")), FAILURE,
     ["no gateway", "cc-devthrottle setup status"]),
    ("prompt blank", ["session", "prompt", SID, " "], None, None, USAGE, ["blank", "session prompt"]),
    ("prompt gateway", ["session", "prompt", SID, "x"], None, _set(**{f"POST sessions/{SID}/prompt": _raise("nope")}),
     FAILURE, ["nope", "cc-devthrottle session list"]),
    ("interrupt gateway", ["session", "interrupt", SID], None,
     _set(**{f"POST sessions/{SID}/interrupt": _raise("nope")}), FAILURE, ["nope", "cc-devthrottle session list"]),
    ("hold release minutes", ["session", "hold", SID, "--release", "--minutes", "5"], None, None, USAGE,
     ["--minutes", "--release"]),
    ("hold zero minutes", ["session", "hold", SID, "--minutes", "0"], None, None, USAGE, ["at least 1"]),
    ("hold gateway", ["session", "hold", SID], None, _set(**{f"POST sessions/{SID}/hold": _raise("nope")}),
     FAILURE, ["nope", "cc-devthrottle session list"]),
    ("raise no reason", ["session", "raise"], AS_SID, None, USAGE, ["say what you need", "session raise"]),
    ("raise reason and clear", ["session", "raise", "x", "--clear"], AS_SID, None, USAGE, ["--clear"]),
    ("raise gateway", ["session", "raise", "x"], AS_SID,
     _set(**{f"POST sessions/{SID}/needs-manager": _raise("nope")}), FAILURE, ["nope", "cc-devthrottle session list"]),
    ("report blank", ["session", "report"], AS_SID, None, USAGE, ["say what you did", "session report"]),
    ("report incomplete roster", ["session", "report", "x"], AS_SID,
     _roster((ROSTER, False, "MACHINE_B is offline", None)), FAILURE,
     ["MACHINE_B is offline", "cc-devthrottle message send"]),
    ("report not on roster", ["session", "report", "x"], {"CC_SESSION_ID": NEW}, None, FAILURE,
     ["not in the fleet roster", "cc-devthrottle session whoami"]),
    ("report delivery", ["session", "report", "x"], AS_SID,
     _set(**{f"POST sessions/{PARENT}/message": _raise("nope")}), FAILURE, ["nope", PARENT, "cc-devthrottle session list"]),
    ("workers not a list", ["session", "workers", "--target", SID], None, _set(**{"GET sessions": {"x": 1}}),
     FAILURE, ["did not return a session list", "cc-devthrottle session list"]),
    ("compact-continue blank", ["session", "compact-continue", SID, " "], None, None, USAGE, ["blank"]),
    ("compact gateway", ["session", "compact", SID], None,
     _set(**{f"POST sessions/{SID}/compact-context": _raise("nope")}), FAILURE,
     ["nope", f"cc-devthrottle session buffer {SID}"]),
    ("buffer gateway", ["session", "buffer", SID], None,
     _set(**{f"GET sessions/{SID}/buffer": _raise("no session at [/tmp/x]")}), FAILURE,
     ["no session at [/tmp/x]", "cc-devthrottle session list"]),
    ("buffer empty", ["session", "buffer", SID], None, _set(**{f"GET sessions/{SID}/buffer": {}}), FAILURE,
     ["no terminal text", "cc-devthrottle setup status"]),
    ("role gateway", ["session", "role", SID, "Pilot"], None,
     _set(**{f"POST sessions/{SID}/role": _raise("unknown role Pilot")}), FAILURE,
     ["unknown role Pilot", "<Standalone|Manager|Worker|Architect|none>"]),
    ("whoami outside", ["session", "whoami"], None, None, FAILURE, ["CC_SESSION_ID", "cc-devthrottle session list"]),
    ("stop no reason", ["session", "stop", SID], None, None, USAGE, ["a reason is required", "--reason"]),
    ("stop refused", ["session", "stop", SID, "--reason", "x"], None,
     _set(**{f"POST sessions/{SID}/stop": _raise("not your session", status=403)}), FAILURE,
     ["Not stopped: not your session", "cc-devthrottle session list"]),
    ("stop unknown", ["session", "stop", SID, "--reason", "x"], None,
     _set(**{f"POST sessions/{SID}/stop": _raise("reply lost", status=504)}), FAILURE,
     ["Outcome unknown: reply lost", "cc-devthrottle session list"]),
    ("stop no headline", ["session", "stop", SID, "--reason", "x"], None,
     _set(**{f"POST sessions/{SID}/stop": {"verdict": "stopped"}}), FAILURE, ["No answer:", "cc-devthrottle session list"]),
    ("done gateway", ["session", "done", SID], None,
     _set(**{f"POST sessions/{SID}/request-deletion": _raise("nope")}), FAILURE, ["nope", "cc-devthrottle session list"]),
    ("undo with reason", ["session", "done", SID, "--undo", "--reason", "x"], None, None, USAGE,
     ["--undo and --reason", "session done --undo"]),
    ("undo gateway", ["session", "done", SID, "--undo"], None,
     _set(**{f"DELETE sessions/{SID}/request-deletion": _raise("nope")}), FAILURE, ["nope", "cc-devthrottle session list"]),
    ("spawn no owner", ["session", "spawn", "/r", "--name", "n"], AS_SID, None, USAGE,
     ["OWN the new session", "--controlled-by self", "--standalone"]),
    ("spawn standalone no why", ["session", "spawn", "/r", "--name", "n", "--standalone"], AS_SID, None, USAGE,
     ["Say why", "--why"]),
    ("spawn self outside", ["session", "spawn", "/r", "--name", "n", "--controlled-by", "self"], None, None, USAGE,
     ["CC_SESSION_ID", "--controlled-by <session-id>"]),
    ("spawn director ambiguous", ["session", "spawn", "/r", "--name", "n", "--director", "twin"], None,
     _set(**{"GET directors": [{"directorId": "d1", "displayName": "Twin", "machineName": "A"},
                               {"directorId": "d2", "displayName": "Twin", "machineName": "B"}]}), FAILURE,
     ["matches 2 Directors", "d1", "d2", "cc-devthrottle director list"]),
    ("spawn director unknown", ["session", "spawn", "/r", "--name", "n", "--director", "nope"], None, None, FAILURE,
     ["No Director matches 'nope'", "cc-devthrottle director list"]),
    ("spawn directors not a list", ["session", "spawn", "/r", "--name", "n", "--director", "x"], None,
     _set(**{"GET directors": None}), FAILURE, ["not a list", "cc-devthrottle director list"]),
    ("spawn no director id", ["session", "spawn", "/r", "--name", "n"], {"CC_DIRECTOR_ID": " "}, None, FAILURE,
     ["CC_DIRECTOR_ID", "cc-devthrottle director list"]),
    ("spawn gateway", ["session", "spawn", "/r", "--name", "n", "--controlled-by", "self"], AS_SID,
     _set(**{f"POST directors/{DIRECTOR}/sessions": _raise("director offline")}), FAILURE,
     ["director offline", "cc-devthrottle director list"]),
    ("spawn no id", ["session", "spawn", "/r", "--name", "n", "--controlled-by", "self"], AS_SID,
     _set(**{f"POST directors/{DIRECTOR}/sessions": {"name": "x"}}), FAILURE, ["unknown", "cc-devthrottle session list"]),
    ("send reason without everyone", ["message", "send", SID, "x", "--reason", "r"], None, None, USAGE,
     ["--reason", "--everyone"]),
    ("send grant without everyone", ["message", "send", "all", "x", "--grant", "g"], None, None, USAGE, ["--grant"]),
    ("send everyone to one", ["message", "send", SID, "x", "--everyone"], None, None, USAGE,
     ["--everyone", "message send all"]),
    ("send blank", ["message", "send", SID, " "], None, None, USAGE, ["blank"]),
    ("send not delivered", ["message", "send", SID, "x"], None,
     _set(**{f"POST sessions/{SID}/message": {"accepted": False, "error": "session is busy"}}), FAILURE,
     ["Not delivered: session is busy", "cc-devthrottle session list"]),
    ("send not delivered no reason", ["message", "send", SID, "x"], None,
     _set(**{f"POST sessions/{SID}/message": {"accepted": False}}), FAILURE,
     ["gave no reason", "cc-devthrottle session list"]),
    ("broadcast refused", ["message", "send", "all", "x"], AS_SID,
     _set(**{"POST fleet/broadcast": {"denied": True, "deniedReason": "no grant"}}), FAILURE,
     ["Not delivered: no grant", "cc-devthrottle session list"]),
    ("broadcast gateway", ["message", "send", "all", "x"], AS_SID,
     _set(**{"POST fleet/broadcast": _raise("nope")}), FAILURE, ["nope", "cc-devthrottle setup status"]),
    ("ask all", ["message", "ask", "all", "q"], None, None, USAGE, ["single session", "message send all"]),
    ("ask blank", ["message", "ask", SID, " "], None, None, USAGE, ["blank"]),
    ("ask timeout", ["message", "ask", SID, "q", "--timeout-ms", "0"], None, None, USAGE, ["--timeout-ms"]),
    ("ask gateway", ["message", "ask", SID, "q"], None, _set(**{f"POST sessions/{SID}/message": _raise("timed out")}),
     FAILURE, ["timed out", f"cc-devthrottle session buffer {SID}"]),
    ("ask no answer object", ["message", "ask", SID, "q"], None, _set(**{f"POST sessions/{SID}/message": None}),
     FAILURE, ["gave nothing for accepted", f"cc-devthrottle session buffer {SID}"]),
    ("mission create blank", ["mission", "create", " "], None, None, USAGE, ["blank", "mission create"]),
    ("mission rename blank", ["mission", "rename", MID, " "], None, None, USAGE, ["blank", "mission rename"]),
    ("mission blank query", ["mission", "complete", " "], None, None, USAGE, ["no mission was named", "mission list"]),
    ("mission no match", ["mission", "complete", "nothing-like-it"], None, None, FAILURE,
     ["No mission matches", "cc-devthrottle mission list --all"]),
    ("mission ambiguous", ["mission", "complete", "AXI"], None, None, FAILURE,
     ["ambiguous", MID, OTHER_MID, "full mission ids"]),
    ("mission patch no mission", ["mission", "reopen", MID], None, _mission_patch({}), FAILURE,
     ["did not include the mission", "cc-devthrottle mission list --all"]),
    ("attach no answer", ["mission", "attach", SID, MID], None, _set(**{f"POST sessions/{SID}/mission": None}),
     FAILURE, ["not attached", "mission attach <session-id>"]),
    ("detach gateway", ["mission", "detach", SID], None, _set(**{f"POST sessions/{SID}/mission": _raise("nope")}),
     FAILURE, ["nope", "cc-devthrottle session list"]),
    ("launch nothing", ["machine", "launch", "MAC"], None, None, USAGE, ["--app", "--path"]),
    ("launch both", ["machine", "launch", "MAC", "--app", "a", "--path", "/p"], None, None, USAGE, ["Drop one"]),
    ("launch error json", ["machine", "launch", "MAC", "--app", "a", "--json"], None,
     _set(**{"POST machines/MAC/launch": {"error": "no such application"}}), FAILURE,
     ["no such application", "cc-devthrottle machine apps MAC"]),
    ("launch gateway", ["machine", "launch", "MAC", "--app", "a"], None,
     _set(**{"POST machines/MAC/launch": _raise("offline")}), FAILURE, ["offline", "cc-devthrottle machine list"]),
    ("launch blank machine", ["machine", "launch", " ", "--app", "a"], None, None, USAGE, ["machine name is blank"]),
    ("restart blank reason", ["machine", "restart-request", "MAC", "--reason", " "], None, None, USAGE, ["blank"]),
    ("restart refused", ["machine", "restart-request", "MAC", "--reason", "r"], None,
     _set(**{"POST machines/MAC/director/restart-requests": {"code": "cannot", "error": "launcher too old"}}), FAILURE,
     ["launcher too old", "cc-devthrottle machine restart-capability MAC"]),
    ("restart refused json", ["machine", "restart-request", "MAC", "--reason", "r", "--json"], None,
     _set(**{"POST machines/MAC/director/restart-requests": {"error": "already pending"}}), FAILURE,
     ["already pending", "restart-capability MAC"]),
    ("restart no id", ["machine", "restart-request", "MAC", "--reason", "r"], None,
     _set(**{"POST machines/MAC/director/restart-requests": {}}), FAILURE, ["no request id", "restart-capability MAC"]),
    ("apps count", ["machine", "apps", "MAC", "--count", "0"], None, None, USAGE, ["--count"]),
    ("apps not an object", ["machine", "apps", "MAC"], None, _set(**{"GET machines/MAC/apps?q=&limit=100": None}),
     FAILURE, ["not an object", "cc-devthrottle setup status"]),
    ("apps no list", ["machine", "apps", "MAC"], None, _set(**{"GET machines/MAC/apps?q=&limit=100": {"machine": "MAC"}}),
     FAILURE, ["no list of applications", "--json"]),
    ("apps gateway error answer", ["machine", "apps", "MAC"], None,
     _set(**{"GET machines/MAC/apps?q=&limit=100": {"error": "machine is offline"}}), FAILURE,
     ["machine is offline", "cc-devthrottle machine list"]),
    ("files blank", ["machine", "files", "MAC", " "], None, None, USAGE, ["blank"]),
    ("files seconds", ["machine", "files", "MAC", "x", "--seconds", "0"], None, None, USAGE, ["--seconds"]),
    ("capability gateway", ["machine", "restart-capability", "MAC"], None,
     _set(**{"GET machines/MAC/restart-capability": _raise("offline")}), FAILURE, ["offline", "cc-devthrottle machine list"]),
    ("status blank id", ["machine", "restart-request-status", "MAC", " "], None, None, USAGE, ["request id is blank"]),
    ("status no state", ["machine", "restart-request-status", "MAC", "req-1"], None,
     _set(**{"GET machines/MAC/director/restart-requests/req-1": {"id": "req-1"}}), FAILURE, ["no state", "--json"]),
]


@pytest.mark.parametrize("label,argv,env,arrange,code,expected", ERRORS, ids=[e[0] for e in ERRORS])
def test_error_GoesToStandardErrorWithWhatFailedAndWhatToDo(gw, monkeypatch, label, argv, env, arrange, code, expected):
    if arrange:
        arrange(gw)

    result = _run(argv, env, monkeypatch)

    assert result.exit_code == code, result.output
    # Standard output carries answers only. An error that printed to it would be read as one.
    assert result.stdout == "", result.stdout
    assert result.stderr.isascii(), result.stderr
    assert "Traceback" not in result.output
    for text in expected:
        assert text in result.stderr, f"{text!r} not in:\n{result.stderr}"


LIST_FAILURES = [
    ("session list", ["session", "list"]),
    ("repo list", ["repo", "list"]),
    ("worktree list", ["worktree", "list"]),
    ("director list", ["director", "list"]),
    ("machine list", ["machine", "list"]),
    ("mission list", ["mission", "list"]),
    ("mission list --json", ["mission", "list", "--json"]),
]


@pytest.mark.parametrize("argv", [a for _, a in LIST_FAILURES], ids=[n for n, _ in LIST_FAILURES])
def test_listCommand_GatewayUnreachable_SaysWhatToRunNext(gw, monkeypatch, argv):
    down = _raise("Cannot reach the Gateway at http://gateway.invalid")
    gw.roster = down
    monkeypatch.setattr(session_ops.gateway, "get_json", lambda path, timeout=30: (_ for _ in ()).throw(down))

    def list_all(self, state=None):
        raise mission_ops.GatewayError("Cannot reach the Gateway at http://gateway.invalid")

    monkeypatch.setattr(mission_ops.MissionClient, "list_all", list_all)

    result = _run(argv, None, monkeypatch)

    assert result.exit_code == 1, result.output
    assert result.stdout == ""
    assert "Cannot reach the Gateway" in result.stderr
    assert result.stderr.rstrip("\n").endswith(axi_cli.CHECK_GATEWAY)


def test_error_UsageErrorsSendNothing(gw, monkeypatch):
    """A usage error is decided before the Gateway is asked anything that changes state."""
    for row in ERRORS:
        if row[4] != USAGE:
            continue
        gw.calls.clear()
        _run(row[1], row[2], monkeypatch)
        changes = [c for c in gw.calls if c[0] in ("POST", "PATCH", "DELETE")]
        assert changes == [], f"{row[0]} sent {changes}"
        if row[2]:
            for key in row[2]:
                monkeypatch.delenv(key, raising=False)


def test_missionAttach_PartialFailure_ReportsEachAndExitsOne(gw, monkeypatch, plain):
    gw.roster = ([ROSTER[1], dict(ROSTER[0], controllerSessionId=PARENT)], True, None, None)
    gw.answers[("POST", f"sessions/{PARENT}/mission")] = {"session": {"sessionId": PARENT, "missionId": MID}}
    gw.answers[("POST", f"sessions/{SID}/mission")] = _raise("director offline")

    result = _run(["mission", "attach", PARENT, MID, "--with-children"], None, monkeypatch)

    assert result.exit_code == 1
    assert f"Attached manager ({PARENT})" in " ".join(plain(result.stdout).split())
    assert f"Failed: worker one ({SID}) - director offline" in result.stderr
    assert f"1 of 2 session(s) were not attached to mission {MID}: {SID}" in result.stderr


def test_mission_GatewayUnreadable_IsAnErrorWithTheRemedy(gw, monkeypatch):
    def fail(self, state=None):
        raise mission_ops.GatewayError("Gateway not reachable")

    monkeypatch.setattr(mission_ops.MissionClient, "list_all", fail)

    result = _run(["mission", "complete", MID], None, monkeypatch)

    assert result.exit_code == 1
    assert result.stdout == ""
    assert "Gateway not reachable" in result.stderr
    assert "cc-devthrottle setup status" in result.stderr


def test_missionPatch_GatewayError_NamesTheMissionAndTheRemedy(gw, monkeypatch):
    def fail(self, mission_id, body):
        raise mission_ops.GatewayError("conflict")

    monkeypatch.setattr(mission_ops.MissionClient, "patch", fail)

    result = _run(["mission", "rename", MID, "new"], None, monkeypatch)

    assert result.exit_code == 1
    assert result.stdout == ""
    assert f"could not rename mission {MID}: conflict" in result.stderr
    assert "cc-devthrottle mission list --all" in result.stderr


def test_missionCreate_GatewayError_IsAnErrorWithTheRemedy(gw, monkeypatch):
    def fail(self, name):
        raise mission_ops.GatewayError("Gateway not reachable")

    monkeypatch.setattr(mission_ops.MissionClient, "create", fail)

    result = _run(["mission", "create", "AXI"], None, monkeypatch)

    assert result.exit_code == 1
    assert result.stdout == ""
    assert "no mission was created: Gateway not reachable" in result.stderr


def test_missionCreate_NoIdInAnswer_IsUnknownNotCreated(gw, monkeypatch):
    monkeypatch.setattr(mission_ops.MissionClient, "create", lambda self, name: {"missionName": name})

    result = _run(["mission", "create", "AXI"], None, monkeypatch)

    assert result.exit_code == 1
    assert "whether the mission was created is unknown" in result.stderr
    assert "cc-devthrottle mission list" in result.stderr


def test_spawn_RosterUnreadableForInheritance_WarnsOnStandardErrorAndStillOpens(gw, monkeypatch):
    gw.roster = _raise("roster down")

    result = _run(["session", "spawn", "/r", "--name", "n", "--controlled-by", SID], None, monkeypatch)

    assert result.exit_code == 0, result.output
    assert "Warning:" in result.stderr and "roster down" in result.stderr
    assert "cc-devthrottle mission attach" in result.stderr
    assert "Warning" not in result.stdout


# ===== machine requests keep what the caller typed as one value ==================================


def test_machineFiles_QueryAndMachineAreEncoded(gw, monkeypatch):
    path = "machines/MY%20PC/files?q=a%20b%26c%23d&limit=200&timeoutMilliseconds=20000"
    gw.answers[("GET", path)] = {"files": []}

    result = _run(["machine", "files", "MY PC", "a b&c#d"], None, monkeypatch)

    assert result.exit_code == 0, result.output
    assert gw.calls[-1][1] == path


CAPABILITY = {
    "verdict": "CanRestart", "reason": "It can restart.", "guardedRestart": "Available",
    "guardedRestartReason": "Guarded.", "launcherVersion": "2.1.0", "reach": "Connected",
    "declaration": "Declared", "restartSignal": "NotObservable", "servingRootKey": "b027",
    "servingRootIsInstanceHome": False, "declaredCommands": ["director/start", "director/restart"],
    "quietForSeconds": 25,
}


def test_restartCapability_FalseAndListsAreReadAsWhatTheyAre(gw, monkeypatch, plain):
    gw.answers[("GET", "machines/MAC/restart-capability")] = CAPABILITY

    result = _run(["machine", "restart-capability", "MAC"], None, monkeypatch)

    assert result.exit_code == 0, result.output
    out = plain(result.stdout)
    assert "YES - this is the fault" not in out
    row = next(line for line in out.splitlines() if "serving an instance home" in line)
    assert row.split("|")[2].strip() == "no"
    assert "director/start, director/restart" in out


def test_restartCapability_TrueIsTheFault(gw, monkeypatch, plain):
    gw.answers[("GET", "machines/MAC/restart-capability")] = dict(CAPABILITY, servingRootIsInstanceHome=True)

    result = _run(["machine", "restart-capability", "MAC"], None, monkeypatch)

    assert result.exit_code == 0, result.output
    assert "YES - this is the fault" in plain(result.stdout)


def test_restartCapability_DeclaredCommandsNotAList_IsAnError(gw, monkeypatch):
    gw.answers[("GET", "machines/MAC/restart-capability")] = dict(CAPABILITY, declaredCommands="director/start")

    result = _run(["machine", "restart-capability", "MAC"], None, monkeypatch)

    assert result.exit_code == 1
    assert "declaredCommands" in result.stderr and "--json" in result.stderr


# ===== every change is confirmed from the Gateway's answer ======================================
#
# One row per mutating command in session, message, mission and machine (repo, worktree and director
# have none), each with an answer that does not confirm the change: {} and, where the answer has more
# than one field to read, a partial or contrary one. Each must exit 1 with the reason on standard error
# and print nothing on standard output that reads as success - never the requested value echoed back.


def _mission_patch(answer):
    def apply(fake):
        fake.mission_patch = answer
    return apply


def _mission_create(answer):
    def apply(fake):
        fake.mission_create = answer
    return apply


UNCONFIRMED = [
    # (id, argv, env, arrange, text that must be in standard error)
    ("rename empty", ["session", "rename", SID, "new name"], None,
     _set(**{f"PATCH sessions/{SID}": {}}), "gave nothing for sessionId"),
    ("rename no name", ["session", "rename", SID, "new name"], None,
     _set(**{f"PATCH sessions/{SID}": {"sessionId": SID}}), "gave nothing for name"),
    ("rename other session", ["session", "rename", SID, "new name"], None,
     _set(**{f"PATCH sessions/{SID}": {"sessionId": PARENT, "name": "new name"}}), "for sessionId"),
    ("rename old name", ["session", "rename", SID, "New name"], None,
     _set(**{f"PATCH sessions/{SID}": {"sessionId": SID, "name": "Old name"}}),
     'gave the name "Old name", not the requested "New name"'),
    ("rename not trimmed", ["session", "rename", SID, "New name"], None,
     _set(**{f"PATCH sessions/{SID}": {"sessionId": SID, "name": "New name "}}),
     'gave the name "New name ", not the requested "New name"'),
    ("prompt empty", ["session", "prompt", SID, "hello"], None,
     _set(**{f"POST sessions/{SID}/prompt": {}}), "gave nothing for accepted"),
    ("prompt refused", ["session", "prompt", SID, "hello"], None,
     _set(**{f"POST sessions/{SID}/prompt": {"accepted": False, "error": "a menu is open"}}),
     "was not accepted: a menu is open"),
    ("interrupt empty", ["session", "interrupt", SID], None,
     _set(**{f"POST sessions/{SID}/interrupt": {}}), "gave nothing for accepted"),
    ("report empty", ["session", "report", "did it"], AS_SID,
     _set(**{f"POST sessions/{PARENT}/message": {}}), "did not accept"),
    ("raise empty", ["session", "raise", "which?"], AS_SID,
     _set(**{f"POST sessions/{SID}/needs-manager": {}}), "gave nothing for sessionId"),
    ("raise not raised", ["session", "raise", "which?"], AS_SID,
     _set(**{f"POST sessions/{SID}/needs-manager": {"sessionId": SID, "raised": False}}), "False for raised"),
    ("raise clear still raised", ["session", "raise", "--clear"], AS_SID,
     _set(**{f"POST sessions/{SID}/needs-manager": {"sessionId": SID, "raised": True}}), "True for raised"),
    ("hold empty", ["session", "hold", SID], None,
     _set(**{f"POST sessions/{SID}/hold": {}}), "gave nothing for onHold"),
    ("hold not held", ["session", "hold", SID], None,
     _set(**{f"POST sessions/{SID}/hold": {"onHold": False, "pending": False}}), "was not held"),
    ("release still held", ["session", "hold", SID, "--release"], None,
     _set(**{f"POST sessions/{SID}/hold": {"onHold": True, "pending": False}}), "was not released"),
    ("compact empty", ["session", "compact", SID], None,
     _set(**{f"POST sessions/{SID}/compact-context": {}}), "gave nothing for submitted"),
    ("compact not submitted", ["session", "compact-continue", SID], None,
     _set(**{f"POST sessions/{SID}/compact-context": {"submitted": False, "detail": "busy"}}),
     "False for submitted"),
    ("role empty", ["session", "role", SID, "Worker"], None,
     _set(**{f"POST sessions/{SID}/role": {}}), "gave nothing for sessionId"),
    ("role not set", ["session", "role", SID, "Worker"], None,
     _set(**{f"POST sessions/{SID}/role": {"sessionId": SID, "explicitRole": None}}),
     "gave the explicit role none, not Worker"),
    ("role set no role", ["session", "role", SID, "Worker"], None,
     _set(**{f"POST sessions/{SID}/role": {"sessionId": SID}}), "gave nothing for explicitRole"),
    ("role clear no role", ["session", "role", SID, "none"], None,
     _set(**{f"POST sessions/{SID}/role": {"sessionId": SID}}), "gave nothing for explicitRole"),
    ("role not cleared", ["session", "role", SID, "none"], None,
     _set(**{f"POST sessions/{SID}/role": {"sessionId": SID, "explicitRole": "Worker"}}),
     "gave the explicit role Worker, not none"),
    ("stop empty", ["session", "stop", SID, "--reason", "done"], None,
     _set(**{f"POST sessions/{SID}/stop": {}}), "returned nothing that says what happened"),
    ("done empty", ["session", "done", SID], None,
     _set(**{f"POST sessions/{SID}/request-deletion": {}}), "gave nothing for pendingDeletion"),
    ("done not flagged", ["session", "done", SID], None,
     _set(**{f"POST sessions/{SID}/request-deletion": {"pendingDeletion": False}}), "False for pendingDeletion"),
    ("undo empty", ["session", "done", SID, "--undo"], None,
     _set(**{f"DELETE sessions/{SID}/request-deletion": {}}), "gave nothing for pendingDeletion"),
    ("undo still flagged", ["session", "done", SID, "--undo"], None,
     _set(**{f"DELETE sessions/{SID}/request-deletion": {"pendingDeletion": True}}), "True for pendingDeletion"),
    ("spawn empty", ["session", "spawn", "/repos/x", "--controlled-by", "self", "--name", "n", "--mission", "none"],
     AS_SID, _set(**{f"POST directors/{DIRECTOR}/sessions": {}}), "did not return a session id"),
    ("send empty", ["message", "send", SID, "hello"], None,
     _set(**{f"POST sessions/{SID}/message": {}}), "did not accept"),
    ("send all empty", ["message", "send", "all", "hello"], AS_SID,
     _set(**{"POST fleet/broadcast": {}}), "did not accept"),
    ("ask empty", ["message", "ask", SID, "which?"], None,
     _set(**{f"POST sessions/{SID}/message": {}}), "gave nothing for accepted"),
    ("ask no wait", ["message", "ask", SID, "which?"], None,
     _set(**{f"POST sessions/{SID}/message": {"accepted": True, "output": "x"}}), "gave nothing for waitStatus"),
    ("mission create empty", ["mission", "create", "AXI"], None,
     _mission_create({}), "did not return a mission id"),
    ("mission create no name", ["mission", "create", "AXI"], None,
     _mission_create({"missionId": MID}), "gave nothing for missionName"),
    ("mission create other name", ["mission", "create", "AXI"], None,
     _mission_create({"missionId": MID, "missionName": "Other"}), "'Other' for missionName"),
    ("mission rename empty", ["mission", "rename", MID, "AXI renamed"], None,
     _mission_patch({}), "did not include the mission"),
    ("mission rename no name", ["mission", "rename", MID, "AXI renamed"], None,
     _mission_patch({"mission": {"missionId": MID}}), "gave nothing for missionName"),
    ("mission rename old name", ["mission", "rename", MID, "AXI renamed"], None,
     _mission_patch({"mission": dict(MISSION)}), "'AXI' for missionName"),
    ("mission rename other id", ["mission", "rename", MID, "AXI renamed"], None,
     _mission_patch({"mission": dict(OTHER_MISSION, missionName="AXI renamed")}), "for missionId"),
    ("mission complete no state", ["mission", "complete", MID], None,
     _mission_patch({"mission": {"missionId": MID, "missionName": "AXI"}}), "gave nothing for state"),
    ("mission remove wrong state", ["mission", "remove", MID], None,
     _mission_patch({"mission": dict(MISSION)}), "'active' for state"),
    ("mission reopen empty", ["mission", "reopen", MID], None,
     _mission_patch({}), "did not include the mission"),
    ("mission reopen no id", ["mission", "reopen", MID], None,
     _mission_patch({"mission": {"missionName": "AXI", "state": "active"}}), "gave nothing for missionId"),
    ("mission attach empty", ["mission", "attach", SID, MID], None,
     _set(**{f"POST sessions/{SID}/mission": {}}), "did not include the session"),
    ("mission attach no mission", ["mission", "attach", SID, MID], None,
     _set(**{f"POST sessions/{SID}/mission": {"session": {"sessionId": SID}}}),
     "did not say which mission the session is now on"),
    ("mission attach null mission", ["mission", "attach", SID, MID], None,
     _set(**{f"POST sessions/{SID}/mission": {"session": {"sessionId": SID, "missionId": None}}}),
     "gave the session mission None"),
    ("mission attach other session", ["mission", "attach", SID, MID], None,
     _set(**{f"POST sessions/{SID}/mission": {"session": {"sessionId": PARENT, "missionId": MID}}}),
     f"named session '{PARENT}'"),
    ("mission detach empty", ["mission", "detach", SID], None,
     _set(**{f"POST sessions/{SID}/mission": {}}), "did not include the session"),
    ("mission detach no mission field", ["mission", "detach", SID], None,
     _set(**{f"POST sessions/{SID}/mission": {"session": {"sessionId": SID}}}),
     "did not say which mission the session is now on"),
    ("mission detach still attached", ["mission", "detach", SID], None,
     _set(**{f"POST sessions/{SID}/mission": {"session": {"sessionId": SID, "missionId": MID}}}),
     "still gives the session mission"),
    ("launch empty", ["machine", "launch", "MAC", "--app", "Chrome"], None,
     _set(**{"POST machines/MAC/launch": {}}), "gave nothing for relayStatus"),
    ("launch no status", ["machine", "launch", "MAC", "--app", "Chrome", "--json"], None,
     _set(**{"POST machines/MAC/launch": {"machine": "MAC", "verb": "launch", "payload": ""}}),
     "gave nothing for relayStatus"),
    ("restart-request empty", ["machine", "restart-request", "MAC", "--reason", "update"], None,
     _set(**{"POST machines/MAC/director/restart-requests": {}}), "carried no request id"),
]


@pytest.mark.parametrize("label,argv,env,arrange,expected", UNCONFIRMED, ids=[u[0] for u in UNCONFIRMED])
def test_mutation_AnswerDoesNotConfirmTheChange_ExitsOneWithoutClaimingIt(
    gw, monkeypatch, plain, label, argv, env, arrange, expected
):
    arrange(gw)

    result = _run(argv, env, monkeypatch)

    assert result.exit_code == 1, result.output
    stderr = " ".join(plain(result.stderr).split())
    assert expected in stderr, stderr
    assert "help[" in result.stderr
    # Nothing on standard output reads as a completed change: no help[] block, and no success word.
    assert "help[" not in result.stdout, result.stdout
    for word in ("Renamed", "Sent", "Interrupted", "Delivered", "Hand up", "Hand down", "Held", "Released",
                 "Compact", "Role set", "Role cleared", "Marked", "Cleared", "Opened", "Created",
                 "Completed", "Removed", "Reopened", "Attached", "Detached", "Started", "REQUESTED"):
        assert word not in result.stdout, result.stdout


def test_mutation_EveryChangingCommandHasAnUnconfirmedAnswerTest():
    """Every command in MUTATIONS (the full list of changing commands in scope) has a row above."""
    firsts = {u[1][0] + " " + u[1][1] for u in UNCONFIRMED}
    for label, argv, _, _ in MUTATIONS:
        command = f"{argv[0]} {argv[1]}"
        assert command in firsts, f"{label} has no unconfirmed-answer test"


# ===== selftest ==================================================================================


def test_selftest_NotWindows_RefusesBeforeSpawningAnything(gw, monkeypatch):
    monkeypatch.setattr(session_ops, "_runs_on_windows", lambda: False)

    result = _run(["selftest"], AS_SID, monkeypatch)

    assert result.exit_code == 1
    assert result.stdout == ""
    assert "runs only on Windows" in result.stderr
    assert "cc-devthrottle session list" in result.stderr
    assert gw.calls == []


RESPONDER, RECIPIENT = NEW, "0e0e0e0e-aaaa-bbbb-cccc-dddddddddddd"


def _selftest_gateway(gw, monkeypatch, deletion_answer):
    """Both throwaways spawn, both messages land, and the roster KEEPS listing both after they are
    flagged - as the real Director does for its 30-second grace period and until its next reaper sweep."""
    monkeypatch.setattr(session_ops, "_runs_on_windows", lambda: True)
    monkeypatch.setattr(session_ops.time, "sleep", lambda seconds: None)
    spawned = iter([{"sessionId": RESPONDER}, {"sessionId": RECIPIENT}])
    gw.roster = ([{"sessionId": RESPONDER}, {"sessionId": RECIPIENT}], True, None, None)
    gw.answers[("POST", f"directors/{DIRECTOR}/sessions")] = lambda body: next(spawned)
    gw.answers[("POST", f"sessions/{RECIPIENT}/message")] = {"accepted": True}
    gw.answers[("POST", f"sessions/{RESPONDER}/message")] = {"output": "FLEETPONG>"}
    gw.answers[("POST", f"sessions/{RECIPIENT}/request-deletion")] = deletion_answer
    gw.answers[("POST", f"sessions/{RESPONDER}/request-deletion")] = deletion_answer


def test_selftest_Windows_NamesAndOwnsItsThrowawaysAtBirthAndEndsWithHelp(gw, monkeypatch, plain):
    _selftest_gateway(gw, monkeypatch, {"pendingDeletion": True})

    result = _run(["selftest"], AS_SID, monkeypatch)

    assert result.exit_code == 0, result.output
    spawns = [c[2] for c in gw.calls if c[1] == f"directors/{DIRECTOR}/sessions"]
    assert [s["name"] for s in spawns] == ["selftest-responder", "selftest-recipient"]
    assert all(s["controllerSessionId"] == SID for s in spawns)
    assert not [c for c in gw.calls if c[0] == "PATCH"], "the throwaways were renamed after birth"
    assert _help_block(result.stdout)[1] == [
        "cc-devthrottle session list", 'cc-devthrottle message send <session-id> "<message>"'
    ]


def test_selftest_Windows_FlaggedSessionsStillListed_PassesOnTheAcceptedFlags(gw, monkeypatch, plain):
    # The inspection's reproduction: every step succeeds and the roster still lists both flagged
    # sessions, because the Director keeps them through its grace period. That is not a leak.
    _selftest_gateway(gw, monkeypatch, {"pendingDeletion": True})

    result = _run(["selftest"], AS_SID, monkeypatch)

    assert result.exit_code == 0, result.output
    out = " ".join(plain(result.stdout).split())
    assert "PASS throwaway sessions flagged for deletion - 2/2 accepted;" in out
    # Removal is stated with its condition, never claimed and never given a deadline: the Director
    # skips a flagged session on every sweep while it is working.
    assert "after its 30-second grace period, on a later reaper sweep once they are no longer working" in out
    assert "within" not in out and "next reaper sweep" not in out
    assert "cleaned up" not in out and "FAIL" not in out
    assert "5/5 checks passed" in out


@pytest.mark.parametrize("answer", [{}, {"pendingDeletion": False}, None])
def test_selftest_Windows_DeletionNotConfirmed_IsAFailure(gw, monkeypatch, plain, answer):
    _selftest_gateway(gw, monkeypatch, answer)

    result = _run(["selftest"], AS_SID, monkeypatch)

    assert result.exit_code == 1
    out = " ".join(plain(result.stdout).split())
    assert "FAIL throwaway sessions flagged for deletion - 0/2 accepted" in out
    assert "did not say pendingDeletion: true" in out
    assert RESPONDER in result.stderr and RECIPIENT in result.stderr


def test_selftest_Windows_AFailedCheckIsAnErrorNamingTheLeftovers(gw, monkeypatch):
    monkeypatch.setattr(session_ops, "_runs_on_windows", lambda: True)
    monkeypatch.setattr(session_ops.time, "sleep", lambda seconds: None)
    gw.answers[("POST", f"directors/{DIRECTOR}/sessions")] = {"sessionId": NEW}
    gw.answers[("POST", f"sessions/{NEW}/message")] = _raise("unreachable")
    gw.answers[("POST", f"sessions/{NEW}/request-deletion")] = _raise("cannot flag")
    gw.roster = ([{"sessionId": NEW}], True, None, None)

    result = _run(["selftest"], AS_SID, monkeypatch)

    assert result.exit_code == 1
    assert "FAIL" in result.stdout and "cannot flag" in result.stdout
    assert "FAIL - fleet messaging self-test" in result.stderr
    assert NEW in result.stderr and "cc-devthrottle session done" in result.stderr


# ===== the helper itself =========================================================================


@pytest.mark.parametrize("next_commands", [[], [" "]])
def test_fail_WithoutANextStep_IsACallerDefect(next_commands):
    with pytest.raises(ValueError):
        axi_cli.fail("something broke", next_commands)


def test_fail_WithoutWhatFailed_IsACallerDefect():
    with pytest.raises(ValueError):
        axi_cli.fail(" ", ["cc-devthrottle session list"])


def test_fail_WritesOneAsciiLineAndTheNextStepsToStandardErrorAndExits(capsys):
    with pytest.raises(typer.Exit) as ex:
        axi_cli.fail("caf\u00e9 is gone\nsecond line", ["cc-devthrottle session list"])

    assert ex.value.exit_code == 1
    captured = capsys.readouterr()
    assert captured.out == ""
    assert captured.err == "Error: caf\\u00e9 is gone\\nsecond line\nhelp[1]:\n  cc-devthrottle session list\n"


def test_fail_Label_ReplacesErrorWord(capsys):
    with pytest.raises(typer.Exit):
        axi_cli.fail("not your session", ["cc-devthrottle session list"], label="Not stopped:")

    assert capsys.readouterr().err.startswith("Not stopped: not your session\n")


def test_warn_WritesOneAsciiLineToStandardErrorAndDoesNotExit(capsys):
    axi_cli.warn("no mission\nfor caf\u00e9")

    captured = capsys.readouterr()
    assert captured.out == ""
    assert captured.err == "Warning: no mission\\nfor caf\\u00e9\n"


def test_shown_EscapesMarkupAndLeavesPlainTextAlone():
    assert axi_cli.shown(r"C:\repos\x") == r"C:\repos\x"
    assert axi_cli.shown("tab\there") == "tab\\there"
    assert axi_cli.shown("[fix] tests") == "\\[fix] tests"


# ===== short --help everywhere ===================================================================

#: The top-level names this step (6b) covers: groups and root-level commands.
SHORT_HELP_6B = {"session", "message", "mission", "repo", "worktree", "director", "machine", "selftest"}

#: Top-level names step 6c covers (tests/test_axi_step_6c_help_and_errors.py checks them its own way too).
SHORT_HELP_6C = {
    "actions", "settings", "schedule", "workflow", "skill", "setup", "email", "diag", "autostart", "browser",
}

#: Top-level names added to main after steps 6b and 6c were written. Their help is checked here too,
#: so a group that lands later is held to the same one-line summary.
SHORT_HELP_LATER = {"fleet-manager"}

#: Top-level names whose help is not yet checked. Empty now that steps 6b and 6c are together, and
#: nothing may be added here: a new command or group belongs in one of the sets above, and
#: test_shortHelp_EveryTopLevelNameIsAccountedFor enforces that.
NOT_YET_COVERED = set()

MAX_SUMMARY = 80


def _walk(command, path):
    yield path, command
    for name, sub in (getattr(command, "commands", None) or {}).items():
        yield from _walk(sub, path + [name])


def _tree():
    return list(_walk(typer.main.get_command(app), []))


def _summary_problem(command):
    text = (command.help or "").strip()
    if not text:
        return "has no help"
    first = text.split("\n\n")[0]
    if "\n" in first:
        return f"summary runs over more than one line: {first!r}"
    if len(first) > MAX_SUMMARY:
        return f"summary is {len(first)} characters, over {MAX_SUMMARY}: {first!r}"
    if not first.endswith("."):
        return f"summary does not end with a full stop: {first!r}"
    if not all(0x20 <= ord(ch) <= 0x7E for ch in text.replace("\n", " ")):
        return "help is not printable ASCII"
    return None


def test_shortHelp_EveryTopLevelNameIsAccountedFor():
    top = {path[0] for path, _ in _tree() if len(path) == 1}
    accounted = SHORT_HELP_6B | SHORT_HELP_6C | SHORT_HELP_LATER | NOT_YET_COVERED
    assert top == accounted, f"unaccounted: {sorted(top - accounted)}, stale: {sorted(accounted - top)}"
    assert not (SHORT_HELP_6B | SHORT_HELP_6C | SHORT_HELP_LATER) & NOT_YET_COVERED


def test_shortHelp_EveryCoveredCommandAndGroupHasAOneLineSummary():
    problems = []
    for path, command in _tree():
        if path and path[0] in NOT_YET_COVERED:
            continue
        problem = _summary_problem(command)
        if problem:
            problems.append(f"cc-devthrottle {' '.join(path)}: {problem}")
    assert not problems, "\n".join(problems)


def test_shortHelp_TheCheckCanFail():
    class Long:
        help = "This summary is far too long to fit on one line of a command list in a terminal window."

    class TwoLines:
        help = "One line\nand another."

    class Blank:
        help = None

    assert _summary_problem(Long) and _summary_problem(TwoLines) and _summary_problem(Blank)


@pytest.mark.parametrize("argv", [path for path, _ in _tree() if not path or path[0] in SHORT_HELP_6B],
                         ids=lambda p: " ".join(p) or "root")
def test_shortHelp_HelpRunsAndIsAscii(argv):
    result = runner.invoke(app, argv + ["--help"])

    assert result.exit_code == 0, result.output
    assert result.stdout.isascii()


@pytest.mark.parametrize("answer", [{"explicitRole": None}, {"explicitRole": ""}, {"ExplicitRole": None}])
def test_confirmed_IsCleared_FieldPresentAndEmpty_IsConfirmed(answer):
    assert axi_cli.confirmed(answer, ("explicitRole", "ExplicitRole"), "x", ["cc-devthrottle session list"],
                             accept=axi_cli.is_cleared) in (None, "")


@pytest.mark.parametrize("answer,shown", [({}, "nothing"), ({"sessionId": "s"}, "nothing"),
                                          ({"explicitRole": "Worker"}, "'Worker'")])
def test_confirmed_IsCleared_FieldAbsentOrSet_ExitsOne(capsys, answer, shown):
    # Absent is a missing answer, not a cleared value.
    with pytest.raises(typer.Exit) as ex:
        axi_cli.confirmed(answer, ("explicitRole", "ExplicitRole"), "clearing it", ["cc-devthrottle session list"],
                          accept=axi_cli.is_cleared)

    assert ex.value.exit_code == 1
    assert f"gave {shown} for explicitRole" in capsys.readouterr().err


def test_confirmed_NullWithoutAccept_ExitsOneSayingNull(capsys):
    with pytest.raises(typer.Exit):
        axi_cli.confirmed({"name": None}, ("name", "Name"), "the rename", ["cc-devthrottle session list"])

    assert "gave null for name" in capsys.readouterr().err
