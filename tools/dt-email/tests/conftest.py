"""Shared set-up: the tool imported from this checkout, its config in a temporary folder, and the network
replaced by the in-memory servers in fakes.py."""

import sys
from email.message import EmailMessage
from email.utils import make_msgid
from pathlib import Path

import pytest
from typer.testing import CliRunner

TOOL_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(TOOL_DIR))
sys.path.insert(0, str(TOOL_DIR.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from src import config, imap_client, smtp_client  # noqa: E402
from src.cli import app  # noqa: E402
from fakes import FakeImapServer, SmtpOutbox  # noqa: E402

ME = "me@example.com"
PASSWORD_ENV = "DT_EMAIL_TEST_PASSWORD"


class Env:
    def __init__(self, imap: FakeImapServer, smtp: SmtpOutbox, runner: CliRunner):
        self.imap = imap
        self.smtp = smtp
        self.runner = runner

    def run(self, *args: str):
        return self.runner.invoke(app, list(args))

    def ok(self, *args: str):
        result = self.run(*args)
        assert result.exit_code == 0, f"exit {result.exit_code}: {result.output}"
        return result


def make_message(subject="Hello", sender="Alice <alice@example.org>", to=ME, cc=None, body="Hi there.",
                 attachments=(), message_id=None, references=None) -> bytes:
    msg = EmailMessage()
    msg["From"] = sender
    msg["To"] = to
    if cc:
        msg["Cc"] = cc
    msg["Subject"] = subject
    msg["Date"] = "Sat, 7 Nov 2026 17:31:20 -0500"
    msg["Message-ID"] = message_id or make_msgid(domain="example.org")
    if references:
        msg["References"] = references
    msg.set_content(body)
    for name, data, ctype in attachments:
        maintype, subtype = ctype.split("/")
        msg.add_attachment(data, maintype=maintype, subtype=subtype, filename=name)
    return msg.as_bytes()


@pytest.fixture
def env(tmp_path, monkeypatch):
    imap = FakeImapServer(user=ME, password="pw")
    smtp = SmtpOutbox(user=ME, password="pw", allowed_senders=(ME, "alias@example.com"))
    monkeypatch.setattr(config, "config_dir", lambda: tmp_path / "dt-email")
    monkeypatch.setattr(imap_client, "connect", imap.connect)
    monkeypatch.setattr(smtp_client, "connect", smtp.connect)
    monkeypatch.setenv(PASSWORD_ENV, "pw")
    e = Env(imap, smtp, CliRunner())
    e.ok("account", "add", "test", "--email", ME, "--imap-host", "imap.example.com", "--smtp-host",
         "smtp.example.com", "--secret-entry", "test-entry", "--password-env", PASSWORD_ENV)
    return e
