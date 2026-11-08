"""Shared set-up: the tool imported from this checkout, its settings in a temporary folder, the Zoho key in the
environment as cc-secrets would supply it, and the network replaced by the in-memory Zoho in fakes.py."""

import sys
from datetime import datetime, timezone
from pathlib import Path

import pytest
from typer.testing import CliRunner

TOOL_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(TOOL_DIR))
sys.path.insert(0, str(TOOL_DIR.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from src import admin, config, transport  # noqa: E402
from src.cli import app  # noqa: E402
from fakes import FakeZoho  # noqa: E402


class Env:
    def __init__(self, zoho: FakeZoho, runner: CliRunner, folder: Path):
        self.zoho = zoho
        self.runner = runner
        self.folder = folder

    def run(self, *args: str):
        return self.runner.invoke(app, list(args))

    def ok(self, *args: str):
        result = self.run(*args)
        assert result.exit_code == 0, f"exit {result.exit_code}: {result.output}"
        return result


@pytest.fixture
def bare(tmp_path, monkeypatch):
    """No settings yet, the key in the environment."""
    zoho = FakeZoho()
    monkeypatch.setattr(config, "config_dir", lambda: tmp_path / "dt-zoho")
    monkeypatch.setattr(transport, "send", zoho.send)
    # No test waits on the clock: the read-back pauses are recorded, not slept.
    zoho.pauses = []
    monkeypatch.setattr(admin, "_sleep", zoho.pauses.append)
    # Nor on the machine's date: 03:00 UTC on 9 November is still 8 November in the account's Toronto.
    monkeypatch.setattr(admin, "_now", lambda: datetime(2026, 11, 9, 3, 0, tzinfo=timezone.utc))
    monkeypatch.setenv(config.CLIENT_ID_ENV, zoho.client_id)
    monkeypatch.setenv(config.CLIENT_SECRET_ENV, zoho.client_secret)
    monkeypatch.setenv(config.REFRESH_TOKEN_ENV, zoho.refresh_token)
    return Env(zoho, CliRunner(), tmp_path / "dt-zoho")


@pytest.fixture
def env(bare):
    """Data centre set to Canada, as on the owner's machine."""
    bare.ok("settings", "set", "--data-centre", "ca")
    return bare
