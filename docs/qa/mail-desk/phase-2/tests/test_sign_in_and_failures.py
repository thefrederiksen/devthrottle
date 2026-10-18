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


CLIENT = "f575e3f4-8f62-4bfb-8f30-f776e55b51bd"
ADD = f"first run cc-outlook accounts add {MAILBOX} -c {CLIENT}"
THEN = f"then run cc-outlook -a {MAILBOX} auth"


def first_line(err):
    return err.splitlines()[0]


def test_SignIn_NoneFound_TheFirstLineIsOnePlainSentenceNamingTheMachineAndTheFix(world):
    """The Gateway quotes the first line of a failure as it is on the Mail Desk page, so it is exactly one sentence;
    where the check looked follows on the next line."""
    world.register({MAILBOX: {"client_id": CLIENT, "nickname": "devthrottle"}})
    world.memory(channel(cursor=CURSOR))
    err = failed(world)
    assert first_line(err) == f"{MAILBOX} is not signed in on {MACHINE}: run cc-outlook -a devthrottle auth"
    assert "found none" in err.splitlines()[1]
    assert world.graph.log == []


def test_SignIn_TheNicknameIsCcOutlooksDerivedOneWhenNoneIsStored(world):
    world.register({MAILBOX: {"client_id": CLIENT}})                       # an account added before nicknames
    world.memory(channel(cursor=CURSOR))
    assert first_line(failed(world)) == f"{MAILBOX} is not signed in on {MACHINE}: run cc-outlook -a devthrottle auth"


def test_SignIn_TheNicknameIsReadFromTheRegistryNeverFromTheAddress(world):
    world.register({MAILBOX: {"client_id": CLIENT, "nickname": "desk"}})
    world.memory(channel(cursor=CURSOR))
    assert first_line(failed(world)).endswith(": run cc-outlook -a desk auth")


def test_SignIn_ANicknameThatLeadsToAnotherAccount_NamesTheAddressInstead(world):
    """cc-outlook's nickname lookup lets the last account win, so a nickname shared with a later account would sign in
    the wrong mailbox. The address always resolves exactly."""
    world.register({MAILBOX: {"client_id": CLIENT}, "other@devthrottle.example": {"client_id": CLIENT,
                                                                                 "nickname": "devthrottle"}})
    world.memory(channel(cursor=CURSOR))
    assert first_line(failed(world)).endswith(f": run cc-outlook -a {MAILBOX} auth")


def test_SignIn_InstancesDisagreeingOnTheNickname_NamesTheAddress(world):
    world.register({MAILBOX: {"client_id": CLIENT, "nickname": "devthrottle"}}, instance="default")
    world.register({MAILBOX: {"client_id": CLIENT, "nickname": "desk"}}, instance="slot5")
    world.memory(channel(cursor=CURSOR))
    assert first_line(failed(world)).endswith(f": run cc-outlook -a {MAILBOX} auth")


def test_SignIn_TheAccountIsInNoRegistry_SaysHowToAddItAndNeverGuessesANickname(world):
    world.register({"someone@example.com": {"client_id": CLIENT}})
    world.memory(channel(cursor=CURSOR))
    err = failed(world)
    assert first_line(err) == f"{MAILBOX} is not signed in on {MACHINE}: {ADD}"
    assert err.splitlines()[1].startswith(THEN)
    assert "-a devthrottle" not in err


def test_SignIn_AnUnreadableRegistry_NamesNoNickname(world):
    folder = world.base / "instances" / "default" / "config" / "outlook"
    folder.mkdir(parents=True)
    (folder / "profiles.json").write_text("{not json", encoding="utf-8")
    world.memory(channel(cursor=CURSOR))
    assert first_line(failed(world)) == f"{MAILBOX} is not signed in on {MACHINE}: {ADD}"


def test_SignIn_MoreThanOneFound_FailsNamingEveryPathAndPicksNone(world):
    first = world.plant(instance="default")
    second = world.plant(instance="slot5")
    world.memory(channel(cursor=CURSOR))
    err = failed(world)
    assert first_line(err).startswith(f"{MAILBOX} is signed in more than once on {MACHINE}")
    assert str(first) in err and str(second) in err
    assert world.sign_in.authorities == []                             # it never opened either of them
    assert world.graph.log == []


def test_SignIn_RenewalFails_IsNotSignedInWithExpiredOnTheNextLine(world):
    world.plant()
    world.register({MAILBOX: {"client_id": CLIENT, "nickname": "devthrottle"}})
    world.sign_in.renew_fails.add(MAILBOX)
    world.memory(channel(cursor=CURSOR))
    err = failed(world)
    assert first_line(err) == f"{MAILBOX} is not signed in on {MACHINE}: run cc-outlook -a devthrottle auth"
    assert "has expired and could not be renewed (invalid_grant)" in err.splitlines()[1]
    assert world.graph.log == []


def test_SignIn_TheCacheHoldsNoAccount_FailsNamingTheFix(world):
    world.plant()
    world.sign_in.accounts[MAILBOX] = False
    world.memory(channel(cursor=CURSOR))
    err = failed(world)
    assert first_line(err) == f"{MAILBOX} is not signed in on {MACHINE}: {ADD}"
    assert "holds no account" in err.splitlines()[1]


def test_Failure_TheRunLogFollowsTheSentence(world):
    world.memory(channel(cursor=CURSOR))
    err = failed(world)
    assert "is not signed in" in first_line(err)
    assert any(line.startswith("screen.py: 1 channel(s)") for line in err.splitlines()[1:])


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
    assert [m["messageKey"] for m in all_messages(context)] == [world.screen.short_key(MAILBOX, "id-1")]


def test_Graph_APageWithoutValue_IsAFailedCheck(world, monkeypatch):
    world.plant()
    world.memory(channel(cursor=CURSOR))
    monkeypatch.setattr(world.graph, "answer", lambda *a: (200, {"items": []}))
    assert "without a 'value' list" in failed(world)


def test_Graph_AMessageWithoutAnId_IsAFailedCheck(world):
    world.plant()
    world.graph.box(MAILBOX)["inbox"] += [{"receivedDateTime": "2026-10-18T09:00:00Z", "subject": "x"}]
    world.memory(channel(cursor=CURSOR))
    assert "without a usable id" in failed(world)


def test_Graph_FailingAfterAMoveInLiveMode_KeepsTheQueueAndTheRetryIsAlreadyGone(world, monkeypatch):
    """The move happened, then reading new mail failed: the memory is not rewritten, so the item is still queued.
    The next run tries it again and Graph no longer has that id: "already-gone". Nothing is moved twice."""
    world.plant()
    world.graph.box(MAILBOX)["inbox"] += [msg("id-1", "2026-10-18T06:00:00Z")]
    key = world.screen.short_key(MAILBOX, "id-1")
    world.memory(channel(mode="live", cursor=CURSOR, keys={key: {"id": "id-1", "reportedUtc": "2026-10-18T06:05:00Z"}},
                         filingQueue=[{"messageKey": key, "decision": "archive"}],
                         sentCache={"refreshedUtc": "2026-10-18T11:00:00Z", "hashes": []}))
    real = world.graph.answer
    monkeypatch.setattr(world.graph, "answer", lambda m, *a: real(m, *a) if m == "POST" else (500, {}))
    failed(world)
    assert len(world.graph.box(MAILBOX)["archive"]) == 1
    monkeypatch.setattr(world.graph, "answer", real)
    _, _, context, _ = world.run()
    assert context["channels"][0]["filing"] == [{"messageKey": key, "decision": "archive", "result": "already-gone"}]
    assert len(world.graph.box(MAILBOX)["archive"]) == 1


def test_Graph_AMoveAnswering500_IsAFailedCheck(world, monkeypatch):
    world.plant()
    key = world.screen.short_key(MAILBOX, "id-1")
    world.memory(channel(mode="live", cursor=CURSOR, keys={key: {"id": "id-1", "reportedUtc": "2026-10-18T06:05:00Z"}},
                         filingQueue=[{"messageKey": key, "decision": "archive"}]))
    monkeypatch.setattr(world.graph, "answer", lambda *a: (500, {}))
    err = failed(world)
    assert first_line(err) == f"Microsoft Graph answered HTTP 500 for {MAILBOX} while moving a message to archive"
    assert "id-1" not in err                                # the Graph id is on no line at all


# ---------------------------------------------------------------------------------------------------- the memory


@pytest.mark.parametrize("content,reason", [("", "memory is empty"), ("not json", "not JSON"),
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
    assert "memory twice" in failed(world)


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


# ---------------------------------------------------------------------------------------------------- observation 1


CAPITALS = "Email@DevThrottle.com"


def test_SignIn_ATokenFileNamedWithCapitals_IsFoundForTheLowerCaseChannel(world):
    """cc-outlook names the file after the address as it was typed (get_msal_token_path, from the profile key that
    save_profile stores as given). The Gateway's channel address is lower case, so the check matches the file name
    without regard to case; MSAL matches the account's username without regard to case too (get_accounts)."""
    path = world.plant(CAPITALS)
    assert path.name == "Email_DevThrottle_com_msal.json"
    world.sign_in.accounts[MAILBOX] = True
    world.memory(channel(cursor=CURSOR))
    code, _, _, _ = world.run()
    assert code == 0


def test_SignIn_TwoFilesDifferingOnlyInCase_AreMoreThanOne(world):
    world.plant(CAPITALS)
    world.plant(MAILBOX)
    world.memory(channel(cursor=CURSOR))
    assert first_line(failed(world)).startswith(f"{MAILBOX} is signed in more than once on {MACHINE}")


def test_SignIn_AnAccountRegisteredWithCapitals_IsNamedAsCcOutlookHoldsIt(world):
    """resolve_account matches the address key exactly and with case, so when the address must be named (here the
    instances disagree on the nickname) it is named as cc-outlook stored it, or -a would not find it."""
    world.register({CAPITALS: {"client_id": CLIENT, "nickname": "devthrottle"}}, instance="default")
    world.register({CAPITALS: {"client_id": CLIENT, "nickname": "desk"}}, instance="slot5")
    world.memory(channel(cursor=CURSOR))
    assert first_line(failed(world)) == f"{MAILBOX} is not signed in on {MACHINE}: run cc-outlook -a {CAPITALS} auth"


def test_SignIn_ANicknameLeadingToAnAccountRegisteredWithCapitals_IsUsed(world):
    world.register({CAPITALS: {"client_id": CLIENT, "nickname": "devthrottle"}})
    world.memory(channel(cursor=CURSOR))
    assert first_line(failed(world)).endswith(": run cc-outlook -a devthrottle auth")


def test_CcOutlook_MatchesTheAddressWithCaseAndNamesTheFileAsTyped(world):
    """The premise of the three tests above, read from cc-outlook's source rather than assumed."""
    source = AUTH.read_text(encoding="utf-8")
    assert "if account_name in profiles.get('profiles', {}):" in source        # resolve_account: exact, with case
    assert "if account_name.lower() in nickname_map:" in source                # nicknames: without case
    assert "profiles['profiles'][email] = {" in source                          # save_profile: the key as typed


# ---------------------------------------------------------------------------------------------------- re-review B


@pytest.mark.parametrize("held", [True, False], ids=["cache-holds-no-account", "no-sign-in-file"])
def test_SignIn_AnAccountListThatCannotBeRead_IsSaidAsItIs_NeverAsTheSignInService(world, monkeypatch, held):
    """cc-outlook's profiles.json is there but cannot be read (a permissions fault). The sentence says so; it is never
    "could not reach the Microsoft sign-in service", and never a guessed fix."""
    if held:
        world.plant()
        world.sign_in.accounts[MAILBOX] = False
    listed = world.register({MAILBOX: {"client_id": CLIENT, "nickname": "devthrottle"}})
    real = type(listed).read_text

    def read_text(self, *args, **kwargs):
        if self.name == "profiles.json":
            raise PermissionError(13, "Permission denied", str(self))
        return real(self, *args, **kwargs)

    monkeypatch.setattr(type(listed), "read_text", read_text)
    world.memory(channel(cursor=CURSOR))
    err = failed(world)
    assert first_line(err) == (f"{MAILBOX} is not signed in on {MACHINE}, and cc-outlook's account list cannot be "
                               f"read: PermissionError")
    assert "profiles.json" in err.splitlines()[1]
    assert "sign-in service" not in err
