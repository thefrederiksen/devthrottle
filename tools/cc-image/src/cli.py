"""CLI for cc-image - unified image toolkit."""

from pathlib import Path
from typing import Optional

import requests
import typer
from PIL import UnidentifiedImageError
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

try:
    from . import __version__
    from .manipulation import image_info, resize, convert
    from .vision import describe, extract_text
    from .generation import generate_to_file, GenerationNotAvailableError
    from .devthrottle import DevThrottleConfigError
    from .batch import catalog_folder, ImageRecord
except ImportError:
    from src import __version__
    from src.manipulation import image_info, resize, convert
    from src.vision import describe, extract_text
    from src.generation import generate_to_file, GenerationNotAvailableError
    from src.devthrottle import DevThrottleConfigError
    from src.batch import catalog_folder, ImageRecord

app = typer.Typer(
    name="cc-image",
    help="Unified image toolkit: generate, analyze, OCR, resize, convert.",
    add_completion=False,
)
console = Console()


def version_callback(value: bool):
    if value:
        console.print(f"cc-image version {__version__}")
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
    """Unified image toolkit."""
    pass


@app.command()
def info(
    image: Path = typer.Argument(..., help="Image file", exists=True),
):
    """Show image metadata."""
    data = image_info(image)
    table = Table(title=f"Image Info: {data['path']}")
    table.add_column("Property", style="cyan")
    table.add_column("Value")

    table.add_row("Dimensions", f"{data['width']} x {data['height']}")
    table.add_row("Format", data['format'] or "Unknown")
    table.add_row("Mode", data['mode'])
    table.add_row("Size", f"{data['size_bytes'] / 1024:.1f} KB")

    console.print(table)


@app.command("resize")
def resize_cmd(
    image: Path = typer.Argument(..., help="Input image", exists=True),
    output: Path = typer.Option(..., "-o", "--output", help="Output path"),
    width: Optional[int] = typer.Option(None, "-w", "--width", help="Target width"),
    height: Optional[int] = typer.Option(None, "-h", "--height", help="Target height"),
    quality: int = typer.Option(95, "-q", "--quality", help="JPEG quality (1-100)"),
):
    """Resize an image."""
    if width is None and height is None:
        console.print("[red]Error:[/red] Specify --width or --height")
        raise typer.Exit(1)

    try:
        result = resize(image, output, width=width, height=height, quality=quality)
        info = image_info(result)
        console.print(f"[green]Resized:[/green] {result}")
        console.print(f"[cyan]New size:[/cyan] {info['width']} x {info['height']}")
    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except ValueError as e:
        console.print(f"[red]Invalid argument:[/red] {e}")
        raise typer.Exit(1)
    except UnidentifiedImageError:
        console.print(f"[red]Error:[/red] Cannot open image file - unsupported or corrupted format")
        raise typer.Exit(1)
    except OSError as e:
        console.print(f"[red]File error:[/red] {e}")
        raise typer.Exit(1)


@app.command("convert")
def convert_cmd(
    image: Path = typer.Argument(..., help="Input image", exists=True),
    output: Path = typer.Option(..., "-o", "--output", help="Output path (format from extension)"),
    quality: int = typer.Option(95, "-q", "--quality", help="JPEG quality (1-100)"),
):
    """Convert image format."""
    try:
        result = convert(image, output, quality=quality)
        console.print(f"[green]Converted:[/green] {result}")
    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except UnidentifiedImageError:
        console.print(f"[red]Error:[/red] Cannot open image file - unsupported or corrupted format")
        raise typer.Exit(1)
    except OSError as e:
        console.print(f"[red]File error:[/red] {e}")
        raise typer.Exit(1)


ENGINE_DISPLAY = {
    "devthrottle": "DevThrottle",
    "openai": "OpenAI",
    "claude_code": "Claude Code",
}


@app.command("describe")
def describe_cmd(
    path: Path = typer.Argument(..., help="Image file OR folder (with --recursive)", exists=True),
    engine: str = typer.Option(
        "devthrottle",
        "--engine", "-e",
        help="AI engine: devthrottle (default), openai, or claude_code",
    ),
    recursive: bool = typer.Option(
        False, "--recursive", "-r",
        help="Treat the path as a folder and describe every image in it (batch mode)",
    ),
    output: Optional[Path] = typer.Option(
        None, "--output", "-o",
        help="Batch results file (JSON + CSV written alongside). Default: <folder>/cc-image-catalog",
    ),
    fmt: str = typer.Option(
        "both", "--format", "-f",
        help="Batch output format: json, csv, or both",
    ),
    workers: int = typer.Option(
        4, "--workers", "-w",
        help="Batch concurrency (parallel images)",
    ),
):
    """Get an AI description of an image, or catalog a whole folder with --recursive."""
    if path.is_dir() or recursive:
        _describe_folder(path, output, fmt, workers)
        return

    try:
        console.print(f"[blue]Analyzing image with {ENGINE_DISPLAY.get(engine, engine)}...[/blue]")
        result = describe(path, engine=engine)
        console.print(f"\n{result}")
    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except DevThrottleConfigError as e:
        console.print(f"[red]Configuration error:[/red] {e}")
        raise typer.Exit(1)
    except RuntimeError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except ValueError as e:
        console.print(f"[red]Configuration error:[/red] {e}")
        raise typer.Exit(1)


def _describe_folder(folder: Path, output: Optional[Path], fmt: str, workers: int) -> None:
    """Batch mode: catalog every image in a folder to JSON/CSV with resume support."""
    if not folder.is_dir():
        console.print(f"[red]Error:[/red] --recursive expects a folder, got: {folder}")
        raise typer.Exit(1)

    fmt = fmt.lower()
    if fmt not in ("json", "csv", "both"):
        console.print("[red]Error:[/red] --format must be one of: json, csv, both")
        raise typer.Exit(1)
    write_json = fmt in ("json", "both")
    write_csv = fmt in ("csv", "both")

    output = output or (folder / "cc-image-catalog")

    def on_progress(done: int, total: int, record: ImageRecord) -> None:
        name = Path(record.path).name
        if record.error:
            console.print(f"[yellow][{done}/{total}][/yellow] [red]FAILED[/red] {name}: {record.error}")
        else:
            preview = record.description.replace("\n", " ")[:70]
            console.print(f"[green][{done}/{total}][/green] {name}: {preview}")

    try:
        console.print(f"[blue]Cataloging images in[/blue] {folder} [blue](recursive)...[/blue]")
        result = catalog_folder(
            folder,
            output_path=output,
            recursive=True,
            workers=workers,
            write_json=write_json,
            write_csv=write_csv,
            on_progress=on_progress,
        )
    except DevThrottleConfigError as e:
        console.print(f"[red]Configuration error:[/red] {e}")
        raise typer.Exit(1)
    except (NotADirectoryError, OSError) as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)

    console.print(
        f"\n[green]Done.[/green] {result.total} images "
        f"({result.processed} described, {result.skipped} skipped, {result.failed} failed)."
    )
    for out in result.output_paths:
        console.print(f"[cyan]Wrote:[/cyan] {out}")


@app.command("ocr")
def ocr_cmd(
    image: Path = typer.Argument(..., help="Image with text", exists=True),
    engine: str = typer.Option(
        "devthrottle",
        "--engine", "-e",
        help="AI engine: devthrottle (default), openai, or claude_code",
    ),
):
    """Extract text from an image (OCR)."""
    try:
        console.print(f"[blue]Extracting text with {ENGINE_DISPLAY.get(engine, engine)}...[/blue]")
        result = extract_text(image, engine=engine)
        console.print(f"\n{result}")
    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except DevThrottleConfigError as e:
        console.print(f"[red]Configuration error:[/red] {e}")
        raise typer.Exit(1)
    except RuntimeError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except ValueError as e:
        console.print(f"[red]Configuration error:[/red] {e}")
        raise typer.Exit(1)


@app.command("generate")
def generate_cmd(
    prompt: str = typer.Argument(..., help="Image description"),
    output: Path = typer.Option(..., "-o", "--output", help="Output path"),
    size: str = typer.Option("1024x1024", "-s", "--size", help="Size: 1024x1024, 1024x1792, 1792x1024"),
    quality: str = typer.Option("standard", "-q", "--quality", help="Quality: standard, hd"),
):
    """Generate an image from a prompt (DEFERRED - DevThrottle images endpoint not built yet)."""
    try:
        console.print(f"[blue]Generating:[/blue] {prompt[:50]}...")
        result = generate_to_file(prompt, output, size=size, quality=quality)
        console.print(f"[green]Generated:[/green] {result}")
    except GenerationNotAvailableError as e:
        console.print(f"[yellow]Not available yet:[/yellow] {e}")
        raise typer.Exit(2)
    except RuntimeError as e:
        console.print(f"[red]API error:[/red] {e}")
        raise typer.Exit(1)
    except requests.RequestException as e:
        console.print(f"[red]Network error:[/red] {e}")
        raise typer.Exit(1)
    except OSError as e:
        console.print(f"[red]File error:[/red] {e}")
        raise typer.Exit(1)


def tool_main() -> None:
    """The console-script entry point. The tool runs through the shared failure reporter (issue #3642): a
    failure is reported to the Gateway, and the exit code and the printed error stay exactly as they were."""
    from cc_shared.tool_errors import run_tool

    run_tool(app, "cc-image", app=app)


if __name__ == "__main__":
    tool_main()
