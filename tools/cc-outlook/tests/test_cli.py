"""Regression tests for cc-outlook CLI behavior.

Covers the recipients command: it now exposes a boolean --json (consistent with
every other JSON-capable command in both email tools) instead of a
--format table|json option, and emits ASCII-only JSON (ensure_ascii=True).
"""

import json
from unittest.mock import MagicMock, patch

from typer.testing import CliRunner

from src.cli import app

runner = CliRunner()


def _patch_client(recipients):
    client = MagicMock()
    client.get_all_recipients.return_value = recipients
    return patch("src.cli.get_client", return_value=client)


def _extract_json(output):
    # The command prints a progress line to stderr before the JSON array; the
    # JSON payload starts at the first '['.
    idx = output.index("[")
    return output[idx:]


class TestRecipientsJson:
    def test_json_flag_outputs_ascii_json(self):
        recipients = {
            "jose@example.com": {"name": "Jos\xe9", "sent_count": 3},
        }
        with _patch_client(recipients):
            result = runner.invoke(app, ["recipients", "--json"])
        assert result.exit_code == 0, result.output
        segment = _extract_json(result.output)
        # Must be valid JSON and pure ASCII (the non-ASCII name is escaped).
        payload = json.loads(segment)
        assert payload[0]["email"] == "jose@example.com"
        segment.encode("ascii")

    def test_format_option_no_longer_accepted(self):
        with _patch_client({}):
            result = runner.invoke(app, ["recipients", "--format", "json"])
        # --format was removed; passing it is now an error.
        assert result.exit_code != 0
