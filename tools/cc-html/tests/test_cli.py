"""Tests for cc-html CLI overwrite guards (--force / --no-clobber / default)."""

import sys
from pathlib import Path

# Make `src` importable as a package and cc_shared importable.
sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from typer.testing import CliRunner

from src.cli import app

runner = CliRunner()


def _write_md(tmp_path: Path) -> Path:
    md = tmp_path / "in.md"
    md.write_text("# Title\n\nBody\n", encoding="utf-8")
    return md


class TestOverwriteGuard:
    def test_creates_output_when_absent(self, tmp_path):
        md = _write_md(tmp_path)
        out = tmp_path / "out.html"
        result = runner.invoke(app, ["from-markdown", str(md), "-o", str(out)])
        assert result.exit_code == 0
        assert out.exists()

    def test_default_refuses_to_overwrite(self, tmp_path):
        md = _write_md(tmp_path)
        out = tmp_path / "out.html"
        out.write_text("SENTINEL", encoding="utf-8")
        result = runner.invoke(app, ["from-markdown", str(md), "-o", str(out)])
        assert result.exit_code == 1
        # Existing file must be left untouched.
        assert out.read_text(encoding="utf-8") == "SENTINEL"

    def test_force_overwrites(self, tmp_path):
        md = _write_md(tmp_path)
        out = tmp_path / "out.html"
        out.write_text("SENTINEL", encoding="utf-8")
        result = runner.invoke(app, ["from-markdown", str(md), "-o", str(out), "--force"])
        assert result.exit_code == 0
        assert "SENTINEL" not in out.read_text(encoding="utf-8")

    def test_no_clobber_skips(self, tmp_path):
        md = _write_md(tmp_path)
        out = tmp_path / "out.html"
        out.write_text("SENTINEL", encoding="utf-8")
        result = runner.invoke(app, ["from-markdown", str(md), "-o", str(out), "--no-clobber"])
        assert result.exit_code == 0
        assert out.read_text(encoding="utf-8") == "SENTINEL"

    def test_force_and_no_clobber_mutually_exclusive(self, tmp_path):
        md = _write_md(tmp_path)
        out = tmp_path / "out.html"
        out.write_text("SENTINEL", encoding="utf-8")
        result = runner.invoke(
            app, ["from-markdown", str(md), "-o", str(out), "--force", "--no-clobber"]
        )
        assert result.exit_code == 1
