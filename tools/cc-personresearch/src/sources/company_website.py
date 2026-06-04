"""Company website scraper - checks homepage, team, about, and blog pages."""

import time

from cc_personresearch.sources.base import BaseSource, PUBLIC_EMAIL_DOMAINS
from cc_personresearch.models import SourceResult


class CompanyWebsiteSource(BaseSource):
    name = "company_website"
    requires_browser = True

    def fetch(self) -> SourceResult:
        if not self.browser:
            return SourceResult(source=self.name, status="skipped",
                                error_message="No browser available")

        domain = self._email_domain()
        if not domain:
            return SourceResult(source=self.name, status="skipped",
                                error_message="No email domain to check")

        if domain.lower() in PUBLIC_EMAIL_DOMAINS:
            return SourceResult(source=self.name, status="skipped",
                                error_message=f"Public email domain: {domain}")

        # Build name variants for searching
        full_name = self.person_name.lower()
        first = self._first_name().lower()
        last = self._last_name().lower()

        # Paths to check, ordered by likelihood
        paths = [
            "/",              # Homepage (often mentions leadership)
            "/about",
            "/about-us",
            "/team",
            "/our-team",
            "/leadership",
            "/people",
            "/staff",
            "/directory",
            "/management",
            f"/blog/author/{first}-{last}",
            f"/author/{first}-{last}",
        ]

        found_data = []
        urls = []

        for path in paths:
            url = f"https://{domain}{path}"
            try:
                self.browser.navigate(url)
                time.sleep(3)

                # Check if we got a real page (not 404)
                info = self.browser.info()
                title = info.get("title", "")

                if "404" in title.lower() or "not found" in title.lower():
                    continue

                # Use browser.text() for reliable extraction
                text = self._get_page_text()
                if not text:
                    continue

                # Check if the person's name appears in the page text
                text_lower = text.lower()
                if full_name in text_lower or (first in text_lower and last in text_lower):
                    # Extract context around the name mention
                    idx = text_lower.find(full_name)
                    if idx == -1:
                        idx = text_lower.find(last)

                    start = max(0, idx - 200)
                    end = min(len(text), idx + len(full_name) + 500)
                    context = text[start:end]

                    found_data.append({
                        "found": True,
                        "page_url": url,
                        "page_title": title,
                        "context": context,
                        "full_page_text": text[:3000],
                    })
                    urls.append(url)

            except Exception:
                continue

        if not found_data:
            return SourceResult(source=self.name, status="not_found")

        return SourceResult(
            source=self.name,
            status="found",
            data={"pages": found_data, "urls": urls},
        )
