"""FEC campaign donation records source with context filtering."""

import httpx

from cc_personresearch.sources.base import BaseSource
from cc_personresearch.models import SourceResult


class FECSource(BaseSource):
    name = "fec"
    requires_browser = False

    def fetch(self) -> SourceResult:
        # FEC API - free, DEMO_KEY has low rate limits but works
        url = "https://api.open.fec.gov/v1/schedules/schedule_a/"
        params = {
            "contributor_name": self.person_name,
            "api_key": "DEMO_KEY",
            "per_page": 20,
            "sort": "-contribution_receipt_date",
        }

        resp = httpx.get(url, params=params, timeout=15)

        if resp.status_code == 429:
            return SourceResult(
                source=self.name, status="error",
                error_message="FEC API rate limit exceeded (DEMO_KEY). Try again later."
            )

        if resp.status_code != 200:
            return SourceResult(
                source=self.name, status="error",
                error_message=f"HTTP {resp.status_code}"
            )

        data = resp.json()
        results_list = data.get("results", [])

        if not results_list:
            return SourceResult(source=self.name, status="not_found")

        company = self._context_company()
        contributions = []
        for item in results_list:
            date = (
                item.get("contribution_receipt_date")
                or item.get("receipt_date")
                or item.get("date")
                or ""
            )
            entry = {
                "contributor_name": item.get("contributor_name", ""),
                "date": date,
                "amount": item.get("contribution_receipt_amount", 0),
                "recipient": item.get("committee", {}).get("name", ""),
                "employer": item.get("contributor_employer", ""),
                "occupation": item.get("contributor_occupation", ""),
                "city": item.get("contributor_city", ""),
                "state": item.get("contributor_state", ""),
                "zip": item.get("contributor_zip", ""),
                "street": item.get("contributor_street_1", ""),
            }

            # Filter: if we have company context, only include matching entries
            if company:
                employer = (entry["employer"] or "").lower()
                employer_normalized = employer.replace(" ", "").replace("-", "").replace("_", "")
                if company in employer or company in employer_normalized or not employer:
                    entry["context_match"] = True
                else:
                    entry["context_match"] = False
            contributions.append(entry)

        # If we have company context, prioritize matches
        if company:
            matched = [c for c in contributions if c.get("context_match", False)]
            unmatched = [c for c in contributions if not c.get("context_match", False)]
            contributions = matched + unmatched
            # Flag how many matched
            match_count = len(matched)
        else:
            match_count = len(contributions)

        return SourceResult(
            source=self.name,
            status="found",
            data={
                "contributions": contributions,
                "total_results": data.get("pagination", {}).get("count", 0),
                "context_matches": match_count,
            },
        )
