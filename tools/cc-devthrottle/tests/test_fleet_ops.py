"""Tests for `cc-devthrottle fleet`: the Fleet Manager's stored news, preferences and digest.

The Gateway is faked in memory, applying the filters the real routes apply (the routes themselves are
proven by the Gateway's own tests, FleetManagerEndpointsTests). What these pin is the command line's
half of the contract, to the AXI standard (docs/axi-standard.md):

  * every record can be read back EXACTLY from the default output - full id, title, kind, status -
    however awkward the title;
  * `--json` is the Gateway's answer, with every filter applied;
  * an empty answer says `count: 0`, and a filtered one says how many there are in all;
  * a wrong closed value fails with exit 2 and names the valid values, before anything is sent;
  * the owner's words travel exactly as given;
  * everything printed is ASCII.
"""

import json
import sys
import urllib.parse
import uuid
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared import gateway as shared_gateway  # noqa: E402
from src import fleet_ops, session_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

ME = "10000000-0000-4000-8000-000000000001"
WORKER = "2b7e0000-0000-4000-8000-000000000002"
FORMER = "3c8f0000-0000-4000-8000-000000000003"


# ---- the fake Gateway -------------------------------------------------------------------------------


class FakeGateway:
    """An in-memory stand-in for /gateway/fleet-manager, with the real routes' filters."""

    def __init__(self):
        self.outcomes = []
        self.preferences = []
        self.digest = None
        self.calls = []

    def add(self, kind, title, status="open", created="2026-09-16T12:00:00Z", **extra):
        record = {
            "id": str(uuid.uuid4()),
            "kind": kind,
            "title": title,
            "status": status,
            "filedBy": ME,
            "sessionId": None,
            "createdAtUtc": created,
            "ready": None,
            "finding": None,
            "decision": None,
            "answeredAtUtc": None,
            "answer": None,
            "answeredBy": None,
            "answeredByRole": None,
            "answerMatchedOption": None,
        }
        record.update(extra)
        # Newest first, as the route answers.
        self.outcomes.insert(0, record)
        return record

    def get_json(self, path, timeout=30):
        self.calls.append(("GET", path, None))
        parsed = urllib.parse.urlparse(path)
        query = dict(urllib.parse.parse_qsl(parsed.query))
        route = parsed.path
        if route == "gateway/fleet-manager/outcomes":
            status = query.get("status", "open")
            rows = [o for o in self.outcomes if status == "all" or o["status"] == status]
            if "kind" in query:
                rows = [o for o in rows if o["kind"] == query["kind"]]
            total = len(rows)
            rows = rows[: int(query.get("count", "50"))]
            return {"count": len(rows), "total": total, "outcomes": rows}
        if route.startswith("gateway/fleet-manager/outcomes/"):
            oid = route.rsplit("/", 1)[1]
            for o in self.outcomes:
                if o["id"] == oid:
                    return o
            raise shared_gateway.GatewayError(f"no outcome {oid} in this account", status=404)
        if route == "gateway/fleet-manager/preferences":
            return {"count": len(self.preferences), "preferences": self.preferences}
        if route == "gateway/fleet-manager/digest":
            assert self.digest is not None, "the test did not set a digest"
            return dict(self.digest, sessionId=query["session"])
        raise AssertionError(f"unexpected GET {path}")

    def post_json(self, path, body=None, timeout=30):
        self.calls.append(("POST", path, body))
        if path == "gateway/fleet-manager/outcomes":
            extra = {body["kind"]: body[body["kind"]], "sessionId": body.get("sessionId")}
            return self.add(body["kind"], body["title"], **extra)
        if path.endswith("/answer"):
            oid = path.split("/")[-2]
            record = next(o for o in self.outcomes if o["id"] == oid)
            if record["status"] == "answered":
                raise shared_gateway.GatewayError(
                    f"outcome {oid} was already answered; an answer is final and was not changed", status=409)
            record.update(status="answered", answer=body["answer"], answeredBy=ME, answeredByRole="fleet-manager",
                          answeredAtUtc="2026-09-16T12:05:00Z")
            if record["kind"] == "decision":
                record["answerMatchedOption"] = body["answer"].strip().lower() in [
                    o.lower() for o in record["decision"]["options"]]
            return record
        if path == "gateway/fleet-manager/preferences":
            pref = {"id": str(uuid.uuid4()), "text": body["text"], "createdAtUtc": "2026-09-16T12:00:00Z",
                    "createdBy": ME}
            self.preferences.append(pref)
            return pref
        raise AssertionError(f"unexpected POST {path}")

    def delete(self, path, timeout=30):
        self.calls.append(("DELETE", path, None))
        pid = path.rsplit("/", 1)[1]
        before = len(self.preferences)
        self.preferences = [p for p in self.preferences if p["id"] != pid]
        if len(self.preferences) == before:
            raise shared_gateway.GatewayError(f"no preference {pid} in this account", status=404)
        return {"deleted": True, "id": pid}


@pytest.fixture
def gw(monkeypatch):
    fake = FakeGateway()
    monkeypatch.setenv("CC_SESSION_ID", ME)
    monkeypatch.setattr(fleet_ops.gateway, "get_json", fake.get_json)
    monkeypatch.setattr(fleet_ops.gateway, "post_json", fake.post_json)
    monkeypatch.setattr(fleet_ops.gateway, "delete", fake.delete)
    monkeypatch.setattr(
        session_ops, "_get_fleet",
        lambda: ([{"sessionId": ME, "name": "Fleet Manager"},
                  {"sessionId": WORKER, "name": "Docs - fix the typo"}], True, None, None),
    )
    return fake


# ---- reading the compact output back ----------------------------------------------------------------


def split_row(line):
    """Split one compact row into its values, reading a JSON-string value back exactly."""
    values, i, text = [], 0, line.strip()
    while i <= len(text):
        if i < len(text) and text[i] == '"':
            decoder = json.JSONDecoder()
            value, end = decoder.raw_decode(text, i)
            values.append(value)
            i = end + 1
        else:
            end = text.find(",", i)
            end = len(text) if end == -1 else end
            raw = text[i:end]
            values.append(None if raw == "-" else raw)
            i = end + 1
    return values


def read_table(output, name):
    """The rows of `name[N]{fields}:` as dicts, and N."""
    lines = output.splitlines()
    for idx, line in enumerate(lines):
        if line.startswith(f"{name}[") and line.endswith(":") and "{" in line:
            n = int(line[len(name) + 1: line.index("]")])
            fields = line[line.index("{") + 1: line.index("}")].split(",")
            rows = [dict(zip(fields, split_row(r))) for r in lines[idx + 1: idx + 1 + n]]
            return n, rows
    raise AssertionError(f"no {name} table in:\n{output}")


AWKWARD_TITLES = [
    "Plain title",
    'Title, with a comma and "quotes"',
    "Ends with a backslash \\",
    "Café menu - non-ASCII",
    "  leading and trailing spaces  ",
    "-",
    "",
]


# ---- filing -----------------------------------------------------------------------------------------


def test_ready_posts_the_typed_record_and_names_the_next_commands(gw):
    result = runner.invoke(app, [
        "fleet", "ready", "Roster sort fix is ready",
        "--pr", "https://github.com/example/product/pull/12",
        "--risk", "LOW", "--checks", "passed",
        "--tested", "Unit tests, and by hand.", "--reviewed-by", "A second session",
        "--change", "The roster keeps its order.", "--session", WORKER[:8],
    ])

    assert result.exit_code == 0, result.output
    method, path, body = gw.calls[-1]
    assert (method, path) == ("POST", "gateway/fleet-manager/outcomes")
    assert body == {
        "kind": "ready",
        "title": "Roster sort fix is ready",
        "sessionId": WORKER,
        "ready": {
            "pullRequest": "https://github.com/example/product/pull/12",
            "risk": "low",
            "checks": "passed",
            "tested": "Unit tests, and by hand.",
            "reviewedBy": "A second session",
            "change": "The roster keeps its order.",
        },
    }
    oid = gw.outcomes[0]["id"]
    assert f"filed: {oid}" in result.output
    assert f"cc-devthrottle fleet show {oid}" in result.output


@pytest.mark.parametrize("flag, value, valid", [
    ("--risk", "tiny", "low, medium, high"),
    ("--checks", "green", "passed, failed, none"),
])
def test_ready_with_an_unknown_value_is_a_usage_error_and_sends_nothing(gw, flag, value, valid):
    args = {"--risk": "low", "--checks": "passed"}
    args[flag] = value
    result = runner.invoke(app, [
        "fleet", "ready", "t", "--pr", "https://x.example/1", "--risk", args["--risk"],
        "--checks", args["--checks"], "--tested", "t", "--reviewed-by", "r", "--change", "c",
    ])

    assert result.exit_code == 2
    assert f"{flag} '{value}' is not valid; use one of: {valid}" in result.output
    assert gw.calls == []


def test_finding_carries_every_link(gw):
    result = runner.invoke(app, [
        "fleet", "finding", "Why the nightly run was slow", "--answer", "A missing index.",
        "--reason", "The plan shows a full scan.",
        "--link", "https://example.com/a", "--link", "https://example.com/b",
    ])

    assert result.exit_code == 0, result.output
    body = gw.calls[-1][2]
    assert body["finding"] == {
        "answer": "A missing index.",
        "reason": "The plan shows a full scan.",
        "links": ["https://example.com/a", "https://example.com/b"],
    }
    assert body["sessionId"] is None


def test_decision_posts_the_options_in_order(gw):
    result = runner.invoke(app, [
        "fleet", "decision", "Which channel", "--question", "Beta or stable?",
        "--option", "Beta", "--option", "Stable", "--recommend", "Beta", "--why", "Untried elsewhere.",
    ])

    assert result.exit_code == 0, result.output
    assert gw.calls[-1][2]["decision"] == {
        "question": "Beta or stable?", "options": ["Beta", "Stable"],
        "recommended": "Beta", "why": "Untried elsewhere.",
    }


@pytest.mark.parametrize("extra, message", [
    (["--option", "Only"], "a decision needs at least two --option values, got 1"),
    (["--option", "A", "--option", "B", "--recommend", "C"], "--recommend 'C' is not one of the options"),
])
def test_decision_that_cannot_be_answered_is_a_usage_error(gw, extra, message):
    result = runner.invoke(app, ["fleet", "decision", "t", "--question", "q", *extra])

    assert result.exit_code == 2
    assert message in result.output
    assert gw.calls == []


# ---- listing ----------------------------------------------------------------------------------------


def test_every_record_reads_back_exactly_from_the_default_output(gw):
    """THE RECOVERABILITY TEST. Full id, title, kind and status come back exactly, however awkward."""
    for i, title in enumerate(AWKWARD_TITLES):
        gw.add(("ready", "finding", "decision")[i % 3], title)

    result = runner.invoke(app, ["fleet", "outcomes"])

    assert result.exit_code == 0, result.output
    n, rows = read_table(result.output, "outcomes")
    assert n == len(AWKWARD_TITLES)
    expected = [{"id": o["id"], "kind": o["kind"], "status": o["status"], "title": o["title"]} for o in gw.outcomes]
    assert rows == expected
    assert result.output.startswith(f"count: {n} (")


def test_json_is_the_gateways_answer_with_every_filter_applied(gw):
    gw.add("ready", "open ready")
    gw.add("decision", "open decision")
    answered = gw.add("ready", "answered ready", status="answered")

    unfiltered = runner.invoke(app, ["fleet", "outcomes", "--json"])
    filtered = runner.invoke(app, ["fleet", "outcomes", "--status", "answered", "--kind", "ready", "--json"])

    assert unfiltered.exit_code == 0 and filtered.exit_code == 0
    assert json.loads(unfiltered.output) == {"count": 2, "total": 2, "outcomes": [o for o in gw.outcomes if o["status"] == "open"]}
    assert json.loads(filtered.output) == {"count": 1, "total": 1, "outcomes": [answered]}
    # The filters went to the Gateway; nothing was filtered only on this side.
    query = dict(urllib.parse.parse_qsl(urllib.parse.urlparse(gw.calls[-1][1]).query))
    assert query == {"status": "answered", "kind": "ready", "count": "50"}


def test_an_empty_list_says_count_zero(gw):
    result = runner.invoke(app, ["fleet", "outcomes", "--status", "all"])

    assert result.exit_code == 0
    assert result.output.splitlines()[0] == "count: 0"


def test_an_empty_filtered_list_says_how_many_there_are_in_all(gw):
    gw.add("ready", "answered", status="answered")

    result = runner.invoke(app, ["fleet", "outcomes", "--kind", "decision"])

    assert result.exit_code == 0
    assert result.output.splitlines()[0] == "count: 0 of 1 total (status open, kind decision)"
    assert "cc-devthrottle fleet outcomes --status all" in result.output


@pytest.mark.parametrize("args, message", [
    (["--status", "closed"], "--status 'closed' is not valid; use one of: open, answered, all"),
    (["--kind", "news"], "--kind 'news' is not valid; use one of: ready, finding, decision"),
    (["--count", "0"], "--count must be between 1 and 200"),
])
def test_an_unknown_filter_is_a_usage_error(gw, args, message):
    result = runner.invoke(app, ["fleet", "outcomes", *args])

    assert result.exit_code == 2
    assert message in result.output
    assert gw.calls == []


def test_an_unknown_flag_fails_loudly(gw):
    result = runner.invoke(app, ["fleet", "outcomes", "--state", "open"])

    assert result.exit_code == 2


# ---- show and answer --------------------------------------------------------------------------------


def test_show_accepts_the_start_of_an_id_and_prints_the_decision(gw):
    record = gw.add("decision", "Which channel", decision={
        "question": "Beta or stable?", "options": ["Beta", "Stable, later"], "recommended": "Beta", "why": None})

    result = runner.invoke(app, ["fleet", "show", record["id"][:8]])

    assert result.exit_code == 0, result.output
    assert f"id: {record['id']}" in result.output
    assert 'options[2]: Beta,"Stable, later"' in result.output
    assert f'cc-devthrottle fleet answer {record["id"]}' in result.output


def test_show_an_ambiguous_start_names_every_match(gw):
    a = gw.add("ready", "a", id="abcd0000-0000-4000-8000-000000000001")
    b = gw.add("ready", "b", id="abcd0000-0000-4000-8000-000000000002")

    result = runner.invoke(app, ["fleet", "show", "abcd"])

    assert result.exit_code == 1
    assert a["id"] in result.output and b["id"] in result.output


def test_answer_sends_the_owners_words_exactly(gw):
    record = gw.add("decision", "Which channel", decision={
        "question": "q", "options": ["Beta", "Stable"], "recommended": None, "why": None})
    words = "  Stable - but only after the demo, \"not before\"  "

    result = runner.invoke(app, ["fleet", "answer", record["id"], words])

    assert result.exit_code == 0, result.output
    assert gw.calls[-1] == ("POST", f"gateway/fleet-manager/outcomes/{record['id']}/answer", {"answer": words})
    assert f"answered: {record['id']}" in result.output
    assert "answerMatchedOption: no" in result.output


def test_answering_an_answered_record_fails_with_the_gateways_reason(gw):
    record = gw.add("ready", "done", status="answered")

    result = runner.invoke(app, ["fleet", "answer", record["id"], "Merge it"])

    assert result.exit_code == 1
    assert "already answered" in result.output


# ---- preferences ------------------------------------------------------------------------------------


def test_prefer_keeps_the_words_exactly_and_they_read_back(gw):
    words = "stop asking me about draft posts, just stage them"

    kept = runner.invoke(app, ["fleet", "prefer", words])
    listed = runner.invoke(app, ["fleet", "preferences"])

    assert kept.exit_code == 0 and listed.exit_code == 0
    assert gw.calls[0][2] == {"text": words}
    n, rows = read_table(listed.output, "preferences")
    assert n == 1
    assert rows == [{"id": gw.preferences[0]["id"], "text": words}]
    assert listed.output.splitlines()[0] == "count: 1"


def test_no_preferences_says_count_zero(gw):
    result = runner.invoke(app, ["fleet", "preferences"])

    assert result.exit_code == 0
    assert result.output.splitlines()[0] == "count: 0"


def test_forget_removes_by_the_start_of_the_id(gw):
    runner.invoke(app, ["fleet", "prefer", "merge docs on green"])
    pid = gw.preferences[0]["id"]

    result = runner.invoke(app, ["fleet", "forget", pid[:8]])

    assert result.exit_code == 0, result.output
    assert f"forgot: {pid}" in result.output
    assert gw.preferences == []


# ---- digest -----------------------------------------------------------------------------------------


def _digest(**overrides):
    d = {
        "isFleetManager": True,
        "generatedAtUtc": "2026-09-16T12:00:00Z",
        "outcomes": [],
        "ownedSessions": [],
        "preferences": [],
        "outcomeCounts": {"ready": 0, "finding": 0, "decision": 0, "total": 0},
        "ownedSessionCounts": {"needsYou": 0, "working": 0, "stopped": 0, "total": 0},
        "fleetManagerSessionId": ME,
        "fleetManagerSessionIds": [ME],
    }
    d.update(overrides)
    return d


def test_digest_defaults_to_this_session_and_reads_back(gw):
    record = gw.add("decision", "Which channel, beta or stable")
    gw.digest = _digest(
        outcomes=[record],
        outcomeCounts={"ready": 0, "finding": 0, "decision": 1, "total": 1},
        ownedSessions=[
            {"sessionId": WORKER, "ownerSessionId": FORMER, "name": "Docs - fix the typo, then publish",
             "state": "stopped", "stateLabel": "Snoozed", "missionName": None, "uncommittedCount": 0,
             "turnVerdict": {"verdict": "needed-you", "label": "Asks whether to publish"}},
        ],
        ownedSessionCounts={"needsYou": 0, "working": 0, "stopped": 1, "total": 1},
        preferences=[{"id": "p-1", "text": "merge docs on green", "createdAtUtc": "x", "createdBy": "owner"}],
        fleetManagerSessionIds=[FORMER, ME],
    )

    result = runner.invoke(app, ["fleet", "digest"])

    assert result.exit_code == 0, result.output
    assert gw.calls[-1][1] == f"gateway/fleet-manager/digest?session={ME}"
    lines = result.output.splitlines()
    assert lines[0] == f"session: {ME}"
    assert lines[1] == "fleetManager: yes"
    assert lines[2] == f"markedFleetManager: {ME}"
    assert lines[3] == f"fleetManagerSessions[2]: {FORMER},{ME}"
    assert "outcomes: 1 open (ready 0, finding 0, decision 1)" in lines
    assert "sessions: 1 owned (needs-you 0, working 0, stopped 1)" in lines
    _, outcomes = read_table(result.output, "outcomes")
    assert outcomes == [{"id": record["id"], "kind": "decision", "title": "Which channel, beta or stable"}]
    _, sessions = read_table(result.output, "sessions")
    assert sessions == [{"id": WORKER, "name": "Docs - fix the typo, then publish", "state": "stopped",
                         "owner": FORMER, "verdict": "needed-you", "label": "Asks whether to publish"}]
    _, prefs = read_table(result.output, "preferences")
    assert prefs == [{"id": "p-1", "text": "merge docs on green"}]


def test_digest_session_flag_uses_the_shared_resolver(gw):
    gw.digest = _digest(isFleetManager=False)

    result = runner.invoke(app, ["fleet", "digest", "--session", "Docs - fix the typo"])

    assert result.exit_code == 0, result.output
    assert gw.calls[-1][1] == f"gateway/fleet-manager/digest?session={WORKER}"
    assert "fleetManager: no" in result.output
    assert "sessions: 0 owned (needs-you 0, working 0, stopped 0)" in result.output
    assert "preferences: 0" in result.output


def test_digest_whose_records_and_counts_disagree_fails_rather_than_print_a_part(gw):
    record = gw.add("finding", "One of two")
    gw.digest = _digest(outcomes=[record], outcomeCounts={"ready": 0, "finding": 2, "decision": 0, "total": 2})

    for args in (["fleet", "digest"], ["fleet", "digest", "--json"]):
        result = runner.invoke(app, args)

        assert result.exit_code == 1
        assert "the digest carried 1 open records but the Gateway counts 2" in result.output


def test_a_page_smaller_than_the_total_says_so(gw):
    for i in range(3):
        gw.add("ready", f"Ready {i}")

    result = runner.invoke(app, ["fleet", "outcomes", "--count", "2"])

    assert result.exit_code == 0, result.output
    assert result.output.splitlines()[0] == "count: 2 of 3 (ready 2, finding 0, decision 0) status: open"
    assert "cc-devthrottle fleet digest" in result.output


def test_the_start_of_an_id_beyond_the_newest_page_fails_naming_the_limit(gw):
    old = gw.add("ready", "the oldest", id="ffff0000-0000-4000-8000-000000000001")
    for i in range(200):
        gw.add("ready", f"Ready {i}", id=f"0000{i:04d}-0000-4000-8000-000000000000")

    result = runner.invoke(app, ["fleet", "show", old["id"][:6]])

    assert result.exit_code == 1
    assert "among the newest 200 of 201 records; give the full id" in result.output
    assert runner.invoke(app, ["fleet", "show", old["id"]]).exit_code == 0


def test_answer_says_who_answered(gw):
    record = gw.add("ready", "Merge the roster fix")

    result = runner.invoke(app, ["fleet", "answer", record["id"], "Merge it"])

    assert "answeredByRole: fleet-manager" in result.output


def test_digest_json_is_the_gateways_answer(gw):
    gw.digest = _digest()

    result = runner.invoke(app, ["fleet", "digest", "--json"])

    assert json.loads(result.output) == dict(gw.digest, sessionId=ME)


# ---- the whole surface ------------------------------------------------------------------------------


def test_every_fleet_output_is_ascii(gw):
    for title in AWKWARD_TITLES:
        gw.add("ready", title)
    gw.digest = _digest(outcomes=list(gw.outcomes))
    runner.invoke(app, ["fleet", "prefer", "café — stage drafts"])

    outputs = [
        runner.invoke(app, ["fleet", "outcomes"]).output,
        runner.invoke(app, ["fleet", "outcomes", "--json"]).output,
        runner.invoke(app, ["fleet", "show", gw.outcomes[0]["id"]]).output,
        runner.invoke(app, ["fleet", "preferences"]).output,
        runner.invoke(app, ["fleet", "digest"]).output,
    ]
    for out in outputs:
        assert out.isascii(), out


def test_the_fleet_commands_are_discoverable_through_actions():
    result = runner.invoke(app, ["actions", "--json"])

    ids = {a["id"] for a in json.loads(result.output)["actions"]}
    assert {
        "fleet-digest", "fleet-ready", "fleet-finding", "fleet-decision", "fleet-outcomes",
        "fleet-show", "fleet-answer", "fleet-prefer", "fleet-preferences", "fleet-forget",
    } <= ids
