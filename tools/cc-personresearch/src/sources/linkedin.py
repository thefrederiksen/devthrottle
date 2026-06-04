"""LinkedIn source - uses cc-browser CLI with LinkedIn connection."""

import json
import logging
import re
import subprocess
import sys

from cc_personresearch.sources.base import BaseSource
from cc_personresearch.models import SourceResult

logger = logging.getLogger(__name__)


def _extract_json(text: str):
    """Extract JSON array or object from text that may have non-JSON header lines.

    cc-browser --format json may output a human-readable header line before the
    JSON payload. This function finds and parses just the JSON portion.
    """
    text = text.strip()
    if not text:
        return None

    # Try parsing the whole string first
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        pass

    # Find the first [ or { which starts the JSON
    for i, ch in enumerate(text):
        if ch in ("[", "{"):
            try:
                return json.loads(text[i:])
            except json.JSONDecodeError:
                continue

    return None


class LinkedInSource(BaseSource):
    name = "linkedin"
    requires_browser = True  # Uses cc-browser with LinkedIn connection

    def __init__(self, *args, linkedin_connection: str = "linkedin", **kwargs):
        super().__init__(*args, **kwargs)
        self.linkedin_connection = linkedin_connection

    def fetch(self) -> SourceResult:
        results = {"search_results": [], "profile": None, "urls": []}

        # Step 1: Search with company context first (more specific)
        company = self._context_company()
        search_results = []

        if company:
            query_with_company = f"{self.person_name} {company}"
            if self.verbose:
                logger.info("Searching: %s", query_with_company)
            search_results = self._search(query_with_company)

        # Fallback: search by name only
        if not search_results:
            if self.verbose:
                logger.info("Searching: %s", self.person_name)
            search_results = self._search(self.person_name)

        if not search_results:
            return SourceResult(source=self.name, status="not_found",
                                data=results)

        results["search_results"] = search_results

        # Step 2: Get detailed profile for top result
        top_result = search_results[0]
        username = top_result.get("username", "")
        if username:
            if self.verbose:
                logger.info("Fetching profile: %s", username)
            profile = self._get_profile(username)
            if profile:
                results["profile"] = profile
                profile_url = f"https://www.linkedin.com/in/{username}"
                results["urls"].append(profile_url)

        # Collect all profile URLs from search
        for sr in search_results:
            uname = sr.get("username", "")
            if uname:
                url = f"https://www.linkedin.com/in/{uname}"
                if url not in results["urls"]:
                    results["urls"].append(url)

        return SourceResult(
            source=self.name,
            status="found",
            data=results,
        )

    def _search(self, query: str) -> list[dict]:
        """Run cc-browser search on LinkedIn.

        Uses the LinkedIn connection to search for people.
        """
        cmd = [
            "cc-browser",
            "--connection", self.linkedin_connection,
            "evaluate", "--fn",
            f"() => {{ window.location.href = 'https://www.linkedin.com/search/results/people/?keywords={query}'; }}",
        ]
        try:
            result = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                timeout=90,
            )
            if result.returncode != 0:
                stderr = result.stderr.strip() if result.stderr else ""
                if self.verbose and stderr:
                    logger.warning("search stderr: %s", stderr)
                return []

            # After navigation, get the page text to extract results
            snapshot_cmd = [
                "cc-browser",
                "--connection", self.linkedin_connection,
                "text",
            ]
            snap_result = subprocess.run(
                snapshot_cmd,
                capture_output=True,
                text=True,
                timeout=30,
            )

            output = snap_result.stdout.strip()
            if not output:
                if self.verbose:
                    logger.debug("search returned empty snapshot")
                return []

            data = _extract_json(output)
            if data is None:
                if self.verbose:
                    logger.debug("no JSON found in output: %s", output[:200])
                return []
            if isinstance(data, list):
                return data
            if isinstance(data, dict) and "results" in data:
                return data["results"]
            return []
        except FileNotFoundError:
            if self.verbose:
                logger.warning("cc-browser CLI not found on PATH")
            return []
        except subprocess.TimeoutExpired:
            if self.verbose:
                logger.warning("search timed out after 90s")
            return []

    def _get_profile(self, username: str) -> dict:
        """Navigate to LinkedIn profile and extract data via cc-browser."""
        cmd = [
            "cc-browser",
            "--connection", self.linkedin_connection,
            "navigate", "--url", f"https://www.linkedin.com/in/{username}",
        ]
        try:
            result = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                timeout=90,
            )
            if result.returncode != 0:
                stderr = result.stderr.strip() if result.stderr else ""
                if self.verbose and stderr:
                    logger.warning("profile stderr: %s", stderr)
                return {}

            # Get page text after navigation
            text_cmd = [
                "cc-browser",
                "--connection", self.linkedin_connection,
                "text",
            ]
            text_result = subprocess.run(
                text_cmd,
                capture_output=True,
                text=True,
                timeout=30,
            )

            output = text_result.stdout.strip()
            if not output:
                return {}

            data = _extract_json(output)
            return data if isinstance(data, dict) else {}
        except FileNotFoundError:
            return {}
        except subprocess.TimeoutExpired:
            if self.verbose:
                logger.warning("profile timed out after 90s")
            return {}
