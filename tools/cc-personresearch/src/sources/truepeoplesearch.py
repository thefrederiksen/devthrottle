"""TruePeopleSearch.com source - uses browser.text() for extraction."""

import time
import urllib.parse

from cc_personresearch.sources.base import BaseSource
from cc_personresearch.models import SourceResult


class TruePeopleSearchSource(BaseSource):
    name = "truepeoplesearch"
    requires_browser = True

    def fetch(self) -> SourceResult:
        if not self.browser:
            return SourceResult(source=self.name, status="skipped",
                                error_message="No browser available")

        first = self._first_name()
        last = self._last_name()
        query = urllib.parse.quote_plus(f"{first} {last}")
        url = f"https://www.truepeoplesearch.com/results?name={query}"

        try:
            self.browser.navigate(url)
            time.sleep(5)

            text = self._get_page_text()

            if not text or len(text) < 100:
                return SourceResult(source=self.name, status="not_found")

            return SourceResult(
                source=self.name,
                status="found",
                data={"results": [{"raw_text": text[:5000]}]},
            )

        except Exception as e:
            return SourceResult(
                source=self.name, status="error",
                error_message=str(e),
            )
