"""Database operations for cc-photos - uses vault database.

This module wraps the vault database functions to provide a consistent
interface for cc-photos while storing all data in the central vault.
"""

import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

# cc-vault is a declared dependency; in the shared venv it installs as the cc_vault package.
from cc_vault import db as vault_db
from cc_vault.config import get_config as get_vault_config


def init_db() -> None:
    """Initialize the vault database (creates photo tables if needed)."""
    vault_db.init_db(silent=True)


# ===========================================
# Source Management
# ===========================================

def add_source(path: str, label: str, category: str, priority: int = 10) -> int:
    """Add a photo source. Returns source ID."""
    return vault_db.add_photo_source(path, label, category, priority)


def get_source(label: str) -> Optional[Dict]:
    """Get a source by label."""
    return vault_db.get_photo_source(label)


def list_sources() -> List[Dict]:
    """List all enabled photo sources."""
    return vault_db.list_photo_sources(enabled_only=True)


def remove_source(label: str) -> bool:
    """Remove a source by label."""
    return vault_db.remove_photo_source(label)


# ===========================================
# Photo Management
# ===========================================

def add_photo(
    source_id: int,
    file_path: str,
    file_name: str,
    category: str,
    file_size: Optional[int] = None,
    sha256_hash: Optional[str] = None,
    is_screenshot: bool = False,
    screenshot_confidence: Optional[float] = None,
    file_modified_at: Optional[str] = None,
) -> int:
    """Add a photo. Returns photo ID."""
    return vault_db.add_photo(
        source_id=source_id,
        file_path=file_path,
        file_name=file_name,
        category=category,
        file_size=file_size,
        sha256_hash=sha256_hash,
        is_screenshot=is_screenshot,
        screenshot_confidence=screenshot_confidence,
        file_modified_at=file_modified_at,
    )


def get_photo_by_path(file_path: str) -> Optional[Dict]:
    """Get a photo by file path."""
    return vault_db.get_photo_by_path(file_path)


def update_photo(
    photo_id: int,
    sha256_hash: Optional[str] = None,
    is_screenshot: Optional[bool] = None,
    screenshot_confidence: Optional[float] = None,
    file_modified_at: Optional[str] = None,
) -> bool:
    """Update a photo."""
    return vault_db.update_photo(
        photo_id=photo_id,
        sha256_hash=sha256_hash,
        is_screenshot=is_screenshot,
        screenshot_confidence=screenshot_confidence,
        file_modified_at=file_modified_at,
    )


def delete_photo(photo_id: int) -> bool:
    """Delete a photo."""
    return vault_db.delete_photo(photo_id)


def list_photos(
    source_id: Optional[int] = None,
    category: Optional[str] = None,
    screenshots_only: bool = False,
    limit: int = 100,
) -> List[Dict]:
    """List photos with filtering."""
    return vault_db.list_photos(
        source_id=source_id,
        category=category,
        screenshots_only=screenshots_only,
        limit=limit,
    )


def get_photos_by_source(source_id: int) -> List[str]:
    """Get all file paths for a source."""
    return vault_db.get_photos_by_source(source_id)


def delete_missing_photos(source_id: int, valid_paths: List[str]) -> int:
    """Delete photos not in the valid paths list."""
    return vault_db.delete_photos_not_in_paths(source_id, valid_paths)


# ===========================================
# Metadata
# ===========================================

def add_metadata(
    photo_id: int,
    width: Optional[int] = None,
    height: Optional[int] = None,
    date_taken: Optional[str] = None,
    camera_make: Optional[str] = None,
    camera_model: Optional[str] = None,
    gps_lat: Optional[float] = None,
    gps_lon: Optional[float] = None,
    orientation: Optional[int] = None,
    raw_exif: Optional[str] = None,
) -> None:
    """Add or replace metadata for a photo."""
    vault_db.add_photo_metadata(
        photo_id=photo_id,
        width=width,
        height=height,
        date_taken=date_taken,
        camera_make=camera_make,
        camera_model=camera_model,
        gps_lat=gps_lat,
        gps_lon=gps_lon,
        orientation=orientation,
        raw_exif=raw_exif,
    )


def get_metadata(photo_id: int) -> Optional[Dict]:
    """Get metadata for a photo."""
    return vault_db.get_photo_metadata(photo_id)


# ===========================================
# Analysis
# ===========================================

def add_analysis(
    photo_id: int,
    description: Optional[str] = None,
    keywords: Optional[str] = None,
    provider: Optional[str] = None,
    model: Optional[str] = None,
) -> None:
    """Add or replace AI analysis for a photo."""
    vault_db.add_photo_analysis(
        photo_id=photo_id,
        description=description,
        keywords=keywords,
        provider=provider,
        model=model,
    )


def get_analysis(photo_id: int) -> Optional[Dict]:
    """Get AI analysis for a photo."""
    return vault_db.get_photo_analysis(photo_id)


def get_unanalyzed_photos(limit: Optional[int] = None) -> List[Dict]:
    """Get photos that haven't been analyzed."""
    return vault_db.get_unanalyzed_photos(limit)


def search_descriptions(query: str) -> List[Dict]:
    """Search photo descriptions."""
    return vault_db.search_photos(query)


# ===========================================
# Duplicates
# ===========================================

def get_duplicate_groups() -> List[List[Dict]]:
    """Get groups of duplicate photos (same hash)."""
    return vault_db.get_photo_duplicate_groups()


# ===========================================
# Statistics
# ===========================================

def get_stats() -> Dict[str, Any]:
    """Get photo statistics."""
    return vault_db.get_photo_stats()


# ===========================================
# Exclusions
# ===========================================

def add_exclusion(path: str, reason: Optional[str] = None) -> int:
    """Add a path to the exclusion list."""
    return vault_db.add_photo_exclusion(path, reason)


def remove_exclusion(path: str) -> bool:
    """Remove a path from the exclusion list."""
    return vault_db.remove_photo_exclusion(path)


def list_exclusions() -> List[Dict]:
    """List all exclusion paths."""
    return vault_db.list_photo_exclusions()


def is_excluded(path: str) -> bool:
    """Check if a path is excluded."""
    return vault_db.is_path_excluded(path)


def add_default_exclusions() -> int:
    """Add default system exclusions."""
    return vault_db.add_default_exclusions()


# ===========================================
# Scan State
# ===========================================

def set_drive_scanned(drive: str) -> None:
    """Mark a drive as scanned."""
    vault_db.set_drive_scanned(drive)


def get_scanned_drives() -> List[Dict]:
    """Get list of scanned drives."""
    return vault_db.get_scanned_drives()


def is_drive_scanned(drive: str) -> bool:
    """Check if a drive has been scanned."""
    return vault_db.is_drive_scanned(drive)
