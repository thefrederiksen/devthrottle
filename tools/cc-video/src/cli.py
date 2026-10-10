"""CLI for cc-video."""

from pathlib import Path
from typing import Optional

import typer
from rich.console import Console
from rich.table import Table

try:
    from . import __version__
    from .ffmpeg import get_video_info, extract_audio
    from .screenshots import extract_screenshots, extract_frame_at
except ImportError:
    from src import __version__
    from src.ffmpeg import get_video_info, extract_audio
    from src.screenshots import extract_screenshots, extract_frame_at

app = typer.Typer(
    name="cc-video",
    help="Video utilities: info, extract audio, screenshots.",
    add_completion=False,
)
console = Console()


def version_callback(value: bool):
    if value:
        console.print(f"cc-video version {__version__}")
        raise typer.Exit()


@app.callback()
def main(
    version: bool = typer.Option(
        False, "--version", "-v",
        callback=version_callback,
        is_eager=True,
        help="Show version",
    ),
):
    """Video utilities."""
    pass


@app.command()
def info(
    video: Path = typer.Argument(..., help="Video file", exists=True),
):
    """Show video information."""
    data = get_video_info(video)

    table = Table(title=f"Video: {data['name']}")
    table.add_column("Property", style="cyan")
    table.add_column("Value")

    table.add_row("Duration", data['duration_formatted'])
    table.add_row("Size", f"{data['size_mb']} MB")
    table.add_row("Format", data['format'])

    if data.get('width') and data.get('height'):
        table.add_row("Resolution", f"{data['width']} x {data['height']}")

    console.print(table)


@app.command("audio")
def audio_cmd(
    video: Path = typer.Argument(..., help="Video file", exists=True),
    output: Optional[Path] = typer.Option(None, "-o", "--output", help="Output path"),
    format: str = typer.Option("mp3", "-f", "--format", help="Format: mp3, wav, aac, flac, ogg"),
    bitrate: str = typer.Option("192k", "-b", "--bitrate", help="Audio bitrate"),
):
    """Extract audio from video."""
    try:
        console.print(f"[blue]Extracting audio from:[/blue] {video.name}")
        result = extract_audio(video, output, format=format, bitrate=bitrate)
        size_mb = result.stat().st_size / 1024 / 1024
        console.print(f"[green]Saved:[/green] {result} ({size_mb:.1f} MB)")
    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except RuntimeError as e:
        console.print(f"[red]FFmpeg error:[/red] {e}")
        raise typer.Exit(1)
    except OSError as e:
        console.print(f"[red]File error:[/red] {e}")
        raise typer.Exit(1)


@app.command("screenshots")
def screenshots_cmd(
    video: Path = typer.Argument(..., help="Video file", exists=True),
    output_dir: Path = typer.Option(..., "-o", "--output", help="Output directory"),
    threshold: float = typer.Option(0.92, "-t", "--threshold", help="Sensitivity (0-1, lower = more screenshots)"),
    interval: float = typer.Option(1.0, "-i", "--interval", help="Min seconds between screenshots"),
    max_count: Optional[int] = typer.Option(None, "-n", "--max", help="Maximum screenshots"),
):
    """Extract screenshots at content changes."""
    try:
        console.print(f"[blue]Processing:[/blue] {video.name}")
        results = extract_screenshots(
            video, output_dir,
            threshold=threshold,
            interval=interval,
            max_screenshots=max_count,
        )
        console.print(f"[green]Extracted:[/green] {len(results)} screenshots to {output_dir}")
    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except ValueError as e:
        console.print(f"[red]Video error:[/red] {e}")
        raise typer.Exit(1)
    except OSError as e:
        console.print(f"[red]File error:[/red] {e}")
        raise typer.Exit(1)


@app.command("frame")
def frame_cmd(
    video: Path = typer.Argument(..., help="Video file", exists=True),
    time: float = typer.Option(..., "-t", "--time", help="Timestamp in seconds"),
    output: Path = typer.Option(..., "-o", "--output", help="Output image path"),
):
    """Extract single frame at timestamp."""
    try:
        console.print(f"[blue]Extracting frame at {time}s...[/blue]")
        result = extract_frame_at(video, time, output)
        console.print(f"[green]Saved:[/green] {result}")
    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except ValueError as e:
        console.print(f"[red]Video error:[/red] {e}")
        raise typer.Exit(1)
    except OSError as e:
        console.print(f"[red]File error:[/red] {e}")
        raise typer.Exit(1)


def tool_main() -> None:
    """The console-script entry point. The tool runs through the shared failure reporter (issue #3642): a
    failure is reported to the Gateway, and the exit code and the printed error stay exactly as they were."""
    from cc_shared.tool_errors import run_tool

    run_tool(app, "cc-video", app=app)


if __name__ == "__main__":
    tool_main()
