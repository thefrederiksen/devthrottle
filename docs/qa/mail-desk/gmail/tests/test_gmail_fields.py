"""R5: the message fields, and standard error. sender and senderName from From; subject with RFC 2047 encoded words
decoded; preview from the snippet with HTML entities unescaped; both cleaned as phase 2's 4.3 (invisible padding out
BEFORE the 200-character cut); hasAttachments and knownSender each from one small function, settled by G0."""

import base64

import pytest

from gmail_rig import gw, world  # noqa: F401 - the fixtures
from gmail_rig import GMAIL_BOX, OUTLOOK_BOX, all_messages, channel, cursor, gmsg, gsent, phase2, token_json

CURSOR = cursor("2026-10-18T07:00:00Z")


def one(gw, message, **fields):
    gw.plant_gmail()
    gw.gmail.box(GMAIL_BOX).append(message)
    gw.memory(channel(GMAIL_BOX, provider="gmail", cursor=CURSOR, **fields))
    code, _, context, err = gw.run()
    assert code == 0, err
    (reported,) = all_messages(context)
    return reported, err


def test_Sender_FromTheFromHeader_AddressInLowerCase(gw):
    reported, _ = one(gw, gmsg("a1", "2026-10-18T08:00:00Z", sender='"Billing, Example" <Billing@Example.COM>'))
    assert reported["sender"] == "billing@example.com" and reported["senderName"] == "Billing, Example"


def test_Sender_AnAddressAlone_HasAnEmptyName(gw):
    reported, _ = one(gw, gmsg("a1", "2026-10-18T08:00:00Z", sender="news@example.org"))
    assert reported["sender"] == "news@example.org" and reported["senderName"] == ""


def test_Sender_NoFromHeader_IsEmptyAndNotKnown(gw):
    message = gmsg("a1", "2026-10-18T08:00:00Z")
    message["payload"]["headers"] = [h for h in message["payload"]["headers"] if h["name"] != "From"]
    reported, _ = one(gw, message)
    assert reported["sender"] == "" and reported["knownSender"] is False


@pytest.mark.parametrize("raw,decoded", [
    ("=?UTF-8?B?w4Vyc29wZ8O4cmVsc2U=?=", "Årsopgørelse"),
    ("=?utf-8?q?Caf=C3=A9_ouvert?=", "Café ouvert"),
    ("Re: =?ISO-8859-1?Q?R=E9union?= demain", "Re: Réunion demain"),
    ("=?UTF-8?B?5pel5pys6Kqe?= =?UTF-8?B?44Gu5Lu25ZCN?=", "日本語の件名"),
    ("Plain words stay as they are", "Plain words stay as they are"),
    ("=?x-unknown?B?SGVsbG8=?=", "=?x-unknown?B?SGVsbG8=?="),
], ids=["base64", "quoted-printable", "mixed", "two-words", "plain", "unknown-charset"])
def test_Subject_EncodedWordsDecoded_NothingReworded(gw, raw, decoded):
    reported, _ = one(gw, gmsg("a1", "2026-10-18T08:00:00Z", subject=raw))
    assert reported["subject"] == decoded


def test_Subject_CleanedAfterDecoding(gw):
    encoded = base64.b64encode("Inv\u200boice\u00ad  due\n".encode("utf-8")).decode("ascii")
    reported, _ = one(gw, gmsg("a1", "2026-10-18T08:00:00Z", subject=f"=?UTF-8?B?{encoded}?="))
    assert reported["subject"] == "Invoice due"


def test_Preview_HtmlEntitiesUnescaped(gw):
    reported, _ = one(gw, gmsg("a1", "2026-10-18T08:00:00Z",
                               snippet="Tom &amp; Jerry said &quot;hi&quot; &#39;twice&#39; &lt;3 &eacute;t&eacute;"))
    assert reported["preview"] == "Tom & Jerry said \"hi\" 'twice' <3 été"


def test_Preview_InvisiblePaddingStrippedBeforeThe200Cut(gw):
    """Phase 0 (c)'s padding: the padding goes first, then the cut, so the visible text is not lost. An entity that
    stands for padding (&#847; is U+034F, &zwnj; is U+200C) is unescaped first and so is stripped too."""
    visible = "word " * 60
    padding = "\u034f\u200c&#847;&zwnj;\u00a0" * 80
    reported, _ = one(gw, gmsg("a1", "2026-10-18T08:00:00Z", snippet=padding + visible))
    assert reported["preview"] == visible.strip()[:200]
    assert len(reported["preview"]) == 200


def test_HasAttachments_IsGmailsOwnAttachmentSearch_OneListARun(gw):
    """G0: format=metadata carries no parts, so hasAttachments is whether the id is in one has:attachment list of the
    same window, one call a run whatever the number of messages."""
    gw.plant_gmail()
    gw.gmail.box(GMAIL_BOX).extend([gmsg("a1", "2026-10-18T08:00:00Z", attached=True),
                                    gmsg("a2", "2026-10-18T08:01:00Z"),
                                    gmsg("a3", "2026-10-18T08:02:00Z", attached=True)])
    gw.memory(channel(GMAIL_BOX, provider="gmail", cursor=CURSOR))
    _, _, context, _ = gw.run()
    assert [m["hasAttachments"] for m in all_messages(context)] == [True, False, True]
    searches = [u for _, u, _, _ in gw.gmail.log if "has%3Aattachment" in u]
    assert len(searches) == 1
    after = f"after%3A{CURSOR['newestMs'] // 1000 - 1}"
    assert f"q={after}+has%3Aattachment" in searches[0] and "labelIds=INBOX" in searches[0]


def test_HasAttachments_NoNewMail_NoSearch(gw):
    gw.plant_gmail()
    gw.memory(channel(GMAIL_BOX, provider="gmail", cursor=CURSOR))
    gw.run()
    assert not any("has%3Aattachment" in u for _, u, _, _ in gw.gmail.log)


def test_ARealisticRun_TenMessagesTenSenders_Is22Calls(gw):
    """G0 (h): one list, 10 metadata reads, one attachment search and one Sent search per distinct sender."""
    gw.plant_gmail()
    gw.gmail.box(GMAIL_BOX).extend(gmsg(f"r{i}", f"2026-10-18T08:0{i}:00Z", sender=f"sender{i}@example.org")
                                   for i in range(10))
    gw.memory(channel(GMAIL_BOX, provider="gmail", cursor=CURSOR))
    _, output, _, _ = gw.run()
    assert output["count"] == 10
    assert len(gw.gmail.gmail_log()) == 22


# ---------------------------------------------------------------------------------------------------- known senders


def test_KnownSender_ByOneSearchPerDistinctSender(gw):
    gw.plant_gmail()
    box = gw.gmail.box(GMAIL_BOX)
    box += [gsent("s1", "Customer <customer@example.org>"), gsent("s2", "x@example.net", cc="Partner@Example.net")]
    box += [gmsg("a1", "2026-10-18T08:00:00Z", sender="customer@example.org"),
            gmsg("a2", "2026-10-18T08:01:00Z", sender="customer@example.org"),
            gmsg("a3", "2026-10-18T08:02:00Z", sender="partner@example.net"),
            gmsg("a4", "2026-10-18T08:03:00Z", sender="stranger@example.com")]
    gw.memory(channel(GMAIL_BOX, provider="gmail", cursor=CURSOR))
    _, _, context, _ = gw.run()
    assert [m["knownSender"] for m in all_messages(context)] == [True, True, True, False]
    searches = [u for _, u, _, _ in gw.gmail.log if "labelIds=SENT" in u]
    assert len(searches) == 3 and all("maxResults=1" in u for u in searches)
    assert "sentCache" not in gw.read_memory()["channels"][0]


HOSTILE = ['"a b"@example.com', "x@example.com OR in:anywhere", "x OR y@example.com", '"quoted"@example.com',
           "*@example.com", "x*@example.com", "x@example.com>", "x%y@example.com", "x@example",
           "x@-example.com,y@example.com", "x@example.com\tOR\tcc:y@example.com", "x@(a).example.com"]


@pytest.mark.parametrize("sender", HOSTILE)
def test_KnownSender_AnAddressThatIsNotPlain_IsNotKnownAndNeverSearched(gw, sender):
    """G0 review finding 1: the From address is the sender's to write, so only a plain address goes into a query."""
    gw.plant_gmail()
    message = gmsg("a1", "2026-10-18T08:00:00Z")
    message["payload"]["headers"][0]["value"] = sender
    gw.gmail.box(GMAIL_BOX).extend([gsent("s1", sender), message])
    gw.memory(channel(GMAIL_BOX, provider="gmail", cursor=CURSOR))
    _, _, context, _ = gw.run()
    assert [m["knownSender"] for m in all_messages(context)] == [False]
    assert not any("labelIds=SENT" in u for _, u, _, _ in gw.gmail.log)


@pytest.mark.parametrize("header", ["<x@example.com", "Some One <x@example.com> (a comment)", "x@exam ple.com"])
def test_KnownSender_AFromThatParsesToAPlainAddress_SearchesForThatAddressOnly(gw, header):
    """email.utils takes the address out of the header first; it is that plain address, and only it, that is searched."""
    gw.plant_gmail()
    message = gmsg("a1", "2026-10-18T08:00:00Z")
    message["payload"]["headers"][0]["value"] = header
    gw.gmail.box(GMAIL_BOX).extend([gsent("s1", "x@example.com"), message])
    gw.memory(channel(GMAIL_BOX, provider="gmail", cursor=CURSOR))
    _, _, context, _ = gw.run()
    assert [m["knownSender"] for m in all_messages(context)] == [True]
    (search,) = [u for _, u, _, _ in gw.gmail.log if "labelIds=SENT" in u]
    assert "q=to%3Ax%40example.com+OR+cc%3Ax%40example.com+OR+bcc%3Ax%40example.com&" in search


def test_KnownSender_PlainAddressesWithDotsPlusAndHyphens_AreSearched(gw):
    gw.plant_gmail()
    box = gw.gmail.box(GMAIL_BOX)
    box += [gsent("s1", "first.last+tag@mail-host.example.org")]
    box += [gmsg("a1", "2026-10-18T08:00:00Z", sender="First.Last+Tag@Mail-Host.Example.org")]
    gw.memory(channel(GMAIL_BOX, provider="gmail", cursor=CURSOR))
    _, _, context, _ = gw.run()
    assert [m["knownSender"] for m in all_messages(context)] == [True]


def test_KnownSender_AnAddressWrittenWithTheKelvinSign_IsNeverSearched(gw):
    """Review observation 4: U+212A lower-cases to an ASCII k, so only an address written in ASCII is searched for."""
    gw.plant_gmail()
    box = gw.gmail.box(GMAIL_BOX)
    box += [gsent("s1", "kate@example.com")]
    box += [gmsg("a1", "2026-10-18T08:00:00Z", sender="\u212aate@example.com")]
    gw.memory(channel(GMAIL_BOX, provider="gmail", cursor=CURSOR))
    _, _, context, _ = gw.run()
    assert [m["knownSender"] for m in all_messages(context)] == [False]
    assert not any("labelIds=SENT" in u for _, u, _, _ in gw.gmail.log)


def test_KnownSender_ALookAlikeHit_IsNotKnown(gw):
    """The search found a message, but its recipients hold only a longer address: the hit is not confirmed."""
    gw.plant_gmail()
    box = gw.gmail.box(GMAIL_BOX)
    box += [gsent("s1", "boss@example.org.evil.example.net", cc="other@example.com")]
    box += [gmsg("a1", "2026-10-18T08:00:00Z", sender="boss@example.org")]
    gw.memory(channel(GMAIL_BOX, provider="gmail", cursor=CURSOR))
    _, _, context, _ = gw.run()
    assert [m["knownSender"] for m in all_messages(context)] == [False]
    searches = [u for _, u, _, _ in gw.gmail.log if "labelIds=SENT" in u]
    confirms = [u for _, u, _, _ in gw.gmail.log if "/messages/s1?" in u]
    assert len(searches) == 1 and len(confirms) == 1 and "metadataHeaders=Bcc" in confirms[0]


def test_KnownSender_ASentCacheOnAGmailChannel_IsAFieldThisCheckDidNotWrite(gw):
    """G0 settled knownSender as one search per sender, so a Gmail channel never has a sentCache. One there fails
    that channel, naming the field, before any network call."""
    gw.plant_gmail()
    gw.memory(channel(GMAIL_BOX, provider="gmail", cursor=CURSOR,
                      sentCache={"refreshedUtc": "2026-10-18T06:00:00Z", "hashes": []}))
    sentence = phase2.channel_failed(gw, GMAIL_BOX).splitlines()[0]
    assert sentence == f"the channel {GMAIL_BOX} has a 'sentCache' this check did not write"
    assert gw.gmail.log == []


# ---------------------------------------------------------------------------------------------------- standard error


def test_StandardError_HoldsNoTokenSenderSubjectOrSnippet(gw):
    gw.plant(OUTLOOK_BOX)
    gw.graph.box(OUTLOOK_BOX)["inbox"].append(phase2.msg("o1", "2026-10-18T08:00:00Z", sender="outlooksender@example.org",
                                                         subject="OutlookSubjectWord", preview="OutlookPreviewWord"))
    gw.plant_gmail(token=token_json(GMAIL_BOX, expiry="2026-10-18T11:00:00Z"))
    box = gw.gmail.box(GMAIL_BOX)
    box += [gsent("s1", "secretpartner@example.net")]
    box += [gmsg("zz1q", "2026-10-18T08:00:00Z", sender="Gmail Sender Name <gmailsender@example.org>",
                 subject="GmailSubjectWord", snippet="GmailSnippetWord"),
            gmsg("zz2q", "2026-10-18T08:01:00Z", sender="secretpartner@example.net")]
    gw.memory(channel(OUTLOOK_BOX, cursor={"newest": "2026-10-18T07:00:00Z", "idsAtNewest": []}),
              channel(GMAIL_BOX, provider="gmail", mode="live", cursor=CURSOR))
    code, _, context, err = gw.run()
    assert code == 0 and len(all_messages(context)) == 3
    for secret in ("gtoken", "refresh-for", "example-client-secret", "apps.googleusercontent", "gmailsender",
                   "Gmail Sender Name", "GmailSubjectWord", "GmailSnippetWord", "secretpartner", "outlooksender",
                   "OutlookSubjectWord", "OutlookPreviewWord", "zz1q", "zz2q"):
        assert secret not in err, secret
    assert "desk@example.com: reported 2 new message(s)" in err
