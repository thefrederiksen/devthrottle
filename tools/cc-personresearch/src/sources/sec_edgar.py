"""SEC EDGAR full-text search source with context filtering."""

import httpx

from cc_personresearch.sources.base import BaseSource
from cc_personresearch.models import SourceResult


class SECEdgarSource(BaseSource):
    name = "sec_edgar"
    requires_browser = False

    def fetch(self) -> SourceResult:
        # SEC EDGAR full-text search - completely free, no API key
        url = "https://efts.sec.gov/LATEST/search-index"
        params = {
            "q": f'"{self.person_name}"',
            "dateRange": "custom",
            "startdt": "2015-01-01",
            "enddt": "2026-12-31",
            "forms": "10-K,10-Q,DEF 14A,8-K,4,S-1",
        }

        resp = httpx.get(
            url,
            params=params,
            timeout=15,
            headers={"User-Agent": "cc-personresearch/0.2.0 research@example.com"},
        )

        if resp.status_code != 200:
            # Try alternate endpoint without filters
            params_alt = {"q": f'"{self.person_name}"'}
            resp = httpx.get(
                url,
                params=params_alt,
                timeout=15,
                headers={"User-Agent": "cc-personresearch/0.2.0 research@example.com"},
            )
            if resp.status_code != 200:
                return SourceResult(
                    source=self.name, status="error",
                    error_message=f"HTTP {resp.status_code}"
                )

        data = resp.json()
        hits = data.get("hits", {}).get("hits", [])

        if not hits:
            return SourceResult(source=self.name, status="not_found")

        company = self._context_company()
        filings = []
        urls = []

        for hit in hits[:15]:
            source = hit.get("_source", {})
            display_names = source.get("display_names", [])
            filing_company = display_names[0] if display_names else ""
            adsh = source.get("adsh", "")
            ciks = source.get("ciks", [])

            filing = {
                "filing_type": source.get("file_type", "") or source.get("form", ""),
                "company": filing_company,
                "date": source.get("file_date", ""),
                "location": ", ".join(source.get("biz_locations", [])),
            }

            # Context matching: flag filings from the known company
            if company:
                filing["context_match"] = company in filing_company.lower()
            else:
                filing["context_match"] = True

            # Build filing URL from CIK and accession number
            if adsh and ciks:
                cik = ciks[0].lstrip("0")
                filing_url = f"https://www.sec.gov/Archives/edgar/data/{cik}/{adsh.replace('-', '')}"
                filing["url"] = filing_url
                urls.append(filing_url)

            filings.append(filing)

        # Prioritize context-matched filings
        if company:
            matched = [f for f in filings if f.get("context_match")]
            unmatched = [f for f in filings if not f.get("context_match")]
            filings = matched + unmatched
            match_count = len(matched)
        else:
            match_count = len(filings)

        return SourceResult(
            source=self.name,
            status="found",
            data={
                "filings": filings,
                "total_results": data.get("hits", {}).get("total", {}).get("value", 0),
                "context_matches": match_count,
                "urls": urls,
            },
        )
