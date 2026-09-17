"""Vercel previews for the verifier.

Previews are behind Vercel's sign-in. cc-ship - never the verifier - trades the
automation bypass secret for Vercel's bypass cookie and hands the verifier a
Playwright storage-state file. The raw secret never reaches a brief, a URL, a
screen or a log - and it never reaches cc-ship either: the one curl call that
needs it runs through `cc-secrets run`, which writes it to curl's standard input,
and curl expands it into the header itself.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
from collections.abc import Iterator
from contextlib import contextmanager
from pathlib import Path
from urllib.parse import urlparse

import fleet

SECRET_ENTRY = "vercel-automation-bypass-secret"
SECRETS_CLI = "cc-secrets"
VERCEL_PREVIEW_SUFFIX = ".vercel.app"
BYPASS_COOKIE = "_vercel_jwt"


class PreviewError(RuntimeError):
    """The preview cannot be reached; the message says what to do."""


def bypass_command(url: str) -> list[str]:
    """curl, started through cc-secrets, fetching the bypass cookie for one Vercel preview.

    The secret never enters this process and never sits on a command line: cc-secrets writes it to
    curl's standard input, curl (8.3 or newer) reads it into a variable (`--variable bypass@-`) and
    expands that into the header. Standard input rather than an environment variable because curl
    names an environment variable with a percent sign, and on Windows cc-secrets is a .cmd file whose
    arguments cmd.exe re-reads - a percent sign there is never safe (see fleet.command).

    The address is checked first, because a Vercel secret may only ever be sent to Vercel.
    """
    parsed = urlparse(url)
    host = parsed.hostname or ""
    if parsed.scheme != "https" or not host.endswith(VERCEL_PREVIEW_SUFFIX):
        raise PreviewError(
            f"Refusing to send the Vercel bypass secret to {url}: it is only ever sent over https to a "
            f"*{VERCEL_PREVIEW_SUFFIX} preview."
        )
    cli = shutil.which(SECRETS_CLI)
    if cli is None:
        raise PreviewError(f"{SECRETS_CLI} is not on PATH; cc-ship reaches the Vercel bypass secret through it.")
    args = [
        "run", SECRET_ENTRY, "--via", "stdin", "--",
        "curl", "-s", "-o", os.devnull, "-D", "-",
        "--variable", "bypass@-",
        "--expand-header", "x-vercel-protection-bypass: {{bypass:trim}}",
        "-H", "x-vercel-set-bypass-cookie: true",
        url,
    ]
    if cli.lower().endswith((".cmd", ".bat")):
        bad = [a for a in args if fleet._cmd_misreads(a)]
        if bad:
            raise PreviewError(f"cannot pass {bad[0]!r} safely to {cli}: cmd.exe would misread it.")
    return [cli, *args]


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
    proc = subprocess.run(bypass_command(url), capture_output=True, text=True, timeout=90)
    if proc.returncode != 0:
        raise PreviewError(
            f"Could not fetch the preview bypass cookie from {url} (exit {proc.returncode}): "
            f"{proc.stderr.strip()} The secret is the cc-secrets entry {SECRET_ENTRY} (see cc-secrets list; "
            f"the owner adds it with: cc-secrets add {SECRET_ENTRY}), and curl must be 8.3 or newer."
        )
    cookie_value = None
    for line in proc.stdout.splitlines():
        name, _, value = line.partition(":")
        if name.strip().lower() == "set-cookie":
            cookie_name, _, rest = value.strip().partition("=")
            if cookie_name == BYPASS_COOKIE:
                cookie_value = rest.split(";", 1)[0]
    if not cookie_value:
        raise PreviewError(
            f"Vercel returned no {BYPASS_COOKIE} cookie for {url}. The cc-secrets entry "
            f"{SECRET_ENTRY} may be wrong or revoked; check it in the Vercel project settings."
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
