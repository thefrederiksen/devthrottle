# cc_shared

Shared configuration and LLM abstraction library for the cc-director suite.

This is **not a CLI tool** - it is a Python library imported by all other cc-director Python packages.

## What It Provides

### Configuration Management (`config.py`)

Centralized configuration for the entire cc-director suite. All Python tools import `get_config()` to access settings.

**Data directory resolution (in priority order):**

1. `CC_TOOLS_DATA` environment variable (if set)
2. `%LOCALAPPDATA%\cc-director\data` (preferred, no admin needed)
3. `C:\cc-director\data` (legacy, backward compat)
4. `~/.cc-director/` (final fallback)

**Config file:** `<data_dir>/config.json`

**Config sections:**

| Section | Purpose | Key Settings |
|---------|---------|-------------|
| `llm` | LLM provider defaults | default_provider, model names, API key env var |
| `photos` | Photo organization | database_path, source directories |
| `vault` | Credential storage | vault_path |
| `comm_manager` | Communication queue | queue_path, default_persona |

**Usage:**

```python
from cc_shared import get_config

config = get_config()
print(config.llm.default_provider)      # "claude_code"
print(config.vault.vault_path)           # resolved path
print(config.photos.get_database_path()) # expanded Path object
```

### LLM Provider Abstraction (`llm.py`)

Pluggable LLM providers so tools can switch between OpenAI and Claude Code without code changes.

**Providers:**

| Provider | Name | Requires |
|----------|------|----------|
| OpenAI | `openai` | OPENAI_API_KEY env var |
| Claude Code CLI | `claude_code` | Claude Code installed and authenticated |

**Capabilities:**

| Method | Purpose |
|--------|---------|
| `describe_image(path)` | Get description of an image |
| `extract_text(path)` | OCR - extract text from an image |
| `generate_text(prompt)` | Generate text from a prompt |

**Usage:**

```python
from cc_shared import get_llm_provider

provider = get_llm_provider()          # uses default from config
provider = get_llm_provider("openai")  # explicit provider

description = provider.describe_image(Path("photo.jpg"))
text = provider.extract_text(Path("screenshot.png"))
```

## What It Does NOT Do

- It is not a CLI tool - no executable, no command-line interface
- It does not manage API keys directly - it reads them from environment variables
- It does not install dependencies for other tools
- It does not handle authentication flows (OAuth, etc.)

## Configuration File Format

```json
{
  "llm": {
    "default_provider": "claude_code",
    "providers": {
      "openai": {
        "api_key_env": "OPENAI_API_KEY",
        "default_model": "gpt-4o-mini",
        "vision_model": "gpt-4o"
      },
      "claude_code": {
        "enabled": true
      }
    }
  },
  "photos": {
    "database_path": "~/.cc-director/photos.db",
    "sources": []
  },
  "vault": {
    "vault_path": "D:/Vault"
  },
  "comm_manager": {
    "queue_path": "D:/path/to/content",
    "default_persona": "personal",
    "default_created_by": "claude_code"
  }
}
```

## Installation

cc_shared is installed as a development dependency by other cc-director tools:

```bash
cd src/cc_shared
pip install -e ".[dev]"
```

## Dependencies

- Python >= 3.11
- openai >= 1.0.0

## Used By

Every Python tool in the cc-director suite:
cc-comm-queue, cc-crawl4ai, cc-gmail, cc-hardware, cc-image,
cc-devthrottle, cc-markdown, cc-outlook, cc-photos, cc-powerpoint, cc-reddit,
cc-transcribe, cc-vault, cc-video, cc-voice, cc-whisper, cc-youtube-info
