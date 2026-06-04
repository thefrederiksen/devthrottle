"""ThatsThem.com people search source - uses browser.text() for extraction."""

import time
import urllib.parse

from cc_personresearch.sources.base import BaseSource
from cc_personresearch.models import SourceResult


class ThatSThemSource(BaseSource):
    name = "thatsthem"
    requires_browser = True

    def fetch(self) -> SourceResult:
        if not self.browser:
            return SourceResult(source=self.name, status="skipped",
                                error_message="No browser available")

        results = []

        # Search by email first (more specific)
        if self.email:
            email_text = self._search_by_email()
            if email_text:
                results.append({"search_type": "email", "raw_text": email_text})

        # Search by name
        name_text = self._search_by_name()
        if name_text:
            results.append({"search_type": "name", "raw_text": name_text})

        if not results:
            return SourceResult(source=self.name, status="not_found")

        return SourceResult(
            source=self.name,
            status="found",
            data={"results": results},
        )

    def _search_by_email(self) -> str:
        """Search ThatsThem by email, return page text."""
        try:
            encoded = urllib.parse.quote_plus(self.email)
            url = f"https://thatsthem.com/email/{encoded}"
            self.browser.navigate(url)
            time.sleep(4)
            text = self._get_page_text()
            return text[:5000] if text and len(text) > 100 else ""
        except Exception:
            return ""

    def _search_by_name(self) -> str:
        """Search ThatsThem by name, return page text."""
        try:
            first = self._first_name()
            last = self._last_name()
            url = f"https://thatsthem.com/name/{first}-{last}"
            self.browser.navigate(url)
            time.sleep(4)
            text = self._get_page_text()
            return text[:5000] if text and len(text) > 100 else ""
        except Exception:
            return ""
