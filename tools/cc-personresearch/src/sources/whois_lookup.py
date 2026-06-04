"""WHOIS domain lookup source."""

from cc_personresearch.sources.base import BaseSource
from cc_personresearch.models import SourceResult


class WhoisSource(BaseSource):
    name = "whois"
    requires_browser = False

    def fetch(self) -> SourceResult:
        domain = self._email_domain()
        if not domain:
            return SourceResult(source=self.name, status="skipped",
                                error_message="No email domain to check")

        # Skip common public email domains
        public_domains = {
            "gmail.com", "yahoo.com", "hotmail.com", "outlook.com",
            "aol.com", "icloud.com", "mail.com", "protonmail.com",
            "live.com", "msn.com",
        }
        if domain.lower() in public_domains:
            return SourceResult(
                source=self.name, status="skipped",
                error_message=f"Public email domain: {domain}"
            )

        try:
            import whois
            w = whois.whois(domain)
        except Exception as e:
            return SourceResult(
                source=self.name, status="error",
                error_message=f"WHOIS lookup failed: {e}"
            )

        if not w or not w.domain_name:
            return SourceResult(source=self.name, status="not_found")

        result_data = {
            "domain": domain,
            "registrar": w.registrar or "",
            "creation_date": str(w.creation_date) if w.creation_date else "",
            "expiration_date": str(w.expiration_date) if w.expiration_date else "",
            "name_servers": w.name_servers if isinstance(w.name_servers, list) else [],
            "org": w.org or "",
            "registrant_name": getattr(w, "name", "") or "",
            "registrant_country": w.country or "",
            "registrant_state": w.state or "",
            "registrant_city": w.city or "",
            "registrant_address": w.address or "",
            "emails": w.emails if isinstance(w.emails, list) else ([w.emails] if w.emails else []),
        }

        return SourceResult(source=self.name, status="found", data=result_data)
