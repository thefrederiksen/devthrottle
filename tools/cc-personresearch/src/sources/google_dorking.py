"""Google dorking source - targeted Google searches with browser.text() extraction."""

import logging
import re
import time
import urllib.parse

from cc_personresearch.sources.base import BaseSource
from cc_personresearch.models import SourceResult

logger = logging.getLogger(__name__)

# Phrases that indicate Google CAPTCHA / block page
CAPTCHA_SIGNALS = [
    "unusual traffic",
    "automated requests",
    "captcha",
    "please enable javascript",
    "sorry/index",
    "not a robot",
]


class GoogleDorkingSource(BaseSource):
    name = "google_dorking"
    requires_browser = True

    def fetch(self) -> SourceResult:
        if not self.browser:
            return SourceResult(source=self.name, status="skipped",
                                error_message="No browser available")

        queries = self._build_queries()
        all_results = []
        all_urls = []
        captcha_hit = False

        for query in queries:
            # If we already hit a CAPTCHA and couldn't solve it, skip remaining queries
            if captcha_hit:
                all_results.append({
                    "query": query,
                    "skipped": True,
                    "page_text": "",
                })
                continue

            encoded = urllib.parse.quote_plus(query)
            search_url = f"https://www.google.com/search?q={encoded}&num=10"

            try:
                self.browser.navigate(search_url)
                time.sleep(3)

                # Get page text
                page_text = self._get_page_text()

                # Check for CAPTCHA in page text
                if self._looks_like_captcha(page_text):
                    if self.verbose:
                        logger.info("CAPTCHA detected, attempting to solve...")

                    solved = self._try_solve_captcha()
                    if solved:
                        if self.verbose:
                            logger.info("CAPTCHA solved, retrying query")
                        # Re-navigate after solving
                        self.browser.navigate(search_url)
                        time.sleep(3)
                        page_text = self._get_page_text()
                        # Check if still blocked
                        if self._looks_like_captcha(page_text):
                            captcha_hit = True
                            all_results.append({
                                "query": query,
                                "captcha": True,
                                "page_text": "",
                            })
                            continue
                    else:
                        if self.verbose:
                            logger.warning("CAPTCHA not solved, skipping remaining queries")
                        captcha_hit = True
                        all_results.append({
                            "query": query,
                            "captcha": True,
                            "page_text": "",
                        })
                        continue

                result_entry = {
                    "query": query,
                    "page_text": page_text[:5000] if page_text else "",
                }
                all_results.append(result_entry)

                # Extract URLs from page text
                if page_text:
                    all_urls.extend(self._extract_urls_from_text(page_text))

            except Exception as e:
                all_results.append({
                    "query": query,
                    "error": str(e),
                    "page_text": "",
                })

            # Delay between searches to avoid rate limiting
            time.sleep(4)

        if captcha_hit and not all_urls:
            return SourceResult(
                source=self.name, status="error",
                error_message="Google CAPTCHA blocked all searches",
                data={"searches": all_results},
            )

        if not any(r.get("page_text") for r in all_results):
            return SourceResult(source=self.name, status="not_found",
                                data={"searches": all_results})

        return SourceResult(
            source=self.name,
            status="found",
            data={"searches": all_results, "urls": all_urls},
        )

    def _looks_like_captcha(self, page_text: str) -> bool:
        """Check if page text contains CAPTCHA / block signals."""
        if not page_text:
            return False
        text_lower = page_text.lower()
        return any(signal in text_lower for signal in CAPTCHA_SIGNALS)

    def _try_solve_captcha(self) -> bool:
        """Try to solve CAPTCHA via cc-browser daemon endpoint."""
        try:
            result = self.browser.captcha_solve(max_attempts=2)
            return result.get("solved", False)
        except Exception as e:
            if self.verbose:
                logger.warning("captcha_solve error: %s", e)
            return False

    def _build_queries(self) -> list[str]:
        """Build targeted Google dork queries tied to known context."""
        name = self.person_name
        queries = []
        company = self._context_company()

        # Name + company (most specific, most valuable)
        if company:
            queries.append(f'"{name}" "{company}"')

        # Name + email together
        if self.email:
            queries.append(f'"{name}" "{self.email}"')

        # Name + role keywords with company
        if company:
            queries.append(f'"{name}" CEO OR "co-founder" OR founder OR president "{company}"')

        # LinkedIn specific with company
        if company:
            queries.append(f'site:linkedin.com "{name}" {company}')
        else:
            queries.append(f'"{name}" site:linkedin.com')

        # Name on team/about pages
        queries.append(f'"{name}" inurl:about OR inurl:team OR inurl:leadership')

        # Email mentioned elsewhere (if corporate email)
        if self.email and company:
            domain = self._email_domain()
            queries.append(f'"{self.email}" -site:{domain}')

        return queries

    def _extract_urls_from_text(self, text: str) -> list[str]:
        """Extract URLs that appear in Google result text."""
        urls = []
        url_pattern = re.compile(r'https?://[^\s<>"\']+')
        for match in url_pattern.finditer(text):
            url = match.group(0).rstrip(".,;:)")
            # Skip google.com internal URLs
            if "google.com" not in url and "gstatic.com" not in url:
                urls.append(url)
        return urls[:20]
