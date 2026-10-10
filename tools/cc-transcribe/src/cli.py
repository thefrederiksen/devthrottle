"""CLI for cc-transcribe."""

import logging
from pathlib import Path
from typing import Optional

import openai
import typer
from rich.console import Console

try:
    from . import __version__
    from .transcriber import transcribe_video
    from .ffmpeg import get_video_info
except ImportError:
    from src import __version__
    from src.transcriber import transcribe_video
    from src.ffmpeg import get_video_info

# Configure logging for library modules
logging.basicConfig(level=logging.INFO, format="%(message)s")

app = typer.Typer(
    name="cc-transcribe",
    help="Transcribe video and audio files with timestamps and screenshots.",
    add_completion=False,
)
console = Console()


def version_callback(value: bool):
    if value:
        console.print(f"cc-transcribe version {__version__}")
        raise typer.Exit()


@app.command()
def main(
    input_file: Path = typer.Argument(
        ...,
        help="Input video file (.mp4, .mkv, .avi, .mov)",
        exists=True,
    ),
    output_dir: Path = typer.Option(
        None,
        "--output", "-o",
        help="Output directory (default: same as input)",
    ),
    screenshots: bool = typer.Option(
        True,
        "--screenshots/--no-screenshots",
        help="Extract screenshots at content changes",
    ),
    threshold: float = typer.Option(
        0.92,
        "--threshold", "-t",
        help="Screenshot sensitivity (0-1, lower = more screenshots)",
    ),
    interval: float = typer.Option(
        1.0,
        "--interval", "-i",
        help="Minimum seconds between screenshots",
    ),
    language: Optional[str] = typer.Option(
        None,
        "--language", "-l",
        help="Force language code (e.g., en, es, de)",
    ),
    info_only: bool = typer.Option(
        False,
        "--info",
        help="Show video info and exit",
    ),
    version: bool = typer.Option(
        False,
        "--version", "-v",
        callback=version_callback,
        is_eager=True,
        help="Show version and exit",
    ),
):
    """Transcribe video/audio with timestamps and optional screenshots."""

    if info_only:
        info = get_video_info(input_file)
        console.print(f"[cyan]File:[/cyan] {info['name']}")
        console.print(f"[cyan]Duration:[/cyan] {int(info['duration']//60)}m {int(info['duration']%60)}s")
        console.print(f"[cyan]Size:[/cyan] {info['size_bytes'] / 1024 / 1024:.1f} MB")
        console.print(f"[cyan]Format:[/cyan] {info['format']}")
        raise typer.Exit()

    if output_dir is None:
        output_dir = input_file.parent / f"{input_file.stem}_transcript"

    try:
        result = transcribe_video(
            video_path=input_file,
            output_dir=output_dir,
            extract_screenshots_flag=screenshots,
            screenshot_threshold=threshold,
            screenshot_interval=interval,
            language=language,
        )

        console.print(f"\n[green]Transcription complete![/green]")
        console.print(f"[cyan]Transcript:[/cyan] {result.transcript_path}")
        console.print(f"[cyan]Words:[/cyan] {result.word_count}")
        if result.screenshots:
            console.print(f"[cyan]Screenshots:[/cyan] {len(result.screenshots)} in {result.screenshots_dir}")

    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except RuntimeError as e:
        console.print(f"[red]Configuration error:[/red] {e}")
        raise typer.Exit(1)
    except ValueError as e:
        console.print(f"[red]Invalid input:[/red] {e}")
        raise typer.Exit(1)
    except openai.APIError as e:
        console.print(f"[red]OpenAI API error:[/red] {e}")
        raise typer.Exit(1)
    except OSError as e:
        console.print(f"[red]File error:[/red] {e}")
        raise typer.Exit(1)


def tool_main() -> None:
    """The console-script entry point. The tool runs through the shared failure reporter (issue #3642): a
    failure is reported to the Gateway, and the exit code and the printed error stay exactly as they were."""
    from cc_shared.tool_errors import run_tool

    run_tool(app, "cc-transcribe", app=app)


if __name__ == "__main__":
    tool_main()
