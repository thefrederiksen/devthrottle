"""Gmail API wrapper for common operations."""

import base64
import logging
import re
from email.mime.text import MIMEText
from email.mime.multipart import MIMEMultipart
from email.mime.base import MIMEBase
from email import encoders
from email.utils import getaddresses, formataddr
from pathlib import Path
from typing import Optional, List, Dict, Any

from googleapiclient.discovery import build
from googleapiclient.errors import HttpError
from google.oauth2.credentials import Credentials

logger = logging.getLogger(__name__)


class GmailClient:
    """Gmail API client wrapper."""

    def __init__(self, credentials: Credentials):
        """Initialize Gmail client with credentials."""
        self.service = build("gmail", "v1", credentials=credentials)
        self.user_id = "me"

    def get_profile(self) -> Dict[str, Any]:
        """Get the authenticated user's Gmail profile."""
        return self.service.users().getProfile(userId=self.user_id).execute()

    def list_labels(self) -> List[Dict[str, Any]]:
        """List all labels/folders."""
        results = self.service.users().labels().list(userId=self.user_id).execute()
        return results.get("labels", [])

    def get_label(self, label_id: str) -> Dict[str, Any]:
        """
        Get detailed info for a specific label including message counts.

        Args:
            label_id: The label ID (e.g., "INBOX", "CATEGORY_UPDATES")

        Returns:
            Label dict with messagesTotal, messagesUnread, threadsTotal, threadsUnread.
        """
        return self.service.users().labels().get(
            userId=self.user_id, id=label_id
        ).execute()

    def list_messages(
        self,
        label_ids: Optional[List[str]] = None,
        query: Optional[str] = None,
        max_results: int = 10,
        include_spam_trash: bool = False,
    ) -> List[Dict[str, Any]]:
        """
        List messages matching criteria.

        Args:
            label_ids: Filter by label IDs (e.g., ["INBOX", "UNREAD"])
            query: Gmail search query string
            max_results: Maximum number of messages to return
            include_spam_trash: Include messages from SPAM and TRASH

        Returns:
            List of message dictionaries with id and threadId.
        """
        kwargs = {"userId": self.user_id, "maxResults": max_results}

        if label_ids:
            kwargs["labelIds"] = label_ids
        if query:
            kwargs["q"] = query
        if include_spam_trash:
            kwargs["includeSpamTrash"] = True

        results = self.service.users().messages().list(**kwargs).execute()
        return results.get("messages", [])

    def list_all_messages(
        self,
        label_ids: Optional[List[str]] = None,
        query: Optional[str] = None,
        max_results: Optional[int] = None,
    ) -> List[Dict[str, Any]]:
        """
        List ALL messages matching criteria, handling pagination.

        Args:
            label_ids: Filter by label IDs
            query: Gmail search query
            max_results: Optional cap on total results (None = all)

        Returns:
            List of all matching message dicts with id and threadId.
        """
        all_messages = []
        page_token = None

        while True:
            kwargs = {"userId": self.user_id, "maxResults": 500}
            if label_ids:
                kwargs["labelIds"] = label_ids
            if query:
                kwargs["q"] = query
            if page_token:
                kwargs["pageToken"] = page_token

            results = self.service.users().messages().list(**kwargs).execute()
            messages = results.get("messages", [])
            all_messages.extend(messages)

            # Check if we've hit optional cap
            if max_results and len(all_messages) >= max_results:
                return all_messages[:max_results]

            # Check for more pages
            page_token = results.get("nextPageToken")
            if not page_token:
                break

        return all_messages

    def get_message(
        self, message_id: str, format: str = "full"
    ) -> Dict[str, Any]:
        """
        Get a specific message.

        Args:
            message_id: The message ID
            format: "full", "metadata", "minimal", or "raw"

        Returns:
            Message dictionary with headers, body, etc.
        """
        return (
            self.service.users()
            .messages()
            .get(userId=self.user_id, id=message_id, format=format)
            .execute()
        )

    def get_message_details(self, message_id: str) -> Dict[str, Any]:
        """Get message with parsed headers and body."""
        msg = self.get_message(message_id)

        # Parse headers
        headers = {}
        payload = msg.get("payload", {})
        for header in payload.get("headers", []):
            name = header.get("name", "").lower()
            if name in ["from", "to", "subject", "date", "cc", "bcc", "message-id", "references"]:
                headers[name] = header.get("value", "")

        # Get body
        body = self._extract_body(payload)

        return {
            "id": msg.get("id"),
            "thread_id": msg.get("threadId"),
            "snippet": msg.get("snippet"),
            "labels": msg.get("labelIds", []),
            "headers": headers,
            "body": body,
            "internal_date": msg.get("internalDate"),
        }

    def _extract_body(self, payload: Dict[str, Any]) -> str:
        """Extract plain text body from message payload."""
        body = ""

        # Check for simple body
        if "body" in payload and payload["body"].get("data"):
            body = self._decode_base64(payload["body"]["data"])
            return body

        # Check for multipart
        parts = payload.get("parts", [])
        for part in parts:
            mime_type = part.get("mimeType", "")
            if mime_type == "text/plain":
                if part.get("body", {}).get("data"):
                    body = self._decode_base64(part["body"]["data"])
                    break
            elif mime_type.startswith("multipart/"):
                # Recursively check nested parts
                body = self._extract_body(part)
                if body:
                    break

        # Fallback to HTML if no plain text
        if not body:
            for part in parts:
                if part.get("mimeType") == "text/html":
                    if part.get("body", {}).get("data"):
                        body = self._decode_base64(part["body"]["data"])
                        break

        return body

    def _decode_base64(self, data: str) -> str:
        """Decode base64url encoded data."""
        # Gmail uses URL-safe base64
        padding = 4 - len(data) % 4
        if padding != 4:
            data += "=" * padding
        return base64.urlsafe_b64decode(data).decode("utf-8", errors="replace")

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
        """
        Send an email message.

        Args:
            to: Recipient email address(es)
            subject: Email subject
            body: Email body (plain text or HTML)
            cc: CC recipients
            bcc: BCC recipients
            html: If True, body is HTML
            attachments: List of file paths to attach

        Returns:
            Sent message response.
        """
        if attachments:
            message = MIMEMultipart()
            message.attach(MIMEText(body, "html" if html else "plain"))

            for file_path in attachments:
                with open(file_path, "rb") as f:
                    part = MIMEBase("application", "octet-stream")
                    part.set_payload(f.read())
                    encoders.encode_base64(part)
                    # Pass the disposition type and filename as separate
                    # arguments so the email library performs RFC 2231
                    # parameter encoding. Building the whole header value as one
                    # pre-formatted string causes a non-ASCII filename to be
                    # RFC 2047-encoded in full (mangling the "attachment" type)
                    # and leaves a spaced ASCII filename unquoted.
                    part.add_header(
                        "Content-Disposition",
                        "attachment",
                        filename=file_path.name,
                    )
                    message.attach(part)
        else:
            message = MIMEText(body, "html" if html else "plain")

        message["to"] = _encode_address_header(to)
        message["subject"] = subject

        if cc:
            message["cc"] = _encode_address_header(cc)
        if bcc:
            message["bcc"] = _encode_address_header(bcc)

        raw = base64.urlsafe_b64encode(message.as_bytes()).decode("utf-8")
        return (
            self.service.users()
            .messages()
            .send(userId=self.user_id, body={"raw": raw})
            .execute()
        )

    def create_draft(
        self,
        to: str,
        subject: str,
        body: str,
        cc: Optional[str] = None,
        html: bool = False,
    ) -> Dict[str, Any]:
        """
        Create a draft email.

        Args:
            to: Recipient email address(es)
            subject: Email subject
            body: Email body
            cc: CC recipients
            html: If True, body is HTML

        Returns:
            Created draft response.
        """
        message = MIMEText(body, "html" if html else "plain")
        message["to"] = _encode_address_header(to)
        message["subject"] = subject

        if cc:
            message["cc"] = _encode_address_header(cc)

        raw = base64.urlsafe_b64encode(message.as_bytes()).decode("utf-8")
        return (
            self.service.users()
            .drafts()
            .create(userId=self.user_id, body={"message": {"raw": raw}})
            .execute()
        )

    def create_reply_draft(
        self,
        message_id: str,
        body: str,
        reply_all: bool = False,
        send: bool = False,
        html: bool = False,
    ) -> Dict[str, Any]:
        """
        Create a reply to an existing message (draft or send immediately).

        Args:
            message_id: The message ID to reply to
            body: Reply body text
            reply_all: If True, reply to all recipients
            send: If True, send immediately instead of saving as draft
            html: If True, body is HTML

        Returns:
            Created draft or sent message response.
        """
        # Get original message details
        original = self.get_message_details(message_id)
        headers = original.get("headers", {})
        thread_id = original.get("thread_id")

        # Build reply headers
        original_from = headers.get("from", "")
        original_subject = headers.get("subject", "")
        original_message_id = headers.get("message-id", "")
        original_references = headers.get("references", "")

        # Reply goes to original sender
        reply_to = original_from

        # Handle reply-all
        if reply_all:
            original_to = headers.get("to", "")
            original_cc = headers.get("cc", "")
            # Combine sender + To + Cc, drop the user's own address, dedupe.
            own_address = self.get_profile().get("emailAddress", "")
            reply_to = _build_reply_all_recipients(
                own_address, original_from, original_to, original_cc
            )

        # Build subject with Re: prefix if not already present
        if original_subject.lower().startswith("re:"):
            reply_subject = original_subject
        else:
            reply_subject = f"Re: {original_subject}"

        # Build References header (chain of message IDs)
        if original_references:
            references = f"{original_references} {original_message_id}"
        else:
            references = original_message_id

        # Create the reply message
        content_type = "html" if html else "plain"
        message = MIMEText(body, content_type)
        message["to"] = _encode_address_header(reply_to)
        message["subject"] = reply_subject

        if original_message_id:
            message["In-Reply-To"] = original_message_id
            message["References"] = references

        raw = base64.urlsafe_b64encode(message.as_bytes()).decode("utf-8")

        if send:
            # Send immediately in the same thread
            send_body = {"raw": raw, "threadId": thread_id}
            return (
                self.service.users()
                .messages()
                .send(userId=self.user_id, body=send_body)
                .execute()
            )
        else:
            # Create draft in the same thread
            draft_body = {"message": {"raw": raw, "threadId": thread_id}}
            return (
                self.service.users()
                .drafts()
                .create(userId=self.user_id, body=draft_body)
                .execute()
            )

    def list_drafts(self, max_results: int = 10) -> List[Dict[str, Any]]:
        """List drafts."""
        results = (
            self.service.users()
            .drafts()
            .list(userId=self.user_id, maxResults=max_results)
            .execute()
        )
        return results.get("drafts", [])

    def delete_message(self, message_id: str, permanent: bool = False) -> None:
        """
        Delete a message.

        Args:
            message_id: The message ID
            permanent: If True, permanently delete. Otherwise, move to trash.
        """
        if permanent:
            self.service.users().messages().delete(
                userId=self.user_id, id=message_id
            ).execute()
        else:
            self.service.users().messages().trash(
                userId=self.user_id, id=message_id
            ).execute()

    def untrash_message(self, message_id: str) -> Dict[str, Any]:
        """Restore a message from trash."""
        return (
            self.service.users()
            .messages()
            .untrash(userId=self.user_id, id=message_id)
            .execute()
        )

    def mark_as_read(self, message_id: str) -> Dict[str, Any]:
        """Mark a message as read."""
        return (
            self.service.users()
            .messages()
            .modify(
                userId=self.user_id,
                id=message_id,
                body={"removeLabelIds": ["UNREAD"]},
            )
            .execute()
        )

    def mark_as_unread(self, message_id: str) -> Dict[str, Any]:
        """Mark a message as unread."""
        return (
            self.service.users()
            .messages()
            .modify(
                userId=self.user_id,
                id=message_id,
                body={"addLabelIds": ["UNREAD"]},
            )
            .execute()
        )

    def archive_message(self, message_id: str) -> Dict[str, Any]:
        """
        Archive a message by removing the INBOX label.

        The message remains in All Mail and any other labels it has.
        """
        return (
            self.service.users()
            .messages()
            .modify(
                userId=self.user_id,
                id=message_id,
                body={"removeLabelIds": ["INBOX"]},
            )
            .execute()
        )

    def batch_archive_messages(self, message_ids: List[str]) -> int:
        """
        Archive multiple messages at once using batchModify.
        Handles chunking for large lists (API limit: 1000/batch).

        Args:
            message_ids: List of message IDs to archive

        Returns:
            Number of messages archived.
        """
        if not message_ids:
            return 0

        archived = 0
        # Process in chunks of 1000 (Gmail API limit)
        for i in range(0, len(message_ids), 1000):
            chunk = message_ids[i:i + 1000]
            self.service.users().messages().batchModify(
                userId=self.user_id,
                body={
                    "ids": chunk,
                    "removeLabelIds": ["INBOX"],
                },
            ).execute()
            archived += len(chunk)

        return archived

    def modify_labels(
        self,
        message_id: str,
        add_labels: Optional[List[str]] = None,
        remove_labels: Optional[List[str]] = None,
    ) -> Dict[str, Any]:
        """
        Modify labels on a message.

        Args:
            message_id: The message ID
            add_labels: Label IDs to add
            remove_labels: Label IDs to remove
        """
        body = {}
        if add_labels:
            body["addLabelIds"] = add_labels
        if remove_labels:
            body["removeLabelIds"] = remove_labels

        return (
            self.service.users()
            .messages()
            .modify(
                userId=self.user_id,
                id=message_id,
                body=body,
            )
            .execute()
        )

    def search(
        self,
        query: str,
        max_results: int = 10,
        include_spam_trash: bool = False,
    ) -> List[Dict[str, Any]]:
        """
        Search messages using Gmail query syntax.

        Args:
            query: Gmail search query (e.g., "from:foo@bar.com subject:hello")
            max_results: Maximum results to return
            include_spam_trash: Include messages from SPAM and TRASH

        Returns:
            List of matching messages.
        """
        return self.list_messages(
            query=query,
            max_results=max_results,
            include_spam_trash=include_spam_trash,
        )

    def count_messages(
        self,
        label_ids: Optional[List[str]] = None,
        query: Optional[str] = None,
    ) -> int:
        """
        Get accurate count of messages matching criteria.

        Paginates through all results to get an exact count.
        For large mailboxes this may take a few seconds.

        Args:
            label_ids: Filter by label IDs (e.g., ["INBOX", "UNREAD"])
            query: Gmail search query string

        Returns:
            Exact count of matching messages.
        """
        total = 0
        page_token = None

        while True:
            kwargs = {"userId": self.user_id, "maxResults": 500}
            if label_ids:
                kwargs["labelIds"] = label_ids
            if query:
                kwargs["q"] = query
            if page_token:
                kwargs["pageToken"] = page_token

            results = self.service.users().messages().list(**kwargs).execute()
            messages = results.get("messages", [])
            total += len(messages)

            page_token = results.get("nextPageToken")
            if not page_token:
                break

        return total

    def get_mailbox_stats(self) -> Dict[str, Any]:
        """
        Get comprehensive mailbox statistics.

        Returns a dictionary with:
        - profile: email, totals
        - system_labels: INBOX, SENT, DRAFT, SPAM, TRASH, STARRED, IMPORTANT
        - categories: UPDATES, PROMOTIONS, SOCIAL, FORUMS
        - user_labels: all custom labels with counts
        """
        profile = self.get_profile()

        # System labels to fetch
        system_label_ids = [
            "INBOX", "SENT", "DRAFT", "SPAM", "TRASH",
            "STARRED", "IMPORTANT", "UNREAD"
        ]

        # Category labels
        category_ids = [
            "CATEGORY_PERSONAL", "CATEGORY_UPDATES",
            "CATEGORY_PROMOTIONS", "CATEGORY_SOCIAL", "CATEGORY_FORUMS"
        ]

        system_labels = {}
        for label_id in system_label_ids:
            try:
                label = self.get_label(label_id)
                system_labels[label_id] = {
                    "total": label.get("messagesTotal", 0),
                    "unread": label.get("messagesUnread", 0),
                    "threads_total": label.get("threadsTotal", 0),
                    "threads_unread": label.get("threadsUnread", 0),
                }
            except HttpError as e:
                logger.debug(f"Label {label_id} not accessible: {e}")
            except ValueError as e:
                logger.debug(f"Invalid label {label_id}: {e}")

        categories = {}
        for label_id in category_ids:
            try:
                label = self.get_label(label_id)
                # Use friendly name
                name = label_id.replace("CATEGORY_", "").title()
                categories[name] = {
                    "id": label_id,
                    "total": label.get("messagesTotal", 0),
                    "unread": label.get("messagesUnread", 0),
                }
            except HttpError as e:
                logger.debug(f"Category {label_id} not accessible: {e}")
            except ValueError as e:
                logger.debug(f"Invalid category {label_id}: {e}")

        # Get user labels
        all_labels = self.list_labels()
        user_labels = []
        for label in all_labels:
            if label.get("type") != "system":
                try:
                    details = self.get_label(label.get("id"))
                    user_labels.append({
                        "id": label.get("id"),
                        "name": label.get("name"),
                        "total": details.get("messagesTotal", 0),
                        "unread": details.get("messagesUnread", 0),
                    })
                except HttpError as e:
                    logger.debug(f"User label {label.get('id')} not accessible: {e}")
                except ValueError as e:
                    logger.debug(f"Invalid user label {label.get('id')}: {e}")

        # Sort user labels by unread count (descending)
        user_labels.sort(key=lambda x: x.get("unread", 0), reverse=True)

        return {
            "profile": {
                "email": profile.get("emailAddress"),
                "messages_total": profile.get("messagesTotal", 0),
                "threads_total": profile.get("threadsTotal", 0),
            },
            "system_labels": system_labels,
            "categories": categories,
            "user_labels": user_labels,
        }

    def create_label(self, name: str) -> Dict[str, Any]:
        """
        Create a new label.

        Args:
            name: The label name

        Returns:
            Created label response with id and name.
        """
        label_body = {
            "name": name,
            "labelListVisibility": "labelShow",
            "messageListVisibility": "show",
        }
        return (
            self.service.users()
            .labels()
            .create(userId=self.user_id, body=label_body)
            .execute()
        )

    def get_label_by_name(self, name: str) -> Optional[Dict[str, Any]]:
        """
        Get a label by its name.

        Args:
            name: The label name to search for

        Returns:
            Label dict if found, None otherwise.
        """
        labels = self.list_labels()
        for label in labels:
            if label.get("name", "").lower() == name.lower():
                return label
        return None

    def get_or_create_label(self, name: str) -> Dict[str, Any]:
        """
        Get an existing label or create it if it doesn't exist.

        Args:
            name: The label name

        Returns:
            Label dict with id and name.
        """
        existing = self.get_label_by_name(name)
        if existing:
            return existing
        return self.create_label(name)

    def get_all_recipients(self) -> Dict[str, Dict[str, Any]]:
        """
        Extract all unique recipients from sent emails.

        Paginates through all SENT messages (metadata only), batch-fetches
        To/Cc/Bcc headers in groups of 50, deduplicates by email address,
        and filters out noreply/system addresses.

        Returns:
            Dict keyed by email address, each value is:
            {"name": str, "sent_count": int}
        """
        logger.info("Fetching all sent message IDs...")

        # Step 1: Get all sent message IDs
        all_ids = []
        page_token = None
        while True:
            kwargs = {"userId": self.user_id, "labelIds": ["SENT"], "maxResults": 500}
            if page_token:
                kwargs["pageToken"] = page_token
            results = self.service.users().messages().list(**kwargs).execute()
            messages = results.get("messages", [])
            all_ids.extend([m["id"] for m in messages])
            page_token = results.get("nextPageToken")
            if not page_token:
                break

        logger.info("Total sent messages: %d", len(all_ids))

        # Step 2: Batch-fetch To/Cc/Bcc headers in groups of 50
        recipients = {}  # email -> {"name": str, "sent_count": int}
        batch_size = 50

        for i in range(0, len(all_ids), batch_size):
            batch_ids = all_ids[i:i + batch_size]
            batch = self.service.new_batch_http_request()

            def make_callback(msg_id):
                def callback(request_id, response, exception):
                    if exception:
                        return
                    headers = response.get("payload", {}).get("headers", [])
                    for h in headers:
                        header_name = h.get("name", "").lower()
                        if header_name in ("to", "cc", "bcc"):
                            for contact in _parse_email_addresses(h.get("value", "")):
                                email = contact["email"]
                                if email in recipients:
                                    recipients[email]["sent_count"] += 1
                                    # Keep the first non-empty name
                                    if contact["name"] and not recipients[email]["name"]:
                                        recipients[email]["name"] = contact["name"]
                                else:
                                    recipients[email] = {
                                        "name": contact["name"],
                                        "sent_count": 1,
                                    }
                return callback

            for msg_id in batch_ids:
                batch.add(
                    self.service.users().messages().get(
                        userId=self.user_id,
                        id=msg_id,
                        format="metadata",
                        metadataHeaders=["To", "Cc", "Bcc"],
                    ),
                    callback=make_callback(msg_id),
                )

            batch.execute()

        # Step 3: Filter out noreply/system addresses
        skip_patterns = [
            "noreply", "no-reply", "notifications@", "mailer-daemon",
            "donotreply", "do-not-reply", "unsubscribe",
        ]

        filtered = {}
        for email, data in recipients.items():
            if any(p in email for p in skip_patterns):
                continue
            filtered[email] = data

        logger.info("Unique recipients after filtering: %d", len(filtered))
        return filtered


def _encode_address_header(value: str) -> str:
    """Re-emit a To/Cc/Bcc header value with only the display-name phrases
    RFC 2047-encoded, leaving each addr-spec intact.

    Assigning a flattened multi-address string straight to a message header
    causes the entire value (addresses included) to be encoded as one blob when
    any display name is non-ASCII. On the Gmail API send path the raw message IS
    the envelope, so that blob corrupts recipient parsing and the send can fail
    or misroute. getaddresses parses the addresses; formataddr re-emits each one
    encoding only the phrase, producing pure-ASCII output.
    """
    if not value:
        return value
    parts = []
    for name, addr in getaddresses([value]):
        if not name and not addr:
            continue
        parts.append(formataddr((name, addr)))
    return ", ".join(parts)


def _build_reply_all_recipients(
    own_address: str,
    original_from: str,
    original_to: str,
    original_cc: str,
) -> str:
    """Build the reply-all recipient list.

    Combines the original sender, To, and Cc recipients, drops the user's own
    address, and de-duplicates by address while preserving order.
    """
    own = (own_address or "").strip().lower()
    seen = set()
    result = []
    for header in (original_from, original_to, original_cc):
        if not header:
            continue
        for name, addr in getaddresses([header]):
            if not addr:
                continue
            low = addr.lower()
            if low == own or low in seen:
                continue
            seen.add(low)
            result.append(formataddr((name, addr)) if name else addr)
    return ", ".join(result)


def _parse_email_addresses(header_value: str) -> List[Dict[str, str]]:
    """Parse email addresses from a To/Cc/Bcc header value.

    Handles formats like:
      - "Name <email@example.com>"
      - "email@example.com"
      - "Name <email@example.com>, Other <other@example.com>"
    """
    if not header_value:
        return []

    contacts = []
    # Split on commas, but not commas inside quotes
    parts = re.split(r',(?=(?:[^"]*"[^"]*")*[^"]*$)', header_value)

    for part in parts:
        part = part.strip()
        if not part:
            continue

        # Try "Name <email>" format
        match = re.match(r'^"?([^"<]*?)"?\s*<([^>]+)>', part)
        if match:
            name = match.group(1).strip().strip('"')
            email = match.group(2).strip().lower()
            contacts.append({"name": name, "email": email})
        else:
            # Plain email
            email = part.strip().strip("<>").lower()
            if "@" in email:
                contacts.append({"name": "", "email": email})

    return contacts
