"""HTML document generator -- intermediate format for Word conversion."""

import html as html_module
from pathlib import Path
from typing import Optional

from bs4 import BeautifulSoup


# Import ParsedMarkdown - handle both package and frozen modes
try:
    from cc_shared.markdown_parser import ParsedMarkdown
except ImportError:
    from dataclasses import dataclass

    @dataclass
    class ParsedMarkdown:
        html: str
        title: Optional[str]
        raw: str


HTML_TEMPLATE = """<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>{title}</title>
    <style>
{css}
    </style>
</head>
<body>
    <article class="markdown-body">
{content}
    </article>
</body>
</html>
"""


def generate_html(parsed: ParsedMarkdown, css: str) -> str:
    """Generate standalone HTML document with embedded CSS.

    Args:
        parsed: ParsedMarkdown object with HTML content
        css: CSS stylesheet content

    Returns:
        Complete HTML document as string
    """
    # Escape the title so a heading containing &, <, or > produces a valid
    # <title> rather than malformed HTML.
    title = html_module.escape(parsed.title or "Document")

    # Indent CSS for cleaner output
    css_indented = "\n".join(f"        {line}" for line in css.split("\n"))

    # Indent content for cleaner output
    content_indented = "\n".join(f"        {line}" for line in parsed.html.split("\n"))

    return HTML_TEMPLATE.format(
        title=title,
        css=css_indented,
        content=content_indented,
    )
