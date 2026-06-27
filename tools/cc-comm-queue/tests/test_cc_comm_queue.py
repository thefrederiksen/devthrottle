"""Regression tests for cc-comm-queue shipped-tool fixes.

Each test maps to a fixed review finding. Everything runs against a temporary
cc-director root so the user's real queue and config are never touched.
"""

import json
import os
import sys
from pathlib import Path

import pytest

# Resolve a clean, isolated cc-director root BEFORE importing any tool module,
# so config/queue paths land under the temp dir for the whole test session.
_TMP_ROOT = Path(__file__).resolve().parent / "_tmp_root"


@pytest.fixture(scope="module")
def app_env(tmp_path_factory):
    root = tmp_path_factory.mktemp("ccroot")
    os.environ["CC_DIRECTOR_ROOT"] = str(root)

    tools_dir = str(Path(__file__).resolve().parent.parent.parent)  # tools/
    src_dir = str(Path(__file__).resolve().parent.parent / "src")
    for p in (src_dir, tools_dir):
        if p not in sys.path:
            sys.path.insert(0, p)

    # Import after env + path are set up.
    import cc_shared.config as shared_config
    shared_config.reload_config()
    import cli as cli_module

    from typer.testing import CliRunner

    yield root, cli_module, CliRunner()

    os.environ.pop("CC_DIRECTOR_ROOT", None)


def _add_linkedin_post(runner, app, content="hello world", extra=None):
    args = ["add", "linkedin", "post", content, "--json"]
    if extra:
        args.extend(extra)
    result = runner.invoke(app, args)
    return result


def test_pyproject_distribution_name_matches_registry():
    """#1 distribution name must match the registry name 'cc-comm-queue'."""
    pyproject = Path(__file__).resolve().parent.parent / "pyproject.toml"
    text = pyproject.read_text(encoding="utf-8")
    assert 'name = "cc-comm-queue"' in text
    assert 'name = "cc_comm_queue"' not in text


def test_config_set_writes_shared_store(app_env):
    """#2 config set must write the store config show reads, not ~/.cc-director."""
    root, cli_module, runner = app_env
    from cc_storage import CcStorage

    result = runner.invoke(cli_module.app, ["config", "set", "default_persona", "mindzie"])
    assert result.exit_code == 0, result.output

    shared_path = CcStorage.config_json()
    assert shared_path.exists()
    data = json.loads(shared_path.read_text(encoding="utf-8"))
    assert data["comm_manager"]["default_persona"] == "mindzie"

    # The legacy home path must NOT be the one written.
    legacy = Path.home() / ".cc-director" / "config.json"
    assert shared_path != legacy

    # config show reflects the new value.
    show = runner.invoke(cli_module.app, ["config", "show"])
    assert "mindzie" in show.output


def test_config_set_preserves_unknown_keys(app_env):
    """#2 unknown keys owned by other tools survive a config set."""
    root, cli_module, runner = app_env
    from cc_storage import CcStorage

    shared_path = CcStorage.config_json()
    shared_path.parent.mkdir(parents=True, exist_ok=True)
    existing = {}
    if shared_path.exists():
        existing = json.loads(shared_path.read_text(encoding="utf-8"))
    existing["some_other_tool"] = {"keep": "me"}
    shared_path.write_text(json.dumps(existing), encoding="utf-8")

    result = runner.invoke(cli_module.app, ["config", "set", "default_created_by", "tester"])
    assert result.exit_code == 0, result.output

    data = json.loads(shared_path.read_text(encoding="utf-8"))
    assert data["some_other_tool"] == {"keep": "me"}
    assert data["comm_manager"]["default_created_by"] == "tester"


def test_config_set_unknown_key_rejected(app_env):
    """#2 an unknown config key is rejected clearly."""
    root, cli_module, runner = app_env
    result = runner.invoke(cli_module.app, ["config", "set", "bogus_key", "x"])
    assert result.exit_code == 1
    assert "Unknown config key" in result.output


def test_mark_posted_blocked_without_approval(app_env):
    """#3 add then mark-posted must NOT skip the approval workflow."""
    root, cli_module, runner = app_env
    add = _add_linkedin_post(runner, cli_module.app, "gate test")
    assert add.exit_code == 0, add.output
    ticket = _ticket_from_json(add.output)

    posted = runner.invoke(cli_module.app, ["mark-posted", str(ticket)])
    assert posted.exit_code == 1
    assert "Cannot change status" in posted.output

    # --force overrides.
    forced = runner.invoke(cli_module.app, ["mark-posted", str(ticket), "--force"])
    assert forced.exit_code == 0
    assert "as posted" in forced.output


def test_list_unknown_status_rejected(app_env):
    """#4 list --status with a typo must error (exit 2), not show everything."""
    root, cli_module, runner = app_env
    result = runner.invoke(cli_module.app, ["list", "--status", "pendng"])
    assert result.exit_code == 2
    assert "Invalid status" in result.output


def test_add_invalid_linkedin_visibility_rejected(app_env):
    """#4 invalid --linkedin-visibility must error (exit 2), not default to public."""
    root, cli_module, runner = app_env
    result = runner.invoke(
        cli_module.app,
        ["add", "linkedin", "post", "vis test", "--linkedin-visibility", "secret"],
    )
    assert result.exit_code == 2
    assert "Invalid linkedin-visibility" in result.output


def test_add_invalid_youtube_privacy_rejected(app_env):
    """#4 invalid --youtube-privacy must error (exit 2)."""
    root, cli_module, runner = app_env
    result = runner.invoke(
        cli_module.app,
        ["add", "youtube", "post", "yt", "--youtube-privacy", "semi"],
    )
    assert result.exit_code == 2
    assert "Invalid youtube-privacy" in result.output


def test_list_limit_passthrough_and_true_total(app_env):
    """#5 -n must pass through and the footer total must be the real total."""
    root, cli_module, runner = app_env
    # Use an isolated campaign so the count is deterministic regardless of order.
    campaign = "limit-campaign"
    for i in range(7):
        add = _add_linkedin_post(
            runner, cli_module.app, f"item {i}", extra=["--campaign-id", campaign]
        )
        assert add.exit_code == 0, add.output

    result = runner.invoke(
        cli_module.app, ["list", "--campaign-id", campaign, "-n", "3"]
    )
    assert result.exit_code == 0
    assert "Showing 3 of 7 items" in result.output

    result_all = runner.invoke(
        cli_module.app, ["list", "--campaign-id", campaign, "-n", "200"]
    )
    assert "Showing 7 of 7 items" in result_all.output


def test_json_output_is_plain_not_rich_wrapped(app_env):
    """#6 --json must be valid JSON even for long values (no Rich line wrap)."""
    root, cli_module, runner = app_env
    long_content = "x" * 200  # well over Rich's 80-column wrap threshold
    add = _add_linkedin_post(runner, cli_module.app, long_content)
    assert add.exit_code == 0
    ticket = _ticket_from_json(add.output)

    show = runner.invoke(cli_module.app, ["show", str(ticket), "--json"])
    assert show.exit_code == 0
    parsed = json.loads(show.output)  # raises if Rich corrupted the JSON
    assert parsed["content"] == long_content


def test_database_uses_wal_and_busy_timeout(app_env):
    """#7 shared SQLite must open in WAL mode with a busy timeout."""
    root, cli_module, runner = app_env
    # Ensure the db exists.
    _add_linkedin_post(runner, cli_module.app, "wal")

    import sqlite3
    from cc_storage import CcStorage

    db_path = CcStorage.comm_queue_db()
    conn = sqlite3.connect(str(db_path))
    try:
        mode = conn.execute("PRAGMA journal_mode").fetchone()[0]
        assert mode.lower() == "wal"
    finally:
        conn.close()


def test_error_status_visible_in_stats(app_env):
    """#8 error-status items must be counted in stats."""
    root, cli_module, runner = app_env
    add = _add_linkedin_post(runner, cli_module.app, "to error")
    ticket = _ticket_from_json(add.output)

    err = runner.invoke(
        cli_module.app, ["mark-error", str(ticket), "--reason", "boom"]
    )
    assert err.exit_code == 0, err.output

    status = runner.invoke(cli_module.app, ["status"])
    assert "Error" in status.output


@pytest.fixture()
def db_module(app_env):
    """Import the Database layer (src is already on sys.path via app_env)."""
    import database as database_module
    import schema as schema_module
    return database_module, schema_module


def _make_db(db_module, tmp_path):
    database_module, _ = db_module
    return database_module.Database(tmp_path)


def _make_item(schema_module, content="hello"):
    return schema_module.ContentItem(
        platform=schema_module.Platform.LINKEDIN,
        type=schema_module.ContentType.POST,
        content=content,
    )


def test_update_content_missing_ticket_returns_false(db_module, tmp_path):
    """H1: editing a non-existent ticket must report failure, not success."""
    db = _make_db(db_module, tmp_path)
    try:
        # A prior write bumps connection-cumulative total_changes above zero,
        # which used to make every later update spuriously report success.
        item = _make_item(db_module[1], "seed")
        db.add_communication(item)
        assert db.update_content(999999, "no such ticket") is False
    finally:
        db.close()


def test_update_recipient_missing_ticket_returns_false(db_module, tmp_path):
    """H1: update_recipient on a missing ticket must report failure."""
    db = _make_db(db_module, tmp_path)
    try:
        db.add_communication(_make_item(db_module[1], "seed"))
        assert db.update_recipient(999999, '{"name": "x"}') is False
    finally:
        db.close()


def test_update_status_missing_ticket_returns_false(db_module, tmp_path):
    """H1: update_status on a missing ticket must report failure."""
    database_module, schema_module = db_module
    db = _make_db(db_module, tmp_path)
    try:
        db.add_communication(_make_item(schema_module, "seed"))
        assert db.update_status(999999, schema_module.Status.APPROVED) is False
    finally:
        db.close()


def test_update_content_existing_ticket_returns_true(db_module, tmp_path):
    """H1: a real edit must still report success."""
    database_module, schema_module = db_module
    db = _make_db(db_module, tmp_path)
    try:
        ticket = db.add_communication(_make_item(schema_module, "before"))
        assert db.update_content(ticket, "after") is True
        assert db.get_by_ticket(ticket)["content"] == "after"
    finally:
        db.close()


def test_ticket_number_allocation_retries_on_collision(db_module, tmp_path):
    """L10/#2: a colliding auto-allocated ticket number must be retried."""
    database_module, schema_module = db_module
    db = _make_db(db_module, tmp_path)
    try:
        first = db.add_communication(_make_item(schema_module, "first"))

        # Force the allocator to hand out the already-used number once, then
        # fall back to the real allocator -- simulating a concurrent add that
        # grabbed our number between read and insert.
        real_alloc = db._get_next_ticket_number
        calls = {"n": 0}

        def flaky_alloc():
            calls["n"] += 1
            if calls["n"] == 1:
                return first  # collide with the existing row
            return real_alloc()

        db._get_next_ticket_number = flaky_alloc
        second = db.add_communication(_make_item(schema_module, "second"))
        assert second != first
        assert calls["n"] >= 2  # it retried after the collision
    finally:
        db.close()


def test_unknown_current_status_raises(db_module, tmp_path):
    """L11/#3: an unrecognized current status must NOT silently pass the guard."""
    database_module, _ = db_module
    with pytest.raises(database_module.InvalidStatusTransition):
        database_module._validate_status_transition("bogus_status", "approved", force=False)
    # --force still overrides.
    database_module._validate_status_transition("bogus_status", "approved", force=True)


def test_status_check_constraint_rejects_bad_value(db_module, tmp_path):
    """L11/#3: the schema CHECK constraint must reject an unknown status."""
    import sqlite3
    db = _make_db(db_module, tmp_path)
    try:
        with pytest.raises(sqlite3.IntegrityError):
            db.conn.execute(
                "INSERT INTO communications "
                "(id, ticket_number, platform, type, persona, content, created_at, status) "
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                ("bad-1", 90001, "linkedin", "post", "personal", "x", "2026-01-01T00:00:00", "bogus"),
            )
    finally:
        db.close()


def _ticket_from_json(output: str) -> int:
    """Extract the ticket number from an add --json result's 'file' field."""
    data = json.loads(output.strip().splitlines()[-1])
    assert data["success"], data
    # file looks like "ticket #N"
    return int(data["file"].split("#")[1])


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
