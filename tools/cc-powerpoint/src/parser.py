"""Markdown parser for slide-based presentations.

Parses markdown with --- slide separators into structured Slide dataclasses
using markdown-it-py token-level parsing.
"""

import re
from dataclasses import dataclass, field
from enum import Enum
from typing import Optional

from markdown_it import MarkdownIt
from mdit_py_plugins.tasklists import tasklists_plugin


class SlideLayout(Enum):
    """Detected slide layout type."""
    TITLE = "title"               # First slide with # + optional ##
    SECTION_HEADER = "section"    # Non-first slide with only # heading
    TITLE_CONTENT = "content"     # # heading + bullet list
    TITLE_TABLE = "table"         # # heading + table
    TITLE_CODE = "code"           # # heading + code fence
    TITLE_IMAGE = "image"         # # heading + image
    BLANK_IMAGE = "blank_image"   # Image only, no heading
    BLANK = "blank"               # Fallback


@dataclass
class TableData:
    """Parsed table structure."""
    headers: list[str] = field(default_factory=list)
    rows: list[list[str]] = field(default_factory=list)


@dataclass
class ImageData:
    """Parsed image reference."""
    src: str
    alt: str = ""


@dataclass
class Slide:
    """A single parsed slide."""
    layout: SlideLayout
    title: str = ""
    subtitle: str = ""
    bullets: list[str] = field(default_factory=list)
    sub_bullets: dict[int, list[str]] = field(default_factory=dict)
    code: str = ""
    code_language: str = ""
    table: Optional[TableData] = None
    image: Optional[ImageData] = None
    speaker_notes: str = ""
    raw_markdown: str = ""


def parse_markdown(content: str) -> list[Slide]:
    """Parse markdown content into a list of slides.

    Splits on --- separators, then analyzes each slide's markdown-it tokens
    to determine layout and extract structured content.

    Args:
        content: Raw markdown string with --- slide separators

    Returns:
        List of Slide dataclasses
    """
    # Split into raw slide chunks on --- separators
    raw_slides = _split_slides(content)

    if not raw_slides:
        return []

    slides = []
    for i, raw in enumerate(raw_slides):
        slide = _parse_slide(raw.strip(), is_first=(i == 0))
        if slide:
            slides.append(slide)

    return slides


def _split_slides(content: str) -> list[str]:
    """Split markdown content on --- slide separators.

    A separator is a line that is exactly --- (with optional whitespace),
    not inside a code fence.
    """
    lines = content.split("\n")
    chunks: list[str] = []
    current: list[str] = []
    in_code_fence = False

    for line in lines:
        stripped = line.strip()

        # Track code fences
        if stripped.startswith("```"):
            in_code_fence = not in_code_fence

        # Check for slide separator (only outside code fences)
        if not in_code_fence and stripped == "---":
            chunk = "\n".join(current).strip()
            if chunk or chunks:  # Skip empty leading chunk
                chunks.append(chunk)
            current = []
            continue

        current.append(line)

    # Don't forget the last chunk
    last = "\n".join(current).strip()
    if last:
        chunks.append(last)

    return chunks


def _parse_slide(raw: str, is_first: bool) -> Optional[Slide]:
    """Parse a single slide's markdown into a Slide dataclass."""
    if not raw:
        return None

    # Extract speaker notes (blockquotes at end of slide)
    raw, speaker_notes = _extract_speaker_notes(raw)
    if not raw and not speaker_notes:
        return None

    # Parse tokens with markdown-it (commonmark preset - no linkify dependency)
    md = MarkdownIt("commonmark")
    md.enable("table")
    md.enable("strikethrough")
    md.use(tasklists_plugin)
    tokens = md.parse(raw)

    # Extract components from tokens
    title = _extract_heading(tokens, level=1)
    subtitle = _extract_heading(tokens, level=2)
    bullets, sub_bullets = _extract_bullets(tokens)
    code, code_lang = _extract_code(tokens)
    table = _extract_table(tokens)
    image = _extract_image(tokens, raw)

    # Determine layout
    layout = _detect_layout(
        is_first=is_first,
        title=title,
        subtitle=subtitle,
        bullets=bullets,
        code=code,
        table=table,
        image=image,
    )

    return Slide(
        layout=layout,
        title=title,
        subtitle=subtitle,
        bullets=bullets,
        sub_bullets=sub_bullets,
        code=code,
        code_language=code_lang,
        table=table,
        image=image,
        speaker_notes=speaker_notes,
        raw_markdown=raw,
    )


def _extract_speaker_notes(raw: str) -> tuple[str, str]:
    """Extract speaker notes from blockquotes at the end of a slide.

    Returns (remaining_markdown, speaker_notes_text).
    """
    lines = raw.split("\n")
    note_lines: list[str] = []
    content_lines: list[str] = []
    found_notes = False

    # Walk backwards from the end to find trailing blockquotes
    for line in reversed(lines):
        stripped = line.strip()
        if not found_notes and not stripped:
            # Skip trailing blank lines
            note_lines.insert(0, line)
            continue
        if stripped.startswith("> "):
            found_notes = True
            note_lines.insert(0, stripped[2:])
        elif stripped == ">":
            found_notes = True
            note_lines.insert(0, "")
        else:
            content_lines.insert(0, line)
            break

    if not found_notes:
        return raw, ""

    # Rebuild: everything before the blockquote is content
    idx = raw.rfind("\n> ")
    if idx == -1 and raw.startswith("> "):
        idx = 0

    # Find where the blockquote section starts
    content_part = []
    note_part = []
    in_notes = False
    for line in lines:
        stripped = line.strip()
        if not in_notes:
            if stripped.startswith("> ") or stripped == ">":
                # Check if everything from here to end is blockquote
                remaining_idx = lines.index(line)
                rest = lines[remaining_idx:]
                all_blockquote = all(
                    l.strip().startswith(">") or l.strip() == ""
                    for l in rest
                )
                if all_blockquote:
                    in_notes = True
                    text = stripped[2:] if stripped.startswith("> ") else ""
                    note_part.append(text)
                else:
                    content_part.append(line)
            else:
                content_part.append(line)
        else:
            if stripped.startswith("> "):
                note_part.append(stripped[2:])
            elif stripped == ">":
                note_part.append("")
            # Skip blank lines at end

    remaining = "\n".join(content_part).strip()
    notes = "\n".join(note_part).strip()
    return remaining, notes


def _extract_heading(tokens: list, level: int) -> str:
    """Extract the first heading of a given level from tokens."""
    for i, token in enumerate(tokens):
        if token.type == "heading_open" and token.tag == f"h{level}":
            # Next token should be inline with the heading text
            if i + 1 < len(tokens) and tokens[i + 1].type == "inline":
                return tokens[i + 1].content.strip()
    return ""


def _extract_bullets(tokens: list) -> tuple[list[str], dict[int, list[str]]]:
    """Extract bullet points from tokens.

    Returns (bullets, sub_bullets) where sub_bullets maps parent bullet
    index to its sub-bullet list.
    """
    bullets: list[str] = []
    sub_bullets: dict[int, list[str]] = {}
    list_depth = 0

    for i, token in enumerate(tokens):
        if token.type in ("bullet_list_open", "ordered_list_open"):
            list_depth += 1
        elif token.type in ("bullet_list_close", "ordered_list_close"):
            list_depth -= 1
        elif token.type == "inline" and list_depth > 0:
            # Check if previous token opens a list item
            if i > 0 and tokens[i - 1].type == "paragraph_open":
                if list_depth == 1:
                    bullets.append(token.content.strip())
                elif list_depth == 2 and bullets:
                    parent_idx = len(bullets) - 1
                    if parent_idx not in sub_bullets:
                        sub_bullets[parent_idx] = []
                    sub_bullets[parent_idx].append(token.content.strip())

    return bullets, sub_bullets


def _extract_code(tokens: list) -> tuple[str, str]:
    """Extract the first code fence from tokens.

    Returns (code_content, language).
    """
    for token in tokens:
        if token.type == "fence":
            lang = (token.info or "").strip()
            return token.content.rstrip("\n"), lang
    return "", ""


def _extract_table(tokens: list) -> Optional[TableData]:
    """Extract table data from tokens."""
    headers: list[str] = []
    rows: list[list[str]] = []
    in_thead = False
    in_tbody = False
    current_row: list[str] = []

    for token in tokens:
        if token.type == "thead_open":
            in_thead = True
        elif token.type == "thead_close":
            in_thead = False
        elif token.type == "tbody_open":
            in_tbody = True
        elif token.type == "tbody_close":
            in_tbody = False
        elif token.type == "tr_open":
            current_row = []
        elif token.type == "tr_close":
            if in_thead:
                headers = current_row
            elif in_tbody:
                rows.append(current_row)
            current_row = []
        elif token.type == "inline" and (in_thead or in_tbody):
            current_row.append(token.content.strip())

    if headers or rows:
        return TableData(headers=headers, rows=rows)
    return None


def _extract_image(tokens: list, raw: str) -> Optional[ImageData]:
    """Extract image from tokens or raw markdown.

    markdown-it parses images inside inline tokens, so we also
    fall back to regex on the raw markdown.
    """
    # Check inline tokens for images
    for token in tokens:
        if token.type == "inline" and token.children:
            for child in token.children:
                if child.type == "image":
                    src = child.attrGet("src") or ""
                    alt = child.content or ""
                    if src:
                        return ImageData(src=src, alt=alt)

    # Fallback: regex on raw markdown
    match = re.search(r"!\[([^\]]*)\]\(([^)]+)\)", raw)
    if match:
        return ImageData(alt=match.group(1), src=match.group(2))

    return None


def _detect_layout(
    is_first: bool,
    title: str,
    subtitle: str,
    bullets: list[str],
    code: str,
    table: Optional[TableData],
    image: Optional[ImageData],
) -> SlideLayout:
    """Detect the slide layout based on its content."""
    has_title = bool(title)
    has_subtitle = bool(subtitle)
    has_bullets = bool(bullets)
    has_code = bool(code)
    has_table = table is not None
    has_image = image is not None

    # Title slide: first slide with heading + optional subtitle, no other content
    if is_first and has_title and not has_bullets and not has_code and not has_table:
        return SlideLayout.TITLE

    # Image-only slide (no heading)
    if has_image and not has_title:
        return SlideLayout.BLANK_IMAGE

    # Section header: non-first slide with only a heading
    if (
        not is_first
        and has_title
        and not has_bullets
        and not has_code
        and not has_table
        and not has_image
        and not has_subtitle
    ):
        return SlideLayout.SECTION_HEADER

    # Title + image
    if has_title and has_image:
        return SlideLayout.TITLE_IMAGE

    # Title + table
    if has_title and has_table:
        return SlideLayout.TITLE_TABLE

    # Title + code
    if has_title and has_code:
        return SlideLayout.TITLE_CODE

    # Title + content (bullets)
    if has_title and has_bullets:
        return SlideLayout.TITLE_CONTENT

    # Title + subtitle only (treat as title slide)
    if has_title and has_subtitle:
        return SlideLayout.TITLE

    # Fallback
    if has_title:
        return SlideLayout.SECTION_HEADER

    return SlideLayout.BLANK
