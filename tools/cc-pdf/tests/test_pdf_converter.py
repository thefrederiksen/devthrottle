"""Tests for cc-pdf PDF converter."""

import os
import subprocess
import sys
from pathlib import Path
from unittest.mock import patch, MagicMock

# Add src to path for testing
sys.path.insert(0, str(Path(__file__).parent.parent / "src"))

import pdf_converter
from pdf_converter import (
    find_chrome,
    convert_to_pdf,
    PAGE_SIZES,
    BROWSER_ENV_VAR,
    _build_page_style,
    _inject_page_style,
)


class TestPageSizes:
    def test_a4_dimensions(self):
        assert "a4" in PAGE_SIZES
        assert PAGE_SIZES["a4"]["width"] == "8.27in"
        assert PAGE_SIZES["a4"]["height"] == "11.69in"

    def test_letter_dimensions(self):
        assert "letter" in PAGE_SIZES
        assert PAGE_SIZES["letter"]["width"] == "8.5in"
        assert PAGE_SIZES["letter"]["height"] == "11in"


class TestFindChrome:
    @patch("pdf_converter.os.path.exists")
    def test_finds_chrome_windows(self, mock_exists):
        def side_effect(path):
            return "Program Files\\Google\\Chrome" in path
        mock_exists.side_effect = side_effect

        result = find_chrome()
        assert result is not None
        assert "chrome" in result.lower()

    @patch("pdf_converter.os.path.exists", return_value=False)
    def test_returns_none_when_not_found(self, mock_exists):
        result = find_chrome()
        assert result is None


class TestConvertToPdf:
    def test_invalid_page_size_raises(self, tmp_path):
        with patch("pdf_converter.find_chrome", return_value="chrome.exe"):
            try:
                convert_to_pdf("<html></html>", tmp_path / "out.pdf", page_size="tabloid")
                assert False, "Should have raised ValueError"
            except ValueError as e:
                assert "tabloid" in str(e)

    def test_no_chrome_raises(self, tmp_path):
        with patch("pdf_converter.find_chrome", return_value=None):
            try:
                convert_to_pdf("<html></html>", tmp_path / "out.pdf")
                assert False, "Should have raised RuntimeError"
            except RuntimeError as e:
                # Error message must name the override env var so the user
                # knows how to recover.
                assert BROWSER_ENV_VAR in str(e)

    def test_timeout_raises_clean_runtime_error(self, tmp_path):
        """A wedged headless browser (TimeoutExpired) must surface as a clean
        RuntimeError naming the timeout, not a raw traceback."""
        def fake_run(cmd, **kwargs):
            raise subprocess.TimeoutExpired(cmd=cmd, timeout=kwargs.get("timeout", 60))

        with patch("pdf_converter.find_chrome", return_value="chrome.exe"), \
                patch("pdf_converter.subprocess.run", side_effect=fake_run):
            try:
                convert_to_pdf("<html><head></head><body>hi</body></html>",
                               tmp_path / "out.pdf")
                assert False, "Should have raised RuntimeError"
            except RuntimeError as e:
                assert "timed out" in str(e).lower()
                assert "60 seconds" in str(e)


class TestPageStyle:
    def test_a4_named_size_and_margin(self):
        # Arrange / Act
        style = _build_page_style("a4", "1in")
        # Assert
        assert "@page" in style
        assert "size: A4" in style
        assert "margin: 1in" in style

    def test_letter_named_size(self):
        style = _build_page_style("letter", "2cm")
        assert "size: Letter" in style
        assert "margin: 2cm" in style

    def test_inject_before_head_close(self):
        html = "<html><head><title>x</title></head><body>hi</body></html>"
        result = _inject_page_style(html, "<style>@page{}</style>\n")
        assert "<style>@page{}</style>" in result
        # Injected before </head>
        assert result.index("@page") < result.index("</head>")

    def test_default_a4_produces_a4_rule_in_document(self, tmp_path):
        """convert_to_pdf with default page size must inject an A4 @page rule."""
        captured = {}

        def fake_run(cmd, **kwargs):
            # The last argument is the file URI of the temp HTML.
            file_uri = cmd[-1]
            html_path = file_uri.replace("file:///", "")
            captured["html"] = Path(html_path).read_text(encoding="utf-8")
            captured["cmd"] = cmd
            # Simulate Chrome writing the output PDF.
            out = cmd[[i for i, a in enumerate(cmd)
                       if str(a).startswith("--print-to-pdf=")][0]].split("=", 1)[1]
            Path(out).write_bytes(b"%PDF-1.4 fake")
            result = MagicMock()
            result.returncode = 0
            result.stderr = ""
            return result

        with patch("pdf_converter.find_chrome", return_value="chrome.exe"), \
                patch("pdf_converter.subprocess.run", side_effect=fake_run):
            convert_to_pdf("<html><head></head><body>hi</body></html>",
                           tmp_path / "out.pdf")

        assert "size: A4" in captured["html"]


class TestBrowserDiscovery:
    def test_env_override_used_when_exists(self, tmp_path):
        fake_browser = tmp_path / "mybrowser.exe"
        fake_browser.write_text("x")
        with patch.dict(os.environ, {BROWSER_ENV_VAR: str(fake_browser)}):
            assert find_chrome() == str(fake_browser)

    def test_env_override_missing_raises(self):
        with patch.dict(os.environ, {BROWSER_ENV_VAR: r"C:\nope\missing.exe"}):
            try:
                find_chrome()
                assert False, "Should have raised RuntimeError"
            except RuntimeError as e:
                assert BROWSER_ENV_VAR in str(e)

    def test_finds_edge(self):
        with patch.dict(os.environ, {BROWSER_ENV_VAR: ""}):
            def exists(path):
                return "Microsoft\\Edge" in path and path.endswith("msedge.exe")
            with patch("pdf_converter.os.path.exists", side_effect=exists):
                result = find_chrome()
                assert result is not None
                assert "msedge.exe" in result

    def test_which_fallback(self):
        with patch.dict(os.environ, {BROWSER_ENV_VAR: ""}):
            with patch("pdf_converter.os.path.exists", return_value=False), \
                    patch("pdf_converter.shutil.which",
                          side_effect=lambda n: "/usr/bin/brave" if n == "brave" else None):
                result = find_chrome()
                assert result == "/usr/bin/brave"


class TestUserDataDir:
    def test_dedicated_user_data_dir_passed(self, tmp_path):
        captured = {}

        def fake_run(cmd, **kwargs):
            captured["cmd"] = cmd
            out = [a for a in cmd if str(a).startswith("--print-to-pdf=")][0].split("=", 1)[1]
            Path(out).write_bytes(b"%PDF-1.4 fake")
            result = MagicMock()
            result.returncode = 0
            result.stderr = ""
            return result

        with patch("pdf_converter.find_chrome", return_value="chrome.exe"), \
                patch("pdf_converter.subprocess.run", side_effect=fake_run):
            convert_to_pdf("<html><head></head><body>hi</body></html>",
                           tmp_path / "out.pdf")

        user_data_args = [a for a in captured["cmd"]
                          if str(a).startswith("--user-data-dir=")]
        assert user_data_args, "Chrome must be launched with a dedicated --user-data-dir"
