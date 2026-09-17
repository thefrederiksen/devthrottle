"""Settings: values that are not secret (hosts, email addresses, identifiers) kept in cc-secrets beside the secrets,
so every credential has one home. A setting can be read back and is never hidden from output; a secret is still
never printed."""

import json
import sys

from typer.testing import CliRunner

from conftest import add_entry, new_secret
from src import cli
from src.redact import REDACTED, SCRUBBER

runner = CliRunner()
PY = sys.executable
HOST = "https://us.posthog.com"


def _text(result):
    return result.output + (result.stderr if result.stderr_bytes is not None else "")


def _import(tmp_path, lines, *extra):
    path = tmp_path / "credentials.env"
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return runner.invoke(cli.app, ["import", str(path), "--agents", *extra])


def test_Import_Settings_AreStoredAsSettings_AndTheRestAsSecrets(store, tmp_path):
    key = new_secret()

    result = _import(tmp_path, [f"POSTHOG_HOST={HOST}", f"POSTHOG_PERSONAL_API_KEY={key}"], "--settings", "POSTHOG_HOST")

    assert result.exit_code == 0, _text(result)
    assert store.get("posthog-host").kind == "setting"
    assert store.get("posthog-personal-api-key").kind == "secret"
    assert store.get("posthog-host").secret.reveal() == HOST


def test_Import_AShortSetting_IsAccepted(store, tmp_path):
    result = _import(tmp_path, ["DEPLOY_ENV=dev"], "--settings", "DEPLOY_ENV")

    assert result.exit_code == 0, _text(result)
    assert store.get("deploy-env").secret.reveal() == "dev"


def test_Import_AKeyInBothSkipAndSettings_IsRefused_AndNothingImported(store, tmp_path):
    result = _import(tmp_path, [f"POSTHOG_HOST={HOST}"], "--settings", "POSTHOG_HOST", "--skip", "POSTHOG_HOST")

    assert result.exit_code != 0
    assert store.entries() == []


def test_Import_ASettingInsideACredentialKey_DoesNotBlockIt(store, tmp_path):
    secret = new_secret()

    result = _import(tmp_path, ["ENV=PROD", f"PROD_API_KEY={secret}"], "--settings", "ENV")

    assert result.exit_code == 0, _text(result)
    assert store.get("prod-api-key").secret.reveal() == secret


def test_Get_PrintsASettingsValue(store, tmp_path):
    _import(tmp_path, [f"POSTHOG_HOST={HOST}"], "--settings", "POSTHOG_HOST")

    result = runner.invoke(cli.app, ["get", "posthog-host"])

    assert result.exit_code == 0, _text(result)
    assert result.output.strip() == HOST


def test_Get_ASecret_IsRefused_AndPrintsNothingOfIt(store):
    secret = add_entry(store, name="github-token")

    result = runner.invoke(cli.app, ["get", "github-token"])

    assert result.exit_code == cli.EXIT_REFUSED
    assert "never printed" in _text(result)
    assert secret not in _text(result)


def test_Get_ASettingAgentsMayNotUse_IsRefused(store, tmp_path):
    path = tmp_path / "credentials.env"
    path.write_text(f"POSTHOG_HOST={HOST}\n", encoding="utf-8")
    runner.invoke(cli.app, ["import", str(path), "--no-agents", "--settings", "POSTHOG_HOST"])

    result = runner.invoke(cli.app, ["get", "posthog-host"])

    assert result.exit_code == cli.EXIT_REFUSED
    assert HOST not in result.output


def test_List_ShowsASettingsValue_ButNeverASecret(store, tmp_path):
    secret = new_secret()
    _import(tmp_path, [f"POSTHOG_HOST={HOST}", f"POSTHOG_PERSONAL_API_KEY={secret}"], "--settings", "POSTHOG_HOST")

    result = runner.invoke(cli.app, ["list", "--json"])

    entries = {e["name"]: e for e in json.loads(result.stdout)["entries"]}
    assert entries["posthog-host"]["value"] == HOST
    assert "value" not in entries["posthog-personal-api-key"]
    assert secret not in result.stdout


def test_Run_ASettingIsSuppliedAndNotHidden_WhileTheSecretBesideItIs(store, tmp_path):
    secret = new_secret()
    _import(tmp_path, [f"POSTHOG_HOST={HOST}", f"POSTHOG_PERSONAL_API_KEY={secret}"], "--settings", "POSTHOG_HOST")
    script = "import os; print(os.environ['POSTHOG_HOST']); print(os.environ['POSTHOG_PERSONAL_API_KEY'])"

    result = runner.invoke(cli.app, ["run", "posthog-personal-api-key", "--with", "posthog-host", "--", PY, "-c", script])

    assert result.exit_code == 0, _text(result)
    assert HOST in result.output
    assert REDACTED in result.output and secret not in _text(result)


def test_ReadingTheStore_DoesNotHideSettings(store, tmp_path):
    _import(tmp_path, [f"POSTHOG_HOST={HOST}"], "--settings", "POSTHOG_HOST")
    SCRUBBER.clear()

    store.entries()

    assert SCRUBBER.scrub(f"querying {HOST}") == f"querying {HOST}"


def test_Login_WithASetting_IsRefused(store, tmp_path):
    _import(tmp_path, [f"SITE_EMAIL=soren@example.com"], "--settings", "SITE_EMAIL", "--uses", "login,run")

    try:
        store.entry_for_agent("site-email", "login")
        refused = False
    except Exception as exc:
        refused = "setting" in str(exc)

    assert refused


def test_AddSetting_PipedValue_IsStoredAsASetting(store):
    result = runner.invoke(cli.app, ["add", "service-host", "--username", "", "--domains", "", "--agents", "--setting",
                                     "--env-name", "SERVICE_HOST"], input=HOST + "\n")

    assert result.exit_code == 0, _text(result)
    entry = store.get("service-host")
    assert entry.kind == "setting" and entry.env_name == "SERVICE_HOST" and entry.secret.reveal() == HOST
