"""cc-spotify CLI - Spotify control via browser automation."""

import typer
from rich.console import Console
from rich.table import Table
from typing import Optional
import json
from pathlib import Path

from .browser_client import BrowserClient, BrowserError, ConnectionError, WorkspaceError
from .selectors import SpotifyKeys, SpotifySelectors, SpotifyURLs
from .delays import jittered_sleep
import time
from .spotify_js import (
    get_now_playing_js,
    get_shuffle_state_js,
    get_repeat_state_js,
    get_playlists_js,
    get_search_results_js,
    get_queue_js,
    get_tracklist_rows_js,
    scroll_main_view_js,
    scroll_main_view_to_js,
    set_volume_js,
)

app = typer.Typer(
    name="cc-spotify",
    help="Spotify CLI via browser automation",
    no_args_is_help=True,
)

console = Console()


# =============================================================================
# Config Helpers
# =============================================================================

def get_config_dir() -> Path:
    """Get cc-spotify config directory."""
    try:
        from cc_storage import CcStorage
        return CcStorage.tool_config("spotify")
    except ImportError:
        import sys
        _tools_dir = str(Path(__file__).resolve().parent.parent.parent)
        if _tools_dir not in sys.path:
            sys.path.insert(0, _tools_dir)
        from cc_storage import CcStorage
        return CcStorage.tool_config("spotify")


def load_default_connection() -> Optional[str]:
    """Load default connection from config.json.

    Returns:
        Default connection name from config, or None to auto-resolve by tool binding.
    """
    config_file = get_config_dir() / "config.json"

    if not config_file.exists():
        return None

    try:
        with open(config_file, "r") as f:
            data = json.load(f)
        return data.get("default_connection", data.get("default_workspace"))
    except json.JSONDecodeError as e:
        console.print(f"[red]ERROR:[/red] Invalid JSON in {config_file}: {e}")
        raise typer.Exit(1)
    except IOError as e:
        console.print(f"[red]ERROR:[/red] Cannot read {config_file}: {e}")
        raise typer.Exit(1)


def save_config(connection: str) -> Path:
    """Save connection to config.json."""
    config_dir = get_config_dir()
    config_dir.mkdir(parents=True, exist_ok=True)
    config_file = config_dir / "config.json"

    data = {}
    if config_file.exists():
        try:
            with open(config_file, "r") as f:
                data = json.load(f)
        except (json.JSONDecodeError, IOError):
            data = {}

    data["default_connection"] = connection

    with open(config_file, "w") as f:
        json.dump(data, f, indent=2)

    return config_file


def list_available_connections() -> list[str]:
    """List available cc-browser connections."""
    from .browser_client import get_connections_registry
    registry = get_connections_registry()
    if not registry.exists():
        return []
    try:
        import json as _json
        connections = _json.loads(registry.read_text())
        return [c.get("name", "?") for c in connections]
    except (json.JSONDecodeError, IOError):
        return []


# =============================================================================
# Global Options
# =============================================================================

class AppConfig:
    workspace: str = ""
    format: str = "text"
    verbose: bool = False


app_config = AppConfig()


def get_client() -> BrowserClient:
    """Get browser client instance for configured connection."""
    try:
        return BrowserClient(connection=app_config.workspace)
    except (ConnectionError, WorkspaceError) as e:
        console.print(f"[red]ERROR:[/red] {e}")
        raise typer.Exit(1)


def error(msg: str):
    """Print error message."""
    console.print(f"[red]ERROR:[/red] {msg}")


def success(msg: str):
    """Print success message."""
    console.print(f"[green]OK:[/green] {msg}")


def output_json(data: dict):
    """Output data in configured format."""
    if app_config.format == "json":
        console.print_json(json.dumps(data))
    else:
        return data


def verbose_snapshot(client: BrowserClient):
    """If verbose, dump the current page snapshot."""
    if app_config.verbose:
        try:
            snap = client.snapshot()
            console.print("[dim]--- Snapshot ---[/dim]")
            console.print_json(json.dumps(snap, indent=2)[:3000])
            console.print("[dim]--- End Snapshot ---[/dim]")
        except BrowserError:
            console.print("[dim]Could not get snapshot[/dim]")


def parse_js_result(result: dict) -> dict:
    """Parse JavaScript evaluation result from cc-browser."""
    raw = result.get("result", result.get("value", "{}"))
    if isinstance(raw, str):
        try:
            return json.loads(raw)
        except json.JSONDecodeError:
            return {"raw": raw}
    if isinstance(raw, dict):
        return raw
    return {"raw": str(raw)}


def _version_callback(value: bool) -> None:
    """Print version and exit. Eager so --version works without a subcommand."""
    if value:
        typer.echo("cc-spotify version 0.1.0")
        raise typer.Exit()


@app.callback()
def main(
    version: Optional[bool] = typer.Option(
        None, "--version", help="Show version and exit.",
        callback=_version_callback, is_eager=True,
    ),
    connection: Optional[str] = typer.Option(
        None, "--connection", "-c",
        help="cc-browser connection name"
    ),
    workspace: Optional[str] = typer.Option(
        None, "--workspace", "-w", hidden=True,
        help="Deprecated: use --connection"
    ),
    format: str = typer.Option(
        "text", "--format", "-f",
        help="Output format: text, json"
    ),
    verbose: bool = typer.Option(
        False, "--verbose", "-v",
        help="Verbose output (dump snapshots for debugging)"
    ),
):
    """Spotify CLI via browser automation.

    Control Spotify Web Player through a cc-browser connection.
    Requires cc-browser daemon to be running with Spotify open.

    First-time setup:
      cc-spotify config --connection <name>
      cc-browser connections open <name>
      (navigate to open.spotify.com and log in)
    """
    resolved = connection or workspace or load_default_connection()

    app_config.workspace = resolved  # May be None; BrowserClient auto-resolves
    app_config.format = format
    app_config.verbose = verbose


def _ensure_connection():
    """Ensure connection is available before running commands."""
    # With v2, BrowserClient can auto-resolve via tool binding,
    # so we only need to check if workspace is explicitly empty string
    pass


# =============================================================================
# Config Command
# =============================================================================

@app.command()
def config(
    connection: Optional[str] = typer.Option(
        None, "--connection", "-c",
        help="Set default connection"
    ),
    workspace: Optional[str] = typer.Option(
        None, "--workspace", "-w", hidden=True,
        help="Deprecated: use --connection"
    ),
    show: bool = typer.Option(
        False, "--show", "-s",
        help="Show current config"
    ),
):
    """Configure cc-spotify settings."""
    config_file = get_config_dir() / "config.json"
    resolved = connection or workspace

    if show or resolved is None:
        if config_file.exists():
            with open(config_file, "r") as f:
                data = json.load(f)
            console.print(f"Config file: {config_file}")
            console.print_json(json.dumps(data, indent=2))
        else:
            console.print(f"No config file found at: {config_file}")
            console.print("Run: cc-spotify config --connection <name>")

        available = list_available_connections()
        if available:
            console.print("\nAvailable cc-browser connections:")
            for c in available:
                console.print(f"  - {c}")
        return

    saved = save_config(resolved)
    success(f"Default connection set to '{resolved}'")
    console.print(f"Config saved to: {saved}")


# =============================================================================
# Status Command
# =============================================================================

@app.command()
def status():
    """Check cc-browser daemon and Spotify connection status."""
    _ensure_connection()

    try:
        client = get_client()

        result = client.status()
        console.print(f"[green]cc-browser daemon:[/green] running (port {client.port})")

        browser_status = result.get("browser", "unknown")
        console.print(f"[green]Browser:[/green] {browser_status}")

        try:
            info = client.info()
            url = info.get("url", "")
            if "open.spotify.com" in url:
                console.print(f"[green]Spotify:[/green] connected ({url})")

                # Try to get now playing info
                js_result = client.evaluate(get_now_playing_js())
                track_data = parse_js_result(js_result)
                if track_data.get("name"):
                    state = "Playing" if track_data.get("is_playing") else "Paused"
                    console.print(
                        f"[green]Now:[/green] {track_data['name']} - "
                        f"{track_data.get('artist', '?')} [{state}]"
                    )
            else:
                console.print(
                    f"[yellow]Current page:[/yellow] {url}\n"
                    "Navigate to open.spotify.com to use cc-spotify."
                )
        except BrowserError:
            console.print("[yellow]Browser:[/yellow] no page loaded")

        verbose_snapshot(client)

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Now Playing
# =============================================================================

@app.command()
def now():
    """Show currently playing track info."""
    _ensure_connection()

    try:
        client = get_client()
        js_result = client.evaluate(get_now_playing_js())
        data = parse_js_result(js_result)

        if data.get("error"):
            error(data["error"])
            console.print("Make sure Spotify Web Player is open and a track is loaded.")
            raise typer.Exit(1)

        if app_config.format == "json":
            console.print_json(json.dumps(data))
            return

        state = "Playing" if data.get("is_playing") else "Paused"
        liked = " [red]*[/red]" if data.get("is_liked") else ""

        console.print(f"[bold]{data.get('name', '?')}[/bold]{liked}")
        console.print(f"  Artist: {data.get('artist', '?')}")
        pos = data.get("position", "")
        dur = data.get("duration", "")
        if pos and dur:
            console.print(f"  Time:   {pos} / {dur}")
        console.print(f"  Status: {state}")

        verbose_snapshot(client)

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Playback Controls
# =============================================================================

@app.command()
def play():
    """Resume playback."""
    _ensure_connection()

    try:
        client = get_client()

        # Check if already playing
        js_result = client.evaluate(get_now_playing_js())
        data = parse_js_result(js_result)
        if data.get("is_playing"):
            console.print("Already playing.")
            return

        client.press(SpotifyKeys.PLAY_PAUSE)
        jittered_sleep(0.5)
        success("Playback resumed")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def pause():
    """Pause playback."""
    _ensure_connection()

    try:
        client = get_client()

        # Check if already paused
        js_result = client.evaluate(get_now_playing_js())
        data = parse_js_result(js_result)
        if not data.get("is_playing"):
            console.print("Already paused.")
            return

        client.press(SpotifyKeys.PLAY_PAUSE)
        jittered_sleep(0.5)
        success("Playback paused")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command(name="next")
def next_track():
    """Skip to next track."""
    _ensure_connection()

    try:
        client = get_client()
        client.press(SpotifyKeys.NEXT_TRACK)
        jittered_sleep(1.0)

        # Show what's now playing
        js_result = client.evaluate(get_now_playing_js())
        data = parse_js_result(js_result)
        if data.get("name"):
            console.print(f"Now playing: {data['name']} - {data.get('artist', '?')}")
        else:
            success("Skipped to next track")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command(name="prev")
def prev_track():
    """Go to previous track."""
    _ensure_connection()

    try:
        client = get_client()
        client.press(SpotifyKeys.PREV_TRACK)
        jittered_sleep(1.0)

        js_result = client.evaluate(get_now_playing_js())
        data = parse_js_result(js_result)
        if data.get("name"):
            console.print(f"Now playing: {data['name']} - {data.get('artist', '?')}")
        else:
            success("Went to previous track")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Shuffle / Repeat / Volume / Like
# =============================================================================

@app.command()
def shuffle(
    on: bool = typer.Option(False, "--on", help="Turn shuffle on"),
    off: bool = typer.Option(False, "--off", help="Turn shuffle off"),
):
    """Toggle shuffle mode, or set explicitly with --on/--off."""
    _ensure_connection()

    try:
        client = get_client()

        if on or off:
            # Check current state first
            js_result = client.evaluate(get_shuffle_state_js())
            data = parse_js_result(js_result)
            current = data.get("shuffle", False)

            if (on and current) or (off and not current):
                state_str = "on" if current else "off"
                console.print(f"Shuffle is already {state_str}.")
                return

        # Use snapshot + click for shuffle button
        snap = client.snapshot()
        verbose_snapshot(client)
        _click_by_testid(client, snap, "control-button-shuffle")
        jittered_sleep(0.5)

        # Report new state
        js_result = client.evaluate(get_shuffle_state_js())
        data = parse_js_result(js_result)
        state_str = "on" if data.get("shuffle") else "off"
        success(f"Shuffle: {state_str}")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def repeat(
    mode: Optional[str] = typer.Argument(
        None,
        help="Target mode: off, context, track (omit to cycle)"
    ),
):
    """Set repeat mode or cycle through modes."""
    _ensure_connection()

    try:
        client = get_client()

        if mode and mode not in ("off", "context", "track"):
            error("Mode must be one of: off, context, track")
            raise typer.Exit(1)

        # Click the repeat button (cycles: off -> context -> track -> off)
        snap = client.snapshot()
        verbose_snapshot(client)
        _click_by_testid(client, snap, "control-button-repeat")
        jittered_sleep(0.5)

        # If a specific mode is requested, keep clicking until we reach it
        if mode:
            for _ in range(3):
                js_result = client.evaluate(get_repeat_state_js())
                data = parse_js_result(js_result)
                if data.get("repeat") == mode:
                    break
                snap = client.snapshot()
                _click_by_testid(client, snap, "control-button-repeat")
                jittered_sleep(0.5)

        # Report state
        js_result = client.evaluate(get_repeat_state_js())
        data = parse_js_result(js_result)
        success(f"Repeat: {data.get('repeat', 'unknown')}")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def volume(
    level: int = typer.Argument(..., help="Volume level 0-100"),
):
    """Set volume level (0-100)."""
    _ensure_connection()

    if level < 0 or level > 100:
        error("Volume must be between 0 and 100")
        raise typer.Exit(1)

    try:
        client = get_client()
        js_result = client.evaluate(set_volume_js(level))
        data = parse_js_result(js_result)

        if data.get("error"):
            error(data["error"])
            console.print(
                "Volume slider may not be accessible. "
                "Try clicking in the Spotify window first."
            )
            raise typer.Exit(1)

        success(f"Volume set to {level}")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def like():
    """Heart/save the currently playing track."""
    _ensure_connection()

    try:
        client = get_client()

        # Try keyboard shortcut first (most reliable)
        client.press(SpotifyKeys.LIKE)
        jittered_sleep(0.5)

        # Check result
        js_result = client.evaluate(get_now_playing_js())
        data = parse_js_result(js_result)
        if data.get("is_liked"):
            success(f"Liked: {data.get('name', 'current track')}")
        else:
            console.print(
                "Toggled like state for: "
                f"{data.get('name', 'current track')}"
            )

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Search
# =============================================================================

@app.command()
def search(
    query: str = typer.Argument(..., help="Search query"),
):
    """Search Spotify for tracks, artists, albums."""
    _ensure_connection()

    try:
        client = get_client()

        # Navigate to search URL
        url = SpotifyURLs.search(query)
        client.navigate(url)
        jittered_sleep(3.0)

        # Extract results via JS
        js_result = client.evaluate(get_search_results_js())
        results = parse_js_result(js_result)

        verbose_snapshot(client)

        if app_config.format == "json":
            console.print_json(json.dumps(results))
            return

        if isinstance(results, list):
            if not results:
                console.print(f"No results found for: {query}")
                return

            table = Table(title=f"Search: {query}")
            table.add_column("#", style="dim", width=4)
            table.add_column("Name", style="bold")
            table.add_column("Artist")
            table.add_column("Type", style="dim")

            for i, r in enumerate(results[:20], 1):
                table.add_row(
                    str(i),
                    r.get("name", ""),
                    r.get("artist", ""),
                    r.get("type", ""),
                )
            console.print(table)
        else:
            console.print("Unexpected result format.")
            if app_config.verbose:
                console.print_json(json.dumps(results))

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Playlists
# =============================================================================

@app.command()
def playlists(
    filter_type: Optional[str] = typer.Option(
        None, "--type", "-t",
        help="Filter by type: playlist, podcast, artist, album"
    ),
):
    """List library items from the sidebar."""
    _ensure_connection()

    try:
        client = get_client()

        js_result = client.evaluate(get_playlists_js())
        data = parse_js_result(js_result)

        verbose_snapshot(client)

        if not isinstance(data, list):
            console.print("Unexpected result format.")
            return

        # Apply type filter
        if filter_type:
            data = [p for p in data if p.get("type", "") == filter_type.lower()]

        if app_config.format == "json":
            console.print_json(json.dumps(data))
            return

        if not data:
            console.print("No items found in sidebar.")
            if filter_type:
                console.print(f"Try without --type filter, or check type: playlist, podcast, artist, album")
            return

        table = Table(title="Your Library")
        table.add_column("#", style="dim", width=4)
        table.add_column("Name", style="bold")
        table.add_column("Type", style="cyan", width=10)

        for i, p in enumerate(data, 1):
            table.add_row(
                str(i),
                p.get("name", ""),
                p.get("type", ""),
            )
        console.print(table)

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def playlist(
    name: str = typer.Argument(..., help="Playlist name to play"),
):
    """Play a playlist by name (searches sidebar)."""
    _ensure_connection()

    try:
        client = get_client()

        # Get playlists from sidebar
        js_result = client.evaluate(get_playlists_js())
        data = parse_js_result(js_result)

        if not isinstance(data, list) or not data:
            error("No playlists found in sidebar.")
            raise typer.Exit(1)

        # Find matching playlist (case-insensitive partial match)
        name_lower = name.lower()
        match = None
        for p in data:
            if name_lower in p.get("name", "").lower():
                match = p
                break

        if not match:
            error(f"Playlist '{name}' not found.")
            console.print("Available playlists:")
            for p in data[:10]:
                console.print(f"  - {p.get('name', '')}")
            raise typer.Exit(1)

        # Click the playlist via snapshot
        snap = client.snapshot()
        snapshot_text = json.dumps(snap)

        # Find the playlist element ref from the snapshot
        found_ref = _find_text_ref(snap, match["name"])
        if found_ref:
            client.click(found_ref)
            jittered_sleep(2.0)
            success(f"Opened playlist: {match['name']}")
        else:
            # Navigate to collection playlists and try to find it there
            console.print(f"Could not click playlist directly. Try: cc-spotify goto <playlist-url>")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Queue
# =============================================================================

@app.command()
def queue():
    """Show the playback queue."""
    _ensure_connection()

    try:
        client = get_client()

        # Navigate to queue view
        client.navigate(SpotifyURLs.queue())
        jittered_sleep(2.0)

        js_result = client.evaluate(get_queue_js())
        data = parse_js_result(js_result)

        verbose_snapshot(client)

        if app_config.format == "json":
            console.print_json(json.dumps(data))
            return

        if isinstance(data, list):
            if not data:
                console.print("Queue is empty.")
                return

            table = Table(title="Playback Queue")
            table.add_column("#", style="dim", width=4)
            table.add_column("Track", style="bold")
            table.add_column("Artist")

            for track in data[:30]:
                table.add_row(
                    str(track.get("position", "")),
                    track.get("name", ""),
                    track.get("artist", ""),
                )
            console.print(table)

            if len(data) > 30:
                console.print(f"[dim]... and {len(data) - 30} more tracks[/dim]")
        else:
            console.print("Unexpected result format.")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Liked Songs
# =============================================================================

def _collect_visible_tracks(client: BrowserClient, all_tracks: dict):
    """Extract currently visible tracklist rows and merge into all_tracks.

    Uses aria-rowindex as primary key to avoid missing tracks with
    duplicate name+artist combinations.
    """
    result = client.evaluate(get_tracklist_rows_js())
    data = parse_js_result(result)
    if isinstance(data, list):
        for t in data:
            key = t.get("idx") or (t.get("name", "") + "|" + t.get("artist", ""))
            if key and key not in all_tracks:
                all_tracks[key] = t


def _hover_on_content_area(client: BrowserClient):
    """Position the Playwright cursor over the main content area.

    Required before mouse wheel scrolling -- page.mouse.wheel() dispatches
    events at the cursor position, so we need the cursor over the scroll
    container. Uses the accessibility snapshot to find a track link ref.
    """
    import re

    snap = client.snapshot()
    snapshot_text = snap.get("snapshot", "")

    # Find a track link ref in the content area
    for line in snapshot_text.split("\n"):
        m = re.search(r'link ".+?" \[ref=(e\d+)\]', line)
        if m and any(kw in line for kw in ["Play ", "link \"Knock", "link \"Without"]):
            continue  # Skip play buttons
        if m and "link" in line and "[nth=" not in line:
            # Found a link -- check it's in the track area (high ref number)
            ref_num = int(m.group(1)[1:])
            if ref_num > 70:  # Track area refs start after sidebar/controls
                client._post("/hover", {"ref": m.group(1)})
                return True
    return False


def _check_at_bottom(client: BrowserClient) -> bool:
    """Check if main content scroll is at the bottom."""
    result = client.evaluate("""(() => {
        const child = document.querySelector('.main-view-container__scroll-node-child');
        const c = child ? child.parentElement : null;
        if (!c) return JSON.stringify({atBottom: true});
        return JSON.stringify({atBottom: c.scrollTop + c.clientHeight >= c.scrollHeight - 20});
    })()""")
    data = parse_js_result(result)
    return data.get("atBottom", False)


def _scroll_liked_js(client: BrowserClient, all_tracks: dict, limit: int):
    """Scroll Liked Songs using JS scrollBy (fast, ~2 min)."""
    prev_count = 0
    stale_rounds = 0

    for scroll_num in range(500):
        time.sleep(1.2)
        _collect_visible_tracks(client, all_tracks)
        current_count = len(all_tracks)

        if limit and current_count >= limit:
            break

        if current_count == prev_count:
            stale_rounds += 1
            if stale_rounds >= 3:
                break
        else:
            stale_rounds = 0
        prev_count = current_count

        # Half-page scroll for maximum overlap with virtual list
        result = client.evaluate("""
        (() => {
            const child = document.querySelector('.main-view-container__scroll-node-child');
            const c = child ? child.parentElement : null;
            if (!c) return JSON.stringify({atBottom: true});
            c.scrollBy(0, Math.floor(c.clientHeight * 0.5));
            return JSON.stringify({atBottom: c.scrollTop + c.clientHeight >= c.scrollHeight - 20});
        })()
        """)
        scroll_data = parse_js_result(result)
        if scroll_data.get("atBottom"):
            time.sleep(1.5)
            _collect_visible_tracks(client, all_tracks)
            break


def _scroll_liked_wheel(client: BrowserClient, all_tracks: dict, limit: int):
    """Scroll Liked Songs using real mouse wheel events (~6 min).

    Uses the cc-browser daemon's scroll endpoint which calls
    page.mouse.wheel(). In human mode, each scroll is broken into
    3-6 smaller wheel events with random 30-100ms delays between them.
    """
    # Position cursor over the content area first
    if not _hover_on_content_area(client):
        console.print("[yellow]Could not position cursor. Falling back to JS scroll.[/yellow]")
        _scroll_liked_js(client, all_tracks, limit)
        return

    prev_count = 0
    stale_rounds = 0

    for tick in range(1500):
        _collect_visible_tracks(client, all_tracks)
        current_count = len(all_tracks)

        if limit and current_count >= limit:
            break

        if current_count == prev_count:
            stale_rounds += 1
            if stale_rounds >= 8:
                break
        else:
            stale_rounds = 0
        prev_count = current_count

        # Small wheel scroll -- 200px is ~2 mouse wheel ticks
        client.scroll("down", amount=200)
        time.sleep(0.4)

        # Check bottom every 5 ticks
        if tick % 5 == 0 and _check_at_bottom(client):
            time.sleep(1.0)
            _collect_visible_tracks(client, all_tracks)
            break


@app.command()
def liked(
    limit: int = typer.Option(
        0, "--limit", "-n",
        help="Max tracks to show (0 = all)"
    ),
    wheel: bool = typer.Option(
        False, "--wheel",
        help="Use real mouse wheel scrolling (slower but more realistic)"
    ),
):
    """List your Liked Songs by scrolling through the collection.

    By default uses JS scrollBy (fast, ~2 min). With --wheel, uses real
    mouse wheel events via the daemon (slower, ~6 min, but behaves like
    a human scrolling). Both methods typically capture 96%+ of tracks.
    """
    _ensure_connection()

    try:
        client = get_client()

        # Navigate to Liked Songs
        client.navigate(SpotifyURLs.collection_tracks())
        time.sleep(3.0)

        # Scroll to top
        client.evaluate(scroll_main_view_to_js(0))
        time.sleep(1.0)

        all_tracks: dict = {}

        if wheel:
            console.print("[dim]Scrolling with mouse wheel (slower, more realistic)...[/dim]")
            _scroll_liked_wheel(client, all_tracks, limit)
        else:
            console.print("[dim]Scrolling through Liked Songs...[/dim]")
            _scroll_liked_js(client, all_tracks, limit)

        # Sort by aria-rowindex
        sorted_tracks = sorted(
            all_tracks.values(),
            key=lambda t: int(t.get("idx") or "0")
        )

        if limit:
            sorted_tracks = sorted_tracks[:limit]

        if app_config.format == "json":
            console.print_json(json.dumps(sorted_tracks))
            return

        table = Table(title=f"Liked Songs ({len(sorted_tracks)} tracks)")
        table.add_column("#", style="dim", width=5)
        table.add_column("Title", style="bold")
        table.add_column("Artist")
        table.add_column("Album", style="dim")

        for i, t in enumerate(sorted_tracks, 1):
            table.add_row(
                str(i),
                t.get("name", ""),
                t.get("artist", ""),
                t.get("album", ""),
            )
        console.print(table)

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Navigate
# =============================================================================

@app.command()
def goto(
    url: str = typer.Argument(..., help="Spotify URL to navigate to"),
):
    """Navigate to a Spotify URL."""
    _ensure_connection()

    if not url.startswith("http"):
        # Assume it's a Spotify URI or path
        if url.startswith("spotify:"):
            # Convert URI to URL: spotify:track:123 -> open.spotify.com/track/123
            parts = url.replace("spotify:", "").split(":")
            if len(parts) == 2:
                url = f"{SpotifyURLs.BASE}/{parts[0]}/{parts[1]}"
            else:
                error(f"Cannot parse Spotify URI: {url}")
                raise typer.Exit(1)
        elif url.startswith("/"):
            url = f"{SpotifyURLs.BASE}{url}"
        else:
            url = f"{SpotifyURLs.BASE}/{url}"

    try:
        client = get_client()
        client.navigate(url)
        jittered_sleep(2.0)
        success(f"Navigated to: {url}")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Recommend (Vault Integration)
# =============================================================================

@app.command()
def recommend(
    mood: Optional[str] = typer.Option(
        None, "--mood", "-m",
        help="Mood or genre hint (e.g., 'chill jazz', 'energetic workout')"
    ),
):
    """Get music recommendations powered by vault preferences."""
    _ensure_connection()

    try:
        from .vault_integration import get_recommendations
        suggestions = get_recommendations(mood=mood)

        if not suggestions:
            console.print(
                "No music preferences found in vault.\n\n"
                "Add preferences with:\n"
                "  cc-vault docs import music-preferences.md\n\n"
                "Example content:\n"
                "  # Music Preferences\n"
                "  - Genres: jazz, ambient, indie rock\n"
                "  - Artists: Miles Davis, Radiohead, Khruangbin\n"
                "  - Moods: chill for work, energetic for workouts"
            )
            return

        console.print("[bold]Vault Recommendations:[/bold]")
        for s in suggestions:
            console.print(f"  -> {s}")

        # Offer to search the first suggestion
        if suggestions:
            console.print(
                f"\nSearch Spotify: cc-spotify search \"{suggestions[0]}\""
            )

    except ImportError:
        error("vault_integration module not available")
        raise typer.Exit(1)
    except Exception as e:
        error(f"Vault query failed: {e}")
        console.print("Make sure cc-vault is installed and accessible.")
        raise typer.Exit(1)


# =============================================================================
# Snapshot Helpers
# =============================================================================

def _click_by_testid(client: BrowserClient, snapshot: dict, testid: str):
    """Find and click an element by data-testid in a snapshot.

    Searches the snapshot tree for an element with matching data-testid
    and clicks it via its ref.
    """
    ref = _find_testid_ref(snapshot, testid)
    if ref:
        client.click(ref)
        return True

    # Fallback: try CSS selector click
    error(f"Element with data-testid='{testid}' not found in snapshot")
    return False


def _find_testid_ref(snapshot: dict, testid: str) -> Optional[str]:
    """Recursively search snapshot for element with data-testid."""
    return _search_snapshot(
        snapshot,
        lambda node: testid in node.get("attributes", {}).get("data-testid", "")
    )


def _find_text_ref(snapshot: dict, text: str) -> Optional[str]:
    """Recursively search snapshot for element containing text."""
    text_lower = text.lower()
    return _search_snapshot(
        snapshot,
        lambda node: text_lower in (node.get("text", "") or "").lower()
    )


def _search_snapshot(data: dict, predicate) -> Optional[str]:
    """Search snapshot tree for a node matching predicate. Returns ref."""
    if isinstance(data, dict):
        if predicate(data) and data.get("ref"):
            return data["ref"]

        # Search children
        for key in ("children", "nodes", "content"):
            children = data.get(key, [])
            if isinstance(children, list):
                for child in children:
                    result = _search_snapshot(child, predicate)
                    if result:
                        return result
            elif isinstance(children, dict):
                result = _search_snapshot(children, predicate)
                if result:
                    return result
    elif isinstance(data, list):
        for item in data:
            result = _search_snapshot(item, predicate)
            if result:
                return result

    return None
