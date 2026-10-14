"""Tests for check_new_mail.py, with a fake Graph in place of the network.

Run:  python -m pytest docs/qa/factory-definition/phase-0/inventory/email-check -q
"""

import json
import os
import subprocess
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import check_new_mail as cnm  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
ENV = {"OUTREACH_MAIL_TENANT_ID": "tenant", "OUTREACH_MAIL_CLIENT_ID": "client", "OUTREACH_MAIL_CLIENT_SECRET": "secret"}
NOW = cnm.datetime(2026, 10, 14, 12, 0, 0, tzinfo=cnm.timezone.utc)


class FakeGraph:
    """Answers the sign-in and the folder reads; records every call so a test can prove nothing else ran."""

    def __init__(self, folders):
        self.folders = folders  # folder -> list of messages as Graph returns them
        self.calls = []

    def __call__(self, method, url, headers=None, form=None):
        self.calls.append((method, url))
        if "login.microsoftonline.com" in url:
            return {"access_token": "token"}
        folder = url.split("/mailFolders/")[1].split("/")[0]
        since = cnm.urllib.parse.parse_qs(cnm.urllib.parse.urlsplit(url).query)["$filter"][0].split(" ge ")[1]
        return {"value": [m for m in self.folders.get(folder, []) if m["receivedDateTime"] >= since]}


def msg(mid, when, sender, subject):
    return {"id": mid, "receivedDateTime": when, "from": {"emailAddress": {"address": sender}}, "subject": subject}


@pytest.fixture
def setup(tmp_path, monkeypatch):
    ledger = tmp_path / "ledger.json"
    ledger.write_text(json.dumps({"people": [
        {"address": "partner@firm-a.example", "sent": "2026-10-07"},
        {"address": "cto@firm-b.example", "sent": "2026-10-08"},
        {"address": "never-written@firm-c.example", "sent": None},
    ]}), encoding="utf-8")
    rules = {"mailbox": "outreach@example.com", "folders": ["inbox", "junkemail", "archive"], "ledger": str(ledger),
             "bounceSubjectMarkers": ["Undeliverable"], "firstRunLookbackHours": 24}
    state = tmp_path / "state.json"
    state.write_text("", encoding="utf-8")

    def go(folders):
        fake = FakeGraph(folders)
        monkeypatch.setattr(cnm, "http_json", fake)
        return cnm.run(rules, ENV, str(state), NOW), fake

    return go, state


def test_run_EmptyInbox_CountsZero(setup):
    go, _ = setup
    result, _ = go({})
    assert result == {"count": 0, "context": ""}


def test_run_ReplyFromSomeoneWeWroteTo_CountsItWithSenderAndSubject(setup):
    go, _ = setup
    result, _ = go({"inbox": [msg("1", "2026-10-14T09:00:00Z", "Partner@Firm-A.example", "Re: your note")]})
    assert result["count"] == 1
    assert "partner@firm-a.example - Re: your note [inbox]" in result["context"]


def test_run_MailFromSomeoneNotInTheLedger_CountsZero(setup):
    go, _ = setup
    result, _ = go({"inbox": [msg("1", "2026-10-14T09:00:00Z", "newsletter@shop.example", "Sale"),
                              msg("2", "2026-10-14T09:05:00Z", "never-written@firm-c.example", "Hello")]})
    assert result["count"] == 0


def test_run_BounceInArchive_CountsIt(setup):
    go, _ = setup
    result, _ = go({"archive": [msg("9", "2026-10-14T08:00:00Z", "postmaster@mail.example", "Undeliverable: hi")]})
    assert result["count"] == 1
    assert "[archive]" in result["context"]


def test_run_SecondRun_RemembersTheFirstThroughTheStateFile(setup):
    go, state = setup
    inbox = {"inbox": [msg("1", "2026-10-14T09:00:00Z", "cto@firm-b.example", "Re: hello")]}
    first, _ = go(inbox)
    second, _ = go(inbox)
    assert first["count"] == 1
    assert second["count"] == 0
    assert json.loads(state.read_text(encoding="utf-8")) == {"newest": "2026-10-14T09:00:00Z", "idsAtNewest": ["1"]}


def test_run_NewMailAtTheSameSecondAsTheLastCounted_IsStillCounted(setup):
    go, _ = setup
    go({"inbox": [msg("1", "2026-10-14T09:00:00Z", "cto@firm-b.example", "Re: one")]})
    result, _ = go({"inbox": [msg("1", "2026-10-14T09:00:00Z", "cto@firm-b.example", "Re: one"),
                              msg("2", "2026-10-14T09:00:00Z", "partner@firm-a.example", "Re: two")]})
    assert result["count"] == 1
    assert "Re: two" in result["context"]


def test_run_FirstRun_LooksBackTheConfiguredHoursOnly(setup):
    go, _ = setup
    result, fake = go({"inbox": [msg("old", "2026-10-12T09:00:00Z", "cto@firm-b.example", "Re: old")]})
    assert result["count"] == 0
    assert "receivedDateTime+ge+2026-10-13T12%3A00%3A00Z" in fake.calls[1][1]


def test_run_ManyMessages_ContextStaysWithinEightKilobytes(setup):
    go, _ = setup
    many = [msg(str(i), f"2026-10-14T09:{i // 60:02d}:{i % 60:02d}Z", "cto@firm-b.example", "Re: " + "x" * 120)
            for i in range(200)]
    result, _ = go({"inbox": many})
    assert result["count"] == 200
    assert len(result["context"].encode("utf-8")) <= cnm.CONTEXT_CAP_BYTES
    assert result["context"].endswith("more not shown)")


def test_run_ReadsOnly_EveryMailCallIsAGetAndNoBodyIsAskedFor(setup):
    go, _ = setup
    _, fake = go({"inbox": [msg("1", "2026-10-14T09:00:00Z", "cto@firm-b.example", "Re: hello")]})
    mail_calls = [c for c in fake.calls if "graph.microsoft.com" in c[1]]
    assert len(mail_calls) == 3
    assert all(method == "GET" for method, _ in mail_calls)
    assert all("%24select=id%2CreceivedDateTime%2Cfrom%2Csubject" in url for _, url in mail_calls)
    assert [m for m, u in fake.calls if m != "GET"] == ["POST"]  # the sign-in, nothing else


def test_run_MissingSecretName_FailsNamingIt(setup, monkeypatch):
    _, state = setup
    rules = {"mailbox": "m", "folders": [], "ledger": "x", "bounceSubjectMarkers": [], "firstRunLookbackHours": 1}
    with pytest.raises(cnm.CheckFailed, match="OUTREACH_MAIL_CLIENT_SECRET"):
        cnm.run(rules, {k: v for k, v in ENV.items() if k != "OUTREACH_MAIL_CLIENT_SECRET"}, str(state), NOW)


def test_main_NoStateFileNamed_ExitsNonZeroWithReason(tmp_path):
    env = {k: v for k, v in os.environ.items() if k != "CC_CHECK_STATE_FILE"}
    p = subprocess.run([sys.executable, os.path.join(HERE, "check_new_mail.py")], capture_output=True, text=True, env=env)
    assert p.returncode == 1
    assert p.stdout == ""
    assert "CC_CHECK_STATE_FILE is not set" in p.stderr


def test_main_SecretsMissing_ExitsNonZeroAndPrintsNothingOnStandardOutput(tmp_path):
    state = tmp_path / "state.json"
    state.write_text("", encoding="utf-8")
    env = {k: v for k, v in os.environ.items() if not k.startswith("OUTREACH_MAIL_")}
    env["CC_CHECK_STATE_FILE"] = str(state)
    p = subprocess.run([sys.executable, os.path.join(HERE, "check_new_mail.py")], capture_output=True, text=True, env=env)
    assert p.returncode == 1
    assert p.stdout == ""
    assert "not given: OUTREACH_MAIL_TENANT_ID, OUTREACH_MAIL_CLIENT_ID, OUTREACH_MAIL_CLIENT_SECRET" in p.stderr


def test_redact_SenderAndSubject_KeepsOnlyTheDomainAndABounceMarker():
    import run_email_check as rec
    context = ("2026-10-14T09:00:00Z from partner@firm-a.example - Re: your note [inbox]\n"
               "2026-10-14T09:01:00Z from postmaster@mail.example - Undeliverable: hello Pat [archive]\n"
               "(3 more not shown)")
    assert rec.redact(context).splitlines() == [
        "2026-10-14T09:00:00Z from <someone>@firm-a.example - <subject withheld> [inbox]",
        "2026-10-14T09:01:00Z from <someone>@mail.example - Undeliverable: <rest withheld> [archive]",
        "(3 more not shown)",
    ]
