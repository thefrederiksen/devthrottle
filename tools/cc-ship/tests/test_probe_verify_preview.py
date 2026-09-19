"""The verifier probe never leaves the bypass cookie behind (issue 2935)."""

import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "probes"))
from src import briefs  # noqa: E402
from src import preview  # noqa: E402
import probe_verify_preview  # noqa: E402


def test_main_BriefWriteFailsAfterCookie_CookieFileRemoved(tmp_path, monkeypatch):
    # Re-inspection round 3, finding 2: guard the probe's own caller, not just the helper.
    work = tmp_path / "run"
    seen = {}

    def fake_write_state(url, state_file):
        state_file.write_text("{}", encoding="ascii")
        seen["state"] = state_file

    def failing_brief(**_kwargs):
        assert seen["state"].exists()
        raise OSError("disk full while writing the brief")

    monkeypatch.setattr(preview, "find_preview_url", lambda _slug, _sha: "https://x.vercel.app")
    monkeypatch.setattr(preview, "write_bypass_state", fake_write_state)
    monkeypatch.setattr(briefs, "verifier_brief", failing_brief)
    monkeypatch.setattr(sys, "argv", ["probe", str(work), str(tmp_path), "abc", "ClaudeCode"])

    with pytest.raises(OSError):
        probe_verify_preview.main()
    assert not seen["state"].exists()
