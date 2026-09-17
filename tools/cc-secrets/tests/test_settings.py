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



def test_Import_ASkippedOrSettingValueThatIsACommonWord_DoesNotBreakEntryNamesOrTheAudit(store, tmp_path):
    # Found on the owner's machine: ORBI_ADMIN_USERNAME=admin was hidden during import, so entry names containing
    # "admin" were refused by the audit guard after the store had been saved.
    from src import paths
    from src.audit import AuditLog

    for mode in ("--skip", "--settings"):
        home_values = [f"ORBI_ADMIN_USERNAME=admin", f"ORBI_ADMIN_PASSWORD={new_secret()}", f"ADMIN_SERVICE_TOKEN={new_secret()}"]
        path = tmp_path / f"credentials{mode.strip('-')}.env"
        path.write_text("\n".join(home_values) + "\n", encoding="utf-8")

        result = runner.invoke(cli.app, ["import", str(path), "--agents", "--replace", mode, "ORBI_ADMIN_USERNAME"])

        assert result.exit_code == 0, _text(result)
        assert {"orbi-admin-password", "admin-service-token"} <= {e.name for e in store.entries()}
        audited = {l["entry"] for l in AuditLog(paths.audit_path()).read(100) if l["command"] == "import"}
        assert {"orbi-admin-password", "admin-service-token"} <= audited


def test_Import_AnAuditLineThatWouldBeRefused_ThroughTheRealUnchangedPath_ChangesNothing(store, tmp_path, monkeypatch):
    # The review's case (35b34779): an existing entry is recorded as "unchanged ... exists", which the old check
    # never built. A secret that is part of those words passed the check and the record refused it after saving.
    from src import paths

    add_entry(store, name="api-token")
    path = tmp_path / "credentials.env"
    path.write_text(f"API_TOKEN={new_secret()}\nNEW_KEY=changed\n", encoding="utf-8")

    result = runner.invoke(cli.app, ["import", str(path), "--agents"])

    assert result.exit_code != 0
    assert "nothing was imported" in _text(result)
    assert store.get("new-key") is None
    audit = paths.audit_path()
    assert not audit.exists() or "new-key" not in audit.read_text(encoding="utf-8")


def test_Import_AShortCommentedValue_DoesNotBlockTheImport(store, tmp_path):
    # The review's case: '# DEBUG=1' registered "1", and every audit line holds digits in its time.
    secret = new_secret()
    path = tmp_path / "credentials.env"
    path.write_text(f"# DEBUG=1\n# ENV=dev\nAPI_KEY={secret}\n", encoding="utf-8")

    result = runner.invoke(cli.app, ["import", str(path), "--agents"])

    assert result.exit_code == 0, _text(result)
    assert store.get("api-key").secret.reveal() == secret


def test_AStoredEntryWithAMissingOrUnknownKind_LoadsAsASecret_HiddenAndRefusedByGet(store, tmp_path):
    import json as _json
    from src import paths
    from src.storefile import UserOnlyFile

    secrets_by_kind = {}
    add_entry(store, name="no-kind")
    add_entry(store, name="odd-kind")
    document = _json.loads(paths.store_path().read_text(encoding="utf-8"))
    for record in document["entries"]:
        secrets_by_kind[record["name"]] = record["secret"]
        if record["name"] == "no-kind":
            record.pop("kind", None)
        else:
            record["kind"] = "Setting"
    UserOnlyFile(paths.store_path()).write((_json.dumps(document) + "\n").encode("utf-8"))
    SCRUBBER.clear()

    entries = {e.name: e for e in store.entries()}
    for name in ("no-kind", "odd-kind"):
        assert entries[name].is_setting is False
        assert SCRUBBER.scrub(secrets_by_kind[name]) == REDACTED
        result = runner.invoke(cli.app, ["get", name])
        assert result.exit_code == cli.EXIT_REFUSED
        assert secrets_by_kind[name] not in _text(result)


def test_ListTable_ASettingWhoseValueHoldsAStoredSecret_ShowsTheSecretHidden(store, tmp_path, plain, monkeypatch):
    # Wide enough that no cell is cut short: a truncated table would pass "not in the output" without redaction.
    from rich.console import Console
    monkeypatch.setattr(cli, "console", Console(width=400))
    secret = new_secret()
    add_entry(store, name="service-token", secret=secret)
    path = tmp_path / "credentials.env"
    path.write_text(f"SERVICE_URL=https://x.example.com/?t={secret}\n", encoding="utf-8")
    runner.invoke(cli.app, ["import", str(path), "--agents", "--settings", "SERVICE_URL"])

    result = runner.invoke(cli.app, ["list"])

    text = plain(result.output)
    assert "service-url" in text and "https://x.example.com/?t=" in text, "the table did not print the row"
    assert secret not in result.output
