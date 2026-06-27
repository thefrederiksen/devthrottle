"""Tests for cc-devthrottle settings operations."""

import json
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared.config import CCDirectorConfig  # noqa: E402
from src.settings_ops import (  # noqa: E402
    get_all_settings,
    get_section,
    get_section_names,
    get_value,
    list_keys,
    set_value,
)


class TestGetAllSettings:
    def test_returns_flat_dict(self):
        config = CCDirectorConfig()
        settings = get_all_settings(config)
        assert isinstance(settings, dict)
        assert "llm.default_provider" in settings
        assert "vault.vault_path" in settings
        assert "screenshots.source_directory" in settings

    def test_no_nested_dicts_in_values(self):
        config = CCDirectorConfig()
        settings = get_all_settings(config)
        for key, value in settings.items():
            assert not isinstance(value, dict), f"Key {key} has dict value"


class TestGetSection:
    def test_known_section(self):
        config = CCDirectorConfig()
        section = get_section(config, "screenshots")
        assert section is not None
        assert "source_directory" in section

    def test_unknown_section(self):
        config = CCDirectorConfig()
        section = get_section(config, "nonexistent")
        assert section is None

    def test_all_sections_accessible(self):
        config = CCDirectorConfig()
        for name in config.to_dict():
            section = get_section(config, name)
            assert section is not None, f"Section {name} not accessible"


class TestGetValue:
    def test_known_key(self):
        config = CCDirectorConfig()
        found, value = get_value(config, "llm.default_provider")
        assert found is True
        assert value == "claude_code"

    def test_unknown_key(self):
        config = CCDirectorConfig()
        found, value = get_value(config, "nonexistent.key")
        assert found is False
        assert value is None

    def test_screenshots_key(self):
        config = CCDirectorConfig()
        found, value = get_value(config, "screenshots.source_directory")
        assert found is True
        assert isinstance(value, str)


class TestSetValue:
    def test_set_string_value(self, tmp_path):
        config_file = tmp_path / "config.json"
        config = CCDirectorConfig()
        config._config_path = config_file

        success = set_value(config, "screenshots.source_directory", "D:/New/Path")
        assert success is True
        assert config.screenshots.source_directory == "D:/New/Path"

        saved = json.loads(config_file.read_text())
        assert saved["screenshots"]["source_directory"] == "D:/New/Path"

    def test_set_bool_value(self, tmp_path):
        config_file = tmp_path / "config.json"
        config = CCDirectorConfig()
        config._config_path = config_file

        success = set_value(config, "llm.providers.claude_code.enabled", "false")
        assert success is True
        assert config.llm.providers.claude_code.enabled is False

    def test_set_unknown_key(self, tmp_path):
        config = CCDirectorConfig()
        config._config_path = tmp_path / "config.json"
        success = set_value(config, "fake.section.key", "value")
        assert success is False

    def test_set_single_segment_key(self, tmp_path):
        config = CCDirectorConfig()
        config._config_path = tmp_path / "config.json"
        success = set_value(config, "nodots", "value")
        assert success is False


class TestPreservesUnknownSections:
    def test_set_preserves_unknown_section(self, tmp_path):
        config_file = tmp_path / "config.json"
        config_file.write_text(
            json.dumps(
                {
                    "screenshots": {"source_directory": "C:/old"},
                    "future_block": {"some_key": "must_survive"},
                }
            )
        )

        config = CCDirectorConfig()
        config._config_path = config_file
        config.load()

        ok = set_value(config, "screenshots.source_directory", "/Users/alice/Desktop")
        assert ok is True

        saved = json.loads(config_file.read_text())
        assert saved["screenshots"]["source_directory"] == "/Users/alice/Desktop"
        assert saved["future_block"]["some_key"] == "must_survive"

    def test_set_preserves_gateway_block(self, tmp_path):
        config_file = tmp_path / "config.json"
        config_file.write_text(
            json.dumps(
                {
                    "gateway": {"url": "http://gw.example:7878", "token": "abc"},
                    "screenshots": {"source_directory": "C:/old"},
                }
            )
        )

        config = CCDirectorConfig()
        config._config_path = config_file
        config.load()

        ok = set_value(config, "screenshots.source_directory", "/new")
        assert ok is True

        saved = json.loads(config_file.read_text())
        assert saved["gateway"]["url"] == "http://gw.example:7878"
        assert saved["gateway"]["token"] == "abc"

    def test_set_gateway_url_via_dotted_key(self, tmp_path):
        config_file = tmp_path / "config.json"
        config = CCDirectorConfig()
        config._config_path = config_file

        ok = set_value(config, "gateway.url", "http://gw.example:7878")
        assert ok is True

        saved = json.loads(config_file.read_text())
        assert saved["gateway"]["url"] == "http://gw.example:7878"

    def test_save_refuses_to_clobber_corrupt_file(self, tmp_path):
        config_file = tmp_path / "config.json"
        config_file.write_text("{ not valid json ")

        config = CCDirectorConfig()
        config._config_path = config_file

        with pytest.raises(json.JSONDecodeError):
            config.save()
        assert config_file.read_text() == "{ not valid json "


class TestListKeys:
    def test_returns_sorted_list(self):
        config = CCDirectorConfig()
        keys = list_keys(config)
        assert isinstance(keys, list)
        assert keys == sorted(keys)
        assert len(keys) > 0

    def test_contains_expected_keys(self):
        config = CCDirectorConfig()
        keys = list_keys(config)
        assert "llm.default_provider" in keys
        assert "vault.vault_path" in keys
        assert "screenshots.source_directory" in keys
        assert "comm_manager.queue_path" in keys


class TestGetSectionNames:
    def test_returns_sorted_sections(self):
        config = CCDirectorConfig()
        names = get_section_names(config)
        assert names == sorted(names)

    def test_includes_all_sections(self):
        config = CCDirectorConfig()
        names = get_section_names(config)
        assert "llm" in names
        assert "photos" in names
        assert "vault" in names
        assert "comm_manager" in names
        assert "screenshots" in names
