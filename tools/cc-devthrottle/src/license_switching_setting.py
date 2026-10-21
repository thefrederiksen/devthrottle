"""The license switching SETTING: `cc-devthrottle license switching status / on / off` (issue #4327).

THIS IS THE SWITCH THE DIRECTOR READS, and nothing else wrote it until this command: the only way to turn switching on
was to edit config.json by hand, and a fresh Director data folder silently turned it off. The Director reads the same
value through `LicenseSwitchingSetting` (C#, `src/CcDirector.Core/Configuration/LicenseSwitchingSetting.cs`), and every
rule here is that class's rule, copied deliberately:

  * THE PLACE. `licenseSwitching.enabled` in the same `config.json` (`CcStorage.config_json()`), written as a deep
    merge so every other section of the file - and `licenseSwitching.offerApprovers` beside it - is kept.
  * THE VALUE WHEN NOTHING IS WRITTEN. An explicit true is on and an explicit false is off; anything else (no file, no
    section, no key, a value that is not a boolean) is ON when this machine's license store holds a spare and off when
    it holds none. `off` therefore writes an explicit false - it is how the owner opts a machine out - and is never
    the same as leaving the key out.
  * THE SPARES. Counted from the store's account files alone (`<store>/spares/*/.claude.json` naming an account), the
    way `LicenseHeldOnDisk` counts them: no key is read and the license tool is not run.
  * WHEN IT TAKES EFFECT. The Director reads it when it starts, so a change takes a Director restart.
"""

from __future__ import annotations

import json
import os
import sys
import uuid
from pathlib import Path
from typing import Any, Dict, List, Optional

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output  # noqa: E402
from cc_storage import CcStorage  # noqa: E402

from . import axi_cli  # noqa: E402

SECTION = "licenseSwitching"
ENABLED = "enabled"
SPARES_DIR = "spares"
ACCOUNT_FILE = ".claude.json"

STATUS = "cc-devthrottle license switching status"
ON = "cc-devthrottle license switching on"
OFF = "cc-devthrottle license switching off"
TAKES_EFFECT = "The Director reads this when it starts: restart the Director for a change to take effect."


class SettingsError(Exception):
    """config.json cannot be read, so the setting is neither reported nor changed."""


def config_path() -> Path:
    """The shared settings file, resolved the way every other cc-* tool and the Director resolve it."""
    return CcStorage.config_json()


def read_document() -> Dict[str, Any]:
    """The whole of config.json. Missing or empty is an empty document; a file that cannot be parsed RAISES - it is
    never silently replaced, because writing over a document nobody could read destroys the owner's settings."""
    path = config_path()
    if not path.exists():
        return {}
    text = path.read_text(encoding="utf-8")
    if not text.strip():
        return {}
    try:
        document = json.loads(text)
    except json.JSONDecodeError as error:
        raise SettingsError(f"{path} is not readable JSON: {error}") from error
    if not isinstance(document, dict):
        raise SettingsError(f"{path} does not hold a JSON object")
    return document


def read_explicit(document: Dict[str, Any]) -> Optional[bool]:
    """True or False when config.json says so; None when it says nothing (LicenseSwitchingSetting.ReadExplicit)."""
    section = document.get(SECTION)
    value = section.get(ENABLED) if isinstance(section, dict) else None
    return value if isinstance(value, bool) else None


def resolve(explicit: Optional[bool], holds_spare: bool) -> bool:
    """The effective setting (LicenseSwitchingSetting.Resolve): what config.json says, else on exactly when the machine
    holds a spare."""
    return explicit if explicit is not None else holds_spare


def spare_emails(store_dir: str) -> List[str]:
    """Every spare the store holds, by the account its folder's account file names, lower-cased and sorted."""
    spares = Path(store_dir) / SPARES_DIR
    if not spares.is_dir():
        return []
    emails = set()
    for folder in spares.iterdir():
        account = folder / ACCOUNT_FILE
        if not folder.is_dir() or not account.is_file():
            continue
        try:
            data = json.loads(account.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue  # an account file that cannot be read names no account, as the key store reads it
        block = data.get("oauthAccount") if isinstance(data, dict) else None
        email = block.get("emailAddress") if isinstance(block, dict) else None
        if isinstance(email, str) and email.strip():
            emails.add(email.strip().lower())
    return sorted(emails)


def save(enabled: bool) -> None:
    """Write `licenseSwitching.enabled` (LicenseSwitchingSetting.Set): a deep merge into config.json, then an atomic
    replace, so a crash part way through never leaves a half-written file."""
    document = read_document()
    section = document.get(SECTION)
    if not isinstance(section, dict):
        section = {}
        document[SECTION] = section
    section[ENABLED] = bool(enabled)
    path = config_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.parent / f".config.{uuid.uuid4().hex}.tmp"
    temporary.write_text(json.dumps(document, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def _report(store_dir: str, json_output: bool, changed: Optional[bool]) -> None:
    try:
        explicit = read_explicit(read_document())
    except SettingsError as error:
        axi_cli.fail(f"{error}, so license switching was not read", [f"Repair or remove {config_path()}"])
    spares = spare_emails(store_dir)
    effective = resolve(explicit, bool(spares))
    configured = "true" if explicit is True else "false" if explicit is False else "not set"
    if explicit is None:
        why = ("on because this machine holds spares and config.json does not say" if effective
               else "off because this machine holds no spare and config.json does not say")
    else:
        why = f"{'on' if effective else 'off'} because config.json says so"
    following = [OFF if effective else ON, STATUS + " --json"]
    if json_output:
        print(json.dumps({
            "enabled": effective,
            "configured": explicit,
            "sparesHeld": len(spares),
            "why": why,
            "changed": changed,
            "configFile": str(config_path()),
            "storeDir": store_dir,
            "note": TAKES_EFFECT,
        }, indent=2))
        return
    record = {
        "enabled": effective,
        "configured": configured,
        "spares_held": len(spares),
        "why": why,
        "note": TAKES_EFFECT,
    }
    body = "\n".join(f"{key}: {axi_output.format_value(value)}" for key, value in record.items())
    axi_output.write_blocks(sys.stdout, body)
    axi_cli.print_next(following)


def status(store_dir: str, json_output: bool) -> None:
    """Say whether license switching is on for this machine's Director, and why."""
    _report(store_dir, json_output, None)


def turn(enabled: bool, store_dir: str, json_output: bool) -> None:
    """Write the setting, then report it as the Director will read it - read back, not what was asked for."""
    try:
        before = read_explicit(read_document())
        save(enabled)
    except SettingsError as error:
        axi_cli.fail(f"{error}, so license switching was not changed", [f"Repair or remove {config_path()}"])
    _report(store_dir, json_output, before != enabled)
