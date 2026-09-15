"""The tool's diagnostic log.

Every line is scrubbed of every secret this process has opened before it is written. The callers
also never pass a secret or a browser debug-protocol parameter in the first place - the log records
method names and outcomes only - so the scrub is a second guard, not the plan.
"""

from __future__ import annotations

from datetime import datetime, timezone

from . import paths
from .redact import SCRUBBER


def write(message: str) -> None:
    """Append one timestamped, scrubbed line to today's log file."""
    folder = paths.ensure_home() / "logs"
    folder.mkdir(exist_ok=True)
    now = datetime.now(timezone.utc)
    line = f"{now.isoformat(timespec='milliseconds')} {SCRUBBER.scrub(message)}\n"
    with open(folder / f"cc-secrets-{now:%Y-%m-%d}.log", "a", encoding="utf-8") as fh:
        fh.write(line)
