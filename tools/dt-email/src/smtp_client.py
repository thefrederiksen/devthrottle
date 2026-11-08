"""The SMTP side of dt-email: hand one message to the account's SMTP server."""

from __future__ import annotations

import smtplib
from email.message import EmailMessage
from typing import List

from .config import Account
from .errors import DtEmailError

TIMEOUT_SECONDS = 60


def connect(account: Account) -> smtplib.SMTP:
    """Open the raw connection. Tests replace this function with one returning a fake."""
    if account.smtp_security == "ssl":
        return smtplib.SMTP_SSL(account.smtp_host, account.smtp_port, timeout=TIMEOUT_SECONDS)
    conn = smtplib.SMTP(account.smtp_host, account.smtp_port, timeout=TIMEOUT_SECONDS)
    conn.starttls()
    return conn


def send(account: Account, password: str, message: EmailMessage, recipients: List[str]) -> None:
    """Send `message` to exactly `recipients` (To, CC and BCC together). A Bcc header in the message is
    never transmitted: smtplib removes it. Any recipient the server refuses makes this an error."""
    if not recipients:
        raise DtEmailError("the message has no recipients. Pass --to, --cc or --bcc.", exit_code=2)
    conn = connect(account)
    try:
        try:
            conn.login(account.user, password)
        except smtplib.SMTPAuthenticationError as exc:
            raise DtEmailError(
                f"the SMTP server {account.smtp_host} refused the sign-in for {account.user}: {exc}. "
                f"Check the password held in cc-secrets entry '{account.secret_entry}'.") from exc
        envelope_from = _plain(message["From"])
        try:
            refused = conn.send_message(message, from_addr=envelope_from, to_addrs=recipients)
        except (smtplib.SMTPSenderRefused, smtplib.SMTPDataError) as exc:
            # Some servers refuse a foreign From at MAIL FROM, others (Zoho) only at DATA.
            raise DtEmailError(
                f"the SMTP server refused the message from {envelope_from}: {exc.smtp_code} {_reply(exc.smtp_error)}. "
                "A From address must be the account's own address or an alias the provider has set up for it. "
                "Nothing was sent.") from exc
        except smtplib.SMTPRecipientsRefused as exc:
            raise DtEmailError(
                f"the SMTP server refused every recipient: {', '.join(exc.recipients)}. Nothing was sent.") from exc
        if refused:
            raise DtEmailError(
                f"the message was sent, but the server refused {', '.join(sorted(refused))}. "
                "Only the other recipients got it.")
    except BaseException:
        # A server may already have closed the connection after refusing (Zoho does), so a QUIT could
        # fail and hide the real error. close() only drops the socket; the real error propagates.
        conn.close()
        raise
    conn.quit()


def _reply(text) -> str:
    return text.decode("utf-8", "replace") if isinstance(text, bytes) else str(text)


def _plain(header: str) -> str:
    from email.utils import parseaddr

    return parseaddr(header)[1]
