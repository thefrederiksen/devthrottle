"""Changing an entry's details without re-entering its secret - so an imported password can be set up for login."""

from typer.testing import CliRunner

from conftest import add_entry, new_secret
from src import cli, paths
from src.audit import AuditLog

runner = CliRunner()


def _text(result):
    return result.output + (result.stderr if result.stderr_bytes is not None else "")


def test_Edit_SetsUsernameDomainsAndUses_AndKeepsTheSecret(store):
    secret = add_entry(store, name="mindzie-qa", username="", domains=(), uses=("run",))
    created = store.get("mindzie-qa").created_utc

    result = runner.invoke(cli.app, ["edit", "mindzie-qa", "--username", "qa@example.com",
                                     "--domains", "https://localhost:7330", "--uses", "login,run"])

    assert result.exit_code == 0, _text(result)
    entry = store.get("mindzie-qa")
    assert entry.secret.reveal() == secret
    assert entry.username == "qa@example.com"
    assert entry.allowed_domains == ["https://localhost:7330"]
    assert entry.uses == ["login", "run"]
    assert entry.created_utc == created
    assert secret not in _text(result)


def test_Edit_LeavesEveryDetailNotGiven_AsItWas(store):
    add_entry(store, name="web", username="me", domains=("https://example.com",), uses=("login",), notes="keep")

    runner.invoke(cli.app, ["edit", "web", "--no-agents"])

    entry = store.get("web")
    assert (entry.username, entry.allowed_domains, entry.uses, entry.notes, entry.agents_may_use) == \
        ("me", ["https://example.com"], ["login"], "keep", False)


def test_Edit_AnEmptyDomainsValue_ClearsThem(store):
    add_entry(store, name="web", domains=("https://example.com",))

    runner.invoke(cli.app, ["edit", "web", "--domains="])

    assert store.get("web").allowed_domains == []


def test_Edit_InsideASession_IsRefused_AndChangesNothing(store, monkeypatch):
    add_entry(store, name="web", username="me")
    monkeypatch.setenv("CC_SESSION_ID", "agent-session")

    result = runner.invoke(cli.app, ["edit", "web", "--username", "someone-else"])

    assert result.exit_code == cli.EXIT_REFUSED
    assert store.get("web").username == "me"


def test_Edit_WithNothingToChange_OrAMissingEntry_Fails(store):
    add_entry(store, name="web")

    assert runner.invoke(cli.app, ["edit", "web"]).exit_code != 0
    assert runner.invoke(cli.app, ["edit", "not-there", "--username", "x"]).exit_code != 0


def test_Edit_ASetting_KeepsItASetting(store, tmp_path):
    path = tmp_path / "credentials.env"
    path.write_text("SERVICE_HOST=https://svc.example.com\n", encoding="utf-8")
    runner.invoke(cli.app, ["import", str(path), "--agents", "--settings", "SERVICE_HOST"])

    result = runner.invoke(cli.app, ["edit", "service-host", "--notes", "the service"])

    assert result.exit_code == 0, _text(result)
    entry = store.get("service-host")
    assert entry.is_setting and entry.secret.reveal() == "https://svc.example.com" and entry.notes == "the service"


def test_Edit_IsAudited_ByFieldName_WithoutTheSecret(store):
    secret = add_entry(store, name="web")

    runner.invoke(cli.app, ["edit", "web", "--username", "me2", "--uses", "run"])

    line = [l for l in AuditLog(paths.audit_path()).read(20) if l["command"] == "edit"][-1]
    assert line["entry"] == "web" and "username" in line["detail"] and "uses" in line["detail"]
    assert secret not in str(line)


def test_Edit_AnOptionSwallowedAsAValue_IsRefused_AndChangesNothing(store, paths_snapshot=None):
    # The review's case: PowerShell 5.1 dropped "" from `--username "" --no-agents`.
    import json as _json
    from src import paths as _paths

    add_entry(store, name="web", username="me")
    before = _paths.store_path().read_bytes()

    for args in (["--username", "--no-agents"], ["--notes", "--agents"], ["--domains", "--uses"]):
        result = runner.invoke(cli.app, ["edit", "web", *args])
        assert result.exit_code != 0, args
        assert _paths.store_path().read_bytes() == before, args
    assert "--username=" in _text(runner.invoke(cli.app, ["edit", "web", "--username", "--no-agents"]))


def test_Add_AnOptionSwallowedAsAValue_IsRefused_AndNothingIsSaved(store):
    # "--username --no-agents": a well-formed command line that only the guard refuses, not a usage error.
    result = runner.invoke(cli.app, ["add", "web", "--username", "--no-agents", "--domains", "https://example.com",
                                     "--agents"], input=new_secret() + "\n")

    assert result.exit_code != 0
    assert "--username=" in _text(result)
    assert store.get("web") is None


def test_Edit_AppliesTheSameValidationAsAdd_AndAFailureChangesNothing(store):
    # The review's mutation: edit building the entry without make_entry went unnoticed.
    from src import paths as _paths

    add_entry(store, name="web")
    before = _paths.store_path().read_bytes()

    for args in (["--domains", "ftp://x"], ["--env-name", "1BAD"], ["--uses", "fill"]):
        result = runner.invoke(cli.app, ["edit", "web", *args])
        assert result.exit_code != 0, args
        assert _paths.store_path().read_bytes() == before, args
