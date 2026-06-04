"""Wayback Machine CDX API source."""

import httpx

from cc_personresearch.sources.base import BaseSource
from cc_personresearch.models import SourceResult


class WaybackSource(BaseSource):
    name = "wayback"
    requires_browser = False

    def fetch(self) -> SourceResult:
        domain = self._email_domain()
        if not domain:
            return SourceResult(source=self.name, status="skipped",
                                error_message="No email domain to check")

        # Check if company website has archived team/about pages
        urls_to_check = [
            f"{domain}/about",
            f"{domain}/team",
            f"{domain}/about-us",
            f"{domain}/our-team",
            f"{domain}/leadership",
        ]

        archived_pages = []
        urls = []

        for check_url in urls_to_check:
            try:
                resp = httpx.get(
                    "https://web.archive.org/cdx/search/cdx",
                    params={
                        "url": check_url,
                        "output": "json",
                        "limit": 3,
                        "filter": "statuscode:200",
                        "fl": "timestamp,original,statuscode",
                    },
                    timeout=8,
                )
            except httpx.TimeoutException:
                continue

            if resp.status_code != 200:
                continue

            data = resp.json()
            if len(data) > 1:  # First row is headers
                for row in data[1:]:
                    timestamp = row[0] if len(row) > 0 else ""
                    original = row[1] if len(row) > 1 else ""
                    wayback_url = f"https://web.archive.org/web/{timestamp}/{original}"
                    archived_pages.append({
                        "original_url": original,
                        "timestamp": timestamp,
                        "wayback_url": wayback_url,
                    })
                    urls.append(wayback_url)

        if not archived_pages:
            return SourceResult(source=self.name, status="not_found")

        return SourceResult(
            source=self.name,
            status="found",
            data={"archived_pages": archived_pages, "urls": urls},
        )
