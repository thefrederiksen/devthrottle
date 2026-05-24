"""Tests for the markdown slide parser."""

import sys
from pathlib import Path

# Add src to path
sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent / "src"))

from src.parser import (
    parse_markdown,
    SlideLayout,
    _split_slides,
    _extract_speaker_notes,
)


class TestSplitSlides:
    """Tests for splitting markdown on --- separators."""

    def test_basic_split(self):
        content = "Slide 1\n---\nSlide 2\n---\nSlide 3"
        chunks = _split_slides(content)
        assert len(chunks) == 3

    def test_empty_leading_chunk(self):
        content = "---\n# Title\n---\n# Content"
        chunks = _split_slides(content)
        assert len(chunks) == 2

    def test_code_fence_not_split(self):
        content = "# Slide\n```\n---\n```\n---\n# Next"
        chunks = _split_slides(content)
        assert len(chunks) == 2

    def test_trailing_separator(self):
        content = "---\n# Title\n---\n# Content\n---"
        chunks = _split_slides(content)
        assert len(chunks) == 2


class TestSpeakerNotes:
    """Tests for speaker notes extraction."""

    def test_blockquote_notes(self):
        raw = "# Title\n\n- Bullet\n\n> These are speaker notes"
        content, notes = _extract_speaker_notes(raw)
        assert notes == "These are speaker notes"
        assert ">" not in content

    def test_no_notes(self):
        raw = "# Title\n\n- Bullet one\n- Bullet two"
        content, notes = _extract_speaker_notes(raw)
        assert notes == ""
        assert content == raw


class TestLayoutDetection:
    """Tests for automatic slide layout detection."""

    def test_title_slide(self):
        md = "---\n# My Presentation\n## By Author\n---"
        slides = parse_markdown(md)
        assert len(slides) == 1
        assert slides[0].layout == SlideLayout.TITLE
        assert slides[0].title == "My Presentation"
        assert slides[0].subtitle == "By Author"

    def test_section_header(self):
        md = "---\n# Title\n---\n# Section Name\n---"
        slides = parse_markdown(md)
        assert len(slides) == 2
        assert slides[1].layout == SlideLayout.SECTION_HEADER
        assert slides[1].title == "Section Name"

    def test_content_slide(self):
        md = "---\n# Title\n---\n# My Points\n\n- Point A\n- Point B\n---"
        slides = parse_markdown(md)
        assert len(slides) == 2
        assert slides[1].layout == SlideLayout.TITLE_CONTENT
        assert slides[1].bullets == ["Point A", "Point B"]

    def test_table_slide(self):
        md = "---\n# Title\n---\n# Data\n\n| A | B |\n|---|---|\n| 1 | 2 |\n---"
        slides = parse_markdown(md)
        assert len(slides) == 2
        assert slides[1].layout == SlideLayout.TITLE_TABLE
        assert slides[1].table is not None
        assert slides[1].table.headers == ["A", "B"]
        assert slides[1].table.rows == [["1", "2"]]

    def test_code_slide(self):
        md = "---\n# Title\n---\n# Code\n\n```python\nprint('hello')\n```\n---"
        slides = parse_markdown(md)
        assert len(slides) == 2
        assert slides[1].layout == SlideLayout.TITLE_CODE
        assert slides[1].code == "print('hello')"
        assert slides[1].code_language == "python"

    def test_image_slide(self):
        md = "---\n# Title\n---\n# Screenshot\n\n![Alt text](image.png)\n---"
        slides = parse_markdown(md)
        assert len(slides) == 2
        assert slides[1].layout == SlideLayout.TITLE_IMAGE
        assert slides[1].image is not None
        assert slides[1].image.src == "image.png"
        assert slides[1].image.alt == "Alt text"

    def test_blank_image_slide(self):
        md = "---\n# Title\n---\n![Full image](photo.jpg)\n---"
        slides = parse_markdown(md)
        assert len(slides) == 2
        assert slides[1].layout == SlideLayout.BLANK_IMAGE

    def test_sub_bullets(self):
        md = "---\n# Title\n---\n# Items\n\n- Top\n  - Sub A\n  - Sub B\n- Second\n---"
        slides = parse_markdown(md)
        assert len(slides) == 2
        assert slides[1].bullets == ["Top", "Second"]
        assert 0 in slides[1].sub_bullets
        assert slides[1].sub_bullets[0] == ["Sub A", "Sub B"]

    def test_numbered_list(self):
        md = "---\n# Title\n---\n# Items\n\n1. First item\n2. Second item\n3. Third item\n---"
        slides = parse_markdown(md)
        assert len(slides) == 2
        assert slides[1].bullets == ["First item", "Second item", "Third item"]

    def test_numbered_sub_bullets(self):
        md = "---\n# Title\n---\n# Items\n\n1. Top\n   1. Sub A\n   2. Sub B\n2. Second\n---"
        slides = parse_markdown(md)
        assert len(slides) == 2
        assert slides[1].bullets == ["Top", "Second"]
        assert slides[1].sub_bullets[0] == ["Sub A", "Sub B"]


class TestFullParsing:
    """Integration tests for complete markdown parsing."""

    def test_sample_presentation(self):
        sample = Path(__file__).parent.parent / "samples" / "quarterly-report.md"
        if not sample.exists():
            return  # Skip if sample not available

        content = sample.read_text(encoding="utf-8")
        slides = parse_markdown(content)

        assert len(slides) >= 5
        assert slides[0].layout == SlideLayout.TITLE
        assert slides[0].title == "Q4 2025 Quarterly Report"

    def test_empty_content(self):
        slides = parse_markdown("")
        assert slides == []

    def test_no_separators(self):
        slides = parse_markdown("# Just a heading\n\n- Some content")
        assert len(slides) == 1
