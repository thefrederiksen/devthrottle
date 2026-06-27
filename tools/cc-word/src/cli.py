"""CLI interface for cc-word using Typer."""

import sys
from pathlib import Path
from typing import Optional

import typer
from rich import box
from rich.console import Console
from rich.table import Table

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

# Handle imports for both package and frozen executable modes
try:
    from . import __version__
    from .html_generator import generate_html
    from .word_converter import convert_to_word
    from .md_converter import convert_docx_to_markdown
except ImportError:
    # Frozen executable mode - use absolute imports
    from src import __version__
    from src.html_generator import generate_html
    from src.word_converter import convert_to_word
    from src.md_converter import convert_docx_to_markdown

# Import shared modules - handle both package and frozen modes
try:
    from cc_shared.markdown_parser import parse_markdown
    from cc_shared.css_themes import get_theme_css
    from cc_shared.themes import THEMES
except ImportError:
    try:
        sys.path.insert(0, str(Path(__file__).resolve().parent.parent.parent / "cc_shared"))
        from cc_shared.markdown_parser import parse_markdown
        from cc_shared.css_themes import get_theme_css
        from cc_shared.themes import THEMES
    except ImportError:
        from markdown_parser import parse_markdown
        from css_themes import get_theme_css
        from themes import THEMES

app = typer.Typer(
    name="cc-word",
    help="Convert between Markdown and Word documents with beautiful themes.",
    add_completion=False,
    invoke_without_command=True,
)
# stdout is reserved for data; progress and status go to stderr.
console = Console()
err_console = Console(stderr=True)


def _progress(message: str, quiet: bool) -> None:
    """Print a progress/status message to stderr unless quiet is set."""
    if not quiet:
        err_console.print(message)


def _guard_output(output: Path, force: bool, no_clobber: bool, quiet: bool) -> None:
    """Refuse to silently overwrite an existing output file.

    Default behavior errors when the output exists; --force overwrites and
    --no-clobber skips (exit 0). The two flags are mutually exclusive.
    """
    if force and no_clobber:
        err_console.print("[red]Error:[/red] --force and --no-clobber cannot be used together")
        raise typer.Exit(1)
    if output.exists():
        if no_clobber:
            _progress(f"Skip: {output} already exists (--no-clobber)", quiet)
            raise typer.Exit(0)
        if not force:
            err_console.print(
                f"[red]Error:[/red] {output} already exists. "
                "Use --force to overwrite or --no-clobber to skip."
            )
            raise typer.Exit(1)


def version_callback(value: bool):
    if value:
        console.print(f"cc-word version {__version__}")
        raise typer.Exit()


def themes_callback(value: bool):
    if value:
        table = Table(title="Available Themes", box=box.ASCII)
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
    """Convert between Markdown and Word documents with beautiful themes."""
    if ctx.invoked_subcommand is None:
        console.print("Use 'cc-word from-markdown' or 'cc-word to-markdown'. Run --help for details.")
        raise typer.Exit()


@app.command("from-markdown")
def from_markdown(
    input_file: Path = typer.Argument(
        ...,
        help="Input Markdown file",
        exists=True,
        readable=True,
    ),
    output: Path = typer.Option(
        ...,
        "--output", "-o",
        help="Output Word document (.docx)",
    ),
    theme: str = typer.Option(
        "paper",
        "--theme", "-t",
        help="Built-in theme name (Word styling is theme-name-only)",
    ),
    quiet: bool = typer.Option(
        False,
        "--quiet", "-q",
        help="Suppress progress output (errors still shown)",
    ),
    force: bool = typer.Option(
        False,
        "--force", "-f",
        help="Overwrite the output file if it already exists",
    ),
    no_clobber: bool = typer.Option(
        False,
        "--no-clobber",
        help="Skip (do not overwrite) if the output file already exists",
    ),
):
    """Convert Markdown to Word documents with beautiful themes."""

    # Validate theme
    if theme not in THEMES:
        err_console.print(f"[red]Error:[/red] Unknown theme '{theme}'. Use --themes to list available themes.")
        raise typer.Exit(1)

    # Validate output extension
    if output.suffix.lower() != ".docx":
        err_console.print("[red]Error:[/red] Output file must have .docx extension")
        raise typer.Exit(1)

    # Do not silently overwrite.
    _guard_output(output, force, no_clobber, quiet)

    try:
        # Read input
        _progress(f"[blue]Reading:[/blue] {input_file}", quiet)
        markdown_content = input_file.read_text(encoding="utf-8")

        # Parse markdown
        _progress("[blue]Parsing:[/blue] Markdown", quiet)
        parsed = parse_markdown(markdown_content)

        # Generate the intermediate HTML. Word styling is applied by name in
        # convert_to_word (it does not read CSS), so no theme CSS is generated
        # here -- the styling pipeline is theme-name-only.
        _progress("[blue]Generating:[/blue] HTML", quiet)
        html_content = generate_html(parsed, "")

        # Convert to Word with theme. base_path lets relative <img> sources
        # resolve so images are embedded in the .docx.
        _progress(f"[blue]Converting:[/blue] Word document (theme '{theme}')", quiet)
        convert_to_word(html_content, output, theme_name=theme, base_path=input_file.parent)

        _progress(f"[green]Done:[/green] {output}", quiet)

    except FileNotFoundError as e:
        err_console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except ValueError as e:
        err_console.print(f"[red]Invalid input:[/red] {e}")
        raise typer.Exit(1)
    except OSError as e:
        err_console.print(f"[red]File error:[/red] {e}")
        raise typer.Exit(1)


@app.command("to-markdown")
def to_markdown(
    input_file: Path = typer.Argument(
        ...,
        help="Input Word document (.docx)",
        exists=True,
        readable=True,
    ),
    output: Optional[Path] = typer.Option(
        None,
        "--output", "-o",
        help="Output Markdown file (defaults to input name with .md extension)",
    ),
    quiet: bool = typer.Option(
        False,
        "--quiet", "-q",
        help="Suppress progress output (errors still shown)",
    ),
    force: bool = typer.Option(
        False,
        "--force", "-f",
        help="Overwrite the output file if it already exists",
    ),
    no_clobber: bool = typer.Option(
        False,
        "--no-clobber",
        help="Skip (do not overwrite) if the output file already exists",
    ),
):
    """Convert a Word document to Markdown, extracting embedded images."""

    # Default output path
    if output is None:
        output = input_file.with_suffix(".md")

    # Validate output extension
    if output.suffix.lower() != ".md":
        err_console.print("[red]Error:[/red] Output file must have .md extension")
        raise typer.Exit(1)

    # Do not silently overwrite.
    _guard_output(output, force, no_clobber, quiet)

    try:
        _progress(f"[blue]Reading:[/blue] {input_file}", quiet)

        _progress("[blue]Converting:[/blue] DOCX to Markdown", quiet)
        markdown = convert_docx_to_markdown(input_file, output)

        _progress(f"[blue]Writing:[/blue] {output}", quiet)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(markdown, encoding="utf-8")

        _progress(f"[green]Done:[/green] {output}", quiet)

    except FileNotFoundError as e:
        err_console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except ValueError as e:
        err_console.print(f"[red]Invalid input:[/red] {e}")
        raise typer.Exit(1)
    except OSError as e:
        err_console.print(f"[red]File error:[/red] {e}")
        raise typer.Exit(1)


if __name__ == "__main__":
    app()
