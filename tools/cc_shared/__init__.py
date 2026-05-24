"""Shared configuration, LLM abstraction, and theme definitions for cc-director."""

__version__ = "0.1.0"

from .config import CCDirectorConfig, get_config, get_config_path
from .llm import LLMProvider, get_llm_provider
from .themes import (
    CanonicalTheme,
    ThemeColors,
    ThemeFonts,
    get_theme,
    list_themes,
)

# NOTE: markdown_parser is imported lazily (PEP 562) rather than eagerly. It pulls in
# markdown-it-py + mdit_py_plugins, which only cc-pdf / cc-html / cc-word need. Tools
# like cc-settings that import a sibling submodule (e.g. cc_shared.config) would
# otherwise crash if those packages were not bundled into their frozen build. Accessing
# cc_shared.parse_markdown / cc_shared.ParsedMarkdown still works on demand.
_LAZY = {"ParsedMarkdown", "parse_markdown"}


def __getattr__(name):
    if name in _LAZY:
        from . import markdown_parser
        return getattr(markdown_parser, name)
    raise AttributeError(f"module {__name__!r} has no attribute {name!r}")


def __dir__():
    return sorted(set(globals()) | _LAZY)

__all__ = [
    "CCDirectorConfig",
    "get_config",
    "get_config_path",
    "LLMProvider",
    "get_llm_provider",
    "CanonicalTheme",
    "ThemeColors",
    "ThemeFonts",
    "get_theme",
    "list_themes",
    "ParsedMarkdown",
    "parse_markdown",
]
