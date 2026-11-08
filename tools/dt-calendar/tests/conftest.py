"""Shared set-up: the tool imported from this checkout, its config in a temporary folder, and the network
replaced by the in-memory CalDAV server in fakes.py."""

import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

TOOL_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(TOOL_DIR))
sys.path.insert(0, str(TOOL_DIR.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from src import caldav, config  # noqa: E402
from src.cli import app  # noqa: E402
from fakes import FakeCalDav  # noqa: E402

ME = "me@example.com"
OTHER = "other@example.com"
OUTSIDER = "someone@elsewhere.org"
PASSWORD_ENV = "DT_CALENDAR_TEST_PASSWORD"
OTHER_ENV = "DT_CALENDAR_TEST_OTHER_PASSWORD"


class Env:
    def __init__(self, server: FakeCalDav, runner: CliRunner):
        self.server = server
        self.runner = runner
        self.me = server.users[ME]
        self.other = server.users[OTHER]

    def run(self, *args: str):
        return self.runner.invoke(app, list(args))

    def ok(self, *args: str):
        result = self.run(*args)
        assert result.exit_code == 0, f"exit {result.exit_code}: {result.output}"
        return result

    def writes(self):
        return [r for r in self.server.requests if r[0] in ("PUT", "DELETE")]


@pytest.fixture
def env(tmp_path, monkeypatch):
    server = FakeCalDav(org_domain="example.com")
    server.add_user(ME, "pw", "aaa111")
    server.add_user(OTHER, "pw2", "bbb222")
    monkeypatch.setattr(config, "config_dir", lambda: tmp_path / "dt-calendar")
    monkeypatch.setattr(caldav, "send", server.send)
    monkeypatch.setenv(PASSWORD_ENV, "pw")
    monkeypatch.setenv(OTHER_ENV, "pw2")
    e = Env(server, CliRunner())
    e.ok("account", "add", "me", "--email", ME, "--url", "https://cal.example.com", "--secret-entry", "me-entry",
         "--password-env", PASSWORD_ENV, "--default")
    e.ok("account", "add", "other", "--email", OTHER, "--url", "https://cal.example.com", "--secret-entry",
         "other-entry", "--password-env", OTHER_ENV)
    return e
