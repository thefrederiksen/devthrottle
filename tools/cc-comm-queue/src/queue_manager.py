"""Queue operations for Communication Manager."""

import logging
import re
from pathlib import Path
from typing import Any, Dict, List, Optional

# Handle imports for both package and frozen executable
try:
    from .schema import ContentItem, QueueResult, QueueStats, Status
    from .database import Database, InvalidStatusTransition
except ImportError:
    from schema import ContentItem, QueueResult, QueueStats, Status
    from database import Database, InvalidStatusTransition

logger = logging.getLogger(__name__)


class QueueManager:
    """Manages the communication content queue using SQLite database."""

    def __init__(self, queue_path: Path):
        """Initialize the queue manager.

        Args:
            queue_path: Path to the content directory (contains communications.db)
        """
        self.queue_path = queue_path
        self.db = Database(queue_path)

        # Keep paths for backward compatibility (migration)
        self.pending_path = queue_path / "pending_review"
        self.approved_path = queue_path / "approved"
        self.rejected_path = queue_path / "rejected"
        self.posted_path = queue_path / "posted"

    def ensure_directories(self) -> None:
        """Ensure content directory exists (for backward compatibility)."""
        self.queue_path.mkdir(parents=True, exist_ok=True)

    def add_content(self, item: ContentItem, media_files: Optional[List[Path]] = None) -> QueueResult:
        """Add a content item to the pending_review queue.

        Args:
            item: The content item to add
            media_files: Optional list of media file paths to attach

        Returns:
            QueueResult with success status and ticket number
        """
        try:
            ticket_number = self.db.add_communication(item)

            # Add additional media files if provided
            if media_files:
                for media_path in media_files:
                    if media_path.exists():
                        media_type = self._guess_media_type(media_path)
                        self.db.add_media(ticket_number, media_path, media_type)

            logger.info("Added content to queue: ticket #%d", ticket_number)
            return QueueResult(
                success=True,
                id=item.id,
                file=f"ticket #{ticket_number}",
            )

        except Exception as e:
            logger.error("Failed to add content: %s", e)
            return QueueResult(
                success=False,
                error=f"Failed to add content: {e}",
            )

    def _guess_media_type(self, path: Path) -> str:
        """Guess media type from file extension."""
        ext = path.suffix.lower()
        if ext in [".jpg", ".jpeg", ".png", ".gif", ".svg", ".webp"]:
            return "image"
        elif ext in [".mp4", ".webm", ".mov", ".avi"]:
            return "video"
        else:
            return "document"

    def list_content(self, status: Optional[Status] = None, limit: int = 100, campaign_id: Optional[str] = None) -> List[Dict[str, Any]]:
        """List content items, optionally filtered by status and/or campaign.

        Args:
            status: Filter by status, or None for all
            limit: Maximum results
            campaign_id: Filter by campaign identifier, or None for all

        Returns:
            List of content item dictionaries
        """
        return self.db.list_by_status(status, limit, campaign_id=campaign_id)

    def count_content(self, status: Optional[Status] = None, campaign_id: Optional[str] = None) -> int:
        """Count content items matching a status and/or campaign (ignores any limit).

        Args:
            status: Filter by status, or None for all
            campaign_id: Filter by campaign identifier, or None for all

        Returns:
            The true number of matching items.
        """
        return self.db.count_by_status(status, campaign_id=campaign_id)

    def get_stats(self) -> QueueStats:
        """Get queue statistics.

        Returns:
            QueueStats with counts for each status
        """
        stats = self.db.get_stats()
        return QueueStats(
            pending_review=stats.get("pending_review", 0),
            approved=stats.get("approved", 0),
            rejected=stats.get("rejected", 0),
            posted=stats.get("posted", 0),
            error=stats.get("error", 0),
        )

    def get_content_by_id(self, content_id: str) -> Optional[Dict[str, Any]]:
        """Get a content item by ID.

        Args:
            content_id: The content item ID (or partial ID)

        Returns:
            Content item dictionary or None if not found
        """
        return self.db.get_by_id(content_id)

    def get_content_by_ticket(self, ticket_number: int) -> Optional[Dict[str, Any]]:
        """Get a content item by ticket number.

        Args:
            ticket_number: The ticket number

        Returns:
            Content item dictionary or None if not found
        """
        return self.db.get_by_ticket(ticket_number)

    def approve_content(self, ticket_number: int) -> bool:
        """Approve a content item.

        Args:
            ticket_number: The ticket number

        Returns:
            True if successful
        """
        return self.db.update_status(ticket_number, Status.APPROVED)

    def reject_content(self, ticket_number: int, reason: Optional[str] = None) -> bool:
        """Reject a content item.

        Args:
            ticket_number: The ticket number
            reason: Optional rejection reason

        Returns:
            True if successful
        """
        from datetime import datetime
        return self.db.update_status(
            ticket_number,
            Status.REJECTED,
            rejection_reason=reason,
            rejected_at=datetime.now().isoformat(),
            rejected_by="user",
        )

    def mark_posted(self, ticket_number: int, posted_by: str = "cc_director", posted_url: Optional[str] = None, force: bool = False) -> bool:
        """Mark a content item as posted.

        Args:
            ticket_number: The ticket number
            posted_by: Who/what posted it
            posted_url: Optional URL where it was posted
            force: Bypass the approval-workflow guard (only approved items may post)

        Returns:
            True if successful

        Raises:
            InvalidStatusTransition: if the item is not approved and force is False
        """
        from datetime import datetime
        return self.db.update_status(
            ticket_number,
            Status.POSTED,
            force=force,
            posted_at=datetime.now().isoformat(),
            posted_by=posted_by,
            posted_url=posted_url,
        )

    def mark_first_comment_posted(
        self,
        ticket_number: int,
        comment_url: Optional[str] = None,
        comment_text: Optional[str] = None,
    ) -> bool:
        """Record that the first_comment has been published.

        Sets first_comment_posted_at to now. Optionally updates the comment
        text (if it changed at post time) and the comment URL (permalink).
        Does NOT touch the parent post's status.
        """
        from datetime import datetime
        return self.db.update_first_comment_state(
            ticket_number,
            posted_at=datetime.now().isoformat(),
            comment_url=comment_url,
            comment_text=comment_text,
        )

    def mark_error(self, ticket_number: int, error_reason: str, error_by: str = "cc_director") -> bool:
        """Mark a content item as error.

        Args:
            ticket_number: The ticket number
            error_reason: Why the send failed
            error_by: Who/what detected the error

        Returns:
            True if successful
        """
        from datetime import datetime
        return self.db.update_status(
            ticket_number,
            Status.ERROR,
            rejection_reason=error_reason,
            rejected_at=datetime.now().isoformat(),
            rejected_by=error_by,
        )

    def move_to_review(self, ticket_number: int) -> bool:
        """Move a content item back to pending review.

        Args:
            ticket_number: The ticket number

        Returns:
            True if successful
        """
        return self.db.update_status(
            ticket_number,
            Status.PENDING_REVIEW,
            rejection_reason=None,
            rejected_at=None,
            rejected_by=None,
        )

    def update_content(self, ticket_number: int, content: str) -> bool:
        """Update the content of an item.

        Args:
            ticket_number: The ticket number
            content: The new content

        Returns:
            True if successful
        """
        return self.db.update_content(ticket_number, content)

    def delete_content(self, ticket_number: int) -> bool:
        """Delete a content item.

        Args:
            ticket_number: The ticket number

        Returns:
            True if successful
        """
        return self.db.delete_communication(ticket_number)

    def add_media(self, ticket_number: int, file_path: Path, media_type: str = "image", alt_text: Optional[str] = None) -> int:
        """Add a media file to a communication as BLOB.

        Args:
            ticket_number: The ticket number
            file_path: Path to the media file
            media_type: Type of media
            alt_text: Optional alt text

        Returns:
            The media ID
        """
        return self.db.add_media(ticket_number, file_path, media_type, alt_text)

    def get_media_data(self, media_id: int) -> Optional[bytes]:
        """Retrieve media BLOB data by ID.

        Args:
            media_id: The media ID

        Returns:
            The file bytes or None if not found
        """
        return self.db.get_media_data(media_id)

    def get_media(self, ticket_number: int) -> List[Dict[str, Any]]:
        """Get media files for a communication.

        Args:
            ticket_number: The ticket number

        Returns:
            List of media dictionaries
        """
        return self.db.get_media(ticket_number)

    def search(self, query: str, limit: int = 50) -> List[Dict[str, Any]]:
        """Search communications by content.

        Args:
            query: Search query
            limit: Maximum results

        Returns:
            List of matching communications
        """
        return self.db.search(query, limit)

    def backfill_recipients(self) -> Dict[str, int]:
        """Backfill null recipient fields from destination_url and notes.

        For items where recipient is null but destination_url contains a LinkedIn
        profile URL, parse the URL and notes to populate the recipient field.

        Returns:
            Dict with 'updated' and 'skipped' counts
        """
        import json

        # Get all items with null recipient
        if self.db.conn is None:
            raise RuntimeError("Database not connected")

        cursor = self.db.conn.execute(
            "SELECT ticket_number, destination_url, notes, content FROM communications WHERE recipient IS NULL AND destination_url IS NOT NULL"
        )
        rows = cursor.fetchall()

        updated = 0
        skipped = 0

        for row in rows:
            ticket_number = row['ticket_number']
            destination_url = row['destination_url'] or ''
            notes = row['notes'] or ''
            content = row['content'] or ''

            # Only handle LinkedIn profile URLs
            if '/in/' not in destination_url:
                skipped += 1
                continue

            # Extract name from notes ("DM to {Name}" pattern)
            name = None
            dm_match = re.search(r'DM to (.+?)(?:\s*$)', notes)
            if dm_match:
                name = dm_match.group(1).strip()

            # Fallback: extract first name from content (first line is "{FirstName},")
            if not name:
                first_line = content.split('\n')[0].strip().rstrip(',')
                if first_line and len(first_line) < 50:
                    name = first_line

            if not name:
                skipped += 1
                continue

            recipient = {
                "name": name,
                "profile_url": destination_url,
            }

            self.db.update_recipient(ticket_number, json.dumps(recipient))
            updated += 1

        return {"updated": updated, "skipped": skipped}

    def log_to_vault(self, ticket_number: int) -> Optional[int]:
        """Log a posted communication to the vault as an interaction.

        Resolves the recipient to a vault contact and creates an interaction record.

        Args:
            ticket_number: The ticket number of the posted item

        Returns:
            The vault interaction ID, or None if logging was not possible
        """
        import subprocess
        import json

        item = self.get_content_by_ticket(ticket_number)
        if not item:
            logger.warning("log_to_vault: ticket #%d not found", ticket_number)
            return None

        platform = item.get('platform', '')
        content_type = item.get('type', '')
        content = item.get('content', '')
        posted_at = item.get('posted_at', '')
        send_from = item.get('send_from', '')

        # Determine recipient email/identifier for vault lookup
        recipient_email = None
        recipient_name = None
        subject = None

        # Try email_specific first
        email_spec = item.get('email_specific')
        if email_spec:
            if isinstance(email_spec, str):
                try:
                    email_spec = json.loads(email_spec)
                except (json.JSONDecodeError, TypeError):
                    email_spec = None
            if email_spec and isinstance(email_spec, dict):
                to_list = email_spec.get('to', [])
                if to_list:
                    recipient_email = to_list[0]
                subject = email_spec.get('subject', '')

        # Try recipient info (LinkedIn messages, etc.)
        recipient_info = item.get('recipient')
        if recipient_info:
            if isinstance(recipient_info, str):
                try:
                    recipient_info = json.loads(recipient_info)
                except (json.JSONDecodeError, TypeError):
                    recipient_info = None
            if recipient_info and isinstance(recipient_info, dict):
                recipient_name = recipient_info.get('name', '')
                profile_url = recipient_info.get('profile_url', '')
                if not recipient_email and recipient_name:
                    recipient_email = recipient_name
                if not recipient_email and profile_url:
                    recipient_email = profile_url

        if not recipient_email:
            # Check destination_url for LinkedIn profile URLs
            destination_url = item.get('destination_url', '') or ''
            if '/in/' in destination_url:
                recipient_email = destination_url
                logger.info("log_to_vault: using destination_url as recipient for ticket #%d: %s", ticket_number, destination_url)
                # Try to extract name from notes "DM to {Name}" pattern
                notes = item.get('notes', '') or ''
                dm_match = re.search(r'DM to ([A-Za-z ]+)', notes)
                if dm_match:
                    recipient_name = dm_match.group(1).strip()
                    recipient_email = recipient_name
                    logger.info("log_to_vault: extracted recipient name from notes: %s", recipient_name)

        if not recipient_email:
            logger.info("log_to_vault: no recipient for ticket #%d, skipping", ticket_number)
            return None

        # Determine interaction type from platform
        type_map = {
            'email': 'email',
            'linkedin': 'linkedin',
            'twitter': 'twitter',
            'reddit': 'reddit',
            'facebook': 'facebook',
            'whatsapp': 'whatsapp',
            'youtube': 'youtube',
        }
        interaction_type = type_map.get(platform, platform or 'message')

        # Build summary from content (truncate)
        summary = content[:200] if content else ''

        # Determine account
        account = send_from or platform or ''

        # Use cc-vault CLI to add interaction (avoids importing vault db directly)
        if not posted_at:
            from datetime import datetime
            posted_at = datetime.now().isoformat()

        cmd = [
            "cc-vault", "contacts", "log-interaction",
            recipient_email,
            "--type", interaction_type,
            "--date", posted_at,
            "--direction", "outbound",
            "--summary", summary,
            "--format", "json",
        ]
        if subject:
            cmd.extend(["--subject", subject])
        if account:
            cmd.extend(["--account", account])

        try:
            result = subprocess.run(
                cmd, capture_output=True, text=True, timeout=30, encoding='utf-8', errors='replace'
            )
            if result.returncode == 0:
                logger.info("log_to_vault: logged ticket #%d to vault", ticket_number)
                # Try to parse interaction ID from output
                for line in result.stdout.splitlines():
                    if 'interaction' in line.lower() and '#' in line:
                        try:
                            return int(line.split('#')[1].split()[0])
                        except (IndexError, ValueError):
                            pass
                return 0
            else:
                logger.warning(
                    "log_to_vault: cc-vault failed for ticket #%d: %s",
                    ticket_number, result.stderr or result.stdout
                )
                return None
        except FileNotFoundError:
            logger.warning("log_to_vault: cc-vault not found on PATH")
            return None
        except subprocess.TimeoutExpired:
            logger.warning("log_to_vault: cc-vault timed out for ticket #%d", ticket_number)
            return None

    def close(self) -> None:
        """Close database connection."""
        self.db.close()
