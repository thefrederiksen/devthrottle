"""dt-zoho's admin commands driven through the real command line, with only the network replaced (fakes.py and
fake_admin.py)."""

import json
from datetime import date, timedelta

from fake_admin import ZOID, ZUID
from fakes import ACCOUNT_ID

ME = "soren@devthrottle.com"
OTHER = "email@devthrottle.com"


def _puts(env):
    return [r for r in env.zoho.admin.requests if r[0] == "PUT"]


def _day(offset: int) -> str:
    return (date.today() + timedelta(days=offset)).isoformat()


# --------------------------------------------------------------------------------------------- read


def test_org_ReadsTheOrganisationFromTheAccountsZoid(env):
    shown = json.loads(env.ok("org", "--json").stdout)
    assert (shown["id"], shown["name"], shown["primary_domain"], shown["mailboxes"]) == (ZOID, "DevThrottle",
                                                                                      "devthrottle.com", 2)


def test_domains_AreListedReadOnly(env):
    shown = json.loads(env.ok("domains", "--json").stdout)
    assert shown["count"] == 2 and shown["domains"][0] == {"name": "devthrottle.com", "primary": True,
                                                           "verified": True, "mx": "enabled", "spf": True,
                                                           "dkim": True, "domain_alias": False}
    assert _puts(env) == []


def test_users_AskWithoutTheTrailingSlash(env):
    # With a trailing slash Zoho answers 404 URL_RULE_NOT_CONFIGURED.
    shown = json.loads(env.ok("users", "--json").stdout)
    assert [u["email"] for u in shown["users"]] == [OTHER, ME]
    assert [u["role"] for u in shown["users"]] == ["member", "super_admin"]
    assert any(r[1] == f"/api/organization/{ZOID}/accounts" for r in env.zoho.admin.requests)


def test_groups_None_SaysCountZero(env):
    result = env.ok("groups")
    assert "count: 0" in result.stdout
    assert json.loads(env.ok("groups", "--json").stdout) == {"count": 0, "groups": []}


def test_org_AccountWithoutZoid_IsOneSentence(env):
    env.zoho.accounts[0].pop("policyId")
    result = env.run("org")
    assert result.exit_code == 1 and "organisation id" in result.output and "Traceback" not in result.output


# --------------------------------------------------------------------------------------------- aliases


def test_alias_AddThenRemove_OnTheZuidAndReadBack(env):
    added = json.loads(env.ok("alias", "add", "dt-test@devthrottle.com", "--json").stdout)
    assert added["added"] and "dt-test@devthrottle.com" in added["aliases"]
    put = _puts(env)[-1]
    assert put[1] == f"/api/organization/{ZOID}/accounts/{ZUID}"
    assert put[3] == {"zuid": ZUID, "mode": "addEmailAlias", "emailAlias": ["dt-test@devthrottle.com"]}
    listed = json.loads(env.ok("alias", "list", "--json").stdout)
    assert listed["aliases"] == ["info@example.com", "dt-test@devthrottle.com"]
    removed = json.loads(env.ok("alias", "remove", "dt-test@devthrottle.com", "--json").stdout)
    assert removed["removed"] and removed["aliases"] == ["info@example.com"]
    assert _puts(env)[-1][3]["mode"] == "deleteEmailAlias"


def test_alias_AddOneTheAccountHas_IsRefusedBeforeAnyChange(env):
    result = env.run("alias", "add", ME)
    assert result.exit_code == 2 and "already" in result.output and _puts(env) == []


def test_alias_RemoveNotAnAlias_IsRefused(env):
    result = env.run("alias", "remove", "nobody@devthrottle.com")
    assert result.exit_code == 2 and "not an alias" in result.output and _puts(env) == []


def test_alias_RemoveThePrimaryAddress_IsRefused(env):
    result = env.run("alias", "remove", ME)
    assert result.exit_code == 2 and _puts(env) == []


def test_alias_ZohoRefusesTheDomain_IsOneSentence(env):
    result = env.run("alias", "add", "x@not-ours.org")
    assert result.exit_code == 1 and "DOMAIN_NOT_IN_ORG" in result.output and "Traceback" not in result.output


def test_alias_NotThereWhenReadBack_IsAnError(env, monkeypatch):
    monkeypatch.setattr(env.zoho.admin, "_admin_put", lambda rest, body: __import__("fake_admin")._ok())
    result = env.run("alias", "add", "dt-test@devthrottle.com")
    assert result.exit_code == 1 and "read back" in result.output


# --------------------------------------------------------------------------------------------- IMAP


def test_imap_OffThenOn_OnTheAccountIdAndReadBack(env):
    off = json.loads(env.ok("imap", "off", "--json").stdout)
    assert (off["imap_enabled"], off["was"]) == (False, True)
    put = _puts(env)[-1]
    assert put[1] == f"/api/organization/{ZOID}/accounts/{ACCOUNT_ID}"
    assert put[3] == {"zuid": ZUID, "mode": "updateIMAPStatus", "imapAccessEnabled": False}
    assert "imap_enabled: false" in env.ok("imap", "status").stdout.lower()
    on = json.loads(env.ok("imap", "on", "--json").stdout)
    assert (on["imap_enabled"], on["was"]) == (True, False)


def test_imap_On_WaitsForZohosAnswerToShowIt(env):
    # Live, switching on showed in the account answer only after 1.4 to 2 seconds; the fake lags three reads.
    env.ok("imap", "off")
    env.zoho.pauses.clear()
    env.ok("imap", "on")
    assert env.zoho.pauses == [0.5, 0.5, 0.5]


def test_imap_NotSwitchedWhenReadBack_IsAnError(env, monkeypatch):
    monkeypatch.setattr(env.zoho.admin, "_admin_put", lambda rest, body: __import__("fake_admin")._ok())
    result = env.run("imap", "off")
    assert result.exit_code == 1 and "still reads back as on after 30 reads" in result.output
    assert len(env.zoho.pauses) == 29


# --------------------------------------------------------------------------------------------- forwarding


def test_forwarding_AddListRemove_ShowsUnconfirmed(env):
    added = json.loads(env.ok("forwarding", "add", OTHER, "--json").stdout)
    assert added["forwards"] == [{"to": OTHER, "status": "UNCONFIRMED", "type": "USER"}]
    put = _puts(env)[-1]
    assert put[1] == f"/api/accounts/{ACCOUNT_ID}"
    assert put[3] == {"mode": "addMailForward", "isUserForward": True, "mailForward": [{"mailForwardTo": OTHER}]}
    assert "forwards nothing until it does" in env.ok("forwarding", "list").stdout
    removed = json.loads(env.ok("forwarding", "remove", OTHER, "--json").stdout)
    assert removed["count"] == 0 and _puts(env)[-1][3]["mode"] == "deleteMailForward"


def test_forwarding_TargetNotAllowed_IsRefusedBeforeAnyCall(env):
    # Zoho mails the target a confirmation, then every incoming message once it confirms.
    env.ok("allowed-recipients", "set", ME, OTHER)
    before = len(env.zoho.calls)
    result = env.run("forwarding", "add", "stranger@example.org")
    assert result.exit_code == 1 and "stranger@example.org" in result.output and "Nothing was sent" in result.output
    assert len(env.zoho.calls) == before


def test_forwarding_RemoveOneNotThere_IsRefused(env):
    result = env.run("forwarding", "remove", OTHER)
    assert result.exit_code == 2 and "does not forward" in result.output and _puts(env) == []


def test_forwarding_None_ListSaysCountZero(env):
    assert "count: 0" in env.ok("forwarding", "list").stdout


# --------------------------------------------------------------------------------------------- vacation


def test_vacation_SetUpdateRemove_AlwaysSendToAll(env):
    first, last = _day(2), _day(4)
    shown = json.loads(env.ok("vacation", "set", "--subject", "Away", "--content", "Back soon.", "--from", first,
                              "--to", last, "--json").stdout)
    assert (shown["subject"], shown["from"], shown["to"], shown["replaced"]) == ("Away", first, last, False)
    body = _puts(env)[-1][3]
    assert body["mode"] == "addVacationReply"
    # "org", a documented value, answers 500 at Zoho; "all" is the one that works.
    assert body["vacationResponse"]["sendTo"] == "all"
    y, m, d = first.split("-")
    assert body["vacationResponse"]["fromDate"] == f"{m}/{d}/{y} 00:00:00"
    again = json.loads(env.ok("vacation", "set", "--subject", "Away still", "--content", "x", "--from", first,
                              "--to", last, "--json").stdout)
    assert again["replaced"] and _puts(env)[-1][3]["mode"] == "updateVacationReply"
    env.ok("vacation", "remove")
    assert json.loads(env.ok("vacation", "show", "--json").stdout)["set"] is False


def test_vacation_TestModeAndStartingToday_IsRefusedBeforeAnyCall(env):
    # A vacation reply answers whoever writes in, which no allowed list can check.
    env.ok("allowed-recipients", "set", ME, OTHER)
    before = len(env.zoho.calls)
    result = env.run("vacation", "set", "--subject", "s", "--content", "c", "--from", _day(0), "--to", _day(1))
    assert result.exit_code == 2 and "test mode" in result.output and len(env.zoho.calls) == before
    env.ok("vacation", "set", "--subject", "s", "--content", "c", "--from", _day(1), "--to", _day(1))


def test_vacation_BadDays_AreRefused(env):
    for first, last in (("tomorrow", _day(2)), (_day(3), _day(2))):
        result = env.run("vacation", "set", "--subject", "s", "--content", "c", "--from", first, "--to", last)
        assert result.exit_code == 2
    assert _puts(env) == []


def test_vacation_RemoveWhenNone_IsRefused(env):
    result = env.run("vacation", "remove")
    assert result.exit_code == 2 and "no vacation reply" in result.output and _puts(env) == []


# --------------------------------------------------------------------------------------------- signatures


def test_signature_AddChangeDelete(env):
    added = json.loads(env.ok("signature", "add", "--name", "work", "--content", "-- Soren", "--json").stdout)
    sid = added["id"]
    assert (added["name"], added["content"], added["position"]) == ("work", "-- Soren", 0)
    changed = json.loads(env.ok("signature", "change", sid, "--content", "-- Soren F", "--json").stdout)
    # What is not given is kept.
    assert (changed["name"], changed["content"]) == ("work", "-- Soren F")
    renamed = json.loads(env.ok("signature", "change", sid, "--name", "office", "--json").stdout)
    assert (renamed["name"], renamed["content"]) == ("office", "-- Soren F")
    listed = json.loads(env.ok("signature", "list", "--json").stdout)
    assert listed["count"] == 1 and listed["signatures"][0]["content"] == "-- Soren F"
    env.ok("signature", "delete", sid)
    assert json.loads(env.ok("signature", "list", "--json").stdout)["count"] == 0
    assert any(r[0] == "DELETE" and r[2] == {"id": sid} for r in env.zoho.admin.requests)


def test_signature_UnknownId_IsRefused(env):
    for args in (("change", "123", "--name", "x"), ("delete", "123")):
        result = env.run("signature", *args)
        assert result.exit_code == 2 and "no signature 123" in result.output


def test_signature_ChangeNothing_IsRefused(env):
    result = env.run("signature", "change", "123")
    assert result.exit_code == 2 and "nothing to change" in result.output
