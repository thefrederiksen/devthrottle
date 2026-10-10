"""CLI for cc-whisper."""

import json
from pathlib import Path
from typing import Optional

import typer
from rich.console import Console

try:
    from . import __version__
    from .transcribe import transcribe, transcribe_to_file, transcribe_formatted, translate
except ImportError:
    from src import __version__
    from src.transcribe import transcribe, transcribe_to_file, transcribe_formatted, translate

app = typer.Typer(
    name="cc-whisper",
    help="Transcribe audio files using OpenAI Whisper.",
    add_completion=False,
)
console = Console()


def version_callback(value: bool) -> None:
    if value:
        console.print(f"cc-whisper version {__version__}")
        raise typer.Exit()


@app.callback(invoke_without_command=True)
def main(
    ctx: typer.Context,
    version: bool = typer.Option(
        False, "--version", "-v",
        callback=version_callback,
        is_eager=True,
        help="Show version",
    ),
) -> None:
    """Transcribe and translate audio using OpenAI Whisper."""
    if ctx.invoked_subcommand is None:
        console.print(ctx.get_help())


@app.command("transcribe")
def transcribe_cmd(
    audio: Path = typer.Argument(..., help="Audio file to transcribe", exists=True),
    output: Optional[Path] = typer.Option(None, "-o", "--output", help="Output file (default: print to console)"),
    language: Optional[str] = typer.Option(None, "-l", "--language", help="Language code (e.g., en, es, de)"),
    timestamps: bool = typer.Option(False, "-t", "--timestamps", help="Include timestamps"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
    format: str = typer.Option("txt", "-f", "--format", help="Output format: txt, srt, vtt"),
    prompt: Optional[str] = typer.Option(None, "-p", "--prompt", help="Context hint for accuracy (names, terms, jargon)"),
    temperature: Optional[float] = typer.Option(None, "--temp", help="Sampling temperature 0-1 (0=deterministic)"),
) -> None:
    """Transcribe audio using OpenAI Whisper."""
    size_mb = audio.stat().st_size / 1024 / 1024
    console.print(f"[blue]Audio:[/blue] {audio.name} ({size_mb:.1f} MB)")

    if language:
        console.print(f"[blue]Language:[/blue] {language}")
    if prompt:
        console.print(f"[blue]Prompt:[/blue] {prompt[:50]}{'...' if len(prompt) > 50 else ''}")

    try:
        console.print("[blue]Transcribing...[/blue]")

        # Use formatted output for srt/vtt
        if format in ("srt", "vtt"):
            text = transcribe_formatted(audio, format=format, language=language, prompt=prompt, temperature=temperature)
            if output:
                output.parent.mkdir(parents=True, exist_ok=True)
                output.write_text(text, encoding="utf-8")
                console.print(f"[green]Saved:[/green] {output}")
            else:
                console.print(text)
            return

        # Standard transcription
        if output:
            if json_output:
                result = transcribe(audio, language, timestamps, prompt, temperature)
                output.parent.mkdir(parents=True, exist_ok=True)
                output.write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
            else:
                transcribe_to_file(audio, output, language, timestamps, prompt, temperature)

            console.print(f"[green]Saved:[/green] {output}")
        else:
            result = transcribe(audio, language, timestamps, prompt, temperature)

            if json_output:
                console.print(json.dumps(result, indent=2, ensure_ascii=False))
            elif timestamps and result.get("segments"):
                for seg in result["segments"]:
                    start = seg["start"]
                    mins = int(start // 60)
                    secs = int(start % 60)
                    console.print(f"[cyan][{mins:02d}:{secs:02d}][/cyan] {seg['text']}")
            else:
                console.print(result["text"])

    except (FileNotFoundError, RuntimeError, ValueError) as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@app.command("translate")
def translate_cmd(
    audio: Path = typer.Argument(..., help="Audio file to translate", exists=True),
    output: Optional[Path] = typer.Option(None, "-o", "--output", help="Output file (default: print to console)"),
    prompt: Optional[str] = typer.Option(None, "-p", "--prompt", help="Context hint in English"),
    temperature: Optional[float] = typer.Option(None, "--temp", help="Sampling temperature 0-1"),
) -> None:
    """Translate audio from any language to English."""
    size_mb = audio.stat().st_size / 1024 / 1024
    console.print(f"[blue]Audio:[/blue] {audio.name} ({size_mb:.1f} MB)")
    console.print("[blue]Translating to English...[/blue]")

    try:
        result = translate(audio, prompt=prompt, temperature=temperature)

        if output:
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(result["text"], encoding="utf-8")
            console.print(f"[green]Saved:[/green] {output}")
        else:
            console.print(result["text"])

    except (FileNotFoundError, RuntimeError) as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


def tool_main() -> None:
    """The console-script entry point. The tool runs through the shared failure reporter (issue #3642): a
    failure is reported to the Gateway, and the exit code and the printed error stay exactly as they were."""
    from cc_shared.tool_errors import run_tool

    run_tool(app, "cc-whisper", app=app)


if __name__ == "__main__":
    tool_main()
