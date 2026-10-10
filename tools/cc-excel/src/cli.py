"""CLI interface for cc-excel using Typer."""

import sys
from pathlib import Path
from typing import Optional

import typer
from rich.console import Console
from rich.table import Table

# Handle imports for both package and frozen executable modes
try:
    from . import __version__
    from .models import ChartSpec, ChartType, SummaryType, HighlightType
    from .parsers.csv_parser import parse_csv
    from .parsers.json_parser import parse_json
    from .parsers.markdown_parser import parse_markdown_tables
    from .type_inference import infer_types
    from .xlsx_generator import generate_xlsx
    from .spec_parser import parse_spec
    from .spec_generator import generate_from_spec
    from .themes import THEMES, get_theme
    from .md_converter import convert_xlsx_to_markdown
except ImportError:
    from src import __version__
    from src.models import ChartSpec, ChartType, SummaryType, HighlightType
    from src.parsers.csv_parser import parse_csv
    from src.parsers.json_parser import parse_json
    from src.parsers.markdown_parser import parse_markdown_tables
    from src.type_inference import infer_types
    from src.xlsx_generator import generate_xlsx
    from src.spec_parser import parse_spec
    from src.spec_generator import generate_from_spec
    from src.themes import THEMES, get_theme
    from src.md_converter import convert_xlsx_to_markdown

app = typer.Typer(
    name="cc-excel",
    help="Convert CSV, JSON, and Markdown tables to formatted Excel workbooks.",
    add_completion=False,
)
console = Console()


def version_callback(value: bool):
    if value:
        console.print(f"cc-excel version {__version__}")
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


# Global options via callback
@app.callback()
def main_callback(
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
    """Convert CSV, JSON, and Markdown tables to formatted Excel workbooks."""
    pass


def _validate_theme(theme: str) -> None:
    """Validate theme name and exit with error if unknown."""
    if theme not in THEMES:
        console.print(f"[red]Error:[/red] Unknown theme '{theme}'. Use --themes to list available themes.")
        raise typer.Exit(1)


def _validate_output(output: Path) -> None:
    """Validate output file has .xlsx extension."""
    if output.suffix.lower() != ".xlsx":
        console.print(f"[red]Error:[/red] Output file must have .xlsx extension, got '{output.suffix}'")
        raise typer.Exit(1)


def _build_chart_spec(
    chart: Optional[str],
    chart_x: Optional[str],
    chart_y: Optional[list[str]],
    columns: list,
) -> Optional[ChartSpec]:
    """Build a ChartSpec from CLI options, or return None if no chart requested."""
    if not chart:
        return None

    try:
        chart_type = ChartType(chart)
    except ValueError:
        valid = ", ".join(t.value for t in ChartType)
        console.print(f"[red]Error:[/red] Invalid chart type '{chart}'. Valid types: {valid}")
        raise typer.Exit(1)

    if chart_x is None or chart_y is None or len(chart_y) == 0:
        console.print("[red]Error:[/red] --chart-x and --chart-y are required when using --chart")
        raise typer.Exit(1)

    cat_col = _resolve_column_ref(chart_x, columns, "chart-x")
    val_cols = [_resolve_column_ref(y, columns, "chart-y") for y in chart_y]

    return ChartSpec(
        chart_type=chart_type,
        title=f"{chart_type.value.title()} Chart",
        category_column=cat_col,
        value_columns=val_cols,
    )


def _resolve_column_ref(ref: str, columns: list, option_name: str) -> int:
    """Resolve a column reference (index or name) to a 0-based index."""
    # Try as integer index first
    try:
        idx = int(ref)
        if 0 <= idx < len(columns):
            return idx
        console.print(f"[red]Error:[/red] --{option_name} index {idx} out of range (0-{len(columns) - 1})")
        raise typer.Exit(1)
    except ValueError:
        pass

    # Try as column name
    for i, col in enumerate(columns):
        if col.name.lower() == ref.lower():
            return i

    col_names = ", ".join(f"'{c.name}'" for c in columns)
    console.print(f"[red]Error:[/red] --{option_name} column '{ref}' not found. Available: {col_names}")
    raise typer.Exit(1)


@app.command()
def from_csv(
    input_file: Path = typer.Argument(
        ...,
        help="Input CSV file",
        exists=True,
        readable=True,
    ),
    output: Path = typer.Option(
        ...,
        "--output", "-o",
        help="Output .xlsx file path",
    ),
    theme: str = typer.Option(
        "paper",
        "--theme", "-t",
        help="Theme name",
    ),
    delimiter: str = typer.Option(
        ",",
        "--delimiter",
        help="CSV delimiter character",
    ),
    encoding: str = typer.Option(
        "utf-8",
        "--encoding",
        help="File encoding",
    ),
    no_header: bool = typer.Option(
        False,
        "--no-header",
        help="First row is data, not headers",
    ),
    sheet_name: Optional[str] = typer.Option(
        None,
        "--sheet-name",
        help="Worksheet tab name (defaults to filename)",
    ),
    no_autofilter: bool = typer.Option(
        False,
        "--no-autofilter",
        help="Disable autofilter on header row",
    ),
    no_freeze: bool = typer.Option(
        False,
        "--no-freeze",
        help="Disable freeze panes on header row",
    ),
    chart: Optional[str] = typer.Option(
        None,
        "--chart",
        help="Chart type: bar, line, pie, column",
    ),
    chart_x: Optional[str] = typer.Option(
        None,
        "--chart-x",
        help="Column name or index for chart categories",
    ),
    chart_y: Optional[list[str]] = typer.Option(
        None,
        "--chart-y",
        help="Column name(s) or index(es) for chart values (repeatable)",
    ),
    summary: Optional[str] = typer.Option(
        None,
        "--summary",
        help="Add summary rows: sum, avg, or all (sum+avg+min+max)",
    ),
    highlight: Optional[str] = typer.Option(
        None,
        "--highlight",
        help="Conditional formatting: best-worst or scale",
    ),
):
    """Convert a CSV file to a formatted Excel workbook."""
    _validate_theme(theme)
    _validate_output(output)
    summary_type = _parse_summary(summary)
    highlight_type = _parse_highlight(highlight)

    try:
        console.print(f"[blue]Reading:[/blue] {input_file}")
        sheet_data = parse_csv(
            input_file,
            delimiter=delimiter,
            encoding=encoding,
            has_header=not no_header,
        )

        if sheet_name:
            sheet_data.title = sheet_name

        console.print(f"[blue]Parsed:[/blue] {len(sheet_data.rows)} rows, {len(sheet_data.columns)} columns")

        console.print("[blue]Detecting:[/blue] Column types")
        sheet_data = infer_types(sheet_data)

        type_summary = ", ".join(f"{c.name}={c.col_type.value}" for c in sheet_data.columns)
        console.print(f"[blue]Types:[/blue] {type_summary}")

        excel_theme = get_theme(theme)
        chart_spec = _build_chart_spec(chart, chart_x, chart_y, sheet_data.columns)

        console.print(f"[blue]Theme:[/blue] {theme}")
        console.print("[blue]Generating:[/blue] Excel workbook")

        generate_xlsx(
            sheets=[sheet_data],
            theme=excel_theme,
            output_path=output,
            autofilter=not no_autofilter,
            freeze=not no_freeze,
            chart_spec=chart_spec,
            summary=summary_type,
            highlight=highlight_type,
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


@app.command()
def from_json(
    input_file: Path = typer.Argument(
        ...,
        help="Input JSON file",
        exists=True,
        readable=True,
    ),
    output: Path = typer.Option(
        ...,
        "--output", "-o",
        help="Output .xlsx file path",
    ),
    theme: str = typer.Option(
        "paper",
        "--theme", "-t",
        help="Theme name",
    ),
    json_path: Optional[str] = typer.Option(
        None,
        "--json-path",
        help="Dot-path or JSONPath to locate the data array (e.g. 'data' or '$.results')",
    ),
    sheet_name: Optional[str] = typer.Option(
        None,
        "--sheet-name",
        help="Worksheet tab name (defaults to filename)",
    ),
    no_autofilter: bool = typer.Option(
        False,
        "--no-autofilter",
        help="Disable autofilter on header row",
    ),
    no_freeze: bool = typer.Option(
        False,
        "--no-freeze",
        help="Disable freeze panes on header row",
    ),
    chart: Optional[str] = typer.Option(
        None,
        "--chart",
        help="Chart type: bar, line, pie, column",
    ),
    chart_x: Optional[str] = typer.Option(
        None,
        "--chart-x",
        help="Column name or index for chart categories",
    ),
    chart_y: Optional[list[str]] = typer.Option(
        None,
        "--chart-y",
        help="Column name(s) or index(es) for chart values (repeatable)",
    ),
    summary: Optional[str] = typer.Option(
        None,
        "--summary",
        help="Add summary rows: sum, avg, or all (sum+avg+min+max)",
    ),
    highlight: Optional[str] = typer.Option(
        None,
        "--highlight",
        help="Conditional formatting: best-worst or scale",
    ),
):
    """Convert a JSON file to a formatted Excel workbook."""
    _validate_theme(theme)
    _validate_output(output)
    summary_type = _parse_summary(summary)
    highlight_type = _parse_highlight(highlight)

    try:
        console.print(f"[blue]Reading:[/blue] {input_file}")
        sheet_data = parse_json(input_file, json_path=json_path)

        if sheet_name:
            sheet_data.title = sheet_name

        console.print(f"[blue]Parsed:[/blue] {len(sheet_data.rows)} rows, {len(sheet_data.columns)} columns")

        console.print("[blue]Detecting:[/blue] Column types")
        sheet_data = infer_types(sheet_data)

        type_summary = ", ".join(f"{c.name}={c.col_type.value}" for c in sheet_data.columns)
        console.print(f"[blue]Types:[/blue] {type_summary}")

        excel_theme = get_theme(theme)
        chart_spec = _build_chart_spec(chart, chart_x, chart_y, sheet_data.columns)

        console.print(f"[blue]Theme:[/blue] {theme}")
        console.print("[blue]Generating:[/blue] Excel workbook")

        generate_xlsx(
            sheets=[sheet_data],
            theme=excel_theme,
            output_path=output,
            autofilter=not no_autofilter,
            freeze=not no_freeze,
            chart_spec=chart_spec,
            summary=summary_type,
            highlight=highlight_type,
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


@app.command()
def from_markdown(
    input_file: Path = typer.Argument(
        ...,
        help="Input Markdown file containing pipe tables",
        exists=True,
        readable=True,
    ),
    output: Path = typer.Option(
        ...,
        "--output", "-o",
        help="Output .xlsx file path",
    ),
    theme: str = typer.Option(
        "paper",
        "--theme", "-t",
        help="Theme name",
    ),
    table_index: int = typer.Option(
        0,
        "--table-index",
        help="Which table to extract (0-based index)",
    ),
    all_tables: bool = typer.Option(
        False,
        "--all-tables",
        help="Extract all tables as separate sheets",
    ),
    sheet_name: Optional[str] = typer.Option(
        None,
        "--sheet-name",
        help="Worksheet tab name (defaults to 'Table')",
    ),
    no_autofilter: bool = typer.Option(
        False,
        "--no-autofilter",
        help="Disable autofilter on header row",
    ),
    no_freeze: bool = typer.Option(
        False,
        "--no-freeze",
        help="Disable freeze panes on header row",
    ),
    summary: Optional[str] = typer.Option(
        None,
        "--summary",
        help="Add summary rows: sum, avg, or all (sum+avg+min+max)",
    ),
    highlight: Optional[str] = typer.Option(
        None,
        "--highlight",
        help="Conditional formatting: best-worst or scale",
    ),
):
    """Convert Markdown pipe tables to a formatted Excel workbook."""
    _validate_theme(theme)
    _validate_output(output)
    summary_type = _parse_summary(summary)
    highlight_type = _parse_highlight(highlight)

    try:
        console.print(f"[blue]Reading:[/blue] {input_file}")
        sheets = parse_markdown_tables(
            input_file,
            table_index=table_index,
            all_tables=all_tables,
        )

        if sheet_name and len(sheets) == 1:
            sheets[0].title = sheet_name

        total_rows = sum(len(s.rows) for s in sheets)
        console.print(f"[blue]Parsed:[/blue] {len(sheets)} table(s), {total_rows} total rows")

        console.print("[blue]Detecting:[/blue] Column types")
        for sheet_data in sheets:
            infer_types(sheet_data)

        excel_theme = get_theme(theme)

        console.print(f"[blue]Theme:[/blue] {theme}")
        console.print("[blue]Generating:[/blue] Excel workbook")

        generate_xlsx(
            sheets=sheets,
            theme=excel_theme,
            output_path=output,
            autofilter=not no_autofilter,
            freeze=not no_freeze,
            summary=summary_type,
            highlight=highlight_type,
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


@app.command()
def from_spec(
    input_file: Path = typer.Argument(
        ...,
        help="Input JSON workbook specification file",
        exists=True,
        readable=True,
    ),
    output: Path = typer.Option(
        ...,
        "--output", "-o",
        help="Output .xlsx file path",
    ),
    theme: str = typer.Option(
        None,
        "--theme", "-t",
        help="Theme name (overrides spec theme if set)",
    ),
):
    """Generate a multi-sheet Excel workbook from a JSON spec file."""
    _validate_output(output)

    try:
        console.print(f"[blue]Reading:[/blue] {input_file}")
        spec = parse_spec(input_file)

        # Theme priority: CLI --theme > spec theme > default "paper"
        effective_theme = theme or spec.theme or "paper"
        _validate_theme(effective_theme)

        sheet_count = len(spec.sheets)
        total_rows = sum(len(s.rows) for s in spec.sheets)
        console.print(f"[blue]Parsed:[/blue] {sheet_count} sheet(s), {total_rows} total rows")

        if spec.named_ranges:
            console.print(f"[blue]Named ranges:[/blue] {len(spec.named_ranges)}")

        excel_theme = get_theme(effective_theme)

        console.print(f"[blue]Theme:[/blue] {effective_theme}")
        console.print("[blue]Generating:[/blue] Excel workbook")

        generate_from_spec(
            spec=spec,
            theme=excel_theme,
            output_path=output,
        )

        console.print(f"[green]Done:[/green] {output}")

    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except ValueError as e:
        console.print(f"[red]Invalid spec:[/red] {e}")
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
        help="Input .xlsx file",
        exists=True,
        readable=True,
    ),
    output: Optional[Path] = typer.Option(
        None,
        "--output", "-o",
        help="Output Markdown file (defaults to input name with .md extension)",
    ),
    sheet_name: Optional[str] = typer.Option(
        None,
        "--sheet-name",
        help="Convert a specific sheet by name",
    ),
    all_sheets: bool = typer.Option(
        False,
        "--all-sheets",
        help="Convert all sheets (default: first sheet only)",
    ),
):
    """Convert an Excel workbook to Markdown pipe tables."""

    # Default output path
    if output is None:
        output = input_file.with_suffix(".md")

    # Validate output extension
    if output.suffix.lower() != ".md":
        console.print("[red]Error:[/red] Output file must have .md extension")
        raise typer.Exit(1)

    try:
        console.print(f"[blue]Reading:[/blue] {input_file}")
        markdown = convert_xlsx_to_markdown(
            input_file,
            sheet_name=sheet_name,
            all_sheets=all_sheets,
        )

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


def _parse_summary(value: Optional[str]) -> Optional[SummaryType]:
    """Parse --summary CLI option into SummaryType enum."""
    if value is None:
        return None
    try:
        return SummaryType(value)
    except ValueError:
        valid = ", ".join(t.value for t in SummaryType)
        console.print(f"[red]Error:[/red] Invalid summary type '{value}'. Valid types: {valid}")
        raise typer.Exit(1)


def _parse_highlight(value: Optional[str]) -> Optional[HighlightType]:
    """Parse --highlight CLI option into HighlightType enum."""
    if value is None:
        return None
    try:
        return HighlightType(value)
    except ValueError:
        valid = ", ".join(t.value for t in HighlightType)
        console.print(f"[red]Error:[/red] Invalid highlight type '{value}'. Valid types: {valid}")
        raise typer.Exit(1)


def tool_main() -> None:
    """The console-script entry point. The tool runs through the shared failure reporter (issue #3642): a
    failure is reported to the Gateway, and the exit code and the printed error stay exactly as they were."""
    from cc_shared.tool_errors import run_tool

    run_tool(app, "cc-excel", app=app)


if __name__ == "__main__":
    tool_main()
