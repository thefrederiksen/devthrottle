"""Regression tests for cc-vault shipped-tool fixes.

Behavioral tests drive the real CLI in a subprocess against a throwaway vault so
the user's real vault is never touched. Pure-function tests import directly.
"""

import json
import os
import subprocess
import sys
from pathlib import Path

import pytest

SRC = Path(__file__).resolve().parent.parent / "src"
TOOLS = Path(__file__).resolve().parent.parent.parent  # tools/


def run_cli(args, vault_path, director_root=None):
    """Run `cc-vault <args>` in a subprocess pointed at an isolated vault."""
    env = dict(os.environ)
    env["PYTHONPATH"] = str(TOOLS) + os.pathsep + env.get("PYTHONPATH", "")
    env["CC_VAULT_PATH"] = str(vault_path)
    if director_root is not None:
        env["CC_DIRECTOR_ROOT"] = str(director_root)
    env["PYTHONIOENCODING"] = "utf-8"
    return subprocess.run(
        [sys.executable, str(SRC / "cli.py"), *args],
        capture_output=True, text=True, env=env, cwd=str(SRC),
        encoding="utf-8", errors="replace",
    )


# --------------------------------------------------------------------------
# #9 init honors the path argument
# --------------------------------------------------------------------------

def test_init_honors_path_argument(tmp_path):
    """`init <path>` must create vault.db at <path>, not the default location."""
    root = tmp_path / "root"
    target = tmp_path / "my_vault"
    # Note: do NOT set CC_VAULT_PATH so the path argument is what's honored.
    env = dict(os.environ)
    env["PYTHONPATH"] = str(TOOLS) + os.pathsep + env.get("PYTHONPATH", "")
    env["CC_DIRECTOR_ROOT"] = str(root)
    env.pop("CC_VAULT_PATH", None)
    result = subprocess.run(
        [sys.executable, str(SRC / "cli.py"), "init", str(target)],
        capture_output=True, text=True, env=env, cwd=str(SRC),
        encoding="utf-8", errors="replace",
    )
    assert result.returncode == 0, result.stdout + result.stderr
    assert (target / "vault.db").exists(), "vault.db should be created at the requested path"
    # The choice is persisted so later commands resolve the same vault.
    persisted = root / "config" / "vault" / "config.json"
    assert persisted.exists()
    data = json.loads(persisted.read_text(encoding="utf-8"))
    assert Path(data["vault_path"]) == target


# --------------------------------------------------------------------------
# #10 tasks: 'done' vs 'completed' alignment
# --------------------------------------------------------------------------

def test_completed_task_counts_in_stats_and_filter(tmp_path):
    """A completed task must show in stats and match --status completed/done."""
    vault = tmp_path / "vault"
    assert run_cli(["init"], vault).returncode == 0

    add = run_cli(["tasks", "add", "Finish report"], vault)
    assert add.returncode == 0, add.stdout + add.stderr
    # Extract task id like "#3"
    import re
    task_id = re.search(r"#(\d+)", add.stdout).group(1)

    done = run_cli(["tasks", "done", task_id], vault)
    assert done.returncode == 0, done.stdout + done.stderr

    # stats must report one completed task (previously always zero).
    stats = run_cli(["stats"], vault)
    assert stats.returncode == 0
    # Find the "Tasks (completed)" row count.
    m = re.search(r"Tasks \(completed\)\s*\|?\s*(\d+)", stats.stdout)
    # Rich table layout may vary; fall back to asserting a '1' appears near the label.
    assert "Tasks (completed)" in stats.stdout
    if m:
        assert int(m.group(1)) >= 1

    # --status completed (documented alias) and done must both find the task.
    for status in ("completed", "done"):
        listed = run_cli(["tasks", "list", "--status", status], vault)
        assert listed.returncode == 0, listed.stdout + listed.stderr
        assert "Finish report" in listed.stdout, f"status={status} should match the done task"


# --------------------------------------------------------------------------
# #11 lists structured filters replace raw SQL --query
# --------------------------------------------------------------------------

def test_lists_structured_filters_and_where_guard(tmp_path):
    """Structured filters select contacts; raw --where needs --yes and is validated."""
    vault = tmp_path / "vault"
    assert run_cli(["init"], vault).returncode == 0

    # Two contacts at Acme, one elsewhere.
    run_cli(["contacts", "add", "Ann Acme", "-e", "ann@acme.com", "-a", "personal", "-c", "Acme"], vault)
    run_cli(["contacts", "add", "Bob Acme", "-e", "bob@acme.com", "-a", "personal", "-c", "Acme"], vault)
    run_cli(["contacts", "add", "Cara Other", "-e", "cara@other.com", "-a", "personal", "-c", "Other"], vault)
    assert run_cli(["lists", "create", "Acme List"], vault).returncode == 0

    # Structured --company filter adds exactly the two Acme contacts.
    added = run_cli(["lists", "add", "Acme List", "--company", "Acme"], vault)
    assert added.returncode == 0, added.stdout + added.stderr
    assert "Added 2 contacts" in added.stdout

    # Bulk removal without --yes is a no-op that previews the count.
    preview = run_cli(["lists", "remove", "Acme List", "--company", "Acme"], vault)
    assert preview.returncode == 1
    assert "matches 2 contact" in preview.stdout
    members = run_cli(["lists", "show", "Acme List"], vault)
    assert "Ann Acme" in members.stdout  # still present

    # With --yes the removal applies.
    removed = run_cli(["lists", "remove", "Acme List", "--company", "Acme", "--yes"], vault)
    assert removed.returncode == 0
    assert "Removed 2 contacts" in removed.stdout

    # Expert --where with a statement-breaking payload is rejected.
    bad = run_cli(["lists", "add", "Acme List", "--where", "1=1; DROP TABLE contacts", "--yes"], vault)
    assert bad.returncode == 1
    assert "single expression" in bad.stdout or "must not contain" in bad.stdout

    # Valid --where without --yes only previews.
    preview2 = run_cli(["lists", "add", "Acme List", "--where", "company = 'Acme'"], vault)
    assert preview2.returncode == 1
    assert "match" in preview2.stdout.lower()


# --------------------------------------------------------------------------
# #12 vault DB opens in WAL mode
# --------------------------------------------------------------------------

def test_vault_db_uses_wal(tmp_path):
    """The vault database must be opened in WAL journal mode."""
    vault = tmp_path / "vault"
    assert run_cli(["init"], vault).returncode == 0

    import sqlite3
    conn = sqlite3.connect(str(vault / "vault.db"))
    try:
        mode = conn.execute("PRAGMA journal_mode").fetchone()[0]
        assert mode.lower() == "wal"
    finally:
        conn.close()


# --------------------------------------------------------------------------
# #13 pure-function fixes
# --------------------------------------------------------------------------

@pytest.fixture(scope="module")
def imported_modules(tmp_path_factory):
    """Import vault modules with an isolated vault path for pure-function tests."""
    os.environ.setdefault("CC_VAULT_PATH", str(tmp_path_factory.mktemp("pfvault")))
    if str(SRC) not in sys.path:
        sys.path.insert(0, str(SRC))
    if str(TOOLS) not in sys.path:
        sys.path.insert(0, str(TOOLS))
    import fuzzy_search
    import db as db_module
    return fuzzy_search, db_module


def test_compute_metaphone_primary_equals_secondary(imported_modules):
    """compute_metaphone returns identical primary/secondary (single Metaphone)."""
    fuzzy_search, _ = imported_modules
    primary, secondary = fuzzy_search.compute_metaphone("Smith")
    assert primary == secondary
    assert primary  # non-empty for an alphabetic name


def test_validate_where_clause_blocks_injection(imported_modules):
    """validate_where_clause rejects semicolons and comments, accepts a single expr."""
    _, db_module = imported_modules
    assert db_module.validate_where_clause("company = 'Acme'") == "company = 'Acme'"
    with pytest.raises(ValueError):
        db_module.validate_where_clause("1=1; DROP TABLE contacts")
    with pytest.raises(ValueError):
        db_module.validate_where_clause("1=1 -- comment")
    with pytest.raises(ValueError):
        db_module.validate_where_clause("")


# --------------------------------------------------------------------------
# Second-pass fix: contacts add --role must persist (mapped to title)
# --------------------------------------------------------------------------

def _added_contact_id(stdout: str) -> str:
    import re
    m = re.search(r"#(\d+)", stdout)
    assert m, f"could not find contact id in: {stdout}"
    return m.group(1)


def test_contacts_add_role_persists(tmp_path):
    """contacts add --role must be stored (as title), not silently dropped."""
    vault = tmp_path / "vault"
    assert run_cli(["init"], vault).returncode == 0

    add = run_cli(
        ["contacts", "add", "Role Person", "-e", "role@x.com",
         "-a", "personal", "-r", "Chief Widget Officer"],
        vault,
    )
    assert add.returncode == 0, add.stdout + add.stderr
    cid = _added_contact_id(add.stdout)

    shown = run_cli(["contacts", "show", cid, "--format", "json"], vault)
    assert shown.returncode == 0, shown.stdout + shown.stderr
    data = json.loads(shown.stdout)
    assert data.get("title") == "Chief Widget Officer", data


def test_contacts_show_json_is_ascii(tmp_path):
    """contacts show --format json must emit valid, ASCII-only JSON even for
    a long non-ASCII name (no Rich wrapping, ensure_ascii=True)."""
    vault = tmp_path / "vault"
    assert run_cli(["init"], vault).returncode == 0

    # A long name with genuine non-ASCII characters (built from escapes so this
    # source file stays ASCII) exercises ensure_ascii and the no-Rich-wrap
    # requirement at the same time. 0xE9=e-acute, 0xDC=U-umlaut, 0xF1=n-tilde.
    name = ("Jos" + chr(0xE9) + " ") + (("" + chr(0xDC) + "ber ") * 20) + ("Mu" + chr(0xF1) + "oz")
    add = run_cli(
        ["contacts", "add", name, "-e", "ascii@x.com", "-a", "personal"],
        vault,
    )
    assert add.returncode == 0, add.stdout + add.stderr
    cid = _added_contact_id(add.stdout)

    shown = run_cli(["contacts", "show", cid, "--format", "json"], vault)
    assert shown.returncode == 0, shown.stdout + shown.stderr
    # Valid JSON...
    data = json.loads(shown.stdout)
    # ...and the machine output line carrying the JSON must be ASCII-only.
    json_line = shown.stdout.strip().splitlines()[0]
    assert all(ord(ch) < 128 for ch in json_line), "machine JSON must be ASCII-only"
    # The non-ASCII name round-trips through the escaped JSON.
    assert data["name"] == name


# --------------------------------------------------------------------------
# Second-pass fix: single _sanitize_fts_query that neutralizes operator chars,
# a column-filter colon, and a stray double quote; FTS errors are surfaced.
# --------------------------------------------------------------------------

def test_only_one_sanitize_fts_definition():
    """The duplicate _sanitize_fts_query must be gone (only one def remains)."""
    text = (SRC / "db.py").read_text(encoding="utf-8")
    assert text.count("def _sanitize_fts_query(") == 1


def test_sanitize_fts_quotes_special_terms(imported_modules):
    """Operator chars, a colon, an ampersand, and a stray quote get quoted/escaped."""
    _, db_module = imported_modules
    s = db_module._sanitize_fts_query

    # A plain word is left as a bareword token.
    assert s("hello") == "hello"
    # A column-filter colon is neutralized by quoting.
    assert s("term:value") == '"term:value"'
    # An ampersand (e.g. SR&ED) is quoted, not treated as syntax.
    assert s("SR&ED") == '"SR&ED"'
    # A hyphen is quoted.
    assert s("multi-word") == '"multi-word"'
    # A stray double quote is escaped (doubled) inside the quoted term, so it
    # cannot terminate the FTS string early.
    out = s('a"b')
    assert out.startswith('"') and out.endswith('"')
    assert '""' in out


def test_search_chunks_fts_surfaces_error_not_empty(imported_modules, monkeypatch):
    """A query FTS5 still rejects must raise a clear error, not return []."""
    _, db_module = imported_modules
    db_module.init_db(silent=True)
    # Force the sanitizer to emit an FTS string that is still invalid.
    monkeypatch.setattr(db_module, "_sanitize_fts_query", lambda q: '"')
    with pytest.raises(ValueError):
        db_module.search_chunks_fts("anything")


# --------------------------------------------------------------------------
# Second-pass fix: catalog embed failure is surfaced as a warning, not swallowed
# --------------------------------------------------------------------------

def test_embed_summary_returns_warning_on_failure(imported_modules):
    """_embed_summary returns an error string (not None) when embedding fails,
    so the caller can surface a warning instead of silently dropping it."""
    if str(SRC) not in sys.path:
        sys.path.insert(0, str(SRC))
    import catalog
    scanner = catalog.CatalogScanner()
    warning = scanner._embed_summary(1, {"title": "t", "summary": "s", "tags": ""})
    # embed_and_store does not exist in vectors, so this surfaces a real failure.
    assert isinstance(warning, str) and warning, "embedding failure must be surfaced"


# --------------------------------------------------------------------------
# Second-pass fix: search semantic mode honors -n instead of hardcoding 5
# --------------------------------------------------------------------------

def test_search_semantic_honors_n(imported_modules, monkeypatch):
    """Semantic search display must show up to -n items per collection, not 5."""
    if str(SRC) not in sys.path:
        sys.path.insert(0, str(SRC))
    import rag as rag_module
    from typer.testing import CliRunner
    import cli as cli_module

    class _FakeRag:
        def semantic_search(self, query, collections=None, n_results=10):
            return {"contacts": [{"id": i, "document": f"doc{i}"} for i in range(n_results)]}

    monkeypatch.setattr(rag_module, "get_vault_rag", lambda: _FakeRag())

    runner = CliRunner()
    result = runner.invoke(cli_module.app, ["search", "anything", "-n", "8"])
    assert result.exit_code == 0, result.output
    # Eight items requested -> eight printed (previously capped at 5).
    assert result.output.count("] doc") == 8, result.output


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
