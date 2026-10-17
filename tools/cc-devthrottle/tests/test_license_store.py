"""The key store's unit tests - the License Switching mission's check for the key store (MISSION.md section 7).

What these prove, each on the Windows/Linux credentials-file backend AND on the macOS Keychain backend (driven
through a fake `security` so it runs on any machine):

  * a switch EXCHANGES the active sign-in with a spare's, and the account block (`oauthAccount`) follows the key;
  * after every operation - add, remove, switch, a crash at every single change point of each, and the recovery
    after it (including a second crash during the recovery) - each sign-in exists in exactly one place;
  * the macOS Keychain items carry Claude Code's exact service names;
  * the machine's real default folder, and the real Keychain item, are refused;
  * a key never reaches an exception, a log, the journal or a command line.

Every folder is under pytest's tmp_path. Nothing here can reach the real ~/.claude or the real Keychain.
"""

from __future__ import annotations

import datetime
import hashlib
import json
import os
import re
import sys
import unicodedata
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import license_store  # noqa: E402
from src.license_store import (  # noqa: E402
    FileKeyBackend,
    HeldKey,
    KeychainKeyBackend,
    LicenseStore,
    LicenseStoreError,
    RunResult,
    keychain_service_for,
)

ACCOUNT_A = "account-a@example.com"
ACCOUNT_B = "account-b@example.com"
ACCOUNT_C = "account-c@example.com"


# ===== a fake `security`, so the Keychain backend runs anywhere =====


class FakeSecurity:
    """Holds Keychain items as {service: [(account, secret), ...]} and answers `security` the way macOS does.
    Records every call so a test can prove no secret was ever on a command line."""

    def __init__(self):
        self.items: dict[str, list[tuple[str, str]]] = {}
        self.calls: list[tuple[list[str], str | None]] = []

    def __call__(self, args, stdin):
        args = list(args)
        self.calls.append((args, stdin))
        if args[0] == "find-generic-password":
            service = args[args.index("-s") + 1]
            held = self.items.get(service)
            if not held:
                return RunResult(44, "", "security: SecKeychainSearchCopyNext: The specified item could not be found.")
            account, secret = held[0]
            if "-w" in args:
                return RunResult(0, secret + "\n", "")
            return RunResult(0, f'keychain: "login.keychain-db"\nattributes:\n    "acct"<blob>="{account}"\n'
                                f'    "svce"<blob>="{service}"\n', "")
        if args[0] == "-i":
            match = re.fullmatch(r'add-generic-password -U -a "([^"]*)" -s "([^"]*)" -X "([0-9a-f]+)"\n', stdin)
            assert match, f"unexpected interactive input shape: {stdin[:60]!r}"
            account, service, payload = match.group(1), match.group(2), bytes.fromhex(match.group(3)).decode("utf-8")
            held = self.items.setdefault(service, [])
            for i, (acct, _) in enumerate(held):
                if acct == account:
                    held[i] = (account, payload)
                    break
            else:
                held.append((account, payload))
            return RunResult(0, "", "")
        if args[0] == "delete-generic-password":
            service = args[args.index("-s") + 1]
            held = self.items.get(service)
            if not held:
                return RunResult(44, "", "")
            held.pop(0)
            if not held:
                del self.items[service]
            return RunResult(0, "", "")
        raise AssertionError(f"unexpected security call: {args}")


# ===== the world: folders, sign-ins and the invariant =====


class SimulatedCrash(Exception):
    pass


class World:
    """An isolated machine: a default config folder, a store, and a place to sign accounts in."""

    def __init__(self, tmp_path: Path, kind: str):
        self.kind = kind
        self.root = tmp_path
        self.default = str(tmp_path / "home" / "claude-default")
        self.store_dir = str(tmp_path / "store")
        self.signins = tmp_path / "signins"
        self.security = FakeSecurity() if kind == "keychain" else None
        self.secrets: dict[str, str] = {}  # secret -> email it belongs to
        self._serial = 0

    def backend(self):
        return KeychainKeyBackend(runner=self.security) if self.kind == "keychain" else FileKeyBackend()

    def store(self, checkpoint=None) -> LicenseStore:
        return LicenseStore(self.default, self.store_dir, self.backend(), checkpoint=checkpoint)

    def sign_in(self, folder: str, email: str, keychain_account: str = "claude-code-user") -> str:
        """What `claude auth login` leaves in a folder: a key and an account file with oauthAccount."""
        self._serial += 1
        secret = json.dumps({"claudeAiOauth": {
            "accessToken": f"sk-ant-oat01-SECRET-ACCESS-{self._serial}-{email}",
            "refreshToken": f"sk-ant-ort01-SECRET-REFRESH-{self._serial}-{email}",
            "expiresAt": 1999999999000, "scopes": ["user:inference"], "subscriptionType": "max"}})
        self.secrets[secret] = email
        Path(folder).mkdir(parents=True, exist_ok=True)
        self.backend().write(folder, HeldKey(secret, keychain_account=keychain_account))
        (Path(folder) / ".claude.json").write_text(json.dumps({
            "numStartups": 7,
            "oauthAccount": {"emailAddress": email, "organizationName": f"Org {email.split('@')[0]}"},
        }), encoding="utf-8")
        return secret

    def new_signin_folder(self, email: str) -> str:
        folder = str(self.signins / email.split("@")[0])
        self.sign_in(folder, email)
        return folder

    def folders(self) -> list[str]:
        found = [self.default]
        spares = Path(self.store_dir) / "spares"
        if spares.is_dir():
            found += [str(p) for p in sorted(spares.iterdir()) if p.is_dir()]
        if self.signins.is_dir():
            found += [str(p) for p in sorted(self.signins.iterdir()) if p.is_dir()]
        found += self.backups()
        return found

    def backups(self) -> list[str]:
        """The folders a heal of a hand sign-in moved stale spares into."""
        root = Path(self.store_dir) / "hand-sign-in-backups"
        return [str(p) for p in sorted(root.iterdir()) if p.is_dir()] if root.is_dir() else []

    def key_at(self, folder: str) -> str | None:
        key = self.backend().read(folder)
        return key.reveal() if key else None

    def email_at(self, folder: str) -> str | None:
        path = Path(folder) / ".claude.json"
        if not path.is_file():
            return None
        return json.loads(path.read_text(encoding="utf-8")).get("oauthAccount", {}).get("emailAddress")

    def snapshot(self) -> dict[str, tuple[str | None, str | None]]:
        """{folder: (key, email)} for every folder that holds a key."""
        return {f: (self.key_at(f), self.email_at(f)) for f in self.folders() if self.key_at(f) is not None}

    def assert_each_signin_in_exactly_one_place(self) -> None:
        places: dict[str, list[str]] = {}
        for folder, (key, email) in self.snapshot().items():
            places.setdefault(key, []).append(folder)
            assert email == self.secrets[key], f"{folder} holds {self.secrets[key]}'s key but says {email}"
        for key, where in places.items():
            assert len(where) == 1, f"{self.secrets[key]}'s sign-in is in {len(where)} places: {where}"
        for key, email in self.secrets.items():
            if email in self.removed or key in self.gone_ok:
                continue
            assert key in places, f"{email}'s sign-in was lost"
        # The account block travels with its key: a folder must never keep one after its key has gone.
        for folder in self.folders():
            if self.key_at(folder) is None:
                assert self.email_at(folder) is None, (
                    f"{folder} still names {self.email_at(folder)} but holds no key")

    removed: set[str] = set()
    #: Keys an operation sets out to replace (a renewal's old sign-in): they may be gone afterwards.
    gone_ok: frozenset[str] = frozenset()


@pytest.fixture(params=["file", "keychain"])
def world(tmp_path, request):
    w = World(tmp_path, request.param)
    w.removed = set()
    return w


def _held(world: World) -> dict[str, str]:
    return {a.email: a.role for a in world.store().held_accounts()}


def _machine_with_active_and_spare(world: World) -> None:
    world.sign_in(world.default, ACCOUNT_A)
    world.store().add(world.new_signin_folder(ACCOUNT_B))


# ===== exchange: the switch itself =====


def test_exchange_SwitchToSpare_ActiveAndSpareTradePlaces(world):
    _machine_with_active_and_spare(world)
    key_a, key_b = world.key_at(world.default), world.key_at(world.store().spare_folder(ACCOUNT_B))

    result = world.store().switch(ACCOUNT_B)

    assert (result.active, result.previous, result.changed) == (ACCOUNT_B, ACCOUNT_A, True)
    assert world.key_at(world.default) == key_b
    assert world.key_at(world.store().spare_folder(ACCOUNT_A)) == key_a
    assert world.key_at(world.store().spare_folder(ACCOUNT_B)) is None
    assert _held(world) == {ACCOUNT_B: "active", ACCOUNT_A: "spare"}
    world.assert_each_signin_in_exactly_one_place()


def test_exchange_OauthAccountFollowsTheKey_AndTheRestOfTheAccountFileIsKept(world):
    _machine_with_active_and_spare(world)

    world.store().switch(ACCOUNT_B)

    data = json.loads((Path(world.default) / ".claude.json").read_text(encoding="utf-8"))
    assert data["oauthAccount"]["emailAddress"] == ACCOUNT_B
    assert data["oauthAccount"]["organizationName"] == "Org account-b"
    assert data["numStartups"] == 7
    assert world.email_at(world.store().spare_folder(ACCOUNT_A)) == ACCOUNT_A
    world.assert_each_signin_in_exactly_one_place()


def test_exchange_SwitchBackAndForth_EverySigninStaysInOnePlace(world):
    _machine_with_active_and_spare(world)
    world.store().add(world.new_signin_folder(ACCOUNT_C))

    for target in [ACCOUNT_B, ACCOUNT_C, ACCOUNT_A, ACCOUNT_C, ACCOUNT_B]:
        world.store().switch(target)
        assert _held(world)[target] == "active"
        world.assert_each_signin_in_exactly_one_place()
    assert len(_held(world)) == 3


def test_exchange_AlreadyActive_ChangesNothing(world):
    _machine_with_active_and_spare(world)
    before = world.snapshot()

    result = world.store().switch(ACCOUNT_A)

    assert result.changed is False
    assert world.snapshot() == before


def test_exchange_DefaultFolderSignedOut_SpareMovesIn(world):
    world.store().add(world.new_signin_folder(ACCOUNT_B))

    result = world.store().switch(ACCOUNT_B)

    assert (result.active, result.previous) == (ACCOUNT_B, None)
    assert _held(world) == {ACCOUNT_B: "active"}
    world.assert_each_signin_in_exactly_one_place()


def test_exchange_UnknownAccount_IsRefusedAsSpareNotHeld(world):
    _machine_with_active_and_spare(world)
    with pytest.raises(LicenseStoreError, match="not held here") as refused:
        world.store().switch(ACCOUNT_C)
    assert refused.value.reason_code == "spare-not-held"
    assert refused.value.previous_active == ACCOUNT_A


# ===== the contract's switch vocabulary (missions/license-switching/contract.md, sections 3.3 and 3.4) =====


def test_contract_ExpectActiveMatches_Switches(world):
    _machine_with_active_and_spare(world)
    result = world.store().switch(ACCOUNT_B, expect_active=ACCOUNT_A)
    assert result.changed and result.active == ACCOUNT_B
    assert result.exchange_milliseconds >= 0 and result.finished_at >= result.started_at


def test_contract_ExpectActiveDiffers_IsActiveAccountChangedAndNothingMoves(world):
    _machine_with_active_and_spare(world)
    before = world.snapshot()
    with pytest.raises(LicenseStoreError) as refused:
        world.store().switch(ACCOUNT_B, expect_active=ACCOUNT_C)
    assert refused.value.reason_code == "active-account-changed"
    assert world.snapshot() == before


def test_contract_AlreadyActive_IsCheckedBeforeExpectActive(world):
    # Contract step 6 comes before step 7: a target that is already active is "already-active", whatever
    # the Gateway expected.
    _machine_with_active_and_spare(world)
    result = world.store().switch(ACCOUNT_A, expect_active=ACCOUNT_C)
    assert result.changed is False and result.exchange_milliseconds == 0


def test_contract_ExpectActiveIsCheckedBeforeTheSpare(world):
    _machine_with_active_and_spare(world)
    with pytest.raises(LicenseStoreError) as refused:
        world.store().switch(ACCOUNT_C, expect_active=ACCOUNT_B)
    assert refused.value.reason_code == "active-account-changed"


def test_contract_AnotherOperationRunning_IsSwitchInProgress(world):
    _machine_with_active_and_spare(world)
    holder = world.store()
    with holder._lock:
        with pytest.raises(LicenseStoreError) as refused:
            world.store().switch(ACCOUNT_B)
    assert refused.value.reason_code == "switch-in-progress"


def test_contract_ReadBackWrong_IsVerifyFailed(world):
    # A running Claude Code rewrites its account file whenever it likes. If it puts an older account block
    # back just after the exchange, the read-back must say so rather than report a clean switch.
    _machine_with_active_and_spare(world)
    store = world.store()
    real_clear = store._clear_journal

    def clear_then_another_writer_rewrites(*args):
        real_clear(*args)
        path = Path(world.default) / ".claude.json"
        data = json.loads(path.read_text(encoding="utf-8"))
        data["oauthAccount"]["emailAddress"] = ACCOUNT_A
        path.write_text(json.dumps(data), encoding="utf-8")

    store._clear_journal = clear_then_another_writer_rewrites
    with pytest.raises(LicenseStoreError) as refused:
        store.switch(ACCOUNT_B)
    assert refused.value.reason_code == "verify-failed"


def _switch_crash_point(tmp_path: Path, kind: str, label: str, occurrence: int = 0) -> int:
    """The change point (1-based) of the n-th occurrence of ``label`` in a switch, counted on a probe run."""
    probe = World(tmp_path / f"probe-{label}-{occurrence}", kind)
    probe.removed = set()
    _machine_with_active_and_spare(probe)
    labels = []
    probe.store(checkpoint=labels.append).switch(ACCOUNT_B)
    return [i for i, seen in enumerate(labels) if seen == label][occurrence] + 1


def test_review_RollBackNeverOverwritesASigninWrittenAfterTheCrash(world, tmp_path):
    # The review's reproduction: crash at the delete of the spare's copy (A put away, B brought in), then a
    # login writes C into the default folder before anyone recovers. Rolling back must not copy A over C.
    crash_at = _switch_crash_point(tmp_path, world.kind, "drop-key")
    _machine_with_active_and_spare(world)
    assert _run_with_crash_at(world, crash_at, _switch)
    world.sign_in(world.default, ACCOUNT_C)

    with pytest.raises(LicenseStoreError) as refused:
        world.store().recover()

    assert refused.value.reason_code == "exchange-failed-unknown"
    assert world.email_at(world.default) == ACCOUNT_C
    world.assert_each_signin_in_exactly_one_place()  # A, B and C each still in exactly one place
    assert (Path(world.store_dir) / "journal.json").exists()


def test_review_RollForwardNeverOverwritesASigninWrittenAfterTheCrash(world, tmp_path):
    # The same in the other direction: crash while bringing B in, C written into the default folder; the
    # re-run of "bring B in" must not overwrite C either.
    crash_at = _switch_crash_point(tmp_path, world.kind, "copy-key", occurrence=1)
    _machine_with_active_and_spare(world)
    assert _run_with_crash_at(world, crash_at, _switch)
    world.sign_in(world.default, ACCOUNT_C)

    with pytest.raises(LicenseStoreError):
        world.store().recover()

    assert world.email_at(world.default) == ACCOUNT_C
    world.assert_each_signin_in_exactly_one_place()


def test_fixRound_RemoveRecoveryNeverDeletesASigninMadeAfterTheCrash(world, tmp_path):
    # The fix-round review's reproduction: remove crashes after B's key is deleted, then a login puts C into
    # the same, now empty, spare folder before anyone recovers. Recovery must finish removing B only.
    labels = []
    probe = World(tmp_path / "probe-remove-c", world.kind)
    probe.removed = set()
    _machine_with_active_and_spare(probe)
    probe.store(checkpoint=labels.append).remove(ACCOUNT_B)
    _machine_with_active_and_spare(world)
    assert _run_with_crash_at(world, labels.index("erase-account") + 1, _remove)
    spare_b = world.store().spare_folder(ACCOUNT_B)
    world.removed.add(ACCOUNT_B)
    key_c = world.sign_in(spare_b, ACCOUNT_C)

    world.store().held_accounts()

    assert world.key_at(spare_b) == key_c and world.email_at(spare_b) == ACCOUNT_C
    world.assert_each_signin_in_exactly_one_place()
    assert not (Path(world.store_dir) / "journal.json").exists()
    assert "erase-left-alone" in (Path(world.store_dir) / "license-store.log").read_text(encoding="utf-8")


def test_fixRound_RemoveJournalHoldsAFingerprintNotTheKey(world, tmp_path):
    _machine_with_active_and_spare(world)
    key_b = world.key_at(world.store().spare_folder(ACCOUNT_B))
    assert _run_with_crash_at(world, 2, _remove)  # just after the journal was written
    journal = json.loads((Path(world.store_dir) / "journal.json").read_text(encoding="utf-8"))
    (step,) = journal["steps"]
    assert step["fingerprint"] == hashlib.sha256(key_b.encode("utf-8")).hexdigest()
    assert "SECRET" not in json.dumps(journal)


def test_fixRound_AddRecoveryNeverDeletesASigninMadeAfterTheCrash(world, tmp_path):
    # The same rule for the other step that deletes: add crashes after the source folder's key is deleted,
    # then a login puts D into the source folder. Recovery must not clear D's account block or key.
    labels = []
    probe = World(tmp_path / "probe-add-d", world.kind)
    probe.removed = set()
    probe.sign_in(probe.default, ACCOUNT_A)
    probe_source = probe.new_signin_folder(ACCOUNT_B)
    probe.store(checkpoint=labels.append).add(probe_source)
    world.sign_in(world.default, ACCOUNT_A)
    source = world.new_signin_folder(ACCOUNT_B)
    assert _run_with_crash_at(world, labels.index("drop-account") + 1, lambda store: store.add(source))
    assert world.key_at(source) is None
    key_d = world.sign_in(source, "account-d@example.com")

    try:
        world.store().held_accounts()
    except LicenseStoreError:
        pass  # stopping with nothing changed is an acceptable answer; losing D is not

    assert world.key_at(source) == key_d and world.email_at(source) == "account-d@example.com"
    world.assert_each_signin_in_exactly_one_place()


@pytest.mark.parametrize("operation_name", ["add", "remove"])
def test_fixRound_RecoveryLeavesAnotherAccountsBlockAlone(world, tmp_path, operation_name):
    # The key is already gone, and before recovery something writes ANOTHER account's block into the folder
    # (a login that has written its account file but not yet its key). Recovery clears only the block of the
    # account it moved or removed, never someone else's.
    labels = []
    probe = World(tmp_path / f"probe-{operation_name}-block", world.kind)
    probe.removed = set()
    _machine_with_active_and_spare(probe)
    probe_source = probe.new_signin_folder(ACCOUNT_C)
    if operation_name == "add":
        probe.store(checkpoint=labels.append).add(probe_source)
        crash_label = "drop-account"
    else:
        probe.store(checkpoint=labels.append).remove(ACCOUNT_B)
        crash_label = "erase-account"
    _machine_with_active_and_spare(world)
    source = world.new_signin_folder(ACCOUNT_C)
    operation = (lambda store: store.add(source)) if operation_name == "add" else _remove
    folder = source if operation_name == "add" else world.store().spare_folder(ACCOUNT_B)
    assert _run_with_crash_at(world, labels.index(crash_label) + 1, operation)
    assert world.key_at(folder) is None
    path = Path(folder) / ".claude.json"
    data = json.loads(path.read_text(encoding="utf-8"))
    data["oauthAccount"] = {"emailAddress": "account-d@example.com"}
    path.write_text(json.dumps(data), encoding="utf-8")

    world.store().held_accounts()

    assert world.email_at(folder) == "account-d@example.com"


def test_review_RemoveCrashBetweenKeyAndAccountBlock_TheNextRunClearsTheBlock(world, tmp_path):
    _machine_with_active_and_spare(world)
    labels = []
    probe = World(tmp_path / "probe-remove", world.kind)
    probe.removed = set()
    _machine_with_active_and_spare(probe)
    probe.store(checkpoint=labels.append).remove(ACCOUNT_B)
    assert _run_with_crash_at(world, labels.index("erase-account") + 1, _remove)
    spare_b = world.store().spare_folder(ACCOUNT_B)
    assert world.key_at(spare_b) is None and world.email_at(spare_b) == ACCOUNT_B, "crash was not between the two"

    world.store().held_accounts()
    world.removed.add(ACCOUNT_B)

    assert world.email_at(spare_b) is None
    world.assert_each_signin_in_exactly_one_place()


def _refuse_the_final_delete(then=None):
    """A checkpoint that makes the switch's last step (deleting the spare's copy) find it cannot go on, the way
    `_step_drop` does when the copy is not provably held elsewhere, so the switch rolls back. ``then`` is called
    for every later change point (to crash during the roll back)."""
    state = {"refused": False}

    def checkpoint(label):
        if label == "drop-key" and not state["refused"]:
            state["refused"] = True
            raise license_store._StepFailed("the spare's copy is not provably held elsewhere (test)")
        if state["refused"] and then is not None:
            then(label)

    return checkpoint


def test_contract_RollBackAfterTheSpareCameIn_PutsTheOldActiveSigninBack(world):
    # Undoing "bring the spare in" must restore the old active sign-in to the default folder, not leave it
    # empty (an empty default folder would sign the machine out).
    _machine_with_active_and_spare(world)
    key_a = world.key_at(world.default)

    with pytest.raises(LicenseStoreError, match="rolled back") as refused:
        world.store(checkpoint=_refuse_the_final_delete()).switch(ACCOUNT_B)

    assert refused.value.reason_code == "exchange-failed-restored"
    assert world.key_at(world.default) == key_a and world.email_at(world.default) == ACCOUNT_A
    assert _held(world) == {ACCOUNT_A: "active", ACCOUNT_B: "spare"}
    world.assert_each_signin_in_exactly_one_place()


def test_contract_SpareRefreshedMidSwitch_StopsWithNothingLost(world, tmp_path):
    # Crash after B came in; B's spare copy is then refreshed (a new key). Neither the delete of the spare's
    # copy nor the undo can be proven safe - the default folder holds the OLD B key, held nowhere else - so
    # recovery stops, changes nothing, and keeps the journal.
    crash_at = _switch_crash_point(tmp_path, world.kind, "journal", occurrence=2) + 1
    _machine_with_active_and_spare(world)
    assert _run_with_crash_at(world, crash_at, _switch)
    spare_b = world.store().spare_folder(ACCOUNT_B)
    assert world.key_at(world.default) == world.key_at(spare_b), "the crash came before step 2 finished"
    world.sign_in(spare_b, ACCOUNT_B)  # the refreshed key
    before = world.snapshot()

    with pytest.raises(LicenseStoreError) as refused:
        world.store().recover()

    assert refused.value.reason_code == "exchange-failed-unknown"
    assert world.snapshot() == before
    assert (Path(world.store_dir) / "journal.json").exists()


@pytest.mark.parametrize("kind", ["file", "keychain"])
def test_contract_CrashDuringTheRollBack_TheNextRunResumesIt(tmp_path, kind):
    def make(tag):
        w = World(tmp_path / tag, kind)
        w.removed = set()
        _machine_with_active_and_spare(w)
        return w

    rollback_labels = []
    counter = make("count")
    with pytest.raises(LicenseStoreError):
        counter.store(checkpoint=_refuse_the_final_delete(then=rollback_labels.append)).switch(ACCOUNT_B)
    assert len(rollback_labels) >= 4, rollback_labels

    for n in range(1, len(rollback_labels) + 1):
        w = make(f"rb{n}")
        key_a = w.key_at(w.default)
        seen = {"count": 0}

        def crash_at_n(label):
            seen["count"] += 1
            if seen["count"] == n:
                raise SimulatedCrash(label)

        with pytest.raises(SimulatedCrash):
            w.store(checkpoint=_refuse_the_final_delete(then=crash_at_n)).switch(ACCOUNT_B)
        try:
            w.store().recover()
        except LicenseStoreError as exc:
            assert exc.reason_code == "exchange-failed-restored", f"crash at {rollback_labels[n - 1]}: {exc}"
        w.assert_each_signin_in_exactly_one_place()
        if n == 1:
            # The crash came before the roll back was recorded, so the journal still says "go forward" and
            # the next run finishes the switch instead. Either whole outcome is correct.
            assert _held(w) in ({ACCOUNT_A: "active", ACCOUNT_B: "spare"}, {ACCOUNT_B: "active", ACCOUNT_A: "spare"})
            continue
        assert _held(w) == {ACCOUNT_A: "active", ACCOUNT_B: "spare"}, f"crash at {rollback_labels[n - 1]}"
        assert w.key_at(w.default) == key_a


def test_contract_DiskErrorOnceMidExchange_IsFinishedAtOnce(world):
    _machine_with_active_and_spare(world)
    calls = {"n": 0}

    def fail_once(label):
        calls["n"] += 1
        if calls["n"] == 5:  # inside the second step, bringing the spare in
            raise OSError("disk hiccup")

    result = world.store(checkpoint=fail_once).switch(ACCOUNT_B)

    assert result.changed and _held(world) == {ACCOUNT_B: "active", ACCOUNT_A: "spare"}
    world.assert_each_signin_in_exactly_one_place()


def test_contract_DiskErrorThatPersists_IsExchangeFailedUnknown_AndTheNextRunRecovers(world):
    _machine_with_active_and_spare(world)
    calls = {"n": 0}

    def fail_from_five(label):
        calls["n"] += 1
        if calls["n"] >= 5:
            raise OSError("disk gone")

    with pytest.raises(LicenseStoreError) as refused:
        world.store(checkpoint=fail_from_five).switch(ACCOUNT_B)
    assert refused.value.reason_code == "exchange-failed-unknown"
    assert (Path(world.store_dir) / "journal.json").exists()

    world.store().held_accounts()
    world.assert_each_signin_in_exactly_one_place()


def test_exchange_ActiveAccountAlsoHeldAsSpare_TheStaleSpareIsHealedAwayFirstAndTheSwitchRuns(world):
    # Someone ran `claude auth login` straight into the default folder for an account the store holds a
    # spare of. Before the hand sign-in heal this switch was refused (putting the active key away would have
    # overwritten the spare). Now the stale spare is moved to a backup first, so the switch has room and runs,
    # and nothing is overwritten: the old spare is in its backup, the hand sign-in is a spare again.
    _machine_with_active_and_spare(world)
    old_spare_b = world.key_at(str(Path(world.store_dir) / "spares" / license_store.spare_slug(ACCOUNT_B)))
    hand_b = world.sign_in(world.default, ACCOUNT_B)
    world.store().add(world.new_signin_folder(ACCOUNT_C))

    world.store().switch(ACCOUNT_C)

    assert _held(world) == {ACCOUNT_C: "active", ACCOUNT_B: "spare"}
    assert world.key_at(str(Path(world.store_dir) / "spares" / license_store.spare_slug(ACCOUNT_B))) == hand_b
    [backup] = world.backups()
    assert world.key_at(backup) == old_spare_b
    world.gone_ok = frozenset(k for k, e in world.secrets.items() if e == ACCOUNT_A)  # overwritten by the hand sign-in
    world.assert_each_signin_in_exactly_one_place()


# ===== a sign-in made by hand into the default folder for an account held as a spare =====


def _files(folder: str) -> dict[str, bytes]:
    """Every file under `folder`, by its path relative to it, byte for byte."""
    root = Path(folder)
    return {str(p.relative_to(root)): p.read_bytes() for p in sorted(root.rglob("*")) if p.is_file()}


def _hand_sign_in_over_a_spare(world: World) -> tuple[str, dict[str, bytes], str]:
    """A on the machine and B held as a spare; then B signed in BY HAND into the default folder, overwriting A.
    Returns the spare folder, its files byte for byte, and the spare's key, all as they were before any heal."""
    _machine_with_active_and_spare(world)
    spare = str(Path(world.store_dir) / "spares" / license_store.spare_slug(ACCOUNT_B))
    (Path(spare) / "projects-cache.txt").write_text("something Claude Code keeps in a config folder", encoding="utf-8")
    files, key = _files(spare), world.key_at(spare)
    world.sign_in(world.default, ACCOUNT_B)
    # A's key was overwritten by the hand sign-in: gone, and nothing here can bring it back.
    world.gone_ok = frozenset(k for k, e in world.secrets.items() if e == ACCOUNT_A)
    return spare, files, key


def _log_records(world: World) -> list[dict]:
    path = Path(world.store_dir) / "license-store.log"
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def test_heal_ActiveAlsoHeldAsSpare_SpareMovesWholeToABackupAndIsHeldOnce(world):
    # The account in the default folder is also a spare: the spare is stale, so its folder is moved, whole and
    # unchanged, out of the spares into a backup folder, and the account is listed exactly once, as active.
    spare, files, key = _hand_sign_in_over_a_spare(world)
    store = world.store()

    held = store.held_accounts()

    assert [(a.email, a.role) for a in held] == [(ACCOUNT_B, "active")]
    assert not os.path.exists(spare)
    [backup] = world.backups()
    assert Path(backup).parent == Path(world.store_dir) / "hand-sign-in-backups"
    assert Path(backup).name.startswith(license_store.spare_slug(ACCOUNT_B) + "-")
    assert _files(backup) == files, "the backup is not the spare's folder byte for byte"
    assert world.key_at(backup) == key
    [heal] = store.last_heals
    assert (heal.account, heal.moved_from, heal.moved_to) == (ACCOUNT_B, spare, backup)
    assert heal.duplicate_key_removed is False
    world.assert_each_signin_in_exactly_one_place()
    [logged] = [r for r in _log_records(world) if r["event"] == "hand-sign-in-healed"]
    assert (logged["account"], logged["moved_from"], logged["moved_to"]) == (ACCOUNT_B, spare, backup)
    log = (Path(world.store_dir) / "license-store.log").read_text(encoding="utf-8")
    assert not any(secret in log for secret in world.secrets)
    assert not (Path(world.store_dir) / "journal.json").exists()


def test_heal_NothingToHeal_TouchesNothing(world):
    _machine_with_active_and_spare(world)
    before = world.snapshot()
    store = world.store()
    store.held_accounts()
    assert store.last_heals == [] and world.backups() == [] and world.snapshot() == before


def test_heal_NeverDeletes_TwoHealsOfOneAccountKeepBothBackups(world):
    # The same account signed in by hand twice over a spare of it: two heals, two backups, both kept, with
    # different names even when both heals fall in the same second.
    spare, _, first_key = _hand_sign_in_over_a_spare(world)
    world.store().held_accounts()
    world.sign_in(spare, ACCOUNT_B)  # a spare of B again, as `license sign-in` or a switch could leave it
    second_key = world.key_at(spare)

    store = world.store()
    store.held_accounts()

    backups = world.backups()
    assert len(backups) == 2 and len(set(backups)) == 2
    assert sorted(world.key_at(b) for b in backups) == sorted([first_key, second_key])
    assert [a.email for a in store.held_accounts()] == [ACCOUNT_B]
    world.assert_each_signin_in_exactly_one_place()


def test_heal_LockTaken_DoesNothing_AndTheNextRoundHeals(world):
    # A switch, a renewal or a sign-in's write holds the store lock: the heal never runs inside it. The read that
    # would have healed is refused whole, as every store read is while the lock is held, and changes nothing; the
    # next round, with the lock free, heals.
    spare, files, _ = _hand_sign_in_over_a_spare(world)
    before = world.snapshot()
    holder = world.store()
    with holder.exclusive():
        with pytest.raises(LicenseStoreError) as refused:
            world.store().held_accounts()
    assert refused.value.reason_code == "switch-in-progress"
    assert world.snapshot() == before and world.backups() == [] and _files(spare) == files

    store = world.store()
    store.held_accounts()

    assert len(store.last_heals) == 1 and len(world.backups()) == 1
    world.assert_each_signin_in_exactly_one_place()


def test_heal_SpareHeldTheSameKey_TheDuplicateIsRemovedSoTheKeyIsInOnePlace(world):
    # A duplicate no operation of this store leaves behind, but if one is found the backup must not become a
    # second home for the live key: the backup keeps the folder, the duplicate key is dropped from it (the default
    # folder is proven to hold it), and the key is in exactly one place.
    _machine_with_active_and_spare(world)
    spare = str(Path(world.store_dir) / "spares" / license_store.spare_slug(ACCOUNT_B))
    world.backend().write(world.default, HeldKey(world.key_at(spare)))
    (Path(world.default) / ".claude.json").write_text((Path(spare) / ".claude.json").read_text(encoding="utf-8"),
                                                      encoding="utf-8")
    world.gone_ok = frozenset(k for k, e in world.secrets.items() if e == ACCOUNT_A)
    store = world.store()

    store.held_accounts()

    [backup] = world.backups()
    assert store.last_heals[0].duplicate_key_removed is True
    assert world.key_at(backup) is None and world.key_at(world.default) is not None
    world.assert_each_signin_in_exactly_one_place()


@pytest.mark.parametrize("kind", ["file", "keychain"])
def test_heal_CrashAtEveryChangePoint_TheNextRunFinishesTheHeal(tmp_path, kind):
    # A crash at each change point of the heal - the journal, the rename, the Keychain move - is finished by the
    # next run: the spare ends in exactly one backup, never lost, never in two places, and the heal is reported.
    def make(tag):
        w = World(tmp_path / tag, kind)
        w.removed = set()
        _hand_sign_in_over_a_spare(w)
        return w

    probe = make("probe")
    labels = []
    probe.store(checkpoint=labels.append).held_accounts()
    assert "rename-folder" in labels, labels

    for n in range(1, len(labels) + 1):
        w = make(f"crash{n}")
        assert _run_with_crash_at(w, n, lambda store: store.held_accounts()), f"no crash at {labels[n - 1]}"
        store = w.store()
        held = [(a.email, a.role) for a in store.held_accounts()]

        assert held == [(ACCOUNT_B, "active")], f"crash at {labels[n - 1]}: {held}"
        assert len(w.backups()) == 1, f"crash at {labels[n - 1]}: {w.backups()}"
        assert [h.account for h in store.last_heals] == [ACCOUNT_B], f"crash at {labels[n - 1]}"
        w.assert_each_signin_in_exactly_one_place()
        assert not (Path(w.store_dir) / "journal.json").exists()


def test_exchange_ActiveKeyWithNoAccountBlock_IsRefused(world):
    _machine_with_active_and_spare(world)
    (Path(world.default) / ".claude.json").write_text("{}", encoding="utf-8")
    with pytest.raises(LicenseStoreError, match="account is unknown"):
        world.store().switch(ACCOUNT_B)


# ===== add and remove =====


def test_add_MovesTheSigninOutOfTheSourceFolder(world):
    source = world.new_signin_folder(ACCOUNT_B)
    key = world.key_at(source)

    account = world.store().add(source)

    assert (account.email, account.role) == (ACCOUNT_B, "spare")
    assert world.key_at(source) is None, "the source folder still holds the key: it was copied, not moved"
    assert world.email_at(source) is None
    assert world.key_at(account.config_dir) == key
    world.assert_each_signin_in_exactly_one_place()


def test_add_SourceNotSignedIn_IsRefused(world, tmp_path):
    empty = tmp_path / "not-signed-in"
    empty.mkdir()
    with pytest.raises(LicenseStoreError, match="holds no sign-in key"):
        world.store().add(str(empty))


def test_add_AccountAlreadyHeld_IsRefusedAndTheSourceKeepsItsKey(world):
    _machine_with_active_and_spare(world)
    second = world.new_signin_folder(ACCOUNT_A)
    with pytest.raises(LicenseStoreError, match="already held"):
        world.store().add(second)
    assert world.key_at(second) is not None


def test_add_FromTheDefaultFolder_IsRefused(world):
    world.sign_in(world.default, ACCOUNT_A)
    with pytest.raises(LicenseStoreError, match="default config folder"):
        world.store().add(world.default)


def test_remove_DeletesTheSpareOnly(world):
    _machine_with_active_and_spare(world)

    world.store().remove(ACCOUNT_B)
    world.removed.add(ACCOUNT_B)

    assert _held(world) == {ACCOUNT_A: "active"}
    world.assert_each_signin_in_exactly_one_place()


def test_remove_TheActiveAccount_IsRefused(world):
    _machine_with_active_and_spare(world)
    with pytest.raises(LicenseStoreError, match="active account"):
        world.store().remove(ACCOUNT_A)
    assert _held(world) == {ACCOUNT_A: "active", ACCOUNT_B: "spare"}


# ===== a crash at every change point, and the recovery =====


def _run_with_crash_at(world: World, n: int, operation) -> bool:
    """Run `operation(store)` with a crash at the n-th change point. True when it crashed."""
    seen = {"count": 0}

    def checkpoint(_label):
        seen["count"] += 1
        if seen["count"] == n:
            raise SimulatedCrash(_label)

    try:
        operation(world.store(checkpoint=checkpoint))
    except SimulatedCrash:
        return True
    return False


def _shape(world: World) -> dict:
    """{folder relative to the world: (whose key it holds, whose account block it has)}, so two worlds compare."""
    return {str(Path(f).relative_to(world.root)): (world.secrets[key], email)
            for f, (key, email) in world.snapshot().items()}


def _switch(store):
    return store.switch(ACCOUNT_B)


def _remove(store):
    return store.remove(ACCOUNT_B)


@pytest.mark.parametrize("kind", ["file", "keychain"])
@pytest.mark.parametrize("operation_name", ["switch", "add", "remove"])
def test_crash_AtEveryChangePoint_RecoveryLeavesEachSigninInExactlyOnePlace(tmp_path, kind, operation_name):
    def make(tag):
        w = World(tmp_path / tag, kind)
        w.removed = set()
        _machine_with_active_and_spare(w)
        source = w.new_signin_folder(ACCOUNT_C) if operation_name == "add" else None
        operation = {"switch": _switch, "remove": _remove, "add": lambda store: store.add(source)}[operation_name]
        return w, operation

    probe, probe_operation = make("probe")
    labels = []
    probe_operation(probe.store(checkpoint=labels.append))
    assert len(labels) >= 2, "the operation reported no change points, so the crash test would prove nothing"
    done_shape = _shape(probe)

    for n in range(1, len(labels) + 1):
        w, operation = make(f"crash{n}")
        before_shape = _shape(w)
        assert _run_with_crash_at(w, n, operation), f"no crash at change point {n} ({labels[n - 1]})"

        held = {a.email: a.role for a in w.store().held_accounts()}  # the next run recovers first

        if operation_name == "remove" and ACCOUNT_B not in held:
            w.removed.add(ACCOUNT_B)
        w.assert_each_signin_in_exactly_one_place()
        assert _shape(w) in (before_shape, done_shape), (
            f"crash at change point {n} ({labels[n - 1]}) left a state that is neither before nor after: {held}")
        assert not (Path(w.store_dir) / "journal.json").exists()


@pytest.mark.parametrize("kind", ["file", "keychain"])
def test_crash_DuringTheRecoveryToo_TheNextRecoveryStillLeavesEachSigninInOnePlace(tmp_path, kind):
    def make(tag):
        w = World(tmp_path / tag, kind)
        w.removed = set()
        _machine_with_active_and_spare(w)
        return w

    probe = make("probe")
    labels = []
    probe.store(checkpoint=labels.append).switch(ACCOUNT_B)

    # One crash inside each step of the switch: putting the active key away, bringing the spare in, and
    # deleting the spare's copy. Every change point of the recovery is then crashed in turn.
    copies = [i + 1 for i, label in enumerate(labels) if label == "copy-account"]
    firsts = [copies[0], copies[1], labels.index("drop-account") + 1]
    for first in firsts:
        for second in range(1, len(labels) + 1):
            w = make(f"r{first}-{second}")
            assert _run_with_crash_at(w, first, _switch)
            _run_with_crash_at(w, second, lambda store: store.recover())
            held = {a.email: a.role for a in w.store().held_accounts()}
            w.assert_each_signin_in_exactly_one_place()
            assert held == {ACCOUNT_B: "active", ACCOUNT_A: "spare"}, (
                f"crash at {labels[first - 1]} then at recovery point {second}: {held}")


def test_recovery_IsLogged(world):
    _machine_with_active_and_spare(world)
    assert _run_with_crash_at(world, 4, lambda store: store.switch(ACCOUNT_B))

    store = world.store()
    store.held_accounts()

    assert "finished" in store.last_recovery
    events = [json.loads(line)["event"] for line in
              (Path(world.store_dir) / "license-store.log").read_text(encoding="utf-8").splitlines()]
    assert "recovery-started" in events and "recovery-finished" in events


def _vanish(world: World, folder: str) -> None:
    """Someone deletes a sign-in from outside the store: its key and its account block both go."""
    world.backend().delete(folder)
    license_store._clear_oauth_account(folder)


def _after_first_switch_step(tmp_path: Path, kind: str) -> int:
    """The change point just after the first step of a switch (the active key put away) is recorded."""
    probe = World(tmp_path / "probe", kind)
    probe.removed = set()
    _machine_with_active_and_spare(probe)
    labels = []
    probe.store(checkpoint=labels.append).switch(ACCOUNT_B)
    return labels.index("journal", 1) + 2


def test_recovery_SpareVanishedMidSwitch_RollsBackAndKeepsTheActiveSignin(world, tmp_path):
    crash_at = _after_first_switch_step(tmp_path, world.kind)
    _machine_with_active_and_spare(world)
    key_a = world.key_at(world.default)
    assert _run_with_crash_at(world, crash_at, _switch)
    assert world.key_at(world.store().spare_folder(ACCOUNT_A)) == key_a, "the crash came before step 1 finished"
    _vanish(world, world.store().spare_folder(ACCOUNT_B))  # the spare disappears before recovery
    world.removed.add(ACCOUNT_B)

    with pytest.raises(LicenseStoreError, match="rolled back") as refused:
        world.store().recover()
    assert refused.value.reason_code == "exchange-failed-restored"

    assert world.key_at(world.default) == key_a
    assert world.key_at(world.store().spare_folder(ACCOUNT_A)) is None, "the put-away copy was left behind"
    world.assert_each_signin_in_exactly_one_place()
    assert not (Path(world.store_dir) / "journal.json").exists()


def test_recovery_WhenRollingBackCannotBeProven_NothingIsDeleted(world, tmp_path):
    crash_at = _after_first_switch_step(tmp_path, world.kind)
    _machine_with_active_and_spare(world)
    assert _run_with_crash_at(world, crash_at, _switch)
    _vanish(world, world.store().spare_folder(ACCOUNT_B))  # finishing is impossible
    world.sign_in(world.default, ACCOUNT_C)  # and the default no longer holds the key that was put away
    before = world.snapshot()

    with pytest.raises(LicenseStoreError, match="neither finish nor roll back") as refused:
        world.store().recover()
    assert refused.value.reason_code == "exchange-failed-unknown"

    assert world.snapshot() == before
    assert (Path(world.store_dir) / "journal.json").exists()


# ===== the macOS Keychain names =====


def test_keychain_ServiceNames_AreClaudeCodesExactNames(tmp_path):
    folder = "/Users/example/seat-test/work"
    assert keychain_service_for(folder) == "Claude Code-credentials-f7aced35"
    other = str(tmp_path / "x")
    assert keychain_service_for(other) == "Claude Code-credentials-" + hashlib.sha256(other.encode()).hexdigest()[:8]
    assert keychain_service_for(str(Path.home() / ".claude")) == "Claude Code-credentials"


def test_keychain_ServiceName_NormalFormCLikeClaudeCode():
    # Claude Code 2.1.296 hashes (CLAUDE_CONFIG_DIR ?? ~/.claude).normalize("NFC"), so a path typed with a
    # decomposed accent names the same item as the composed one.
    decomposed, composed = "/Users/example/café", "/Users/example/café"
    expected = "Claude Code-credentials-" + hashlib.sha256(composed.encode("utf-8")).hexdigest()[:8]
    assert keychain_service_for(decomposed) == expected == keychain_service_for(composed)


def test_store_CanonicalFolderIsHashedExactlyAsGiven(tmp_path):
    world = World(tmp_path, "keychain")
    store = LicenseStore(world.default, world.store_dir, world.backend())
    world.sign_in(world.default, ACCOUNT_A)
    (account,) = store.held_accounts()
    assert account.config_dir == world.default
    expected = "Claude Code-credentials-" + hashlib.sha256(world.default.encode("utf-8")).hexdigest()[:8]
    assert account.key_location == expected


def test_store_DecomposedUnicodeFolder_IsAcceptedAndHashedInNormalFormC(tmp_path):
    # Measured on the Mac: Claude Code hashes the raw CLAUDE_CONFIG_DIR string after normal form C.
    world = World(tmp_path, "keychain")
    decomposed = os.path.join(str(tmp_path), "café")
    store = LicenseStore(decomposed, world.store_dir, world.backend())
    world.sign_in(decomposed, ACCOUNT_A)
    (account,) = store.held_accounts()
    composed = unicodedata.normalize("NFC", decomposed)
    assert account.key_location == "Claude Code-credentials-" + hashlib.sha256(composed.encode("utf-8")).hexdigest()[:8]


#: Every spelling of a folder other than the canonical one names a different Keychain item, so each is refused.
_NON_CANONICAL = {
    "trailing separator": lambda base: base + os.sep,
    "dot segment": lambda base: os.path.dirname(base) + os.sep + "." + os.sep + os.path.basename(base),
    "dot-dot segment": lambda base: base + os.sep + "x" + os.sep + "..",
    "doubled separator": lambda base: os.path.dirname(base) + os.sep + os.sep + os.path.basename(base),
}


@pytest.mark.parametrize("which", ["default", "store", "add"])
@pytest.mark.parametrize("form", sorted(_NON_CANONICAL))
def test_store_NonCanonicalFolder_IsRefusedNamingTheCanonicalForm(tmp_path, which, form):
    default, store_dir, source = str(tmp_path / "d"), str(tmp_path / "s"), str(tmp_path / "login")
    bad = _NON_CANONICAL[form](str(tmp_path / ("d" if which == "default" else "s" if which == "store" else "login")))
    if which == "add":
        with pytest.raises(LicenseStoreError, match="canonical form") as refused:
            LicenseStore(default, store_dir, FileKeyBackend()).add(bad)
    else:
        with pytest.raises(LicenseStoreError, match="canonical form") as refused:
            LicenseStore(bad if which == "default" else default, bad if which == "store" else store_dir,
                         FileKeyBackend())
    # The way out is a NEW sign-in under the canonical spelling - never "retype it", which would point at a
    # different Keychain item from the one the existing sign-in is in.
    assert "not supported for license switching" in str(refused.value)
    assert refused.value.next_steps[0].startswith("Sign the account in again with CLAUDE_CONFIG_DIR=")
    assert os.path.normpath(bad) in refused.value.next_steps[0]


def test_store_RootFolder_IsRefused(tmp_path):
    root = os.path.abspath(os.sep)
    with pytest.raises(LicenseStoreError, match="root folder"):
        LicenseStore(root, str(tmp_path / "s"), FileKeyBackend())


@pytest.mark.parametrize("which", ["default", "store", "add"])
def test_store_RelativeFolder_IsRefused(tmp_path, which):
    # Claude Code would hash a relative CLAUDE_CONFIG_DIR as typed; resolving it here would name another item.
    default, store_dir = str(tmp_path / "d"), str(tmp_path / "s")
    if which == "default":
        default = "isolated-claude"
    if which == "store":
        store_dir = "store"
    if which == "add":
        with pytest.raises(LicenseStoreError, match="relative path") as refused:
            LicenseStore(default, store_dir, FileKeyBackend()).add("isolated-claude")
    else:
        with pytest.raises(LicenseStoreError, match="relative path") as refused:
            LicenseStore(default, store_dir, FileKeyBackend())
    # Not supported, and the way out is signing in again under an absolute folder - never "use the absolute
    # spelling", which names a different Keychain item from the existing sign-in's.
    assert "A relative CLAUDE_CONFIG_DIR is not supported for license switching" in str(refused.value)
    assert refused.value.next_steps[0].startswith("Sign the account in again with CLAUDE_CONFIG_DIR set to an absolute")


def test_keychain_Exchange_ItemsLandUnderTheExactServiceNames(tmp_path):
    world = World(tmp_path, "keychain")
    world.removed = set()
    _machine_with_active_and_spare(world)
    key_a = world.key_at(world.default)

    world.store().switch(ACCOUNT_B)

    def service(folder):
        return "Claude Code-credentials-" + hashlib.sha256(folder.encode("utf-8")).hexdigest()[:8]

    spare_a = world.store().spare_folder(ACCOUNT_A)
    spare_b = world.store().spare_folder(ACCOUNT_B)
    assert world.security.items[service(spare_a)][0][1] == key_a
    assert world.secrets[world.security.items[service(world.default)][0][1]] == ACCOUNT_B
    assert service(spare_b) not in world.security.items
    assert "Claude Code-credentials" not in world.security.items
    assert all(len(v) == 1 for v in world.security.items.values()), "a service holds two items"


def test_keychain_MovedItemKeepsItsKeychainAccount(tmp_path):
    # Older Claude Code builds wrote the item under the macOS user name; it must come back under the same one.
    world = World(tmp_path, "keychain")
    world.removed = set()
    world.sign_in(world.default, ACCOUNT_A, keychain_account="legacy-user")
    world.store().add(world.new_signin_folder(ACCOUNT_B))

    world.store().switch(ACCOUNT_B)

    spare_a = world.store().spare_folder(ACCOUNT_A)
    service_a = keychain_service_for(spare_a)
    assert world.security.items[service_a][0][0] == "legacy-user"


def test_keychain_TheRealDefaultItem_IsRefused():
    security = FakeSecurity()
    backend = KeychainKeyBackend(runner=security)
    with pytest.raises(LicenseStoreError, match="real Claude login"):
        backend.read(str(Path.home() / ".claude"))
    assert security.calls == []


def test_keychain_NoKeyIsEverOnACommandLine(tmp_path):
    world = World(tmp_path, "keychain")
    world.removed = set()
    _machine_with_active_and_spare(world)
    world.store().switch(ACCOUNT_B)
    world.store().switch(ACCOUNT_A)

    _assert_no_key_on_a_command_line(world.security.calls, list(world.secrets))


#: The exact argument shapes the Keychain backend may use: a service name and fixed flags, nothing else.
_SAFE_SECURITY_SHAPES = [
    lambda a: len(a) == 3 and a[:2] == ["find-generic-password", "-s"],
    lambda a: len(a) == 4 and a[:2] == ["find-generic-password", "-s"] and a[3] == "-w",
    lambda a: len(a) == 3 and a[:2] == ["delete-generic-password", "-s"],
    lambda a: a == ["-i"],
]


def _assert_no_key_on_a_command_line(calls, secrets) -> None:
    """Every `security` call has a safe shape, and no key appears in its arguments in any encoding - raw, or
    hex as `-X` takes it. A key moved onto the command line fails this whatever it looks like."""
    for args, _stdin in calls:
        assert any(shape(list(args)) for shape in _SAFE_SECURITY_SHAPES), f"unexpected security arguments: {args}"
        joined = " ".join(args)
        for secret in secrets:
            for form in (secret, secret.encode("utf-8").hex(), secret.encode("utf-8").hex().upper()):
                assert form not in joined, "a key is on the command line"


def test_keychain_TheCommandLineCheckCanFail():
    # The arguments a regression to `security add-generic-password ... -X <hex>` would produce must fail.
    secret = '{"claudeAiOauth": {"accessToken": "x"}}'
    leaked = ["add-generic-password", "-U", "-a", "claude-code-user", "-s", "Claude Code-credentials-12345678",
              "-X", secret.encode("utf-8").hex()]
    with pytest.raises(AssertionError):
        _assert_no_key_on_a_command_line([(leaked, None)], [secret])
    # And a safe shape carrying the hex as its service argument fails on the encoding check alone.
    with pytest.raises(AssertionError, match="on the command line"):
        _assert_no_key_on_a_command_line([(["find-generic-password", "-s", secret.encode("utf-8").hex()], None)],
                                         [secret])


# ===== the real default folder =====


def test_realDefault_IsRefused_BeforeAnythingIsRead(tmp_path):
    with pytest.raises(LicenseStoreError, match="real default"):
        LicenseStore(Path.home() / ".claude", tmp_path / "store", FileKeyBackend())
    assert not (tmp_path / "store").exists()


def test_realDefault_IsRefused_InEverySpelling(tmp_path):
    # Spelled canonically it is the real default; spelled any other way it is not a canonical folder. Either
    # way it is refused before anything is read or created.
    home = str(Path.home() / ".claude")
    for spelling in (home, home + os.sep, os.path.dirname(home) + os.sep + "." + os.sep + ".claude"):
        with pytest.raises(LicenseStoreError, match="real default|canonical form"):
            LicenseStore(spelling, str(tmp_path / "store"), FileKeyBackend())
    assert not (tmp_path / "store").exists()


def test_storeInsideDefault_IsRefused(tmp_path):
    with pytest.raises(LicenseStoreError, match="contain one another"):
        LicenseStore(tmp_path / "d", tmp_path / "d" / "store", FileKeyBackend())


# ===== a key never shows =====


def test_secrets_NeverInLogsJournalReprOrErrors(world):
    _machine_with_active_and_spare(world)
    world.store().add(world.new_signin_folder(ACCOUNT_C))
    assert _run_with_crash_at(world, 4, lambda store: store.switch(ACCOUNT_B))
    journal = (Path(world.store_dir) / "journal.json").read_text(encoding="utf-8")
    store = world.store()
    store.held_accounts()
    store.switch(ACCOUNT_C)
    with pytest.raises(LicenseStoreError) as refused:
        store.switch("nobody@example.com")
    accounts = store.held_accounts()

    exposed = "\n".join([
        journal,
        (Path(world.store_dir) / "license-store.log").read_text(encoding="utf-8"),
        str(refused.value),
        repr(accounts),
        json.dumps([a.to_json() for a in accounts]),
        repr(accounts[0].read_key()),
        str(accounts[0].read_key()),
    ])
    assert "SECRET" not in exposed


def test_heldKey_AccessToken_IsReadableForTheUsageReader(world):
    world.sign_in(world.default, ACCOUNT_A)
    (account,) = world.store().held_accounts()
    assert account.read_key().access_token().startswith("sk-ant-oat01-SECRET-ACCESS-")


def test_heldKey_MalformedKey_ErrorCarriesNoValue():
    with pytest.raises(LicenseStoreError) as refused:
        HeldKey('{"not": "SECRET-shape"}').access_token()
    assert "SECRET" not in str(refused.value)


def test_storeRefreshedKey_ReplacesInPlace_NoSecondCopy(world):
    _machine_with_active_and_spare(world)
    spare = world.store().spare_folder(ACCOUNT_B)
    new_secret = json.dumps({"claudeAiOauth": {"accessToken": "sk-ant-oat01-SECRET-NEW", "refreshToken": "r"}})
    world.secrets[new_secret] = ACCOUNT_B
    old = world.key_at(spare)
    del world.secrets[old]

    world.store().store_refreshed_key(ACCOUNT_B, new_secret)

    assert world.key_at(spare) == new_secret
    world.assert_each_signin_in_exactly_one_place()


def test_lock_ASecondOperationAtTheSameTime_IsRefused(world):
    world.sign_in(world.default, ACCOUNT_A)
    first = world.store()
    second = world.store()
    with first._lock:
        with pytest.raises(LicenseStoreError, match="another license operation"):
            second.held_accounts()
    assert len(second.held_accounts()) == 1


# ===== a new sign-in, written straight into the folder it belongs in (never moved) =====


SIGNED_IN_AT = datetime.datetime(2026, 10, 11, 14, 0, tzinfo=datetime.timezone.utc)
ENDS_MS = 1_793_000_000_000


def _login_writes(world: World, folder: str, email: str, ends_ms: int = ENDS_MS):
    """What typing the code into `claude auth login` does: ONE write of a new key and account block into `folder`.
    Returns (write, holder) - `holder["key"]` is the new key once written."""
    holder = {}

    def write():
        world.sign_in(folder, email)
        document = json.loads(world.key_at(folder))
        del world.secrets[world.key_at(folder)]
        document["claudeAiOauth"]["refreshTokenExpiresAt"] = ends_ms
        key = json.dumps(document)
        world.secrets[key] = email
        world.backend().write(folder, HeldKey(key, keychain_account="claude-code-user"))
        holder["key"] = key

    return write, holder


def _now():
    return SIGNED_IN_AT


def _places(world: World, key: str) -> list[str]:
    return [f for f, (k, _) in world.snapshot().items() if k == key]


def test_signIn_NewAccount_LandsInItsSpareFolder_OneWrite(world):
    world.sign_in(world.default, ACCOUNT_A)
    store = world.store()
    target = store.sign_in_target(ACCOUNT_B)
    assert target.folder == store.spare_folder(ACCOUNT_B) and not target.renewing
    assert _held(world) == {ACCOUNT_A: "active"}, "an empty spare folder is not a held account"
    write, holder = _login_writes(world, target.folder, ACCOUNT_B)

    result = world.store().finish_sign_in(target, write, _now)

    assert not result.renewed and result.account.role == "spare"
    assert _held(world) == {ACCOUNT_A: "active", ACCOUNT_B: "spare"}
    assert _places(world, holder["key"]) == [target.folder]
    world.assert_each_signin_in_exactly_one_place()


def test_signIn_HeldSpare_IsReplacedInItsOwnFolder(world):
    _machine_with_active_and_spare(world)
    spare = world.store().spare_folder(ACCOUNT_B)
    world.gone_ok = frozenset({world.key_at(spare)})
    target = world.store().sign_in_target(ACCOUNT_B)
    assert target.renewing and target.folder == spare and target.role == "spare"
    write, holder = _login_writes(world, spare, ACCOUNT_B)

    result = world.store().finish_sign_in(target, write, _now)

    assert result.renewed and _places(world, holder["key"]) == [spare]
    world.assert_each_signin_in_exactly_one_place()


def test_signIn_ActiveAccount_IsReplacedInTheDefaultFolder(world):
    world.sign_in(world.default, ACCOUNT_A)
    world.gone_ok = frozenset({world.key_at(world.default)})
    target = world.store().sign_in_target(ACCOUNT_A)
    assert target.renewing and target.role == "active"
    write, holder = _login_writes(world, world.default, ACCOUNT_A)

    result = world.store().finish_sign_in(target, write, _now)

    assert result.renewed and result.account.role == "active"
    assert _places(world, holder["key"]) == [world.default]
    world.assert_each_signin_in_exactly_one_place()


@pytest.mark.parametrize("case", ["new", "renew-spare", "renew-active"])
@pytest.mark.parametrize("crash", ["before-write", "after-write"])
def test_signIn_CrashAtEitherSide_EachSignInIsInOnePlace_AtTheInstantOfTheCrash(world, case, crash):
    # The only change a sign-in makes is the login's one write. A crash on either side of it leaves every sign-in
    # in exactly one place AT ONCE - checked before any recovery runs - and the active folder never empty.
    _machine_with_active_and_spare(world)
    email = {"new": ACCOUNT_C, "renew-spare": ACCOUNT_B, "renew-active": ACCOUNT_A}[case]
    target = world.store().sign_in_target(email)
    if target.renewing:
        world.gone_ok = frozenset({world.key_at(target.folder)})
    write, holder = _login_writes(world, target.folder, email)

    def crash_after():
        write()
        raise SimulatedCrash("after the write")

    def checkpoint(label):
        if crash == "before-write" and label == "sign-in-write":
            raise SimulatedCrash(label)

    with pytest.raises(SimulatedCrash):
        world.store(checkpoint=checkpoint).finish_sign_in(target, crash_after if crash == "after-write" else write,
                                                          _now)

    world.assert_each_signin_in_exactly_one_place()
    assert world.key_at(world.default) is not None
    if crash == "after-write":
        assert _places(world, holder["key"]) == [target.folder]
    else:
        assert "key" not in holder
    assert not (Path(world.store_dir) / "journal.json").exists(), "a sign-in leaves nothing to recover"


def test_signIn_SwitchBetweenPlanAndWrite_NothingIsWritten(world):
    # Account B is a spare when the login starts; a switch then puts account A's key in B's spare folder. Writing
    # B's new sign-in there would destroy A's.
    _machine_with_active_and_spare(world)
    target = world.store().sign_in_target(ACCOUNT_B)
    world.store().switch(ACCOUNT_B)
    before = world.snapshot()
    called = []

    with pytest.raises(LicenseStoreError, match="moved while it was being signed in"):
        world.store().finish_sign_in(target, lambda: called.append(1), _now)

    assert called == [] and world.snapshot() == before


def test_signIn_NewAccountHeldMeanwhile_NothingIsWritten(world):
    world.sign_in(world.default, ACCOUNT_A)
    target = world.store().sign_in_target(ACCOUNT_B)
    world.store().add(world.new_signin_folder(ACCOUNT_B))
    called = []

    with pytest.raises(LicenseStoreError, match="nothing was written"):
        world.store().finish_sign_in(target, lambda: called.append(1), _now)

    assert called == []


def test_signIn_LoginWroteAnotherAccount_IsReportedNotAccepted(world):
    world.sign_in(world.default, ACCOUNT_A)
    target = world.store().sign_in_target(ACCOUNT_B)
    write, _ = _login_writes(world, target.folder, ACCOUNT_C)

    with pytest.raises(LicenseStoreError, match="is for account-c@example.com, not account-b@example.com"):
        world.store().finish_sign_in(target, write, _now)

    assert "sign-in-wrong-account" in (Path(world.store_dir) / "license-store.log").read_text(encoding="utf-8")


def test_abandonSignIn_RemovesOnlyAnEmptyNewFolder_NeverARenewalsFolder(world):
    _machine_with_active_and_spare(world)
    new = world.store().sign_in_target(ACCOUNT_C)
    world.store().abandon_sign_in(new)
    assert not Path(new.folder).exists()

    renewal = world.store().sign_in_target(ACCOUNT_B)
    world.store().abandon_sign_in(renewal)
    assert world.key_at(renewal.folder) is not None

    written = world.store().sign_in_target(ACCOUNT_C)
    world.sign_in(written.folder, ACCOUNT_C)
    world.store().abandon_sign_in(written)
    assert world.key_at(written.folder) is not None, "a folder holding the account's own key is never deleted"


def test_abandonSignIn_NewFolderHoldingAnotherAccountsKey_IsDeleted(world):
    world.sign_in(world.default, ACCOUNT_A)
    target = world.store().sign_in_target(ACCOUNT_B)
    world.sign_in(target.folder, ACCOUNT_C)  # the login signed in the wrong account
    world.removed = {ACCOUNT_C}

    world.store().abandon_sign_in(target)

    assert not Path(target.folder).exists()
    assert _held(world) == {ACCOUNT_A: "active"}
    world.assert_each_signin_in_exactly_one_place()


def test_signedInAt_RecordedWithTheEndDate_AndOnlyMatchesThatSignIn(world):
    world.sign_in(world.default, ACCOUNT_A)
    target = world.store().sign_in_target(ACCOUNT_B)
    write, _ = _login_writes(world, target.folder, ACCOUNT_B)
    world.store().finish_sign_in(target, write, _now)
    ends = datetime.datetime.fromtimestamp(ENDS_MS / 1000, datetime.timezone.utc)
    store = world.store()

    assert store.signed_in_at(ACCOUNT_B, ends) == SIGNED_IN_AT
    # A refresh recomputes the end date from "now + seconds left": seconds apart is the same sign-in.
    assert store.signed_in_at(ACCOUNT_B, ends + datetime.timedelta(seconds=40)) == SIGNED_IN_AT
    # A sign-in made by hand since has another end date: its time is not known, never the old one.
    assert store.signed_in_at(ACCOUNT_B, ends + datetime.timedelta(days=3)) is None
    assert store.signed_in_at(ACCOUNT_B, None) is None
    assert store.signed_in_at(ACCOUNT_C, ends) is None
    assert "SECRET" not in (Path(world.store_dir) / "sign-ins.json").read_text(encoding="utf-8")
