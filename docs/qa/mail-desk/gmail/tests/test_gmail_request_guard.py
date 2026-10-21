"""R3: nothing but reading messages and, in live mode only, the two filings can leave the Gmail request guard.

cc-gmail's saved sign-in CAN send (its scopes include gmail.send and gmail.compose), so the guard is the whole of the
promise that the Mail Desk never sends, drafts, trashes, deletes or relabels. Each refused shape below is sent straight
to gmail_request(), the one function every Gmail API call goes through, and two things are asserted: it raises
RefusedRequest, and the fake Gmail API's log is EMPTY - the request never reached the network. The allowed shapes are
sent too, to show the log is a working instrument and not an empty one by accident.
"""

import re

import pytest

from gmail_rig import gw, world  # noqa: F401 - the fixtures
from gmail_rig import GMAIL_BOX, NOW, TOKEN_URI, channel, cursor, gmsg

API = "https://gmail.googleapis.com/gmail/v1/users/me"
MESSAGE = f"{API}/messages/18c2f0a9d4b3e1aa"
MODIFY = f"{MESSAGE}/modify"
ARCHIVE = {"removeLabelIds": ["INBOX"]}
JUNK = {"addLabelIds": ["SPAM"], "removeLabelIds": ["INBOX"]}
TOKEN = "gtoken-for-desk@example.com"

REFUSED = [
    # other methods
    ("PUT", MESSAGE, {"labelIds": []}),
    ("PATCH", MESSAGE, {"labelIds": []}),
    ("DELETE", MESSAGE, None),
    ("HEAD", f"{API}/messages", None),
    ("post", MODIFY, ARCHIVE),
    ("get", f"{API}/messages", None),
    # sending, drafts, trash, delete, the batch calls, import and insert
    ("POST", f"{API}/messages/send", {"raw": "eA"}),
    ("POST", f"{API}/drafts", {"message": {"raw": "eA"}}),
    ("POST", f"{API}/drafts/send", {"id": "r1"}),
    ("GET", f"{API}/drafts", None),
    ("POST", f"{MESSAGE}/trash", None),
    ("POST", f"{MESSAGE}/untrash", None),
    ("POST", f"{MESSAGE}/delete", None),
    # the same calls carrying a filing body, so the path rule alone must refuse them
    ("POST", f"{MESSAGE}/trash", ARCHIVE),
    ("POST", f"{MESSAGE}/untrash", ARCHIVE),
    ("POST", f"{MESSAGE}/delete", JUNK),
    ("POST", f"{MESSAGE}/send", ARCHIVE),
    ("POST", f"{API}/messages/batchModify", ARCHIVE),
    ("POST", f"{API}/threads/18c2f0a9d4b3e1aa/modify", JUNK),
    ("POST", f"{API}/drafts", ARCHIVE),
    ("POST", f"{API}/messages/batchDelete", {"ids": ["18c2f0a9d4b3e1aa"]}),
    ("POST", f"{API}/messages/batchModify", {"ids": ["18c2f0a9d4b3e1aa"], "removeLabelIds": ["INBOX"]}),
    ("POST", f"{API}/messages/import", {"raw": "eA"}),
    ("POST", f"{API}/messages", {"raw": "eA"}),
    # threads, settings, labels, history, the profile
    ("POST", f"{API}/threads/18c2f0a9d4b3e1aa/modify", ARCHIVE),
    ("GET", f"{API}/threads/18c2f0a9d4b3e1aa", None),
    ("POST", f"{API}/threads/18c2f0a9d4b3e1aa/trash", None),
    ("GET", f"{API}/settings/forwardingAddresses", None),
    ("POST", f"{API}/settings/filters", {"criteria": {}}),
    ("POST", f"{API}/labels", {"name": "x"}),
    ("GET", f"{API}/labels", None),
    ("GET", f"{API}/history?startHistoryId=1", None),
    ("POST", f"{API}/stop", None),
    ("GET", f"{API}/profile", None),
    ("GET", f"{MESSAGE}/attachments/abc", None),
    # a modify with any other body
    ("POST", MODIFY, {"removeLabelIds": ["INBOX", "UNREAD"]}),
    ("POST", MODIFY, {"addLabelIds": ["TRASH"], "removeLabelIds": ["INBOX"]}),
    ("POST", MODIFY, {"addLabelIds": ["SPAM"]}),
    ("POST", MODIFY, {"addLabelIds": ["STARRED"]}),
    ("POST", MODIFY, {"removeLabelIds": ["SPAM"]}),
    ("POST", MODIFY, {"addLabelIds": ["Label_1"], "removeLabelIds": ["INBOX"]}),
    ("POST", MODIFY, {"removeLabelIds": ["INBOX"], "addLabelIds": ["SPAM"]}),
    ("POST", MODIFY, {"removeLabelIds": ["inbox"]}),
    ("POST", MODIFY, {"removeLabelIds": "INBOX"}),
    ("POST", MODIFY, {"removeLabelIds": ["INBOX"], "comment": "x"}),
    ("POST", MODIFY, {}),
    ("POST", MODIFY, None),
    ("POST", MODIFY, '{"removeLabelIds": ["INBOX"]}'),
    ("POST", MODIFY, [ARCHIVE]),
    ("POST", MODIFY + "?fields=id", ARCHIVE),
    # another user, host or version
    ("GET", "https://gmail.googleapis.com/gmail/v1/users/someone@example.com/messages", None),
    ("POST", "https://gmail.googleapis.com/gmail/v1/users/someone@example.com/messages/abc/modify", ARCHIVE),
    ("GET", "https://gmail.googleapis.com/gmail/v1/users/me@example.com/messages", None),
    ("GET", "https://gmail.googleapis.com/gmail/v2/users/me/messages", None),
    ("GET", "https://gmail.googleapis.com/upload/gmail/v1/users/me/messages", None),
    ("GET", "https://www.googleapis.com/gmail/v1/users/me/messages", None),
    ("GET", "https://evil.example.com/gmail/v1/users/me/messages", None),
    ("GET", "http://gmail.googleapis.com/gmail/v1/users/me/messages", None),
    ("GET", "https://gmail.googleapis.com.evil.example.com/gmail/v1/users/me/messages", None),
    ("GET", "https://gmail.googleapis.com:8443/gmail/v1/users/me/messages", None),
    ("GET", "https://someone@gmail.googleapis.com/gmail/v1/users/me/messages", None),
    ("GET", "https://graph.microsoft.com/v1.0/me/messages", None),
    ("POST", TOKEN_URI, ARCHIVE),
    # ids and paths that try to reach another path
    ("POST", f"{API}/messages/../send/modify", ARCHIVE),
    ("POST", f"{API}/messages/abc%2Fsend/modify", ARCHIVE),
    ("POST", f"{API}/messages/abc-def/modify", ARCHIVE),
    ("POST", f"{API}/messages/abc_def/modify", ARCHIVE),
    ("POST", f"{API}/messages/abc=/modify", ARCHIVE),
    ("POST", f"{API}/messages/abc.def/modify", ARCHIVE),
    ("POST", f"{API}/messages/abc/def/modify", ARCHIVE),
    ("POST", f"{API}/messages//modify", ARCHIVE),
    ("GET", f"{API}/messages/abc-def?format=metadata", None),
    ("GET", f"{API}/messages/?format=metadata", None),
    ("GET", f"{API}/messages/abc/../send?format=metadata", None),
    ("GET", f"{API}/messages/abc\\x?format=metadata", None),
    # spaces and control characters, before parsing
    ("POST", f"{API}/messages/abc\tdef/modify", ARCHIVE),
    ("POST", f"{API}/messages/abc\ndef/modify", ARCHIVE),
    ("POST", f"{API}/messages/abc def/modify", ARCHIVE),
    ("GET", f"{API}/messages?q=after:1\r\n", None),
    # a GET with a body, a fragment, a whole message, or a query this check never sends
    ("GET", f"{API}/messages", {"x": 1}),
    ("GET", f"{API}/messages#x", None),
    ("GET", f"{MESSAGE}?format=metadata#x", None),
    ("GET", MESSAGE, None),
    ("GET", f"{MESSAGE}?format=full", None),
    ("GET", f"{MESSAGE}?format=raw", None),
    ("GET", f"{MESSAGE}?format=metadata&metadataHeaders=Authorization", None),
    ("GET", f"{MESSAGE}?format=metadata&fields=payload", None),
    ("GET", f"{API}/messages?labelIds=TRASH", None),
    ("GET", f"{API}/messages?labelIds=INBOX&includeSpamTrash=true", None),
    ("GET", f"{API}/messages?labelIds=INBOX&key=x", None),
    # spaces and control characters once decoded: never in a name, and no control character in a value (G0)
    ("GET", f"{API}/messages?label%20Ids=INBOX", None),
    ("GET", f"{API}/messages?labelIds+=INBOX", None),
    ("GET", f"{API}/messages?labelIds=INBOX&q=after%3A1%0Ahas%3Aattachment", None),
    ("GET", f"{API}/messages?labelIds=INBOX&q=after%3A1%09x", None),
    ("GET", f"{API}/messages?labelIds=INBOX&q=after%3A1%7F", None),
    ("GET", f"{API}/messages?labelIds=INBOX%00", None),
    ("GET", f"{API}/messages?labelIds=INBOX&q=after:1 has:attachment", None),
]


@pytest.mark.parametrize("method,url,body", REFUSED, ids=[f"{m} {u[-56:]!r} {b}" for m, u, b in REFUSED])
@pytest.mark.parametrize("live", [False, True], ids=["observe", "live"])
def test_GmailRequest_RefusedShape_RaisesAndNothingReachesTheNetwork(gw, method, url, body, live):
    with pytest.raises(gw.screen.RefusedRequest):
        gw.screen.gmail_request(method, url, TOKEN, body, live=live)
    assert gw.gmail.log == []


@pytest.mark.parametrize("body", [ARCHIVE, JUNK], ids=["archive", "junk"])
def test_GmailRequest_ObserveMode_RefusesEvenTheTwoFilings(gw, body):
    with pytest.raises(gw.screen.RefusedRequest, match="observe mode"):
        gw.screen.gmail_request("POST", MODIFY, TOKEN, body)
    assert gw.gmail.log == []


@pytest.mark.parametrize("body", [ARCHIVE, JUNK], ids=["archive", "junk"])
def test_GmailRequest_LiveMode_TheTwoFilingsReachTheNetwork(gw, body):
    gw.gmail.box(GMAIL_BOX).append(gmsg("18c2f0a9d4b3e1aa", "2026-10-18T09:00:00Z"))
    gw.screen.gmail_request("POST", MODIFY, TOKEN, body, live=True)
    assert [(m, u) for m, u, _, _ in gw.gmail.log] == [("POST", MODIFY)]


@pytest.mark.parametrize("url", [
    f"{API}/messages?labelIds=INBOX&q=after%3A1760000000&maxResults=500",
    f"{API}/messages?labelIds=INBOX&q=after%3A1760000000&maxResults=500&pageToken=500",
    f"{API}/messages?labelIds=SENT&q=to%3Aa%40example.com&maxResults=1",
    f"{API}/messages?labelIds=INBOX&q=after%3A1760000000+has%3Aattachment&maxResults=500",
    f"{API}/messages?labelIds=SENT&q=to%3Aa%40example.com+OR+cc%3Aa%40example.com+OR+bcc%3Aa%40example.com&maxResults=1",
    f"{API}/messages?labelIds=INBOX&q=after%3A1760000000%20has%3Aattachment&maxResults=500",
    f"{MESSAGE}?format=metadata&metadataHeaders=From&metadataHeaders=Subject",
    f"{MESSAGE}?format=metadata&metadataHeaders=To&metadataHeaders=Cc&metadataHeaders=Bcc",
], ids=["list", "next page", "sent search", "attachment search", "sent search with spaces",
       "space as %20", "one message", "one sent message"])
def test_GmailRequest_TheReadsReachTheNetwork(gw, url):
    """The instrument works: an allowed read is logged."""
    gw.gmail.box(GMAIL_BOX).append(gmsg("18c2f0a9d4b3e1aa", "2026-10-18T09:00:00Z"))
    gw.screen.gmail_request("GET", url, TOKEN)
    assert len(gw.gmail.log) == 1


TOKEN_FORM = {"grant_type": "refresh_token", "client_id": "c", "client_secret": "s", "refresh_token": "r"}
REFUSED_REFRESHES = [
    ("https://oauth2.googleapis.com/token/", TOKEN_FORM),
    ("https://evil.example.com/token", TOKEN_FORM),
    ("https://oauth2.googleapis.com/token?x=1", TOKEN_FORM),
    ("http://oauth2.googleapis.com/token", TOKEN_FORM),
    ("https://accounts.google.com/o/oauth2/token", TOKEN_FORM),
    (TOKEN_URI, dict(TOKEN_FORM, grant_type="authorization_code")),
    (TOKEN_URI, dict(TOKEN_FORM, scope="https://www.googleapis.com/auth/gmail.send")),
    (TOKEN_URI, {k: v for k, v in TOKEN_FORM.items() if k != "client_secret"}),
    (TOKEN_URI, dict(TOKEN_FORM, refresh_token="")),
    (TOKEN_URI, "grant_type=refresh_token"),
]


@pytest.mark.parametrize("url,form", REFUSED_REFRESHES, ids=[f"{u[-40:]} {sorted(f) if isinstance(f, dict) else f}"
                                                            for u, f in REFUSED_REFRESHES])
def test_GoogleTokenRequest_RefusedShape_RaisesAndNothingReachesTheNetwork(gw, url, form):
    with pytest.raises(gw.screen.RefusedRequest):
        gw.screen.google_token_request(url, form)
    assert gw.gmail.log == []


def test_GoogleTokenRequest_TheRefreshReachesTheNetwork(gw):
    gw.screen.google_token_request(TOKEN_URI, dict(TOKEN_FORM, refresh_token="refresh-for-desk@example.com"))
    assert len(gw.gmail.refreshes()) == 1


def test_Source_EveryGmailCallGoesThroughTheGuard(gw):
    """The readers call the Gmail API only through gmail_request() and the sign-in service only through
    google_token_request(); nothing in the Gmail reader calls transport() or urlopen itself."""
    source = open(gw.screen.__file__, encoding="utf-8").read()
    reader = source.split("\nclass GmailReader:")[1].split("\nREADERS = ")[0]
    assert "transport(" not in reader and "urlopen" not in reader
    assert "graph_request(" not in reader
    assert len(re.findall(r"gmail_request\(", reader)) == 1
    assert len(re.findall(r"google_token_request\(", reader)) == 1


def test_FilingQueue_AnIdThatIsNotLettersAndDigits_IsRefusedPerItemAndTheRestFiled(gw):
    """An id from the memory holding a character a Gmail id never has is refused by the guard itself, per item."""
    gw.plant_gmail()
    gw.gmail.box(GMAIL_BOX).append(gmsg("aa01", "2026-10-18T09:00:00Z"))
    keys = {"k-bad": {"id": "aa01/../send", "reportedUtc": "2026-10-18T09:05:00Z"},
            "k-tab": {"id": "aa\t01", "reportedUtc": "2026-10-18T09:05:00Z"},
            "k-good": {"id": "aa01", "reportedUtc": "2026-10-18T09:05:00Z"}}
    gw.memory(channel(GMAIL_BOX, mode="live", provider="gmail", cursor=cursor("2026-10-18T09:00:00Z", "aa01"),
                      keys=keys, filingQueue=[{"messageKey": k, "decision": "archive"} for k in keys]))
    code, _, context, _ = gw.run()
    assert code == 0
    assert [r["result"] for r in context["channels"][0]["filing"]] == ["refused", "refused", "moved"]
    assert [u for _, u, _, _ in gw.gmail.posts()] == [f"{API}/messages/aa01/modify"]
    assert "aa01/../send" not in context["channels"][0]["filing"][0]["reason"]
