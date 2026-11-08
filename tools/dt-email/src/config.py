"""Accounts and settings for dt-email, kept in one local JSON file with NO secret in it.

The file is `<cc-director config>/dt-email/config.json`:

    {
      "default": "soren",
      "allowed_recipients": ["soren@devthrottle.com", "email@devthrottle.com"],
      "accounts": {
        "soren": {
          "email": "soren@devthrottle.com", "user": "soren@devthrottle.com",
          "imap_host": "imap.zohocloud.ca", "imap_port": 993, "imap_security": "ssl",
          "smtp_host": "smtp.zohocloud.ca", "smtp_port": 465, "smtp_security": "ssl",
          "secret_entry": "zoho-mail-admin", "password_env": "ZOHO_ADMIN_PASSWORD",
          "save_sent": false
        }
      }
    }

The password itself is never stored here. It comes from the environment variable named in
`password_env`, which `cc-secrets run <secret_entry> -- dt-email ...` supplies.

`allowed_recipients`, when present, is the test-mode list: every To, CC, BCC of anything dt-email sends
must be on it, or the send is refused before anything leaves the machine. Absent means no restriction.
"""

from __future__ import annotations

import json
import os
import shlex
import sys
from dataclasses import asdict, dataclass, field
from email.utils import getaddresses
from pathlib import Path
from typing import Dict, List, Optional

from .errors import DtEmailError

SECURITY_CHOICES = ("ssl", "starttls")


def config_dir() -> Path:
    """The folder holding config.json. Tests replace this function."""
    from cc_storage import CcStorage

    return CcStorage.config() / "dt-email"


def config_path() -> Path:
    return config_dir() / "config.json"


@dataclass
class Account:
    name: str
    email: str
    user: str
    imap_host: str
    imap_port: int
    imap_security: str
    smtp_host: str
    smtp_port: int
    smtp_security: str
    secret_entry: str
    password_env: str
    save_sent: bool = False

    def to_dict(self) -> dict:
        data = asdict(self)
        del data["name"]
        return data

    @classmethod
    def from_dict(cls, name: str, data: dict) -> "Account":
        required = ("email", "user", "imap_host", "imap_port", "imap_security", "smtp_host", "smtp_port",
                    "smtp_security", "secret_entry", "password_env")
        missing = [key for key in required if key not in data]
        if missing:
            raise DtEmailError(
                f"account '{name}' in {config_path()} is missing {', '.join(missing)}. "
                f"Add it again with: dt-email account add {name} ...")
        return cls(name=name, email=data["email"], user=data["user"], imap_host=data["imap_host"],
                   imap_port=int(data["imap_port"]), imap_security=data["imap_security"],
                   smtp_host=data["smtp_host"], smtp_port=int(data["smtp_port"]),
                   smtp_security=data["smtp_security"], secret_entry=data["secret_entry"],
                   password_env=data["password_env"], save_sent=bool(data.get("save_sent", False)))

    def password(self) -> str:
        """The password from the environment, or an error naming the exact cc-secrets command."""
        value = os.environ.get(self.password_env)
        if not value:
            args = " ".join(shlex.quote(a) for a in sys.argv[1:])
            raise DtEmailError(
                f"the password variable {self.password_env} for account '{self.name}' is not set. "
                f"dt-email reads the password only from cc-secrets. Run: "
                f"cc-secrets run {self.secret_entry} -- dt-email {args}".rstrip())
        return value


@dataclass
class Config:
    accounts: Dict[str, Account] = field(default_factory=dict)
    default: Optional[str] = None
    allowed_recipients: Optional[List[str]] = None

    def account(self, name: Optional[str]) -> Account:
        """The named account, or the default one. Never guesses between several."""
        if not self.accounts:
            raise DtEmailError(
                "no account is set up. Add one with: dt-email account add <name> --email <address> "
                "--imap-host <host> --smtp-host <host> --secret-entry <cc-secrets entry> --password-env <VARIABLE>")
        if name is None:
            if self.default is None:
                if len(self.accounts) == 1:
                    return next(iter(self.accounts.values()))
                raise DtEmailError(
                    f"several accounts are set up ({', '.join(sorted(self.accounts))}) and none is the default. "
                    "Pass --account <name>, or set one with: dt-email account default <name>")
            name = self.default
        if name not in self.accounts:
            raise DtEmailError(
                f"no account named '{name}'. Accounts: {', '.join(sorted(self.accounts))}. "
                "List them with: dt-email account list")
        return self.accounts[name]

    def to_dict(self) -> dict:
        data: dict = {"default": self.default,
                      "accounts": {n: a.to_dict() for n, a in sorted(self.accounts.items())}}
        if self.allowed_recipients is not None:
            data["allowed_recipients"] = list(self.allowed_recipients)
        return data


def load() -> Config:
    path = config_path()
    if not path.exists():
        return Config()
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise DtEmailError(f"{path} is not valid JSON ({exc}). Fix or delete the file.") from exc
    accounts = {name: Account.from_dict(name, raw) for name, raw in (data.get("accounts") or {}).items()}
    allowed = data.get("allowed_recipients")
    if allowed is not None:
        allowed = [normalise_address(a) for a in allowed]
    return Config(accounts=accounts, default=data.get("default"), allowed_recipients=allowed)


def save(config: Config) -> Path:
    path = config_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(config.to_dict(), indent=2) + "\n", encoding="utf-8")
    os.replace(tmp, path)
    return path


def normalise_address(address: str) -> str:
    """One plain address, lower case. Refuses a value that is not exactly one address."""
    parsed = [a for _, a in getaddresses([address]) if a]
    if len(parsed) != 1 or "@" not in parsed[0]:
        raise DtEmailError(f"'{address}' is not one email address.", exit_code=2)
    return parsed[0].strip().lower()


def check_recipients(config: Config, addresses: List[str]) -> None:
    """Refuse the send when test mode is on and any recipient is not on the allowed list.

    Runs before any connection is opened, so a refused send sends nothing at all."""
    if config.allowed_recipients is None:
        return
    allowed = set(config.allowed_recipients)
    refused = sorted({a for a in addresses if a.lower() not in allowed})
    if refused:
        raise DtEmailError(
            f"refused before sending: {', '.join(refused)} not on the allowed recipients list "
            f"({', '.join(sorted(allowed))}). Nothing was sent. "
            "See the list with: dt-email allowed-recipients show")
