"""A message never carries a stored secret (the Secret Handoff mission, plan section 4.7).

Before a fleet message or a reply leaves this machine, cc-secrets is asked whether the text contains any value it
holds here. It answers only yes or no - never which entry - and cc-devthrottle never sees a secret: the text goes to
cc-secrets on standard input and one JSON line comes back. A message that carries one is refused, with the way a
session should get a credential instead. When cc-secrets cannot answer, the message is not sent either: an unchecked
message is exactly the leak this guard exists to stop.
"""

from __future__ import annotations

import json
import shutil
import subprocess
from typing import Optional

from . import axi_cli

CHECK_TIMEOUT_SECONDS = 30
INSTEAD = ("cc-secrets request <entry> --from <machine> --reason \"<why>\"   (run by the session that needs it, "
           "on its own machine)")


def _answer(text: str) -> Optional[bool]:
    """True when the text holds a stored secret, False when it does not, None when cc-secrets could not say."""
    exe = shutil.which("cc-secrets")
    if exe is None:
        axi_cli.fail(
            "cc-secrets is not installed on this machine, so the text could not be checked for a stored secret, "
            "and it was not sent.",
            ["cc-devthrottle setup status", "Install DevThrottle's tools again, then send it again."],
            label="Not sent:",
        )
    try:
        proc = subprocess.run([exe, "check-text"], input=text, capture_output=True, text=True, encoding="utf-8",
                              timeout=CHECK_TIMEOUT_SECONDS)
    except subprocess.TimeoutExpired:
        return None
    lines = [line for line in (proc.stdout or "").splitlines() if line.strip()]
    if proc.returncode != 0 or not lines:
        return None
    try:
        answer = json.loads(lines[-1])
    except ValueError:
        return None
    value = answer.get("containsSecret") if isinstance(answer, dict) else None
    return value if isinstance(value, bool) else None


def refuse_if_it_carries_a_secret(text: str, what: str) -> None:
    """Stop here, with the reason, if `text` contains a value cc-secrets holds on this machine."""
    answer = _answer(text)
    if answer is None:
        axi_cli.fail(
            f"cc-secrets could not check this {what} for a stored secret, so it was not sent. An older cc-secrets "
            "has no 'check-text' command.",
            ["cc-secrets version", "cc-devthrottle setup status"],
            label="Not sent:",
        )
    if answer:
        axi_cli.fail(
            f"this {what} contains a value stored in cc-secrets on this machine, so it was not sent. Never put a "
            "secret in a message: the session that needs a credential asks for it, and the owner approves.",
            [INSTEAD, "cc-secrets machines"],
            label="Not sent:",
        )
