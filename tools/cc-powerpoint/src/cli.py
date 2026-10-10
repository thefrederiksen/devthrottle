"""CLI interface for cc-powerpoint using Typer."""

import sys
from pathlib import Path
from typing import Optional

import typer
from rich.console import Console
from rich.table import Table

# Handle imports for both package and frozen executable modes
try:
    from . import __version__
    from .parser import parse_markdown
    from .pptx_generator import generate_pptx
    from .themes import THEMES, get_theme
    from .md_converter import convert_pptx_to_markdown
except ImportError:
    # Frozen executable mode - use absolute imports
    from src import __version__
    from src.parser import parse_markdown
    from src.pptx_generator import generate_pptx
    from src.themes import THEMES, get_theme
    from src.md_converter import convert_pptx_to_markdown

app = typer.Typer(
    name="cc-powerpoint",
    help="Convert between Markdown and PowerPoint presentations with beautiful themes.",
    add_completion=False,
    invoke_without_command=True,
)
console = Console()


def version_callback(value: bool):
    if value:
        console.print(f"cc-powerpoint version {__version__}")
        raise typer.Exit()


def themes_callback(value: bool):
    if value:
        table = Table(title="Available Themes")
        table.add_column("Theme", style="cyan")
        table.add_column("Description")

        for name, desc in THEMES.items():
            table.add_row(name, desc)

        console.print(table)
        raise typer.Exit()


@app.callback(invoke_without_command=True)
def main_callback(
    ctx: typer.Context,
    version: bool = typer.Option(
        False,
        "--version", "-v",
        callback=version_callback,
        is_eager=True,
        help="Show version and exit",
    ),
    themes_list: bool = typer.Option(
        False,
        "--themes",
        callback=themes_callback,
        is_eager=True,
        help="List available themes and exit",
    ),
):
    """Convert between Markdown and PowerPoint presentations with beautiful themes."""
    if ctx.invoked_subcommand is None:
        console.print("Use 'cc-powerpoint from-markdown' or 'cc-powerpoint to-markdown'. Run --help for details.")
        raise typer.Exit()


@app.command("from-markdown")
def from_markdown(
    input_file: Path = typer.Argument(
        ...,
        help="Input Markdown file with --- slide separators",
        exists=True,
        readable=True,
    ),
    output: Optional[Path] = typer.Option(
        None,
        "--output", "-o",
        help="Output .pptx file path (defaults to input filename with .pptx extension)",
    ),
    theme: str = typer.Option(
        "paper",
        "--theme", "-t",
        help="Built-in theme name",
    ),
):
    """Convert Markdown to PowerPoint presentations with beautiful themes."""

    # Validate theme
    if theme not in THEMES:
        console.print(f"[red]Error:[/red] Unknown theme '{theme}'. Use --themes to list available themes.")
        raise typer.Exit(1)

    # Default output path
    if output is None:
        output = input_file.with_suffix(".pptx")

    # Validate output extension
    if output.suffix.lower() != ".pptx":
        console.print(f"[red]Error:[/red] Output file must have .pptx extension, got '{output.suffix}'")
        raise typer.Exit(1)

    try:
        # Read input
        console.print(f"[blue]Reading:[/blue] {input_file}")
        markdown_content = input_file.read_text(encoding="utf-8")

        # Parse markdown into slides
        console.print("[blue]Parsing:[/blue] Markdown slides")
        slides = parse_markdown(markdown_content)

        if not slides:
            console.print("[red]Error:[/red] No slides found. Use --- to separate slides.")
            raise typer.Exit(1)

        console.print(f"[blue]Found:[/blue] {len(slides)} slides")

        # Load theme
        console.print(f"[blue]Theme:[/blue] {theme}")
        presentation_theme = get_theme(theme)

        # Generate PowerPoint
        console.print("[blue]Generating:[/blue] PowerPoint presentation")
        generate_pptx(
            slides=slides,
            theme=presentation_theme,
            output_path=output,
            input_dir=input_file.parent,
        )

        console.print(f"[green]Done:[/green] {output}")

    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except ValueError as e:
        console.print(f"[red]Invalid input:[/red] {e}")
        raise typer.Exit(1)
    except RuntimeError as e:
        console.print(f"[red]Generation error:[/red] {e}")
        raise typer.Exit(1)
    except OSError as e:
        console.print(f"[red]File error:[/red] {e}")
        raise typer.Exit(1)


@app.command("to-markdown")
def to_markdown(
    input_file: Path = typer.Argument(
        ...,
        help="Input PowerPoint file (.pptx)",
        exists=True,
        readable=True,
    ),
    output: Optional[Path] = typer.Option(
        None,
        "--output", "-o",
        help="Output Markdown file (defaults to input name with .md extension)",
    ),
):
    """Convert a PowerPoint presentation to Markdown, extracting images."""

    # Default output path
    if output is None:
        output = input_file.with_suffix(".md")

    # Validate output extension
    if output.suffix.lower() != ".md":
        console.print("[red]Error:[/red] Output file must have .md extension")
        raise typer.Exit(1)

    try:
        console.print(f"[blue]Reading:[/blue] {input_file}")

        console.print("[blue]Converting:[/blue] PPTX to Markdown")
        markdown = convert_pptx_to_markdown(input_file, output)

        console.print(f"[blue]Writing:[/blue] {output}")
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(markdown, encoding="utf-8")

        console.print(f"[green]Done:[/green] {output}")

    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except ValueError as e:
        console.print(f"[red]Invalid input:[/red] {e}")
        raise typer.Exit(1)
    except OSError as e:
        console.print(f"[red]File error:[/red] {e}")
        raise typer.Exit(1)


def tool_main() -> None:
    """The console-script entry point. The tool runs through the shared failure reporter (issue #3642): a
    failure is reported to the Gateway, and the exit code and the printed error stay exactly as they were."""
    from cc_shared.tool_errors import run_tool

    run_tool(app, "cc-powerpoint", app=app)


if __name__ == "__main__":
    tool_main()
