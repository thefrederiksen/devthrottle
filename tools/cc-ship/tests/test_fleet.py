"""How cc-ship decides a spawned session has finished, crashed or stalled (issue 2935)."""

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
import fleet  # noqa: E402


def _row(activity="Working", pending=False, crashed=False):
    return {"sessionId": "s1", "status": "Running", "activityState": activity,
            "pendingDeletion": pending, "crashed": crashed}


def test_classify_OutputAndDoneFlag_Finished():
    assert fleet.classify(True, _row(pending=True), False)[0] == fleet.FINISHED


def test_classify_OutputAndGoneAfterFlagSeen_Finished():
    assert fleet.classify(True, None, True)[0] == fleet.FINISHED


def test_classify_OutputAndGoneWithoutFlagEverSeen_Crashed():
    # Inspection finding 1: the file lands, then the process dies before `session done`.
    assert fleet.classify(True, None, False)[0] == fleet.CRASHED


def test_classify_CrashedAndFlaggedWithOutput_Crashed():
    assert fleet.classify(True, _row(pending=True, crashed=True), True)[0] == fleet.CRASHED


def test_classify_OutputWithoutDoneFlag_StillWorking():
    # Observed live: the file lands a few seconds before the flag. Not finished yet.
    outcome, reason = fleet.classify(True, _row(), False)
    assert outcome is None
    assert "waiting for the session to flag itself done" in reason


def test_classify_DoneFlagWithoutOutput_StillWorking():
    assert fleet.classify(False, _row(pending=True), True)[0] is None


def test_classify_GoneWithoutOutput_Crashed():
    assert fleet.classify(False, None, False)[0] == fleet.CRASHED


def test_classify_CrashedFlag_Crashed():
    assert fleet.classify(False, _row(crashed=True), False)[0] == fleet.CRASHED


class _FakeFleet:
    """Replays a scripted list of fleet rows, one per poll."""

    def __init__(self, rows, output=None, write_at=None):
        self.rows, self.polls = rows, 0
        self.output, self.write_at = output, write_at

    def find(self, _session_id):
        row = self.rows[min(self.polls, len(self.rows) - 1)]
        self.polls += 1
        if self.output is not None and self.polls == self.write_at:
            self.output.write_text("{}", encoding="ascii")
        return row


@pytest.fixture
def clock(monkeypatch):
    now = [1000.0]
    monkeypatch.setattr(fleet.time, "monotonic", lambda: now[0])
    monkeypatch.setattr(fleet.time, "time", lambda: now[0])
    monkeypatch.setattr(fleet.time, "sleep", lambda s: now.__setitem__(0, now[0] + s))
    monkeypatch.setattr(fleet, "session_screen", lambda _sid: "Error: missing or invalid token")
    return now


def test_wait_for_output_WorkedThenIdleWithNoOutput_Stalled(tmp_path, monkeypatch, clock):
    fake = _FakeFleet([_row("WaitingForInput"), _row("Working")] + [_row("WaitingForInput")] * 50)
    monkeypatch.setattr(fleet, "find_session", fake.find)
    result = fleet.wait_for_output("s1", tmp_path / "out.json", 3600, poll_seconds=10)
    assert result.outcome == fleet.STALLED
    assert "invalid token" in result.reason


def test_wait_for_output_IdleBeforeEverWorking_NotStalled(tmp_path, monkeypatch, clock):
    # A session waits for its first prompt before it works; a short idle start is not a stall.
    fake = _FakeFleet([_row("WaitingForInput")] * 50)
    monkeypatch.setattr(fleet, "find_session", fake.find)
    result = fleet.wait_for_output("s1", tmp_path / "out.json", 120, poll_seconds=10)
    assert result.outcome == fleet.TIMED_OUT


def test_wait_for_output_OutputThenFlag_Finished(tmp_path, monkeypatch, clock):
    out = tmp_path / "out.json"
    fake = _FakeFleet([_row(), _row(), _row(pending=True)], output=out, write_at=2)
    monkeypatch.setattr(fleet, "find_session", fake.find)
    result = fleet.wait_for_output("s1", out, 300, poll_seconds=5)
    assert result.outcome == fleet.FINISHED


def test_wait_for_output_OutputThenVanishesBeforeFlag_Crashed(tmp_path, monkeypatch, clock):
    out = tmp_path / "out.json"
    fake = _FakeFleet([_row(), _row(), None], output=out, write_at=2)
    monkeypatch.setattr(fleet, "find_session", fake.find)
    result = fleet.wait_for_output("s1", out, 300, poll_seconds=5)
    assert result.outcome == fleet.CRASHED


def test_wait_for_output_FlagSeenThenReaped_Finished(tmp_path, monkeypatch, clock):
    out = tmp_path / "out.json"
    # Poll 1: flagged, file not there yet. Poll 2: the row is reaped and the file is there.
    fake = _FakeFleet([_row(pending=True), None], output=out, write_at=2)
    monkeypatch.setattr(fleet, "find_session", fake.find)
    result = fleet.wait_for_output("s1", out, 300, poll_seconds=5)
    assert result.outcome == fleet.FINISHED
    assert "reaped" in result.reason


def test_wait_for_output_StaleFileBeforeCorrection_NotFinished(tmp_path, monkeypatch, clock):
    out = tmp_path / "out.json"
    out.write_text("{}", encoding="ascii")
    fake = _FakeFleet([_row(pending=True)] * 50)
    monkeypatch.setattr(fleet, "find_session", fake.find)
    later = out.stat().st_mtime + 60
    result = fleet.wait_for_output("s1", out, 100, poll_seconds=10, written_after=later)
    assert result.outcome == fleet.TIMED_OUT


def test_spawn_session_GatewayTimeoutButSessionLanded_ReturnsIt(monkeypatch, clock):
    def fail(_args, timeout=120):
        raise fleet.FleetError("Error: The Gateway at x did not answer in time")
    monkeypatch.setattr(fleet, "_run", fail)
    monkeypatch.setattr(fleet, "list_sessions",
                        lambda: [{"sessionId": "abc", "name": "cc-ship - Reviewer - x"}])
    assert fleet.spawn_session(Path("."), "Codex", "me", "cc-ship - Reviewer - x", Path("b")) == "abc"


def test_spawn_session_GatewayTimeoutAndNoSession_Raises(monkeypatch, clock):
    def fail(_args, timeout=120):
        raise fleet.FleetError("Error: The Gateway at x did not answer in time")
    monkeypatch.setattr(fleet, "_run", fail)
    monkeypatch.setattr(fleet, "list_sessions", lambda: [])
    with pytest.raises(fleet.FleetError, match="unknown"):
        fleet.spawn_session(Path("."), "Codex", "me", "cc-ship - Reviewer - x", Path("b"))


def test_prompt_session_MultiLineText_Rejected():
    with pytest.raises(ValueError):
        fleet.prompt_session("s1", "line one\nline two")


def test_wait_for_output_StallSpansSeparateCalls_Stalled(tmp_path, monkeypatch, clock):
    # cc-ship waits in slices; the second slice must remember the session was seen working.
    watch = {}
    fake = _FakeFleet([_row("Working")] + [_row("WaitingForInput")] * 50)
    monkeypatch.setattr(fleet, "find_session", fake.find)
    first = fleet.wait_for_output("s1", tmp_path / "out.json", 60, poll_seconds=10, watch=watch)
    assert first.outcome == fleet.TIMED_OUT
    second = fleet.wait_for_output("s1", tmp_path / "out.json", 60, poll_seconds=10, watch=watch)
    assert second.outcome == fleet.STALLED


def test_wait_for_output_NeverSeenWorkingForFiveMinutes_Stalled(tmp_path, monkeypatch, clock):
    # Live, 2026-09-16: three reviewers stopped at once on a usage-limit screen, went idle
    # before any poll saw them working, and would have waited out the 45-minute limit.
    watch = {"started": clock[0]}
    fake = _FakeFleet([_row("WaitingForInput")] * 100)
    monkeypatch.setattr(fleet, "find_session", fake.find)
    result = fleet.wait_for_output("s1", tmp_path / "out.json", 3600, poll_seconds=10, watch=watch)
    assert result.outcome == fleet.STALLED
    assert "never seen working" in result.reason
    assert clock[0] - watch["started"] >= fleet.NEVER_WORKED_SECONDS


def test_wait_for_output_ReapedBeforeAnyPollButMarkerWritten_Finished(tmp_path, monkeypatch, clock):
    # Live, 2026-09-16: a reviewer wrote its review, flagged itself done and was reaped
    # while the author was not inside cc-ship wait; it was wrongly called a crash.
    out = tmp_path / "review.json"
    out.write_text("{}", encoding="ascii")
    fleet.done_marker(out).write_text("done", encoding="ascii")
    monkeypatch.setattr(fleet, "find_session", lambda sid: None)
    result = fleet.wait_for_output("s1", out, 60, poll_seconds=10)
    assert result.outcome == fleet.FINISHED


def test_wait_for_output_OutputButNoMarkerAndGone_StillCrashed(tmp_path, monkeypatch, clock):
    out = tmp_path / "review.json"
    out.write_text("{}", encoding="ascii")
    monkeypatch.setattr(fleet, "find_session", lambda sid: None)
    result = fleet.wait_for_output("s1", out, 60, poll_seconds=10)
    assert result.outcome == fleet.CRASHED


def test_wait_for_output_MarkerFromBeforeCorrection_DoesNotCount(tmp_path, monkeypatch, clock):
    out = tmp_path / "review.json"
    fleet.done_marker(out).write_text("done", encoding="ascii")
    out.write_text("{}", encoding="ascii")
    later = max(out.stat().st_mtime, fleet.done_marker(out).stat().st_mtime) + 60
    monkeypatch.setattr(fleet, "find_session", lambda sid: None)
    result = fleet.wait_for_output("s1", out, 60, poll_seconds=10, written_after=later)
    assert result.outcome == fleet.CRASHED


def test_briefs_EverySessionWritesItsMarkerAfterSessionDone(tmp_path):
    import briefs
    out = tmp_path / "review-r1.json"
    text = briefs.reviewer_brief(repo=tmp_path, base="a", head="b", intent=tmp_path / "i.md",
                                 diff=tmp_path / "d.patch", decisions=[], output=out,
                                 repo_rules=[], first_reviewed_head=None)
    done_at = text.index("cc-devthrottle session done")
    assert text.index(str(fleet.done_marker(out))) > done_at
    fix = briefs.correction_brief(out, ["x"], 1, "reviewer")
    assert str(fleet.done_marker(out)) in fix
    verify_out = tmp_path / "verify.json"
    vtext = briefs.verifier_brief(repo=tmp_path, intent=tmp_path / "i.md", preview_url=None,
                                  browser_state=None, evidence_dir=tmp_path, output=verify_out)
    assert str(fleet.done_marker(verify_out)) in vtext
