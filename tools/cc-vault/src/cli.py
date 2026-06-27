"""CLI for cc-vault - Personal Vault from the command line."""

import json
import logging
import sqlite3
import os
import sys
import zipfile

os.environ.setdefault("PYTHONIOENCODING", "utf-8")

# Fix Windows console encoding for non-ASCII characters (e.g. Turkish names)
if sys.stdout.encoding and sys.stdout.encoding.lower() != 'utf-8':
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')
from datetime import datetime
from pathlib import Path
from types import ModuleType
from typing import Any, Optional, List

import typer
from rich import box
from rich.console import Console
from rich.table import Table as _RichTable
from rich.panel import Panel as _RichPanel
from rich.text import Text

# --- ASCII-only output (project house rule): Rich truncates an overflowing table cell with the
# Unicode ellipsis U+2026; emit ASCII "..." instead. Patched once at module import. ---
def _install_ascii_truncation():
    import rich.text
    from rich.cells import set_cell_size
    _orig = rich.text.Text.truncate
    if getattr(_orig, "_ascii_ellipsis", False):
        return
    def truncate(self, max_width, *, overflow=None, pad=False):
        _orig(self, max_width, overflow=overflow, pad=pad)
        if "\u2026" in self.plain:
            self.plain = set_cell_size(self.plain.replace("\u2026", ""), max(0, max_width - 3)) + "..."
            if pad and len(self.plain) < max_width:
                self.plain += " " * (max_width - len(self.plain))
    truncate._ascii_ellipsis = True
    rich.text.Text.truncate = truncate


_install_ascii_truncation()


def Table(*args, **kwargs):
    """Rich Table defaulting to ASCII box drawing (house ASCII-only rule).

    Callers passing box=... (including box=None) keep their explicit choice.
    """
    kwargs.setdefault("box", box.ASCII)
    return _RichTable(*args, **kwargs)


def Panel(*args, **kwargs):
    """Rich Panel defaulting to ASCII box drawing (house ASCII-only rule)."""
    kwargs.setdefault("box", box.ASCII)
    return _RichPanel(*args, **kwargs)

# Configure logger
logger = logging.getLogger(__name__)

# Handle imports for both package and frozen executable
try:
    from . import __version__
    from .config import (
        VAULT_PATH, DB_PATH, VECTORS_PATH, DOCUMENTS_PATH,
        get_vault_path, get_config
    )
except ImportError:
    # Running as frozen executable
    import __init__ as pkg
    __version__ = pkg.__version__
    from config import (
        VAULT_PATH, DB_PATH, VECTORS_PATH, DOCUMENTS_PATH,
        get_vault_path, get_config
    )

# Configure logging
logging.basicConfig(level=logging.INFO, format="%(message)s")


class AliasGroup(typer.core.TyperGroup):
    """Typer Group with command aliases and 'did you mean?' suggestions."""

    ALIASES = {
        "get": "show",
        "view": "show",
        "members": "show",
        "ls": "list",
        "find": "search",
        "rm": "delete",
        "del": "delete",
        "edit": "update",
        "modify": "update",
        "new": "add",
        "insert": "add",
        "mv": "rename",
    }

    def get_command(self, ctx, cmd_name):
        resolved = self.ALIASES.get(cmd_name, cmd_name)
        return super().get_command(ctx, resolved)


app = typer.Typer(
    name="cc-vault",
    help="Personal Vault CLI: manage contacts, tasks, goals, ideas, documents, and more.",
    add_completion=False,
    cls=AliasGroup,
)

# Sub-apps -- cls=AliasGroup adds "get/view -> show" aliases and "did you mean?" suggestions
tasks_app = typer.Typer(help="Task management", cls=AliasGroup)
goals_app = typer.Typer(help="Goal tracking", cls=AliasGroup)
ideas_app = typer.Typer(help="Idea capture", cls=AliasGroup)
contacts_app = typer.Typer(help="Contact management", cls=AliasGroup)
docs_app = typer.Typer(help="Document management", cls=AliasGroup)
config_app = typer.Typer(help="Configuration", cls=AliasGroup)
health_app = typer.Typer(help="Health data", cls=AliasGroup)
posts_app = typer.Typer(help="Social media posts", cls=AliasGroup)
lists_app = typer.Typer(help="Contact list management", cls=AliasGroup)
tags_app = typer.Typer(help="Contact tag management", cls=AliasGroup)
library_app = typer.Typer(help="Document library management", cls=AliasGroup)
catalog_app = typer.Typer(help="Document catalog", cls=AliasGroup)

app.add_typer(tasks_app, name="tasks")
app.add_typer(goals_app, name="goals")
app.add_typer(ideas_app, name="ideas")
app.add_typer(contacts_app, name="contacts")
app.add_typer(docs_app, name="docs")
app.add_typer(config_app, name="config")
app.add_typer(health_app, name="health")
app.add_typer(posts_app, name="posts")
app.add_typer(lists_app, name="lists")
app.add_typer(tags_app, name="tags")
app.add_typer(library_app, name="library")
app.add_typer(catalog_app, name="catalog")

# Singular aliases (hidden so they don't clutter --help)
app.add_typer(tasks_app, name="task", hidden=True)
app.add_typer(goals_app, name="goal", hidden=True)
app.add_typer(ideas_app, name="idea", hidden=True)
app.add_typer(contacts_app, name="contact", hidden=True)
app.add_typer(docs_app, name="doc", hidden=True)
app.add_typer(posts_app, name="post", hidden=True)
app.add_typer(lists_app, name="list", hidden=True)
app.add_typer(tags_app, name="tag", hidden=True)

_is_tty = sys.stdout.isatty()
console = Console(force_terminal=_is_tty, no_color=not _is_tty)


def version_callback(value: bool) -> None:
    """Print version and exit if --version flag is set."""
    if value:
        console.print(f"cc-vault version {__version__}")
        raise typer.Exit()


def get_db() -> ModuleType:
    """Get initialized database module."""
    try:
        from . import db
    except ImportError:
        import db
    db.init_db(silent=True)
    return db


@app.callback(invoke_without_command=True)
def main(
    ctx: typer.Context,
    version: bool = typer.Option(
        False,
        "--version",
        "-v",
        callback=version_callback,
        is_eager=True,
        help="Show version and exit",
    ),
):
    """Personal Vault CLI: manage contacts, tasks, goals, ideas, and documents."""
    if ctx.invoked_subcommand is None:
        console.print(ctx.get_help())


# =============================================================================
# Main Commands
# =============================================================================

@app.command()
def init(
    path: Optional[str] = typer.Argument(None, help="Vault path (default: %LOCALAPPDATA%\\cc-director\\vault)"),
    force: bool = typer.Option(False, "--force", "-f", help="Reinitialize if exists"),
):
    """Initialize a new vault.

    When a path is given, the database and directories are created there and the
    choice is persisted so later commands use the same vault.
    """
    try:
        from .config import ensure_directories, save_config, get_vault_path
        from . import db as db_module
    except ImportError:
        from config import ensure_directories, save_config, get_vault_path
        import db as db_module

    vault_path = Path(path).resolve() if path else get_vault_path()
    db_path = vault_path / "vault.db"

    if db_path.exists() and not force:
        console.print(f"[yellow]Vault already exists at:[/yellow] {vault_path}")
        console.print("Use --force to reinitialize.")
        return

    try:
        if path:
            # Honor the requested path: make every dynamic resolver target it for
            # this process, point the database layer at it, and persist the choice.
            os.environ["CC_VAULT_PATH"] = str(vault_path)
            db_module.set_db_path(db_path)
            save_config(str(vault_path))

        ensure_directories()
        db_module.init_db(silent=True)
        console.print(f"[green]Vault initialized at:[/green] {vault_path}")
        console.print(f"  Database: {db_path}")
        console.print(f"  Documents: {vault_path / 'documents'}")
    except (OSError, sqlite3.Error) as e:
        console.print(f"[red]Error initializing vault:[/red] {e}")
        raise typer.Exit(1)


@app.command()
def stats() -> None:
    """Show vault statistics."""
    db = get_db()

    try:
        stats_data = db.get_vault_stats()

        table = Table(title=f"Vault Statistics")
        table.add_column("Category", style="cyan")
        table.add_column("Count", justify="right")

        table.add_row("Contacts", str(stats_data.get('contacts', 0)))
        table.add_row("Tasks (pending)", str(stats_data.get('tasks_pending', 0)))
        table.add_row("Tasks (completed)", str(stats_data.get('tasks_completed', 0)))
        table.add_row("Goals (active)", str(stats_data.get('goals_active', 0)))
        table.add_row("Ideas", str(stats_data.get('ideas', 0)))
        table.add_row("Documents", str(stats_data.get('documents', 0)))
        table.add_row("Health Entries", str(stats_data.get('health_entries', 0)))
        if stats_data.get('social_posts', 0) > 0:
            table.add_row("Social Posts", str(stats_data.get('social_posts', 0)))
            table.add_row("  - Draft", str(stats_data.get('social_posts_draft', 0)))
            table.add_row("  - Posted", str(stats_data.get('social_posts_posted', 0)))

        console.print(table)
        console.print(f"\n[dim]Vault path: {VAULT_PATH}[/dim]")

    except sqlite3.Error as e:
        console.print(f"[red]Error getting stats:[/red] {e}")
        raise typer.Exit(1)


@app.command()
def ask(
    question: str = typer.Argument(..., help="Question to ask the vault"),
    model: str = typer.Option("gpt-4o", "-m", "--model", help="OpenAI model to use"),
    no_hybrid: bool = typer.Option(False, "--no-hybrid", help="Disable hybrid search"),
) -> None:
    """Ask a question using RAG (Retrieval Augmented Generation)."""
    try:
        from .rag import get_vault_rag
    except ImportError:
        from rag import get_vault_rag

    rag = get_vault_rag()

    console.print(f"[dim]Searching vault...[/dim]")
    result = rag.ask(question, model=model, use_hybrid=not no_hybrid)

    if 'error' in result:
        console.print(f"[red]Error:[/red] {result['error']}")
        raise typer.Exit(1)

    console.print(f"\n[cyan]Answer:[/cyan]\n{result['answer']}")
    console.print(f"\n[dim]Sources: {result['context_used']} items, Mode: {result.get('search_mode', 'unknown')}[/dim]")


ENTITY_TYPES = {
    "contacts", "contact", "tasks", "task", "goals", "goal",
    "ideas", "idea", "docs", "doc", "posts", "post",
    "health", "lists", "list", "tags", "tag",
}


@app.command("search")
def search_cmd(
    query: str = typer.Argument(..., help="Search query"),
    n: int = typer.Option(10, "-n", help="Number of results"),
    hybrid: bool = typer.Option(False, "--hybrid", help="Use hybrid search"),
    entity_type: Optional[str] = typer.Option(None, "--type", "-t", help="Filter by entity type (contacts, docs, research, ideas, health, catalog, facts, chunks)"),
):
    """Search the vault using semantic or hybrid search.

    Examples:
      cc-vault search "Omnigo"                    # Search all collections
      cc-vault search "Omnigo" --type contacts     # Only contact facts
      cc-vault search "Omnigo" --type docs          # Only documents
      cc-vault search "Omnigo" --type ideas         # Only ideas
      cc-vault search "SR&ED" --hybrid --type docs  # Hybrid, documents only
    """
    # Map user-friendly type names to vector collection names
    TYPE_ALIASES = {
        "contacts": "facts",
        "contact": "facts",
        "documents": "documents",
        "docs": "documents",
        "doc": "documents",
        "research": "documents",
        "ideas": "ideas",
        "idea": "ideas",
        "health": "health",
        "facts": "facts",
        "fact": "facts",
        "chunks": "chunks",
        "chunk": "chunks",
        "catalog": "catalog",
    }

    collections = None
    if entity_type:
        mapped = TYPE_ALIASES.get(entity_type.lower())
        if not mapped:
            valid = sorted(set(TYPE_ALIASES.keys()))
            console.print(f"[red]Error:[/red] Unknown type '{entity_type}'. Valid types: {', '.join(valid)}")
            raise typer.Exit(1)
        collections = [mapped]

    try:
        from .rag import get_vault_rag
    except ImportError:
        from rag import get_vault_rag

    rag = get_vault_rag()

    type_label = f" (type={entity_type})" if entity_type else ""

    if hybrid:
        results = rag.hybrid_search(query, n_results=n)

        # Filter hybrid results by doc_type if type filter is set
        if entity_type and collections:
            coll = collections[0]
            if coll == "documents":
                # Hybrid searches chunks; filter by doc_type metadata
                results = [r for r in results if r.get('metadata', {}).get('doc_type') in ('transcript', 'note', 'journal', 'research')]
            elif coll == "facts":
                # Facts are in a separate collection, not in chunks -- hybrid won't find them
                console.print("[yellow]Hybrid search only covers document chunks. Use semantic search for contacts/facts.[/yellow]")
                return

        if not results:
            console.print(f"[yellow]No results found{type_label}[/yellow]")
            return

        console.print(f"\n[cyan]Hybrid Search Results ({len(results)} chunks){type_label}:[/cyan]\n")
        for r in results:
            meta = r.get('metadata', {})
            doc_title = meta.get('doc_title', 'Unknown')
            lines = f"lines {meta.get('start_line', '?')}-{meta.get('end_line', '?')}"
            content = r.get('content', '')[:150]
            combined = r.get('combined_score', 0)
            console.print(f"  [{doc_title}, {lines}] score={combined:.3f}")
            console.print(f"    {content}...")
            console.print()
    else:
        results = rag.semantic_search(query, collections=collections, n_results=n)

        found = False
        for coll, items in results.items():
            if items:
                found = True
                console.print(f"\n[cyan]{coll.upper()} ({len(items)} results):[/cyan]")
                for item in items[:5]:
                    doc = item.get('document', '')[:100]
                    console.print(f"  [{item['id']}] {doc}...")

        if not found:
            console.print(f"[yellow]No results found{type_label}[/yellow]")


@app.command()
def backup(
    destination: Optional[Path] = typer.Argument(None, help="Directory where the backup zip will be saved"),
    list_backups: bool = typer.Option(False, "--list", help="List available backups"),
):
    """Create a full zip backup of the entire vault directory."""
    if list_backups:
        try:
            from .config import BACKUPS_PATH
        except ImportError:
            from config import BACKUPS_PATH

        if not BACKUPS_PATH.exists():
            console.print("[yellow]No backups directory found[/yellow]")
            return

        backups = sorted(BACKUPS_PATH.glob("vault_backup_*.zip"), reverse=True)
        # Also check the destination if different
        if not backups:
            # Check vault path for any zips
            backups = sorted(VAULT_PATH.parent.glob("vault_backup_*.zip"), reverse=True)

        if not backups:
            console.print("[yellow]No backups found[/yellow]")
            return

        table = Table(title="Available Backups")
        table.add_column("File", style="cyan")
        table.add_column("Size", justify="right")
        table.add_column("Date", style="dim")

        for bp in backups:
            size_mb = bp.stat().st_size / (1024 * 1024)
            mod_time = datetime.fromtimestamp(bp.stat().st_mtime).strftime('%Y-%m-%d %H:%M')
            table.add_row(str(bp), f"{size_mb:.1f} MB", mod_time)

        console.print(table)
        return

    if destination is None:
        console.print("[red]Error:[/red] Provide a destination directory, or use --list to see backups")
        raise typer.Exit(1)

    dest = Path(destination)
    if not dest.is_dir():
        console.print(f"[red]Error:[/red] Destination directory does not exist: {dest}")
        raise typer.Exit(1)

    timestamp = datetime.now().strftime('%Y%m%d_%H%M%S')
    zip_path = dest / f'vault_backup_{timestamp}.zip'

    file_count = 0
    try:
        with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED) as zf:
            for file in VAULT_PATH.rglob('*'):
                # Skip the backups subdirectory
                try:
                    file.relative_to(VAULT_PATH / 'backups')
                    continue
                except ValueError:
                    pass

                if file.is_file():
                    arcname = file.relative_to(VAULT_PATH)
                    zf.write(file, arcname)
                    file_count += 1

        size_mb = zip_path.stat().st_size / (1024 * 1024)
        console.print(f"[green]Backup created:[/green] {zip_path}")
        console.print(f"  Files: {file_count}")
        console.print(f"  Size:  {size_mb:.1f} MB")
    except OSError as e:
        console.print(f"[red]Error creating backup:[/red] {e}")
        raise typer.Exit(1)


@app.command("repair-vectors")
def repair_vectors():
    """Delete and rebuild the vector index from SQLite chunks."""
    console.print("[dim]Repairing vector index...[/dim]")

    try:
        try:
            from .vectors import VaultVectors
            from .db import get_db as get_db_conn, init_db
        except ImportError:
            from vectors import VaultVectors
            from db import get_db as get_db_conn, init_db

        init_db(silent=True)

        # Step 1: Clear all vec_embeddings
        conn = get_db_conn()
        try:
            conn.execute("DELETE FROM vec_embeddings")
            conn.commit()
            console.print("  Cleared vector embeddings table")
        finally:
            conn.close()

        # Step 2: Re-index all chunks
        vecs = VaultVectors()

        conn = get_db_conn()
        try:
            cursor = conn.execute("""
                SELECT c.id, c.document_id, c.content, c.content_hash,
                       c.start_line, c.end_line, c.chunk_index,
                       d.title as doc_title, d.path as doc_path, d.doc_type
                FROM chunks c
                JOIN documents d ON c.document_id = d.id
                WHERE c.content IS NOT NULL AND LENGTH(TRIM(c.content)) > 0
                ORDER BY c.document_id, c.chunk_index
            """)
            chunks = cursor.fetchall()
        finally:
            conn.close()

        if not chunks:
            console.print("[yellow]No chunks found in database to index[/yellow]")
            return

        # Index in batches
        batch_size = 50
        indexed = 0
        batch = []

        for chunk in chunks:
            chunk_meta = {
                'document_id': chunk['document_id'],
                'doc_title': chunk['doc_title'] or '',
                'doc_path': chunk['doc_path'] or '',
                'doc_type': chunk['doc_type'] or '',
                'start_line': chunk['start_line'] or 0,
                'end_line': chunk['end_line'] or 0,
                'chunk_index': chunk['chunk_index'] or 0
            }
            batch.append({
                'id': f"chunk_{chunk['id']}",
                'content': chunk['content'],
                'metadata': chunk_meta
            })

            if len(batch) >= batch_size:
                vecs.add_chunks_batch(batch)
                indexed += len(batch)
                console.print(f"  Indexed {indexed}/{len(chunks)} chunks...")
                batch = []

        # Final batch
        if batch:
            vecs.add_chunks_batch(batch)
            indexed += len(batch)

        # Update vector IDs in SQLite
        conn = get_db_conn()
        try:
            for chunk in chunks:
                conn.execute(
                    "UPDATE chunks SET vector_id = ? WHERE id = ?",
                    (f"chunk_{chunk['id']}", chunk['id'])
                )
            conn.commit()
        finally:
            conn.close()

        # Log migration hint if old vectors directory exists
        if VECTORS_PATH.exists():
            console.print(f"[dim]NOTE: Old vectors/ directory still exists at {VECTORS_PATH}[/dim]")
            console.print("[dim]It is no longer needed and can be deleted.[/dim]")

        console.print(f"[green]Repair complete:[/green] {indexed} chunks indexed")

    except RuntimeError as e:
        console.print(f"[red]Error during repair:[/red] {e}")
        raise typer.Exit(1)


@app.command()
def restore(
    zip_file: Path = typer.Argument(..., help="Path to vault backup zip file"),
):
    """Restore the vault from a backup zip file."""
    zip_path = Path(zip_file)

    if not zip_path.exists():
        console.print(f"[red]Error:[/red] File not found: {zip_path}")
        raise typer.Exit(1)

    if not zip_path.suffix == '.zip':
        console.print(f"[red]Error:[/red] Not a zip file: {zip_path}")
        raise typer.Exit(1)

    # Validate zip contains vault.db
    try:
        with zipfile.ZipFile(zip_path, 'r') as zf:
            names = zf.namelist()
            if 'vault.db' not in names:
                console.print("[red]Error:[/red] Backup does not contain vault.db - not a valid vault backup")
                raise typer.Exit(1)
    except zipfile.BadZipFile:
        console.print(f"[red]Error:[/red] Corrupt zip file: {zip_path}")
        raise typer.Exit(1)

    # Create safety backup of current vault first
    try:
        from .config import BACKUPS_PATH
    except ImportError:
        from config import BACKUPS_PATH

    BACKUPS_PATH.mkdir(parents=True, exist_ok=True)
    timestamp = datetime.now().strftime('%Y%m%d_%H%M%S')
    safety_path = BACKUPS_PATH / f'vault_backup_pre_restore_{timestamp}.zip'

    console.print("[dim]Creating safety backup of current vault...[/dim]")
    file_count = 0
    try:
        with zipfile.ZipFile(safety_path, 'w', zipfile.ZIP_DEFLATED) as zf:
            for file in VAULT_PATH.rglob('*'):
                try:
                    file.relative_to(VAULT_PATH / 'backups')
                    continue
                except ValueError:
                    pass
                if file.is_file():
                    arcname = file.relative_to(VAULT_PATH)
                    zf.write(file, arcname)
                    file_count += 1
        console.print(f"  Safety backup: {safety_path} ({file_count} files)")
    except OSError as e:
        console.print(f"[red]Error creating safety backup:[/red] {e}")
        raise typer.Exit(1)

    # Extract backup to vault path
    console.print(f"[dim]Restoring from: {zip_path}[/dim]")
    try:
        with zipfile.ZipFile(zip_path, 'r') as zf:
            zf.extractall(VAULT_PATH)
            restored_count = len(zf.namelist())
    except (zipfile.BadZipFile, OSError) as e:
        console.print(f"[red]Error extracting backup:[/red] {e}")
        console.print(f"[yellow]Safety backup available at:[/yellow] {safety_path}")
        raise typer.Exit(1)

    # Run schema migrations
    try:
        try:
            from .db import init_db
        except ImportError:
            from db import init_db
        init_db(silent=True)
    except Exception as e:
        console.print(f"[yellow]Warning:[/yellow] Schema migration issue: {e}")

    console.print(f"[green]Restore complete:[/green] {restored_count} files restored")
    console.print(f"  Safety backup: {safety_path}")
    console.print("[dim]Run 'cc-vault repair-vectors' if vector search doesn't work[/dim]")


# =============================================================================
# Tasks Commands
# =============================================================================

@tasks_app.command("list")
def tasks_list(
    status: str = typer.Option("pending", "-s", "--status", help="Filter by status: pending, done, all (completed is an alias for done)"),
    contact_id: Optional[int] = typer.Option(None, "-c", "--contact", help="Filter by contact"),
    sort: str = typer.Option("priority", "--sort", help="Sort: priority, newest, due"),
    limit: int = typer.Option(20, "-n", help="Max results"),
):
    """List tasks."""
    db = get_db()

    try:
        if status == "all":
            tasks = db.list_tasks(status=None, contact_id=contact_id, limit=limit, sort=sort)
        else:
            tasks = db.list_tasks(status=status, contact_id=contact_id, limit=limit, sort=sort)

        if not tasks:
            console.print(f"[yellow]No {status} tasks found[/yellow]")
            return

        table = Table(title=f"Tasks ({status})")
        table.add_column("ID", style="dim")
        table.add_column("Task", style="cyan")
        table.add_column("Due", style="yellow")
        table.add_column("Priority")
        table.add_column("Contact")

        for task in tasks:
            # Convert numeric priority to label
            priority_num = task.get('priority', 3)
            if isinstance(priority_num, int):
                if priority_num <= 2:
                    priority_label = 'high'
                elif priority_num >= 4:
                    priority_label = 'low'
                else:
                    priority_label = 'medium'
            else:
                priority_label = str(priority_num)

            priority_style = {'high': '[red]', 'low': '[dim]'}.get(priority_label, '')
            priority_end = '[/red]' if priority_label == 'high' else '[/dim]' if priority_label == 'low' else ''

            table.add_row(
                str(task['id']),
                task['title'][:50],
                task.get('due_date', '-')[:10] if task.get('due_date') else '-',
                f"{priority_style}{priority_label}{priority_end}",
                task.get('contact_name', '-') or '-',
            )

        console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error listing tasks:[/red] {e}")
        raise typer.Exit(1)


@tasks_app.command("add")
def tasks_add(
    title: str = typer.Argument(..., help="Task title"),
    due: Optional[str] = typer.Option(None, "-d", "--due", help="Due date (YYYY-MM-DD)"),
    priority: str = typer.Option("medium", "-p", "--priority", help="Priority: low, medium, high (or 1-5)"),
    contact_id: Optional[int] = typer.Option(None, "-c", "--contact", help="Link to contact"),
    goal_id: Optional[int] = typer.Option(None, "-g", "--goal", help="Link to goal"),
    description: Optional[str] = typer.Option(None, "-n", "--notes", help="Task description"),
):
    """Add a new task."""
    db = get_db()

    # Convert priority string to int (1-5)
    priority_map = {'high': 1, 'medium': 3, 'low': 5}
    if priority.lower() in priority_map:
        priority_int = priority_map[priority.lower()]
    else:
        try:
            priority_int = int(priority)
        except ValueError:
            console.print(f"[red]Error:[/red] Invalid priority. Use low, medium, high, or 1-5")
            raise typer.Exit(1)

    try:
        task_id = db.add_task(
            title=title,
            due_date=due,
            priority=priority_int,
            description=description,
            contact_id=contact_id,
            goal_id=goal_id,
        )
        console.print(f"[green]Task added:[/green] #{task_id} - {title}")

    except sqlite3.Error as e:
        console.print(f"[red]Error adding task:[/red] {e}")
        raise typer.Exit(1)


@tasks_app.command("done")
def tasks_done(
    task_id: int = typer.Argument(..., help="Task ID to complete"),
):
    """Mark a task as completed."""
    db = get_db()

    try:
        db.complete_task(task_id)
        console.print(f"[green]Task #{task_id} marked as completed[/green]")

    except sqlite3.Error as e:
        console.print(f"[red]Error completing task:[/red] {e}")
        raise typer.Exit(1)


@tasks_app.command("cancel")
def tasks_cancel(
    task_id: int = typer.Argument(..., help="Task ID to cancel"),
):
    """Cancel a task."""
    db = get_db()

    try:
        db.update_task(task_id, status='cancelled')
        console.print(f"[yellow]Task #{task_id} cancelled[/yellow]")

    except sqlite3.Error as e:
        console.print(f"[red]Error cancelling task:[/red] {e}")
        raise typer.Exit(1)


@tasks_app.command("show")
def tasks_show(
    task_id: int = typer.Argument(..., help="Task ID"),
):
    """Show full details of a task."""
    db = get_db()

    try:
        task = db.get_task(task_id)

        if not task:
            console.print(f"[red]Task #{task_id} not found[/red]")
            raise typer.Exit(1)

        # Convert numeric priority to label
        priority_num = task.get('priority', 3)
        if isinstance(priority_num, int):
            if priority_num <= 2:
                priority_label = 'high'
            elif priority_num >= 4:
                priority_label = 'low'
            else:
                priority_label = 'medium'
        else:
            priority_label = str(priority_num)

        # Build detail lines
        lines = []
        lines.append(f"[bold]Status:[/bold] {task.get('status', 'pending')}")
        lines.append(f"[bold]Priority:[/bold] {priority_label} ({priority_num})")
        if task.get('due_date'):
            lines.append(f"[bold]Due:[/bold] {task['due_date'][:10]}")
        if task.get('context'):
            lines.append(f"[bold]Context:[/bold] {task['context']}")
        if task.get('contact_name'):
            contact_info = task['contact_name']
            if task.get('contact_email'):
                contact_info += f" ({task['contact_email']})"
            lines.append(f"[bold]Contact:[/bold] {contact_info}")
        if task.get('goal_title'):
            lines.append(f"[bold]Goal:[/bold] {task['goal_title']}")
        lines.append(f"[bold]Created:[/bold] {task.get('created_at', '-')}")
        if task.get('completed_at'):
            lines.append(f"[bold]Completed:[/bold] {task['completed_at']}")
        if task.get('description'):
            lines.append("")
            lines.append("[bold]Description:[/bold]")
            lines.append(task['description'])

        console.print(Panel("\n".join(lines), title=f"Task #{task_id}: {task['title']}"))

    except sqlite3.Error as e:
        console.print(f"[red]Error showing task:[/red] {e}")
        raise typer.Exit(1)


@tasks_app.command("update")
def tasks_update(
    task_id: int = typer.Argument(..., help="Task ID"),
    title: Optional[str] = typer.Option(None, "--title", help="New title"),
    description: Optional[str] = typer.Option(None, "-d", "--description", help="New description"),
    priority: Optional[str] = typer.Option(None, "-p", "--priority", help="Priority: low, medium, high (or 1-5)"),
    due: Optional[str] = typer.Option(None, "--due", help="Due date (YYYY-MM-DD)"),
    context: Optional[str] = typer.Option(None, "--context", help="Context tag"),
    contact_id: Optional[int] = typer.Option(None, "-c", "--contact", help="Link to contact ID (0 to unlink)"),
    goal_id: Optional[int] = typer.Option(None, "-g", "--goal", help="Link to goal ID (0 to unlink)"),
):
    """Update a task's details."""
    db = get_db()

    # Convert priority string to int if provided
    priority_int = None
    if priority is not None:
        priority_map = {'high': 1, 'medium': 3, 'low': 5}
        if priority.lower() in priority_map:
            priority_int = priority_map[priority.lower()]
        else:
            try:
                priority_int = int(priority)
            except ValueError:
                console.print(f"[red]Error:[/red] Invalid priority. Use low, medium, high, or 1-5")
                raise typer.Exit(1)

    try:
        success = db.update_task(
            task_id,
            title=title,
            description=description,
            priority=priority_int,
            due_date=due,
            context=context,
            contact_id=contact_id,
            goal_id=goal_id,
        )
        if success:
            console.print(f"[green]Task #{task_id} updated[/green]")
        else:
            console.print(f"[red]Task #{task_id} not found[/red]")
            raise typer.Exit(1)

    except ValueError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error updating task:[/red] {e}")
        raise typer.Exit(1)


@tasks_app.command("search")
def tasks_search(
    query: str = typer.Argument(..., help="Search query"),
):
    """Search tasks by title, description, or context."""
    db = get_db()

    try:
        tasks = db.search_tasks(query)

        if not tasks:
            console.print(f"[yellow]No tasks found matching '{query}'[/yellow]")
            return

        console.print(f"[bold]Search results for '{query}':[/bold] {len(tasks)} found\n")

        table = Table()
        table.add_column("ID", style="dim")
        table.add_column("Task", style="cyan")
        table.add_column("Status")
        table.add_column("Due", style="yellow")
        table.add_column("Priority")
        table.add_column("Contact")

        for task in tasks:
            # Convert numeric priority to label
            priority_num = task.get('priority', 3)
            if isinstance(priority_num, int):
                if priority_num <= 2:
                    priority_label = 'high'
                elif priority_num >= 4:
                    priority_label = 'low'
                else:
                    priority_label = 'medium'
            else:
                priority_label = str(priority_num)

            priority_style = {'high': '[red]', 'low': '[dim]'}.get(priority_label, '')
            priority_end = '[/red]' if priority_label == 'high' else '[/dim]' if priority_label == 'low' else ''

            table.add_row(
                str(task['id']),
                task['title'][:50],
                task.get('status', 'pending'),
                task.get('due_date', '-')[:10] if task.get('due_date') else '-',
                f"{priority_style}{priority_label}{priority_end}",
                task.get('contact_name', '-') or '-',
            )

        console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error searching tasks:[/red] {e}")
        raise typer.Exit(1)


# =============================================================================
# Goals Commands
# =============================================================================

@goals_app.command("list")
def goals_list(
    status: str = typer.Option("active", "-s", "--status", help="Filter: active, achieved, paused, all"),
    category: Optional[str] = typer.Option(None, "-c", "--category", help="Filter by category"),
    timeframe: Optional[str] = typer.Option(None, "-t", "--timeframe", help="Filter by timeframe"),
):
    """List goals."""
    db = get_db()

    try:
        if status == "all":
            goals = db.list_goals(status=None, category=category, timeframe=timeframe, include_achieved=True)
        else:
            goals = db.list_goals(status=status, category=category, timeframe=timeframe)

        if not goals:
            console.print(f"[yellow]No {status} goals found[/yellow]")
            return

        table = Table(title=f"Goals ({status})")
        table.add_column("ID", style="dim")
        table.add_column("Goal", style="cyan")
        table.add_column("Target", style="yellow")
        table.add_column("Progress")
        table.add_column("Status")

        for goal in goals:
            progress = goal.get('progress', 0) or 0
            progress_bar = f"[{'=' * int(progress / 10)}{' ' * (10 - int(progress / 10))}] {progress}%"

            table.add_row(
                str(goal['id']),
                goal['title'][:40],
                goal.get('target_date', '-')[:10] if goal.get('target_date') else '-',
                progress_bar,
                goal.get('status', 'active'),
            )

        console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error listing goals:[/red] {e}")
        raise typer.Exit(1)


@goals_app.command("add")
def goals_add(
    title: str = typer.Argument(..., help="Goal title"),
    target: Optional[str] = typer.Option(None, "-t", "--target", help="Target date (YYYY-MM-DD)"),
    description: Optional[str] = typer.Option(None, "-d", "--description", help="Goal description"),
    category: Optional[str] = typer.Option(None, "-c", "--category", help="Goal category"),
):
    """Add a new goal."""
    db = get_db()

    try:
        goal_id = db.add_goal(
            title=title,
            target_date=target,
            description=description,
            category=category,
        )
        console.print(f"[green]Goal added:[/green] #{goal_id} - {title}")

    except sqlite3.Error as e:
        console.print(f"[red]Error adding goal:[/red] {e}")
        raise typer.Exit(1)


@goals_app.command("achieve")
def goals_achieve(
    goal_id: int = typer.Argument(..., help="Goal ID to mark as achieved"),
):
    """Mark a goal as achieved."""
    db = get_db()

    try:
        db.achieve_goal(goal_id)
        console.print(f"[green]Goal #{goal_id} marked as achieved![/green]")

    except sqlite3.Error as e:
        console.print(f"[red]Error updating goal:[/red] {e}")
        raise typer.Exit(1)


@goals_app.command("pause")
def goals_pause(
    goal_id: int = typer.Argument(..., help="Goal ID to pause"),
):
    """Pause a goal."""
    db = get_db()

    try:
        db.pause_goal(goal_id)
        console.print(f"[yellow]Goal #{goal_id} paused[/yellow]")

    except sqlite3.Error as e:
        console.print(f"[red]Error pausing goal:[/red] {e}")
        raise typer.Exit(1)


@goals_app.command("resume")
def goals_resume(
    goal_id: int = typer.Argument(..., help="Goal ID to resume"),
):
    """Resume a paused goal."""
    db = get_db()

    try:
        db.resume_goal(goal_id)
        console.print(f"[green]Goal #{goal_id} resumed[/green]")

    except sqlite3.Error as e:
        console.print(f"[red]Error resuming goal:[/red] {e}")
        raise typer.Exit(1)


@goals_app.command("update")
def goals_update(
    goal_id: int = typer.Argument(..., help="Goal ID"),
    title: Optional[str] = typer.Option(None, "--title", help="New title"),
    description: Optional[str] = typer.Option(None, "-d", "--description", help="New description"),
    category: Optional[str] = typer.Option(None, "-c", "--category", help="New category"),
    target: Optional[str] = typer.Option(None, "-t", "--target", help="New target date (YYYY-MM-DD)"),
):
    """Update a goal's details."""
    db = get_db()

    try:
        db.update_goal(
            goal_id,
            title=title,
            description=description,
            category=category,
            target_date=target,
        )
        console.print(f"[green]Goal #{goal_id} updated[/green]")

    except sqlite3.Error as e:
        console.print(f"[red]Error updating goal:[/red] {e}")
        raise typer.Exit(1)


# =============================================================================
# Ideas Commands
# =============================================================================

@ideas_app.command("list")
def ideas_list(
    status: str = typer.Option("new", "-s", "--status", help="Filter: new, actionable, archived, all"),
    domain: Optional[str] = typer.Option(None, "-d", "--domain", help="Filter by domain"),
    limit: int = typer.Option(20, "-n", help="Max results"),
):
    """List ideas."""
    db = get_db()

    try:
        if status == "all":
            ideas = db.list_ideas(status=None, domain=domain, limit=limit)
        else:
            ideas = db.list_ideas(status=status, domain=domain, limit=limit)

        if not ideas:
            console.print(f"[yellow]No {status} ideas found[/yellow]")
            return

        table = Table(title=f"Ideas ({status})")
        table.add_column("ID", style="dim")
        table.add_column("Idea", style="cyan")
        table.add_column("Domain")
        table.add_column("Created")
        table.add_column("Status")

        for idea in ideas:
            table.add_row(
                str(idea['id']),
                idea['content'][:50],
                idea.get('domain', '-') or '-',
                idea.get('created_at', '')[:10],
                idea.get('status', 'new'),
            )

        console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error listing ideas:[/red] {e}")
        raise typer.Exit(1)


@ideas_app.command("add")
def ideas_add(
    content: str = typer.Argument(..., help="Idea content"),
    tags: Optional[str] = typer.Option(None, "-t", "--tags", help="Tags (comma-separated)"),
    domain: Optional[str] = typer.Option(None, "-d", "--domain", help="Domain/category"),
    goal_id: Optional[int] = typer.Option(None, "-g", "--goal", help="Link to goal"),
):
    """Add a new idea."""
    db = get_db()

    try:
        idea_id = db.add_idea(
            content=content,
            tags=tags,
            domain=domain,
            goal_id=goal_id,
        )
        console.print(f"[green]Idea added:[/green] #{idea_id}")

    except sqlite3.Error as e:
        console.print(f"[red]Error adding idea:[/red] {e}")
        raise typer.Exit(1)


@ideas_app.command("actionable")
def ideas_actionable(
    idea_id: int = typer.Argument(..., help="Idea ID"),
):
    """Mark an idea as actionable."""
    db = get_db()

    try:
        db.update_idea_status(idea_id, 'actionable')
        console.print(f"[green]Idea #{idea_id} marked as actionable[/green]")

    except sqlite3.Error as e:
        console.print(f"[red]Error updating idea:[/red] {e}")
        raise typer.Exit(1)


@ideas_app.command("archive")
def ideas_archive(
    idea_id: int = typer.Argument(..., help="Idea ID"),
):
    """Archive an idea."""
    db = get_db()

    try:
        db.update_idea_status(idea_id, 'archived')
        console.print(f"[yellow]Idea #{idea_id} archived[/yellow]")

    except sqlite3.Error as e:
        console.print(f"[red]Error archiving idea:[/red] {e}")
        raise typer.Exit(1)


# =============================================================================
# Social Posts Commands
# =============================================================================

@posts_app.command("list")
def posts_list(
    platform: Optional[str] = typer.Option(None, "-p", "--platform", help="Filter: linkedin, twitter, reddit, other"),
    status: Optional[str] = typer.Option(None, "-s", "--status", help="Filter: draft, scheduled, posted"),
    limit: int = typer.Option(50, "-n", help="Max results"),
):
    """List social media posts."""
    db = get_db()

    try:
        posts = db.list_social_posts(platform=platform, status=status, limit=limit)

        if not posts:
            console.print("[yellow]No posts found[/yellow]")
            return

        table = Table(title="Social Posts")
        table.add_column("ID", style="dim")
        table.add_column("Platform", style="cyan")
        table.add_column("Status")
        table.add_column("Content")
        table.add_column("Audience")
        table.add_column("Created")

        platform_colors = {'linkedin': 'blue', 'twitter': 'cyan', 'reddit': 'red', 'other': 'white'}
        status_colors = {'draft': 'yellow', 'scheduled': 'magenta', 'posted': 'green'}

        for post in posts:
            platform_val = post.get('platform', 'other')
            status_val = post.get('status', 'draft')
            content = post.get('content', '')[:40]
            if len(post.get('content', '')) > 40:
                content += '...'

            table.add_row(
                str(post['id']),
                f"[{platform_colors.get(platform_val, 'white')}]{platform_val}[/]",
                f"[{status_colors.get(status_val, 'white')}]{status_val}[/]",
                content.replace('\n', ' '),
                post.get('audience', '-') or '-',
                post.get('created_at', '')[:10],
            )

        console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error listing posts:[/red] {e}")
        raise typer.Exit(1)


@posts_app.command("add")
def posts_add(
    content: str = typer.Argument(..., help="Post content"),
    platform: str = typer.Option("linkedin", "-p", "--platform", help="Platform: linkedin, twitter, reddit, other"),
    audience: Optional[str] = typer.Option(None, "-a", "--audience", help="Target audience"),
    tags: Optional[str] = typer.Option(None, "-t", "--tags", help="Tags (comma-separated)"),
    goal_id: Optional[int] = typer.Option(None, "-g", "--goal", help="Link to goal ID"),
    status: str = typer.Option("draft", "-s", "--status", help="Status: draft, scheduled, posted"),
):
    """Add a new social media post."""
    db = get_db()

    try:
        post_id = db.add_social_post(
            platform=platform,
            content=content,
            status=status,
            audience=audience,
            tags=tags,
            goal_id=goal_id,
        )
        console.print(f"[green]Post added:[/green] #{post_id} ({platform}, {status})")

    except ValueError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error adding post:[/red] {e}")
        raise typer.Exit(1)


@posts_app.command("show")
def posts_show(
    post_id: int = typer.Argument(..., help="Post ID"),
):
    """Show details of a social post."""
    db = get_db()

    try:
        post = db.get_social_post(post_id)
        if not post:
            console.print(f"[red]Post #{post_id} not found[/red]")
            raise typer.Exit(1)

        platform_names = {'linkedin': 'LinkedIn', 'twitter': 'Twitter/X', 'reddit': 'Reddit', 'other': 'Other'}
        title = f"{platform_names.get(post['platform'], 'Unknown')} Post #{post['id']}"

        lines = []
        lines.append(f"[bold]Status:[/bold] {post['status']}")
        if post.get('audience'):
            lines.append(f"[bold]Audience:[/bold] {post['audience']}")
        if post.get('tags'):
            lines.append(f"[bold]Tags:[/bold] {post['tags']}")
        if post.get('goal_title'):
            lines.append(f"[bold]Goal:[/bold] {post['goal_title']}")
        if post.get('url'):
            lines.append(f"[bold]URL:[/bold] {post['url']}")
        if post.get('posted_at'):
            lines.append(f"[bold]Posted:[/bold] {post['posted_at']}")
        lines.append(f"[bold]Created:[/bold] {post['created_at']}")
        lines.append("")
        lines.append("[bold]Content:[/bold]")
        lines.append(post['content'])

        console.print(Panel("\n".join(lines), title=title))

    except sqlite3.Error as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@posts_app.command("posted")
def posts_posted(
    post_id: int = typer.Argument(..., help="Post ID"),
    url: Optional[str] = typer.Option(None, "-u", "--url", help="URL of live post"),
):
    """Mark a post as posted."""
    db = get_db()

    try:
        success = db.mark_social_post_posted(post_id, url)
        if success:
            console.print(f"[green]Post #{post_id} marked as posted[/green]")
            if url:
                console.print(f"URL: {url}")
        else:
            console.print(f"[red]Post #{post_id} not found[/red]")
            raise typer.Exit(1)

    except sqlite3.Error as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@posts_app.command("search")
def posts_search(
    query: str = typer.Argument(..., help="Search query"),
):
    """Search social posts by content, tags, or audience."""
    db = get_db()

    try:
        posts = db.search_social_posts(query)

        if not posts:
            console.print(f"[yellow]No posts found matching '{query}'[/yellow]")
            return

        console.print(f"[bold]Search results for '{query}':[/bold] {len(posts)} found\n")

        table = Table()
        table.add_column("ID", style="dim")
        table.add_column("Platform", style="cyan")
        table.add_column("Status")
        table.add_column("Content")
        table.add_column("Created")

        for post in posts:
            content = post.get('content', '')[:50]
            if len(post.get('content', '')) > 50:
                content += '...'

            table.add_row(
                str(post['id']),
                post.get('platform', 'other'),
                post.get('status', 'draft'),
                content.replace('\n', ' '),
                post.get('created_at', '')[:10],
            )

        console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error searching posts:[/red] {e}")
        raise typer.Exit(1)


@posts_app.command("update")
def posts_update(
    post_id: int = typer.Argument(..., help="Post ID"),
    content: Optional[str] = typer.Option(None, "--content", help="New content"),
    audience: Optional[str] = typer.Option(None, "-a", "--audience", help="Target audience"),
    tags: Optional[str] = typer.Option(None, "-t", "--tags", help="Tags (comma-separated)"),
    status: Optional[str] = typer.Option(None, "-s", "--status", help="Status: draft, scheduled, posted"),
):
    """Update a social post."""
    db = get_db()

    try:
        success = db.update_social_post(
            post_id=post_id,
            content=content,
            status=status,
            audience=audience,
            tags=tags,
        )
        if success:
            console.print(f"[green]Post #{post_id} updated[/green]")
        else:
            console.print(f"[red]Post #{post_id} not found[/red]")
            raise typer.Exit(1)

    except ValueError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error updating post:[/red] {e}")
        raise typer.Exit(1)


# =============================================================================
# Contacts Commands
# =============================================================================

@contacts_app.command("list")
def contacts_list(
    account: Optional[str] = typer.Option(None, "-a", "--account", help="Filter by account: consulting, personal, both"),
    category: Optional[str] = typer.Option(None, "-c", "--category", help="Filter by category"),
    relationship: Optional[str] = typer.Option(None, "-r", "--relationship", help="Filter by relationship"),
    tag: Optional[List[str]] = typer.Option(None, "--tag", help="Filter by tag (repeatable, AND logic)"),
    has: Optional[List[str]] = typer.Option(None, "--has", help="Only show contacts where field is non-empty (repeatable)"),
    missing: Optional[List[str]] = typer.Option(None, "--missing", help="Only show contacts where field is empty/null (repeatable)"),
    format: str = typer.Option("text", "--format", "-f", help="Output format: text, json"),
):
    """List contacts."""
    db = get_db()

    try:
        contacts = db.list_contacts(
            account=account,
            category=category,
            relationship=relationship,
            has_fields=has,
            missing_fields=missing,
            tags=tag,
        )

        if not contacts:
            console.print("[yellow]No contacts found[/yellow]")
            return

        if format == "json":
            print(json.dumps(contacts, indent=2, default=str))
            return

        table = Table(title="Contacts")
        table.add_column("ID", style="dim")
        table.add_column("Name", style="cyan")
        table.add_column("Email")
        table.add_column("Company")
        table.add_column("Last Contact")

        for c in contacts:
            table.add_row(
                str(c['id']),
                c['name'],
                c.get('email', '-') or '-',
                c.get('company', '-') or '-',
                c.get('last_contact', '-')[:10] if c.get('last_contact') else '-',
            )

        console.print(table)

    except ValueError as e:
        console.print(f"[red]Invalid filter:[/red] {e}")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error listing contacts:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("add")
def contacts_add(
    name: str = typer.Argument(..., help="Contact name"),
    email: str = typer.Option(..., "-e", "--email", help="Email address (required)"),
    account: str = typer.Option("personal", "-a", "--account", help="Account: consulting, personal, both"),
    phone: Optional[str] = typer.Option(None, "-p", "--phone", help="Phone number"),
    company: Optional[str] = typer.Option(None, "-c", "--company", help="Company"),
    role: Optional[str] = typer.Option(None, "-r", "--role", help="Role/title"),
):
    """Add a new contact."""
    db = get_db()

    try:
        contact_id = db.add_contact(
            email=email,
            name=name,
            account=account,
            phone=phone,
            company=company,
            role=role,
        )
        console.print(f"[green]Contact added:[/green] #{contact_id} - {name}")

    except sqlite3.Error as e:
        console.print(f"[red]Error adding contact:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("show")
def contacts_show(
    contact_id: int = typer.Argument(..., help="Contact ID"),
    format: str = typer.Option("table", "--format", "-f", help="Output format: table or json"),
):
    """Show contact details."""
    db = get_db()

    try:
        contact = db.get_contact_by_id(contact_id)

        if not contact:
            console.print(f"[red]Contact #{contact_id} not found[/red]")
            raise typer.Exit(1)

        if format == "json":
            import json as json_mod
            result = dict(contact)
            result['tags'] = db.get_tags(contact_id)
            result['memories'] = db.get_memories(contact_id)
            if contact.get('email'):
                result['recent_interactions'] = db.get_interactions(contact['email'], limit=5)
            result['email_activity'] = db.get_email_activity(contact_id=contact_id)
            result['emails'] = db.get_contact_emails(contact_id)
            print(json_mod.dumps(result, indent=2, default=str, ensure_ascii=False))
            return

        # Header
        display_name = contact.get('name') or "(no name)"
        console.print(f"\n[bold cyan]{display_name}[/bold cyan]")
        console.print(f"[dim]Contact #{contact_id}[/dim]\n")

        # Basic info
        table = Table(show_header=False, box=None)
        table.add_column("Property", style="cyan", width=20)
        table.add_column("Value")

        basic_fields = [
            ('email', 'Email'),
            ('phone', 'Phone'),
            ('company', 'Company'),
            ('title', 'Title'),
            ('location', 'Location'),
            ('account', 'Account'),
            ('category', 'Category'),
            ('relationship', 'Relationship'),
            ('priority', 'Priority'),
            ('lead_source', 'Lead Source'),
            ('lead_status', 'Lead Status'),
        ]
        for field, label in basic_fields:
            val = contact.get(field)
            if val:
                table.add_row(label, str(val))

        # Social links
        social_fields = [
            ('linkedin', 'LinkedIn'),
            ('twitter', 'Twitter'),
            ('github', 'GitHub'),
            ('website', 'Website'),
            ('whatsapp', 'WhatsApp'),
            ('instagram', 'Instagram'),
            ('facebook', 'Facebook'),
            ('telegram', 'Telegram'),
            ('signal', 'Signal'),
            ('skype', 'Skype'),
        ]
        for field, label in social_fields:
            val = contact.get(field)
            if val:
                table.add_row(label, str(val))

        # Personal details
        personal_fields = [
            ('birthday', 'Birthday'),
            ('spouse_name', 'Spouse'),
            ('children', 'Children'),
            ('pets', 'Pets'),
            ('hobbies', 'Hobbies'),
            ('address', 'Address'),
            ('timezone', 'Timezone'),
        ]
        for field, label in personal_fields:
            val = contact.get(field)
            if val:
                table.add_row(label, str(val))

        # Contact preferences
        pref_fields = [
            ('best_contact_method', 'Best Contact'),
            ('best_time', 'Best Time'),
            ('response_speed', 'Response Speed'),
            ('contact_frequency', 'Frequency'),
            ('style', 'Style'),
            ('greeting', 'Greeting'),
            ('signoff', 'Sign-off'),
        ]
        for field, label in pref_fields:
            val = contact.get(field)
            if val and val != 'casual' and val != 'normal':
                table.add_row(label, str(val))

        # Dates
        if contact.get('first_contact'):
            table.add_row("First Contact", contact['first_contact'][:10])
        if contact.get('last_contact'):
            # Enrich last contact with interaction details
            last_comm = db.get_last_communication(contact['id'])
            last_touch = last_comm.get('last_touch')
            if last_touch:
                touch_type = last_touch.get('type', '')
                direction = last_touch.get('direction', '')
                subject = last_touch.get('subject', '')
                detail = contact['last_contact'][:10]
                if touch_type:
                    label = touch_type
                    if direction:
                        label += f" ({direction})"
                    detail += f" - {label}"
                if subject:
                    detail += f": {subject}"
                table.add_row("Last Contact", detail)
            else:
                table.add_row("Last Contact", contact['last_contact'][:10])

        # Context/notes
        if contact.get('context'):
            table.add_row("Notes", contact['context'])

        console.print(table)

        # Tags
        tags = db.get_tags(contact_id)
        if tags:
            console.print(f"\n[cyan]Tags:[/cyan] {', '.join(tags)}")

        # Memories
        memories = db.get_memories(contact_id)
        if memories:
            console.print("\n[cyan]Memories:[/cyan]")
            for m in memories:
                cat = f"[dim][{m.get('category', '')}][/dim] " if m.get('category') else ""
                console.print(f"  {cat}{m.get('fact', '')}")
                if m.get('detail'):
                    console.print(f"    [dim]{m['detail']}[/dim]")

        # Recent interactions (if contact has email)
        if contact.get('email'):
            interactions = db.get_interactions(contact['email'], limit=5)
            if interactions:
                console.print("\n[cyan]Recent Interactions:[/cyan]")
                for i in interactions:
                    idate = (i.get('interaction_date') or '')[:10]
                    itype = i.get('type') or ''
                    idir = i.get('direction') or ''
                    isubject = i.get('subject') or i.get('summary') or ''
                    label = itype
                    if idir:
                        label += f" ({idir})"
                    console.print(f"  [{idate}] {label} - {isubject}")

    except sqlite3.Error as e:
        console.print(f"[red]Error showing contact:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("memory")
def contacts_memory(
    contact_id: int = typer.Argument(..., help="Contact ID"),
    fact: str = typer.Argument(..., help="Memory/fact to remember"),
    category: str = typer.Option("general", "-c", "--category", help="Memory category"),
    detail: Optional[str] = typer.Option(None, "-d", "--detail", help="Additional detail"),
):
    """Add a memory/fact about a contact."""
    db = get_db()

    try:
        contact = db.get_contact_by_id(contact_id)
        if not contact:
            console.print(f"[red]Contact #{contact_id} not found[/red]")
            raise typer.Exit(1)

        db.add_memory(
            contact_id=contact_id,
            category=category,
            fact=fact,
            detail=detail,
        )
        display_name = contact.get('name') or f"#{contact_id}"
        console.print(f"[green]Memory added for {display_name}[/green]")

    except sqlite3.Error as e:
        console.print(f"[red]Error adding memory:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("update")
def contacts_update(
    contact_id: int = typer.Argument(..., help="Contact ID"),
    name: Optional[str] = typer.Option(None, "--name", help="Name"),
    email: Optional[str] = typer.Option(None, "-e", "--email", help="Email address"),
    phone: Optional[str] = typer.Option(None, "-p", "--phone", help="Phone number"),
    company: Optional[str] = typer.Option(None, "-c", "--company", help="Company"),
    title: Optional[str] = typer.Option(None, "-t", "--title", help="Title/role"),
    location: Optional[str] = typer.Option(None, "-l", "--location", help="Location"),
    linkedin: Optional[str] = typer.Option(None, "--linkedin", help="LinkedIn URL"),
    account: Optional[str] = typer.Option(None, "-a", "--account", help="Account: consulting, personal, both"),
    category: Optional[str] = typer.Option(None, "--category", help="Category"),
    relationship: Optional[str] = typer.Option(None, "-r", "--relationship", help="Relationship"),
    notes: Optional[str] = typer.Option(None, "--notes", help="Notes"),
    lead_source: Optional[str] = typer.Option(None, "--lead-source", help="Lead source"),
    lead_status: Optional[str] = typer.Option(None, "--lead-status", help="Lead status"),
):
    """Update a contact."""
    db = get_db()

    try:
        contact = db.get_contact_by_id(contact_id)
        if not contact:
            console.print(f"[red]Contact #{contact_id} not found[/red]")
            raise typer.Exit(1)

        db.update_contact(
            contact_id,
            name=name,
            email=email,
            phone=phone,
            company=company,
            title=title,
            location=location,
            linkedin=linkedin,
            account=account,
            category=category,
            relationship=relationship,
            context=notes,
            lead_source=lead_source,
            lead_status=lead_status,
        )
        display_name = name or contact.get('name') or f"#{contact_id}"
        console.print(f"[green]Contact {display_name} updated[/green]")

    except sqlite3.Error as e:
        console.print(f"[red]Error updating contact:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("delete")
def contacts_delete(
    contact_id: int = typer.Argument(..., help="Contact ID"),
    force: bool = typer.Option(False, "--force", "-f", help="Skip confirmation"),
):
    """Delete a contact."""
    db = get_db()

    try:
        contact = db.get_contact_by_id(contact_id)
        if not contact:
            console.print(f"[red]Contact #{contact_id} not found[/red]")
            raise typer.Exit(1)

        display_name = contact.get('name') or contact.get('email') or f"#{contact_id}"
        if not force:
            confirm = typer.confirm(f"Delete contact {display_name} (#{contact_id})?")
            if not confirm:
                console.print("Cancelled.")
                return

        deleted = db.delete_contact(contact_id)
        if deleted:
            console.print(f"[green]Contact {display_name} (#{contact_id}) deleted[/green]")
        else:
            console.print(f"[red]Failed to delete contact #{contact_id}[/red]")
            raise typer.Exit(1)

    except sqlite3.Error as e:
        console.print(f"[red]Error deleting contact:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("enrich")
def contacts_enrich(
    contact_id: int = typer.Argument(..., help="Contact ID to enrich"),
    data: str = typer.Argument(..., help="JSON string with profile data from linkedin-enrich"),
):
    """Enrich a contact with data from LinkedIn profile extraction."""
    db = get_db()

    try:
        contact = db.get_contact_by_id(contact_id)
        if not contact:
            console.print(f"[red]Contact #{contact_id} not found[/red]")
            raise typer.Exit(1)

        profile = json.loads(data)

        if not profile.get("profile_exists", False):
            db.add_memory(
                contact_id=contact_id,
                category="linkedin",
                fact="LinkedIn profile not found or unavailable",
                source="linkedin-enrich",
            )
            console.print(f"[yellow]Contact #{contact_id}: profile not found, memory added[/yellow]")
            return

        # Update vault contact fields from profile data
        update_fields = {}
        if profile.get("name") and not contact.get("name"):
            update_fields["name"] = profile["name"]
        elif profile.get("name") and contact.get("name") == "":
            update_fields["name"] = profile["name"]

        if profile.get("current_company") and not contact.get("company"):
            update_fields["company"] = profile["current_company"]

        if profile.get("current_title") and not contact.get("title"):
            update_fields["title"] = profile["current_title"]

        if profile.get("location") and not contact.get("location"):
            update_fields["location"] = profile["location"]

        if update_fields:
            db.update_contact(contact_id, **update_fields)

        # Store headline as context/notes if empty
        if profile.get("headline") and not contact.get("context"):
            db.update_contact(contact_id, context=profile["headline"])

        # Store extra info as memories
        if profile.get("about"):
            db.add_memory(
                contact_id=contact_id,
                category="about",
                fact=profile["about"],
                source="linkedin-enrich",
            )

        if profile.get("education"):
            db.add_memory(
                contact_id=contact_id,
                category="education",
                fact=profile["education"],
                source="linkedin-enrich",
            )

        if profile.get("pronouns"):
            db.add_memory(
                contact_id=contact_id,
                category="personal",
                fact=f"Pronouns: {profile['pronouns']}",
                source="linkedin-enrich",
            )

        if profile.get("connections"):
            db.add_memory(
                contact_id=contact_id,
                category="linkedin",
                fact=f"Connections: {profile['connections']}",
                source="linkedin-enrich",
            )

        display_name = update_fields.get("name") or contact.get("name") or f"#{contact_id}"
        console.print(f"[green]Enriched: {display_name}[/green]")

    except json.JSONDecodeError as e:
        console.print(f"[red]Invalid JSON data:[/red] {e}")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error enriching contact:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("search")
def contacts_search(
    name: Optional[str] = typer.Argument(None, help="Name to search for (fuzzy match)"),
    company: Optional[str] = typer.Option(None, "--company", help="Search by company name"),
    domain: Optional[str] = typer.Option(None, "--domain", help="Search by email domain (e.g. bakertilly.ca)"),
    tag: Optional[str] = typer.Option(None, "--tag", help="Search by tag"),
    notes: Optional[str] = typer.Option(None, "--notes", help="Full-text search in notes/context"),
    title: Optional[str] = typer.Option(None, "--title", help="Search by job title"),
    location: Optional[str] = typer.Option(None, "--location", help="Search by location"),
    threshold: int = typer.Option(50, "--threshold", "-t", help="Minimum fuzzy match score (0-100)"),
    n: int = typer.Option(10, "-n", help="Max results"),
    exact: bool = typer.Option(False, "--exact", help="Use exact matching (LIKE) instead of fuzzy for name"),
):
    """Search contacts by name, company, domain, tag, notes, title, or location.

    Examples:
      cc-vault contacts search "Baker"                  # Fuzzy name search
      cc-vault contacts search --company "Baker Tilly"   # By company
      cc-vault contacts search --domain "bakertilly.ca"  # By email domain
      cc-vault contacts search --tag consultant          # By tag
      cc-vault contacts search --notes "SR&ED"           # In notes/context
      cc-vault contacts search --company Acme --location Toronto  # Combined
    """
    db = get_db()
    has_filters = any([company, domain, tag, notes, title, location])

    if not name and not has_filters:
        console.print("[red]Error:[/red] Provide a name argument or at least one filter flag.")
        console.print("[dim]Example: cc-vault contacts search --company \"Baker Tilly\"[/dim]")
        raise typer.Exit(1)

    try:
        # Field-level filter search (with or without name)
        if has_filters:
            results = db.filter_contacts(
                company=company, domain=domain, tag=tag,
                notes=notes, title=title, location=location, limit=n,
            )

            # If name is also provided, further filter by fuzzy name match
            if name and results:
                from rapidfuzz import fuzz
                scored = []
                for c in results:
                    score = fuzz.token_set_ratio(name.lower(), (c.get('name') or '').lower())
                    if score >= threshold:
                        c['match_score'] = score
                        scored.append(c)
                results = sorted(scored, key=lambda x: x['match_score'], reverse=True)

            # Build title string from filters used
            filter_parts = []
            if company:
                filter_parts.append(f"company={company}")
            if domain:
                filter_parts.append(f"domain={domain}")
            if tag:
                filter_parts.append(f"tag={tag}")
            if notes:
                filter_parts.append(f"notes={notes}")
            if title:
                filter_parts.append(f"title={title}")
            if location:
                filter_parts.append(f"location={location}")
            if name:
                filter_parts.append(f"name~{name}")
            title_str = ", ".join(filter_parts)

            if not results:
                console.print(f"[yellow]No contacts matching:[/yellow] {title_str}")
                return

            table = Table(title=f"Search Results ({title_str})")
            table.add_column("ID", style="dim")
            table.add_column("Name", style="cyan")
            table.add_column("Email")
            table.add_column("Company")
            table.add_column("Title")

            for c in results[:n]:
                table.add_row(
                    str(c['id']),
                    c['name'],
                    c.get('email', '-') or '-',
                    c.get('company', '-') or '-',
                    c.get('title', '-') or '-',
                )

            console.print(table)
            return

        # Name-only search (original behavior)
        if exact:
            results = db.search_contacts(name)
            if not results:
                console.print(f"[yellow]No contacts matching:[/yellow] {name}")
                return

            table = Table(title=f"Search Results for '{name}' (exact)")
            table.add_column("ID", style="dim")
            table.add_column("Name", style="cyan")
            table.add_column("Email")
            table.add_column("Company")
            table.add_column("Match", style="green")

            for c in results[:n]:
                table.add_row(
                    str(c['id']),
                    c['name'],
                    c.get('email', '-') or '-',
                    c.get('company', '-') or '-',
                    "exact",
                )

            console.print(table)
        else:
            results = db.fuzzy_search_contacts(name, threshold=threshold, limit=n)

            if not results:
                console.print(f"[yellow]No contacts matching:[/yellow] {name} (threshold={threshold})")
                console.print("[dim]Try lowering the threshold with --threshold 30[/dim]")
                return

            table = Table(title=f"Search Results for '{name}'")
            table.add_column("ID", style="dim")
            table.add_column("Name", style="cyan")
            table.add_column("Email")
            table.add_column("Company")
            table.add_column("Score", justify="right")
            table.add_column("Match", style="green")

            for c in results:
                score = c.get('match_score', 0)
                match_type = c.get('match_type', 'fuzzy')

                if score >= 80:
                    score_style = "[green]"
                    score_end = "[/green]"
                elif score >= 60:
                    score_style = "[yellow]"
                    score_end = "[/yellow]"
                else:
                    score_style = "[dim]"
                    score_end = "[/dim]"

                table.add_row(
                    str(c['id']),
                    c['name'],
                    c.get('email', '-') or '-',
                    c.get('company', '-') or '-',
                    f"{score_style}{score:.0f}{score_end}",
                    match_type,
                )

            console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error searching contacts:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("email-activity")
def contacts_email_activity(
    contact_id: Optional[int] = typer.Argument(None, help="Contact ID (omit to list all)"),
    update: bool = typer.Option(False, "--update", help="Update mode: store email activity"),
    account: Optional[str] = typer.Option(None, "-a", "--account", help="Account name (e.g. personal, consulting, outlook)"),
    sent: Optional[int] = typer.Option(None, "--sent", help="Number of sent emails"),
    received: Optional[int] = typer.Option(None, "--received", help="Number of received emails"),
    first_date: Optional[str] = typer.Option(None, "--first-date", help="First email date (ISO format)"),
    last_date: Optional[str] = typer.Option(None, "--last-date", help="Last email date (ISO format)"),
    format: str = typer.Option("table", "--format", "-f", help="Output format: table or json"),
):
    """Show or update email activity for a contact.

    View:   cc-vault contacts email-activity <contact_id>
    Update: cc-vault contacts email-activity <contact_id> --update --account personal --sent 47
    """
    db = get_db()

    if update:
        # Update mode: store email activity
        if contact_id is None:
            console.print("[red]Contact ID required for --update[/red]")
            raise typer.Exit(1)
        if not account:
            console.print("[red]--account required for --update[/red]")
            raise typer.Exit(1)

        contact = db.get_contact_by_id(contact_id)
        if not contact:
            console.print(f"[red]Contact #{contact_id} not found[/red]")
            raise typer.Exit(1)

        db.upsert_email_activity(
            contact_id=contact_id,
            account=account,
            sent_count=sent or 0,
            received_count=received or 0,
            first_email_date=first_date,
            last_email_date=last_date,
        )
        display_name = contact.get('name') or f"#{contact_id}"
        console.print(f"[green]Email activity updated for {display_name} ({account})[/green]")
        return

    # View mode: show email activity
    try:
        results = db.get_email_activity(contact_id=contact_id, account=account)

        if not results:
            if contact_id:
                console.print(f"[yellow]No email activity for contact #{contact_id}[/yellow]")
            else:
                console.print("[yellow]No email activity recorded[/yellow]")
            return

        if format == "json":
            import json as json_mod
            console.print(json_mod.dumps(results, indent=2, ensure_ascii=False))
        else:
            title = f"Email Activity for Contact #{contact_id}" if contact_id else "All Email Activity"
            table = Table(title=title)
            table.add_column("Contact ID", style="dim")
            table.add_column("Name", style="cyan")
            table.add_column("Email")
            table.add_column("Account")
            table.add_column("Sent", justify="right", style="green")
            table.add_column("Received", justify="right", style="blue")
            table.add_column("Total", justify="right", style="bold")
            table.add_column("Last Scanned", style="dim")

            for r in results:
                table.add_row(
                    str(r['contact_id']),
                    r.get('name', '-') or '-',
                    r.get('email', '-') or '-',
                    r.get('account', '-'),
                    str(r.get('sent_count', 0)),
                    str(r.get('received_count', 0)),
                    str(r.get('email_count', 0)),
                    (r.get('scanned_at', '') or '')[:19],
                )

            console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error reading email activity:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("last-comm")
def contacts_last_comm(
    identifier: str = typer.Argument(..., help="Contact name, email, or ID"),
    format: str = typer.Option("table", "--format", "-f", help="Output format: table or json"),
):
    """Show last communication summary for a contact.

    Usage:
      cc-vault contacts last-comm "John Smith"
      cc-vault contacts last-comm 42 --format json
    """
    db = get_db()

    try:
        # Resolve contact: try ID first, then name/email
        contact = None
        if identifier.isdigit():
            contact = db.get_contact_by_id(int(identifier))
        if not contact:
            contact = db.get_contact(identifier)
        if not contact:
            console.print(f"[red]Contact not found: {identifier}[/red]")
            raise typer.Exit(1)

        result = db.get_last_communication(contact['id'])
        result['contact_name'] = contact.get('name', '(unknown)')
        result['contact_email'] = contact.get('email', '')

        if format == "json":
            import json as json_mod
            console.print(json_mod.dumps(result, indent=2, ensure_ascii=False, default=str))
            return

        display_name = contact.get('name') or f"#{contact['id']}"
        console.print(f"\n[bold cyan]Communication History: {display_name}[/bold cyan]\n")

        def _format_touch(touch):
            if not touch:
                return "[dim]None[/dim]"
            date_str = (touch.get('interaction_date', '') or '')[:10]
            touch_type = touch.get('type', '')
            direction = touch.get('direction', '')
            subject = touch.get('subject', '') or ''
            account = touch.get('account', '') or ''
            parts = [date_str]
            if touch_type:
                label = touch_type
                if direction:
                    label += f" ({direction})"
                parts.append(label)
            if subject:
                parts.append(subject)
            if account:
                parts.append(f"[dim]via {account}[/dim]")
            return " - ".join(parts)

        table = Table(show_header=False, box=None)
        table.add_column("Property", style="cyan", width=20)
        table.add_column("Value")

        table.add_row("Last Touch", _format_touch(result.get('last_touch')))
        table.add_row("Last Inbound", _format_touch(result.get('last_inbound')))
        table.add_row("Last Outbound", _format_touch(result.get('last_outbound')))

        days = result.get('days_since_last')
        if days is not None:
            table.add_row("Days Since Last", str(days))
        else:
            table.add_row("Days Since Last", "[dim]No interactions[/dim]")

        console.print(table)

        # Email activity
        ea = result.get('email_activity', [])
        if ea:
            console.print("\n[cyan]Email Activity:[/cyan]")
            ea_table = Table()
            ea_table.add_column("Account")
            ea_table.add_column("Sent", justify="right", style="green")
            ea_table.add_column("Received", justify="right", style="blue")
            ea_table.add_column("Last Email", style="dim")
            for r in ea:
                ea_table.add_row(
                    r.get('account', '-'),
                    str(r.get('sent_count', 0)),
                    str(r.get('received_count', 0)),
                    (r.get('last_email_date', '') or '')[:10],
                )
            console.print(ea_table)

    except sqlite3.Error as e:
        console.print(f"[red]Error getting communication history:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("merge")
def contacts_merge(
    target_id: int = typer.Argument(..., help="Target contact ID (the survivor)"),
    source_ids: List[int] = typer.Argument(..., help="Source contact IDs to merge into target"),
    email_labels: Optional[str] = typer.Option(None, "--email-labels", help='JSON map of email->label, e.g. \'{"foo@bar.com": "work"}\''),
    yes: bool = typer.Option(False, "--yes", "-y", help="Skip confirmation and execute"),
    format: str = typer.Option("table", "--format", "-f", help="Output format: table or json"),
):
    """Merge duplicate contacts into one.

    Moves all interactions, memories, notes, tags, and emails from source contacts
    into the target contact, then deletes the sources.

    Usage:
      cc-vault contacts merge 156 4993 5230 5537
      cc-vault contacts merge 156 4993 --email-labels '{"sergei@accessair.ca": "work"}'
      cc-vault contacts merge 156 4993 5230 --yes --format json
    """
    db = get_db()

    labels = {}
    if email_labels:
        try:
            labels = json.loads(email_labels)
        except json.JSONDecodeError:
            console.print("[red]ERROR:[/red] --email-labels must be valid JSON")
            raise typer.Exit(1)

    try:
        # Preview
        preview = db.get_merge_preview(target_id, source_ids)

        if format == "json" and not yes:
            import json as json_mod
            console.print(json_mod.dumps(preview, indent=2, default=str))
            return

        target = preview['target']
        console.print(f"\n[bold cyan]Merge Preview[/bold cyan]\n")
        console.print(f"[green]Target (survivor):[/green] #{target_id} - {target.get('name', '?')} ({target.get('email', '?')})")
        console.print()

        for s in preview['sources']:
            console.print(f"[yellow]Source (will be deleted):[/yellow] #{s['id']} - {s.get('name', '?')} ({s.get('email', '?')}) - {s.get('interaction_count', 0)} interactions")
        console.print()

        table = Table(title="Records to Reassign", show_header=True)
        table.add_column("Type", style="cyan")
        table.add_column("Count", justify="right")

        table.add_row("Interactions", str(preview['interactions']))
        table.add_row("Memories", str(preview['memories']))
        table.add_row("Notes", str(preview['notes']))
        table.add_row("Tags", str(preview['tags']))
        table.add_row("List Memberships", str(preview['list_members']))
        table.add_row("Actions", str(preview['actions']))
        table.add_row("Email Activity", str(preview['email_activity']))
        console.print(table)

        if preview['emails_to_add']:
            console.print(f"\n[cyan]Emails to add:[/cyan] {', '.join(preview['emails_to_add'])}")

        if not yes:
            console.print("\n[dim]Run with --yes to execute the merge[/dim]")
            return

        # Execute
        result = db.merge_contacts(target_id, source_ids, email_labels=labels)

        if format == "json":
            import json as json_mod
            console.print(json_mod.dumps(result, indent=2))
        else:
            console.print(f"\n[green]OK:[/green] Merge complete!")
            console.print(f"  Interactions reassigned: {result['interactions']}")
            console.print(f"  Memories reassigned: {result['memories']}")
            console.print(f"  Notes reassigned: {result['notes']}")
            console.print(f"  Tags reassigned: {result['tags']}")
            console.print(f"  Emails added: {result['emails_added']}")
            console.print(f"  Sources deleted: {result['sources_deleted']}")

    except ValueError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Database error:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("emails")
def contacts_emails(
    contact_id: int = typer.Argument(..., help="Contact ID"),
    add: Optional[str] = typer.Option(None, "--add", help="Add an email address"),
    remove: Optional[str] = typer.Option(None, "--remove", help="Remove an email address"),
    set_primary: Optional[str] = typer.Option(None, "--set-primary", help="Set an email as primary"),
    update_label: Optional[str] = typer.Option(None, "--update-label", help="Email address to update the label for"),
    label: str = typer.Option("other", "--label", "-l", help="Label for new email or --update-label: primary, work, personal, other"),
    format: str = typer.Option("table", "--format", "-f", help="Output format: table or json"),
):
    """Manage email addresses for a contact.

    Usage:
      cc-vault contacts emails 156                                      # list
      cc-vault contacts emails 156 --add foo@bar.com --label work       # add
      cc-vault contacts emails 156 --remove foo@bar.com                 # remove
      cc-vault contacts emails 156 --set-primary foo@bar.com            # change primary
      cc-vault contacts emails 156 --update-label foo@bar.com -l work   # change label
    """
    db = get_db()

    try:
        contact = db.get_contact_by_id(contact_id)
        if not contact:
            console.print(f"[red]Contact #{contact_id} not found[/red]")
            raise typer.Exit(1)

        if add:
            row_id = db.add_contact_email(contact_id, add, label=label, is_primary=(label == 'primary'))
            console.print(f"[green]OK:[/green] Added {add} ({label}) to {contact.get('name', '')} (row #{row_id})")
            return

        if remove:
            db.remove_contact_email(contact_id, remove)
            console.print(f"[green]OK:[/green] Removed {remove} from {contact.get('name', '')}")
            return

        if set_primary:
            db.set_primary_email(contact_id, set_primary)
            console.print(f"[green]OK:[/green] Set {set_primary} as primary for {contact.get('name', '')}")
            return

        if update_label:
            db.update_contact_email_label(contact_id, update_label, label)
            console.print(f"[green]OK:[/green] Updated label for {update_label} to '{label}' on {contact.get('name', '')}")
            return

        # List mode
        emails = db.get_contact_emails(contact_id)

        if format == "json":
            import json as json_mod
            console.print(json_mod.dumps(emails, indent=2, default=str))
            return

        display_name = contact.get('name') or f"#{contact_id}"
        if not emails:
            console.print(f"[yellow]No emails in contact_emails table for {display_name}[/yellow]")
            console.print(f"[dim]Primary email from contacts table: {contact.get('email', 'N/A')}[/dim]")
            return

        table = Table(title=f"Emails for {display_name}")
        table.add_column("Email")
        table.add_column("Label")
        table.add_column("Primary", justify="center")

        for e in emails:
            primary_marker = "[green]*[/green]" if e.get('is_primary') else ""
            table.add_row(e.get('email', ''), e.get('label', ''), primary_marker)

        console.print(table)

    except ValueError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Database error:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("log-interaction")
def contacts_log_interaction(
    identifier: str = typer.Argument(..., help="Contact name, email, or ID"),
    interaction_type: str = typer.Option(..., "--type", "-t", help="Type: email, linkedin, phone, meeting, sms"),
    date: str = typer.Option(..., "--date", "-d", help="Interaction date (ISO format)"),
    direction: str = typer.Option("outbound", "--direction", help="Direction: inbound, outbound"),
    subject: Optional[str] = typer.Option(None, "--subject", "-s", help="Subject/title"),
    summary: Optional[str] = typer.Option(None, "--summary", help="Brief summary"),
    account: Optional[str] = typer.Option(None, "--account", "-a", help="Account used (e.g. mindzie-outlook)"),
    source_url: Optional[str] = typer.Option(None, "--source-url", help="URL to original communication"),
    message_id: Optional[str] = typer.Option(None, "--message-id", help="Email Message-ID for dedup"),
    sentiment: Optional[str] = typer.Option(None, "--sentiment", help="Tone: positive, neutral, negative"),
    action_required: bool = typer.Option(False, "--action-required", help="Follow-up needed"),
    action_description: Optional[str] = typer.Option(None, "--action-description", help="What follow-up is needed"),
    format: str = typer.Option("table", "--format", "-f", help="Output format: table or json"),
):
    """Log a communication interaction with a contact.

    Usage:
      cc-vault contacts log-interaction "John" --type email --date 2026-03-06 --direction outbound --subject "Proposal"
      cc-vault contacts log-interaction 42 --type linkedin --date 2026-03-06 --summary "Connected on LinkedIn"
    """
    db = get_db()

    try:
        # Resolve contact
        contact = None
        if identifier.isdigit():
            contact = db.get_contact_by_id(int(identifier))
        if not contact:
            contact = db.get_contact(identifier)
        if not contact:
            console.print(f"[red]Contact not found: {identifier}[/red]")
            raise typer.Exit(1)

        # Use add_interaction_direct since we already have the contact_id
        interaction_id = db.add_interaction_direct(
            contact_id=contact['id'],
            interaction_type=interaction_type,
            interaction_date=date,
            direction=direction,
            subject=subject,
            summary=summary,
            sentiment=sentiment,
            action_required=action_required,
            action_description=action_description,
            message_id=message_id,
            account=account,
            source_url=source_url,
        )

        if format == "json":
            import json as json_mod
            console.print(json_mod.dumps({
                "success": True,
                "interaction_id": interaction_id,
                "contact_id": contact['id'],
                "contact_name": contact.get('name', ''),
            }, indent=2))
        else:
            display_name = contact.get('name') or f"#{contact['id']}"
            console.print(f"[green]OK:[/green] Logged interaction #{interaction_id} for {display_name}")

    except ValueError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Database error:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("sync-recipients")
def contacts_sync_recipients(
    account: str = typer.Option(..., "-a", "--account", help="Account name (e.g. consulting, personal)"),
    file: Path = typer.Option(..., "--file", help="Path to recipients JSON file from cc-gmail/cc-outlook recipients --format json"),
    dry_run: bool = typer.Option(False, "--dry-run", help="Preview what would happen without writing"),
):
    """Bulk-import recipients into vault contacts and email activity.

    Usage:
      cc-vault contacts sync-recipients -a consulting --file recipients.json
      cc-vault contacts sync-recipients -a consulting --file recipients.json --dry-run
    """
    db = get_db()

    # Read and validate JSON file
    if not file.exists():
        console.print(f"[red]File not found:[/red] {file}")
        raise typer.Exit(1)

    try:
        data = json.loads(file.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError) as e:
        console.print(f"[red]Error reading JSON file:[/red] {e}")
        raise typer.Exit(1)

    if not isinstance(data, list):
        console.print("[red]JSON must be an array of recipient objects[/red]")
        raise typer.Exit(1)

    if len(data) == 0:
        console.print("[yellow]No recipients in file[/yellow]")
        return

    # Validate structure of first entry
    sample = data[0]
    if "email" not in sample:
        console.print("[red]Each recipient must have an 'email' field[/red]")
        raise typer.Exit(1)

    mode_label = "[yellow]DRY RUN[/yellow] " if dry_run else ""
    console.print(f"{mode_label}Syncing {len(data)} recipients for account '{account}'...")

    try:
        result = db.sync_recipients(data, account=account, dry_run=dry_run)
    except ValueError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)

    if dry_run:
        console.print(f"\n[yellow]Dry run results:[/yellow]")
    else:
        console.print(f"\n[green]Sync complete![/green]")

    console.print(f"  New contacts created:    {result['new_contacts']}")
    console.print(f"  Existing contacts found: {result['existing_contacts']}")
    console.print(f"  Email activities stored: {result['activities_upserted']}")

    if result.get("errors"):
        console.print(f"\n[yellow]Warnings ({len(result['errors'])}):[/yellow]")
        for err in result["errors"][:10]:
            console.print(f"  - {err}")
        if len(result["errors"]) > 10:
            console.print(f"  ... and {len(result['errors']) - 10} more")


@contacts_app.command("scan-emails")
def contacts_scan_emails(
    identifier: str = typer.Argument(..., help="Contact name, email, or ID"),
    account: str = typer.Option(..., "-a", "--account", help="Account: outlook, gmail, or gmail:consulting"),
    count: int = typer.Option(20, "-n", "--count", help="Number of emails to scan per direction"),
    format: str = typer.Option("table", "--format", "-f", help="Output format: table or json"),
):
    """Scan recent emails for a contact and create interaction records.

    Pulls recent inbound and outbound emails for the contact using cc-outlook or cc-gmail,
    then creates interaction entries with dedup via message_id.

    For Gmail sub-accounts, use colon syntax: gmail:consulting, gmail:personal

    Usage:
      cc-vault contacts scan-emails "John" --account outlook
      cc-vault contacts scan-emails 42 --account gmail -n 30
      cc-vault contacts scan-emails 42 --account gmail:consulting -n 20
    """
    import subprocess
    import re

    db = get_db()

    def _normalize_date(date_str: str) -> str:
        """Convert RFC 2822 or other date formats to ISO format."""
        from email.utils import parsedate_to_datetime
        try:
            dt = parsedate_to_datetime(date_str)
            return dt.strftime('%Y-%m-%dT%H:%M:%S')
        except (ValueError, TypeError):
            pass
        # Already ISO or close enough
        return date_str

    def _parse_search_output(text: str) -> list:
        """Parse cc-gmail/cc-outlook search output into email dicts.

        Expected format:
        [ ] <message_id>
            From: Name <email>
            Subject: subject line
            Date: date string
        """
        emails = []
        current = None
        for line in text.splitlines():
            line = line.rstrip()
            # Match message ID line: [ ] <id> or [x] <id>
            id_match = re.match(r'^\[.\]\s+(\S+)', line)
            if id_match:
                if current:
                    emails.append(current)
                current = {'message_id': id_match.group(1), 'subject': '', 'date': '', 'from': ''}
                continue
            if current is None:
                continue
            stripped = line.strip()
            if stripped.startswith('From:'):
                current['from'] = stripped[5:].strip()
            elif stripped.startswith('Subject:'):
                current['subject'] = stripped[8:].strip()
            elif stripped.startswith('Date:'):
                current['date'] = _normalize_date(stripped[5:].strip())
        if current:
            emails.append(current)
        return emails

    try:
        # Resolve contact
        contact = None
        if identifier.isdigit():
            contact = db.get_contact_by_id(int(identifier))
        if not contact:
            contact = db.get_contact(identifier)
        if not contact:
            console.print(f"[red]Contact not found: {identifier}[/red]")
            raise typer.Exit(1)

        contact_email = contact.get('email')
        if not contact_email:
            console.print(f"[red]Contact #{contact['id']} has no email address[/red]")
            raise typer.Exit(1)

        display_name = contact.get('name') or f"#{contact['id']}"

        # Parse account: "gmail:consulting" -> tool="cc-gmail", sub_account="consulting"
        sub_account = None
        if ':' in account:
            base_account, sub_account = account.split(':', 1)
            tool = f"cc-{base_account}"
            account_label = account
        else:
            tool = f"cc-{account}"
            account_label = account

        console.print(f"Scanning {account_label} emails for {display_name} ({contact_email})...")

        created = 0
        skipped = 0
        errors = 0

        for direction, search_flag in [("inbound", "from"), ("outbound", "to")]:
            cmd = [
                tool,
            ]
            if sub_account:
                cmd.extend(["-a", sub_account])
            cmd.extend([
                "search",
                f"{search_flag}:{contact_email}",
                "--count", str(count),
            ])

            try:
                result = subprocess.run(
                    cmd, capture_output=True, text=True, timeout=60,
                    encoding='utf-8', errors='replace',
                )
            except FileNotFoundError:
                console.print(f"[red]ERROR:[/red] {tool} not found on PATH")
                raise typer.Exit(1)
            except subprocess.TimeoutExpired:
                console.print(f"[red]ERROR:[/red] {tool} search timed out")
                raise typer.Exit(1)

            if result.returncode != 0:
                console.print(f"[red]ERROR:[/red] {tool} search failed: {result.stderr or result.stdout}")
                errors += 1
                continue

            emails = _parse_search_output(result.stdout)
            console.print(f"  {direction}: found {len(emails)} emails")

            for email in emails:
                msg_id = email.get('message_id', '')
                email_subject = email.get('subject', '')
                email_date = email.get('date', '')

                if not email_date:
                    continue

                try:
                    interaction_id = db.add_interaction(
                        email=contact_email,
                        interaction_type='email',
                        interaction_date=email_date,
                        direction=direction,
                        subject=email_subject,
                        message_id=msg_id if msg_id else None,
                        account=account_label,
                    )
                    if interaction_id > 0:
                        created += 1
                    else:
                        # Negative ID means dedup hit
                        skipped += 1
                except (ValueError, sqlite3.IntegrityError):
                    skipped += 1

        # Update email_activity from actual interaction records
        conn = db.get_db()
        cursor = conn.cursor()
        cursor.execute("""
            SELECT
                COUNT(CASE WHEN direction = 'outbound' THEN 1 END) AS sent,
                COUNT(CASE WHEN direction = 'inbound' THEN 1 END) AS received,
                MIN(interaction_date) AS first_date,
                MAX(interaction_date) AS last_date
            FROM interactions
            WHERE contact_id = ? AND type = 'email' AND account = ?
        """, (contact['id'], account_label))
        row = cursor.fetchone()
        conn.close()

        sent_total = row['sent'] if row else 0
        recv_total = row['received'] if row else 0
        first_date = row['first_date'] if row else None
        last_date_val = row['last_date'] if row else None

        db.upsert_email_activity(
            contact_id=contact['id'],
            account=account_label,
            sent_count=sent_total,
            received_count=recv_total,
            first_email_date=first_date,
            last_email_date=last_date_val,
        )

        # Update last_contact on the contact
        last_comm = db.get_last_communication(contact['id'])
        if last_comm.get('last_touch'):
            last_date = last_comm['last_touch'].get('interaction_date', '')
            if last_date:
                conn = db.get_db()
                cursor = conn.cursor()
                cursor.execute(
                    "UPDATE contacts SET last_contact = ?, updated_at = CURRENT_TIMESTAMP WHERE id = ?",
                    (last_date[:10], contact['id'])
                )
                conn.commit()
                conn.close()

        if format == "json":
            import json as json_mod
            console.print(json_mod.dumps({
                "success": True,
                "contact_id": contact['id'],
                "contact_name": display_name,
                "created": created,
                "skipped_dedup": skipped,
                "errors": errors,
                "email_activity": {
                    "sent": sent_total,
                    "received": recv_total,
                    "first_date": first_date,
                    "last_date": last_date_val,
                },
            }, indent=2))
        else:
            console.print(f"\n[green]OK:[/green] Scan complete for {display_name}")
            console.print(f"  Created: {created} interactions")
            console.print(f"  Skipped (dedup): {skipped}")
            if errors:
                console.print(f"  Errors: {errors}")
            console.print(f"  Email activity: {sent_total} sent, {recv_total} received")

    except sqlite3.Error as e:
        console.print(f"[red]Database error:[/red] {e}")
        raise typer.Exit(1)


# =============================================================================
# Contact Tag Commands
# =============================================================================

@contacts_app.command("tag")
def contacts_tag(
    contact_id: int = typer.Argument(..., help="Contact ID"),
    tags: List[str] = typer.Argument(..., help="Tags to add"),
):
    """Add tags to a contact.

    Example: cc-vault contacts tag 42 toronto ai-interested course-prospect
    """
    db = get_db()

    try:
        contact = db.get_contact_by_id(contact_id)
        if not contact:
            console.print(f"[red]Contact #{contact_id} not found[/red]")
            raise typer.Exit(1)

        added = db.add_tags_by_id(contact_id, *tags)
        display_name = contact.get('name') or f"#{contact_id}"
        all_tags = db.get_tags(contact_id)
        console.print(f"[green]Added {added} tag(s) to {display_name}[/green]")
        console.print(f"Tags: {', '.join(all_tags)}")

    except ValueError as e:
        console.print(f"[red]{e}[/red]")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error tagging contact:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("untag")
def contacts_untag(
    contact_id: int = typer.Argument(..., help="Contact ID"),
    tags: List[str] = typer.Argument(..., help="Tags to remove"),
):
    """Remove tags from a contact.

    Example: cc-vault contacts untag 42 course-prospect
    """
    db = get_db()

    try:
        contact = db.get_contact_by_id(contact_id)
        if not contact:
            console.print(f"[red]Contact #{contact_id} not found[/red]")
            raise typer.Exit(1)

        removed = db.remove_tags_by_id(contact_id, *tags)
        display_name = contact.get('name') or f"#{contact_id}"
        remaining = db.get_tags(contact_id)
        console.print(f"[green]Removed {removed} tag(s) from {display_name}[/green]")
        if remaining:
            console.print(f"Remaining tags: {', '.join(remaining)}")
        else:
            console.print("No tags remaining.")

    except ValueError as e:
        console.print(f"[red]{e}[/red]")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error removing tags:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("tags")
def contacts_tags(
    contact_id: int = typer.Argument(..., help="Contact ID"),
):
    """Show all tags for a contact.

    Example: cc-vault contacts tags 42
    """
    db = get_db()

    try:
        contact = db.get_contact_by_id(contact_id)
        if not contact:
            console.print(f"[red]Contact #{contact_id} not found[/red]")
            raise typer.Exit(1)

        tags = db.get_tags(contact_id)
        display_name = contact.get('name') or f"#{contact_id}"

        if not tags:
            console.print(f"[yellow]{display_name} has no tags[/yellow]")
            return

        console.print(f"[bold]{display_name}[/bold] ({len(tags)} tags):")
        for tag in tags:
            console.print(f"  - {tag}")

    except sqlite3.Error as e:
        console.print(f"[red]Error getting tags:[/red] {e}")
        raise typer.Exit(1)


# =============================================================================
# Tags App Commands (cc-vault tags ...)
# =============================================================================

@tags_app.callback(invoke_without_command=True)
def tags_default(ctx: typer.Context):
    """List all tags with contact counts."""
    if ctx.invoked_subcommand is not None:
        return

    db = get_db()
    try:
        all_tags = db.list_all_tags()

        if not all_tags:
            console.print("[yellow]No tags found. Tag a contact with: cc-vault contacts tag <id> <tag>[/yellow]")
            return

        table = Table(title="Contact Tags")
        table.add_column("Tag", style="cyan")
        table.add_column("Contacts", justify="right")

        for t in all_tags:
            table.add_row(t['tag'], str(t['count']))

        console.print(table)
        console.print(f"\n[dim]{len(all_tags)} tags across {sum(t['count'] for t in all_tags)} assignments[/dim]")

    except sqlite3.Error as e:
        console.print(f"[red]Error listing tags:[/red] {e}")
        raise typer.Exit(1)


@tags_app.command("show")
def tags_show(
    tag: str = typer.Argument(..., help="Tag to look up"),
    format: str = typer.Option("text", "--format", "-f", help="Output format: text, json"),
):
    """Show all contacts with a specific tag.

    Example: cc-vault tags show toronto
    """
    db = get_db()

    try:
        contacts = db.list_contacts_by_tag(tag)

        if not contacts:
            console.print(f"[yellow]No contacts found with tag \"{tag}\"[/yellow]")
            return

        if format == "json":
            print(json.dumps(contacts, indent=2, default=str))
            return

        table = Table(title=f"Contacts tagged \"{tag}\" ({len(contacts)})")
        table.add_column("ID", style="dim")
        table.add_column("Name", style="cyan")
        table.add_column("Email")
        table.add_column("Company")
        table.add_column("Relationship")

        for c in contacts:
            table.add_row(
                str(c['id']),
                c.get('name', '-') or '-',
                c.get('email', '-') or '-',
                c.get('company', '-') or '-',
                c.get('relationship', '-') or '-',
            )

        console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error showing tag:[/red] {e}")
        raise typer.Exit(1)


# =============================================================================
# Documents Commands
# =============================================================================

@docs_app.command("list")
def docs_list(
    doc_type: Optional[str] = typer.Option(None, "-t", "--type", help="Filter by type: note, journal, transcript, research"),
    limit: int = typer.Option(20, "-n", help="Max results"),
):
    """List documents."""
    db = get_db()

    try:
        docs = db.list_documents(doc_type=doc_type, limit=limit)

        if not docs:
            console.print("[yellow]No documents found[/yellow]")
            return

        table = Table(title="Documents")
        table.add_column("ID", style="dim")
        table.add_column("Title", style="cyan")
        table.add_column("Type")
        table.add_column("Date")
        table.add_column("Tags")

        for doc in docs:
            table.add_row(
                str(doc['id']),
                doc.get('title', '')[:40],
                doc.get('doc_type', '-'),
                doc.get('created_at', '')[:10],
                doc.get('tags', '-') or '-',
            )

        console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error listing documents:[/red] {e}")
        raise typer.Exit(1)


@docs_app.command("add")
def docs_add(
    path: Path = typer.Argument(..., help="File path to import"),
    doc_type: str = typer.Option("research", "-t", "--type", help="Document type"),
    title: Optional[str] = typer.Option(None, "--title", help="Document title"),
    tags: Optional[str] = typer.Option(None, "--tags", help="Comma-separated tags"),
):
    """Import a document into the vault."""
    try:
        try:
            from .importer import import_document
        except ImportError:
            from importer import import_document

        result = import_document(str(path), doc_type=doc_type, title=title, tags=tags)

        if result.get('success'):
            console.print(f"[green]Document imported:[/green] #{result['document_id']}")
            console.print(f"  Path: {result.get('path')}")
            if result.get('chunk_count'):
                console.print(f"  Chunks: {result['chunk_count']}")
        else:
            console.print(f"[red]Error:[/red] {result.get('error')}")
            raise typer.Exit(1)

    except (OSError, sqlite3.Error, ValueError) as e:
        console.print(f"[red]Error importing document:[/red] {e}")
        raise typer.Exit(1)


@docs_app.command("show")
def docs_show(
    doc_id: int = typer.Argument(..., help="Document ID"),
):
    """Show document details."""
    db = get_db()

    try:
        doc = db.get_document(doc_id)

        if not doc:
            console.print(f"[red]Document #{doc_id} not found[/red]")
            raise typer.Exit(1)

        # Header
        console.print(f"\n[bold cyan]{doc.get('title', 'Untitled')}[/bold cyan]")
        console.print(f"[dim]Document #{doc_id}[/dim]\n")

        # Details
        table = Table(show_header=False, box=None)
        table.add_column("Property", style="cyan", width=15)
        table.add_column("Value")

        table.add_row("Type", doc.get('doc_type', '-'))
        table.add_row("Path", doc.get('path', '-'))
        table.add_row("Created", doc.get('created_at', '-')[:19] if doc.get('created_at') else '-')
        if doc.get('tags'):
            table.add_row("Tags", doc['tags'])
        if doc.get('source'):
            table.add_row("Source", doc['source'])

        console.print(table)

        # Content preview
        if doc.get('path'):
            full_path = DOCUMENTS_PATH / doc['path']
            if full_path.exists():
                content = full_path.read_text(encoding='utf-8')[:500]
                console.print(f"\n[cyan]Preview:[/cyan]\n{content}...")

    except (OSError, sqlite3.Error) as e:
        console.print(f"[red]Error showing document:[/red] {e}")
        raise typer.Exit(1)


@docs_app.command("search")
def docs_search(
    query: str = typer.Argument(..., help="Search query"),
):
    """Search documents using full-text search."""
    db = get_db()

    try:
        results = db.search_documents(query)

        if not results:
            console.print(f"[yellow]No documents matching:[/yellow] {query}")
            return

        console.print(f"\n[cyan]Search Results ({len(results)}):[/cyan]\n")

        for doc in results:
            console.print(f"  #{doc['id']} [cyan]{doc.get('title', 'Untitled')}[/cyan]")
            console.print(f"    Type: {doc.get('doc_type', '-')} | Path: {doc.get('path', '-')[:50]}")
            console.print()

    except sqlite3.Error as e:
        console.print(f"[red]Error searching documents:[/red] {e}")
        raise typer.Exit(1)


def _resolve_docs_to_index(
    docs: List[dict],
) -> tuple[List[tuple[dict, str]], List[dict]]:
    """Read document files from disk, separating indexable from missing/empty."""
    to_index = []
    missing = []

    for doc in docs:
        doc_path = doc.get('path', '')
        if not doc_path:
            missing.append(doc)
            continue

        full_path = DOCUMENTS_PATH / doc_path
        if not full_path.exists():
            missing.append(doc)
            continue

        content = full_path.read_text(encoding='utf-8', errors='replace')
        if not content.strip():
            missing.append(doc)
            continue

        to_index.append((doc, content))

    return to_index, missing


def _print_dry_run(
    docs_to_index: List[tuple[dict, str]],
    missing_files: List[dict],
) -> None:
    """Print dry-run report of what would be indexed."""
    console.print("\n[dim]-- Dry run, no changes made --[/dim]\n")
    for doc, content in docs_to_index[:20]:
        console.print(f"  #{doc['id']} {doc.get('title', 'Untitled')} ({len(content)} chars)")
    if len(docs_to_index) > 20:
        console.print(f"  ... and {len(docs_to_index) - 20} more")
    if missing_files:
        console.print(f"\n[yellow]Files missing or empty:[/yellow]")
        for doc in missing_files[:10]:
            console.print(f"  #{doc['id']} {doc.get('title', 'Untitled')} -> {doc.get('path', 'no path')}")
        if len(missing_files) > 10:
            console.print(f"  ... and {len(missing_files) - 10} more")


@docs_app.command("reindex")
def docs_reindex(
    doc_id: Optional[int] = typer.Argument(None, help="Specific document ID to reindex"),
    all_docs: bool = typer.Option(False, "--all", help="Reindex all documents (re-chunk everything)"),
    unchunked: bool = typer.Option(False, "--unchunked", help="Only index documents that have no chunks"),
    dry_run: bool = typer.Option(False, "--dry-run", help="Show what would be indexed without doing it"),
):
    """Chunk and index documents that are missing from the search index."""
    try:
        try:
            from .vectors import VaultVectors
            from .db import get_db as get_db_conn, init_db, get_document
        except ImportError:
            from vectors import VaultVectors
            from db import get_db as get_db_conn, init_db, get_document

        init_db(silent=True)
        conn = get_db_conn()

        # Determine which documents to process
        if doc_id is not None:
            doc = get_document(doc_id)
            if not doc:
                console.print(f"[red]Document #{doc_id} not found[/red]")
                raise typer.Exit(1)
            docs = [doc]
        elif all_docs:
            cursor = conn.execute(
                "SELECT id, title, path, doc_type FROM documents ORDER BY id"
            )
            docs = [dict(row) for row in cursor.fetchall()]
        else:
            cursor = conn.execute("""
                SELECT id, title, path, doc_type FROM documents d
                WHERE NOT EXISTS (SELECT 1 FROM chunks c WHERE c.document_id = d.id)
                ORDER BY id
            """)
            docs = [dict(row) for row in cursor.fetchall()]

        conn.close()

        if not docs:
            console.print("[green]All documents are already indexed.[/green]")
            return

        docs_to_index, missing_files = _resolve_docs_to_index(docs)

        console.print(f"\n[cyan]Documents to index:[/cyan] {len(docs_to_index)}")
        if missing_files:
            console.print(f"[yellow]Skipping {len(missing_files)} docs (file missing or empty)[/yellow]")

        if dry_run:
            _print_dry_run(docs_to_index, missing_files)
            return

        # Index documents
        vecs = VaultVectors()
        indexed = 0
        errors = 0

        for i, (doc, content) in enumerate(docs_to_index, 1):
            try:
                chunk_ids = vecs.index_document_chunks(
                    document_id=doc['id'],
                    content=content,
                    metadata={
                        'doc_title': doc.get('title', ''),
                        'doc_type': doc.get('doc_type', ''),
                        'doc_path': doc.get('path', ''),
                    }
                )

                ts_conn = get_db_conn()
                try:
                    ts_conn.execute(
                        "UPDATE documents SET indexed_at = CURRENT_TIMESTAMP WHERE id = ?",
                        (doc['id'],)
                    )
                    ts_conn.commit()
                finally:
                    ts_conn.close()

                indexed += 1
                if i % 10 == 0 or i == len(docs_to_index):
                    console.print(f"  Indexed {i}/{len(docs_to_index)} ({len(chunk_ids)} chunks for #{doc['id']})")

            except (OSError, sqlite3.Error, ValueError, RuntimeError) as e:
                errors += 1
                console.print(f"  [red]Error indexing #{doc['id']} {doc.get('title', '')}:[/red] {e}")

        console.print(f"\n[green]Done.[/green] Indexed: {indexed} | Errors: {errors} | Skipped: {len(missing_files)}")

    except (OSError, sqlite3.Error) as e:
        console.print(f"[red]Error during reindex:[/red] {e}")
        raise typer.Exit(1)


# =============================================================================
# Config Commands
# =============================================================================

@config_app.command("show")
def config_show():
    """Show current configuration."""
    import os
    config = get_config()

    table = Table(title="Vault Configuration")
    table.add_column("Setting", style="cyan")
    table.add_column("Value")

    table.add_row("Vault Path", str(config.vault_path))
    table.add_row("Database", str(config.db_path))
    table.add_row("Vector DB", str(config.vectors_path))
    table.add_row("Documents", str(config.documents_path))
    table.add_row("OpenAI API Key", "Set" if os.environ.get("OPENAI_API_KEY") else "[red]Not Set[/red]")

    console.print(table)


@config_app.command("set")
def config_set(
    key: str = typer.Argument(..., help="Config key to set"),
    value: str = typer.Argument(..., help="Config value"),
):
    """Set a configuration value."""
    try:
        from .config import save_config
    except ImportError:
        from config import save_config

    try:
        save_config(key, value)
        console.print(f"[green]Config updated:[/green] {key} = {value}")
    except (OSError, ValueError) as e:
        console.print(f"[red]Error setting config:[/red] {e}")
        raise typer.Exit(1)


# =============================================================================
# Health Commands
# =============================================================================

@health_app.command("list")
def health_list(
    category: Optional[str] = typer.Option(None, "-c", "--category", help="Filter by category"),
    days: int = typer.Option(30, "-d", "--days", help="Days to show"),
):
    """List health entries."""
    db = get_db()

    try:
        entries = db.list_health_entries(category=category, days=days)

        if not entries:
            console.print("[yellow]No health entries found[/yellow]")
            return

        table = Table(title="Health Entries")
        table.add_column("ID", style="dim")
        table.add_column("Date", style="cyan")
        table.add_column("Category")
        table.add_column("Summary")

        for e in entries:
            table.add_row(
                str(e['id']),
                e.get('entry_date', '-'),
                e.get('category', '-'),
                (e.get('summary', '-') or '-')[:50],
            )

        console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error listing health entries:[/red] {e}")
        raise typer.Exit(1)


@health_app.command("insights")
def health_insights(
    query: str = typer.Option("recent health trends", "-q", "--query", help="Health query"),
    days: int = typer.Option(30, "-d", "--days", help="Days to analyze"),
):
    """Get AI-powered health insights."""
    try:
        try:
            from .rag import get_vault_rag
        except ImportError:
            from rag import get_vault_rag
        rag = get_vault_rag()

        console.print(f"[dim]Analyzing health data...[/dim]")
        result = rag.health_insights(query, days=days)

        if 'error' in result:
            console.print(f"[red]Error:[/red] {result['error']}")
            raise typer.Exit(1)

        console.print(f"\n[cyan]Health Insights:[/cyan]\n{result['insights']}")
        console.print(f"\n[dim]Based on {result['data_points']} data points[/dim]")

    except ImportError as e:
        console.print(f"[red]Error:[/red] RAG not available: {e}")
        raise typer.Exit(1)


# =============================================================================
# Graph Commands (Entity Links)
# =============================================================================

graph_app = typer.Typer(help="Graph statistics and traversal", cls=AliasGroup)
app.add_typer(graph_app, name="graph")


def _get_valid_entity_types() -> List[str]:
    """Get valid entity types from config."""
    try:
        from .config import ENTITY_TYPES
    except ImportError:
        from config import ENTITY_TYPES
    return ENTITY_TYPES


@app.command("link")
def create_link(
    source_type: str = typer.Argument(..., help="Source entity type (contact, task, goal, idea, document)"),
    source_id: int = typer.Argument(..., help="Source entity ID"),
    target_type: str = typer.Argument(..., help="Target entity type"),
    target_id: int = typer.Argument(..., help="Target entity ID"),
    rel: str = typer.Option(..., "--rel", "-r", help="Relationship type (e.g., works_on, mentions, supports)"),
    strength: int = typer.Option(3, "--strength", "-s", help="Relationship strength (1-5)"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Create a link between two entities."""
    db = get_db()

    valid_types = _get_valid_entity_types()
    if source_type not in valid_types:
        console.print(f"[red]Error:[/red] Invalid source_type '{source_type}'. Must be: {', '.join(valid_types)}")
        raise typer.Exit(1)
    if target_type not in valid_types:
        console.print(f"[red]Error:[/red] Invalid target_type '{target_type}'. Must be: {', '.join(valid_types)}")
        raise typer.Exit(1)
    if strength < 1 or strength > 5:
        console.print(f"[red]Error:[/red] Strength must be between 1 and 5")
        raise typer.Exit(1)

    try:
        link_id = db.add_entity_link(
            source_type=source_type,
            source_id=source_id,
            target_type=target_type,
            target_id=target_id,
            relationship=rel,
            strength=strength,
        )

        if json_output:
            result = {
                "success": True,
                "link_id": link_id,
                "source": {"type": source_type, "id": source_id},
                "target": {"type": target_type, "id": target_id},
                "relationship": rel,
                "strength": strength,
            }
            console.print(json.dumps(result, indent=2))
        else:
            console.print(f"[green]Linked[/green] {source_type}:{source_id} -> {target_type}:{target_id} ({rel}, strength={strength})")

    except (sqlite3.Error, ValueError) as e:
        console.print(f"[red]Error creating link:[/red] {e}")
        raise typer.Exit(1)


@app.command("unlink")
def remove_link(
    source_type: str = typer.Argument(..., help="Source entity type"),
    source_id: int = typer.Argument(..., help="Source entity ID"),
    target_type: str = typer.Argument(..., help="Target entity type"),
    target_id: int = typer.Argument(..., help="Target entity ID"),
    rel: Optional[str] = typer.Option(None, "--rel", "-r", help="Specific relationship to remove (optional)"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Remove a link between two entities."""
    db = get_db()

    try:
        removed = db.remove_entity_link(
            source_type=source_type,
            source_id=source_id,
            target_type=target_type,
            target_id=target_id,
            relationship=rel,
        )

        if json_output:
            result = {
                "success": removed,
                "source": {"type": source_type, "id": source_id},
                "target": {"type": target_type, "id": target_id},
                "relationship": rel,
            }
            console.print(json.dumps(result, indent=2))
        else:
            if removed:
                console.print(f"[green]Unlinked[/green] {source_type}:{source_id} -> {target_type}:{target_id}")
            else:
                console.print(f"[yellow]No link found[/yellow] {source_type}:{source_id} -> {target_type}:{target_id}")

    except sqlite3.Error as e:
        console.print(f"[red]Error removing link:[/red] {e}")
        raise typer.Exit(1)


@app.command("links")
def get_links(
    entity_type: str = typer.Argument(..., help="Entity type (contact, task, goal, idea, document)"),
    entity_id: int = typer.Argument(..., help="Entity ID"),
    depth: int = typer.Option(1, "--depth", "-d", help="Depth of traversal (1=direct only)"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Get all links for an entity."""
    try:
        try:
            from .graph import get_vault_graph
        except ImportError:
            from graph import get_vault_graph

        graph = get_vault_graph()
        result = graph.get_links(entity_type, entity_id, depth=depth)

        if 'error' in result:
            console.print(f"[red]Error:[/red] {result['error']}")
            raise typer.Exit(1)

        if json_output:
            console.print(json.dumps(result, indent=2))
        else:
            entity = result.get('entity', {})
            console.print(f"\n[bold cyan]{entity.get('type', '')}:{entity.get('id', '')}[/bold cyan] - {entity.get('label', '')}")

            links = result.get('links', [])
            if not links:
                console.print("[dim]No links found[/dim]")
            else:
                table = Table(title=f"Links ({len(links)} total)")
                table.add_column("Type", style="cyan")
                table.add_column("ID", style="dim")
                table.add_column("Label")
                table.add_column("Relationship")
                table.add_column("Dir")
                table.add_column("Strength", justify="right")

                for link in links:
                    direction = "<-" if link.get('direction') == 'incoming' else "->"
                    via = f" (via {link.get('via')})" if link.get('via') else ""
                    table.add_row(
                        link.get('type', ''),
                        str(link.get('id', '')),
                        (link.get('label', '')[:40] + via),
                        link.get('relationship', '-') or '-',
                        direction,
                        str(link.get('strength', '')),
                    )

                console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error getting links:[/red] {e}")
        raise typer.Exit(1)


@app.command("context")
def get_context(
    entity_type: str = typer.Argument(..., help="Entity type (contact, task, goal, idea, document)"),
    entity_id: int = typer.Argument(..., help="Entity ID"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Get entity with full linked context (for agents)."""
    try:
        try:
            from .graph import get_vault_graph
        except ImportError:
            from graph import get_vault_graph

        graph = get_vault_graph()
        result = graph.get_context(entity_type, entity_id)

        if 'error' in result:
            console.print(f"[red]Error:[/red] {result['error']}")
            raise typer.Exit(1)

        if json_output:
            console.print(json.dumps(result, indent=2))
        else:
            entity = result.get('entity', {})
            details = entity.get('details', {})

            # Header
            console.print(f"\n[bold cyan]{entity.get('type', '')}:{entity.get('id', '')}[/bold cyan]")

            # Entity details
            table = Table(show_header=False, box=None)
            table.add_column("Property", style="cyan", width=15)
            table.add_column("Value")

            for key, value in details.items():
                if value and key != 'label':
                    table.add_row(key, str(value)[:60])

            console.print(table)

            # Linked items
            linked = result.get('linked', [])
            if linked:
                console.print(f"\n[cyan]Linked Items ({len(linked)}):[/cyan]")

                for item in linked:
                    direction = "<-" if item.get('direction') == 'incoming' else "->"
                    rel = item.get('relationship') or ''
                    item_details = item.get('details', {})
                    label = item_details.get('label', '') or item_details.get('name', '') or item_details.get('title', '')

                    console.print(f"  {direction} [{item.get('type')}:{item.get('id')}] {label[:50]}")
                    if rel:
                        console.print(f"     [dim]relationship: {rel}, strength: {item.get('strength', 1)}[/dim]")
            else:
                console.print("\n[dim]No linked items[/dim]")

    except sqlite3.Error as e:
        console.print(f"[red]Error getting context:[/red] {e}")
        raise typer.Exit(1)


@graph_app.command("stats")
def graph_stats(
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Show graph statistics."""
    db = get_db()

    try:
        stats = db.get_graph_stats()

        if json_output:
            console.print(json.dumps(stats, indent=2))
        else:
            # Entity counts
            table = Table(title="Graph Statistics")
            table.add_column("Entity Type", style="cyan")
            table.add_column("Count", justify="right")

            entities = stats.get('entities', {})
            for entity_type, count in entities.items():
                table.add_row(entity_type, str(count))

            table.add_row("", "")
            table.add_row("[bold]Total Links[/bold]", f"[bold]{stats.get('total_links', 0)}[/bold]")

            console.print(table)

            # Most connected
            most_connected = stats.get('most_connected', [])
            if most_connected:
                console.print("\n[cyan]Most Connected Entities:[/cyan]")
                for item in most_connected[:5]:
                    console.print(f"  {item.get('type')}:{item.get('id')} - {item.get('name', '')} ({item.get('links', 0)} links)")

    except sqlite3.Error as e:
        console.print(f"[red]Error getting stats:[/red] {e}")
        raise typer.Exit(1)


@graph_app.command("path")
def graph_path(
    from_type: str = typer.Argument(..., help="Source entity type"),
    from_id: int = typer.Argument(..., help="Source entity ID"),
    to_type: str = typer.Argument(..., help="Target entity type"),
    to_id: int = typer.Argument(..., help="Target entity ID"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Find path between two entities."""
    try:
        try:
            from .graph import get_vault_graph
        except ImportError:
            from graph import get_vault_graph

        graph = get_vault_graph()
        path = graph.find_path(from_type, from_id, to_type, to_id)

        if path is None:
            result = {"found": False, "path": None}
            if json_output:
                console.print(json.dumps(result, indent=2))
            else:
                console.print(f"[yellow]No path found[/yellow] between {from_type}:{from_id} and {to_type}:{to_id}")
            return

        if json_output:
            result = {"found": True, "path": path, "length": len(path)}
            console.print(json.dumps(result, indent=2))
        else:
            console.print(f"\n[cyan]Path ({len(path)} steps):[/cyan]\n")

            for i, node in enumerate(path):
                prefix = "  " if i == 0 else "  -> "
                rel = f" ({node.get('relationship')})" if node.get('relationship') else ""
                console.print(f"{prefix}[{node.get('type')}:{node.get('id')}] {node.get('label', '')}{rel}")

    except sqlite3.Error as e:
        console.print(f"[red]Error finding path:[/red] {e}")
        raise typer.Exit(1)


@graph_app.command("sync-fk")
def graph_sync_fk(
    dry_run: bool = typer.Option(False, "--dry-run", help="Preview without making changes"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Populate entity_links from FK relationships in the schema."""
    db = get_db()

    try:
        stats = db.populate_links_from_fk(dry_run=dry_run)

        if json_output:
            console.print(json.dumps(stats, indent=2))
        else:
            mode = "[yellow]DRY RUN[/yellow] - " if dry_run else ""
            console.print(f"\n{mode}[cyan]FK Relationship Sync[/cyan]\n")

            # Show per-relationship stats
            table = Table(title="Relationships Processed")
            table.add_column("Relationship", style="cyan")
            table.add_column("Found", justify="right")
            table.add_column("Created", justify="right", style="green")
            table.add_column("Skipped", justify="right", style="yellow")

            for rel_name, rel_stats in stats.get("relationships", {}).items():
                table.add_row(
                    rel_name,
                    str(rel_stats.get("found", 0)),
                    str(rel_stats.get("created", 0)),
                    str(rel_stats.get("skipped", 0))
                )

            console.print(table)

            # Summary
            console.print(f"\n[bold]Total links created:[/bold] {stats.get('total_created', 0)}")
            if stats.get("total_skipped", 0) > 0:
                console.print(f"[yellow]Total skipped:[/yellow] {stats.get('total_skipped', 0)}")

            # Errors
            errors = stats.get("errors", [])
            if errors:
                console.print(f"\n[red]Errors ({len(errors)}):[/red]")
                for err in errors[:10]:
                    console.print(f"  - {err}")
                if len(errors) > 10:
                    console.print(f"  ... and {len(errors) - 10} more")

            if dry_run:
                console.print("\n[yellow]Run without --dry-run to create links[/yellow]")

    except sqlite3.Error as e:
        console.print(f"[red]Error syncing FK relationships:[/red] {e}")
        raise typer.Exit(1)


# ===========================================
# LISTS COMMANDS
# ===========================================


@lists_app.callback(invoke_without_command=True)
def lists_default(ctx: typer.Context):
    """List all contact lists."""
    if ctx.invoked_subcommand is not None:
        return

    db = get_db()
    try:
        lists = db.list_lists()

        if not lists:
            console.print("[yellow]No lists found. Create one with: cc-vault lists create \"List Name\"[/yellow]")
            return

        table = Table(title="Contact Lists")
        table.add_column("ID", style="dim")
        table.add_column("Name", style="cyan")
        table.add_column("Type")
        table.add_column("Members", justify="right")
        table.add_column("Description")
        table.add_column("Created", style="dim")

        for lst in lists:
            table.add_row(
                str(lst['id']),
                lst['name'],
                lst.get('list_type', 'general') or 'general',
                str(lst.get('member_count', 0)),
                (lst.get('description') or '-')[:40],
                lst.get('created_at', '-')[:10] if lst.get('created_at') else '-',
            )

        console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error listing lists:[/red] {e}")
        raise typer.Exit(1)


@lists_app.command("create")
def lists_create(
    name: str = typer.Argument(..., help="List name"),
    description: Optional[str] = typer.Option(None, "--description", "-d", help="List description"),
    list_type: str = typer.Option("general", "--type", "-t", help="List type (e.g. general, mailing, outreach)"),
):
    """Create a new contact list."""
    db = get_db()
    try:
        list_id = db.create_list(name=name, description=description, list_type=list_type)
        console.print(f"[green]List created:[/green] #{list_id} - {name}")
    except sqlite3.IntegrityError:
        console.print(f"[red]List already exists:[/red] {name}")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error creating list:[/red] {e}")
        raise typer.Exit(1)


@lists_app.command("show")
def lists_show(
    name: str = typer.Argument(..., help="List name"),
):
    """Show members of a contact list."""
    db = get_db()
    try:
        lst = db.get_list(name)
        if not lst:
            console.print(f"[red]List not found:[/red] {name}")
            raise typer.Exit(1)

        members = db.get_list_members(name)

        console.print(f"\n[bold cyan]{lst['name']}[/bold cyan]")
        if lst.get('description'):
            console.print(f"[dim]{lst['description']}[/dim]")
        console.print(f"Type: {lst.get('list_type', 'general')}  |  Members: {len(members)}\n")

        if not members:
            console.print("[yellow]No members in this list[/yellow]")
            return

        table = Table(title=f"Members of \"{name}\"")
        table.add_column("ID", style="dim")
        table.add_column("Name", style="cyan")
        table.add_column("Email")
        table.add_column("Company")
        table.add_column("Location")
        table.add_column("Added", style="dim")

        for m in members:
            table.add_row(
                str(m['id']),
                m['name'],
                m.get('email', '-') or '-',
                m.get('company', '-') or '-',
                m.get('location', '-') or '-',
                m.get('list_added_at', '-')[:10] if m.get('list_added_at') else '-',
            )

        console.print(table)

    except ValueError as e:
        console.print(f"[red]{e}[/red]")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error showing list:[/red] {e}")
        raise typer.Exit(1)


@lists_app.command("delete")
def lists_delete(
    name: str = typer.Argument(..., help="List name"),
    yes: bool = typer.Option(False, "--yes", "-y", help="Skip confirmation"),
):
    """Delete a contact list."""
    db = get_db()
    try:
        lst = db.get_list(name)
        if not lst:
            console.print(f"[red]List not found:[/red] {name}")
            raise typer.Exit(1)

        if not yes:
            members = db.get_list_members(name)
            confirm = typer.confirm(f"Delete list \"{name}\" with {len(members)} members?")
            if not confirm:
                console.print("[yellow]Cancelled[/yellow]")
                raise typer.Exit()

        deleted = db.delete_list(name)
        if deleted:
            console.print(f"[green]List deleted:[/green] {name}")
        else:
            console.print(f"[red]Failed to delete list:[/red] {name}")
            raise typer.Exit(1)

    except sqlite3.Error as e:
        console.print(f"[red]Error deleting list:[/red] {e}")
        raise typer.Exit(1)


@lists_app.command("rename")
def lists_rename(
    name: str = typer.Argument(..., help="Current list name"),
    new_name: str = typer.Argument(..., help="New list name"),
):
    """Rename a contact list."""
    db = get_db()
    try:
        db.rename_list(name, new_name)
        console.print(f"[green]List renamed:[/green] \"{name}\" -> \"{new_name}\"")
    except ValueError as e:
        console.print(f"[red]{e}[/red]")
        raise typer.Exit(1)
    except sqlite3.IntegrityError:
        console.print(f"[red]List already exists:[/red] {new_name}")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error renaming list:[/red] {e}")
        raise typer.Exit(1)


@lists_app.command("update")
def lists_update(
    name: str = typer.Argument(..., help="List name"),
    description: Optional[str] = typer.Option(None, "--description", "-d", help="New description"),
    list_type: Optional[str] = typer.Option(None, "--type", "-t", help="New list type"),
):
    """Update a list's description or type."""
    if description is None and list_type is None:
        console.print("[red]Provide --description or --type (or both)[/red]")
        raise typer.Exit(1)

    db = get_db()
    try:
        db.update_list(name, description=description, list_type=list_type)
        console.print(f"[green]List updated:[/green] {name}")
    except ValueError as e:
        console.print(f"[red]{e}[/red]")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error updating list:[/red] {e}")
        raise typer.Exit(1)


@lists_app.command("copy")
def lists_copy(
    source: str = typer.Argument(..., help="Source list name"),
    dest: str = typer.Argument(..., help="Destination list name"),
):
    """Duplicate a list with all its members."""
    db = get_db()
    try:
        count = db.copy_list(source, dest)
        console.print(f"[green]List copied:[/green] \"{source}\" -> \"{dest}\" ({count} members)")
    except ValueError as e:
        console.print(f"[red]{e}[/red]")
        raise typer.Exit(1)
    except sqlite3.IntegrityError:
        console.print(f"[red]List already exists:[/red] {dest}")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error copying list:[/red] {e}")
        raise typer.Exit(1)


def _has_structured_filter(company, tag, account, category, relationship) -> bool:
    return any([company, tag, account, category, relationship])


@lists_app.command("add")
def lists_add(
    name: str = typer.Argument(..., help="List name"),
    contact_id: Optional[int] = typer.Option(None, "--contact-id", "-c", help="Single contact ID to add"),
    company: Optional[str] = typer.Option(None, "--company", help="Match contacts at this company"),
    tag: Optional[List[str]] = typer.Option(None, "--tag", help="Match contacts with this tag (repeatable, AND logic)"),
    account: Optional[str] = typer.Option(None, "--account", help="Match contacts in this account: consulting, personal, both"),
    category: Optional[str] = typer.Option(None, "--category", help="Match contacts in this category"),
    relationship: Optional[str] = typer.Option(None, "--relationship", help="Match contacts with this relationship"),
    where: Optional[str] = typer.Option(None, "--where", hidden=True, help="EXPERT: raw SQL WHERE clause (requires --yes)"),
    yes: bool = typer.Option(False, "--yes", "-y", help="Confirm the expert --where clause"),
):
    """Add contacts to a list by ID or by structured filters.

    Use --company / --tag / --account / --category / --relationship to select
    contacts safely. --where is a hidden expert escape hatch for a raw SQL WHERE
    clause and requires --yes.
    """
    has_filter = _has_structured_filter(company, tag, account, category, relationship)
    if not contact_id and not has_filter and not where:
        console.print("[red]Provide --contact-id, a structured filter (--company/--tag/--account/--category/--relationship), or --where[/red]")
        raise typer.Exit(1)

    db = get_db()
    try:
        if contact_id:
            added = db.add_list_member(name, contact_id)
            if added:
                console.print(f"[green]Added contact #{contact_id} to \"{name}\"[/green]")
            else:
                console.print(f"[yellow]Contact #{contact_id} is already in \"{name}\"[/yellow]")
        elif has_filter:
            count = db.add_list_members_by_filters(
                name, company=company, tag=tag, account=account,
                category=category, relationship=relationship,
            )
            console.print(f"[green]Added {count} contacts to \"{name}\"[/green]")
        elif where:
            clause = db.validate_where_clause(where)
            if not yes:
                matched = db.count_contacts_by_query(clause)
                console.print(f"[yellow]--where would match {matched} contact(s). Re-run with --yes to apply.[/yellow]")
                raise typer.Exit(1)
            count = db.add_list_members_by_query(name, clause)
            console.print(f"[green]Added {count} contacts to \"{name}\"[/green]")

    except ValueError as e:
        console.print(f"[red]{e}[/red]")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error adding to list:[/red] {e}")
        raise typer.Exit(1)


@lists_app.command("remove")
def lists_remove(
    name: str = typer.Argument(..., help="List name"),
    contact_id: Optional[int] = typer.Option(None, "--contact-id", "-c", help="Contact ID to remove"),
    company: Optional[str] = typer.Option(None, "--company", help="Match contacts at this company"),
    tag: Optional[List[str]] = typer.Option(None, "--tag", help="Match contacts with this tag (repeatable, AND logic)"),
    account: Optional[str] = typer.Option(None, "--account", help="Match contacts in this account: consulting, personal, both"),
    category: Optional[str] = typer.Option(None, "--category", help="Match contacts in this category"),
    relationship: Optional[str] = typer.Option(None, "--relationship", help="Match contacts with this relationship"),
    where: Optional[str] = typer.Option(None, "--where", hidden=True, help="EXPERT: raw SQL WHERE clause (requires --yes)"),
    yes: bool = typer.Option(False, "--yes", "-y", help="Confirm a bulk removal (required for filters/--where)"),
):
    """Remove contacts from a list by ID or by structured filters.

    Bulk removals (structured filters or --where) show the matched count and
    require --yes to actually remove.
    """
    has_filter = _has_structured_filter(company, tag, account, category, relationship)
    if not contact_id and not has_filter and not where:
        console.print("[red]Provide --contact-id, a structured filter (--company/--tag/--account/--category/--relationship), or --where[/red]")
        raise typer.Exit(1)

    db = get_db()
    try:
        if contact_id:
            removed = db.remove_list_member(name, contact_id)
            if removed:
                console.print(f"[green]Removed contact #{contact_id} from \"{name}\"[/green]")
            else:
                console.print(f"[yellow]Contact #{contact_id} was not in \"{name}\"[/yellow]")
        elif has_filter:
            matched = db.count_contacts_by_filters(
                company=company, tag=tag, account=account,
                category=category, relationship=relationship,
            )
            if not yes:
                console.print(f"[yellow]Filter matches {matched} contact(s). Re-run with --yes to remove them from \"{name}\".[/yellow]")
                raise typer.Exit(1)
            count = db.remove_list_members_by_filters(
                name, company=company, tag=tag, account=account,
                category=category, relationship=relationship,
            )
            console.print(f"[green]Removed {count} contacts from \"{name}\"[/green]")
        elif where:
            clause = db.validate_where_clause(where)
            matched = db.count_contacts_by_query(clause)
            if not yes:
                console.print(f"[yellow]--where matches {matched} contact(s). Re-run with --yes to remove them from \"{name}\".[/yellow]")
                raise typer.Exit(1)
            count = db.remove_list_members_by_query(name, clause)
            console.print(f"[green]Removed {count} contacts from \"{name}\"[/green]")

    except ValueError as e:
        console.print(f"[red]{e}[/red]")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error removing from list:[/red] {e}")
        raise typer.Exit(1)


@lists_app.command("export")
def lists_export(
    name: str = typer.Argument(..., help="List name"),
    format: str = typer.Option("json", "--format", "-f", help="Export format: json or csv"),
):
    """Export list members as JSON or CSV."""
    db = get_db()
    try:
        output = db.export_list(name, format=format)
        if not output:
            console.print(f"[yellow]List \"{name}\" is empty[/yellow]")
            return
        console.print(output)

    except ValueError as e:
        console.print(f"[red]{e}[/red]")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error exporting list:[/red] {e}")
        raise typer.Exit(1)


# ==========================================
# LIBRARY COMMANDS
# ==========================================


@library_app.command("add")
def library_add(
    path: str = typer.Argument(..., help="Directory path to register"),
    label: str = typer.Option(..., "--label", "-l", help="Unique label for this library"),
    category: str = typer.Option("business", "--category", "-c", help="Category: business, personal, other"),
    owner: Optional[str] = typer.Option(None, "--owner", help="Owner name"),
    no_recursive: bool = typer.Option(False, "--no-recursive", help="Do not scan subdirectories"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Register a document library folder.

    Example: cc-vault library add "D:\\Docs\\Corporate" --label Corporate --category business
    """
    db = get_db()
    resolved = Path(path).resolve()
    if not resolved.is_dir():
        console.print(f"[red]Error:[/red] Directory not found: {resolved}")
        raise typer.Exit(1)

    try:
        lib = db.add_library(
            path=str(resolved), label=label, category=category,
            owner=owner, recursive=not no_recursive,
        )
        if json_output:
            print(json.dumps(lib, indent=2))
        else:
            console.print(f"[green]Library added:[/green] {label} -> {resolved}")
    except sqlite3.IntegrityError as e:
        console.print(f"[red]Error:[/red] Library already exists or duplicate label/path: {e}")
        raise typer.Exit(1)


@library_app.command("list")
def library_list(
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """List all registered libraries."""
    db = get_db()
    libs = db.list_libraries()

    if json_output:
        print(json.dumps(libs, indent=2))
        return

    if not libs:
        console.print("[yellow]No libraries registered. Use 'cc-vault library add' to add one.[/yellow]")
        return

    table = Table(title="Document Libraries")
    table.add_column("ID", style="dim", justify="right")
    table.add_column("Label", style="cyan bold")
    table.add_column("Category")
    table.add_column("Path")
    table.add_column("Last Scanned")
    table.add_column("Enabled")

    for lib in libs:
        table.add_row(
            str(lib['id']),
            lib['label'],
            lib['category'],
            lib['path'],
            lib.get('last_scanned') or '-',
            'Yes' if lib.get('enabled') else 'No',
        )

    console.print(table)


@library_app.command("show")
def library_show(
    label: str = typer.Argument(..., help="Library label"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Show details for a library."""
    db = get_db()
    lib = db.get_library(label)
    if not lib:
        console.print(f"[red]Error:[/red] Library '{label}' not found")
        raise typer.Exit(1)

    stats = db.get_catalog_stats(lib['id'])

    if json_output:
        lib['stats'] = stats
        print(json.dumps(lib, indent=2))
        return

    panel_lines = [
        f"[bold]Label:[/bold] {lib['label']}",
        f"[bold]Path:[/bold] {lib['path']}",
        f"[bold]Category:[/bold] {lib['category']}",
        f"[bold]Owner:[/bold] {lib.get('owner') or '-'}",
        f"[bold]Recursive:[/bold] {'Yes' if lib.get('recursive') else 'No'}",
        f"[bold]Last Scanned:[/bold] {lib.get('last_scanned') or 'Never'}",
        "",
        f"[bold]Files:[/bold] {stats['total']}",
        f"  Summarized: {stats['summarized']}",
        f"  Pending: {stats['pending']}",
        f"  Skipped: {stats['skipped']}",
        f"  Errors: {stats['errors']}",
        f"  Missing: {stats['missing']}",
    ]
    console.print(Panel("\n".join(panel_lines), title=f"Library: {lib['label']}"))


@library_app.command("delete")
def library_delete(
    label: str = typer.Argument(..., help="Library label to delete"),
    confirm: bool = typer.Option(False, "--yes", "-y", help="Skip confirmation"),
):
    """Delete a library and all its catalog entries."""
    db = get_db()
    lib = db.get_library(label)
    if not lib:
        console.print(f"[red]Error:[/red] Library '{label}' not found")
        raise typer.Exit(1)

    if not confirm:
        stats = db.get_catalog_stats(lib['id'])
        console.print(f"[yellow]This will delete library '{label}' and {stats['total']} catalog entries.[/yellow]")
        if not typer.confirm("Are you sure?"):
            raise typer.Exit(0)

    db.delete_library(label)
    console.print(f"[green]Library '{label}' deleted.[/green]")


# ==========================================
# CATALOG COMMANDS
# ==========================================


@catalog_app.command("scan")
def catalog_scan(
    library: str = typer.Option(..., "--library", "-l", help="Library label to scan"),
    stream: bool = typer.Option(False, "--stream", help="Output JSON progress lines for UI"),
):
    """Scan a library directory and catalog files.

    Example: cc-vault catalog scan --library Corporate --stream
    """
    try:
        try:
            from .catalog import CatalogScanner
        except ImportError:
            from catalog import CatalogScanner

        scanner = CatalogScanner()
        result = scanner.scan_library_by_label(library, stream=stream)

        if not stream:
            console.print(f"\n[green]Scan complete:[/green]")
            console.print(f"  New: {result.get('new', 0)}")
            console.print(f"  Updated: {result.get('updated', 0)}")
            console.print(f"  Skipped: {result.get('skipped', 0)}")
            console.print(f"  Missing: {result.get('missing', 0)}")
            console.print(f"  Errors: {result.get('errors', 0)}")

    except ValueError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@catalog_app.command("summarize")
def catalog_summarize(
    library: Optional[str] = typer.Option(None, "--library", "-l", help="Library label (all if omitted)"),
    batch: int = typer.Option(10, "--batch", "-b", help="Batch size"),
    stream: bool = typer.Option(False, "--stream", help="Output JSON progress lines for UI"),
    dry_run: bool = typer.Option(False, "--dry-run", help="Show what would be summarized"),
):
    """Generate AI summaries for pending catalog entries.

    Example: cc-vault catalog summarize --library Corporate --stream
    """
    try:
        try:
            from .catalog import CatalogScanner
        except ImportError:
            from catalog import CatalogScanner

        scanner = CatalogScanner()
        library_id = None
        if library:
            db = get_db()
            lib = db.get_library(library)
            if not lib:
                console.print(f"[red]Error:[/red] Library '{library}' not found")
                raise typer.Exit(1)
            library_id = lib['id']

        result = scanner.summarize_entries(
            library_id=library_id, batch_size=batch,
            stream=stream, dry_run=dry_run,
        )

        if not stream:
            if dry_run:
                console.print(f"[yellow]Dry run:[/yellow] {result.get('pending', 0)} entries would be summarized")
            else:
                console.print(f"\n[green]Summarize complete:[/green]")
                console.print(f"  Summarized: {result.get('summarized', 0)}")
                console.print(f"  Deduped: {result.get('deduped', 0)}")
                console.print(f"  Errors: {result.get('errors', 0)}")

    except ValueError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@catalog_app.command("list")
def catalog_list(
    library: Optional[str] = typer.Option(None, "--library", "-l", help="Filter by library label"),
    ext: Optional[str] = typer.Option(None, "--ext", "-e", help="Filter by extension (e.g. .pdf)"),
    dept: Optional[str] = typer.Option(None, "--dept", "-d", help="Filter by department"),
    status: Optional[str] = typer.Option(None, "--status", "-s", help="Filter by status"),
    n: int = typer.Option(50, "-n", "--count", help="Number of results"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """List catalog entries with optional filters.

    Example: cc-vault catalog list --library Corporate --ext .pdf -n 20
    """
    db = get_db()
    library_id = None
    if library:
        lib = db.get_library(library)
        if not lib:
            console.print(f"[red]Error:[/red] Library '{library}' not found")
            raise typer.Exit(1)
        library_id = lib['id']

    entries = db.list_catalog_entries(
        library_id=library_id, ext=ext, department=dept, status=status, limit=n,
    )

    if json_output:
        print(json.dumps(entries, indent=2))
        return

    if not entries:
        console.print("[yellow]No catalog entries found.[/yellow]")
        return

    table = Table(title=f"Catalog Entries ({len(entries)} shown)")
    table.add_column("ID", style="dim", justify="right")
    table.add_column("File", style="cyan", max_width=40)
    table.add_column("Ext", style="dim")
    table.add_column("Dept")
    table.add_column("Status")
    table.add_column("Title", max_width=40)

    for e in entries:
        status_style = {
            'summarized': 'green', 'pending': 'yellow',
            'error': 'red', 'skipped': 'dim', 'missing': 'red dim',
        }.get(e['status'], '')
        table.add_row(
            str(e['id']),
            e['file_name'],
            e['file_ext'],
            e.get('department') or '-',
            f"[{status_style}]{e['status']}[/{status_style}]",
            (e.get('title') or '-')[:40],
        )

    console.print(table)


@catalog_app.command("show")
def catalog_show(
    entry_id: int = typer.Argument(..., help="Catalog entry ID"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Show details for a catalog entry."""
    db = get_db()
    conn = db.get_db()
    try:
        row = conn.execute("SELECT * FROM catalog_entries WHERE id = ?", (entry_id,)).fetchone()
    finally:
        conn.close()

    if not row:
        console.print(f"[red]Error:[/red] Entry #{entry_id} not found")
        raise typer.Exit(1)

    entry = dict(row)
    if json_output:
        print(json.dumps(entry, indent=2))
        return

    lines = [
        f"[bold]File:[/bold] {entry['file_name']}",
        f"[bold]Path:[/bold] {entry['file_path']}",
        f"[bold]Extension:[/bold] {entry['file_ext']}",
        f"[bold]Size:[/bold] {entry.get('file_size', 0):,} bytes",
        f"[bold]Department:[/bold] {entry.get('department') or '-'}",
        f"[bold]Status:[/bold] {entry['status']}",
    ]
    if entry.get('title'):
        lines.append(f"\n[bold]Title:[/bold] {entry['title']}")
    if entry.get('summary'):
        lines.append(f"[bold]Summary:[/bold] {entry['summary']}")
    if entry.get('tags'):
        lines.append(f"[bold]Tags:[/bold] {entry['tags']}")
    if entry.get('error_message'):
        lines.append(f"[red]Error:[/red] {entry['error_message']}")

    console.print(Panel("\n".join(lines), title=f"Catalog Entry #{entry_id}"))


@catalog_app.command("search")
def catalog_search(
    query: str = typer.Argument(..., help="Search query"),
    n: int = typer.Option(20, "-n", "--count", help="Number of results"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Full-text search across catalog entries.

    Example: cc-vault catalog search "investor update"
    """
    db = get_db()
    results = db.search_catalog_fts(query, limit=n)

    if json_output:
        print(json.dumps(results, indent=2))
        return

    if not results:
        console.print(f"[yellow]No results for '{query}'[/yellow]")
        return

    table = Table(title=f"Search: '{query}' ({len(results)} results)")
    table.add_column("ID", style="dim", justify="right")
    table.add_column("File", style="cyan", max_width=40)
    table.add_column("Dept")
    table.add_column("Title", max_width=50)

    for e in results:
        table.add_row(
            str(e['id']),
            e['file_name'],
            e.get('department') or '-',
            (e.get('title') or '-')[:50],
        )

    console.print(table)


@catalog_app.command("stats")
def catalog_stats(
    library: Optional[str] = typer.Option(None, "--library", "-l", help="Filter by library label"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Show catalog statistics."""
    db = get_db()
    library_id = None
    if library:
        lib = db.get_library(library)
        if not lib:
            console.print(f"[red]Error:[/red] Library '{library}' not found")
            raise typer.Exit(1)
        library_id = lib['id']

    stats = db.get_catalog_stats(library_id)

    if json_output:
        print(json.dumps(stats, indent=2))
        return

    title = f"Library: {library}" if library else "All Libraries"
    lines = [
        f"  Total files:  {stats['total']}",
        f"  Summarized:   {stats['summarized']}",
        f"  Pending:      {stats['pending']}",
        f"  Skipped:      {stats['skipped']}",
        f"  Errors:       {stats['errors']}",
        f"  Missing:      {stats['missing']}",
    ]
    console.print(Panel("\n".join(lines), title=title))


def _check_search_entity_mistake():
    """Detect 'cc-vault search <entity-type> <query>' and suggest the correct command."""
    args = sys.argv[1:]
    if len(args) >= 3 and args[0] == "search" and args[1].lower() in ENTITY_TYPES:
        entity = args[1]
        rest = " ".join(args[2:])
        console.print(
            f"[red]ERROR:[/red] 'cc-vault search' takes a single QUERY argument.\n"
            f"\n"
            f"  Did you mean: [green]cc-vault {entity} search \"{rest}\"[/green]\n"
        )
        raise SystemExit(1)


if __name__ == "__main__":
    _check_search_entity_mistake()
    app()
