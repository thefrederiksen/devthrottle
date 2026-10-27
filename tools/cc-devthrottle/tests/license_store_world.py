"""The isolated machine the key store tests run on, shared by the test files that need one.

`World` is a default config folder, a store and a place to sign accounts in, all under a test's tmp_path; `FakeSecurity`
answers `security` the way macOS does, so the Keychain backend runs on every platform. Kept out of the test files so a
test file that needs them still collects on its own (importing one test file from another resolved `tests` to a stray
installed package on a machine that had one).
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src.license_store import (  # noqa: E402
    FileKeyBackend,
    HeldKey,
    KeychainKeyBackend,
    LicenseStore,
    RunResult,
)


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
