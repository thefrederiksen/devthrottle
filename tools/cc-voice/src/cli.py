"""CLI for cc-voice."""

import logging
from pathlib import Path
from typing import Optional

import openai
import typer
from rich.console import Console

try:
    from . import __version__
    from .tts import tts_to_file, Voice, Model
except ImportError:
    from src import __version__
    from src.tts import tts_to_file, Voice, Model

# Configure logging for library modules
logging.basicConfig(level=logging.INFO, format="%(message)s")

app = typer.Typer(
    name="cc-voice",
    help="Convert text to speech using OpenAI TTS.",
    add_completion=False,
)
console = Console()


def version_callback(value: bool):
    if value:
        console.print(f"cc-voice version {__version__}")
        raise typer.Exit()


@app.command()
def main(
    text: str = typer.Argument(..., help="Text to convert (or path to text file)"),
    output: Path = typer.Option(..., "-o", "--output", help="Output audio file (.mp3)"),
    voice: str = typer.Option("onyx", "-v", "--voice", help="Voice: alloy, echo, fable, nova, onyx, shimmer"),
    model: str = typer.Option("tts-1", "-m", "--model", help="Model: tts-1, tts-1-hd"),
    speed: float = typer.Option(1.0, "-s", "--speed", help="Speed: 0.25 to 4.0"),
    raw: bool = typer.Option(False, "--raw", help="Don't clean markdown formatting"),
    version: bool = typer.Option(
        False, "--version",
        callback=version_callback,
        is_eager=True,
        help="Show version",
    ),
):
    """Convert text to speech."""

    # Check if input is a file path
    input_path = Path(text)
    if input_path.exists() and input_path.is_file():
        console.print(f"[blue]Reading:[/blue] {input_path}")
        text = input_path.read_text(encoding="utf-8")

    if not text.strip():
        console.print("[red]Error:[/red] No text provided")
        raise typer.Exit(1)

    char_count = len(text)
    console.print(f"[blue]Text:[/blue] {char_count} characters")
    console.print(f"[blue]Voice:[/blue] {voice}")
    console.print(f"[blue]Model:[/blue] {model}")

    try:
        console.print("[blue]Converting...[/blue]")
        result = tts_to_file(
            text=text,
            output_path=output,
            voice=voice,
            model=model,
            speed=speed,
            clean_markdown=not raw,
        )

        size_kb = result.stat().st_size / 1024
        console.print(f"[green]Created:[/green] {result} ({size_kb:.1f} KB)")

    except ValueError as e:
        console.print(f"[red]Invalid input:[/red] {e}")
        raise typer.Exit(1)
    except RuntimeError as e:
        console.print(f"[red]Configuration error:[/red] {e}")
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

    run_tool(app, "cc-voice", app=app)


if __name__ == "__main__":
    tool_main()
