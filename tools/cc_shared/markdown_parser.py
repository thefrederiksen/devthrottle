"""Markdown parser using markdown-it-py with GFM extensions.

Shared by cc-pdf, cc-html, and cc-word.
Moved from cc-markdown/src/parser.py.
"""

from dataclasses import dataclass
from typing import Optional

from markdown_it import MarkdownIt
from mdit_py_plugins.tasklists import tasklists_plugin
from mdit_py_plugins.footnote import footnote_plugin


@dataclass
class ParsedMarkdown:
    """Result of parsing markdown content."""
    html: str
    title: Optional[str]
    raw: str


def parse_markdown(content: str) -> ParsedMarkdown:
    """Parse Markdown content to HTML.

    Supports:
    - CommonMark specification
    - GitHub Flavored Markdown (tables, strikethrough, task lists)
    - Fenced code blocks (rendered as plain <pre><code>, no syntax highlighting)
    - Footnotes

    Args:
        content: Raw markdown string

    Returns:
        ParsedMarkdown with HTML output and extracted metadata
    """
    # Initialize markdown-it with GFM-like settings
    md = MarkdownIt("gfm-like", {"typographer": True})

    # Add plugins
    md.use(tasklists_plugin)
    md.use(footnote_plugin)

    # Enable additional features
    md.enable("table")
    md.enable("strikethrough")

    # Render to HTML
    html = md.render(content)

    # Extract title from first H1 if present
    title = _extract_title(content)

    return ParsedMarkdown(
        html=html,
        title=title,
        raw=content,
    )


def _extract_title(content: str) -> Optional[str]:
    """Extract title from first H1 heading."""
    for line in content.split("\n"):
        line = line.strip()
        if line.startswith("# "):
            return line[2:].strip()
    return None
