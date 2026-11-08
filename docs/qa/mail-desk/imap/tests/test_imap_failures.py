"""An IMAP mailbox that cannot be read fails that channel only, with one sentence saying what is wrong and how to fix
it, its memory back exactly as it was, and every other mailbox read as usual."""

import pytest

from imap_rig import iw, world  # noqa: F401 - the fixtures
from imap_rig import BOX, MACHINE, OTHER, cursor, imap, mail, phase2, section


def test_NoDtEmailAccountForTheAddress_SaysToAddOne(iw):
    iw.tools.account("someone.else@example.net")
    iw.memory(imap())
    sentence = phase2.channel_failed(iw, BOX).splitlines()[0]
    assert sentence == f"{BOX} has no dt-email account on {MACHINE}: run dt-email account add there"


@pytest.mark.parametrize("instance", [None, "default", "devthrottle-2", "cc-storage"])
def test_AnAccountSavedUnderAnyCcDirectorFolder_IsFound_AndDtEmailIsPointedAtIt(iw, instance):
    """dt-email account add run in a Director session saves under that session's instance folder; run by hand, under
    the Director's own folder. The check, with no CC_ variable, finds either."""
    iw.tools.account(instance=instance).inbox(mail("2026-10-18T08:00:00Z"))
    iw.memory(imap())
    code, output, context, _ = iw.run()
    assert code == 0 and "failure" not in section(context, BOX)


def test_TheAccountInTwoCcDirectorFolders_FailsSayingToKeepOne(iw):
    iw.tools.account(name="desk", instance=None)
    iw.tools.account(name="desk-again", instance="default")
    iw.memory(imap())
    lines = phase2.channel_failed(iw, BOX).splitlines()
    assert lines[0] == f"{BOX} is in more than one dt-email account on {MACHINE}: keep one"
    assert "desk in " in lines[1] and "desk-again in " in lines[1]


def test_NoDtEmailSettingsAnywhere_SaysToAddTheAccount_AndWhereItLooked(iw):
    iw.memory(imap())
    lines = phase2.channel_failed(iw, BOX).splitlines()
    assert lines[0] == f"{BOX} has no dt-email account on {MACHINE}: run dt-email account add there"
    assert lines[1].endswith("and their instances, which hold no dt-email settings")
    assert iw.tools.log == []     # no settings, so dt-email was never asked


def test_TheFoldersSearched_IncludeWhereCcStorageSavesDtEmailSettings(iw, monkeypatch):
    """dt-email saves through cc-storage. Its user folder, with no CC_DIRECTOR_ROOT, must be among the folders the
    check searches, or an account added from a plain terminal is never found (on Linux it is not the Director's)."""
    import importlib.util
    monkeypatch.delenv("LOCALAPPDATA", raising=False)
    monkeypatch.setenv("HOME", str(iw.home))
    spec = importlib.util.spec_from_file_location("cc_storage_for_the_rig",
                                                  phase2.REPO / "tools" / "cc_storage" / "storage.py")
    storage = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(storage)
    assert storage.CcStorage._user_base() in iw.screen.dt_email_bases(str(iw.home))


def test_ThePasswordIsNotInCcSecretsHere_TheSentenceIsCcSecretsReason(iw):
    iw.tools.account(entry="desk-mail")
    iw.tools.secrets.clear()
    iw.memory(imap())
    lines = phase2.channel_failed(iw, BOX).splitlines()
    assert lines[0] == f"cc-secrets refused the password of {BOX} on {MACHINE}: No secret named 'desk-mail' is on this machine."
    assert lines[1] == ("the dt-email account names the entry desk-mail; cc-secrets said: No secret named 'desk-mail' "
                        "is on this machine.")


def test_AnEntryNotAllowedForRun_SaysSoRatherThanToAddIt(iw, monkeypatch):
    iw.tools.account(entry="desk-mail")
    iw.tools.secrets.clear()
    real = iw.tools.secrets_run

    def not_allowed(argv, root):
        done = real(argv, root)
        done.stdout = done.stdout.replace(b"No secret named 'desk-mail' is on this machine.",
                                          b"Secret 'desk-mail' is not allowed for 'run'.")
        return done
    monkeypatch.setattr(iw.tools, "secrets_run", not_allowed)
    iw.memory(imap())
    assert phase2.channel_failed(iw, BOX).splitlines()[0] == \
        f"cc-secrets refused the password of {BOX} on {MACHINE}: Secret 'desk-mail' is not allowed for 'run'."


@pytest.mark.parametrize("program", ["dt-email", "cc-secrets"])
def test_AProgramNotInstalled_SaysSo(iw, program):
    iw.tools.account()
    iw.tools.installed.discard(program)
    iw.memory(imap())
    sentence = phase2.channel_failed(iw, BOX).splitlines()[0]
    assert sentence == f"{program} is not installed on {MACHINE}, so {BOX} cannot be read: install the DevThrottle tools there"


def test_DtEmailError_IsTheChannelsSentence_TheWholeLineIsTheDetail(iw):
    iw.tools.account()
    iw.tools.fail["desk"] = "Error: could not reach the mail server: [Errno 111] Connection refused. Check the host and port of the account (dt-email account list) and the network."
    iw.memory(imap())
    lines = phase2.channel_failed(iw, BOX).splitlines()
    assert lines[0] == f"dt-email failed for {BOX} while signing in: could not reach the mail server: [Errno 111] Co..."
    assert lines[1].startswith("could not reach the mail server: [Errno 111] Connection refused. Check the host")


@pytest.mark.parametrize("failure, start", [
    ("not-json", f"dt-email answered something that is not JSON for {BOX} while signing in"),
    ("secrets-broken", f"cc-secrets failed on {MACHINE} for {BOX} while signing in: error: the cc-secrets store is locked by anothe..."),
    ("timeout", f"dt-email did not finish within 60 seconds for {BOX} while signing in"),
])
def test_OtherFailures_EachIsASentence(iw, failure, start):
    iw.tools.account()
    iw.tools.fail["desk"] = failure
    iw.memory(imap())
    assert phase2.channel_failed(iw, BOX).splitlines()[0] == start


def test_OneMailboxFailing_TheOtherIsReadAsUsual(iw):
    iw.tools.account()
    iw.tools.fail["desk"] = "Error: the IMAP server answered with an error: AUTHENTICATIONFAILED"
    iw.tools.account(OTHER, name="second").inbox(mail("2026-10-18T08:00:00Z", subject="Still read"))
    iw.memory(imap(), imap(OTHER))
    code, output, context, _ = iw.run()
    assert code == 0
    assert "failure" in section(context, BOX)
    assert [m["subject"] for m in section(context, OTHER)["messages"]] == ["Still read"]


def test_ACursorThisReaderDidNotWrite_FailsThatChannel(iw):
    iw.tools.account()
    iw.memory(imap(cursor={"newest": "2026-10-18T09:00:00Z", "idsAtNewest": []}))   # an Outlook cursor
    assert phase2.channel_failed(iw, BOX).splitlines()[0] == f"the channel {BOX} has a 'cursor' this check did not write"


LONG = "a.rather.long.mailbox.name@a-long-domain.example.com"


BEFORE_SIGN_IN = ["no-account", "no-secret", "no-dt-email", "no-cc-secrets"]   # these stop the sign-in, so never file
ANY_STEP = ["dt-email-error", "secrets-broken", "timeout", "not-json"]


@pytest.mark.parametrize("break_it, step", [(b, "signing in") for b in BEFORE_SIGN_IN + ANY_STEP]
                         + [(b, s) for b in ANY_STEP for s in ("archive", "move")])
def test_EverySentence_ForALongAddress_AtEveryStep_FitsWhatTheGatewayKeeps(iw, break_it, step):
    """Each failure taken from a real run with a long address and a long entry name, never written out by hand, while
    signing in and while filing in live mode."""
    box = iw.tools.account(LONG, entry="x" * 40)
    box.inbox(mail("2026-10-18T08:00:00Z"))
    if break_it == "no-account":
        iw.tools.accounts["desk"]["email"] = "someone.else@example.net"
    elif break_it == "no-secret":
        iw.tools.secrets.clear()
    elif break_it in ("no-dt-email", "no-cc-secrets"):
        iw.tools.installed.discard(break_it.removeprefix("no-"))
    elif break_it == "dt-email-error":
        iw.tools.fail["desk"] = "Error: " + "a very long server complaint " * 10
    else:
        iw.tools.fail["desk"] = break_it
    if step != "signing in":
        iw.tools.fail_only = step
        key = iw.key(LONG, 1, 1)
        iw.memory(imap(LONG, mode="live", cursor=cursor(1),
                       keys={key: {"id": "1.1", "reportedUtc": "2026-10-18T09:00:00Z"}},
                       filingQueue=[{"messageKey": key, "decision": "archive" if step == "archive" else "junk"}]))
    else:
        iw.memory(imap(LONG))
    sentence = phase2.channel_failed(iw, LONG).splitlines()[0]
    assert LONG in sentence
    if step != "signing in" and break_it != "timeout":
        assert ("archiving" if step == "archive" else "junk") in sentence
    assert len(sentence) <= iw.screen.SENTENCE_LIMIT, sentence


def test_Renumbered_WhereItStoppedCannotBeFound_FailsThatChannel_InASentenceThatFits(iw, monkeypatch):
    """More mail since the day before the cursor's time than the search reads (one page here, 20 in use): the channel
    fails saying so, rather than starting anywhere, and the sentence fits even for a long address."""
    monkeypatch.setattr(iw.screen, "IMAP_MAX_RENUMBERED_PAGES", 1)
    box = iw.tools.account(LONG)
    box.inbox(*[mail(f"2026-10-17T{n // 60:02d}:{n % 60:02d}:00Z") for n in range(201)])
    box.inbox(mail("2026-10-18T09:00:00Z", subject="Reported last"))
    box.renumber()
    iw.memory(imap(LONG, cursor=cursor(202, "2026-10-18T09:00:00Z", validity=1)))
    lines = phase2.channel_failed(iw, LONG).splitlines()
    assert lines[0] == f"the server renumbered the Inbox of {LONG}; the Mail Desk cannot find where it stopped"
    assert len(lines[0]) <= iw.screen.SENTENCE_LIMIT
    assert lines[1] == "read 200 messages oldest first from 2026-10-17 and none arrived at or after 2026-10-18T09:00:00Z"
