"""`cc-devthrottle license switching status / on / off` (issue #4327).

Until this command nothing in the product wrote `licenseSwitching.enabled`, and a missing key read as OFF - so a fresh
Director data folder on a machine holding spares silently dropped that machine out of every switch. These tests hold
the three rules the Director's `LicenseSwitchingSetting` reads by: an explicit value wins, a missing one is on exactly
when the store holds a spare, and a write keeps every other part of config.json. That the Director reads what this
writes is proven from the C# side by `LicenseSwitchingSettingTests`.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import license_switching_setting  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()


@pytest.fixture
def home(tmp_path, monkeypatch):
    """A throwaway cc-director root and key store, so nothing here can see or touch this machine's real ones."""
    root = tmp_path / "cc-director-root"
    root.mkdir()
    monkeypatch.setenv("CC_DIRECTOR_ROOT", str(root))
    monkeypatch.delenv("CC_LICENSE_STORE_DIR", raising=False)
    return root


@pytest.fixture
def store(tmp_path):
    path = tmp_path / "store"
    path.mkdir()
    return path


def hold_spare(store: Path, email: str) -> None:
    folder = store / "spares" / email.replace("@", "-at-")
    folder.mkdir(parents=True)
    (folder / ".claude.json").write_text(json.dumps({"oauthAccount": {"emailAddress": email}}), encoding="utf-8")


def run(verb: str, store: Path):
    result = runner.invoke(app, ["license", "switching", verb, "--store-dir", str(store), "--json"])
    assert result.exit_code == 0, result.output
    return json.loads(result.output)


def test_status_missing_key_with_a_spare_is_on(home, store):
    # The case that broke a machine on 21 October 2026: a fresh config.json with no licenseSwitching section on a
    # machine that holds spares.
    hold_spare(store, "spare@example.com")

    answer = run("status", store)

    assert answer["enabled"] is True
    assert answer["configured"] is None
    assert answer["sparesHeld"] == 1


def test_status_missing_key_without_a_spare_is_off(home, store):
    answer = run("status", store)

    assert answer["enabled"] is False
    assert answer["sparesHeld"] == 0


def test_off_writes_an_explicit_false_that_wins_over_held_spares(home, store):
    hold_spare(store, "spare@example.com")

    answer = run("off", store)

    assert answer["enabled"] is False
    assert answer["configured"] is False
    assert answer["changed"] is True
    stored = json.loads(license_switching_setting.config_path().read_text(encoding="utf-8"))
    assert stored["licenseSwitching"]["enabled"] is False


def test_on_keeps_every_other_setting_in_the_file(home, store):
    path = license_switching_setting.config_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps({"gateway": {"url": "https://gateway.example"},
                                "licenseSwitching": {"enabled": False, "offerApprovers": True}}), encoding="utf-8")

    answer = run("on", store)

    assert answer["enabled"] is True
    stored = json.loads(path.read_text(encoding="utf-8"))
    assert stored["gateway"] == {"url": "https://gateway.example"}
    assert stored["licenseSwitching"] == {"enabled": True, "offerApprovers": True}


def test_a_config_that_cannot_be_read_is_never_overwritten(home, store):
    path = license_switching_setting.config_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("{ not json", encoding="utf-8")

    result = runner.invoke(app, ["license", "switching", "on", "--store-dir", str(store)])

    assert result.exit_code == 1
    assert path.read_text(encoding="utf-8") == "{ not json"


@pytest.mark.parametrize("value", ["true", 1, None, {}])
def test_a_value_that_is_not_a_boolean_says_nothing(value):
    assert license_switching_setting.read_explicit({"licenseSwitching": {"enabled": value}}) is None
    assert license_switching_setting.resolve(None, True) is True
    assert license_switching_setting.resolve(None, False) is False


def test_spares_are_counted_from_account_files_only(store):
    hold_spare(store, "B@Example.com")
    (store / "spares" / "no-account-file").mkdir()
    broken = store / "spares" / "broken"
    broken.mkdir()
    (broken / ".claude.json").write_text("{", encoding="utf-8")

    assert license_switching_setting.spare_emails(str(store)) == ["b@example.com"]
