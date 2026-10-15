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
# The entries the rules name for each part, and where the Director said each arrived. The client secret's own
# variable differs from the upper-cased entry name, as 11 of 122 entries on the owner's machine do.
SECRETS = {"tenantId": "rig-tenant", "clientId": "rig-client", "clientSecret": "rig-secret"}
VARS = {"rig-tenant": "RIG_TENANT", "rig-client": "RIG_CLIENT", "rig-secret": "RIG_SECRET_PASSWORD"}
ENV = {"CC_CHECK_SECRET_VARS": json.dumps(VARS), "RIG_TENANT": "tenant", "RIG_CLIENT": "client",
       "RIG_SECRET_PASSWORD": "secret"}
NOW = cnm.datetime(2026, 10, 14, 12, 0, 0, tzinfo=cnm.timezone.utc)


class FakeGraph:
    """Answers the sign-in and the folder reads; records every call so a test can prove nothing else ran."""

    def __init__(self, folders):
        self.folders = folders  # folder -> list of messages as Graph returns them
        self.calls = []
        self.forms = []

    def __call__(self, method, url, headers=None, form=None):
        self.calls.append((method, url))
        if "login.microsoftonline.com" in url:
            self.forms.append((url, form))
            return {"access_token": "token"}
        folder = url.split("/mailFolders/")[1].split("/")[0]
        query = cnm.urllib.parse.parse_qs(cnm.urllib.parse.urlsplit(url).query)
        since = query["$filter"][0].split(" ge ")[1]
        assert query["$orderby"] == ["receivedDateTime asc"]
        skip = int(query.get("$skip", ["0"])[0])
        matching = sorted((m for m in self.folders.get(folder, []) if m["receivedDateTime"] >= since),
                          key=lambda m: m["receivedDateTime"])
        answer = {"value": matching[skip:skip + cnm.PAGE_SIZE]}
        if skip + cnm.PAGE_SIZE < len(matching):  # Graph pages with an opaque next link; this one carries $skip
            answer["@odata.nextLink"] = url.split("&%24skip=")[0] + f"&%24skip={skip + cnm.PAGE_SIZE}"
        return answer


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
             "bounceSubjectMarkers": ["Undeliverable"], "firstRunLookbackHours": 24, "secrets": SECRETS}
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
    assert json.loads(state.read_text(encoding="utf-8"))["folders"]["inbox"] == {"newest": "2026-10-14T09:00:00Z", "idsAtNewest": ["1"]}


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


RULES_NO_MAIL = {"mailbox": "m", "folders": [], "ledger": "x", "bounceSubjectMarkers": [], "firstRunLookbackHours": 1,
                 "secrets": SECRETS}


def test_run_SecretsAreFoundThroughTheVariablesTheDirectorNamed(setup):
    go, _ = setup
    _, fake = go({})
    url, form = fake.forms[0]
    assert "/tenant/oauth2/" in url
    assert (form["client_id"], form["client_secret"]) == ("client", "secret")


def test_run_AnEntryTheStartDidNotDeclare_FailsNamingTheEntryAndItsPart(setup):
    _, state = setup
    env = dict(ENV, CC_CHECK_SECRET_VARS=json.dumps({k: v for k, v in VARS.items() if k != "rig-secret"}))
    with pytest.raises(cnm.CheckFailed, match=r"rig-secret \(clientSecret\) was not declared"):
        cnm.run(RULES_NO_MAIL, env, str(state), NOW)


def test_run_TheVariableTheDirectorNamedIsNotSet_FailsNamingIt(setup):
    _, state = setup
    env = {k: v for k, v in ENV.items() if k != "RIG_SECRET_PASSWORD"}
    with pytest.raises(cnm.CheckFailed, match=r"rig-secret \(clientSecret\) should be in RIG_SECRET_PASSWORD"):
        cnm.run(RULES_NO_MAIL, env, str(state), NOW)


def test_run_NoSecretVariables_Fails(setup):
    _, state = setup
    with pytest.raises(cnm.CheckFailed, match="CC_CHECK_SECRET_VARS is not set"):
        cnm.run(RULES_NO_MAIL, {k: v for k, v in ENV.items() if k != "CC_CHECK_SECRET_VARS"}, str(state), NOW)


def test_run_MoreThan500NewInOneFolder_CountsWhatItRead_AdvancesAndSaysMoreNotShown_ThenReadsOn(setup):
    go, state = setup
    inbox = [msg(str(i), f"2026-10-14T{8 + i // 3600:02d}:{i // 60 % 60:02d}:{i % 60:02d}Z", "cto@firm-b.example", "Re: hi")
             for i in range(520)]
    first, _ = go({"inbox": inbox})
    assert first["count"] == 500
    assert "(more not shown: inbox has more than 500 new messages; this run read up to 2026-10-14T08:08:19Z" in first["context"]
    assert json.loads(state.read_text(encoding="utf-8"))["folders"]["inbox"]["newest"] == "2026-10-14T08:08:19Z"

    second, _ = go({"inbox": inbox})
    assert second["count"] == 20
    assert "more not shown: inbox" not in second["context"]


def test_run_OneFolderCutShort_TheOtherFoldersAdvanceOnTheirOwn(setup):
    go, state = setup
    inbox = [msg(f"i{i}", f"2026-10-14T08:{i // 60:02d}:{i % 60:02d}Z", "cto@firm-b.example", "Re: hi") for i in range(510)]
    archive = [msg("a1", "2026-10-14T11:00:00Z", "partner@firm-a.example", "Re: later")]
    go({"inbox": inbox, "archive": archive})
    folders = json.loads(state.read_text(encoding="utf-8"))["folders"]
    assert folders["inbox"]["newest"] == "2026-10-14T08:08:19Z"
    assert folders["archive"]["newest"] == "2026-10-14T11:00:00Z"

    second, _ = go({"inbox": inbox, "archive": archive})
    assert second["count"] == 10  # the rest of the inbox; the archive message is not counted twice


def test_main_NoStateFileNamed_ExitsNonZeroWithReason(tmp_path):
    env = {k: v for k, v in os.environ.items() if k != "CC_CHECK_STATE_FILE"}
    p = subprocess.run([sys.executable, os.path.join(HERE, "check_new_mail.py")], capture_output=True, text=True, env=env)
    assert p.returncode == 1
    assert p.stdout == ""
    assert "CC_CHECK_STATE_FILE is not set" in p.stderr


def test_main_SecretsMissing_ExitsNonZeroAndPrintsNothingOnStandardOutput(tmp_path):
    state = tmp_path / "state.json"
    state.write_text("", encoding="utf-8")
    env = {k: v for k, v in os.environ.items() if k != "CC_CHECK_SECRET_VARS"}
    env["CC_CHECK_STATE_FILE"] = str(state)
    p = subprocess.run([sys.executable, os.path.join(HERE, "check_new_mail.py")], capture_output=True, text=True, env=env)
    assert p.returncode == 1
    assert p.stdout == ""
    assert "CC_CHECK_SECRET_VARS is not set" in p.stderr


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
