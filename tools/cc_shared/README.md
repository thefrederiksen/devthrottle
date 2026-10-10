# cc_shared

Shared configuration and LLM abstraction library for the cc-director suite.

This is **not a CLI tool** - it is a Python library imported by all other cc-director Python packages.

## What It Provides

### Tool Failure Reports (`tool_errors.py`)

Every cc-* tool's console script (and its `main.py`) runs the tool through `run_tool`, which reports a
failure to the Gateway as component `tool` and leaves the exit code and the printed error unchanged.
A usage mistake (exit code 2, a Click usage error, Ctrl+C) is not reported. Command-line values are cut
out of every message before it is scrubbed, so a path, a flag value or a prompt never leaves the machine.
A shared fail helper names its sentence with `note_failure` before it exits.

Credential, first that applies: the session key inside a session, the machine's own Gateway credential
outside one, and the hosted Gateway's `POST /install-reports` before the machine has signed in. A report
that cannot be sent is kept in `<machine root>/logs/error-outbox/tool/` and sent with the next failure;
what happened to each report is in `<machine root>/logs/tool-error-reports.log`. Nothing in the send path can
change how the tool ends. After a send that got no answer (the Gateway is down, the machine is offline),
sending pauses for five minutes, so an outage costs one timeout rather than one per failing command.

A test suite that makes a tool fail on purpose must keep the reports in the test: point
`CC_DIRECTOR_ROOT` at a throwaway folder, remove the session pair, and set
`DEVTHROTTLE_HOSTED_GATEWAY_URL` to an address that fails at once, in an autouse fixture named
`tool_error_reports_stay_in_the_test` in the suite's `conftest.py` (see `tools/cc-secrets/tests/conftest.py`).
`tools/test_tool_error_reporting.py` reads every tool's code and fails when an exit cannot reach the hook, when a
tool with an entry point is not wired, and when a suite that starts a Python process or calls an entry point
has no such fixture.

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
