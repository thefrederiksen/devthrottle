"""Tests for the cc-devthrottle email owner verb (issue #1318 consumer)."""

import base64
import sys
from pathlib import Path
from unittest.mock import patch

from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src.cli import app  # noqa: E402

runner = CliRunner()


def _run_owner(extra_args, send_return=None):
    with patch("src.email_ops.EmailClient") as client_cls:
        instance = client_cls.return_value
        instance.send_owner.return_value = send_return if send_return is not None else {"sent": True}
        result = runner.invoke(app, ["email", "owner"] + extra_args)
        call = instance.send_owner.call_args
    return result, call


def test_owner_send_passes_subject_and_body():
    result, call = _run_owner(
        ["--subject", "Nightly drift", "--body", "2 aging PRs"],
        send_return={"sent": True, "providerId": "resend-1"},
    )
    assert result.exit_code == 0
    # send_owner(subject, body_text, body_html, attachments)
    assert call.args[0] == "Nightly drift"
    assert call.args[1] == "2 aging PRs"
    assert call.args[3] == []  # no attachments
    assert "Sent" in result.output


def test_owner_has_no_recipient_option(plain):
    result = runner.invoke(app, ["email", "owner", "--help"])
    assert result.exit_code == 0
    # Asserted against the text, not the rendering: Rich styles the help panel, so the raw capture
    # threads escape sequences through the option names (see tests/conftest.py).
    help_text = plain(result.output)
    assert "--subject" in help_text
    assert "--attach" in help_text
    assert "--to" not in help_text
    assert "--recipient" not in help_text


def test_owner_requires_a_body_or_attachment():
    result, call = _run_owner(["--subject", "Only a subject"])
    assert result.exit_code != 0
    assert call is None
    assert "body" in result.output.lower()


def test_owner_html_body_is_forwarded():
    result, call = _run_owner(["--subject", "Report", "--html", "<p>drift</p>"])
    assert result.exit_code == 0
    assert call.args[1] is None       # no plain text
    assert call.args[2] == "<p>drift</p>"


def test_owner_attaches_file_as_base64(tmp_path):
    report = tmp_path / "report.html"
    report.write_text("<html><body>offline report</body></html>", encoding="utf-8")

    result, call = _run_owner(["--subject", "Report", "--attach", str(report)])
    assert result.exit_code == 0
    attachments = call.args[3]
    assert len(attachments) == 1
    a = attachments[0]
    assert a["filename"] == "report.html"
    assert a["contentType"] == "text/html"
    decoded = base64.b64decode(a["content"]).decode("utf-8")
    assert decoded == "<html><body>offline report</body></html>"


def test_owner_missing_attachment_fails_clearly(tmp_path):
    missing = tmp_path / "nope.html"
    result, call = _run_owner(["--subject", "Report", "--attach", str(missing)])
    assert result.exit_code != 0
    assert "not found" in result.output.lower()


# ---- in-mail by default, email only with --email (the owner, 8 November 2026) ----


_INBOX_ANSWER = {
    "sent": True,
    "providerId": "your-inbox:7b0c",
    "deliveredTo": "inbox",
    "inboxMessageId": "7b0c",
    "message": "Delivered to Your inbox, not sent as an email.",
}


def test_owner_default_asks_for_your_inbox_and_prints_the_gateway_sentence():
    result, call = _run_owner(["--subject", "Nightly drift", "--body", "2 aging"], send_return=_INBOX_ANSWER)
    assert result.exit_code == 0
    assert call.kwargs.get("email") is False
    assert "sent: Your inbox" in result.output
    assert "id: 7b0c" in result.output
    assert "Delivered to Your inbox, not sent as an email." in result.output


def test_owner_email_flag_asks_for_a_real_email():
    answer = {"sent": True, "providerId": "resend-1", "deliveredTo": "email", "message": "Sent as an email to the account owner, as asked."}
    result, call = _run_owner(["--subject", "S", "--body", "B", "--email"], send_return=answer)
    assert result.exit_code == 0
    assert call.kwargs.get("email") is True
    assert "sent: email" in result.output
    assert "id: resend-1" in result.output


def test_owner_attachment_sent_as_email_shows_the_note(tmp_path):
    report = tmp_path / "week.pdf"
    report.write_bytes(b"pdf")
    answer = {
        "sent": True,
        "providerId": "resend-2",
        "deliveredTo": "email",
        "inboxMessageId": "n1",
        "message": "Sent as an email to the account owner, not to Your inbox, because it carries 1 attachment.",
    }
    result, _ = _run_owner(["--subject", "S", "--attach", str(report)], send_return=answer)
    assert result.exit_code == 0
    assert "sent: email with 1 attachment(s)" in result.output
    assert "note in Your inbox: n1" in result.output


def test_owner_json_keeps_the_gateway_answer_whole():
    result, _ = _run_owner(["--subject", "S", "--body", "B", "--json"], send_return=_INBOX_ANSWER)
    assert result.exit_code == 0
    import json as _json
    assert _json.loads(result.output) == _INBOX_ANSWER


def test_owner_older_gateway_answer_says_an_email_was_sent():
    result, _ = _run_owner(["--subject", "S", "--body", "B"], send_return={"sent": True, "providerId": "resend-3"})
    assert result.exit_code == 0
    assert "Sent email to the account owner (id resend-3)." in result.output
    assert "older" in result.output


def test_owner_unknown_flag_fails():
    result = runner.invoke(app, ["email", "owner", "--subject", "S", "--body", "B", "--inmail"])
    assert result.exit_code != 0


def test_owner_help_says_your_inbox_is_the_default(plain):
    result = runner.invoke(app, ["email", "owner", "--help"])
    assert result.exit_code == 0
    help_text = plain(result.output)
    assert "--email" in help_text
    assert "Your inbox" in help_text


class _Response:
    status_code = 200
    headers = {"Content-Type": "application/json"}
    text = '{"sent": true}'

    def json(self):
        return {"sent": True}


def _post_through_client(monkeypatch, email):
    from src import email_ops

    monkeypatch.setattr(email_ops, "_auth_token", lambda: "account-key")
    monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "session-key-1")
    with patch("src.email_ops.requests.post", return_value=_Response()) as post, \
            patch("src.email_ops.gateway.parse_json_body", return_value={"sent": True}):
        email_ops.EmailClient(base_url="http://127.0.0.1:7878").send_owner("S", "B", None, [], email=email)
    return post.call_args


def test_client_hands_the_session_key_and_keeps_the_account_key(monkeypatch):
    call = _post_through_client(monkeypatch, email=False)
    headers = call.kwargs["headers"]
    assert headers["Authorization"] == "Bearer account-key"
    assert headers["X-CC-Session-Key"] == "session-key-1"
    assert "deliver" not in call.kwargs["json"]


def test_client_email_choice_sends_deliver_email(monkeypatch):
    call = _post_through_client(monkeypatch, email=True)
    assert call.kwargs["json"]["deliver"] == "email"
