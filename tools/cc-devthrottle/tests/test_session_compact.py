"""Tests for `cc-devthrottle session compact`: the fleet verb that rescues a full session (issue #2150).

A session whose context window is full swallows every message sent to it, so this command is the only
way an agent gets one moving again. Two things therefore have to be right, and both are asserted here:
what goes out on the wire (the target, and the follow-up that is sent only after compaction finishes),
and what comes back (a report that never dresses "submitted but not watched" up as "compacted").
"""

import sys
from pathlib import Path

import pytest
import typer

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import session_ops  # noqa: E402

SESSION_ID = "11111111-2222-3333-4444-555555555555"


@pytest.fixture
def posted(monkeypatch):
    """Capture the request the verb posts, and serve a chosen response - no real HTTP."""
    calls = []

    def serve(response):
        monkeypatch.setenv("CC_SESSION_ID", SESSION_ID)
        # An explicit target is resolved against the fleet roster; serve one rather than reaching for a
        # live Director.
        # Patch the ONE fetch every resolve goes through. It reports the roster as complete, so these
        # tests assert the compact verb and never trip over the incomplete-roster caveat (issue #1051).
        monkeypatch.setattr(session_ops, "_get_fleet",
                            lambda: ([{"sessionId": SESSION_ID, "name": "stuck", "machineName": "M", "number": 114}], True, None, None))

        def post_json(path, body, timeout=30):
            calls.append({"path": path, "body": body, "timeout": timeout})
            return response

        monkeypatch.setattr(session_ops.gateway, "post_json", post_json)
        return calls

    return serve


def _ok(detail="Compacted in 41 seconds, then sent the follow-up.", observed=True):
    return {"submitted": True, "compactionObserved": observed, "waitedSeconds": 41.0,
            "continued": True, "detail": detail}


def test_it_posts_the_target_and_the_follow_up(posted):
    calls = posted(_ok())

    session_ops.compact_session(SESSION_ID, "continue")

    assert calls[0]["path"] == f"sessions/{SESSION_ID}/compact-context"
    # The target is in the PATH now, asserted above; the body carries only what was asked for.
    assert "toSessionId" not in calls[0]["body"]
    assert calls[0]["body"]["continuePrompt"] == "continue"


def test_plain_compact_sends_no_message(posted):
    # `session compact` is the housekeeping verb: it frees room and leaves the session where it was.
    # A continuation smuggled in here would put words into a session the caller never agreed to send -
    # which is exactly why this is a separate verb from compact-continue rather than a default.
    calls = posted(_ok())

    session_ops.compact_session(SESSION_ID, None)

    assert "continuePrompt" not in calls[0]["body"]


def test_the_two_verbs_are_separately_discoverable(monkeypatch):
    # An agent picks a verb by listing actions. Rolling both behaviours into one entry would hide the
    # side-effecting one inside a flag description, and whichever became the default would be what
    # happens when nobody thinks about it.
    from src import cli  # noqa: E402

    plain = next(a for a in cli._ACTIONS if a["id"] == "session-compact")
    both = next(a for a in cli._ACTIONS if a["id"] == "session-compact-continue")

    assert "compact-continue" not in plain["command"]
    assert "NOTHING afterwards" in plain["description"]
    assert "compact-continue" in both["command"]
    assert "THEN type a prompt" in both["description"]
    # The Message Load mission, ruling 17: the continue half is the owner's, and the entry says so, so an
    # agent listing actions does not pick a verb the Gateway will refuse it.
    assert "REFUSES this to every agent" in both["description"]


def test_compact_only_sends_no_follow_up(posted):
    # The caller asked for a compaction and nothing else. A continuePrompt smuggled in here would put
    # words into a session the caller never agreed to send.
    calls = posted(_ok(observed=True))

    session_ops.compact_session(SESSION_ID, None)

    assert "continuePrompt" not in calls[0]["body"]


def test_it_defaults_to_this_session_when_no_target_is_given(posted):
    calls = posted(_ok())

    session_ops.compact_session(None, "continue")

    # The target is in the PATH now, asserted above; the body carries only what was asked for.
    assert "toSessionId" not in calls[0]["body"]


def test_it_waits_far_longer_than_the_ordinary_verb(posted):
    # Compaction routinely runs for a minute or more, and this call deliberately waits for the FINISH.
    # At the 30-second default the client would give up on a compaction that was working perfectly and
    # report a failure - the exact false alarm this verb exists to avoid.
    calls = posted(_ok())

    session_ops.compact_session(SESSION_ID, "continue")

    assert calls[0]["timeout"] >= 240


def test_a_watched_compaction_reports_it_as_compacted(posted, capsys):
    posted(_ok())

    session_ops.compact_session(SESSION_ID, "continue")

    # Joined back into one line: the detail is long, and Rich wraps it at the console width.
    out = " ".join(capsys.readouterr().out.split())
    assert "Compacted" in out
    assert "then sent the follow-up" in out


def test_an_unwatched_compaction_is_not_reported_as_compacted(posted, capsys):
    # Some tools can be told to compact but cannot report finishing. Saying "Compacted" there would tell
    # an agent the session is moving again when nobody actually checked.
    posted({"submitted": True, "compactionObserved": False, "waitedSeconds": 0.0, "continued": False,
            "detail": "Compaction submitted. Codex cannot report when it finishes, so this was not watched."})

    session_ops.compact_session(SESSION_ID, None)

    out = capsys.readouterr().out
    assert "Compaction submitted" in out
    assert "Compacted " not in out


def test_a_failure_is_reported_and_exits_nonzero(posted, monkeypatch, capsys):
    monkeypatch.setenv("CC_SESSION_ID", SESSION_ID)

    def boom(path, body, timeout=30):
        raise session_ops.gateway.GatewayError("gateway returned Timeout")

    monkeypatch.setattr(session_ops.gateway, "post_json", boom)

    with pytest.raises(typer.Exit):
        session_ops.compact_session(None, "continue")

    captured = capsys.readouterr()
    assert captured.out == ""
    assert "gateway returned Timeout" in captured.err
    assert f"cc-devthrottle session buffer {SESSION_ID}" in captured.err


def test_a_bracketed_error_does_not_crash_the_verb(posted, monkeypatch, capsys, plain):
    # The server's error text can quote a path or a fragment of the session's own output. Interpolated
    # raw into Rich markup, a token like [/tmp/x] raises MarkupError out of the branch whose only job is
    # to explain a failure - the same defect already fixed for the buffer verb.
    monkeypatch.setenv("CC_SESSION_ID", SESSION_ID)

    def boom(path, body, timeout=30):
        raise session_ops.gateway.GatewayError("no session at [/tmp/x] on that Director")

    monkeypatch.setattr(session_ops.gateway, "post_json", boom)

    with pytest.raises(typer.Exit):
        session_ops.compact_session(None, "continue")

    assert "no session at [/tmp/x] on that Director" in plain(capsys.readouterr().err)


def test_the_gateways_refusal_is_printed_verbatim(posted, monkeypatch, capsys, either_console):
    # The Message Load mission: the Gateway refuses compact-continue to an agent with a sentence that names
    # what to do instead. It is quoted text, so it must reach the reader unstyled on a colour terminal too.
    monkeypatch.setenv("CC_SESSION_ID", SESSION_ID)
    sentence = ("An agent may compact a session but may not send it a prompt afterwards: typing into a session "
                "is the owner's alone. Send 1 queued message: cc-devthrottle message send <session> \"<text>\"")

    def refuse(path, body, timeout=30):
        raise session_ops.gateway.GatewayError(sentence, status=403)

    monkeypatch.setattr(session_ops.gateway, "post_json", refuse)

    with pytest.raises(typer.Exit):
        session_ops.compact_session(None, "continue")

    # An error goes to standard error (docs/axi-standard.md); the sentence inside it is unchanged.
    assert sentence in capsys.readouterr().err


def test_the_actions_are_discoverable_with_their_command_lines():
    # Agents find these verbs by listing actions, not by reading the source. An action missing from the
    # catalogue does not exist as far as the fleet is concerned.
    from src import cli  # noqa: E402

    for action_id in ("session-compact", "session-compact-continue"):
        action = next(a for a in cli._ACTIONS if a["id"] == action_id)
        assert "session compact" in action["command"]
        assert action["mutatesState"] is True
