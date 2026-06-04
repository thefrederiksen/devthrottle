"""CLI for cc-photos - photo organization tool."""

import os
import string
from collections import defaultdict
from pathlib import Path
from typing import Dict, List, Optional

import typer
from rich.console import Console
from rich.progress import Progress, SpinnerColumn, TextColumn, BarColumn, TaskProgressColumn
from rich.table import Table

try:
    from . import __version__
    from . import database as db
    from .scanner import scan_source, find_images, IMAGE_EXTENSIONS
    from .duplicates import find_duplicate_groups, get_duplicate_stats, delete_duplicates, format_file_size
    from .analyzer import analyze_photos
except ImportError:
    from cc_photos import __version__
    from cc_photos import database as db
    from cc_photos.scanner import scan_source, find_images, IMAGE_EXTENSIONS
    from cc_photos.duplicates import find_duplicate_groups, get_duplicate_stats, delete_duplicates, format_file_size
    from cc_photos.analyzer import analyze_photos


app = typer.Typer(
    name="cc-photos",
    help="Photo organization tool: scan, categorize, detect duplicates and screenshots, AI descriptions.",
    add_completion=False,
)
console = Console()


def version_callback(value: bool):
    if value:
        console.print(f"cc-photos version {__version__}")
        raise typer.Exit()


@app.callback()
def main(
    version: bool = typer.Option(
        False, "--version", "-v",
        callback=version_callback,
        is_eager=True,
        help="Show version and exit",
    ),
):
    """Photo organization tool."""
    # Initialize database on startup
    db.init_db()


# ============================================================================
# Source management commands
# ============================================================================

source_app = typer.Typer(help="Manage photo sources")
app.add_typer(source_app, name="source")


@source_app.command("add")
def source_add(
    path: str = typer.Argument(..., help="Directory path to add as source"),
    category: str = typer.Option(
        "other",
        "--category", "-c",
        help="Category: private, work, other",
    ),
    label: str = typer.Option(
        ...,
        "--label", "-l",
        help="Label for this source",
    ),
    priority: int = typer.Option(
        10,
        "--priority", "-p",
        help="Priority (lower = higher priority for keeping duplicates)",
    ),
):
    """Add a photo source directory."""
    # Validate category
    if category not in ("private", "work", "other"):
        console.print("[red]Error:[/red] Invalid category. Must be: private, work, other")
        raise typer.Exit(1)

    # Validate path exists
    source_path = Path(path)
    if not source_path.exists():
        console.print(f"[red]Error:[/red] Path does not exist: {path}")
        raise typer.Exit(1)

    if not source_path.is_dir():
        console.print(f"[red]Error:[/red] Path is not a directory: {path}")
        raise typer.Exit(1)

    # Check if label already exists
    existing = db.get_source(label)
    if existing:
        console.print(f"[red]Error:[/red] Source with label '{label}' already exists")
        raise typer.Exit(1)

    source_id = db.add_source(
        path=str(source_path.resolve()),
        label=label,
        category=category,
        priority=priority,
    )

    console.print(f"[green]Added source:[/green] {label} (ID: {source_id})")
    console.print(f"  Path: {source_path.resolve()}")
    console.print(f"  Category: {category}")
    console.print(f"  Priority: {priority}")


@source_app.command("list")
def source_list():
    """List all photo sources."""
    sources = db.list_sources()

    if not sources:
        console.print("No sources configured. Add one with: cc-photos source add <path> -l <label>")
        return

    table = Table(title="Photo Sources")
    table.add_column("ID", style="dim", justify="right")
    table.add_column("Priority", style="cyan", justify="right")
    table.add_column("Label", style="green")
    table.add_column("Category")
    table.add_column("Path")
    table.add_column("Status")

    for source in sorted(sources, key=lambda s: s.get('priority', 10)):
        path = Path(source['path'])
        status = "[green]OK[/green]" if path.exists() else "[red]Missing[/red]"
        table.add_row(
            str(source['id']),
            str(source.get('priority', 10)),
            source['label'],
            source['category'],
            source['path'],
            status,
        )

    console.print(table)


@source_app.command("remove")
def source_remove(
    label: str = typer.Argument(..., help="Label of source to remove"),
):
    """Remove a photo source."""
    if db.remove_source(label):
        console.print(f"[green]Removed source:[/green] {label}")
    else:
        console.print(f"[red]Error:[/red] Source not found: {label}")
        raise typer.Exit(1)


# ============================================================================
# Exclusion management commands
# ============================================================================

exclude_app = typer.Typer(help="Manage excluded paths (paths to skip during scanning)")
app.add_typer(exclude_app, name="exclude")


@exclude_app.command("add")
def exclude_add(
    path: str = typer.Argument(..., help="Path to exclude from scanning"),
    reason: Optional[str] = typer.Option(
        None,
        "--reason", "-r",
        help="Reason for exclusion",
    ),
):
    """Add a path to the exclusion list."""
    exc_path = Path(path)
    if not exc_path.exists():
        console.print(f"[yellow]Warning:[/yellow] Path does not exist: {path}")
        console.print("Adding anyway in case it will exist later.")

    db.add_exclusion(str(exc_path.resolve()), reason)
    console.print(f"[green]Excluded:[/green] {exc_path.resolve()}")
    if reason:
        console.print(f"  Reason: {reason}")


@exclude_app.command("list")
def exclude_list():
    """List all excluded paths."""
    exclusions = db.list_exclusions()

    if not exclusions:
        console.print("No exclusions configured.")
        console.print("Add one with: cc-photos exclude add <path>")
        return

    table = Table(title="Excluded Paths")
    table.add_column("Path")
    table.add_column("Reason")
    table.add_column("Added", style="dim")

    for exc in exclusions:
        table.add_row(
            exc['path'],
            exc.get('reason') or '-',
            exc.get('created_at', '')[:10] if exc.get('created_at') else '-',
        )

    console.print(table)


@exclude_app.command("remove")
def exclude_remove(
    path: str = typer.Argument(..., help="Path to remove from exclusion list"),
):
    """Remove a path from the exclusion list."""
    if db.remove_exclusion(path):
        console.print(f"[green]Removed exclusion:[/green] {path}")
    else:
        console.print(f"[red]Error:[/red] Exclusion not found: {path}")
        raise typer.Exit(1)


@exclude_app.command("defaults")
def exclude_defaults():
    """Add default system exclusions (Windows, Program Files, etc.)."""
    count = db.add_default_exclusions()
    console.print(f"[green]Added {count} default exclusions[/green]")

    # Show what was added
    exclusions = db.list_exclusions()
    for exc in exclusions:
        if exc.get('reason') == 'Default system exclusion':
            console.print(f"  - {exc['path']}")


# ============================================================================
# Helper functions
# ============================================================================

def get_available_drives() -> List[str]:
    """Get list of available drive letters on Windows."""
    drives = []
    if os.name == 'nt':
        import string
        for letter in string.ascii_uppercase:
            drive = f"{letter}:\\"
            if os.path.exists(drive):
                drives.append(drive)
    else:
        # Unix - just use root
        drives = ['/']
    return drives


# ============================================================================
# Discovery and initialization commands
# ============================================================================

@app.command("discover")
def discover(
    path: str = typer.Argument(..., help="Path to scan for photos (e.g., D:\\ or C:\\Users)"),
    top: int = typer.Option(
        20,
        "--top", "-t",
        help="Show top N directories by photo count",
    ),
    min_photos: int = typer.Option(
        5,
        "--min", "-m",
        help="Minimum photos to include a directory",
    ),
):
    """Discover where photos are located without adding to database.

    This is a read-only reconnaissance command that shows you where photos
    are located so you can decide which directories to add as sources.
    """
    scan_path = Path(path)
    if not scan_path.exists():
        console.print(f"[red]Error:[/red] Path does not exist: {path}")
        raise typer.Exit(1)

    if not scan_path.is_dir():
        console.print(f"[red]Error:[/red] Path is not a directory: {path}")
        raise typer.Exit(1)

    console.print(f"[cyan]Discovering photos in:[/cyan] {scan_path}")
    console.print("This may take a while for large drives...\n")

    # Count photos by directory
    dir_counts: Dict[str, int] = defaultdict(int)
    dir_sizes: Dict[str, int] = defaultdict(int)
    total_photos = 0
    total_size = 0
    errors = 0

    # Use simple counter instead of spinner (Unicode spinners fail on Windows)
    photo_count = 0
    last_print = 0

    for root, dirs, files in os.walk(scan_path):
            # Skip hidden directories and common non-photo directories
        dirs[:] = [d for d in dirs if not d.startswith('.') and d.lower() not in (
            'node_modules', '__pycache__', '.git', 'venv', '.venv',
            'appdata', 'programdata', '$recycle.bin', 'windows'
        )]

        for filename in files:
            try:
                file_path = Path(root) / filename
                if file_path.suffix.lower() in IMAGE_EXTENSIONS:
                    dir_path = str(file_path.parent)
                    file_size = file_path.stat().st_size
                    dir_counts[dir_path] += 1
                    dir_sizes[dir_path] += file_size
                    total_photos += 1
                    total_size += file_size

                    # Print progress every 1000 photos
                    if total_photos - last_print >= 1000:
                        console.print(f"  Found {total_photos:,} photos...", end="\r")
                        last_print = total_photos
            except (OSError, PermissionError):
                errors += 1

    if total_photos == 0:
        console.print("[yellow]No photos found in this location.[/yellow]")
        return

    # Sort directories by photo count
    sorted_dirs = sorted(dir_counts.items(), key=lambda x: x[1], reverse=True)

    # Filter by minimum photos
    filtered_dirs = [(d, c) for d, c in sorted_dirs if c >= min_photos]

    console.print(f"\n[green]Discovery Complete[/green]\n")
    console.print(f"Total photos found: {total_photos:,}")
    console.print(f"Total size: {format_file_size(total_size)}")
    console.print(f"Directories with photos: {len(dir_counts):,}")
    if errors:
        console.print(f"[yellow]Skipped (permission errors): {errors}[/yellow]")

    if filtered_dirs:
        console.print(f"\n[cyan]Top {min(top, len(filtered_dirs))} directories (min {min_photos} photos):[/cyan]\n")

        table = Table()
        table.add_column("#", style="dim", justify="right")
        table.add_column("Photos", justify="right", style="green")
        table.add_column("Size", justify="right")
        table.add_column("Directory")

        for i, (dir_path, count) in enumerate(filtered_dirs[:top], 1):
            table.add_row(
                str(i),
                f"{count:,}",
                format_file_size(dir_sizes[dir_path]),
                dir_path,
            )

        console.print(table)

        console.print("\n[dim]To add a directory as a source:[/dim]")
        console.print("  cc-photos init <path> --category <private|work|other>")
        console.print("\n[dim]Or use source add for more control:[/dim]")
        console.print("  cc-photos source add <path> -l <label> -c <category> -p <priority>")




# ============================================================================
# Scanning commands
# ============================================================================

@app.command("scan")
def scan(
    drives: Optional[List[str]] = typer.Argument(
        None,
        help="Specific drives to scan (e.g., D: E:). If omitted, scans all drives.",
    ),
    source: Optional[str] = typer.Option(
        None,
        "--source", "-s",
        help="Scan only this existing source (by label)",
    ),
    category: str = typer.Option(
        "other",
        "--category", "-c",
        help="Default category for new photos: private, work, other",
    ),
    skip_defaults: bool = typer.Option(
        False,
        "--skip-defaults",
        help="Skip adding default exclusions on first run",
    ),
):
    """Scan drives for photos.

    Scans all available drives (or specified drives) for photos, skipping
    excluded paths. Creates one source per drive automatically.

    First run adds default exclusions (Windows, Program Files, etc.)
    Use 'cc-photos exclude add <path>' to add more exclusions.

    Examples:
        cc-photos scan                    # Scan all drives
        cc-photos scan D: E:              # Scan specific drives only
        cc-photos scan --source "D Drive" # Rescan existing source only
        cc-photos scan --category private # Mark new photos as private
    """
    # Validate category
    if category not in ("private", "work", "other"):
        console.print("[red]Error:[/red] Invalid category. Must be: private, work, other")
        raise typer.Exit(1)

    # Mode 1: Scan specific existing source
    if source:
        sources = db.list_sources()
        sources = [s for s in sources if s['label'] == source]
        if not sources:
            console.print(f"[red]Error:[/red] Source not found: {source}")
            raise typer.Exit(1)

        src = sources[0]
        source_path = Path(src['path'])
        if not source_path.exists():
            console.print(f"[red]Error:[/red] Source path missing: {src['path']}")
            raise typer.Exit(1)

        console.print(f"[cyan]Scanning source:[/cyan] {src['label']}")

        with Progress(
            TextColumn("[progress.description]{task.description}"),
            BarColumn(),
            TaskProgressColumn(),
            console=console,
        ) as progress:
            task = progress.add_task(f"  {src['label']}", total=None)

            result = scan_source(
                source=src,
                progress=progress,
                task_id=task,
            )

            progress.update(task, completed=True)

        console.print(f"\n[green]{src['label']}:[/green]")
        console.print(f"  Found: {result.files_found:,}")
        console.print(f"  Added: {result.files_added:,}")
        console.print(f"  Updated: {result.files_updated:,}")
        console.print(f"  Removed: {result.files_removed:,}")

        if result.errors:
            console.print(f"  [red]Errors: {len(result.errors)}[/red]")
            for err in result.errors[:5]:
                console.print(f"    - {err}")
        return

    # Mode 2: Scan all drives (or specified drives)
    # Add default exclusions on first run
    exclusions = db.list_exclusions()
    if not exclusions and not skip_defaults:
        console.print("[cyan]First run - adding default exclusions...[/cyan]")
        count = db.add_default_exclusions()
        console.print(f"  Added {count} default exclusions (Windows, Program Files, etc.)")
        console.print("  Use 'cc-photos exclude list' to view, 'cc-photos exclude add' to add more\n")

    # Get drives to scan
    if drives:
        # Normalize drive letters
        drives_to_scan = []
        for d in drives:
            drive = d.strip().upper()
            if not drive.endswith(':'):
                drive += ':'
            drive_path = drive + '\\'
            if os.path.exists(drive_path):
                drives_to_scan.append(drive_path)
            else:
                console.print(f"[yellow]Warning:[/yellow] Drive {drive} not found, skipping")
    else:
        drives_to_scan = get_available_drives()

    if not drives_to_scan:
        console.print("[red]Error:[/red] No drives available to scan")
        raise typer.Exit(1)

    console.print(f"[cyan]Scanning {len(drives_to_scan)} drive(s):[/cyan] {', '.join(drives_to_scan)}")
    console.print()

    total_found = 0
    total_added = 0
    total_updated = 0
    total_removed = 0
    total_errors = 0

    for drive_path in drives_to_scan:
        drive_letter = drive_path[0]
        label = f"{drive_letter} Drive"

        # Check if already a source
        existing = db.get_source(label)
        if existing:
            source_id = existing['id']
            source_category = existing['category']
        else:
            # Add as new source
            source_id = db.add_source(
                path=drive_path,
                label=label,
                category=category,
                priority=10,
            )
            source_category = category
            console.print(f"[green]{label}:[/green] Added as source")

        # Mark drive as scanned
        db.set_drive_scanned(drive_letter)

        # Scan the source
        source_dict = {
            'id': source_id,
            'path': drive_path,
            'label': label,
            'category': source_category,
            'priority': existing.get('priority', 10) if existing else 10,
        }

        console.print(f"  Scanning {drive_path}...")

        with Progress(
            TextColumn("[progress.description]{task.description}"),
            BarColumn(),
            TaskProgressColumn(),
            console=console,
        ) as progress:
            task = progress.add_task(f"  {label}", total=None)

            result = scan_source(
                source=source_dict,
                progress=progress,
                task_id=task,
            )

            progress.update(task, completed=True)

        console.print(f"    Found: {result.files_found:,}, Added: {result.files_added:,}, Updated: {result.files_updated:,}")

        total_found += result.files_found
        total_added += result.files_added
        total_updated += result.files_updated
        total_removed += result.files_removed
        total_errors += len(result.errors)

        if result.errors:
            console.print(f"    [yellow]Errors: {len(result.errors)}[/yellow]")

    # Print summary
    console.print(f"\n[green]Scan Complete[/green]")
    console.print(f"  Total photos found: {total_found:,}")
    console.print(f"  Added: {total_added:,}")
    console.print(f"  Updated: {total_updated:,}")
    console.print(f"  Removed: {total_removed:,}")
    if total_errors:
        console.print(f"  [yellow]Errors: {total_errors}[/yellow]")

    # Show next steps on first run
    if total_added > 0:
        console.print("\n[dim]Next steps:[/dim]")
        console.print("  cc-photos stats           # View statistics")
        console.print("  cc-photos dupes           # Find duplicates")
        console.print("  cc-photos analyze -n 10   # AI analyze 10 photos")


# ============================================================================
# Duplicate commands
# ============================================================================

@app.command("dupes")
def dupes(
    cleanup: bool = typer.Option(
        False,
        "--cleanup",
        help="Delete duplicate files (keeps highest priority)",
    ),
    review: bool = typer.Option(
        False,
        "--review",
        help="Interactive review of each duplicate group",
    ),
    dry_run: bool = typer.Option(
        False,
        "--dry-run",
        help="Show what would be deleted without deleting",
    ),
    yes: bool = typer.Option(
        False,
        "--yes", "-y",
        help="Skip confirmation prompt",
    ),
):
    """Find and manage duplicate images."""
    groups = find_duplicate_groups()

    if not groups:
        console.print("[green]No duplicates found![/green]")
        return

    stats = get_duplicate_stats(groups)
    console.print(f"\n[yellow]Found {stats['groups']} duplicate groups[/yellow]")
    console.print(f"[yellow]{stats['duplicate_files']} duplicate files ({format_file_size(stats['wasted_bytes'])} wasted)[/yellow]\n")

    if review:
        # Interactive review
        for i, group in enumerate(groups, 1):
            console.print(f"\n[cyan]Group {i}/{len(groups)}:[/cyan] {group.keep.get('file_name', 'unknown')}")
            console.print(f"  Hash: {group.hash[:16]}...")
            console.print(f"  Size: {format_file_size(group.keep.get('file_size') or 0)} x {len(group.photos)} copies")
            console.print(f"  Wasted: {format_file_size(group.wasted_bytes)}")
            console.print()

            for photo in group.photos:
                if photo['id'] == group.keep['id']:
                    console.print(f"  [green][KEEP][/green] {photo['file_path']}")
                else:
                    console.print(f"  [red][DEL][/red]  {photo['file_path']}")

            if not yes:
                action = typer.prompt(
                    "Action? [d]elete duplicates, [s]kip, [q]uit",
                    default="s",
                )
                if action.lower() == "q":
                    break
                elif action.lower() == "d":
                    deleted, freed, errors = delete_duplicates([group], dry_run=dry_run)
                    if dry_run:
                        console.print(f"  [yellow]Would delete {deleted} files ({format_file_size(freed)})[/yellow]")
                    else:
                        console.print(f"  [green]Deleted {deleted} files ({format_file_size(freed)})[/green]")

    elif cleanup:
        # Batch cleanup
        for group in groups:
            console.print(f"\n[cyan]Group:[/cyan] {group.keep.get('file_name', 'unknown')} ({format_file_size(group.wasted_bytes)} wasted)")
            for photo in group.photos:
                if photo['id'] == group.keep['id']:
                    console.print(f"  [green][KEEP][/green] {photo['file_path']}")
                else:
                    console.print(f"  [red][DEL][/red]  {photo['file_path']}")

        if not yes:
            confirm = typer.confirm(
                f"\nDelete {stats['duplicate_files']} files ({format_file_size(stats['wasted_bytes'])})?"
            )
            if not confirm:
                console.print("Cancelled.")
                raise typer.Exit()

        deleted, freed, errors = delete_duplicates(groups, dry_run=dry_run)
        if dry_run:
            console.print(f"\n[yellow]Would delete {deleted} files ({format_file_size(freed)})[/yellow]")
        else:
            console.print(f"\n[green]Deleted {deleted} files ({format_file_size(freed)})[/green]")

        if errors:
            console.print(f"[red]Errors: {len(errors)}[/red]")
            for err in errors[:5]:
                console.print(f"  - {err}")

    else:
        # Just list
        for group in groups:
            console.print(f"\n[cyan]Group:[/cyan] {group.keep.get('file_name', 'unknown')}")
            console.print(f"  {len(group.photos)} copies, {format_file_size(group.wasted_bytes)} wasted")
            for photo in group.photos:
                marker = "[green][KEEP][/green]" if photo['id'] == group.keep['id'] else "[dim][dup][/dim]"
                console.print(f"  {marker} {photo['file_path']}")


# ============================================================================
# List and search commands
# ============================================================================

@app.command("list")
def list_images(
    category: Optional[str] = typer.Option(
        None,
        "--category", "-c",
        help="Filter by category: private, work, other",
    ),
    source_label: Optional[str] = typer.Option(
        None,
        "--source", "-s",
        help="Filter by source label",
    ),
    screenshots: bool = typer.Option(
        False,
        "--screenshots",
        help="List only screenshots",
    ),
    limit: int = typer.Option(
        50,
        "--limit", "-n",
        help="Maximum images to show",
    ),
):
    """List images in the database."""
    # Get source_id if source_label provided
    source_id = None
    if source_label:
        source = db.get_source(source_label)
        if not source:
            console.print(f"[red]Error:[/red] Source not found: {source_label}")
            raise typer.Exit(1)
        source_id = source['id']

    photos = db.list_photos(
        source_id=source_id,
        category=category,
        screenshots_only=screenshots,
        limit=limit,
    )

    if not photos:
        console.print("No images found.")
        return

    console.print(f"Found {len(photos)} images")

    table = Table()
    table.add_column("ID", style="dim", justify="right")
    table.add_column("Category", style="cyan")
    table.add_column("File")
    table.add_column("Size", justify="right")
    if screenshots:
        table.add_column("Confidence", justify="right")

    for photo in photos:
        row = [
            str(photo['id']),
            photo['category'],
            photo['file_name'],
            format_file_size(photo.get('file_size') or 0),
        ]
        if screenshots:
            conf = f"{photo.get('screenshot_confidence', 0):.0%}" if photo.get('screenshot_confidence') else "-"
            row.append(conf)
        table.add_row(*row)

    console.print(table)


@app.command("search")
def search(
    query: str = typer.Argument(..., help="Search query"),
    limit: int = typer.Option(
        20,
        "--limit", "-n",
        help="Maximum results",
    ),
):
    """Search image descriptions."""
    results = db.search_descriptions(query)

    if not results:
        console.print(f"No images matching '{query}'")
        return

    console.print(f"Found {len(results)} matches")
    if len(results) > limit:
        console.print(f"Showing first {limit}")
        results = results[:limit]

    for result in results:
        console.print(f"\n[cyan]{result.get('file_name', 'unknown')}[/cyan]")
        console.print(f"  Path: {result.get('file_path', 'unknown')}")
        description = result.get('description', '')
        if description:
            console.print(f"  {description[:200]}...")


# ============================================================================
# AI analysis commands
# ============================================================================

@app.command("analyze")
def analyze(
    limit: Optional[int] = typer.Option(
        None,
        "--limit", "-n",
        help="Maximum images to analyze",
    ),
    provider: Optional[str] = typer.Option(
        None,
        "--provider", "-p",
        help="LLM provider: openai, claude_code",
    ),
):
    """Analyze images with AI to generate descriptions."""
    # Check for unanalyzed photos first
    unanalyzed = db.get_unanalyzed_photos(limit=limit)

    if not unanalyzed:
        console.print("[green]All images have been analyzed![/green]")
        return

    console.print(f"Analyzing {len(unanalyzed)} images...")

    with Progress(
        SpinnerColumn(),
        TextColumn("[progress.description]{task.description}"),
        BarColumn(),
        TaskProgressColumn(),
        console=console,
    ) as progress:
        task = progress.add_task("[cyan]Analyzing...", total=len(unanalyzed))

        analyzed, error_count, errors = analyze_photos(
            limit=limit,
            provider_name=provider,
            progress=progress,
            task_id=task,
        )

    console.print(f"\n[green]Analyzed: {analyzed}[/green]")
    if error_count:
        console.print(f"[red]Errors: {error_count}[/red]")
        for err in errors[:5]:
            console.print(f"  - {err}")


# ============================================================================
# Stats command
# ============================================================================

@app.command("stats")
def stats():
    """Show database statistics."""
    s = db.get_stats()

    console.print("\n[cyan]Photo Database Statistics[/cyan]\n")

    total = s.get('total', 0)
    analyzed = s.get('analyzed', 0)
    unanalyzed = total - analyzed

    console.print(f"Total photos: {total}")
    console.print(f"Total size: {format_file_size(s.get('total_size_bytes', 0))}")
    console.print(f"Screenshots: {s.get('screenshots', 0)}")
    console.print(f"Analyzed: {analyzed}")
    console.print(f"Unanalyzed: {unanalyzed}")
    console.print(f"Duplicate groups: {s.get('duplicate_groups', 0)}")
    console.print(f"Duplicate files: {s.get('duplicate_files', 0)}")

    by_category = s.get('by_category', {})
    if by_category:
        console.print("\n[cyan]By Category:[/cyan]")
        for cat, data in sorted(by_category.items()):
            count = data.get('count', 0) if isinstance(data, dict) else data
            size = data.get('size', 0) if isinstance(data, dict) else 0
            console.print(f"  {cat}: {count} ({format_file_size(size)})")

    by_source = s.get('by_source', {})
    if by_source:
        console.print("\n[cyan]By Source:[/cyan]")
        for src, data in sorted(by_source.items()):
            count = data.get('count', 0) if isinstance(data, dict) else data
            size = data.get('size', 0) if isinstance(data, dict) else 0
            console.print(f"  {src}: {count} ({format_file_size(size)})")


if __name__ == "__main__":
    app()
