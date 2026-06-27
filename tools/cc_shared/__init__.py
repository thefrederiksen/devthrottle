"""Shared configuration, LLM abstraction, and theme definitions for cc-director."""

__version__ = "0.1.0"

from .themes import (
    CanonicalTheme,
    ThemeColors,
    ThemeFonts,
    get_theme,
    list_themes,
)

# NOTE: config, llm, and markdown_parser are imported lazily (PEP 562) rather
# than eagerly. Each pulls in dependencies that only SOME consumers need:
#   * config / llm  -> cc_storage (the credential/config store)
#   * markdown_parser -> markdown-it-py + mdit_py_plugins
# The three document tools (cc-pdf / cc-html / cc-word) never touch config or
# llm, so importing cc_shared (or one of its sibling submodules) must NOT drag
# cc_storage into their frozen builds. Keeping these lazy means
# ``import cc_shared`` stays cheap, while ``cc_shared.get_config`` /
# ``cc_shared.parse_markdown`` still resolve on first access, and the direct
# submodule forms (``from cc_shared.config import get_config``) keep working.
_LAZY_SUBMODULE = {
    "ParsedMarkdown": "markdown_parser",
    "parse_markdown": "markdown_parser",
    "CCDirectorConfig": "config",
    "get_config": "config",
    "get_config_path": "config",
    "LLMProvider": "llm",
    "get_llm_provider": "llm",
}


def __getattr__(name):
    module_name = _LAZY_SUBMODULE.get(name)
    if module_name is not None:
        import importlib
        module = importlib.import_module(f".{module_name}", __name__)
        return getattr(module, name)
    raise AttributeError(f"module {__name__!r} has no attribute {name!r}")


def __dir__():
    return sorted(set(globals()) | set(_LAZY_SUBMODULE))

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
