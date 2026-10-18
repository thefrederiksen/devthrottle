"""Tests 8 and 9, and the rest of what a run reports: invisible characters, more than one channel, an unknown provider,
the Sent-folder cache, short keys and what standard error may say.
"""

import json

from conftest import MAILBOX, NOW, SECOND, all_messages, channel, channel_failed, msg, sent

CURSOR = {"newest": "2026-10-18T07:00:00Z", "idsAtNewest": []}

# The phase 0 finding, rebuilt with example text: a real line, then sender-inserted padding of combining grapheme
# joiners, zero-width non-joiners and soft hyphens between spaces, then the rest of the text.
PADDING = "͏‌­ " * 120
VISIBLE = ("Your invoice for October is ready. The total is 42 dollars and it is due on the first of next month. "
           "Open the billing page to read it, download it, or change how you pay. Thank you for being a customer of "
           "the example service.")


# ---------------------------------------------------------------------------------------------------- test 8


def test_Preview_PaddingIsStrippedBeforeTheCut(world):
    raw = "Hello there." + PADDING + VISIBLE
    naive = raw[:200]
    preview = world.screen.preview_of(raw)
    assert len("".join(ch for ch in naive if ch.isalnum())) < 20        # cut first, padding eats the preview
    assert preview == ("Hello there. " + VISIBLE)[:200]                # strip first, the preview is 200 real ones
    assert len(preview) == 200


def test_Preview_EveryKindOfInvisibleCharacter_IsStripped(world):
    raw = ("﻿A​B‌C‍D⁠E­F͏G᠎H⠀IㅤJ‮K\u0007L\x00M"
           "\t\n\r  N   OP")
    assert world.screen.clean_text(raw) == "ABCDEFGHIJKLM N OP"


def test_Preview_InTheReport_IsCleanedAndCut(world):
    world.plant()
    world.graph.box(MAILBOX)["inbox"] += [msg("id-1", "2026-10-18T09:00:00Z", preview="Hello there." + PADDING + VISIBLE,
                                              subject="​Invoice­  ready﻿", name="Billing‍ Team")]
    world.memory(channel(cursor=CURSOR))
    _, _, context, _ = world.run()
    reported = all_messages(context)[0]
    assert reported["preview"] == ("Hello there. " + VISIBLE)[:200]
    assert reported["subject"] == "Invoice ready" and reported["senderName"] == "Billing Team"


# ---------------------------------------------------------------------------------------------------- test 9


def test_TwoChannels_OneRun_EachSignedInSeparatelyAndReportedUnderItsOwnAddress(world):
    world.plant(MAILBOX)
    world.plant(SECOND)
    world.graph.box(MAILBOX)["inbox"] += [msg("a-1", "2026-10-18T08:00:00Z"), msg("same-id", "2026-10-18T08:20:00Z")]
    world.graph.box(SECOND)["inbox"] += [msg("b-1", "2026-10-18T08:10:00Z"), msg("same-id", "2026-10-18T08:30:00Z")]
    world.memory(channel(MAILBOX, cursor=CURSOR), channel(SECOND, mode="observe", cursor=CURSOR))

    code, output, context, _ = world.run()

    assert code == 0 and output["count"] == 4
    first, second = context["channels"]
    assert (first["address"], second["address"]) == (MAILBOX, SECOND)
    key = world.screen.short_key
    assert [m["messageKey"] for m in first["messages"]] == [key(MAILBOX, "a-1"), key(MAILBOX, "same-id")]
    assert [m["messageKey"] for m in second["messages"]] == [key(SECOND, "b-1"), key(SECOND, "same-id")]
    assert key(MAILBOX, "same-id") != key(SECOND, "same-id")           # a key names its channel
    tokens = {(url.split("/mailFolders/")[1].split("/")[0] if "/mailFolders/" in url else url, token)
              for _, url, _, token in world.graph.log}
    assert ("inbox", f"token-for-{MAILBOX}") in tokens and ("inbox", f"token-for-{SECOND}") in tokens
    memory = world.read_memory()["channels"]
    assert memory[0]["cursor"]["newest"] == "2026-10-18T08:20:00Z"
    assert memory[1]["cursor"]["newest"] == "2026-10-18T08:30:00Z"


def test_TwoChannels_TheTenMessageStopIsSharedOldestFirst(world):
    world.plant(MAILBOX)
    world.plant(SECOND)
    world.graph.box(MAILBOX)["inbox"] += [msg(f"a-{n}", f"2026-10-18T08:{2 * n:02}:00Z") for n in range(8)]
    world.graph.box(SECOND)["inbox"] += [msg(f"b-{n}", f"2026-10-18T08:{2 * n + 1:02}:00Z") for n in range(8)]
    world.memory(channel(MAILBOX, cursor=CURSOR), channel(SECOND, cursor=CURSOR))
    _, _, context, _ = world.run()
    first, second = context["channels"]
    assert len(first["messages"]) == 5 and len(second["messages"]) == 5
    assert first["more"] is True and second["more"] is True
    _, _, context, _ = world.run()
    assert [len(c["messages"]) for c in context["channels"]] == [3, 3]


def test_UnknownProvider_FailsThatChannelNamingItAndTheOtherIsRead(world):
    """Changed in phase 3 by the Delivery Lead's ruling of 18 October 2026: the unknown provider fails its own channel
    only, before anything is asked of the network for it, and the Outlook channel beside it is read."""
    world.plant()
    world.memory(channel(MAILBOX, cursor=CURSOR), channel(SECOND, provider="gmail"))
    assert "provider gmail has no reader in this version of screen.py" in channel_failed(world, SECOND)
    assert {entry[3] for entry in world.graph.log} == {f"token-for-{MAILBOX}"}
    assert world.sign_in.authorities == [("f575e3f4-8f62-4bfb-8f30-f776e55b51bd",
                                          "https://login.microsoftonline.com/common")]   # one sign-in: the Outlook one


def test_Providers_EverythingOutlookIsInsideTheOutlookReader(world):
    """Gmail is a new reader, not a redesign: the run itself names no Graph address, MSAL or cc-outlook."""
    source = open(world.screen.__file__, encoding="utf-8").read()
    run_body = source.split("\ndef run(")[1].split("\ndef main(")[0]
    for outlook_only in ("graph", "msal", "cc-outlook", "outlook_", "OUTLOOK"):
        assert outlook_only not in run_body
    assert world.screen.READERS == {"outlook": world.screen.OutlookReader}


# ---------------------------------------------------------------------------------------------------- known senders


def test_KnownSender_SomeoneInTheSentFolder_IsKnownAndTheCacheHoldsNoAddress(world):
    world.plant()
    box = world.graph.box(MAILBOX)
    box["sentitems"] += [sent("Customer@Example.org")]
    box["inbox"] += [msg("id-1", "2026-10-18T08:00:00Z", sender="customer@example.org"),
                     msg("id-2", "2026-10-18T08:05:00Z", sender="stranger@example.com")]
    world.memory(channel(cursor=CURSOR))
    _, _, context, _ = world.run()
    assert [m["knownSender"] for m in all_messages(context)] == [True, False]
    cache = world.read_memory()["channels"][0]["sentCache"]
    assert cache["refreshedUtc"] == "2026-10-18T12:00:00Z"
    assert "example.org" not in json.dumps(cache)


def test_KnownSender_TheSentFolderIsReadOnceADay(world, monkeypatch):
    world.plant()
    world.memory(channel(cursor=CURSOR))
    sent_reads = lambda: len([u for _, u, _, _ in world.graph.log if "/sentitems/" in u])
    world.run()
    world.run()
    assert sent_reads() == 1
    monkeypatch.setattr(world.screen, "utc_now", lambda: NOW.replace(day=19, hour=12, minute=1))
    world.run()
    assert sent_reads() == 2


# ---------------------------------------------------------------------------------------------------- short keys


def test_Keys_ReportedMessagesAreInTheTableWithTheirIds(world):
    world.plant()
    world.graph.box(MAILBOX)["inbox"] += [msg("AAMkAG-long-id=", "2026-10-18T08:00:00Z")]
    world.memory(channel(cursor=CURSOR))
    _, _, context, _ = world.run()
    key = all_messages(context)[0]["messageKey"]
    assert len(key) == 16 and "AAMkAG" not in context.__repr__()
    assert world.read_memory()["channels"][0]["keys"][key] == {"id": "AAMkAG-long-id=", "reportedUtc": "2026-10-18T12:00:00Z"}


def test_Keys_PrunedToTheFortyNewestUnderSevenDaysButQueuedKeysAreKept(world):
    screen = world.screen
    keys = {f"k{n:03}": {"id": f"id-{n}", "reportedUtc": f"2026-10-{10 + n // 20:02}T00:{n % 60:02}:00Z"}
            for n in range(100)}
    queue = [{"messageKey": "k000", "decision": "archive"}]
    kept = screen.prune_keys(keys, queue, NOW)
    assert "k000" in kept                                              # oldest, but still queued
    assert set(kept) - {"k000"} == {f"k{n:03}" for n in range(60, 100)}
    old = {"k1": {"id": "x", "reportedUtc": "2026-10-10T00:00:00Z"}}
    assert screen.prune_keys(old, [], NOW) == {}                       # over 7 days and not queued


# ---------------------------------------------------------------------------------------------------- the log


def test_StandardError_NamesNoSenderSubjectBodyOrToken(world):
    world.plant()
    world.graph.box(MAILBOX)["inbox"] += [msg("id-1", "2026-10-18T08:00:00Z", sender="private.person@example.org",
                                              name="Private Person", subject="A secret subject", preview="Body words")]
    world.memory(channel(cursor=CURSOR))
    _, _, _, err = world.run()
    for leaked in ("private.person", "Private Person", "secret subject", "Body words", "token-for"):
        assert leaked not in err
    assert "reported 1 new message" in err


def test_Output_IsExactlyCountAndContext(world, capsys):
    world.plant()
    world.memory(channel(cursor=CURSOR))
    capsys.readouterr()
    world.screen.main()
    out = capsys.readouterr().out
    assert list(json.loads(out)) == ["count", "context"]
    assert out.endswith("\n") and out.count("\n") == 1


def test_ChannelId_TheGatewaysIdIsCarriedIntoTheReport(world):
    world.plant()
    world.memory(channel(id="c-0001", cursor=CURSOR))
    _, _, context, _ = world.run()
    assert context["channels"][0]["id"] == "c-0001"


def test_UnknownProvider_TheSentenceIsTheChannelsFailure(world):
    world.memory(channel(provider="gmail"))
    assert channel_failed(world).splitlines()[0] == ("provider gmail has no reader in this version of screen.py (channel "
                                   "email@devthrottle.com); this version reads: outlook")
