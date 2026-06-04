"""Base class for all data sources."""

import time
from abc import ABC, abstractmethod
from typing import Optional

from cc_personresearch.browser_client import BrowserClient
from cc_personresearch.models import SourceResult

# Public email domains that don't indicate a company
PUBLIC_EMAIL_DOMAINS = {
    "gmail.com", "yahoo.com", "hotmail.com", "outlook.com",
    "aol.com", "icloud.com", "mail.com", "protonmail.com",
    "live.com", "msn.com", "me.com", "ymail.com",
    "comcast.net", "att.net", "verizon.net", "cox.net",
}


class BaseSource(ABC):
    """Abstract base for all person-research data sources."""

    # Subclasses must set these
    name: str = ""
    requires_browser: bool = False

    def __init__(
        self,
        person_name: str,
        email: Optional[str] = None,
        location: Optional[str] = None,
        browser: Optional[BrowserClient] = None,
        verbose: bool = False,
    ):
        self.person_name = person_name
        self.email = email
        self.location = location
        self.browser = browser
        self.verbose = verbose

    @abstractmethod
    def fetch(self) -> SourceResult:
        """Execute the source lookup and return results.

        Returns:
            SourceResult with status and data.
        """
        ...

    def run(self) -> SourceResult:
        """Run the source with timing and error handling."""
        start = time.time()
        try:
            result = self.fetch()
            result.query_time_ms = int((time.time() - start) * 1000)
            return result
        except Exception as e:
            elapsed = int((time.time() - start) * 1000)
            return SourceResult(
                source=self.name,
                status="error",
                query_time_ms=elapsed,
                error_message=str(e),
            )

    def _first_name(self) -> str:
        """Extract first name from person_name."""
        parts = self.person_name.strip().split()
        return parts[0] if parts else ""

    def _last_name(self) -> str:
        """Extract last name from person_name."""
        parts = self.person_name.strip().split()
        return parts[-1] if parts else ""

    def _email_domain(self) -> Optional[str]:
        """Extract domain from email address."""
        if self.email and "@" in self.email:
            return self.email.split("@")[1]
        return None

    def _context_company(self) -> Optional[str]:
        """Extract company name from email domain.

        E.g., "user@acmecorp.com" -> "acmecorp"
        Returns None for public email domains (gmail, yahoo, etc.).
        """
        domain = self._email_domain()
        if not domain:
            return None
        if domain.lower() in PUBLIC_EMAIL_DOMAINS:
            return None
        # Strip TLD to get company name: "acmecorp.com" -> "acmecorp"
        return domain.split(".")[0].lower()

    def _get_page_text(self) -> str:
        """Get all visible text from the current page via browser.text().

        Resilient alternative to fragile CSS selector JS evaluation.
        Returns empty string on failure.
        """
        if not self.browser:
            return ""
        try:
            resp = self.browser.text()
            return resp.get("text", "")
        except Exception:
            return ""
