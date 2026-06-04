"""Gravatar API source - email to avatar and linked accounts."""

import hashlib

import httpx

from cc_personresearch.sources.base import BaseSource
from cc_personresearch.models import SourceResult


class GravatarSource(BaseSource):
    name = "gravatar"
    requires_browser = False

    def fetch(self) -> SourceResult:
        if not self.email:
            return SourceResult(source=self.name, status="skipped",
                                error_message="No email provided")

        email_hash = hashlib.sha256(self.email.strip().lower().encode()).hexdigest()

        # Try Gravatar REST API v3
        url = f"https://api.gravatar.com/v3/profiles/{email_hash}"
        response = httpx.get(url, timeout=10, headers={"Accept": "application/json"})

        if response.status_code == 404:
            return SourceResult(source=self.name, status="not_found")

        if response.status_code != 200:
            return SourceResult(
                source=self.name, status="error",
                error_message=f"HTTP {response.status_code}"
            )

        data = response.json()

        result_data = {
            "display_name": data.get("display_name", ""),
            "profile_url": data.get("profile_url", ""),
            "avatar_url": data.get("avatar_url", ""),
            "location": data.get("location", ""),
            "description": data.get("description", ""),
            "job_title": data.get("job_title", ""),
            "company": data.get("company", ""),
            "verified_accounts": [],
            "urls": [],
        }

        # Extract verified accounts
        for account in data.get("verified_accounts", []):
            result_data["verified_accounts"].append({
                "service": account.get("service_label", ""),
                "url": account.get("url", ""),
                "username": account.get("service_username", ""),
            })
            if account.get("url"):
                result_data["urls"].append(account["url"])

        if data.get("profile_url"):
            result_data["urls"].append(data["profile_url"])

        return SourceResult(source=self.name, status="found", data=result_data)
