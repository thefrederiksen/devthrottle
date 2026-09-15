"""Shared fixtures for the cc-secrets tests.

Every test runs against its own secrets folder (CC_SECRETS_HOME points into pytest's temp folder), so
nothing here reads or writes the owner's real store. Every test starts outside a DevThrottle session
(CC_SESSION_ID removed) unless it sets one on purpose, and with an empty scrubber.
"""

import os
import re
import secrets as _secrets
import subprocess
import sys
from pathlib import Path

import pytest

TOOL_DIR = Path(__file__).resolve().parent.parent
if str(TOOL_DIR) not in sys.path:
    sys.path.insert(0, str(TOOL_DIR))

from src import paths  # noqa: E402
from src.redact import SCRUBBER  # noqa: E402
from src.store import SecretStore, make_entry  # noqa: E402
from src.storefile import UserOnlyFile  # noqa: E402

_ANSI_STYLE = re.compile(r"\x1b\[[0-9;]*m")


def new_secret() -> str:
    """A secret unique to this test run, so a hit can only come from this run."""
    return "Sx" + _secrets.token_hex(12)


@pytest.fixture
def home(tmp_path, monkeypatch):
    folder = tmp_path / "secrets-home"
    monkeypatch.setenv("CC_SECRETS_HOME", str(folder))
    monkeypatch.delenv("CC_SESSION_ID", raising=False)
    monkeypatch.setattr(paths, "_checked_home", None)
    SCRUBBER.clear()
    yield folder
    SCRUBBER.clear()


@pytest.fixture
def store(home):
    return SecretStore(UserOnlyFile(paths.store_path()))


def add_entry(store, name="devlinux", secret=None, username="leak-user", domains=("https://127.0.0.1",),
              agents=True, uses=("login", "run"), notes="test entry"):
    secret = secret or new_secret()
    store.put(make_entry(name, username, secret, list(domains), notes, agents, list(uses)))
    return secret


@pytest.fixture
def plain():
    return lambda text: _ANSI_STYLE.sub("", text)


def entry_point_name() -> str:
    """The function pyproject.toml installs as the cc-secrets command."""
    text = (TOOL_DIR / "pyproject.toml").read_text(encoding="utf-8")
    match = re.search(r'(?m)^cc-secrets\s*=\s*"cc_secrets\.cli:(\w+)"', text)
    assert match, "pyproject.toml has no cc-secrets console-script entry"
    return match.group(1)


def run_entry_point(args, env, stdin=None, prelude=""):
    """Run cc-secrets the way the installed command runs: the console-script function named in
    pyproject.toml, in a fresh process, with nothing around it to catch what it raises. (Typer's test
    runner catches exceptions, so it can never show what a crash prints.)"""
    code = "\n".join([
        "import sys",
        f"sys.path.insert(0, {str(TOOL_DIR)!r})",
        "import src.cli as _cli",
        prelude,
        f"getattr(_cli, {entry_point_name()!r})()",
    ])
    full_env = {k: v for k, v in os.environ.items() if k not in ("CC_SESSION_ID", "CC_SECRETS_HOME")}
    full_env.update(env)
    return subprocess.run([sys.executable, "-c", code, *args], input=stdin, capture_output=True,
                          env=full_env, timeout=180)
