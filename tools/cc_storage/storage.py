"""Centralized storage path resolution for cc-director.

All storage paths are resolved through this module. Tools and apps call
CcStorage methods instead of computing paths themselves.

Storage categories:
    vault   - Personal data: contacts, docs, tasks, goals, health, vectors
    config  - Tool settings, app state (per Director instance when a root is pinned)
    user config - Credentials and OAuth tokens: one per operating-system user,
                  never redirected by CC_DIRECTOR_ROOT (see user_config)
    output  - Generated files: PDFs, reports, transcripts, exports
    logs    - All application and tool logs
    bin     - Installed executables (tool binaries)

Environment variable overrides:
    CC_DIRECTOR_ROOT - Override the base directory (default: %LOCALAPPDATA%/cc-director)
    CC_VAULT_PATH    - Override the vault directory specifically
"""

import os
import sys
from pathlib import Path


class CcStorage:
    """Single source of truth for all cc-director storage paths."""

    # -- Root categories --

    @staticmethod
    def _base() -> Path:
        """Base directory for cc-director local app data.

        Must resolve to the SAME directory the Director itself uses, or the
        Python tools silently read an empty world beside the real one. On
        Windows that is %LOCALAPPDATA%/cc-director. On macOS the Director
        lives in ~/Library/Application Support/cc-director - before this
        branch existed the tools fell through to ~/.cc-director there and
        could not see the Director's config (gateway address and token
        included), which is how 'cc-devthrottle email owner' came to report
        an unreachable Gateway on a fully enrolled Mac.
        """
        override = os.environ.get("CC_DIRECTOR_ROOT")
        if override:
            return Path(override)
        return CcStorage._user_base()

    @staticmethod
    def _user_base() -> Path:
        """Base directory for this operating-system user, with NO root override applied.

        The same platform rules as _base(), minus CC_DIRECTOR_ROOT. Paths that
        belong to the USER rather than to a Director instance resolve from here,
        so they land in one place whether the caller inherited a Director's
        CC_DIRECTOR_ROOT or was started from a plain terminal.
        """
        local = os.environ.get("LOCALAPPDATA")
        if local:
            return Path(local) / "cc-director"
        if sys.platform == "darwin":
            return Path.home() / "Library" / "Application Support" / "cc-director"
        return Path.home() / ".cc-director"

    @staticmethod
    def vault() -> Path:
        """Personal data: vault.db, vectors, documents, health, media."""
        override = os.environ.get("CC_VAULT_PATH")
        if override:
            return Path(override)
        return CcStorage._base() / "vault"

    @staticmethod
    def config() -> Path:
        """Tool settings, OAuth tokens, credentials, app state."""
        return CcStorage._base() / "config"

    @staticmethod
    def output() -> Path:
        """Generated files: PDFs, reports, transcripts, exports."""
        docs = os.environ.get("USERPROFILE")
        if docs:
            return Path(docs) / "Documents" / "cc-director"
        return Path.home() / "Documents" / "cc-director"

    @staticmethod
    def logs() -> Path:
        """All application and tool logs."""
        return CcStorage._base() / "logs"

    @staticmethod
    def bin() -> Path:
        """Installed executables (tool binaries)."""
        local = os.environ.get("LOCALAPPDATA")
        if local:
            return Path(local) / "cc-director" / "bin"
        return Path.home() / ".cc-director" / "bin"

    # -- Tool-specific shortcuts --

    @staticmethod
    def tool_config(tool: str) -> Path:
        """Config directory for a specific tool: config/{tool}/"""
        return CcStorage.config() / tool

    @staticmethod
    def user_config() -> Path:
        """Config that belongs to the operating-system USER, not to a Director instance.

        Deliberately resolved from _user_base(), so CC_DIRECTOR_ROOT does NOT
        redirect it. A Director sets CC_DIRECTOR_ROOT to its instance home for
        every session it runs, while the owner runs the same tool from a plain
        terminal with no such variable. Anything the owner sets up ONCE for the
        machine - an OAuth token, a credential - has to be found by both, or
        there are two stores and authenticating in one cannot fix the other
        (issue #3011: cc-gmail kept a token per store and told the user to run
        an 'auth' that could never reach the store the session was reading).

        This mirrors CcStorage.SecretsStore() on the C# side and
        tools/cc-secrets/src/paths.py, which are per-user for the same reason.

        Use config() for anything that is genuinely per-instance.
        """
        return CcStorage._user_base() / "config"

    @staticmethod
    def user_tool_config(tool: str) -> Path:
        """Per-user config directory for a specific tool: <user base>/config/{tool}/"""
        return CcStorage.user_config() / tool

    @staticmethod
    def instance_homes() -> list:
        """Every named Director instance home under this user's base: <user base>/instances/*.

        A tool that has moved a store out from under CC_DIRECTOR_ROOT needs to
        see what its older self left behind in each instance home, so it can
        adopt it instead of asking the user to set it up again. Returns an empty
        list when there are no instances.
        """
        instances = CcStorage._user_base() / "instances"
        if not instances.is_dir():
            return []
        return sorted((path for path in instances.iterdir() if path.is_dir()), key=lambda p: p.name)

    @staticmethod
    def tool_output(tool: str) -> Path:
        """Output directory for a specific tool: output/{tool}/"""
        return CcStorage.output() / tool

    @staticmethod
    def tool_logs(tool: str) -> Path:
        """Log directory for a specific tool: logs/{tool}/"""
        return CcStorage.logs() / tool

    # -- Vault subdirectories --

    @staticmethod
    def vault_db() -> Path:
        """Main personal data database: vault/vault.db"""
        return CcStorage.vault() / "vault.db"

    @staticmethod
    def engine_db() -> Path:
        """Job scheduler state database: vault/engine.db"""
        return CcStorage.vault() / "engine.db"

    @staticmethod
    def vault_documents() -> Path:
        """Imported files: vault/documents/"""
        return CcStorage.vault() / "documents"

    @staticmethod
    def vault_vectors() -> Path:
        """Embeddings: vault/vectors/"""
        return CcStorage.vault() / "vectors"

    @staticmethod
    def vault_media() -> Path:
        """Media files: vault/media/"""
        return CcStorage.vault() / "media"

    @staticmethod
    def vault_health() -> Path:
        """Health data: vault/health/"""
        return CcStorage.vault() / "health"

    @staticmethod
    def vault_backups() -> Path:
        """Backup files: vault/backups/"""
        return CcStorage.vault() / "backups"

    @staticmethod
    def vault_imports() -> Path:
        """Staging for ingest: vault/imports/"""
        return CcStorage.vault() / "imports"

    # -- Config shortcuts --

    @staticmethod
    def config_json() -> Path:
        """Shared settings file: config/config.json"""
        return CcStorage.config() / "config.json"

    @staticmethod
    def comm_queue_db() -> Path:
        """Communication queue database: config/comm-queue/communications.db"""
        return CcStorage.tool_config("comm-queue") / "communications.db"

    # -- Output shortcuts --

    @staticmethod
    def output_reports() -> Path:
        """Generated PDFs and DOCX: output/reports/"""
        return CcStorage.output() / "reports"

    @staticmethod
    def output_transcripts() -> Path:
        """Whisper/transcribe output: output/transcripts/"""
        return CcStorage.output() / "transcripts"

    @staticmethod
    def output_screenshots() -> Path:
        """cc-trisight captures: output/screenshots/"""
        return CcStorage.output() / "screenshots"

    @staticmethod
    def output_diagrams() -> Path:
        """cc-docgen C4 diagrams: output/diagrams/"""
        return CcStorage.output() / "diagrams"

    # -- Utilities --

    @staticmethod
    def ensure(path: Path) -> Path:
        """Create directory if it doesn't exist and return the path."""
        path.mkdir(parents=True, exist_ok=True)
        return path
