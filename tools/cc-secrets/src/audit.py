"""The audit log: one JSON line per use of a secret, and never the secret.

Each line records the time, the entry, the session that asked (CC_SESSION_ID, empty outside a
session), the machine, the command, and the outcome. A line that would contain any form of an open
secret is refused before it reaches the disk - that is an error, not a line quietly rewritten.
"""

from __future__ import annotations

import json
import os
import platform
from datetime import datetime, timezone
from pathlib import Path
from typing import Dict, List

from .redact import SCRUBBER


class AuditLineContainsSecretError(RuntimeError):
    """An audit line was about to be written with a secret in it."""


class AuditLog:
    def __init__(self, path: Path) -> None:
        self._path = path

    @property
    def path(self) -> Path:
        return self._path

    def record(self, entry_name: str, command: str, outcome: str, detail: str = "") -> Dict[str, str]:
        line = {
            "time": datetime.now(timezone.utc).isoformat(timespec="seconds"),
            "entry": entry_name,
            "session": os.environ.get("CC_SESSION_ID", ""),
            "machine": platform.node(),
            "command": SCRUBBER.scrub(command),
            "outcome": outcome,
            "detail": SCRUBBER.scrub(detail),
        }
        text = json.dumps(line, ensure_ascii=True)
        if SCRUBBER.contains(text):
            raise AuditLineContainsSecretError("Refused to write an audit line that contained a secret.")
        self._path.parent.mkdir(parents=True, exist_ok=True)
        with open(self._path, "a", encoding="utf-8") as fh:
            fh.write(text + "\n")
        return line

    def read(self, count: int) -> List[Dict[str, str]]:
        if not self._path.exists():
            return []
        lines = self._path.read_text(encoding="utf-8").splitlines()
        return [json.loads(l) for l in lines[-count:] if l.strip()]
