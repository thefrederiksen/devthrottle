"""Normalize and aggregate results across sources."""

import re
from typing import Optional

from cc_personresearch.models import PersonReport


def normalize_phone(phone: str) -> str:
    """Normalize phone number to digits only."""
    digits = re.sub(r"[^\d]", "", phone)
    if len(digits) == 11 and digits.startswith("1"):
        digits = digits[1:]
    return digits


def normalize_email(email: str) -> str:
    """Normalize email to lowercase."""
    return email.strip().lower()


def normalize_address(address: str) -> str:
    """Basic address normalization for dedup."""
    addr = address.strip().lower()
    addr = re.sub(r"\bst\.?\b", "street", addr)
    addr = re.sub(r"\bave\.?\b", "avenue", addr)
    addr = re.sub(r"\bdr\.?\b", "drive", addr)
    addr = re.sub(r"\brd\.?\b", "road", addr)
    addr = re.sub(r"\bblvd\.?\b", "boulevard", addr)
    addr = re.sub(r"\bapt\.?\b", "apt", addr)
    addr = re.sub(r"\bste\.?\b", "suite", addr)
    addr = re.sub(r"\s+", " ", addr)
    return addr


def extract_all_urls(report: PersonReport) -> list[str]:
    """Extract all URLs found across all sources."""
    urls = set()
    for source_result in report.sources.values():
        if source_result.status != "found":
            continue
        data = source_result.data
        # Direct urls field
        for url in data.get("urls", []):
            urls.add(url)
        # Profile URLs
        if data.get("profile_url"):
            urls.add(data["profile_url"])
        if data.get("avatar_url"):
            urls.add(data["avatar_url"])
        # Nested results
        for item in data.get("results", []):
            if isinstance(item, dict):
                for key in ("url", "profile_url", "link"):
                    if item.get(key):
                        urls.add(item[key])
    return sorted(urls)


def aggregate(report: PersonReport) -> PersonReport:
    """Post-process report: collect all URLs, normalize data."""
    # Collect all discovered URLs
    all_urls = extract_all_urls(report)
    for url in all_urls:
        report.add_url(url)

    return report
