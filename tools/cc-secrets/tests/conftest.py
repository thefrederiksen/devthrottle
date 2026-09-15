"""Shared fixtures for the cc-secrets tests.

Every test runs against its own secrets folder (CC_SECRETS_HOME points into pytest's temp folder), so
nothing here reads or writes the owner's real store. Every test starts outside a DevThrottle session
(CC_SESSION_ID removed) unless it sets one on purpose, and with an empty scrubber.
"""

import re
import secrets as _secrets
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


def add_entry(store, name="devlinux", secret=None, username="leak-user", domains=("127.0.0.1",),
              agents=True, uses=("login", "run"), notes="test entry"):
    secret = secret or new_secret()
    store.put(make_entry(name, username, secret, list(domains), notes, agents, list(uses)))
    return secret


@pytest.fixture
def plain():
    return lambda text: _ANSI_STYLE.sub("", text)
