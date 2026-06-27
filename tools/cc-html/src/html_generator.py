"""HTML document generator with CSS embedding."""

import base64
import html as html_module
import mimetypes
import sys
from pathlib import Path
from typing import List, Optional

from bs4 import BeautifulSoup


class AssetEmbedError(Exception):
    """Raised when a local asset cannot be embedded and strict mode is on."""


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


def embed_images_as_base64(
    html_content: str,
    base_path: Optional[Path] = None,
    *,
    strict: bool = False,
    warnings: Optional[List[str]] = None,
) -> str:
    """Embed all local images in HTML as base64 data URIs.

    Remote (http/https) images and existing data URIs are left untouched.
    A local image that is missing or unreadable produces a visible warning on
    stderr (and is appended to ``warnings`` when provided). In strict mode such
    an image raises :class:`AssetEmbedError` instead of being skipped, so the
    caller can fail rather than silently produce a document with missing images.

    Args:
        html_content: HTML string with img tags
        base_path: Base path to resolve relative image paths from
        strict: If True, raise AssetEmbedError on a missing/unreadable local
            asset instead of emitting a warning and continuing.
        warnings: Optional list that collected warning messages are appended to.

    Returns:
        HTML with local images embedded as base64 data URIs.

    Raises:
        AssetEmbedError: If strict is True and a local asset cannot be embedded.
    """
    if base_path is None:
        return html_content

    def _report(message: str) -> None:
        # ASCII-only, visible warning. Goes to stderr so it never corrupts
        # stdout data, and into the collector list when one is supplied.
        if warnings is not None:
            warnings.append(message)
        if strict:
            raise AssetEmbedError(message)
        print(message, file=sys.stderr)

    soup = BeautifulSoup(html_content, 'html.parser')

    for img in soup.find_all('img'):
        src = img.get('src', '')
        if not src or src.startswith('data:'):
            continue

        # Resolve the image path relative to the base path
        if src.startswith(('http://', 'https://')):
            # Skip remote URLs
            continue
        elif src.startswith('file:///'):
            # Handle file:// URLs
            img_path = Path(src[8:])
        else:
            # Relative path - resolve from base directory
            img_path = (base_path / src).resolve()

        if not img_path.exists():
            _report(f"WARNING: could not embed {src}: file not found")
            continue

        # Read and encode the image
        try:
            with open(img_path, 'rb') as f:
                img_data = f.read()
        except OSError as exc:
            _report(f"WARNING: could not embed {src}: {exc}")
            continue

        # Determine MIME type
        mime_type, _ = mimetypes.guess_type(str(img_path))
        if not mime_type:
            mime_type = 'image/png'

        # Create data URI
        b64_data = base64.b64encode(img_data).decode('utf-8')
        data_uri = f"data:{mime_type};base64,{b64_data}"

        img['src'] = data_uri

    return str(soup)
