"""Tests for cc-pdf HTML generator (title escaping and asset embedding)."""

import sys
from pathlib import Path

# Add src and the tools dir (so the cc_shared package resolves) to path for testing
sys.path.insert(0, str(Path(__file__).parent.parent / "src"))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from html_generator import generate_html, embed_images_as_base64, AssetEmbedError
from cc_shared.markdown_parser import ParsedMarkdown


class TestTitleEscaping:
    def test_title_with_special_chars_is_escaped(self):
        # Arrange
        parsed = ParsedMarkdown(
            html="<p>x</p>",
            title="Tom & Jerry <fun>",
            raw="x",
        )
        # Act
        result = generate_html(parsed, "")
        # Assert
        assert "<title>Tom &amp; Jerry &lt;fun&gt;</title>" in result
        assert "<title>Tom & Jerry <fun>" not in result


class TestEmbedWarnings:
    def test_missing_file_emits_warning(self, capsys):
        # Arrange
        warnings = []
        html = '<img src="nope.png">'
        # Act
        result = embed_images_as_base64(html, Path("."), warnings=warnings)
        # Assert
        assert warnings, "A missing asset must produce a collected warning"
        assert "WARNING" in warnings[0]
        assert "nope.png" in warnings[0]
        # Visible on stderr, ASCII-only
        captured = capsys.readouterr()
        assert "WARNING" in captured.err
        assert captured.err.isascii()
        # Original src left intact (not embedded)
        assert "nope.png" in result

    def test_strict_mode_raises_on_missing_asset(self):
        try:
            embed_images_as_base64('<img src="nope.png">', Path("."), strict=True)
            assert False, "Strict mode should raise AssetEmbedError"
        except AssetEmbedError as e:
            assert "nope.png" in str(e)
