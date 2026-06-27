"""Outlook API wrapper using O365 library."""

import logging
import os
from datetime import datetime, timedelta, timezone
from typing import Optional, List


def _as_aware(dt: Optional[datetime]) -> Optional[datetime]:
    """Attach the local timezone to a naive datetime.

    The CLI builds naive datetimes (datetime.now(), strptime), which O365 and
    Microsoft Graph would otherwise interpret as UTC, shifting times by the
    local offset. A naive datetime's .astimezone() is treated as local time and
    returned as an aware local datetime.
    """
    if dt is None:
        return None
    if dt.tzinfo is None:
        return dt.astimezone()
    return dt


def _to_utc(dt: datetime) -> datetime:
    """Convert a datetime to UTC, treating naive values as local time."""
    if dt.tzinfo is None:
        dt = dt.astimezone()
    return dt.astimezone(timezone.utc)

from O365 import Account

logger = logging.getLogger(__name__)


class OutlookClient:
    """Client for Outlook operations using O365 library."""

    GRAPH_API_BASE = 'https://graph.microsoft.com/v1.0'

    def __init__(self, account: Account):
        """
        Initialize the client with an authenticated account.

        Args:
            account: Authenticated O365 Account instance
        """
        self.account = account

    # =========================================================================
    # Profile
    # =========================================================================

    def get_profile(self) -> dict:
        """
        Get the authenticated user's profile.

        Returns:
            Dict with the user's real email address.
        """
        # Query Microsoft Graph /me directly. mailbox.main_resource is the
        # resource path (often the literal 'me'), not the mailbox address.
        url = f'{self.GRAPH_API_BASE}/me'
        con = self.account.connection
        response = con.get(url)

        if not response:
            raise ConnectionError("Failed to get profile (no response from Graph API)")

        data = response.json()
        email = data.get('mail') or data.get('userPrincipalName')
        if not email:
            raise ConnectionError("Graph /me returned no email address")

        return {'emailAddress': email}

    # =========================================================================
    # Email Operations
    # =========================================================================

    def list_messages(self, folder: str = 'inbox', limit: int = 10,
                      unread_only: bool = False) -> list:
        """
        List messages from a folder.

        Args:
            folder: Folder name (inbox, sent, drafts, deleted, junk)
            limit: Maximum number of messages to return
            unread_only: Only return unread messages

        Returns:
            List of message dicts
        """
        mailbox = self.account.mailbox()

        # Get the specified folder
        folder_lower = folder.lower()
        if folder_lower == 'inbox':
            mail_folder = mailbox.inbox_folder()
        elif folder_lower == 'sent':
            mail_folder = mailbox.sent_folder()
        elif folder_lower == 'drafts':
            mail_folder = mailbox.drafts_folder()
        elif folder_lower == 'deleted':
            mail_folder = mailbox.deleted_folder()
        elif folder_lower == 'junk':
            mail_folder = mailbox.junk_folder()
        else:
            mail_folder = mailbox.get_folder(folder_name=folder)

        if not mail_folder:
            raise ValueError(f"Folder not found: {folder}")

        # Build query if needed
        if unread_only:
            query = mail_folder.new_query().on_attribute('isRead').equals(False)
            messages = mail_folder.get_messages(query=query, limit=limit)
        else:
            messages = mail_folder.get_messages(limit=limit)

        result = []
        for msg in messages:
            result.append(self._format_message(msg))

        return result

    def get_message(self, message_id: str) -> Optional[dict]:
        """
        Get a single message by ID.

        Args:
            message_id: Message ID

        Returns:
            Message dict or None if not found
        """
        mailbox = self.account.mailbox()
        message = mailbox.get_message(object_id=message_id)
        if message:
            return self._format_message(message, include_body=True)
        return None

    def search_messages(self, query: str, folder: str = 'inbox',
                        limit: int = 10) -> list:
        """
        Search messages.

        Args:
            query: Search query string
            folder: Folder to search in
            limit: Maximum results

        Returns:
            List of matching message dicts
        """
        mailbox = self.account.mailbox()

        # Get the folder
        folder_lower = folder.lower()
        if folder_lower == 'inbox':
            mail_folder = mailbox.inbox_folder()
        elif folder_lower == 'sent':
            mail_folder = mailbox.sent_folder()
        else:
            mail_folder = mailbox.get_folder(folder_name=folder)

        if not mail_folder:
            raise ValueError(f"Folder not found: {folder}")

        # Search using OData filter
        q = mail_folder.new_query().search(query)
        messages = mail_folder.get_messages(query=q, limit=limit)

        result = []
        for msg in messages:
            result.append(self._format_message(msg))

        return result

    def send_message(self, to: List[str], subject: str, body: str,
                     cc: Optional[List[str]] = None, bcc: Optional[List[str]] = None,
                     attachments: Optional[List[str]] = None, importance: str = 'normal',
                     html: bool = False) -> dict:
        """
        Send an email.

        Args:
            to: List of recipient emails
            subject: Email subject
            body: Email body
            cc: List of CC recipients
            bcc: List of BCC recipients
            attachments: List of file paths to attach
            importance: Email importance (low, normal, high)
            html: Whether body is HTML

        Returns:
            Dict with send result
        """
        mailbox = self.account.mailbox()
        message = mailbox.new_message()

        # Set recipients
        message.to.add(to)

        if cc:
            message.cc.add(cc)

        if bcc:
            message.bcc.add(bcc)

        # Set message properties
        message.subject = subject
        message.body = body
        # O365 defaults the body type to HTML, so plain text would otherwise be
        # sent as HTML (newlines collapse). Set the type explicitly to honor --html.
        message.body_type = 'HTML' if html else 'text'
        message.importance = importance

        # Add attachments
        if attachments:
            for file_path in attachments:
                if os.path.exists(file_path):
                    message.attachments.add(file_path)
                else:
                    raise FileNotFoundError(f"Attachment not found: {file_path}")

        message.send()
        return {'status': 'sent', 'to': to, 'subject': subject}

    def create_draft(self, to: List[str], subject: str, body: str,
                     cc: Optional[List[str]] = None, html: bool = False) -> dict:
        """
        Create a draft email.

        Args:
            to: List of recipient emails
            subject: Email subject
            body: Email body
            cc: List of CC recipients
            html: Whether body is HTML

        Returns:
            Dict with draft info
        """
        mailbox = self.account.mailbox()
        message = mailbox.new_message()

        message.to.add(to)
        if cc:
            message.cc.add(cc)

        message.subject = subject
        message.body = body
        # Honor --html; O365 defaults the body type to HTML otherwise.
        message.body_type = 'HTML' if html else 'text'

        # Save as draft
        message.save_draft()

        return {
            'id': message.object_id,
            'status': 'draft',
            'to': to,
            'subject': subject
        }

    def delete_message(self, message_id: str, permanent: bool = False) -> bool:
        """
        Delete a message.

        Args:
            message_id: Message ID
            permanent: Permanently delete (not just move to trash)

        Returns:
            True if deleted
        """
        mailbox = self.account.mailbox()
        message = mailbox.get_message(object_id=message_id)

        if not message:
            raise ValueError(f"Message not found: {message_id}")

        message.delete()
        return True

    def move_message(self, message_id: str, target_folder: str) -> bool:
        """
        Move a message to another folder.

        Args:
            message_id: Message ID
            target_folder: Target folder name

        Returns:
            True if moved
        """
        mailbox = self.account.mailbox()
        message = mailbox.get_message(object_id=message_id)

        if not message:
            raise ValueError(f"Message not found: {message_id}")

        # Get target folder
        folder_lower = target_folder.lower()
        if folder_lower == 'inbox':
            target = mailbox.inbox_folder()
        elif folder_lower == 'deleted':
            target = mailbox.deleted_folder()
        elif folder_lower == 'drafts':
            target = mailbox.drafts_folder()
        elif folder_lower == 'junk':
            target = mailbox.junk_folder()
        else:
            target = mailbox.get_folder(folder_name=target_folder)

        if not target:
            raise ValueError(f"Target folder not found: {target_folder}")

        message.move(target)
        return True

    def resolve_folder(self, identifier: str):
        """
        Resolve a folder by ID, full path, or path suffix.

        Matching order (each step is exact, no fuzzy fallback):
          1. Exact folder ID.
          2. Exact full display-path (case-insensitive), e.g.
             "Top of Information Store/Inbox/Customers/Nufarm".
          3. Path-suffix match on the '/'-separated segments, so
             "Customers/Nufarm" or just "Nufarm" resolve the same folder.

        Raises ValueError if nothing matches, or if a suffix matches more
        than one folder (the candidate paths are listed so the caller can
        disambiguate with a longer path).
        """
        mailbox = self.account.mailbox()
        all_folders = self.list_folders()

        # 1. Exact folder ID
        for f in all_folders:
            if f['id'] == identifier:
                return mailbox.get_folder(folder_id=identifier)

        target_segs = [s.lower() for s in identifier.split('/') if s]

        # 2. Exact full path (case-insensitive)
        for f in all_folders:
            if f['display_name'].lower() == identifier.lower():
                return mailbox.get_folder(folder_id=f['id'])

        # 3. Path-suffix match on segments
        matches = []
        for f in all_folders:
            segs = [s.lower() for s in f['display_name'].split('/') if s]
            if segs[-len(target_segs):] == target_segs:
                matches.append(f)

        if len(matches) == 1:
            return mailbox.get_folder(folder_id=matches[0]['id'])
        if len(matches) > 1:
            paths = '\n  '.join(m['display_name'] for m in matches)
            raise ValueError(
                f"Folder path '{identifier}' is ambiguous, matches:\n  {paths}\n"
                "Provide a longer path to disambiguate."
            )
        raise ValueError(f"Folder not found: {identifier}")

    def move_message_to(self, message_id: str, target: str) -> str:
        """
        Move a message to any folder, resolved by ID, full path, or suffix.

        Returns the resolved folder's display name.
        """
        mailbox = self.account.mailbox()
        message = mailbox.get_message(object_id=message_id)
        if not message:
            raise ValueError(f"Message not found: {message_id}")

        folder = self.resolve_folder(target)
        message.move(folder)
        return folder.name

    def mark_as_read(self, message_id: str, is_read: bool = True) -> bool:
        """
        Mark a message as read or unread.

        Args:
            message_id: Message ID
            is_read: Mark as read (True) or unread (False)

        Returns:
            True if updated
        """
        mailbox = self.account.mailbox()
        message = mailbox.get_message(object_id=message_id)

        if not message:
            raise ValueError(f"Message not found: {message_id}")

        if is_read:
            message.mark_as_read()
        else:
            message.mark_as_unread()

        return True

    def get_all_recipients(self) -> dict:
        """
        Extract all unique recipients from sent emails.

        Iterates through all messages in the Sent folder, collects
        To and Cc addresses, deduplicates by email, and filters out
        noreply/system addresses.

        Returns:
            Dict keyed by email address, each value is:
            {"name": str, "sent_count": int}
        """
        logger.info("Fetching all sent messages for recipient extraction...")

        mailbox = self.account.mailbox()
        sent_folder = mailbox.sent_folder()

        # Fetch all sent messages (O365 handles pagination internally when limit > 999)
        messages = sent_folder.get_messages(limit=10000)

        recipients = {}  # email -> {"name": str, "sent_count": int}
        processed = 0

        for msg in messages:
            # Collect To recipients
            if msg.to:
                for recipient in msg.to:
                    email = recipient.address.lower() if recipient.address else None
                    if not email:
                        continue
                    name = recipient.name or ""
                    if email in recipients:
                        recipients[email]["sent_count"] += 1
                        if name and not recipients[email]["name"]:
                            recipients[email]["name"] = name
                    else:
                        recipients[email] = {"name": name, "sent_count": 1}

            # Collect Cc recipients
            if msg.cc:
                for recipient in msg.cc:
                    email = recipient.address.lower() if recipient.address else None
                    if not email:
                        continue
                    name = recipient.name or ""
                    if email in recipients:
                        recipients[email]["sent_count"] += 1
                        if name and not recipients[email]["name"]:
                            recipients[email]["name"] = name
                    else:
                        recipients[email] = {"name": name, "sent_count": 1}

            processed += 1
            if processed % 500 == 0:
                logger.info("Processed %d messages, %d unique recipients so far",
                            processed, len(recipients))

        logger.info("Total sent messages processed: %d", processed)

        # Filter out noreply/system addresses
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

    # =========================================================================
    # Folder Operations
    # =========================================================================

    def list_folders(self) -> list:
        """
        List all mail folders.

        Returns:
            List of folder dicts
        """
        mailbox = self.account.mailbox()
        folders = []

        def get_folders_recursive(parent_folder, prefix=''):
            """Recursively get all folders."""
            try:
                child_folders = parent_folder.get_folders()
                for folder in child_folders:
                    folder_info = {
                        'id': folder.folder_id,
                        'name': folder.name,
                        'display_name': f'{prefix}{folder.name}',
                        'total_count': folder.total_items_count,
                        'unread_count': folder.unread_items_count
                    }
                    folders.append(folder_info)
                    # Get child folders
                    get_folders_recursive(folder, prefix=f'{prefix}{folder.name}/')
            except AttributeError as e:
                logger.debug(f"Folder does not support subfolders: {e}")
            except ValueError as e:
                logger.debug(f"Access denied or invalid folder: {e}")

        # Start from root
        root_folder = mailbox.get_folder(folder_id='root')
        if root_folder:
            get_folders_recursive(root_folder)

        return folders

    def create_folder(self, name: str, parent_folder: str = None) -> dict:
        """
        Create a new mail folder.

        Args:
            name: Folder name
            parent_folder: Parent folder name (optional)

        Returns:
            Created folder info
        """
        mailbox = self.account.mailbox()

        if parent_folder:
            parent = self.resolve_folder(parent_folder)
            new_folder = parent.create_child_folder(name)
        else:
            inbox = mailbox.inbox_folder()
            new_folder = inbox.create_child_folder(name)

        if new_folder:
            return {
                'id': new_folder.folder_id,
                'name': new_folder.name,
            }

        raise Exception("Failed to create folder")

    # =========================================================================
    # Calendar Operations
    # =========================================================================

    def list_calendars(self) -> list:
        """
        List all calendars.

        Returns:
            List of calendar dicts
        """
        schedule = self.account.schedule()
        calendars = schedule.list_calendars()

        result = []
        for cal in calendars:
            result.append({
                'id': cal.calendar_id,
                'name': cal.name,
            })

        return result

    def get_events(self, days_ahead: int = 7, calendar_name: str = None,
                   start_date: datetime = None, end_date: datetime = None) -> list:
        """
        Get calendar events.

        Args:
            days_ahead: Number of days to span from start_date
            calendar_name: Specific calendar name (optional)
            start_date: Start of date range (default: now)
            end_date: End of date range (default: start_date + days_ahead)

        Returns:
            List of event dicts
        """
        schedule = self.account.schedule()

        if calendar_name:
            calendars = schedule.list_calendars()
            calendar = None
            for cal in calendars:
                if cal.name.lower() == calendar_name.lower():
                    calendar = cal
                    break
            if not calendar:
                raise ValueError(f"Calendar not found: {calendar_name}")
        else:
            calendar = schedule.get_default_calendar()

        start = _as_aware(start_date) if start_date else _as_aware(datetime.now())
        end = _as_aware(end_date) if end_date else start + timedelta(days=days_ahead)

        # Paginate with batch so the result is not silently capped. O365 returns
        # a generator that follows @odata.nextLink across all pages when a batch
        # size is given and limit is None.
        events = calendar.get_events(
            limit=None,
            batch=100,
            include_recurring=True,
            start_recurring=start,
            end_recurring=end,
        )

        result = []
        for event in events:
            result.append(self._format_event(event))

        return result

    def create_event(self, subject: str, start_time: datetime,
                     duration_minutes: int = 60, attendees: list = None,
                     location: str = None, body: str = None,
                     all_day: bool = False) -> dict:
        """
        Create a calendar event.

        Args:
            subject: Event subject
            start_time: Event start time
            duration_minutes: Duration in minutes
            attendees: List of attendee emails
            location: Event location
            body: Event description
            all_day: Create as all-day event

        Returns:
            Created event info
        """
        schedule = self.account.schedule()
        calendar = schedule.get_default_calendar()

        event = calendar.new_event()
        event.subject = subject
        # Attach the local timezone so the event is not shifted by the local
        # offset (O365 treats naive datetimes as UTC).
        event.start = _as_aware(start_time)
        event.end = _as_aware(start_time + timedelta(minutes=duration_minutes))

        if all_day:
            event.is_all_day = True

        if location:
            event.location = location

        if body:
            event.body = body

        if attendees:
            for attendee_email in attendees:
                event.attendees.add(attendee_email)

        event.save()

        return {
            'id': event.object_id,
            'subject': subject,
            'start': start_time.isoformat(),
            'duration': duration_minutes,
        }

    # =========================================================================
    # Helper Methods
    # =========================================================================

    # =========================================================================
    # Reply/Forward Operations
    # =========================================================================

    def reply_message(self, message_id: str, body: str, reply_all: bool = False,
                      send: bool = False, html: bool = False) -> dict:
        """
        Create a draft reply (or send immediately) to a message.

        Args:
            message_id: Message ID to reply to
            body: Reply body text
            reply_all: If True, reply to all recipients
            send: If True, send immediately instead of saving as draft
            html: If True, body is HTML

        Returns:
            Dict with reply details
        """
        mailbox = self.account.mailbox()
        message = mailbox.get_message(object_id=message_id)

        if not message:
            raise ValueError(f"Message not found: {message_id}")

        if reply_all:
            reply = message.reply_all()
        else:
            reply = message.reply()

        reply.body = body
        if html:
            reply.body_type = 'HTML'

        if send:
            reply.send()
        else:
            reply.save_draft()

        status = 'sent' if send else 'draft'

        return {
            'status': status,
            'id': reply.object_id if not send else None,
            'subject': reply.subject,
            'to': [r.address for r in reply.to],
            'reply_to': message_id,
            'reply_all': reply_all
        }

    def forward_message(self, message_id: str, to: List[str], body: str = None) -> dict:
        """
        Forward a message.

        Args:
            message_id: Message ID to forward
            to: List of recipient emails
            body: Optional additional message

        Returns:
            Dict with forward status
        """
        mailbox = self.account.mailbox()
        message = mailbox.get_message(object_id=message_id)

        if not message:
            raise ValueError(f"Message not found: {message_id}")

        forward = message.forward()
        forward.to.add(to)

        if body:
            existing = forward.body or ""
            # The forwarded body is HTML for most messages. Prepending a plain
            # -text note with "\n\n" collapses the newlines (HTML ignores them)
            # and leaves any special characters in the note unescaped. When the
            # body is HTML, escape the note and turn newlines into <br>; keep
            # body_type aligned so the note renders as written.
            body_type = str(getattr(forward, "body_type", "") or "")
            if body_type.lower() == "html":
                import html as _html
                note = _html.escape(body).replace("\n", "<br>")
                forward.body = note + "<br><br>" + existing
                forward.body_type = "HTML"
            else:
                forward.body = body + "\n\n" + existing

        forward.send()

        return {
            'status': 'forwarded',
            'original_id': message_id,
            'to': to
        }

    # =========================================================================
    # Flag and Category Operations
    # =========================================================================

    def flag_message(self, message_id: str, flag_status: str = 'flagged',
                     due_date: Optional[datetime] = None) -> bool:
        """
        Flag a message for follow-up.

        Args:
            message_id: Message ID
            flag_status: 'flagged', 'complete', or 'notFlagged'
            due_date: Optional due date for follow-up

        Returns:
            True if flagged
        """
        mailbox = self.account.mailbox()
        message = mailbox.get_message(object_id=message_id)

        if not message:
            raise ValueError(f"Message not found: {message_id}")

        # Set flag using the internal attribute
        flag_data = {'flagStatus': flag_status}
        if due_date and flag_status == 'flagged':
            # Convert to UTC so the due date is not shifted by the local offset.
            flag_data['dueDateTime'] = {
                'dateTime': _to_utc(due_date).isoformat(),
                'timeZone': 'UTC'
            }

        message.flag = flag_data
        message.save_message()

        return True

    def set_categories(self, message_id: str, categories: List[str]) -> bool:
        """
        Set categories on a message.

        Args:
            message_id: Message ID
            categories: List of category names

        Returns:
            True if updated
        """
        mailbox = self.account.mailbox()
        message = mailbox.get_message(object_id=message_id)

        if not message:
            raise ValueError(f"Message not found: {message_id}")

        message.categories = categories
        message.save_message()

        return True

    # =========================================================================
    # Attachment Operations
    # =========================================================================

    def list_attachments(self, message_id: str) -> list:
        """
        List attachments on a message.

        Args:
            message_id: Message ID

        Returns:
            List of attachment dicts
        """
        mailbox = self.account.mailbox()
        message = mailbox.get_message(object_id=message_id)

        if not message:
            raise ValueError(f"Message not found: {message_id}")

        attachments = message.attachments
        result = []

        for att in attachments:
            result.append({
                'id': att.attachment_id,
                'name': att.name,
                'size': getattr(att, 'size', 0),
                'content_type': getattr(att, 'content_type', 'unknown'),
                'is_inline': getattr(att, 'is_inline', False)
            })

        return result

    def download_attachment(self, message_id: str, attachment_id: str,
                           save_path: str) -> dict:
        """
        Download an attachment.

        Args:
            message_id: Message ID
            attachment_id: Attachment ID
            save_path: Path to save the file

        Returns:
            Dict with download info
        """
        mailbox = self.account.mailbox()
        message = mailbox.get_message(object_id=message_id)

        if not message:
            raise ValueError(f"Message not found: {message_id}")

        attachments = message.attachments
        target_attachment = None

        for att in attachments:
            if att.attachment_id == attachment_id:
                target_attachment = att
                break

        if not target_attachment:
            raise ValueError(f"Attachment not found: {attachment_id}")

        target_attachment.save(save_path)

        return {
            'name': target_attachment.name,
            'path': save_path,
            'size': getattr(target_attachment, 'size', 0)
        }

    # =========================================================================
    # Extended Calendar Operations
    # =========================================================================

    def get_event(self, event_id: str) -> Optional[dict]:
        """
        Get a single event by ID.

        Args:
            event_id: Event ID

        Returns:
            Event dict or None if not found
        """
        schedule = self.account.schedule()
        calendar = schedule.get_default_calendar()

        event = calendar.get_event(object_id=event_id)
        if event:
            return self._format_event(event)
        return None

    def delete_event(self, event_id: str) -> bool:
        """
        Delete a calendar event.

        Args:
            event_id: Event ID

        Returns:
            True if deleted
        """
        schedule = self.account.schedule()
        calendar = schedule.get_default_calendar()

        event = calendar.get_event(object_id=event_id)
        if not event:
            raise ValueError(f"Event not found: {event_id}")

        event.delete()
        return True

    def update_event(self, event_id: str, subject: str = None,
                     start_time: datetime = None, end_time: datetime = None,
                     location: str = None, body: str = None) -> dict:
        """
        Update a calendar event.

        Args:
            event_id: Event ID
            subject: New subject (optional)
            start_time: New start time (optional)
            end_time: New end time (optional)
            location: New location (optional)
            body: New body (optional)

        Returns:
            Updated event info
        """
        schedule = self.account.schedule()
        calendar = schedule.get_default_calendar()

        event = calendar.get_event(object_id=event_id)
        if not event:
            raise ValueError(f"Event not found: {event_id}")

        if subject is not None:
            event.subject = subject
        if start_time is not None:
            event.start = _as_aware(start_time)
        if end_time is not None:
            event.end = _as_aware(end_time)
        if location is not None:
            event.location = location
        if body is not None:
            event.body = body

        event.save()

        return self._format_event(event)

    def respond_to_event(self, event_id: str, response: str,
                         message: str = None) -> bool:
        """
        Respond to a calendar event invitation.

        Args:
            event_id: Event ID
            response: 'accept', 'decline', or 'tentative'
            message: Optional response message

        Returns:
            True if responded
        """
        schedule = self.account.schedule()
        calendar = schedule.get_default_calendar()

        event = calendar.get_event(object_id=event_id)
        if not event:
            raise ValueError(f"Event not found: {event_id}")

        if response == 'accept':
            event.accept_event(message=message)
        elif response == 'decline':
            event.decline_event(message=message)
        elif response == 'tentative':
            event.tentatively_accept_event(message=message)
        else:
            raise ValueError(f"Invalid response: {response}. Use 'accept', 'decline', or 'tentative'")

        return True

    def search_events(self, query: str, start_date: datetime = None,
                      end_date: datetime = None, calendar_name: str = None,
                      limit: int = 25) -> list:
        """
        Search calendar events within a date range.

        Matches against event subject, organizer email, and attendee
        names/emails (all case-insensitive).

        Args:
            query: Search text (matched against subject, organizer, attendee names/emails)
            start_date: Start of date range (default: 1 year ago)
            end_date: End of date range (default: now)
            calendar_name: Specific calendar name (optional)
            limit: Max results to return

        Returns:
            List of matching event dicts
        """
        if not start_date:
            start_date = datetime.now() - timedelta(days=365)
        if not end_date:
            end_date = datetime.now()

        events = self.get_events(
            start_date=start_date,
            end_date=end_date,
            calendar_name=calendar_name,
        )

        query_lower = query.lower()
        matched = []
        for event in events:
            # Search subject
            subject = (event.get('subject', '') or '').lower()
            if query_lower in subject:
                matched.append(event)
                if len(matched) >= limit:
                    break
                continue

            # Search organizer
            organizer = (event.get('organizer', '') or '').lower()
            if query_lower in organizer:
                matched.append(event)
                if len(matched) >= limit:
                    break
                continue

            # Search attendee names and emails
            attendees = event.get('attendees', [])
            for att in attendees:
                name = (att.get('name', '') or '').lower()
                email = (att.get('email', '') or '').lower()
                if query_lower in name or query_lower in email:
                    matched.append(event)
                    break
            if len(matched) >= limit:
                break

        return matched

    def get_free_busy(self, emails: list, start: datetime, end: datetime) -> list:
        """
        Check free/busy availability for one or more people.

        Uses the Microsoft Graph getSchedule endpoint.

        Args:
            emails: List of email addresses to check
            start: Start of availability window
            end: End of availability window

        Returns:
            List of dicts with schedule info per person
        """
        # Use direct Graph API call for getSchedule
        url = f'{self.GRAPH_API_BASE}/me/calendar/getSchedule'

        # Convert to UTC so the window matches the timeZone we declare.
        data = {
            'schedules': emails,
            'startTime': {
                'dateTime': _to_utc(start).isoformat(),
                'timeZone': 'UTC',
            },
            'endTime': {
                'dateTime': _to_utc(end).isoformat(),
                'timeZone': 'UTC',
            },
            'availabilityViewInterval': 30,
        }

        con = self.account.connection
        response = con.post(url, data=data)

        # A falsy response means the request failed; do not dereference
        # .status_code on it (that would mask the real error with AttributeError).
        if not response:
            raise ConnectionError("Failed to get schedule (no response from Graph API)")

        result_data = response.json()
        schedules = result_data.get('value', [])

        result = []
        for sched in schedules:
            email = sched.get('scheduleId', '')
            items = []
            for item in sched.get('scheduleItems', []):
                items.append({
                    'subject': item.get('subject', ''),
                    'status': item.get('status', ''),
                    'start': item.get('start', {}).get('dateTime', ''),
                    'end': item.get('end', {}).get('dateTime', ''),
                    'location': item.get('location', ''),
                })
            result.append({
                'email': email,
                'availability_view': sched.get('availabilityView', ''),
                'items': items,
            })

        return result

    def forward_event(self, event_id: str, to_emails: list, comment: str = None) -> bool:
        """
        Forward a calendar event to recipients.

        Uses the Microsoft Graph event forward action.

        Args:
            event_id: Event ID to forward
            to_emails: List of recipient email addresses
            comment: Optional message to include

        Returns:
            True if forwarded successfully
        """
        url = f'{self.GRAPH_API_BASE}/me/events/{event_id}/forward'

        to_recipients = []
        for email in to_emails:
            to_recipients.append({
                'emailAddress': {
                    'address': email,
                }
            })

        data = {
            'ToRecipients': to_recipients,
        }
        if comment:
            data['Comment'] = comment

        con = self.account.connection
        response = con.post(url, data=data)

        # A falsy response means the request failed; do not dereference
        # .status_code on it (that would mask the real error with AttributeError).
        if not response:
            raise ConnectionError("Failed to forward event (no response from Graph API)")

        return True

    def _format_event(self, event) -> dict:
        """Format an event object into a dict with full details."""
        result = {
            'id': event.object_id,
            'subject': event.subject,
            'start': event.start.isoformat() if event.start else None,
            'end': event.end.isoformat() if event.end else None,
            'location': event.location.get('displayName', '') if event.location else '',
            'is_all_day': event.is_all_day,
            'organizer': event.organizer.address if event.organizer else None,
            'body': event.body,
            'is_recurring': getattr(event, 'is_recurring', False),
            'is_cancelled': getattr(event, 'is_cancelled', False),
            'importance': getattr(event, 'importance', 'normal'),
            'sensitivity': getattr(event, 'sensitivity', 'normal'),
            'show_as': getattr(event, 'show_as', 'busy'),
            'categories': getattr(event, 'categories', []),
            'web_link': getattr(event, 'web_link', None),
        }

        # Online meeting info
        if getattr(event, 'is_online_meeting', False):
            result['is_online_meeting'] = True
            online_meeting = getattr(event, 'online_meeting', None)
            if online_meeting:
                result['join_url'] = online_meeting.get('joinUrl', '')
                result['conference_id'] = online_meeting.get('conferenceId', '')

        # Attendees with response status
        attendees = []
        if event.attendees:
            for att in event.attendees:
                att_info = {
                    'email': att.address,
                    'name': getattr(att, 'name', ''),
                    'type': getattr(att, 'type', 'required'),
                }
                response_status = getattr(att, 'response_status', None)
                if response_status:
                    status_val = getattr(response_status, 'status', None)
                    att_info['response'] = str(status_val) if status_val else 'none'
                attendees.append(att_info)
        result['attendees'] = attendees

        # User's response status
        response_status = getattr(event, 'response_status', None)
        if response_status:
            status_val = getattr(response_status, 'status', None)
            result['my_response'] = str(status_val) if status_val else 'none'

        return result

    # =========================================================================
    # Helper Methods
    # =========================================================================

    def _format_message(self, msg, include_body: bool = False) -> dict:
        """Format a message object into a dict."""
        result = {
            'id': msg.object_id,
            'subject': msg.subject or '(no subject)',
            'from': msg.sender.address if msg.sender else None,
            'from_name': msg.sender.name if msg.sender else None,
            'to': [r.address for r in msg.to] if msg.to else [],
            'date': msg.received.isoformat() if msg.received else None,
            'has_attachments': msg.has_attachments,
            'is_read': msg.is_read,
            'importance': msg.importance.value if msg.importance else 'normal',
            'categories': getattr(msg, 'categories', []),
            'conversation_id': getattr(msg, 'conversation_id', None),
            'web_link': getattr(msg, 'web_link', None),
        }

        # Flag status
        flag = getattr(msg, 'flag', None)
        status = getattr(flag, 'status', None) if flag else None
        result['flag_status'] = status.value if status is not None else 'notFlagged'

        if hasattr(msg, 'body_preview'):
            result['snippet'] = msg.body_preview

        if include_body:
            result['body'] = msg.body
            # Include CC and BCC for full message view
            result['cc'] = [r.address for r in msg.cc] if msg.cc else []
            result['bcc'] = [r.address for r in msg.bcc] if msg.bcc else []
            result['reply_to'] = [r.address for r in msg.reply_to] if hasattr(msg, 'reply_to') and msg.reply_to else []

        return result
