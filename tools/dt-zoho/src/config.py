"""Settings for dt-zoho, kept in one local JSON file with NO secret in it.

The file is `<cc-director config>/dt-zoho/config.json`:

    {"data_centre": "ca", "allowed_recipients": ["soren@devthrottle.com", "email@devthrottle.com"]}

`allowed_recipients`, when present, is test mode: every To, CC and BCC of anything dt-zoho sends must be on
it, or the send is refused before anything reaches Zoho. Absent means no restriction.

`data_centre` picks Zoho's servers. Zoho keeps every organisation in one data centre and its sign-in, Mail and
Calendar servers differ per data centre; a key made in one is refused by the others.

The OAuth key itself is never stored here. It comes from three environment variables that
`cc-secrets run zoho-api-client-secret --with zoho-api-client-id --with zoho-api-refresh-token -- dt-zoho ...`
supplies.
"""

from __future__ import annotations

import json
import os
import shlex
import sys
from dataclasses import dataclass
from pathlib import Path
from email.utils import getaddresses
from typing import Dict, List, Optional

from .errors import DtZohoError


@dataclass(frozen=True)
class DataCentre:
    code: str
    accounts: str   # the sign-in server (token endpoint)
    mail: str       # Mail API base, paths start /api
    calendar: str   # Calendar API base, paths start /api/v1


# Zoho's data centres. Only "ca" has been run against a real organisation (proof of 8 November 2026);
# the others follow Zoho's documented host names.
DATA_CENTRES: Dict[str, DataCentre] = {
    "ca": DataCentre("ca", "https://accounts.zohocloud.ca", "https://mail.zohocloud.ca",
                     "https://calendar.zohocloud.ca"),
    "com": DataCentre("com", "https://accounts.zoho.com", "https://mail.zoho.com", "https://calendar.zoho.com"),
    "eu": DataCentre("eu", "https://accounts.zoho.eu", "https://mail.zoho.eu", "https://calendar.zoho.eu"),
    "in": DataCentre("in", "https://accounts.zoho.in", "https://mail.zoho.in", "https://calendar.zoho.in"),
    "com.au": DataCentre("com.au", "https://accounts.zoho.com.au", "https://mail.zoho.com.au",
                         "https://calendar.zoho.com.au"),
    "jp": DataCentre("jp", "https://accounts.zoho.jp", "https://mail.zoho.jp", "https://calendar.zoho.jp"),
}

# The three variables cc-secrets supplies, and the entries they come from.
CLIENT_ID_ENV = "ZOHO_API_CLIENT_ID"
CLIENT_SECRET_ENV = "ZOHO_API_CLIENT_SECRET"
REFRESH_TOKEN_ENV = "ZOHO_API_REFRESH_TOKEN"
SECRETS_COMMAND = ("cc-secrets run zoho-api-client-secret --with zoho-api-client-id "
                   "--with zoho-api-refresh-token -- dt-zoho")


def config_dir() -> Path:
    """The folder holding config.json and the sealed token cache. Tests replace this function."""
    from cc_storage import CcStorage

    return CcStorage.config() / "dt-zoho"


def config_path() -> Path:
    return config_dir() / "config.json"


@dataclass
class Credentials:
    client_id: str
    client_secret: str
    refresh_token: str


def credentials() -> Credentials:
    """The OAuth key from the environment, or an error naming the exact cc-secrets command."""
    names = (CLIENT_ID_ENV, CLIENT_SECRET_ENV, REFRESH_TOKEN_ENV)
    missing = [n for n in names if not os.environ.get(n)]
    if missing:
        args = " ".join(shlex.quote(a) for a in sys.argv[1:])
        raise DtZohoError(
            f"{', '.join(missing)} {'is' if len(missing) == 1 else 'are'} not set. dt-zoho reads its Zoho key only "
            f"from cc-secrets. Run: {SECRETS_COMMAND} {args}".rstrip())
    return Credentials(os.environ[CLIENT_ID_ENV], os.environ[CLIENT_SECRET_ENV], os.environ[REFRESH_TOKEN_ENV])


@dataclass
class Settings:
    data_centre: Optional[str] = None
    allowed_recipients: Optional[List[str]] = None

    def centre(self) -> DataCentre:
        if self.data_centre is None:
            raise DtZohoError(
                f"no Zoho data centre is set. Set it once with: dt-zoho settings set --data-centre <code> "
                f"(one of {', '.join(DATA_CENTRES)}; Canada is ca).")
        if self.data_centre not in DATA_CENTRES:
            raise DtZohoError(
                f"the data centre '{self.data_centre}' in {config_path()} is not one of {', '.join(DATA_CENTRES)}. "
                "Set it with: dt-zoho settings set --data-centre <code>")
        return DATA_CENTRES[self.data_centre]

    def to_dict(self) -> dict:
        data: dict = {"data_centre": self.data_centre}
        if self.allowed_recipients is not None:
            data["allowed_recipients"] = list(self.allowed_recipients)
        return data


def load() -> Settings:
    path = config_path()
    if not path.exists():
        return Settings()
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise DtZohoError(f"{path} is not valid JSON ({exc}). Fix or delete the file.") from exc
    allowed = data.get("allowed_recipients")
    if allowed is not None:
        allowed = [normalise_address(a) for a in allowed]
    return Settings(data_centre=data.get("data_centre"), allowed_recipients=allowed)


def save(settings: Settings) -> Path:
    path = config_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(settings.to_dict(), indent=2) + "\n", encoding="utf-8")
    os.replace(tmp, path)
    return path


def normalise_address(address: str) -> str:
    """One plain address, lower case. Refuses a value that is not exactly one address."""
    parsed = [a for _, a in getaddresses([address]) if a]
    if len(parsed) != 1 or "@" not in parsed[0]:
        raise DtZohoError(f"'{address}' is not one email address.", exit_code=2)
    return parsed[0].strip().lower()


def check_recipients(settings: Settings, addresses: List[str]) -> None:
    """Refuse the send when test mode is on and any recipient is not on the allowed list.

    Runs before any call to Zoho, so a refused send sends nothing at all."""
    if settings.allowed_recipients is None:
        return
    allowed = set(settings.allowed_recipients)
    refused = sorted({a.lower() for a in addresses if a.lower() not in allowed})
    if refused:
        raise DtZohoError(
            f"refused before sending: {', '.join(refused)} not on the allowed recipients list "
            f"({', '.join(sorted(allowed))}). Nothing was sent. "
            "See the list with: dt-zoho allowed-recipients show")
