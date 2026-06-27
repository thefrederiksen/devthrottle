"""SMTP client for sending Gmail via App Password.

Uses Python's smtplib to connect to smtp.gmail.com with STARTTLS.
"""

import logging
import smtplib
from email.mime.text import MIMEText
from email.mime.multipart import MIMEMultipart
from email.mime.base import MIMEBase
from email import encoders
from pathlib import Path
from typing import Optional, List, Dict, Any

logger = logging.getLogger(__name__)

# Gmail SMTP server
SMTP_HOST = "smtp.gmail.com"
SMTP_PORT = 587


class SmtpClient:
    """Gmail SMTP client for sending email via App Password."""

    def __init__(self, email_address: str, app_password: str):
        """Initialize SMTP client.

        Args:
            email_address: Gmail address to send from.
            app_password: App password for authentication.
        """
        self.email_address = email_address
        self.app_password = app_password

    def send_message(
        self,
        to: str,
        subject: str,
        body: str,
        cc: Optional[str] = None,
        bcc: Optional[str] = None,
        html: bool = False,
        attachments: Optional[List[Path]] = None,
    ) -> Dict[str, Any]:
        """Send an email message via SMTP.

        Args:
            to: Recipient email address(es).
            subject: Email subject.
            body: Email body (plain text or HTML).
            cc: CC recipients.
            bcc: BCC recipients.
            html: If True, body is HTML.
            attachments: List of file paths to attach.

        Returns:
            Dict with a synthetic message ID.
        """
        if attachments:
            message = MIMEMultipart()
            message.attach(MIMEText(body, "html" if html else "plain"))

            for file_path in attachments:
                with open(file_path, "rb") as f:
                    part = MIMEBase("application", "octet-stream")
                    part.set_payload(f.read())
                    encoders.encode_base64(part)
                    part.add_header(
                        "Content-Disposition",
                        f"attachment; filename={file_path.name}",
                    )
                    message.attach(part)
        else:
            message = MIMEText(body, "html" if html else "plain")

        message["From"] = self.email_address
        message["To"] = to
        message["Subject"] = subject

        if cc:
            message["Cc"] = cc
        # NOTE: Bcc is intentionally NOT added to the MIME headers. smtplib
        # transmits the message verbatim, so a Bcc header would be delivered to
        # every To and Cc recipient, exposing the hidden recipients. Bcc is kept
        # only in the SMTP envelope (the recipient list passed to sendmail).

        # Build recipient list for SMTP envelope
        recipients = [addr.strip() for addr in to.split(",")]
        if cc:
            recipients.extend(addr.strip() for addr in cc.split(","))
        if bcc:
            recipients.extend(addr.strip() for addr in bcc.split(","))

        with smtplib.SMTP(SMTP_HOST, SMTP_PORT) as server:
            server.starttls()
            server.login(self.email_address, self.app_password)
            server.sendmail(self.email_address, recipients, message.as_string())

        return {"id": "sent-via-smtp"}
