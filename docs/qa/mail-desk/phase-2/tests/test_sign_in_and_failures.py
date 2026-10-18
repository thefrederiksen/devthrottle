"""Tests 6 and 7: a failed sign-in, and Graph failing, are failed checks that leave the memory exactly as it was.

A failed check is a non-zero exit with the reason on standard error, which reaches the Open problems card and the
08:00 email. The memory is NOT rewritten, so the Gateway keeps the old one and the same messages are read next run.
Every memory comparison is byte for byte.
"""

import os
import stat

import pytest

from conftest import AUTH, MACHINE, MAILBOX, all_messages, channel, msg

CURSOR = {"newest": "2026-10-18T07:00:00Z", "idsAtNewest": []}


def failed(world):
    before = world.state.read_bytes()
    code, output, _, err = world.run()
    assert code != 0
    assert output is None
    assert world.state.read_bytes() == before
    return err


# ---------------------------------------------------------------------------------------------------- test 6


def test_SignIn_NoneFound_FailsNamingTheMachineAndTheFix(world):
    world.memory(channel(nickname="devthrottle", cursor=CURSOR))
    err = failed(world)
    assert f"{MAILBOX} is not signed in on {MACHINE}" in err
    assert "run cc-outlook -a devthrottle auth" in err
    assert world.graph.log == []


def test_SignIn_NoneFoundAndNoNickname_TheFixNamesTheAddressNeverAGuessedNickname(world):
    world.memory(channel(cursor=CURSOR))
    err = failed(world)
    assert f"run cc-outlook -a {MAILBOX} auth" in err
    assert "-a devthrottle " not in err


def test_SignIn_MoreThanOneFound_FailsNamingEveryPathAndPicksNone(world):
    first = world.plant(instance="default")
    second = world.plant(instance="slot5")
    world.memory(channel(cursor=CURSOR))
    err = failed(world)
    assert "signed in more than once" in err
    assert str(first) in err and str(second) in err
    assert world.sign_in.authorities == []                             # it never opened either of them
    assert world.graph.log == []


def test_SignIn_RenewalFails_FailsSayingExpiredAndNamingTheFix(world):
    world.plant()
    world.sign_in.renew_fails.add(MAILBOX)
    world.memory(channel(nickname="devthrottle", cursor=CURSOR))
    err = failed(world)
    assert "has expired" in err and "run cc-outlook -a devthrottle auth" in err
    assert world.graph.log == []


def test_SignIn_TheCacheHoldsNoAccount_FailsNamingTheFix(world):
    world.plant()
    world.sign_in.accounts[MAILBOX] = False
    world.memory(channel(cursor=CURSOR))
    err = failed(world)
    assert "not signed in" in err and f"run cc-outlook -a {MAILBOX} auth" in err


def test_SignIn_OnlyUnderHomeOrTheOldFolder_IsNotFound(world):
    """No fallback: a sign-in under the HOME variable's folder, or in ~/.cc-director, is not used."""
    name = "email_devthrottle_com_msal.json"
    for folder in (world.home.parent / "scratch-home" / ".local" / "share" / "cc-director" / "instances" / "default",
                   world.home / ".cc-director"):
        tokens = folder / "config" / "outlook" / "tokens"
        tokens.mkdir(parents=True)
        (tokens / name).write_text("{}", encoding="utf-8")
    world.sign_in.accounts[MAILBOX] = True
    world.memory(channel(cursor=CURSOR))
    assert "is not signed in" in failed(world)


def test_SignIn_Renewed_IsWrittenBackToTheSameFileOwnerOnlyAndNotIntoTheMemory(world):
    path = world.plant(content='{"AccessToken": {}}')
    os.chmod(path, 0o644)
    world.sign_in.renews.add(MAILBOX)
    world.memory(channel(cursor=CURSOR))
    code, _, _, err = world.run()
    assert code == 0
    if os.name == "posix":
        assert stat.S_IMODE(path.stat().st_mode) == 0o600
    assert "token-for-" not in world.state.read_text(encoding="utf-8")
    assert "token-for-" not in err
    assert "written back" in err


def test_SignIn_AsksForMailReadWriteOnlyWithCcOutlooksAppAndAuthority(world):
    world.plant()
    world.memory(channel(cursor=CURSOR))
    world.run()
    assert world.sign_in.scopes_asked == [["https://graph.microsoft.com/Mail.ReadWrite"]]
    assert world.sign_in.authorities == [("f575e3f4-8f62-4bfb-8f30-f776e55b51bd",
                                          "https://login.microsoftonline.com/common")]


def test_CacheFileName_IsCcOutlooksRule(world):
    assert world.screen.outlook_cache_name(MAILBOX) == "email_devthrottle_com_msal.json"
    source = AUTH.read_text(encoding="utf-8")
    assert "safe_name = email.replace('@', '_').replace('.', '_')" in source
    assert "return TOKENS_DIR / f'{safe_name}_msal.json'" in source


# ---------------------------------------------------------------------------------------------------- test 7


@pytest.mark.parametrize("failure,reason", [(500, "answered HTTP 500"), (503, "answered HTTP 503"),
                                            ("unreachable", "could not be reached"),
                                            ("not-json", "not JSON")])
def test_Graph_Failing_IsAFailedCheckAndTheSameMailIsReadNextRun(world, failure, reason):
    world.plant()
    world.graph.box(MAILBOX)["inbox"] += [msg("id-1", "2026-10-18T09:00:00Z")]
    world.memory(channel(cursor=CURSOR))
    world.graph.fail = failure
    assert reason in failed(world)

    world.graph.fail = None
    code, _, context, _ = world.run()
    assert code == 0
    assert [m["key"] for m in all_messages(context)] == [world.screen.short_key(MAILBOX, "id-1")]


def test_Graph_APageWithoutValue_IsAFailedCheck(world, monkeypatch):
    world.plant()
    world.memory(channel(cursor=CURSOR))
    monkeypatch.setattr(world.graph, "answer", lambda *a: (200, {"items": []}))
    assert "without a 'value' list" in failed(world)


def test_Graph_AMessageWithoutAnId_IsAFailedCheck(world):
    world.plant()
    world.graph.box(MAILBOX)["inbox"] += [{"receivedDateTime": "2026-10-18T09:00:00Z", "subject": "x"}]
    world.memory(channel(cursor=CURSOR))
    assert "without an id" in failed(world)


def test_Graph_FailingAfterAMoveInLiveMode_KeepsTheQueueAndTheRetryIsAlreadyGone(world, monkeypatch):
    """The move happened, then reading new mail failed: the memory is not rewritten, so the item is still queued.
    The next run tries it again and Graph no longer has that id: "already-gone". Nothing is moved twice."""
    world.plant()
    world.graph.box(MAILBOX)["inbox"] += [msg("id-1", "2026-10-18T06:00:00Z")]
    key = world.screen.short_key(MAILBOX, "id-1")
    world.memory(channel(mode="live", cursor=CURSOR, keys={key: {"id": "id-1", "reportedUtc": "2026-10-18T06:05:00Z"}},
                         filingQueue=[{"key": key, "to": "archive"}],
                         sentCache={"refreshedUtc": "2026-10-18T11:00:00Z", "hashes": []}))
    real = world.graph.answer
    monkeypatch.setattr(world.graph, "answer", lambda m, *a: real(m, *a) if m == "POST" else (500, {}))
    failed(world)
    assert len(world.graph.box(MAILBOX)["archive"]) == 1
    monkeypatch.setattr(world.graph, "answer", real)
    _, _, context, _ = world.run()
    assert context["channels"][0]["filing"] == [{"key": key, "to": "archive", "result": "already-gone"}]
    assert len(world.graph.box(MAILBOX)["archive"]) == 1


def test_Graph_AMoveAnswering500_IsAFailedCheck(world, monkeypatch):
    world.plant()
    key = world.screen.short_key(MAILBOX, "id-1")
    world.memory(channel(mode="live", cursor=CURSOR, keys={key: {"id": "id-1", "reportedUtc": "2026-10-18T06:05:00Z"}},
                         filingQueue=[{"key": key, "to": "archive"}]))
    monkeypatch.setattr(world.graph, "answer", lambda *a: (500, {}))
    assert "moving a message to archive failed" in failed(world)


# ---------------------------------------------------------------------------------------------------- the memory


@pytest.mark.parametrize("content,reason", [("", "the memory is empty"), ("not json", "not JSON"),
                                            ('{"channels": []}', "holds no 'channels'"),
                                            ('{"cursor": {}}', "holds no 'channels'"),
                                            ('{"channels": [{"provider": "outlook", "mode": "observe"}]}', "no 'address'"),
                                            ('{"channels": [{"address": "email@devthrottle.com", "provider": "outlook", '
                                             '"mode": "sort"}]}', "must be observe or live")])
def test_Memory_NotWhatTheGatewayWrites_IsAFailedCheckWithNoNetwork(world, content, reason):
    world.plant()
    world.state.write_text(content, encoding="utf-8")
    assert reason in failed(world)
    assert world.graph.log == []


def test_Memory_TheSameChannelTwice_IsAFailedCheck(world):
    world.plant()
    world.memory(channel(), channel(address="Email@DevThrottle.com"))
    assert "in the memory twice" in failed(world)


def test_Memory_FieldsTheCheckDoesNotOwn_AreWrittenBackUnchanged(world):
    world.plant()
    gateway = {"channelId": "c-1", "machine": "devlinux", "notes": {"any": ["thing", 1]}}
    world.memory(channel(cursor=CURSOR, **gateway), rulesVersion=7, other={"x": True})
    world.run()
    kept = world.read_memory()
    assert kept["rulesVersion"] == 7 and kept["other"] == {"x": True}
    assert {k: kept["channels"][0][k] for k in gateway} == gateway


def test_Memory_OverTheDirectorsCap_IsAFailedCheckAndNothingIsWritten(world):
    world.plant()
    world.memory(channel(cursor=CURSOR, padding="x" * (64 * 1024)))
    assert "over the Director's 65536" in failed(world)


def test_Memory_NotSet_IsAFailedCheck(world, monkeypatch):
    monkeypatch.delenv("CC_CHECK_STATE_FILE")
    code, _, _, err = world.run()
    assert code != 0 and "CC_CHECK_STATE_FILE is not set" in err
