"""Tests for `cc-devthrottle session stop` and `cc-devthrottle session done --undo`.

These pin the two things this command must not get wrong, and both of them are about EXIT CODES and
WHOSE WORDS get printed.

  * A stop never fails because there is nothing left to stop. All three verdicts - stopped, already
    stopped, and not on this fleet - exit ZERO. The failure being designed out is a second run
    returning an error, which an operator reads as "it is still alive". The trap is real and specific:
    the resolver every other session verb uses prints "No session matches" and exits 1, so a stop
    routed through it would turn the Gateway's careful 200 into an error before anyone could read it.
  * THE CLIENT IS DUMB. The Gateway folds the headline and the detail lines; this prints them, in
    order, and adds exactly one thing of its own - how to type its own flag - and only when a reason
    is what was missing.

The exit code is asserted through the real command line rather than by calling the function, because
the exit code IS the thing under test and only the command line produces one.
"""

import sys
from pathlib import Path

import pytest
import typer
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import session_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

SESSION_ID = "9c41e7a2-1111-2222-3333-444455556666"
SHORT_ID = "9c41e7a2"
OTHER_ID = "9c41e7a2-aaaa-bbbb-cccc-ddddeeeeffff"


def _row(session_id=SESSION_ID, name="worker", machine="MACHINE_A"):
    return {"sessionId": session_id, "name": name, "machineName": machine, "repoPath": r"D:\repo"}


def _stopped():
    """The Gateway's folded answer for a session that really was running. The contract's own example."""
    return {
        "verdict": "stopped",
        "headline": f"stopped {SHORT_ID} - process 51884 ended, row removed",
        "details": [
            r"the worktree C:\Repos\thing was left untouched - it has uncommitted changes in it",
            "reason: it was editing the wrong repository",
        ],
        "sessionId": SESSION_ID,
        "shortId": SHORT_ID,
        "processId": 51884,
        "processEnded": True,
        "rowRemoved": True,
    }


def _already_stopped():
    return {
        "verdict": "alreadyStopped",
        "headline": (
            f"already stopped {SHORT_ID} - no process was running; "
            "the row it left behind has been cleared"
        ),
        "details": ["reason: tidying up"],
        "sessionId": SESSION_ID,
        "processEnded": False,
        "rowRemoved": True,
    }


def _not_on_fleet(target=SHORT_ID):
    return {
        "verdict": "notOnFleet",
        "headline": (
            f"not on this fleet - nothing in this account carries the id {target}, "
            "so no machine was asked and no machine's processes were searched"
        ),
        "details": ["reason: tidying up"],
    }


@pytest.fixture
def gateway_stub(monkeypatch):
    """Serve a chosen Gateway answer, capture what went out, and never touch the network."""
    calls = []

    def serve(response, roster=None):
        monkeypatch.setenv("CC_SESSION_ID", SESSION_ID)
        # The one fetch every target resolution goes through. Reported COMPLETE so these tests assert
        # the stop verb and never trip over the incomplete-roster caveat (issue #1051).
        monkeypatch.setattr(
            session_ops,
            "_get_fleet",
            lambda: ([_row()] if roster is None else roster, True, None, None),
        )

        def post_json(path, body=None, timeout=30):
            calls.append({"path": path, "body": body})
            if isinstance(response, Exception):
                raise response
            return response

        monkeypatch.setattr(session_ops.gateway, "post_json", post_json)
        return calls

    return serve


def _stop(target, *reason_args):
    return runner.invoke(app, ["session", "stop", target, *reason_args])


@pytest.fixture(autouse=True)
def wide_console(monkeypatch):
    """Render into a wide console so no assertion depends on where the terminal wrapped a line.

    Rich wraps to the console width, so a sentence that is one line on a wide terminal arrives in the
    capture with a newline through the middle of it. Asserting on the raw capture would pin the width
    of whatever machine the test ran on rather than the words the reader sees. Wrapping is the
    terminal's business; these tests are about the words and the exit code.
    """
    from rich.console import Console

    monkeypatch.setattr(session_ops, "console", Console(width=200))


# ===== the three verdicts: every one of them is a success =====


def test_a_stopped_session_prints_the_headline_then_every_detail_line_and_exits_zero(
    gateway_stub, plain
):
    calls = gateway_stub(_stopped())

    result = _stop(SESSION_ID, "--reason", "it was editing the wrong repository")

    assert result.exit_code == 0
    assert calls[0]["path"] == f"sessions/{SESSION_ID}/stop"
    assert calls[0]["body"] == {"reason": "it was editing the wrong repository"}
    out = plain(result.output)
    assert f"stopped {SHORT_ID} - process 51884 ended, row removed" in out
    assert r"the worktree C:\Repos\thing was left untouched" in out
    assert "reason: it was editing the wrong repository" in out


def test_an_already_stopped_session_exits_zero(gateway_stub, plain):
    # The whole point of Ruling 3. A second stop that returned an error would be read as "it is still
    # alive" - the exact wrong conclusion, drawn from the exact right action.
    gateway_stub(_already_stopped())

    result = _stop(SESSION_ID, "--reason", "tidying up")

    assert result.exit_code == 0
    assert "already stopped" in plain(result.output)
    assert "the row it left behind has been cleared" in plain(result.output)


def test_a_target_on_no_machine_exits_zero_and_prints_the_gateways_own_sentence(
    gateway_stub, plain
):
    # notOnFleet is a SUCCESS with a 200 behind it, and the sentence that matters is the Gateway's:
    # it says no machine was ASKED, which is a different fact from "it is gone". This client neither
    # writes that sentence nor second-guesses it.
    gateway_stub(_not_on_fleet())

    result = _stop(SESSION_ID, "--reason", "tidying up")

    assert result.exit_code == 0
    out = plain(result.output)
    assert "not on this fleet" in out
    assert "no machine was asked and no machine's processes were searched" in out


def test_a_target_the_roster_does_not_know_is_still_sent_to_the_gateway_and_exits_zero(
    gateway_stub, plain
):
    """THE TRAP THIS COMMAND EXISTS TO AVOID, pinned.

    _resolve_target - which every other session verb uses - prints "No session matches" and exits 1
    when the roster has nothing. Route the stop through it and the not-on-this-fleet case becomes an
    ERROR on the client side, the Gateway is never asked, and its careful 200 is never read. So the
    raw target must go out exactly as typed, and the exit code must be zero.
    """
    unknown = "deadbeef"
    calls = gateway_stub(_not_on_fleet(unknown), roster=[])

    result = _stop(unknown, "--reason", "tidying up")

    assert calls, "the stop was never sent - the client ruled on the target itself"
    assert calls[0]["path"] == f"sessions/{unknown}/stop"
    assert result.exit_code == 0
    assert "No session matches" not in plain(result.output)


def test_an_ambiguous_target_is_still_an_error(gateway_stub, plain):
    # Unchanged behaviour, deliberately. "Which of these two did you mean" is a question about what
    # the caller typed, not a verdict about any session's state - and guessing which of two sessions
    # to end would be the worst possible way to be helpful.
    calls = gateway_stub(
        _stopped(),
        roster=[_row(SESSION_ID, "worker-one"), _row(OTHER_ID, "worker-two", "MACHINE_B")],
    )

    result = _stop("9c41e7a2", "--reason", "tidying up")

    assert result.exit_code != 0
    assert not calls, "an ambiguous target was stopped anyway - one of two sessions was guessed at"
    out = plain(result.output)
    assert "is ambiguous - 2 matches" in out
    assert "cc-devthrottle session stop" in out


# ===== the reason: refused here, and refused again by the Gateway in its own words =====


def test_no_reason_is_refused_and_names_the_reason_and_the_flag(gateway_stub, plain):
    calls = gateway_stub(_stopped())

    result = _stop(SESSION_ID)

    assert result.exit_code != 0
    assert not calls, "a stop with no reason was sent anyway"
    out = plain(result.output)
    assert "a reason is required" in out
    assert "none was given" in out
    assert "--reason" in out


def test_a_whitespace_only_reason_is_refused_the_same_way(gateway_stub, plain):
    # A reason of spaces is not a reason. Accepting it would put an empty string in the audit record
    # and satisfy the letter of the rule while defeating the only purpose it has.
    calls = gateway_stub(_stopped())

    result = _stop(SESSION_ID, "--reason", "   ")

    assert result.exit_code != 0
    assert not calls
    out = plain(result.output)
    assert "a reason is required" in out
    assert "--reason" in out


def test_the_gateways_own_refusal_is_printed_and_the_flag_hint_is_added_after_it(
    gateway_stub, plain
):
    # The Gateway refuses a missing reason too, and its sentence is written for the reader. This
    # client adds the one thing the server cannot know - how to type the flag - AFTER those words,
    # never instead of them.
    server_sentence = "a reason is required to stop a session, and the request carried none"
    gateway_stub(session_ops.gateway.GatewayError(server_sentence))

    result = _stop(SESSION_ID, "--reason", "tidying up")

    assert result.exit_code != 0
    out = plain(result.output)
    assert server_sentence in out
    assert "--reason" in out
    assert out.index(server_sentence) < out.index("Re-run with --reason")


# ===== the two failures that are genuinely failures =====


def test_an_unreachable_gateway_is_non_zero_and_says_so(gateway_stub, plain):
    # The shared client writes this sentence, naming the address it could not reach. Rewording it here
    # would throw away the only line that says where to go and look.
    unreachable = (
        "Cannot reach the Gateway at http://gateway.invalid: [Errno 11001] getaddrinfo failed. "
        "Every fleet command goes through it and there is no local path to fall back to."
    )
    gateway_stub(session_ops.gateway.GatewayError(unreachable))

    result = _stop(SESSION_ID, "--reason", "tidying up")

    assert result.exit_code != 0
    out = plain(result.output)
    assert "Not stopped" in out
    assert "Cannot reach the Gateway at http://gateway.invalid" in out
    # A bracketed token in the server's text must not crash the branch whose only job is to report a
    # failure - Rich reads [Errno 11001] as markup unless it is escaped.
    assert "[Errno 11001]" in out


def test_a_process_that_would_not_die_is_non_zero_and_says_so(gateway_stub, plain):
    # The Director's sentence, carried out through the Gateway. This client does not know that the
    # process survived - only the machine that tried does - so it prints what it was told and adds
    # nothing. What it DOES add is that nothing was stopped, which is the part it knows for certain.
    server_sentence = (
        "the agent process 51884 did not exit after being signalled and then forced, "
        "so the session is still running on MACHINE_A"
    )
    gateway_stub(session_ops.gateway.GatewayError(server_sentence))

    result = _stop(SESSION_ID, "--reason", "tidying up")

    assert result.exit_code != 0
    out = plain(result.output)
    assert "Not stopped" in out
    assert server_sentence in out
    # No flag hint here: nothing was missing from what the caller typed, and offering --reason would
    # send them off to fix a thing that was never wrong.
    assert "Re-run with --reason" not in out


# ===== the detail lines belong to the Gateway, in the order it gave them =====


def test_the_detail_lines_are_printed_in_the_order_the_gateway_gave_them(gateway_stub, plain):
    # The order is part of the answer - the worktree line before the reason line. Re-ordering or
    # filtering here would be this client deciding what matters about a stop.
    gateway_stub(
        {
            "verdict": "stopped",
            "headline": "stopped 9c41e7a2 - process 51884 ended, row removed",
            "details": ["first line", "second line", "third line"],
        }
    )

    result = _stop(SESSION_ID, "--reason", "tidying up")

    out = plain(result.output)
    assert result.exit_code == 0
    assert out.index("first line") < out.index("second line") < out.index("third line")


def test_an_empty_details_list_prints_nothing_beyond_the_headline(gateway_stub, plain):
    gateway_stub(
        {
            "verdict": "stopped",
            "headline": "stopped 9c41e7a2 - process 51884 ended, row removed",
            "details": [],
        }
    )

    result = _stop(SESSION_ID, "--reason", "tidying up")

    assert result.exit_code == 0
    printed = [line for line in plain(result.output).splitlines() if line.strip()]
    assert printed == ["stopped 9c41e7a2 - process 51884 ended, row removed"]


def test_an_answer_with_no_headline_is_reported_as_no_answer_rather_than_as_a_success(
    gateway_stub, plain
):
    """A BROKEN INSTRUMENT IS NOT A FOURTH VERDICT.

    Exiting zero and printing nothing would be the button that accepts a click and says nothing -
    the very defect this mission exists to remove. The sentence says we do not KNOW what happened,
    which is true, rather than naming an outcome nobody was told.
    """
    gateway_stub({"verdict": "stopped"})

    result = _stop(SESSION_ID, "--reason", "tidying up")

    assert result.exit_code != 0
    out = plain(result.output)
    assert "No answer" in out
    assert "cannot report whether it was stopped" in out


# ===== session done --undo =====


@pytest.fixture
def delete_stub(monkeypatch):
    """Capture the DELETE the undo sends, and serve a chosen answer."""
    calls = []

    def serve(response=None):
        monkeypatch.setenv("CC_SESSION_ID", SESSION_ID)
        monkeypatch.setattr(session_ops, "_get_fleet", lambda: ([_row()], True, None, None))

        def delete(path, timeout=30):
            calls.append(path)
            if isinstance(response, Exception):
                raise response
            return response

        monkeypatch.setattr(session_ops.gateway, "delete", delete)
        return calls

    return serve


def test_undo_calls_the_cancel_route_for_this_session_and_needs_no_reason(delete_stub, plain):
    calls = delete_stub({"cancelled": True})

    result = runner.invoke(app, ["session", "done", "--undo"])

    assert result.exit_code == 0
    assert calls == [f"sessions/{SESSION_ID}/request-deletion"]
    assert "no longer marked for deletion" in plain(result.output)


def test_undo_takes_an_explicit_target_too(delete_stub):
    calls = delete_stub({"cancelled": True})

    result = runner.invoke(app, ["session", "done", SESSION_ID, "--undo"])

    assert result.exit_code == 0
    assert calls == [f"sessions/{SESSION_ID}/request-deletion"]


def test_undo_does_not_post_a_deletion_request(delete_stub, monkeypatch):
    # The two directions share a route name, and confusing them would be catastrophic in the one
    # direction that matters: an --undo that FLAGGED the session would delete the very session the
    # caller was rescuing.
    delete_stub({"cancelled": True})

    def must_not_post(path, body=None, timeout=30):
        raise AssertionError(f"--undo posted to {path} instead of clearing the flag")

    monkeypatch.setattr(session_ops.gateway, "post_json", must_not_post)

    result = runner.invoke(app, ["session", "done", "--undo"])

    assert result.exit_code == 0


def test_undo_with_a_reason_is_refused_rather_than_silently_dropping_it(delete_stub, plain):
    # The decision, stated: --undo with --reason is a contradiction, and it is refused. Dropping the
    # reason would let the caller believe something was recorded that never was.
    calls = delete_stub({"cancelled": True})

    result = runner.invoke(app, ["session", "done", "--undo", "--reason", "changed my mind"])

    assert result.exit_code != 0
    assert not calls
    out = plain(result.output)
    assert "--undo and --reason cannot be used together" in out


def test_plain_done_still_flags_the_session(monkeypatch, plain):
    # The undo flag must not have moved the ordinary path. This is the regression guard for the
    # branch that was added around it.
    posted = []
    monkeypatch.setenv("CC_SESSION_ID", SESSION_ID)
    monkeypatch.setattr(session_ops, "_get_fleet", lambda: ([_row()], True, None, None))
    monkeypatch.setattr(
        session_ops.gateway,
        "post_json",
        lambda path, body=None, timeout=30: posted.append((path, body)) or {},
    )

    result = runner.invoke(app, ["session", "done", "--reason", "finished"])

    assert result.exit_code == 0
    assert posted == [(f"sessions/{SESSION_ID}/request-deletion", {"reason": "finished"})]
    assert "Marked" in plain(result.output)


# ===== discovery: a verb an agent cannot find does not exist as far as the fleet is concerned =====


def test_both_verbs_are_discoverable_with_their_command_lines():
    from src import cli  # noqa: E402

    stop = next(a for a in cli._ACTIONS if a["id"] == "session-stop")
    assert "session stop" in stop["command"]
    assert "--reason" in stop["command"]
    assert stop["mutatesState"] is True
    # The two things an agent must learn without running it: the reason is required, and stopping
    # something already stopped is not a failure.
    assert "reason is REQUIRED" in stop["description"]
    assert "already stopped SUCCEEDS" in stop["description"]

    undo = next(a for a in cli._ACTIONS if a["id"] == "session-done-undo")
    assert "--undo" in undo["command"]
    assert undo["mutatesState"] is True


def test_the_reason_flag_is_declared_with_its_short_form_and_the_target_is_required():
    """Asserted on the DECLARATION, not on rendered help.

    Rendering help is broken across the whole tool in this environment - every `--help` raises
    TypeError out of TyperArgument.make_metavar, from the installed typer and click pair, for
    commands written long before this one. Asserting against the parameter declarations tests what
    the command actually offers without depending on a renderer that is broken for reasons unrelated
    to this change. THAT IS A GAP AND IT IS NAMED AS ONE: nothing here proves the help text reads
    well, because nothing here can render it.
    """
    import inspect

    from src import cli  # noqa: E402

    parameters = inspect.signature(cli.stop).parameters
    reason = parameters["reason"].default
    assert reason.param_decls == ("--reason", "-r")
    assert "Required" in reason.help
    # The target is a required positional. A stop with no target must never quietly fall back to
    # "this session" and end the session that asked.
    assert parameters["target"].default.default is ...


# ===== gaps, named as gaps =====
#
# NOT COVERED HERE, deliberately, and nobody should read a green run of this file as covering them:
#
#   * That the Gateway actually answers 200 with verdict notOnFleet for an unknown id, that it
#     refuses a missing reason with 400, and that its headline reads as the contract says. Those are
#     the Gateway's tests, and every Gateway answer in this file is a STUB written from the outcome
#     contract in missions/stop-a-session/handoff-phase-a.md. If the route drifts from that contract,
#     these tests stay green and the command is wrong - only an end-to-end run against a real Gateway
#     catches that.
#   * That the stop is recorded in the governance audit log with its reason. Nothing observable from
#     the command line proves it, and asserting on the client's own request body would be proving the
#     wrong thing.
#   * The distinction between "the Director could not be reached" and "the process would not die" is
#     made by the SENTENCE the server sends, not by anything this client can determine: the shared
#     client raises one exception type and does not carry the status code. The two tests above assert
#     that each sentence survives intact and exits non-zero; they do not and cannot assert that this
#     client could tell them apart on its own.
