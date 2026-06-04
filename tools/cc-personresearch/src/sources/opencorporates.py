"""OpenCorporates officer search source - uses browser.text() for extraction."""

import time
import urllib.parse

from cc_personresearch.sources.base import BaseSource
from cc_personresearch.models import SourceResult


class OpenCorporatesSource(BaseSource):
    name = "opencorporates"
    requires_browser = True

    def fetch(self) -> SourceResult:
        if not self.browser:
            return SourceResult(source=self.name, status="skipped",
                                error_message="No browser available")

        query = urllib.parse.quote_plus(self.person_name)
        url = f"https://opencorporates.com/officers?q={query}&utf8=1"

        try:
            self.browser.navigate(url)
            time.sleep(4)

            text = self._get_page_text()

            if not text or len(text) < 100:
                return SourceResult(source=self.name, status="not_found")

            return SourceResult(
                source=self.name,
                status="found",
                data={"officers": [{"raw_text": text[:5000]}]},
            )

        except Exception as e:
            return SourceResult(
                source=self.name, status="error",
                error_message=str(e),
            )
