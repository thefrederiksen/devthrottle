"""`cc-devthrottle license switching status / on / off` (issue #4327).

Until this command nothing in the product wrote `licenseSwitching.enabled`, and a missing key read as OFF - so a fresh
Director data folder on a machine holding spares silently dropped that machine out of every switch. These tests hold
the rules the Director's `LicenseSwitchingSetting` reads by: an explicit value wins, a missing one is on exactly when
the store holds a spare, and a write keeps every other part of config.json. And they hold WHERE it is written: each
Director's own `instances/<name>/config/config.json`, never the machine root's config.json, which no Director reads
(issue #3506). That the Director reads what this writes is proven from the C# side by `LicenseSwitchingSettingTests`.
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
def machine(tmp_path, monkeypatch):
    """A throwaway user base with two Director instance homes, and CC_DIRECTOR_ROOT pointing at the base itself - the
    machine root a plain terminal resolves - so a write that lands there instead of in an instance home is caught."""
    base = tmp_path / "cc-director"
    for name in ("default", "slot5"):
        (base / "instances" / name / "config").mkdir(parents=True)
    monkeypatch.setenv("CC_DIRECTOR_ROOT", str(base))
    monkeypatch.delenv("CC_LICENSE_STORE_DIR", raising=False)
    monkeypatch.setattr(license_switching_setting, "instance_homes",
                        lambda: sorted((base / "instances").iterdir(), key=lambda p: p.name))
    return base


@pytest.fixture
def store(tmp_path):
    path = tmp_path / "store"
    path.mkdir()
    return path


def config_of(machine: Path, instance: str) -> Path:
    return machine / "instances" / instance / "config" / "config.json"


def hold_spare(store: Path, email: str) -> None:
    folder = store / "spares" / email.replace("@", "-at-")
    folder.mkdir(parents=True)
    (folder / ".claude.json").write_text(json.dumps({"oauthAccount": {"emailAddress": email}}), encoding="utf-8")


def run(verb: str, store: Path, *extra: str):
    result = runner.invoke(app, ["license", "switching", verb, "--store-dir", str(store), "--json", *extra])
    assert result.exit_code == 0, result.output
    return json.loads(result.output)


def by_instance(answer):
    return {row["instance"]: row for row in answer["instances"]}


def test_status_missing_key_with_a_spare_is_on_for_every_director(machine, store):
    # The case that broke a machine on 21 October 2026: a fresh config.json with no licenseSwitching section on a
    # machine that holds spares.
    hold_spare(store, "spare@example.com")

    answer = run("status", store)

    assert answer["count"] == 2
    assert answer["sparesHeld"] == 1
    assert all(row["enabled"] is True and row["configured"] is None for row in answer["instances"])


def test_status_missing_key_without_a_spare_is_off(machine, store):
    answer = run("status", store)

    assert all(row["enabled"] is False for row in answer["instances"])


def test_off_writes_an_explicit_false_into_each_directors_own_config(machine, store):
    hold_spare(store, "spare@example.com")

    answer = run("off", store)

    for name in ("default", "slot5"):
        row = by_instance(answer)[name]
        assert (row["enabled"], row["configured"], row["changed"]) == (False, False, True)
        stored = json.loads(config_of(machine, name).read_text(encoding="utf-8"))
        assert stored["licenseSwitching"]["enabled"] is False
    # The machine root's config.json is read by no Director: nothing may be written there.
    assert not (machine / "config" / "config.json").exists()


def test_instance_writes_only_the_named_director(machine, store):
    run("off", store, "--instance", "SLOT5")

    assert not config_of(machine, "default").exists()
    assert json.loads(config_of(machine, "slot5").read_text(encoding="utf-8"))["licenseSwitching"]["enabled"] is False


def test_an_unknown_instance_is_refused_and_names_the_ones_there(machine, store):
    result = runner.invoke(app, ["license", "switching", "on", "--store-dir", str(store), "--instance", "slot9"])

    assert result.exit_code == 1
    assert "default, slot5" in result.output
    assert not config_of(machine, "default").exists()


def test_no_director_home_at_all_is_an_error_not_a_silent_no_op(tmp_path, store, monkeypatch):
    monkeypatch.setattr(license_switching_setting, "instance_homes", lambda: [])

    result = runner.invoke(app, ["license", "switching", "on", "--store-dir", str(store)])

    assert result.exit_code == 1
    assert "no Director instance home" in result.output


def test_on_keeps_every_other_setting_in_the_file(machine, store):
    path = config_of(machine, "default")
    path.write_text(json.dumps({"gateway": {"url": "https://gateway.example"},
                                "licenseSwitching": {"enabled": False, "offerApprovers": True}}), encoding="utf-8")

    answer = run("on", store, "--instance", "default")

    assert by_instance(answer)["default"]["enabled"] is True
    stored = json.loads(path.read_text(encoding="utf-8"))
    assert stored["gateway"] == {"url": "https://gateway.example"}
    assert stored["licenseSwitching"] == {"enabled": True, "offerApprovers": True}


def test_one_unreadable_config_changes_none(machine, store):
    config_of(machine, "slot5").write_text("{ not json", encoding="utf-8")

    result = runner.invoke(app, ["license", "switching", "on", "--store-dir", str(store)])

    assert result.exit_code == 1
    assert config_of(machine, "slot5").read_text(encoding="utf-8") == "{ not json"
    assert not config_of(machine, "default").exists()


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


def test_plain_output_lists_each_director(machine, store):
    result = runner.invoke(app, ["license", "switching", "status", "--store-dir", str(store)])

    assert result.exit_code == 0, result.output
    assert "default" in result.output and "slot5" in result.output
    assert "not set" in result.output
