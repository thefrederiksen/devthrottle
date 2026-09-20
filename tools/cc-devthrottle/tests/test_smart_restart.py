"""`cc-devthrottle director smart-restart`, `smart-restart-status` and `restart-history`.

The mission "Smart Director Restart", section 5.3 item 12: the SECOND door onto the Director's own
File, Smart Restart, so that one broken window can never leave a Director impossible to empty.

The Gateway is stubbed. What these prove is what the command SENDS, that every sentence it prints is
the Director's own and not one written here, and that each of the three ways a watch can end - the
restart taken, a restart that did not happen, and the Director going silent - is told apart from the
others by what the command says and by the code it exits with.
"""

import json
import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared import gateway as gateway_module  # noqa: E402
from src import smart_restart_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

DIRECTOR = "9d0c7a55-0000-4000-8000-000000000001"
OTHER_DIRECTOR = "9d0c7a55-0000-4000-8000-000000000002"

ACCEPTED = {
    "taken": True,
    "directorId": DIRECTOR,
    "minutes": 10,
    "detail": "The smart restart of DevThrottle_1 has started. Every session is asked to hand over.",
}


def _progress(running, phase="Collecting", outcome=None, detail="", sessions=(), workspace=None):
    return {
        "running": running,
        "started": True,
        "phase": phase,
        "phaseLabel": f"LABEL FOR {phase}",
        "countLabel": "1 of 2 shut down",
        "total": 2,
        "gone": 1,
        "startedUtc": "2026-09-20T08:00:00Z",
        "interruptAtUtc": "2026-09-20T08:06:40Z",
        "limitUtc": "2026-09-20T08:10:00Z",
        "workspaceId": workspace,
        "note": None,
        "outcome": outcome,
        "detail": detail,
        "sessions": list(sessions),
    }


def _row(sid, name, label, detail=None):
    return {
        "sessionId": sid, "name": name, "mission": None, "role": None, "ownerSessionId": None,
        "state": "Asked", "stateLabel": label, "detail": detail,
    }


class FakeGateway:
    """Serves the Director list, the progress readings in order, and records every POST."""

    def __init__(self, readings, accepted=ACCEPTED, history=None, directors=None):
        self.readings = list(readings)
        self.accepted = accepted
        self.history = history
        self.posts = []
        self.gets = []
        self.directors = directors if directors is not None else [
            {"directorId": DIRECTOR, "displayName": "DevThrottle_1", "machineName": "SOREN_NORTH"},
            {"directorId": OTHER_DIRECTOR, "displayName": "DevThrottle_2", "machineName": "SOREN_NORTH"},
        ]

    def get_json(self, path, timeout=30):
        self.gets.append(path)
        if path == "directors":
            return self.directors
        if path.endswith("/restart-history"):
            if isinstance(self.history, Exception):
                raise self.history
            return self.history
        if path.endswith("/smart-restart"):
            if not self.readings:
                raise AssertionError("the command asked for the progress more times than the test served")
            reading = self.readings.pop(0)
            if isinstance(reading, Exception):
                raise reading
            return reading
        raise AssertionError(f"unexpected GET: {path}")

    def post_json(self, path, body=None, timeout=30):
        self.posts.append((path, body))
        if isinstance(self.accepted, Exception):
            raise self.accepted
        return self.accepted


@pytest.fixture
def fake(monkeypatch):
    def install(readings=(), accepted=ACCEPTED, history=None, directors=None):
        gw = FakeGateway(readings, accepted, history, directors)
        monkeypatch.setattr(smart_restart_ops.gateway, "get_json", gw.get_json)
        monkeypatch.setattr(smart_restart_ops.gateway, "post_json", gw.post_json)
        monkeypatch.setattr(smart_restart_ops, "_sleep", lambda s: None)
        return gw
    return install


def _run(*argv):
    return runner.invoke(app, list(argv))


# ===== starting one =====

def test_smartRestart_SendsTheMinutesAndTheReasonToThatDirectorAlone(fake):
    gw = fake([_progress(False, "Finished", "RestartAccepted", "The launcher accepted the restart.")])

    result = _run("director", "smart-restart", "--director", DIRECTOR, "--minutes", "30", "--reason", "update it")

    assert result.exit_code == 0, result.output
    assert gw.posts == [(f"directors/{DIRECTOR}/smart-restart", {"minutes": 30, "reason": "update it"})]


def test_smartRestart_NamesTheDirectorByItsName_ResolvesItToTheOneId(fake):
    gw = fake([_progress(False, "Finished", "RestartAccepted", "done")])

    result = _run("director", "smart-restart", "--director", "DevThrottle_2")

    assert result.exit_code == 0, result.output
    assert gw.posts[0][0] == f"directors/{OTHER_DIRECTOR}/smart-restart"


def test_smartRestart_TheDirectorsAcceptanceSentenceIsPrintedAsItIs(fake, plain):
    fake([_progress(False, "Finished", "RestartAccepted", "done")])

    result = _run("director", "smart-restart", "--director", DIRECTOR)

    assert ACCEPTED["detail"] in plain(result.stdout)


def test_smartRestart_ARefusal_IsTheGatewaysOwnWordsAndExitsOne(fake):
    refusal = gateway_module.GatewayError(
        "An agent may not empty and restart a Director. Ask for one instead - "
        "cc-devthrottle machine restart-request <machine> --reason \"<why>\".",
        status=403,
    )
    gw = fake(accepted=refusal)

    result = _run("director", "smart-restart", "--director", DIRECTOR)

    assert result.exit_code == 1
    assert "An agent may not empty and restart a Director" in result.stderr
    assert "machine restart-request" in result.stderr
    # Refused means refused: nothing was watched and nothing was read back.
    assert not [g for g in gw.gets if g.endswith("/smart-restart")]


def test_smartRestart_AMinutesTheEngineRefuses_IsTheDirectorsSentenceNotASecondListHere(fake):
    """The allowed times live in the engine and nowhere else, so the refusal comes back from it."""
    refusal = gateway_module.GatewayError(
        "The time allowed for a smart shutdown must be one of 5, 10, 15, 30, 60 minutes; "
        "7 minutes was asked for. Nothing has been touched.",
        status=400,
    )
    gw = fake(accepted=refusal)

    result = _run("director", "smart-restart", "--director", DIRECTOR, "--minutes", "7")

    assert result.exit_code == 1
    assert "must be one of 5, 10, 15, 30, 60 minutes" in result.stderr
    assert gw.posts[0][1] == {"minutes": 7}


def test_smartRestart_NoWatch_ExitsThreeSayingNothingHereKnowsHowItEnded(fake, plain):
    gw = fake()

    result = _run("director", "smart-restart", "--director", DIRECTOR, "--no-watch")

    assert result.exit_code == smart_restart_ops.EXIT_ACCEPTED_NOT_WAITED
    assert "accepted, not waited" in plain(result.stdout)
    assert not [g for g in gw.gets if g.endswith("/smart-restart")]


def test_smartRestart_Json_IsExactlyTheGatewaysAnswerAndDoesNotWatch(fake):
    gw = fake()

    result = _run("director", "smart-restart", "--director", DIRECTOR, "--json")

    assert result.exit_code == 0, result.output
    assert json.loads(result.stdout) == ACCEPTED
    assert not [g for g in gw.gets if g.endswith("/smart-restart")]


# ===== watching it =====

def test_smartRestart_PrintsEachSessionsStateAsItChangesAndNeverRepeatsOne(fake, plain):
    fake([
        _progress(True, "Asking", sessions=[_row("s1", "A lead", "Asked to hand over"),
                                            _row("s2", "A worker", "Asked to hand over")]),
        _progress(True, "Collecting", sessions=[_row("s1", "A lead", "Handed over"),
                                                _row("s2", "A worker", "Asked to hand over")]),
        _progress(False, "Finished", "RestartAccepted", "The launcher accepted the restart.",
                  sessions=[_row("s1", "A lead", "Shut down"), _row("s2", "A worker", "Shut down")]),
    ])

    result = _run("director", "smart-restart", "--director", DIRECTOR)
    text = plain(result.stdout)

    assert result.exit_code == 0, result.output
    assert "LABEL FOR Asking" in text
    assert "LABEL FOR Collecting" in text
    # Every state the run passed through is printed once, and the row that did not change is not
    # printed again beside the one that did.
    assert text.count("A lead: Asked to hand over") == 1
    assert text.count("A worker: Asked to hand over") == 1
    assert text.count("A lead: Handed over") == 1
    assert text.count("A lead: Shut down") == 1


def test_smartRestart_ARowsDetail_IsPrintedUnderIt(fake, plain):
    fake([
        _progress(True, "Asking", sessions=[_row("s1", "A wedged seat", "The request did not reach it",
                                                 detail="the session is not taking input")]),
        _progress(False, "Finished", "RestartAccepted", "done",
                  sessions=[_row("s1", "A wedged seat", "Shut down")]),
    ])

    result = _run("director", "smart-restart", "--director", DIRECTOR)

    assert "the session is not taking input" in plain(result.stdout)


def test_smartRestart_RestartAccepted_PrintsTheEnginesSentenceAndTheRecordAndExitsZero(fake, plain):
    fake([_progress(False, "Finished", "RestartAccepted",
                    "Every session is shut down and the Director is empty. The launcher accepted the restart.",
                    workspace="restart-2026-09-20-0800")])

    result = _run("director", "smart-restart", "--director", DIRECTOR)
    text = plain(result.stdout)

    assert result.exit_code == 0, result.output
    assert "The launcher accepted the restart." in text
    assert "restart-2026-09-20-0800" in text


def test_smartRestart_ARestartThatDidNotHappen_ExitsOneWithTheEnginesReasonAndTheRecord(fake):
    fake([_progress(False, "Finished", "RestartRefused",
                    "The Director was emptied, but the launcher did not accept the restart. "
                    "Every session is shut down and recorded; it is offered when the Director is next started.",
                    workspace="restart-2026-09-20-0800")])

    result = _run("director", "smart-restart", "--director", DIRECTOR)

    assert result.exit_code == 1
    assert "RestartRefused" in result.stderr
    assert "the launcher did not accept the restart" in result.stderr
    assert "restart-2026-09-20-0800" in result.stderr


def test_smartRestart_TheDirectorGoesSilent_SaysSoWithTheLastPhaseAndExitsThree(fake, plain):
    fake([
        _progress(True, "Restarting", sessions=[_row("s1", "A lead", "Shut down")]),
        gateway_module.GatewayError("The Director is not connected right now.", status=502),
    ])

    result = _run("director", "smart-restart", "--director", DIRECTOR)
    text = plain(result.stdout)

    assert result.exit_code == smart_restart_ops.EXIT_ACCEPTED_NOT_WAITED
    assert "stopped answering" in text
    # It names the last phase it DID see, and says the silence is also what a restart looks like -
    # it never claims the restart happened, and never claims it failed.
    assert "LABEL FOR Restarting" in text
    assert "not known from this command" in text
    assert "cc-devthrottle director list" in text


def test_smartRestart_TheWatchOutlivesTheLongestRun_StopsWatchingAndSaysSo(fake, monkeypatch, plain):
    fake([_progress(True, "Collecting"), _progress(True, "Collecting")])
    clock = iter([0.0, 0.0, smart_restart_ops.WATCH_PATIENCE_SECONDS + 1])
    monkeypatch.setattr(smart_restart_ops, "_monotonic", lambda: next(clock))

    result = _run("director", "smart-restart", "--director", DIRECTOR)

    assert result.exit_code == smart_restart_ops.EXIT_ACCEPTED_NOT_WAITED
    assert "accepted, not waited" in plain(result.stdout)
    assert "It is still going" in plain(result.stdout)


# ===== where it stands, asked once =====

def test_smartRestartStatus_ADirectorThatHasStartedNone_PrintsTheDirectorsOwnSentence(fake, plain):
    fake([{"running": False, "started": False, "sessions": [],
           "detail": "No smart shutdown has been started on DevThrottle_1 since it came up."}])

    result = _run("director", "smart-restart-status", "--director", DIRECTOR)

    assert result.exit_code == 0, result.output
    assert "No smart shutdown has been started on DevThrottle_1" in plain(result.stdout)


def test_smartRestartStatus_ChangesNothing(fake):
    gw = fake([_progress(True, "Collecting")])

    result = _run("director", "smart-restart-status", "--director", DIRECTOR)

    assert result.exit_code == 0, result.output
    assert gw.posts == []


# ===== the history =====

HISTORY = {
    "refused": False,
    "message": "This Director has 2 restart records, newest first.",
    "entries": [
        {
            "workspaceId": "restart-2026-09-20-0800",
            "atUtc": "2026-09-20T08:00:00Z",
            "whenLabel": "Today at 08:00",
            "kindLabel": "A smart shutdown",
            "reasonLabel": "Reason: update to 2.9.0",
            "outcomeLabel": "2 sessions are waiting to come back",
            "seatsLabel": "2 sessions are waiting to be brought back",
            "seats": [
                {"sessionId": "s1", "name": "A lead", "mission": None, "role": None,
                 "outcome": "Handed over and waiting to come back"},
                {"sessionId": "s2", "name": "A worker", "mission": None, "role": None,
                 "outcome": "Ended when time was up"},
            ],
        },
        {
            "workspaceId": "restart-2026-09-19-2150",
            "atUtc": "2026-09-19T21:50:00Z",
            "whenLabel": "Yesterday at 21:50",
            "kindLabel": "A shutdown that ignored all sessions",
            "reasonLabel": "No reason was given",
            "outcomeLabel": "Everything came back",
            "seatsLabel": None,
            "seats": [],
        },
    ],
}


def test_restartHistory_PrintsEveryLabelTheDirectorComputed(fake, plain):
    fake(history=HISTORY)

    result = _run("director", "restart-history", "--director", DIRECTOR)
    text = plain(result.stdout)

    assert result.exit_code == 0, result.output
    for label in ("This Director has 2 restart records, newest first.",
                  "Today at 08:00", "A smart shutdown", "Reason: update to 2.9.0",
                  "2 sessions are waiting to come back", "2 sessions are waiting to be brought back",
                  "A lead: Handed over and waiting to come back",
                  "A worker: Ended when time was up",
                  "Yesterday at 21:50", "A shutdown that ignored all sessions", "No reason was given"):
        assert label in text, label


def test_restartHistory_NewestFirst_AndTheCountLineSaysHowMany(fake, plain):
    fake(history=HISTORY)

    result = _run("director", "restart-history", "--director", DIRECTOR)
    text = plain(result.stdout)

    assert "count: 2 of 2 total" in text
    assert text.index("restart-2026-09-20-0800") < text.index("restart-2026-09-19-2150")


def test_restartHistory_Count_NarrowsToTheNewestAndSaysHowManyThereAre(fake, plain):
    fake(history=HISTORY)

    result = _run("director", "restart-history", "--director", DIRECTOR, "--count", "1")
    text = plain(result.stdout)

    assert "count: 1 of 2 total" in text
    assert "restart-2026-09-20-0800" in text
    assert "restart-2026-09-19-2150" not in text


def test_restartHistory_AnEmptyHistory_IsStillDefinitive(fake, plain):
    fake(history={"refused": False, "message": "This Director has no restart records.", "entries": []})

    result = _run("director", "restart-history", "--director", DIRECTOR)
    text = plain(result.stdout)

    assert result.exit_code == 0, result.output
    assert "count: 0" in text
    assert "This Director has no restart records." in text


def test_restartHistory_ARefusedHistory_IsAnErrorAndNeverAnEmptyList(fake):
    fake(history={"refused": True, "entries": [],
                  "message": "This Director is not connected to a Gateway, so its records cannot be read."})

    result = _run("director", "restart-history", "--director", DIRECTOR)

    assert result.exit_code == 1
    assert "not connected to a Gateway" in result.stderr
    assert "count:" not in result.stdout


def test_restartHistory_Json_IsTheGatewaysAnswer(fake):
    fake(history=HISTORY)

    result = _run("director", "restart-history", "--director", DIRECTOR, "--json")

    assert result.exit_code == 0, result.output
    assert json.loads(result.stdout) == HISTORY


def test_restartHistory_Count_NarrowsJsonTooAndLeavesTheShapeAlone(fake):
    """A filter applies whatever the output shape is - the repository's own command line standard.

    --count used to be dropped on the --json path, so an agent that asked for one record in
    JavaScript Object Notation was handed every one of them and nothing said so.
    """
    fake(history=HISTORY)

    result = _run("director", "restart-history", "--director", DIRECTOR, "--json", "--count", "1")
    answer = json.loads(result.stdout)

    assert result.exit_code == 0, result.output
    assert [e["workspaceId"] for e in answer["entries"]] == ["restart-2026-09-20-0800"]
    # The SHAPE is the Gateway's own: the same keys, the same message, only the records narrowed.
    assert answer["message"] == HISTORY["message"]
    assert set(answer) == set(HISTORY)


def test_restartHistory_EntriesInAShapeThisCannotRead_IsARefusalNotAnEmptyHistory(fake):
    """The top-level shape check, one level down.

    A Gateway that renamed or reshaped `entries` used to make this command print `count: 0` and exit
    0 - the Director saying "never restarted" when nothing at all had been read.
    """
    for entries in ({"records": []}, [1, 2, 3], "none"):
        fake(history={"refused": False, "message": "This Director has 2 restart records.", "entries": entries})

        result = _run("director", "restart-history", "--director", DIRECTOR)

        assert result.exit_code == 1, entries
        assert "in a shape this command cannot read" in result.stderr, entries
        assert "count:" not in result.stdout, entries


def test_restartHistory_SeatsInAShapeThisCannotRead_IsARefusal(fake):
    record = dict(HISTORY["entries"][0], seats="two of them")
    fake(history={"refused": False, "message": "This Director has one restart record.", "entries": [record]})

    result = _run("director", "restart-history", "--director", DIRECTOR)

    assert result.exit_code == 1
    assert "what became of them is unknown" in result.stderr


def test_restartHistory_CountBelowOne_IsAUsageErrorAndNothingIsAsked(fake):
    gw = fake(history=HISTORY)

    result = _run("director", "restart-history", "--director", DIRECTOR, "--count", "0")

    assert result.exit_code == 2
    assert gw.gets == []


def test_smartRestartStatus_SessionsInAShapeThisCannotRead_IsARefusalNotARunWithNoSessions(fake):
    """A phase and a count with every session missing underneath them is an absence read as an answer."""
    reading = _progress(True, "Collecting")
    reading["sessions"] = {"s1": "Asked"}
    fake([reading])

    result = _run("director", "smart-restart-status", "--director", DIRECTOR)

    assert result.exit_code == 1
    assert "what is happening to them is unknown" in result.stderr


# ===== naming the Director =====

def test_everyCommand_MachineWithoutDirector_IsAUsageErrorAndNothingIsAsked(fake):
    """--machine narrows a NAME; on its own it used to be read and dropped.

    `director smart-restart --machine OTHER_BOX` reads as "empty the Director on OTHER_BOX" and
    emptied the one this session belongs to instead. On the one command here that closes every
    session on a machine, a locator that is accepted and ignored is refused rather than silent.
    """
    for argv in (["director", "smart-restart", "--machine", "OTHER_BOX"],
                 ["director", "smart-restart-status", "--machine", "OTHER_BOX"],
                 ["director", "restart-history", "--machine", "OTHER_BOX"]):
        gw = fake()
        result = _run(*argv)

        assert result.exit_code == 2, argv
        assert "it does not choose a Director on its own" in result.stderr, argv
        assert gw.gets == [] and gw.posts == [], argv


def test_smartRestart_MachineBesideDirector_StillNarrowsTheNameToOneComputer(fake):
    """The flag keeps doing its own job: it is only its use ALONE that is refused."""
    gw = fake([_progress(False, "Done", outcome="RestartAccepted", detail="Restarted.")],
              directors=[
                  {"directorId": DIRECTOR, "displayName": "DevThrottle_1", "machineName": "SOREN_NORTH"},
                  {"directorId": OTHER_DIRECTOR, "displayName": "DevThrottle_1", "machineName": "OTHER_BOX"},
              ])

    result = _run("director", "smart-restart", "--director", "DevThrottle_1", "--machine", "OTHER_BOX")

    assert result.exit_code == 0, result.output
    assert gw.posts[0][0] == f"directors/{OTHER_DIRECTOR}/smart-restart"



def test_everyCommand_ADirectorNameThatMatchesNothing_SaysSoAndNamesDirectorList(fake):
    fake(directors=[])

    for argv in (["director", "smart-restart", "--director", "nobody"],
                 ["director", "smart-restart-status", "--director", "nobody"],
                 ["director", "restart-history", "--director", "nobody"]):
        result = _run(*argv)
        assert result.exit_code == 1, argv
        assert "No Director matches 'nobody'" in result.stderr, argv
        assert "cc-devthrottle director list" in result.stderr, argv


def test_everyCommand_NoDirectorNamedAndNoneHere_SaysToNameOne(fake, monkeypatch):
    fake()
    monkeypatch.delenv("CC_DIRECTOR_ID", raising=False)

    result = _run("director", "smart-restart")

    assert result.exit_code == 1
    assert "--director" in result.stderr
