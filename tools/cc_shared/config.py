"""Shared configuration for cc-director.

Configuration is stored in the cc-director config directory.
All path resolution is delegated to cc_storage.CcStorage.
"""

import json
import logging
import os
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional

logger = logging.getLogger(__name__)

# Import cc_storage for centralized path resolution
try:
    from cc_storage import CcStorage
except ImportError:
    # Allow standalone usage when cc_storage is not installed
    import sys
    _tools_dir = str(Path(__file__).resolve().parent.parent)
    if _tools_dir not in sys.path:
        sys.path.insert(0, _tools_dir)
    from cc_storage import CcStorage


def get_data_dir() -> Path:
    """Get the cc-director config directory.

    Delegates to CcStorage.config() for centralized path resolution.
    Legacy callers that used this for tool data storage will now get
    the unified config directory.

    Returns:
        Path to the config directory
    """
    return CcStorage.config()


def get_install_dir() -> Path:
    """Get the cc-director installation directory (where executables live).

    Returns:
        Path to the bin directory containing cc-director executables.
    """
    return CcStorage.bin()


def get_config_path() -> Path:
    """Get the path to the cc-director config file."""
    return CcStorage.config_json()


def ensure_config_dir() -> Path:
    """Ensure the config directory exists and return the config path."""
    CcStorage.ensure(CcStorage.config())
    return get_config_path()


@dataclass
class OpenAIProviderConfig:
    """OpenAI provider configuration."""
    api_key_env: str = "OPENAI_API_KEY"
    default_model: str = "gpt-4o-mini"
    vision_model: str = "gpt-4o"


@dataclass
class ClaudeCodeProviderConfig:
    """Claude Code provider configuration."""
    enabled: bool = True


@dataclass
class LLMProvidersConfig:
    """LLM providers configuration."""
    openai: OpenAIProviderConfig = field(default_factory=OpenAIProviderConfig)
    claude_code: ClaudeCodeProviderConfig = field(default_factory=ClaudeCodeProviderConfig)


@dataclass
class LLMConfig:
    """LLM configuration."""
    default_provider: str = "claude_code"
    providers: LLMProvidersConfig = field(default_factory=LLMProvidersConfig)


@dataclass
class PhotoSource:
    """A photo source directory."""
    path: str
    category: str  # 'private', 'work', 'other'
    label: str
    priority: int = 10  # Lower = higher priority

    def to_dict(self) -> Dict[str, Any]:
        return {
            "path": self.path,
            "category": self.category,
            "label": self.label,
            "priority": self.priority,
        }

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "PhotoSource":
        return cls(
            path=data["path"],
            category=data["category"],
            label=data["label"],
            priority=data.get("priority", 10),
        )


def _default_vault_path() -> str:
    """Compute the default vault path.

    Delegates to CcStorage.vault() for centralized path resolution.
    """
    return str(CcStorage.vault()).replace("\\", "/")


@dataclass
class VaultConfig:
    """Vault configuration."""
    vault_path: str = ""

    def __post_init__(self):
        if not self.vault_path:
            self.vault_path = _default_vault_path()

    def to_dict(self) -> Dict[str, Any]:
        return {"vault_path": self.vault_path}

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "VaultConfig":
        return cls(vault_path=data.get("vault_path", _default_vault_path()))


def _default_screenshots_path() -> str:
    """Compute the default screenshots directory path.

    Checks the Windows Snipping Tool / Screenshots folder location.
    Falls back to Pictures/Screenshots under the user profile.
    """
    # macOS drops screenshots on the Desktop by default. (USERPROFILE/OneDrive below are
    # Windows-only, so without this branch a Mac falls through to "" - the Settings page's
    # explicit override is still the reliable path on any platform.)
    if sys.platform == "darwin":
        return os.path.join(os.path.expanduser("~"), "Desktop").replace("\\", "/")

    # Check common Windows screenshot locations
    user_profile = os.environ.get("USERPROFILE", "")

    # Check all OneDrive variants (consumer, commercial, generic)
    for env_var in ("OneDriveConsumer", "OneDrive", "OneDriveCommercial"):
        onedrive = os.environ.get(env_var, "")
        if onedrive:
            candidate = os.path.join(onedrive, "Pictures", "Screenshots")
            if os.path.isdir(candidate):
                return candidate.replace("\\", "/")

    # Local Pictures/Screenshots
    if user_profile:
        candidate = os.path.join(user_profile, "Pictures", "Screenshots")
        if os.path.isdir(candidate):
            return candidate.replace("\\", "/")

    # Fallback: just return the typical path (may not exist yet)
    if user_profile:
        return os.path.join(user_profile, "Pictures", "Screenshots").replace("\\", "/")
    return ""


@dataclass
class ScreenshotsConfig:
    """Screenshots configuration."""
    source_directory: str = ""

    def __post_init__(self):
        if not self.source_directory:
            self.source_directory = _default_screenshots_path()

    def get_source_directory(self) -> Path:
        """Get the source directory as a Path object."""
        return Path(self.source_directory)

    def to_dict(self) -> Dict[str, Any]:
        return {"source_directory": self.source_directory}

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "ScreenshotsConfig":
        return cls(
            source_directory=data.get("source_directory", _default_screenshots_path()),
        )


@dataclass
class GatewayConfig:
    """Director-to-Gateway connection settings.

    Mirrors the C# CcDirector.Core.Configuration.GatewayConfig. The JSON keys are
    intentionally camelCase (url, token, tailnetEndpoint) to match what the C# reader and
    the Settings REST API write, so dotted CLI keys like `gateway.tailnetEndpoint` line up
    with the on-disk keys. Empty url means the Director runs local-only (no registration).
    """
    url: str = ""
    token: str = ""
    tailnetEndpoint: str = ""  # noqa: N815 - matches the on-disk JSON key, not Python style

    def is_empty(self) -> bool:
        return not (self.url or self.token or self.tailnetEndpoint)

    def to_dict(self) -> Dict[str, Any]:
        return {
            "url": self.url,
            "token": self.token,
            "tailnetEndpoint": self.tailnetEndpoint,
        }

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "GatewayConfig":
        return cls(
            url=data.get("url", ""),
            token=data.get("token", ""),
            tailnetEndpoint=data.get("tailnetEndpoint", ""),
        )


@dataclass
class SendFromAccount:
    """An email send-from account."""
    email: str
    tool: str  # "cc-outlook" or "cc-gmail"
    tool_account: Optional[str] = None  # e.g. "personal", "consulting"

    def to_dict(self) -> Dict[str, Any]:
        return {
            "email": self.email,
            "tool": self.tool,
            "tool_account": self.tool_account,
        }

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "SendFromAccount":
        return cls(
            email=data["email"],
            tool=data["tool"],
            tool_account=data.get("tool_account"),
        )


def _default_comm_manager_path() -> str:
    """Compute the default Communication Manager content path.

    Delegates to CcStorage.tool_config("comm-queue") for centralized path resolution.
    """
    return str(CcStorage.tool_config("comm-queue")).replace("\\", "/")


@dataclass
class CommManagerConfig:
    """Communication Manager configuration."""
    queue_path: str = ""

    def __post_init__(self):
        if not self.queue_path:
            self.queue_path = _default_comm_manager_path()
    default_persona: str = "personal"
    default_created_by: str = "claude_code"
    send_from_accounts: Dict[str, SendFromAccount] = field(default_factory=dict)

    def get_queue_path(self) -> Path:
        """Get the queue path as a Path object."""
        return Path(self.queue_path)

    def get_pending_path(self) -> Path:
        """Get the pending_review directory path."""
        return self.get_queue_path() / "pending_review"

    def get_approved_path(self) -> Path:
        """Get the approved directory path."""
        return self.get_queue_path() / "approved"

    def get_rejected_path(self) -> Path:
        """Get the rejected directory path."""
        return self.get_queue_path() / "rejected"

    def get_posted_path(self) -> Path:
        """Get the posted directory path."""
        return self.get_queue_path() / "posted"

    def get_valid_account_names(self) -> List[str]:
        """Get list of valid send-from account names."""
        return list(self.send_from_accounts.keys())

    def get_account_email(self, name: str) -> Optional[str]:
        """Get the email address for a send-from account name."""
        account = self.send_from_accounts.get(name)
        return account.email if account else None

    def get_account(self, name: str) -> Optional[SendFromAccount]:
        """Get a send-from account by name."""
        return self.send_from_accounts.get(name)

    def to_dict(self) -> Dict[str, Any]:
        result: Dict[str, Any] = {
            "queue_path": self.queue_path,
            "default_persona": self.default_persona,
            "default_created_by": self.default_created_by,
        }
        if self.send_from_accounts:
            result["send_from_accounts"] = {
                name: acct.to_dict()
                for name, acct in self.send_from_accounts.items()
            }
        return result

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "CommManagerConfig":
        accounts = {}
        for name, acct_data in data.get("send_from_accounts", {}).items():
            accounts[name] = SendFromAccount.from_dict(acct_data)
        return cls(
            queue_path=data.get("queue_path", _default_comm_manager_path()),
            default_persona=data.get("default_persona", "personal"),
            default_created_by=data.get("default_created_by", "claude_code"),
            send_from_accounts=accounts,
        )


def _default_photos_db_path() -> str:
    """Compute the default photos database path.

    Delegates to CcStorage.tool_config("photos") for centralized path resolution.
    """
    return str(CcStorage.tool_config("photos") / "photos.db").replace("\\", "/")


@dataclass
class PhotosConfig:
    """Photos tool configuration."""
    database_path: str = ""
    sources: List[PhotoSource] = field(default_factory=list)

    def __post_init__(self):
        if not self.database_path:
            self.database_path = _default_photos_db_path()

    def get_database_path(self) -> Path:
        """Get the expanded database path."""
        return Path(os.path.expanduser(self.database_path))

    def to_dict(self) -> Dict[str, Any]:
        return {
            "database_path": self.database_path,
            "sources": [s.to_dict() for s in self.sources],
        }

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "PhotosConfig":
        sources = [PhotoSource.from_dict(s) for s in data.get("sources", [])]
        return cls(
            database_path=data.get("database_path", _default_photos_db_path()),
            sources=sources,
        )


def _deep_merge(base: Dict[str, Any], overlay: Dict[str, Any]) -> Dict[str, Any]:
    """Recursively merge overlay into a copy of base.

    Where both sides hold a dict at the same key, merge recursively; otherwise the overlay
    value wins. Keys present only in base are preserved at every level. Used by save() so the
    known (overlay) sections override on disk while unknown (base-only) keys are kept.
    """
    result = dict(base)
    for key, value in overlay.items():
        if isinstance(value, dict) and isinstance(result.get(key), dict):
            result[key] = _deep_merge(result[key], value)
        else:
            result[key] = value
    return result


class CCDirectorConfig:
    """Main configuration class for cc-director."""

    def __init__(self):
        self.llm = LLMConfig()
        self.photos = PhotosConfig()
        self.vault = VaultConfig()
        self.comm_manager = CommManagerConfig()
        self.screenshots = ScreenshotsConfig()
        self.gateway = GatewayConfig()
        self._config_path = get_config_path()

    def load(self) -> "CCDirectorConfig":
        """Load configuration from file."""
        if self._config_path.exists():
            try:
                with open(self._config_path, "r", encoding="utf-8") as f:
                    data = json.load(f)
                self._load_from_dict(data)
            except (json.JSONDecodeError, KeyError, TypeError) as e:
                # Config file corrupted, use defaults
                logger.warning("Config file corrupted, using defaults: %s", e)
        return self

    def _load_from_dict(self, data: Dict[str, Any]) -> None:
        """Load configuration from dictionary."""
        # Load LLM config
        if "llm" in data:
            llm_data = data["llm"]
            self.llm.default_provider = llm_data.get("default_provider", "claude_code")

            if "providers" in llm_data:
                providers = llm_data["providers"]
                if "openai" in providers:
                    openai = providers["openai"]
                    self.llm.providers.openai = OpenAIProviderConfig(
                        api_key_env=openai.get("api_key_env", "OPENAI_API_KEY"),
                        default_model=openai.get("default_model", "gpt-4o-mini"),
                        vision_model=openai.get("vision_model", "gpt-4o"),
                    )
                if "claude_code" in providers:
                    claude = providers["claude_code"]
                    self.llm.providers.claude_code = ClaudeCodeProviderConfig(
                        enabled=claude.get("enabled", True),
                    )

        # Load photos config
        if "photos" in data:
            self.photos = PhotosConfig.from_dict(data["photos"])

        # Load vault config
        if "vault" in data:
            self.vault = VaultConfig.from_dict(data["vault"])

        # Load comm_manager config
        if "comm_manager" in data:
            self.comm_manager = CommManagerConfig.from_dict(data["comm_manager"])

        # Load screenshots config
        if "screenshots" in data:
            self.screenshots = ScreenshotsConfig.from_dict(data["screenshots"])

        # Load gateway config
        if "gateway" in data:
            self.gateway = GatewayConfig.from_dict(data["gateway"])

    def save(self) -> None:
        """Save configuration to file, preserving sections this schema doesn't model.

        config.json is shared with the C# app and may hold sections (or extra keys inside a
        known section) that this dataclass doesn't know about. Writing to_dict() verbatim
        would silently DROP them. Instead we deep-merge the known sections over whatever is
        currently on disk, so unknown keys survive. A corrupt on-disk file is NOT clobbered -
        we raise, matching the C# writer's refuse-to-destroy-recoverable-data rule.
        """
        ensure_config_dir()
        known = self.to_dict()

        existing: Dict[str, Any] = {}
        if self._config_path.exists():
            text = self._config_path.read_text(encoding="utf-8")
            if text.strip():
                existing = json.loads(text)  # raises on corrupt JSON - intentional, no clobber
                if not isinstance(existing, dict):
                    raise ValueError(f"config.json root is not a JSON object: {self._config_path}")

        merged = _deep_merge(existing, known)
        with open(self._config_path, "w", encoding="utf-8") as f:
            json.dump(merged, f, indent=2)

    def to_dict(self) -> Dict[str, Any]:
        """Convert configuration to dictionary."""
        result: Dict[str, Any] = {
            "llm": {
                "default_provider": self.llm.default_provider,
                "providers": {
                    "openai": {
                        "api_key_env": self.llm.providers.openai.api_key_env,
                        "default_model": self.llm.providers.openai.default_model,
                        "vision_model": self.llm.providers.openai.vision_model,
                    },
                    "claude_code": {
                        "enabled": self.llm.providers.claude_code.enabled,
                    },
                },
            },
            "photos": self.photos.to_dict(),
            "vault": self.vault.to_dict(),
            "comm_manager": self.comm_manager.to_dict(),
            "screenshots": self.screenshots.to_dict(),
        }
        # Only emit the gateway block once it has content, so we don't inject an empty
        # gateway section into configs that never had one.
        if not self.gateway.is_empty():
            result["gateway"] = self.gateway.to_dict()
        return result

    def add_photo_source(self, path: str, category: str, label: str, priority: int = 10) -> PhotoSource:
        """Add a photo source."""
        source = PhotoSource(path=path, category=category, label=label, priority=priority)
        # Remove existing source with same label
        self.photos.sources = [s for s in self.photos.sources if s.label != label]
        self.photos.sources.append(source)
        # Sort by priority
        self.photos.sources.sort(key=lambda s: s.priority)
        return source

    def remove_photo_source(self, label: str) -> bool:
        """Remove a photo source by label. Returns True if found and removed."""
        original_len = len(self.photos.sources)
        self.photos.sources = [s for s in self.photos.sources if s.label != label]
        return len(self.photos.sources) < original_len

    def get_photo_source(self, label: str) -> Optional[PhotoSource]:
        """Get a photo source by label."""
        for source in self.photos.sources:
            if source.label == label:
                return source
        return None


# Global config instance
_config: Optional[CCDirectorConfig] = None


def get_config() -> CCDirectorConfig:
    """Get the global configuration instance."""
    global _config
    if _config is None:
        _config = CCDirectorConfig().load()
    return _config


def reload_config() -> CCDirectorConfig:
    """Reload configuration from file."""
    global _config
    _config = CCDirectorConfig().load()
    return _config
