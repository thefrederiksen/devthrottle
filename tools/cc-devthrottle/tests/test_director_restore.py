"""`cc-devthrottle director restore` (the Message Load mission, slice 6).

The DIRECTOR restores a drained fleet; this command asks for it and reads each seat's answer back from the
workspace. The Gateway is stubbed: these prove what the command sends (never an owner), how it tells a new
answer from an old one, and that one failed seat makes the exit code say so.
"""

import json
import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src.cli import app  # noqa: E402
from src import machine_ops  # noqa: E402

runner = CliRunner()

MANAGER = "11111111-1111-4111-8111-111111111111"
WORKER = "22222222-2222-4222-8222-222222222222"
NEW_DIRECTOR = "9d0c7a55-0000-4000-8000-000000000001"


def _seat(sid, name, restored=None, failure=None, attempted=None):
    restore = {"decision": "restore"}
    if failure is not None:
        restore["failure"] = failure
    if attempted is not None:
        restore["attemptedAtUtc"] = attempted
    return {"sessionId": sid, "name": name, "restoredSessionId": restored, "restore": restore}


class FakeGateway:
    """The workspace as successive reads return it, and every POST the command made."""

    def __init__(self, reads, accepted=None):
        self.reads = list(reads)
        self.posts = []
        self.accepted = accepted if accepted is not None else {
            "taken": True, "workspaceId": "drain-1", "directorId": NEW_DIRECTOR, "seats": [MANAGER, WORKER]}

    def get_json(self, path, timeout=30):
        assert path == "gateway/workspaces/drain-1"
        doc = self.reads[0] if len(self.reads) == 1 else self.reads.pop(0)
        return {"id": "drain-1", "seats": doc}

    def post_json(self, path, body=None, timeout=30):
        self.posts.append((path, body))
        if isinstance(self.accepted, Exception):
            raise self.accepted
        return self.accepted


@pytest.fixture
def fake(monkeypatch):
    def install(reads, accepted=None):
        gw = FakeGateway(reads, accepted)
        monkeypatch.setattr(machine_ops.gateway, "get_json", gw.get_json)
        monkeypatch.setattr(machine_ops.gateway, "post_json", gw.post_json)
        monkeypatch.setattr(machine_ops, "_sleep", lambda s: None)
        return gw
    return install


def _run(*extra):
    return runner.invoke(app, ["director", "restore", "drain-1", "--director", NEW_DIRECTOR, *extra])


def test_restore_posts_the_director_and_names_no_owner(fake):
    gw = fake([
        [_seat(MANAGER, "M - Manager"), _seat(WORKER, "M - Worker")],
        [_seat(MANAGER, "M - Manager", restored="new-m", attempted="t1"),
         _seat(WORKER, "M - Worker", restored="new-w", attempted="t1")],
    ])

    result = _run("--seed", f"{WORKER}=/index/SEED-w.md")

    assert result.exit_code == 0, result.output
    path, body = gw.posts[0]
    assert path == "gateway/workspaces/drain-1/restore"
    assert body == {"directorId": NEW_DIRECTOR, "seeds": {WORKER: "/index/SEED-w.md"}}
    assert "controlled" not in json.dumps(body).lower()
    assert "owner" not in json.dumps(body).lower()
    assert "RESTORED" in result.output and "new-w" in result.output


def test_one_failed_seat_is_reported_and_the_exit_code_says_so(fake):
    fake([
        [_seat(MANAGER, "M - Manager"), _seat(WORKER, "M - Worker")],
        [_seat(MANAGER, "M - Manager", failure="repository not found", attempted="t1"),
         _seat(WORKER, "M - Worker", failure="its owner could not be brought back", attempted="t1")],
    ])

    result = _run("--json")

    assert result.exit_code == 1
    rows = {r["sessionId"]: r for r in json.loads(result.output)["seats"]}
    assert rows[MANAGER]["outcome"] == "failed"
    assert rows[MANAGER]["failure"] == "repository not found"
    assert rows[WORKER]["outcome"] == "failed"


def test_an_old_failure_is_not_this_runs_answer(fake):
    # The worker failed in an EARLIER run (attempt stamp t0). Until the Director writes a new stamp, that is not
    # an answer to this restore, and reading it as one would report a failure that has not happened.
    fake([
        [_seat(MANAGER, "M - Manager"), _seat(WORKER, "M - Worker", failure="old", attempted="t0")],
        [_seat(MANAGER, "M - Manager", restored="new-m", attempted="t1"),
         _seat(WORKER, "M - Worker", failure="old", attempted="t0")],
        [_seat(MANAGER, "M - Manager", restored="new-m", attempted="t1"),
         _seat(WORKER, "M - Worker", restored="new-w", attempted="t2")],
    ])

    result = _run("--json")

    assert result.exit_code == 0, result.output
    assert all(r["outcome"] == "restored" for r in json.loads(result.output)["seats"])


def test_a_seat_with_no_answer_when_the_wait_runs_out_is_pending_and_the_exit_code_says_so(fake, monkeypatch):
    fake([[_seat(MANAGER, "M - Manager"), _seat(WORKER, "M - Worker")]])
    clock = iter([0.0, 1.0, 5.0, 11.0, 20.0])
    monkeypatch.setattr(machine_ops, "_monotonic", lambda: next(clock))

    result = _run("--wait-seconds", "10", "--json")

    assert result.exit_code == 1
    assert {r["outcome"] for r in json.loads(result.output)["seats"]} == {"pending"}


def test_the_placeholder_from_the_drain_is_refused_before_anything_is_sent(fake):
    gw = fake([[]])

    result = runner.invoke(app, ["director", "restore", "drain-1", "--director", "<the NEW director id>"])

    assert result.exit_code != 0
    assert "director list" in result.output
    assert gw.posts == []


def test_a_malformed_seed_is_a_usage_error(fake):
    gw = fake([[]])

    result = _run("--seed", "no-equals-sign")

    assert result.exit_code != 0
    assert gw.posts == []


def test_a_restore_the_gateway_refuses_says_nothing_was_restored(fake):
    fake([[_seat(MANAGER, "M - Manager")]],
         accepted=machine_ops.gateway.GatewayError("workspace 'drain-1' has no seat left to bring back"))

    result = _run()

    assert result.exit_code == 1
    assert "nothing was restored" in result.output
    assert "no seat left" in result.output


def test_no_wait_says_accepted_not_waited_reads_nothing_and_does_not_exit_0(fake, monkeypatch):
    # Inspection 7, ruling 6: exit 0 means every seat came back. A restore nobody waited on has not shown that.
    fake([[]])

    def no_read(path, timeout=30):
        raise AssertionError("a restore that does not wait must not read the workspace")

    monkeypatch.setattr(machine_ops.gateway, "get_json", no_read)

    as_json = _run("--wait-seconds", "0", "--json")
    as_text = _run("--wait-seconds", "0")

    assert as_json.exit_code == 3, as_json.output
    out = json.loads(as_json.output)
    assert out["taken"] is True and out["count"] == 2
    assert out["waited"] is False and out["status"] == "accepted, not waited"
    assert as_text.exit_code == 3, as_text.output
    assert "accepted, not waited" in as_text.output
    assert "TAKEN" not in as_text.output


def test_the_help_says_what_each_exit_code_means():
    result = runner.invoke(app, ["director", "restore", "--help"])

    text = " ".join(result.output.split())
    assert "Exit 0 means every seat asked for came back" in text
    assert "Exit 3" in text and "accepted, not waited" in text


def test_force_seat_is_sent_only_when_given(fake):
    gw = fake([[_seat(MANAGER, "M - Manager", restored="new-m", attempted="t1")]],
              accepted={"taken": True, "workspaceId": "drain-1", "directorId": NEW_DIRECTOR, "seats": [MANAGER]})

    _run("--json")
    _run("--force-seat", MANAGER, "--json")

    assert "forceSeats" not in gw.posts[0][1]
    assert gw.posts[1][1]["forceSeats"] == [MANAGER]


def test_director_restore_is_in_the_action_catalogue_and_changes_state():
    result = runner.invoke(app, ["actions", "--json"])
    actions = {a["id"]: a for a in json.loads(result.output)["actions"]}

    assert actions["director-restore"]["mutatesState"] is True
    assert "--controlled-by" not in actions["director-restore"]["command"]
