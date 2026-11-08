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


def test_ThePasswordIsNotInCcSecretsHere_SaysWhichEntry(iw):
    iw.tools.account(entry="desk-mail")
    iw.tools.secrets.clear()
    iw.memory(imap())
    lines = phase2.channel_failed(iw, BOX).splitlines()
    assert lines[0] == f"the password of {BOX} is not in cc-secrets on {MACHINE}: add it there"
    assert lines[1] == ("the dt-email account names the entry desk-mail; cc-secrets said: No secret named 'desk-mail' "
                        "is on this machine.")


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
    ("secrets-broken", f"cc-secrets failed on {MACHINE} for {BOX} while signing in: error: the cc-secrets store is locked"),
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


@pytest.mark.parametrize("break_it", ["no-account", "no-secret", "no-dt-email", "no-cc-secrets", "dt-email-error",
                                      "secrets-broken", "timeout", "not-json"])
def test_EverySentence_ForALongAddress_FitsWhatTheGatewayKeeps(iw, break_it):
    """Each failure taken from a real run with a long address and a long entry name, never written out by hand."""
    iw.tools.account(LONG, entry="x" * 40)
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
    iw.memory(imap(LONG))
    sentence = phase2.channel_failed(iw, LONG).splitlines()[0]
    assert LONG in sentence
    assert len(sentence) <= iw.screen.SENTENCE_LIMIT, sentence
