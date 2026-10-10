"""CLI interface for cc-docgen using Click.

Usage:
    cc-docgen generate [OPTIONS]
"""

import sys
from pathlib import Path

import click

try:
    from cc_docgen import __version__
    from cc_docgen.generator import generate_all
    from cc_docgen.schema import SchemaError, load_manifest
except ImportError:
    # When running as frozen exe, use direct imports
    __version__ = "1.0.0"
    from generator import generate_all
    from schema import SchemaError, load_manifest

# Exit codes
EXIT_SUCCESS = 0
EXIT_MANIFEST_NOT_FOUND = 1
EXIT_INVALID_YAML = 2
EXIT_MISSING_FIELD = 3
EXIT_GRAPHVIZ_NOT_INSTALLED = 4
EXIT_OUTPUT_NOT_WRITABLE = 5


@click.group()
@click.version_option(version=__version__, prog_name="cc-docgen")
def cli():
    """cc-docgen - Generate C4 architecture diagrams from CenCon manifests."""
    pass


@cli.command()
@click.option(
    "--manifest",
    "-m",
    type=click.Path(exists=False, path_type=Path),
    default=None,
    help="Path to architecture_manifest.yaml (default: ./docs/cencon/architecture_manifest.yaml)",
)
@click.option(
    "--output",
    "-o",
    type=click.Path(path_type=Path),
    default=None,
    help="Output directory for diagrams (default: ./docs/cencon/)",
)
@click.option(
    "--format",
    "-f",
    "output_format",
    type=click.Choice(["png", "svg"], case_sensitive=False),
    default="png",
    help="Output format: png or svg (default: png)",
)
@click.option(
    "--verbose",
    "-v",
    is_flag=True,
    default=False,
    help="Enable verbose output",
)
def generate(
    manifest: Path | None,
    output: Path | None,
    output_format: str,
    verbose: bool,
):
    """Generate C4 architecture diagrams from manifest.

    Reads architecture_manifest.yaml and generates context.png and container.png
    diagrams using the C4 model.

    Examples:

        # Generate with defaults
        cc-docgen generate

        # Specify manifest and output
        cc-docgen generate --manifest ./docs/cencon/architecture_manifest.yaml --output ./docs/cencon/

        # Generate SVG format
        cc-docgen generate --format svg
    """
    # Set defaults
    if manifest is None:
        manifest = Path.cwd() / "docs" / "cencon" / "architecture_manifest.yaml"

    if output is None:
        output = manifest.parent

    # Load and validate manifest
    try:
        if verbose:
            click.echo(f"Loading manifest: {manifest}")
        manifest_data = load_manifest(manifest)
    except FileNotFoundError:
        click.echo(f"ERROR: Manifest file not found: {manifest}", err=True)
        sys.exit(EXIT_MANIFEST_NOT_FOUND)
    except SchemaError as e:
        if "Invalid YAML syntax" in str(e):
            click.echo(f"ERROR: {e}", err=True)
            sys.exit(EXIT_INVALID_YAML)
        else:
            click.echo(f"ERROR: {e}", err=True)
            sys.exit(EXIT_MISSING_FIELD)

    # Check output directory is writable
    try:
        output.mkdir(parents=True, exist_ok=True)
        test_file = output / ".cc-docgen_test"
        test_file.touch()
        test_file.unlink()
    except (PermissionError, OSError) as e:
        click.echo(f"ERROR: Cannot write to output directory: {output}", err=True)
        sys.exit(EXIT_OUTPUT_NOT_WRITABLE)

    # Generate diagrams
    try:
        if verbose:
            click.echo(f"Output directory: {output}")
            click.echo(f"Output format: {output_format}")

        results = generate_all(manifest_data, output, output_format, verbose)

        click.echo(f"Generated {len(results)} diagram(s):")
        for path in results:
            click.echo(f"  {path}")

    except RuntimeError as e:
        if "Graphviz" in str(e):
            # Graceful degradation: warn but don't fail
            # INDEX.md already has placeholder text for missing diagrams
            click.echo(f"WARNING: {e}")
            click.echo("Diagrams not generated. Install Graphviz and run again.")
            click.echo("Documentation generation can proceed without diagrams.")
            sys.exit(EXIT_SUCCESS)
        raise


@cli.command()
@click.option(
    "--manifest",
    "-m",
    type=click.Path(exists=False, path_type=Path),
    default=None,
    help="Path to architecture_manifest.yaml (default: ./docs/cencon/architecture_manifest.yaml)",
)
def validate(manifest: Path | None):
    """Validate architecture_manifest.yaml schema.

    Checks that the manifest file exists and contains all required fields.
    Does not generate any diagrams.
    """
    if manifest is None:
        manifest = Path.cwd() / "docs" / "cencon" / "architecture_manifest.yaml"

    try:
        manifest_data = load_manifest(manifest)

        # Extract summary info
        project = manifest_data.get("project", {})
        actors = manifest_data.get("context", {}).get("actors", [])
        containers = manifest_data.get("containers", [])

        click.echo("Manifest validation: PASS")
        click.echo(f"  Project: {project.get('name', 'Unknown')}")
        click.echo(f"  Version: {project.get('version', 'Unknown')}")
        click.echo(f"  Actors: {len(actors)}")
        click.echo(f"  Containers: {len(containers)}")

    except FileNotFoundError:
        click.echo(f"ERROR: Manifest file not found: {manifest}", err=True)
        sys.exit(EXIT_MANIFEST_NOT_FOUND)
    except SchemaError as e:
        click.echo(f"Manifest validation: FAIL", err=True)
        click.echo(f"  {e}", err=True)
        sys.exit(EXIT_MISSING_FIELD)



def tool_main() -> None:
    """The console-script entry point. The tool runs through the shared failure reporter (issue #3642): a
    failure is reported to the Gateway, and the exit code and the printed error stay exactly as they were."""
    from cc_shared.tool_errors import run_tool

    run_tool(cli, "cc-docgen", app=cli)


if __name__ == "__main__":
    tool_main()
