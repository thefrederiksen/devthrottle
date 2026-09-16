"""Tests for `cc-devthrottle session report`: a session telling its parent what it did.

The last step of delegated work. A parent asked for something; getting back to them is part of doing
it, and the session sends that itself rather than leaving a grey row and hoping somebody wonders
about it.

What these assert, and why each one is here:

  * It reaches the PARENT, with the session's own words, and it uses the SAME fact the roster folds
    its colour from - so the report and the dot can never disagree about who owns this session.
  * With no parent it sends NOTHING and still succeeds, because the user already has it: no parent
    means the user, and a session the user owns is red and in his queue the moment it stops. That
    red IS the report.
  * It REFUSES on a roster it could not read in full. "Do I have a parent?" is answered from the
    fleet, so an unreadable roster is not evidence of having none - it is not knowing. An
    absence-shaped check failing open here would convert every roster hiccup into "you are the
    user's", and a worker would silently stop reporting to a supervisor that was alive throughout.
"""

import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared.axi_output import parse_list  # noqa: E402
from src import session_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

WORKER = "11111111-1111-1111-1111-111111111111"
PARENT = "22222222-2222-2222-2222-222222222222"


def _fleet(*rows, complete=True, reason=None):
    return lambda: (list(rows), complete, reason, None)


def _worker(has_live_supervisor=True, controller=PARENT):
    return {
        "sessionId": WORKER,
        "name": "Mission - Worker - the thing",
        "hasLiveSupervisor": has_live_supervisor,
        "controllerSessionId": controller,
    }


# The Gateway always sends controllerSessionId; null is a session nobody drives.
PARENT_ROW = {"sessionId": PARENT, "name": "Mission - Manager", "controllerSessionId": None}


@pytest.fixture
def sent(monkeypatch):
    """Capture what was posted, without any real HTTP."""
    posted = {}

    def fake_post_json(path, body):
        posted["path"] = path
        posted["body"] = body
        # The Gateway's real acceptance shape (FleetMessageSendResponse). Using anything else here would
        # make these tests pass over a report the product would have called not queued.
        return {"status": "queued", "messageId": "m" * 32, "recipientSessionId": path.split("/")[1]}

    monkeypatch.setenv("CC_SESSION_ID", WORKER)
    monkeypatch.setattr(session_ops.gateway, "post_json", fake_post_json)
    return posted


def test_the_report_reaches_the_parent_in_the_sessions_own_words(monkeypatch, sent):
    monkeypatch.setattr(session_ops, "_get_fleet", _fleet(_worker(), PARENT_ROW))

    result = runner.invoke(app, ["session", "report", "Fixed the auth bug; tests green."])

    assert result.exit_code == 0
    # Addressed to the PARENT, not to this session and not broadcast.
    assert sent["path"] == f"sessions/{PARENT}/message"
    assert sent["body"]["text"] == "Fixed the auth bug; tests green."
    # A REPORT, so the Gateway does not hold it to the per-recipient spacing that every refusal points at.
    assert sent["body"]["kind"] == "report"
    assert "Queued" in result.output


def test_with_no_parent_nothing_is_sent_and_it_still_succeeds(monkeypatch, sent):
    # No parent means the USER owns it, and he already has it: the session is red and in his queue
    # the moment it stops. Messaging him a second time through a channel he does not read would be
    # noise, so this is a correct answer to "who do I report to", not a failure.
    monkeypatch.setattr(
        session_ops, "_get_fleet", _fleet(_worker(has_live_supervisor=False, controller=None))
    )

    result = runner.invoke(app, ["session", "report", "Finished the nightly sweep."])

    assert result.exit_code == 0
    assert "path" not in sent
    assert "USER owns you" in result.output


def test_a_dead_parent_is_the_users_session_even_though_the_controller_is_still_named(
    monkeypatch, sent
):
    """THE ORPHAN, and the reason this reads hasLiveSupervisor rather than the controller id.

    A session whose supervisor exited still NAMES that supervisor - the link is a raw fact and
    nothing clears it. Reporting to the named id would send the session's answer into a dead
    mailbox: delivered, unread, and lost, with the session looking like it had reported. The
    liveness answer is the one the roster folds its colour from, so the report and the dot agree.
    """
    monkeypatch.setattr(
        session_ops,
        "_get_fleet",
        _fleet(_worker(has_live_supervisor=False, controller=PARENT)),
    )

    result = runner.invoke(app, ["session", "report", "Blocked on a missing credential."])

    assert result.exit_code == 0
    assert "path" not in sent
    assert "USER owns you" in result.output


def test_an_unreadable_roster_refuses_rather_than_claiming_no_parent(monkeypatch, sent):
    # The absence-shaped check, closed. A roster missing sessions cannot tell "you have no parent"
    # apart from "your parent is one of the rows I could not see".
    monkeypatch.setattr(
        session_ops,
        "_get_fleet",
        _fleet(_worker(), complete=False, reason="a Director is unreachable"),
    )

    result = runner.invoke(app, ["session", "report", "Done."])

    assert result.exit_code != 0
    assert "path" not in sent
    assert "could not be read in full" in result.output


def test_a_session_missing_from_the_roster_refuses(monkeypatch, sent):
    # The roster is complete and this session is simply not in it - who owns it is unanswerable,
    # so it refuses rather than defaulting to either answer.
    monkeypatch.setattr(session_ops, "_get_fleet", _fleet(PARENT_ROW))

    result = runner.invoke(app, ["session", "report", "Done."])

    assert result.exit_code != 0
    assert "path" not in sent


def test_a_report_with_no_words_is_refused(monkeypatch, sent):
    # A report with no words is the 'notice me' ping this design rejects: the parent would have to
    # open the session to find out what happened, which is the work the report exists to save.
    monkeypatch.setattr(session_ops, "_get_fleet", _fleet(_worker(), PARENT_ROW))

    result = runner.invoke(app, ["session", "report"])

    assert result.exit_code != 0
    assert "path" not in sent
    assert "say what you did" in result.output


def test_a_refused_delivery_is_a_failure_not_a_shrug(monkeypatch, sent, either_console):
    """A report that was not queued must not look like one that was.

    The whole point of this verb is that the parent LEARNS. Printing success over a refusal would
    leave a session believing it had handed its work back when nothing had been queued - the same
    silence this replaced, with a green line on top of it. Both shapes a refusal can take are
    checked: a refusal in a 200 body, and the HTTP error the Gateway actually answers with.
    """
    monkeypatch.setattr(session_ops, "_get_fleet", _fleet(_worker(), PARENT_ROW))
    monkeypatch.setattr(
        session_ops.gateway, "post_json",
        lambda path, body: {"status": "refused", "error": "the parent is not accepting messages"},
    )

    result = runner.invoke(app, ["session", "report", "Done."])

    assert result.exit_code != 0
    assert "Not queued" in result.output
    assert "not accepting messages" in result.output

    def refuse(path, body):
        raise session_ops.gateway.GatewayError("You have sent 6 messages in the last hour; the limit is 6.", status=429)

    monkeypatch.setattr(session_ops.gateway, "post_json", refuse)
    result = runner.invoke(app, ["session", "report", "Done."])
    assert result.exit_code != 0
    assert "Not queued" in result.output
    # Verbatim, on the raw output: the Gateway's sentence is quoted, so the console must not style it.
    assert "You have sent 6 messages in the last hour; the limit is 6." in result.output


def test_it_reports_for_a_named_session_when_asked(monkeypatch, sent):
    # --target exists for the same reason every other session verb has one: so a person or another
    # session can act on a session that is not itself.
    monkeypatch.delenv("CC_SESSION_ID", raising=False)
    monkeypatch.setattr(session_ops, "_get_fleet", _fleet(_worker(), PARENT_ROW))
    monkeypatch.setattr(session_ops, "_resolve_target", lambda t, command_name=None: _worker())

    result = runner.invoke(app, ["session", "report", "Done.", "--target", "worker"])

    assert result.exit_code == 0
    assert sent["path"] == f"sessions/{PARENT}/message"


# --- `session workers`: a hand that is DOWN must read as down --------------------------------------


def test_session_workers_shows_no_hand_for_a_worker_that_is_not_asking(monkeypatch, capsys, plain):
    """THE SHIPPED DEFECT THIS RUN FOUND, one line from the report verb and the same mistake.

    `raised` was `bool(gateway.field(x, "needsManager", ...))`. That helper returns a STRING always
    and `str(False)` is `"False"`, which is truthy - so every worker read as having its hand up.
    Measured on the live wire: `needsManager` arrives present and `false`, so it fired on every row.

    It stayed invisible because the reason is null when a hand is down, so the cell rendered empty
    either way. That is luck, not correctness: the moment a reason outlives a lowered hand, a worker
    that is asking for nothing shows as asking its supervisor for something.
    """
    monkeypatch.setenv("CC_SESSION_ID", PARENT)
    worker = {
        "sessionId": WORKER,
        "name": "wkr",
        "controllerSessionId": PARENT,
        "triageBucket": "onHold",
        "activityState": "Idle",
        "stateLabel": "Snoozed",
        "needsManager": False,               # present and false, exactly as the Gateway sends it
        "needsManagerReason": "stale words",  # and a reason that outlived the lowered hand
    }
    monkeypatch.setattr(session_ops, "_get_fleet", lambda: ([worker, PARENT_ROW], True, None, None))

    session_ops.list_my_workers()

    _, records = parse_list(plain(capsys.readouterr().out), "workers")
    # The control: the row IS listed, so an empty list cannot pass - but its hand is down, so nothing
    # is asked for, and the reason that outlived the lowered hand is not shown as a need.
    assert records == [{"id": WORKER, "name": "wkr", "state": "snoozed", "hand": "down", "need": None}]


def test_a_session_a_fleet_manager_owns_sends_nothing_and_says_the_gateway_tells_it(monkeypatch, sent):
    """The Gateway delivers this session's stop to its Fleet Manager at that session's own turn end, so a
    report would only interrupt it with the same news. The Gateway says who the owner is; nothing here
    decides it."""
    worker = dict(_worker(), ownedByFleetManager=True)
    monkeypatch.setattr(session_ops, "_get_fleet", _fleet(worker, PARENT_ROW))

    result = runner.invoke(app, ["session", "report", "Fixed the auth bug; tests green."])

    assert result.exit_code == 0, result.output
    assert sent == {}
    assert "Nothing sent: a Fleet Manager owns you" in result.output
    assert "next turn end" in result.output


def test_an_owner_the_gateway_does_not_call_a_fleet_manager_still_gets_the_report(monkeypatch, sent):
    worker = dict(_worker(), ownedByFleetManager=False)
    monkeypatch.setattr(session_ops, "_get_fleet", _fleet(worker, PARENT_ROW))

    result = runner.invoke(app, ["session", "report", "Done."])

    assert result.exit_code == 0, result.output
    assert sent["path"] == f"sessions/{PARENT}/message"
