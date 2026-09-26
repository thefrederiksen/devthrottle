"""The audit log: one JSON line per use of a secret, and never the secret.

Each line records the time, the entry, the session that asked (CC_SESSION_ID, empty outside a
session), the machine, the command, and the outcome. An owner command run inside a session on the owner's
approval also records the session's name and the approval text (ownerApproved). A line that would contain any form of a secret
this process has read is refused before it reaches the disk - that is an error, not a line quietly
rewritten. The log lives in the private secrets folder, whose permissions are checked before writing.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
import os
import platform
from datetime import datetime, timezone
from pathlib import Path
from typing import Dict, List, Optional

from . import paths
from .errors import CcSecretsError
from .redact import SCRUBBER


class AuditLineContainsSecretError(CcSecretsError):
    """An audit line was about to be written with a secret in it."""


@dataclass(frozen=True)
class OwnerApproval:
    """An owner command run inside a session on the owner's word (issue 3414): the words the session reported,
    verbatim, and the session's name. Recorded on every line the command writes, so the change can be audited."""
    text: str
    session_name: str


class AuditLog:
    def __init__(self, path: Path) -> None:
        self._path = path

    @property
    def path(self) -> Path:
        return self._path

    def prepare(self, entry_name: str, command: str, outcome: str, detail: str = "",
                approval: Optional["OwnerApproval"] = None) -> str:
        """Build and check one line exactly as record would, without writing it, and return it for write_prepared.
        A caller that must not change anything unless every line will be accepted prepares them all first
        (live import on the owner's machine, pull request 2990) - the time is fixed here, so the line checked is
        the line written."""
        return self._line(entry_name, command, outcome, detail, approval)[1]

    def write_prepared(self, lines: List[str]) -> None:
        paths.ensure_home()
        with open(self._path, "a", encoding="utf-8") as fh:
            for text in lines:
                fh.write(text + "\n")

    def record(self, entry_name: str, command: str, outcome: str, detail: str = "",
               approval: Optional["OwnerApproval"] = None) -> Dict[str, str]:
        line, text = self._line(entry_name, command, outcome, detail, approval)
        self.write_prepared([text])
        return line

    def _line(self, entry_name: str, command: str, outcome: str, detail: str, approval: Optional["OwnerApproval"]):
        line = {
            "time": datetime.now(timezone.utc).isoformat(timespec="seconds"),
            "entry": entry_name,
            "session": os.environ.get("CC_SESSION_ID", ""),
            "machine": platform.node(),
            "command": SCRUBBER.scrub(command),
            "outcome": outcome,
            "detail": SCRUBBER.scrub(detail),
        }
        if approval is not None:
            line["sessionName"] = SCRUBBER.scrub(approval.session_name)
            line["ownerApproved"] = SCRUBBER.scrub(approval.text)
        text = json.dumps(line, ensure_ascii=True)
        if SCRUBBER.contains(text):
            raise AuditLineContainsSecretError("Refused to write an audit line that contained a secret.")
        return line, text

    def read(self, count: int) -> List[Dict[str, str]]:
        if not self._path.exists():
            return []
        lines = self._path.read_text(encoding="utf-8").splitlines()
        return [json.loads(l) for l in lines[-count:] if l.strip()]
