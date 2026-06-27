"""
Vault Configuration Module

Central configuration for the Vault 2.0 personal data platform.
All path resolution is delegated to cc_storage.CcStorage.
"""

import json
import os
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import List, Optional

# Import cc_storage for centralized path resolution
try:
    from cc_storage import CcStorage
except ImportError:
    _tools_dir = str(Path(__file__).resolve().parent.parent.parent)
    if _tools_dir not in sys.path:
        sys.path.insert(0, _tools_dir)
    from cc_storage import CcStorage


@dataclass
class VaultConfig:
    """Vault configuration settings."""
    vault_path: Path
    db_path: Path
    vectors_path: Path
    documents_path: Path
    transcripts_path: Path
    notes_path: Path
    journals_path: Path
    research_path: Path
    health_path: Path
    media_path: Path
    screenshots_path: Path
    images_path: Path
    audio_path: Path
    imports_path: Path
    backups_path: Path


def get_config_dir() -> Path:
    """Get the config directory for cc-vault."""
    return CcStorage.tool_config("vault")


def get_config_file() -> Path:
    """Get the config file path."""
    return get_config_dir() / "config.json"


def get_vault_path() -> Path:
    """Resolve the vault path.

    Precedence:
      1. CC_VAULT_PATH environment variable (highest).
      2. The persisted vault override written by `cc-vault init <path>`
         (tool config: config/vault/config.json -> "vault_path").
      3. The default location from CcStorage.vault().

    A corrupt override file is NOT silently ignored -- it raises so the user can
    fix it, rather than quietly redirecting the vault to the default and hiding
    their data.
    """
    override = os.environ.get("CC_VAULT_PATH")
    if override:
        return Path(override)

    config_file = get_config_file()
    if config_file.exists():
        with open(config_file, "r", encoding="utf-8") as f:
            data = json.load(f)  # raises on corrupt JSON - intentional, no silent fallback
        vault_path = data.get("vault_path")
        if vault_path:
            return Path(vault_path)

    return CcStorage.vault()


def get_config() -> VaultConfig:
    """Get the full vault configuration."""
    vault_path = get_vault_path()

    return VaultConfig(
        vault_path=vault_path,
        db_path=vault_path / "vault.db",
        vectors_path=vault_path / "vectors",
        documents_path=vault_path / "documents",
        transcripts_path=vault_path / "documents" / "transcripts",
        notes_path=vault_path / "documents" / "notes",
        journals_path=vault_path / "documents" / "journals",
        research_path=vault_path / "documents" / "research",
        health_path=vault_path / "health",
        media_path=vault_path / "media",
        screenshots_path=vault_path / "media" / "screenshots",
        images_path=vault_path / "media" / "images",
        audio_path=vault_path / "media" / "audio",
        imports_path=vault_path / "imports",
        backups_path=vault_path / "backups",
    )


def save_config(vault_path: Optional[str] = None) -> None:
    """Save configuration to config file."""
    config_dir = get_config_dir()
    config_dir.mkdir(parents=True, exist_ok=True)

    config_file = get_config_file()
    config = {}

    # Load existing config if present
    if config_file.exists():
        try:
            with open(config_file, 'r', encoding='utf-8') as f:
                config = json.load(f)
        except json.JSONDecodeError as e:
            import logging
            logging.getLogger(__name__).warning(f"Invalid config file, starting fresh: {e}")
        except IOError as e:
            import logging
            logging.getLogger(__name__).warning(f"Could not read config file: {e}")

    # Update vault_path if provided
    if vault_path:
        config['vault_path'] = str(Path(vault_path).resolve())

    with open(config_file, 'w', encoding='utf-8') as f:
        json.dump(config, f, indent=2)


# Module-level configuration (for backwards compatibility)
_config = get_config()

VAULT_PATH = _config.vault_path
DB_PATH = _config.db_path
VECTORS_PATH = _config.vectors_path
DOCUMENTS_PATH = _config.documents_path
TRANSCRIPTS_PATH = _config.transcripts_path
NOTES_PATH = _config.notes_path
JOURNALS_PATH = _config.journals_path
RESEARCH_PATH = _config.research_path
HEALTH_PATH = _config.health_path
MEDIA_PATH = _config.media_path
SCREENSHOTS_PATH = _config.screenshots_path
IMAGES_PATH = _config.images_path
AUDIO_PATH = _config.audio_path
IMPORTS_PATH = _config.imports_path
BACKUPS_PATH = _config.backups_path

# OpenAI embedding config
EMBEDDING_MODEL = "text-embedding-3-small"
EMBEDDING_DIMENSIONS = 1536
OPENAI_API_KEY = os.environ.get("OPENAI_API_KEY")

# Chunking configuration
CHUNK_MAX_TOKENS = 400       # Maximum tokens per chunk
CHUNK_OVERLAP_TOKENS = 80    # Token overlap between chunks
CHUNK_MIN_TOKENS = 50        # Minimum chunk size
CHUNK_THRESHOLD_TOKENS = 500 # Documents above this size get chunked

# Hybrid search weights
HYBRID_VECTOR_WEIGHT = 0.7   # Weight for vector (semantic) search
HYBRID_TEXT_WEIGHT = 0.3     # Weight for BM25 (keyword) search

# Vector collections (stored in SQLite vec_embeddings table)
VECTOR_COLLECTIONS = {
    "documents": "Transcripts, notes, journals, research documents",
    "chunks": "Document chunks for hybrid search",
    "facts": "Knowledge base - facts and memories about contacts",
    "health": "Health data summaries",
    "ideas": "Ideas for similarity search",
    "catalog": "Document catalog summaries for library search",
}

# Document types
DOCUMENT_TYPES = ["transcript", "note", "journal", "research"]

# Entity types for linking
ENTITY_TYPES = ["contact", "task", "goal", "idea", "document", "fact", "health", "photo", "social_post", "catalog_entry"]

# Catalog: file extensions that support text extraction and summarization
CATALOG_SUMMARIZABLE_EXTENSIONS = {
    '.pdf', '.docx', '.pptx', '.xlsx',
    '.txt', '.md', '.csv', '.sql', '.json', '.xml',
    '.html', '.htm',
}

# Catalog: file extensions tracked as metadata-only (no text extraction)
CATALOG_METADATA_ONLY_EXTENSIONS = {
    '.doc', '.svg', '.png', '.jpg', '.jpeg', '.gif',
    '.zip', '.pfx', '.pem', '.cer', '.crt',
    '.vsd', '.msg',
}

# Photo categories
PHOTO_CATEGORIES = ["private", "work", "other"]


def ensure_directories() -> bool:
    """Create all vault directories if they don't exist."""
    config = get_config()
    directories = [
        config.vault_path,
        config.documents_path,
        config.transcripts_path,
        config.notes_path,
        config.journals_path,
        config.research_path,
        config.health_path,
        config.health_path / "daily",
        config.health_path / "sleep",
        config.health_path / "workouts",
        config.media_path,
        config.screenshots_path,
        config.images_path,
        config.audio_path,
        config.imports_path,
        config.backups_path,
    ]

    for directory in directories:
        directory.mkdir(parents=True, exist_ok=True)

    return True


def get_document_path(doc_type: str) -> Path:
    """Get the appropriate path for a document type."""
    config = get_config()
    type_paths = {
        "transcript": config.transcripts_path,
        "note": config.notes_path,
        "journal": config.journals_path,
        "research": config.research_path,
    }
    return type_paths.get(doc_type, config.documents_path)


def validate_config() -> List[str]:
    """Validate configuration and return any issues."""
    issues: List[str] = []

    if not OPENAI_API_KEY:
        issues.append("OPENAI_API_KEY environment variable not set (required for embeddings)")

    if not VAULT_PATH.exists():
        issues.append(f"Vault directory does not exist: {VAULT_PATH}")

    return issues
