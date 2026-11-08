"""A Zoho mailbox that cannot be read fails that channel only, with one sentence saying what is wrong and how to fix
it, its memory back exactly as it was, and every other mailbox read as usual."""

import pytest

from zoho_rig import zw, world  # noqa: F401 - the fixtures
from zoho_rig import BOX, ENTRIES, MACHINE, OTHER, cursor, mail, phase2, section, zoho


def test_NoDtZohoSettingsAnywhere_SaysToSetTheDataCentre_AndWhereItLooked(zw):
    zw.memory(zoho())
    lines = phase2.channel_failed(zw, BOX).splitlines()
    assert lines[0] == f"dt-zoho has no settings on {MACHINE}, so {BOX} cannot be read: run dt-zoho settings set there"
    assert lines[1].startswith("dt-zoho settings set --data-centre <ca|com|eu|in|com.au|jp>; looked in ")
    assert zw.tools.log == []     # no settings, so dt-zoho was never asked


@pytest.mark.parametrize("instance", [None, "default", "devthrottle-2", "cc-storage"])
def test_SettingsUnderAnyCcDirectorFolder_AreFound_AndDtZohoIsPointedAtThem(zw, instance):
    """dt-zoho settings set run in a Director session saves under that session's instance folder; run by hand, under
    the Director's own folder or cc-storage's. The check, with no CC_ variable, finds any of them."""
    root = zw.tools.settings(instance)
    zw.tools.account().inbox(mail("2026-10-18T08:00:00Z"))
    zw.memory(zoho())
    code, output, context, _ = zw.run()
    assert code == 0 and "failure" not in section(context)
    assert {e["CC_DIRECTOR_ROOT"] for e in zw.tools.envs} == {str(root)}


def test_SettingsInTwoCcDirectorFolders_FailsSayingToKeepOne(zw):
    zw.tools.settings(None)
    zw.tools.settings("default")
    zw.tools.account()
    zw.memory(zoho())
    lines = phase2.channel_failed(zw, BOX).splitlines()
    assert lines[0] == f"dt-zoho has settings in more than one cc-director folder on {MACHINE}: keep one"
    assert lines[1].startswith("the folders found: ") and lines[1].count("cc-director") >= 2


@pytest.mark.parametrize("entry", ENTRIES)
def test_AKeyEntryNotInCcSecretsHere_TheSentenceIsCcSecretsReason(zw, entry):
    zw.tools.account()
    zw.tools.secrets.discard(entry)
    zw.memory(zoho())
    lines = phase2.channel_failed(zw, BOX).splitlines()
    reason = f"No secret named '{entry}' is on this machine."
    assert lines[0] == zw.screen.with_error(f"cc-secrets refused the Zoho key on {MACHINE} for {BOX}", reason)
    assert lines[0].startswith(f"cc-secrets refused the Zoho key on {MACHINE} for {BOX}: No secret named '{entry}'")
    assert lines[1] == (f"dt-zoho signs in with the entries {', '.join(ENTRIES)}; cc-secrets said: No secret named "
                        f"'{entry}' is on this machine.")


def test_TheKeyDoesNotReachTheMailbox_SaysSo(zw):
    zw.tools.account("someone.else@example.net")
    zw.memory(zoho())
    lines = phase2.channel_failed(zw, BOX).splitlines()
    assert lines[0] == f"the Zoho key on {MACHINE} does not reach {BOX}: use the key of its organisation"
    assert lines[1] == "dt-zoho accounts lists: someone.else@example.net"


def test_AnAliasOfTheAccount_IsReached(zw):
    """A Zoho account receives at its aliases too; dt-zoho accounts lists every address."""
    zw.tools.account()
    zw.tools.boxes[f"alias.{BOX}"] = zw.tools.boxes[BOX]
    del zw.tools.boxes[BOX]
    zw.memory(zoho())
    code, output, context, _ = zw.run()
    assert "the Zoho key" in section(context)["failure"]["sentence"]   # the account under another address: not reached
    zw.tools.boxes = {BOX: zw.tools.boxes[f"alias.{BOX}"]}
    zw.memory(zoho(f"alias.{BOX}"))
    zw.tools.boxes[f"alias.{BOX}"] = zw.tools.boxes[BOX]
    code, output, context, _ = zw.run()
    assert "failure" not in section(context, f"alias.{BOX}")


@pytest.mark.parametrize("kind", ["Inbox", "Sent", "Archive", "Spam"])
def test_AFolderZohoMakesIsMissing_SaysWhich(zw, kind):
    zw.tools.account(without=(kind,))
    zw.memory(zoho())
    assert phase2.channel_failed(zw, BOX).splitlines()[0] == f"{BOX} has no {kind} folder that dt-zoho can find on {MACHINE}"


@pytest.mark.parametrize("program", ["dt-zoho", "cc-secrets"])
def test_AProgramNotInstalled_SaysSo(zw, program):
    zw.tools.account()
    zw.tools.installed.discard(program)
    zw.memory(zoho())
    sentence = phase2.channel_failed(zw, BOX).splitlines()[0]
    assert sentence == f"{program} is not installed on {MACHINE}, so {BOX} cannot be read: install the DevThrottle tools there"
    assert zw.tools.log == []


def test_DtZohoError_IsTheChannelsSentence_TheWholeLineIsTheDetail(zw):
    zw.tools.account()
    zw.tools.fail = ("Error: could not reach accounts.zohocloud.ca: [Errno 111] Connection refused. Check the network "
                     "and the data centre (dt-zoho settings show).")
    zw.memory(zoho())
    lines = phase2.channel_failed(zw, BOX).splitlines()
    assert lines[0] == f"dt-zoho failed for {BOX} while signing in: could not reach accounts.zohocloud.ca: [Errno 1..."
    assert lines[1].startswith("could not reach accounts.zohocloud.ca: [Errno 111] Connection refused.")


@pytest.mark.parametrize("failure, start", [
    ("not-json", f"dt-zoho answered something that is not JSON for {BOX} while signing in"),
    ("secrets-broken", f"cc-secrets failed on {MACHINE} for {BOX} while signing in: error: the cc-secrets store is locked by anothe..."),
    ("timeout", f"dt-zoho did not finish within 60 seconds for {BOX} while signing in"),
])
def test_OtherFailures_EachIsASentence(zw, failure, start):
    zw.tools.account()
    zw.tools.fail = failure
    zw.memory(zoho())
    assert phase2.channel_failed(zw, BOX).splitlines()[0] == start


def test_OneMailboxFailing_TheOtherIsReadAsUsual(zw):
    zw.tools.account()
    zw.tools.account(OTHER).inbox(mail("2026-10-18T08:00:00Z", subject="Still read"))
    zw.tools.fail, zw.tools.fail_for = "Error: Zoho answered 404 to GET /api/accounts/1/folders: Invalid Input.", BOX
    zw.memory(zoho(), zoho(OTHER))
    code, output, context, _ = zw.run()
    assert code == 0
    assert "failure" in section(context, BOX)
    assert [m["subject"] for m in section(context, OTHER)["messages"]] == ["Still read"]


@pytest.mark.parametrize("wrong", [
    {"newest": "2026-10-18T09:00:00Z", "idsAtNewest": []},                                     # an Outlook cursor
    {"provider": "imap", "uidValidity": 1, "lastUid": 2, "newest": "2026-10-18T09:00:00Z"},    # an IMAP cursor
    {"provider": "zoho", "newest": "2026-10-18 09:00", "idsAtNewest": []},
    {"provider": "zoho", "newest": "2026-10-18T09:00:00Z", "idsAtNewest": ["not-an-id"]},
])
def test_ACursorThisReaderDidNotWrite_FailsThatChannel(zw, wrong):
    zw.tools.account()
    zw.memory(zoho(cursor=wrong))
    assert phase2.channel_failed(zw, BOX).splitlines()[0] == f"the channel {BOX} has a 'cursor' this check did not write"


def test_AZohoCursorOnAnImapChannel_FailsThatChannel(zw):
    """The IMAP reader never takes the Zoho reader's cursor for its own (each reader knows its own)."""
    zw.memory(phase2.channel(BOX, provider="imap", cursor=cursor()))
    assert phase2.channel_failed(zw, BOX).splitlines()[0] == f"the channel {BOX} has a 'cursor' this check did not write"


def test_MoreNewMailThanOneRunReads_FailsThatChannel_RatherThanStartAnywhere(zw, monkeypatch):
    monkeypatch.setattr(zw.screen, "MAX_INBOX_PAGES", 1)
    box = zw.tools.account()
    box.inbox(mail("2026-10-17T07:00:00Z", subject="Reported last"))
    box.inbox(*[mail(f"2026-10-17T{8 + n // 60:02d}:{n % 60:02d}:00Z") for n in range(200)])
    zw.memory(zoho(cursor=cursor("2026-10-17T07:00:00Z")))
    lines = phase2.channel_failed(zw, BOX).splitlines()
    assert lines[0] == f"{BOX} has more new mail than the Mail Desk reads in one run; it cannot find where it stopped"
    assert lines[1] == "read 200 Inbox messages newest first and none arrived before 2026-10-17T07:00:00Z"


def test_AMessageWithoutAUsableArrivalTime_FailsThatChannel(zw, monkeypatch):
    zw.tools.account().inbox(mail("2026-10-18T08:00:00Z"))
    real = zw.tools.record
    monkeypatch.setattr(zw.tools, "record", lambda mid, m: dict(real(mid, m), received="2026-10-18T08:00:00"))
    zw.memory(zoho())
    assert phase2.channel_failed(zw, BOX).splitlines()[0] == \
        f"dt-zoho answered a message without a usable id or arrival time for {BOX} while reading the Inbox"


LONG = "a.rather.long.mailbox.name@a-long-domain.example.com"

BEFORE_SIGN_IN = ["no-settings", "no-key", "no-cc-secrets", "not-reached"]   # these stop the sign-in, so never file
ANY_STEP = ["dt-zoho-error", "secrets-broken", "timeout", "not-json"]
WORDS = {"no-settings": "dt-zoho has no settings", "no-key": "cc-secrets refused the Zoho key",
         "no-cc-secrets": "cc-secrets is not installed", "not-reached": "does not reach", "dt-zoho-error": "dt-zoho failed",
         "secrets-broken": "cc-secrets failed", "timeout": "dt-zoho did not finish", "not-json": "dt-zoho answered something"}


@pytest.mark.parametrize("break_it, step", [(b, "signing in") for b in BEFORE_SIGN_IN + ANY_STEP]
                         + [(b, s) for b in ANY_STEP for s in ("archive", "junk")])
def test_EverySentence_ForALongAddress_AtEveryStep_FitsWhatTheGatewayKeeps(zw, break_it, step):
    """Each failure taken from a real run with a long address, never written out by hand, while signing in and while
    filing in live mode."""
    if break_it != "no-settings":
        box = zw.tools.account(LONG)
        [mid] = box.inbox(mail("2026-10-18T08:00:00Z"))
    else:
        mid = "1794167752063002500"
    if break_it == "no-key":
        zw.tools.secrets.clear()
    elif break_it == "no-cc-secrets":
        zw.tools.installed.discard("cc-secrets")
    elif break_it == "not-reached":
        zw.tools.boxes = {"someone.else@example.net": zw.tools.boxes[LONG]}
    elif break_it == "dt-zoho-error":
        zw.tools.fail = "Error: " + "a very long Zoho complaint " * 10
    elif break_it != "no-settings":
        zw.tools.fail = break_it
    if step != "signing in":
        zw.tools.fail_only = "move"
        key = zw.key(LONG, mid)
        zw.memory(zoho(LONG, mode="live", cursor=cursor("2026-10-18T08:00:00Z", mid),
                       keys={key: {"id": mid, "reportedUtc": "2026-10-18T09:00:00Z"}},
                       filingQueue=[{"messageKey": key, "decision": step}]))
    else:
        zw.memory(zoho(LONG))
    sentence = phase2.channel_failed(zw, LONG).splitlines()[0]
    assert LONG in sentence
    assert WORDS[break_it] in sentence, sentence
    if step != "signing in" and break_it != "timeout":
        assert ("archiving" if step == "archive" else "Spam") in sentence
    assert len(sentence) <= zw.screen.SENTENCE_LIMIT, sentence
