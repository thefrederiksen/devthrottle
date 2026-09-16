"""Vercel previews for the verifier.

Previews are behind Vercel's sign-in. cc-ship - never the verifier - trades the
automation bypass secret for Vercel's bypass cookie and hands the verifier a
Playwright storage-state file. The raw secret never reaches a brief, a URL, a
screen or a log.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
from collections.abc import Iterator
from contextlib import contextmanager
from pathlib import Path
from urllib.parse import urlparse

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from cc_storage.storage import CcStorage  # noqa: E402

SECRET_NAME = "VERCEL_AUTOMATION_BYPASS_SECRET"
BYPASS_COOKIE = "_vercel_jwt"


class PreviewError(RuntimeError):
    """The preview cannot be reached; the message says what to do."""


def credentials_file() -> Path:
    return CcStorage.config() / "credentials.env"


def read_bypass_secret() -> str:
    path = credentials_file()
    if path.exists():
        for line in path.read_text(encoding="utf-8").splitlines():
            key, sep, value = line.partition("=")
            if sep and key.strip() == SECRET_NAME and value.strip():
                return value.strip()
    raise PreviewError(
        f"{SECRET_NAME} is not set in {path}. Create it in Vercel (project Settings, "
        "Deployment Protection, Protection Bypass for Automation) and add the line "
        f"{SECRET_NAME}=<secret> to that file."
    )


def find_preview_url(repo_slug: str, sha: str) -> str | None:
    """The Vercel preview for exactly this commit whose LATEST status is success, if any."""
    deployments = json.loads(subprocess.run(
        ["gh", "api", f"repos/{repo_slug}/deployments?sha={sha}&environment=Preview"],
        capture_output=True, text=True, check=True,
    ).stdout)
    for deployment in deployments:
        statuses = json.loads(subprocess.run(
            ["gh", "api", f"repos/{repo_slug}/deployments/{deployment['id']}/statuses"],
            capture_output=True, text=True, check=True,
        ).stdout)
        latest = statuses[0] if statuses else None  # GitHub lists newest first
        if latest and latest["state"] == "success" and latest.get("environment_url"):
            return latest["environment_url"]
    return None


def write_bypass_state(url: str, state_file: Path) -> None:
    """Fetch the bypass cookie for this preview and save it as Playwright state."""
    # The secret goes to curl through a private header file, never the command line.
    with tempfile.TemporaryDirectory() as tmp:
        header_file = Path(tmp) / "headers"
        header_file.write_text(
            f"x-vercel-protection-bypass: {read_bypass_secret()}\n"
            "x-vercel-set-bypass-cookie: true\n",
            encoding="ascii",
        )
        header_file.chmod(0o600)
        proc = subprocess.run(
            ["curl", "-s", "-o", os.devnull, "-D", "-", "-H", f"@{header_file}", url],
            capture_output=True, text=True, timeout=60,
        )
    if proc.returncode != 0:
        raise PreviewError(f"curl could not reach {url} (exit {proc.returncode}): {proc.stderr.strip()}")
    cookie_value = None
    for line in proc.stdout.splitlines():
        name, _, value = line.partition(":")
        if name.strip().lower() == "set-cookie":
            cookie_name, _, rest = value.strip().partition("=")
            if cookie_name == BYPASS_COOKIE:
                cookie_value = rest.split(";", 1)[0]
    if not cookie_value:
        raise PreviewError(
            f"Vercel returned no {BYPASS_COOKIE} cookie for {url}. The secret in "
            f"{credentials_file()} may be wrong or revoked; check it in the Vercel project settings."
        )
    host = urlparse(url).hostname
    state = {
        "cookies": [{
            "name": BYPASS_COOKIE, "value": cookie_value, "domain": host, "path": "/",
            "expires": -1, "httpOnly": True, "secure": True, "sameSite": "Lax",
        }],
        "origins": [],
    }
    # The cookie is itself a bypass credential. The run folder lives in the user's own
    # profile (per-user access on Windows); chmod narrows it further on macOS.
    # It must never outlive the verifier: the engine removes it on every verifier end
    # and every failure; the probe uses bypass_state().
    state_file.write_text(json.dumps(state), encoding="ascii")
    state_file.chmod(0o600)


def remove_bypass_state(state_file: Path) -> None:
    state_file.unlink(missing_ok=True)


@contextmanager
def bypass_state(url: str, state_file: Path) -> Iterator[Path]:
    """The bypass state file exists exactly for the body of the with-block."""
    try:
        write_bypass_state(url, state_file)
        yield state_file
    finally:
        remove_bypass_state(state_file)
