"""Tests for cc-html HTML generator."""

import sys
from pathlib import Path

# Add src and cc_shared to path for testing
sys.path.insert(0, str(Path(__file__).parent.parent / "src"))
sys.path.insert(0, str(Path(__file__).parent.parent.parent / "cc_shared"))

from html_generator import generate_html, embed_images_as_base64, AssetEmbedError
from cc_shared.markdown_parser import ParsedMarkdown


class TestGenerateHtml:
    def test_basic_html_output(self):
        parsed = ParsedMarkdown(
            html="<h1>Hello</h1><p>World</p>",
            title="Hello",
            raw="# Hello\n\nWorld",
        )
        css = "body { color: black; }"
        result = generate_html(parsed, css)

        assert "<!DOCTYPE html>" in result
        assert "<title>Hello</title>" in result
        assert "Hello" in result
        assert "World" in result
        assert "body { color: black; }" in result

    def test_default_title_when_none(self):
        parsed = ParsedMarkdown(html="<p>Text</p>", title=None, raw="Text")
        result = generate_html(parsed, "")

        assert "<title>Document</title>" in result

    def test_css_embedded_in_style_tag(self):
        parsed = ParsedMarkdown(html="<p>x</p>", title="T", raw="x")
        css = ".test { margin: 0; }"
        result = generate_html(parsed, css)

        assert "<style>" in result
        assert ".test { margin: 0; }" in result
        assert "</style>" in result

    def test_content_in_markdown_body(self):
        parsed = ParsedMarkdown(html="<p>content</p>", title="T", raw="content")
        result = generate_html(parsed, "")

        assert '<article class="markdown-body">' in result
        assert "content" in result


class TestEmbedImages:
    def test_no_base_path_returns_unchanged(self):
        html = '<img src="test.png">'
        result = embed_images_as_base64(html, None)
        assert result == html

    def test_remote_urls_skipped(self):
        html = '<img src="https://example.com/img.png">'
        result = embed_images_as_base64(html, Path("."))
        assert "https://example.com/img.png" in result

    def test_data_uris_skipped(self):
        html = '<img src="data:image/png;base64,abc123">'
        result = embed_images_as_base64(html, Path("."))
        assert "data:image/png;base64,abc123" in result

    def test_missing_file_left_unchanged(self):
        html = '<img src="nonexistent.png">'
        result = embed_images_as_base64(html, Path("."))
        assert "nonexistent.png" in result


class TestTitleEscaping:
    def test_title_with_special_chars_is_escaped(self):
        parsed = ParsedMarkdown(html="<p>x</p>", title="A & B <c>", raw="x")
        result = generate_html(parsed, "")
        assert "<title>A &amp; B &lt;c&gt;</title>" in result


class TestEmbedAssetWarnings:
    def test_missing_file_collects_warning(self, capsys):
        warnings = []
        embed_images_as_base64('<img src="missing.png">', Path("."), warnings=warnings)
        assert warnings
        assert "missing.png" in warnings[0]
        captured = capsys.readouterr()
        assert "WARNING" in captured.err
        assert captured.err.isascii()

    def test_strict_mode_raises(self):
        try:
            embed_images_as_base64('<img src="missing.png">', Path("."), strict=True)
            assert False, "Strict mode should raise AssetEmbedError"
        except AssetEmbedError as e:
            assert "missing.png" in str(e)
