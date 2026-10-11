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

import hashlib
import json
import re
import sys
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
        return found

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
            if email in self.removed:
                continue
            assert key in places, f"{email}'s sign-in was lost"

    removed: set[str] = set()


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


def test_contract_RollBackAfterTheSpareCameIn_PutsTheOldActiveSigninBack(world, tmp_path):
    # The last step (deleting the spare's copy) cannot be proven safe, so the switch is rolled back. Undoing
    # "bring the spare in" must restore the old active sign-in to the default folder, not leave it empty.
    probe = World(tmp_path / "probe", world.kind)
    probe.removed = set()
    _machine_with_active_and_spare(probe)
    labels = []
    probe.store(checkpoint=labels.append).switch(ACCOUNT_B)
    after_second_step = [i for i, label in enumerate(labels) if label == "journal"][2] + 2

    _machine_with_active_and_spare(world)
    key_a = world.key_at(world.default)
    assert _run_with_crash_at(world, after_second_step, _switch)
    spare_b = world.store().spare_folder(ACCOUNT_B)
    assert world.key_at(world.default) == world.key_at(spare_b), "the crash came before step 2 finished"
    # The spare's copy changes before recovery (a refresh wrote a new key), so dropping it is not provable.
    del world.secrets[world.key_at(spare_b)]
    world.sign_in(spare_b, ACCOUNT_B)

    with pytest.raises(LicenseStoreError, match="rolled back") as refused:
        world.store().recover()

    assert refused.value.reason_code == "exchange-failed-restored"
    assert world.key_at(world.default) == key_a and world.email_at(world.default) == ACCOUNT_A
    assert _held(world) == {ACCOUNT_A: "active", ACCOUNT_B: "spare"}
    world.assert_each_signin_in_exactly_one_place()


@pytest.mark.parametrize("kind", ["file", "keychain"])
def test_contract_CrashDuringTheRollBack_TheNextRunResumesIt(tmp_path, kind):
    probe = World(tmp_path / "probe", kind)
    probe.removed = set()
    _machine_with_active_and_spare(probe)
    labels = []
    probe.store(checkpoint=labels.append).switch(ACCOUNT_B)
    after_second_step = [i for i, label in enumerate(labels) if label == "journal"][2] + 2

    def make(tag):
        w = World(tmp_path / tag, kind)
        w.removed = set()
        _machine_with_active_and_spare(w)
        assert _run_with_crash_at(w, after_second_step, _switch)
        spare_b = w.store().spare_folder(ACCOUNT_B)
        del w.secrets[w.key_at(spare_b)]
        w.sign_in(spare_b, ACCOUNT_B)
        return w

    rollback_labels = []
    first = make("count")
    with pytest.raises(LicenseStoreError):
        first.store(checkpoint=rollback_labels.append).recover()
    assert len(rollback_labels) >= 4

    for n in range(1, len(rollback_labels) + 1):
        w = make(f"rb{n}")
        key_a = w.key_at(w.store().spare_folder(ACCOUNT_A))
        assert _run_with_crash_at(w, n, lambda store: store.recover()), f"no crash at {rollback_labels[n - 1]}"
        try:
            w.store().recover()
        except LicenseStoreError as exc:
            assert exc.reason_code == "exchange-failed-restored", f"crash at {rollback_labels[n - 1]}: {exc}"
        assert _held(w) == {ACCOUNT_A: "active", ACCOUNT_B: "spare"}, f"crash at {rollback_labels[n - 1]}"
        assert w.key_at(w.default) == key_a
        w.assert_each_signin_in_exactly_one_place()


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


def test_exchange_ActiveAccountAlsoHeldAsSpare_IsRefusedRatherThanOverwritingASignin(world):
    # Someone ran `claude auth login` straight into the default folder for an account the store holds a
    # spare of. Putting the active key away would overwrite that spare - a different, independent sign-in.
    _machine_with_active_and_spare(world)
    world.sign_in(world.default, ACCOUNT_B)
    world.store().add(world.new_signin_folder(ACCOUNT_C))
    before = world.snapshot()

    with pytest.raises(LicenseStoreError, match="would overwrite"):
        world.store().switch(ACCOUNT_C)
    assert world.snapshot() == before


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
    world.backend().delete(world.store().spare_folder(ACCOUNT_B))  # the spare disappears before recovery
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
    world.backend().delete(world.store().spare_folder(ACCOUNT_B))  # finishing is impossible
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

    for args, _stdin in world.security.calls:
        joined = " ".join(args)
        for secret in world.secrets:
            assert "SECRET" not in joined and secret not in joined


# ===== the real default folder =====


def test_realDefault_IsRefused_BeforeAnythingIsRead(tmp_path):
    with pytest.raises(LicenseStoreError, match="real default"):
        LicenseStore(Path.home() / ".claude", tmp_path / "store", FileKeyBackend())
    assert not (tmp_path / "store").exists()


def test_realDefault_IsRefused_EvenWithATrailingSeparator(tmp_path):
    with pytest.raises(LicenseStoreError, match="real default"):
        LicenseStore(str(Path.home() / ".claude") + "/", tmp_path / "store", FileKeyBackend())


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
