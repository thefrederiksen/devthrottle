"""The license switching SETTING: `cc-devthrottle license switching status / on / off` (issue #4327).

THIS IS THE SWITCH THE DIRECTOR READS, and nothing else wrote it until this command: the only way to turn switching on
was to edit config.json by hand, and a fresh Director data folder silently turned it off. The Director reads the same
value through `LicenseSwitchingSetting` (C#, `src/CcDirector.Core/Configuration/LicenseSwitchingSetting.cs`), and every
rule here is that class's rule, copied deliberately:

  * THE PLACE. `licenseSwitching.enabled` in each Director's OWN config.json,
    `<user base>/instances/<name>/config/config.json`. A Director reads only its own home (the trap of issue #3506),
    so a value written to the machine root's config.json - where `CcStorage.config_json()` points from a plain
    terminal - would be read by no Director at all. Every Director on the machine is written unless `--instance`
    names one, because the key store and its lock belong to the machine. The write is a deep merge, so every other
    section of each file - and `licenseSwitching.offerApprovers` beside it - is kept.
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
    """The setting cannot be read or written; carries the next steps to print."""

    def __init__(self, message: str, next_steps: List[str]):
        super().__init__(message)
        self.next_steps = next_steps


def instance_homes() -> List[Path]:
    """Every Director instance home of this operating-system user, whatever CC_DIRECTOR_ROOT says."""
    return CcStorage.instance_homes()


def config_path(home: Path) -> Path:
    """The config.json the Director whose home is `home` reads."""
    return home / "config" / "config.json"


def chosen_homes(instance: Optional[str]) -> List[Path]:
    """The Director homes a command acts on: all of them, or the one `--instance` names. None found is an error, never
    a silent no-op, because a setting written nowhere reads exactly like one that worked."""
    homes = instance_homes()
    if not homes:
        raise SettingsError("no Director instance home was found on this machine, so there is no Director to set",
                            ["Start the Director once, then run: " + STATUS])
    if instance is None:
        return homes
    matching = [h for h in homes if h.name.lower() == instance.strip().lower()]
    if not matching:
        names = ", ".join(h.name for h in homes)
        raise SettingsError(f"no Director instance is named '{instance}'; the instances here are: {names}", [STATUS])
    return matching


def read_document(path: Path) -> Dict[str, Any]:
    """The whole of one config.json. Missing or empty is an empty document; a file that cannot be parsed RAISES - it
    is never silently replaced, because writing over a document nobody could read destroys the owner's settings."""
    if not path.exists():
        return {}
    text = path.read_text(encoding="utf-8")
    if not text.strip():
        return {}
    try:
        document = json.loads(text)
    except json.JSONDecodeError as error:
        raise SettingsError(f"{path} is not readable JSON: {error}", [f"Repair or remove {path}"]) from error
    if not isinstance(document, dict):
        raise SettingsError(f"{path} does not hold a JSON object", [f"Repair or remove {path}"])
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


def save(path: Path, enabled: bool) -> None:
    """Write `licenseSwitching.enabled` into one config.json (LicenseSwitchingSetting.Set): a deep merge, then an
    atomic replace, so a crash part way through never leaves a half-written file."""
    document = read_document(path)
    section = document.get(SECTION)
    if not isinstance(section, dict):
        section = {}
        document[SECTION] = section
    section[ENABLED] = bool(enabled)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.parent / f".config.{uuid.uuid4().hex}.tmp"
    temporary.write_text(json.dumps(document, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def _why(explicit: Optional[bool], effective: bool) -> str:
    if explicit is None:
        return ("on because this machine holds spares and its config.json does not say" if effective
                else "off because this machine holds no spare and its config.json does not say")
    return f"{'on' if effective else 'off'} because its config.json says so"


def _report(homes: List[Path], store_dir: str, json_output: bool, changed: Optional[Dict[Path, bool]]) -> None:
    spares = spare_emails(store_dir)
    rows = []
    for home in homes:
        explicit = read_explicit(read_document(config_path(home)))
        effective = resolve(explicit, bool(spares))
        rows.append({
            "instance": home.name,
            "enabled": effective,
            "configured": explicit,
            "why": _why(explicit, effective),
            "changed": None if changed is None else changed[home],
            "configFile": str(config_path(home)),
        })
    if json_output:
        print(json.dumps({
            "count": len(rows),
            "sparesHeld": len(spares),
            "storeDir": store_dir,
            "note": TAKES_EFFECT,
            "instances": rows,
        }, indent=2))
        return
    fields = ["instance", "enabled", "configured", "why"]
    table = [{
        "instance": r["instance"],
        "enabled": r["enabled"],
        "configured": "true" if r["configured"] is True else "false" if r["configured"] is False else "not set",
        "why": r["why"],
    } for r in rows]
    axi_output.write_blocks(
        sys.stdout,
        f"spares_held: {len(spares)}",
        axi_output.render_list("instances", fields, table),
        f"note: {axi_output.format_value(TAKES_EFFECT)}",
    )
    axi_cli.print_next([OFF if any(r["enabled"] for r in rows) else ON, STATUS + " --json"])


def status(store_dir: str, instance: Optional[str], json_output: bool) -> None:
    """Say, for each Director on this machine, whether license switching is on and why."""
    try:
        _report(chosen_homes(instance), store_dir, json_output, None)
    except SettingsError as error:
        axi_cli.fail(f"{error}; license switching was not read", error.next_steps)


def turn(enabled: bool, store_dir: str, instance: Optional[str], json_output: bool) -> None:
    """Write the setting into each chosen Director's own config.json, then report it as each Director will read it -
    read back, not what was asked for. Every file is read before any is written, so one unreadable file changes none."""
    try:
        homes = chosen_homes(instance)
        before = {home: read_explicit(read_document(config_path(home))) for home in homes}
        for home in homes:
            save(config_path(home), enabled)
        _report(homes, store_dir, json_output, {home: before[home] != enabled for home in homes})
    except SettingsError as error:
        axi_cli.fail(f"{error}; license switching was not changed", error.next_steps)
