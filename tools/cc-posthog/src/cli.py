"""cc-posthog CLI -- PostHog analytics from the command line."""

import sys
from typing import Optional

import typer
from rich.console import Console

try:
    from . import __version__
    from .config import PostHogConfig, ProjectConfig, get_project, load_config, save_config
    from .formatters import format_report_json, format_table, print_error, print_info
    from .posthog_api import PostHogClient, PostHogError
    from .schema import AnalyticsReport
    from .time_range import validate_range
except ImportError:
    from src import __version__
    from src.config import PostHogConfig, ProjectConfig, get_project, load_config, save_config
    from src.formatters import format_report_json, format_table, print_error, print_info
    from src.posthog_api import PostHogClient, PostHogError
    from src.schema import AnalyticsReport
    from src.time_range import validate_range

console = Console()

app = typer.Typer(
    name="cc-posthog",
    help="PostHog analytics CLI for querying page views, funnels, and events.",
    add_completion=False,
)
export_app = typer.Typer(help="Export analytics data")
app.add_typer(export_app, name="export")


def version_callback(value: bool) -> None:
    if value:
        console.print(f"cc-posthog {__version__}")
        raise typer.Exit()


# -- Global options --

ProjectOpt = typer.Option(None, "--project", "-p", help="Project name (uses default if omitted)")
LastOpt = typer.Option("7d", "--last", "-l", callback=validate_range, help="Time range: 7d, 30d, 90d, 1y")
JsonOpt = typer.Option(False, "--json", "-j", help="Output as JSON")
CsvOpt = typer.Option(False, "--csv", help="Output as CSV")
LimitOpt = typer.Option(20, "--count", "-n", help="Number of results")


def _make_client(project: Optional[str] = None) -> tuple[PostHogClient, str]:
    """Create a PostHogClient from config. Returns (client, project_name)."""
    config = load_config()
    name, proj = get_project(config, project)
    return PostHogClient(config.api_key, proj.project_id, proj.host), name


def _get_funnel_steps(project: Optional[str] = None, events: Optional[str] = None) -> list[str]:
    """Resolve funnel steps from --events flag or project config."""
    if events:
        return [e.strip() for e in events.split(",")]

    config = load_config()
    _, proj = get_project(config, project)
    if proj.funnel_steps:
        return proj.funnel_steps

    print_error(
        "No funnel steps configured. Use --events flag or set funnel_steps in config.\n"
        "Example: cc-posthog funnel --events 'homepage_viewed,course_page_viewed,registration_form_submitted'"
    )
    raise typer.Exit(1)


# ==================== COMMANDS ====================


@app.command()
def init() -> None:
    """Configure PostHog API key and add a project."""
    config = load_config()

    # API key
    if config.api_key:
        print_info(f"Current API key: {config.api_key[:8]}...{config.api_key[-4:]}")
        change = typer.confirm("Change API key?", default=False)
        if change:
            config.api_key = typer.prompt("PostHog Personal API Key")
    else:
        config.api_key = typer.prompt("PostHog Personal API Key")

    # Add project
    name = typer.prompt("Project name (e.g. 'myproject')")
    project_id = typer.prompt("PostHog Project ID", type=int)
    host = typer.prompt("PostHog host", default="https://us.posthog.com")
    funnel_input = typer.prompt(
        "Funnel event names (comma-separated, or leave blank)",
        default="",
    )
    funnel_steps = [s.strip() for s in funnel_input.split(",") if s.strip()]

    config.projects[name] = ProjectConfig(
        project_id=project_id,
        host=host,
        funnel_steps=funnel_steps,
    )

    # Set default
    if not config.default_project or len(config.projects) == 1:
        config.default_project = name
    elif typer.confirm(f"Set '{name}' as default project?", default=False):
        config.default_project = name

    save_config(config)
    print_info(f"Project '{name}' saved. Default: {config.default_project}")


@app.command()
def projects() -> None:
    """List configured PostHog projects."""
    config = load_config()
    if not config.projects:
        print_error("No projects configured. Run 'cc-posthog init' first.")
        raise typer.Exit(1)

    columns = ["Name", "Project ID", "Host", "Default"]
    rows = []
    for name, proj in config.projects.items():
        is_default = "Yes" if name == config.default_project else ""
        rows.append([name, proj.project_id, proj.host, is_default])

    format_table("PostHog Projects", columns, rows)


@app.command()
def status(
    project: Optional[str] = ProjectOpt,
) -> None:
    """Show PostHog project status and connection health."""
    try:
        client, name = _make_client(project)
        with client:
            info = client.get_status()
    except PostHogError as e:
        print_error(str(e))
        raise typer.Exit(1)

    columns = ["Property", "Value"]
    rows = [
        ["Project", info.project_name],
        ["Project ID", info.project_id],
        ["Host", info.host],
        ["Events (30d)", f"{info.event_count:,}" if info.event_count else "0"],
        ["Recordings (30d)", f"{info.recording_count:,}" if info.recording_count else "0"],
    ]
    format_table(f"Status: {name}", columns, rows)


@app.command()
def views(
    project: Optional[str] = ProjectOpt,
    last: str = LastOpt,
    limit: int = LimitOpt,
    json_out: bool = JsonOpt,
    csv_out: bool = CsvOpt,
) -> None:
    """Show page view counts by URL."""
    try:
        client, name = _make_client(project)
        with client:
            data = client.get_views(last=last, limit=limit)
    except PostHogError as e:
        print_error(str(e))
        raise typer.Exit(1)

    columns = ["Page", "Views", "Unique Visitors"]
    rows = [[d.page, d.views, d.unique_visitors] for d in data]
    format_table(f"Page Views ({last}) - {name}", columns, rows, as_json=json_out, as_csv=csv_out)


@app.command()
def sources(
    project: Optional[str] = ProjectOpt,
    last: str = LastOpt,
    limit: int = LimitOpt,
    json_out: bool = JsonOpt,
    csv_out: bool = CsvOpt,
) -> None:
    """Show traffic sources."""
    try:
        client, name = _make_client(project)
        with client:
            data = client.get_sources(last=last, limit=limit)
    except PostHogError as e:
        print_error(str(e))
        raise typer.Exit(1)

    columns = ["Source", "Count", "Percentage"]
    rows = [[d.source, d.count, f"{d.percentage}%"] for d in data]
    format_table(f"Traffic Sources ({last}) - {name}", columns, rows, as_json=json_out, as_csv=csv_out)


@app.command()
def visitors(
    project: Optional[str] = ProjectOpt,
    last: str = LastOpt,
    json_out: bool = JsonOpt,
    csv_out: bool = CsvOpt,
) -> None:
    """Show daily unique visitor counts."""
    try:
        client, name = _make_client(project)
        with client:
            data = client.get_visitors(last=last)
    except PostHogError as e:
        print_error(str(e))
        raise typer.Exit(1)

    columns = ["Date", "Visitors"]
    rows = [[d.date, d.visitors] for d in data]
    format_table(f"Daily Visitors ({last}) - {name}", columns, rows, as_json=json_out, as_csv=csv_out)


@app.command()
def pages(
    project: Optional[str] = ProjectOpt,
    last: str = LastOpt,
    limit: int = LimitOpt,
    json_out: bool = JsonOpt,
    csv_out: bool = CsvOpt,
) -> None:
    """Show page rankings by path."""
    try:
        client, name = _make_client(project)
        with client:
            data = client.get_pages(last=last, limit=limit)
    except PostHogError as e:
        print_error(str(e))
        raise typer.Exit(1)

    columns = ["Page", "Views", "Unique Visitors"]
    rows = [[d.page, d.views, d.unique_visitors] for d in data]
    format_table(f"Top Pages ({last}) - {name}", columns, rows, as_json=json_out, as_csv=csv_out)


@app.command()
def funnel(
    project: Optional[str] = ProjectOpt,
    last: str = typer.Option("30d", "--last", "-l", callback=validate_range, help="Time range"),
    events: Optional[str] = typer.Option(None, "--events", "-e", help="Comma-separated event names for funnel steps"),
    json_out: bool = JsonOpt,
    csv_out: bool = CsvOpt,
) -> None:
    """Show conversion funnel analysis."""
    steps = _get_funnel_steps(project, events)
    try:
        client, name = _make_client(project)
        with client:
            data = client.get_funnel(last=last, steps=steps)
    except PostHogError as e:
        print_error(str(e))
        raise typer.Exit(1)

    columns = ["Step", "Event", "Count", "Rate", "Drop-off", "Drop-off Rate"]
    rows = [
        [d.step, d.event, d.count, f"{d.conversion_rate}%", d.drop_off, f"{d.drop_off_rate}%"]
        for d in data
    ]
    format_table(f"Funnel ({last}) - {name}", columns, rows, as_json=json_out, as_csv=csv_out)


@app.command()
def events(
    project: Optional[str] = ProjectOpt,
    last: str = LastOpt,
    event_name: Optional[str] = typer.Option(None, "--event", help="Filter by event name"),
    limit: int = LimitOpt,
    json_out: bool = JsonOpt,
    csv_out: bool = CsvOpt,
) -> None:
    """Show recent events."""
    try:
        client, name = _make_client(project)
        with client:
            data = client.get_events(last=last, event_name=event_name, limit=limit)
    except PostHogError as e:
        print_error(str(e))
        raise typer.Exit(1)

    columns = ["Timestamp", "Event", "Person", "URL"]
    rows = [[d.timestamp, d.event, d.person, d.url] for d in data]
    format_table(f"Events ({last}) - {name}", columns, rows, as_json=json_out, as_csv=csv_out)


@app.command("event-counts")
def event_counts(
    project: Optional[str] = ProjectOpt,
    last: str = LastOpt,
    limit: int = LimitOpt,
    json_out: bool = JsonOpt,
    csv_out: bool = CsvOpt,
) -> None:
    """Show event counts by name."""
    try:
        client, name = _make_client(project)
        with client:
            data = client.get_event_counts(last=last, limit=limit)
    except PostHogError as e:
        print_error(str(e))
        raise typer.Exit(1)

    columns = ["Event", "Count"]
    rows = [[d.event, d.count] for d in data]
    format_table(f"Event Counts ({last}) - {name}", columns, rows, as_json=json_out, as_csv=csv_out)


@app.command()
def recordings(
    project: Optional[str] = ProjectOpt,
    last: str = LastOpt,
    limit: int = LimitOpt,
    json_out: bool = JsonOpt,
    csv_out: bool = CsvOpt,
) -> None:
    """List session recordings."""
    try:
        client, name = _make_client(project)
        with client:
            data = client.get_recordings(last=last, limit=limit)
    except PostHogError as e:
        print_error(str(e))
        raise typer.Exit(1)

    columns = ["ID", "Start", "Duration", "Pages", "Clicks"]
    rows = [[d.id, d.start_time, f"{d.duration_seconds}s", d.pages_visited, d.click_count] for d in data]
    format_table(f"Recordings ({last}) - {name}", columns, rows, as_json=json_out, as_csv=csv_out)


@app.command()
def recording(
    recording_id: str = typer.Argument(..., help="Session recording ID"),
    project: Optional[str] = ProjectOpt,
    json_out: bool = JsonOpt,
    csv_out: bool = CsvOpt,
) -> None:
    """Show events within a specific session recording."""
    try:
        client, _ = _make_client(project)
        with client:
            data = client.get_recording(recording_id)
    except PostHogError as e:
        print_error(str(e))
        raise typer.Exit(1)

    columns = ["Timestamp", "Event", "Properties"]
    rows = [[d.timestamp, d.event, str(d.properties)] for d in data]
    format_table(f"Recording: {recording_id}", columns, rows, as_json=json_out, as_csv=csv_out)


@app.command()
def report(
    project: Optional[str] = ProjectOpt,
    last: str = typer.Option("30d", "--last", "-l", callback=validate_range, help="Time range"),
    json_out: bool = JsonOpt,
) -> None:
    """Generate a comprehensive analytics report."""
    try:
        client, name = _make_client(project)
        with client:
            rpt = AnalyticsReport(project=name, period=last)
            rpt.status = client.get_status()
            rpt.views = client.get_views(last=last, limit=20)
            rpt.sources = client.get_sources(last=last, limit=20)
            rpt.visitors = client.get_visitors(last=last)
            rpt.event_counts = client.get_event_counts(last=last, limit=30)

            # Try funnel if steps configured
            config = load_config()
            _, proj = get_project(config, project)
            if proj.funnel_steps:
                rpt.funnel = client.get_funnel(last=last, steps=proj.funnel_steps)
    except PostHogError as e:
        print_error(str(e))
        raise typer.Exit(1)

    if json_out:
        format_report_json(rpt)
    else:
        # Print each section as a table
        if rpt.status:
            format_table(f"Status: {name}", ["Property", "Value"], [
                ["Project", rpt.status.project_name],
                ["Events (30d)", f"{rpt.status.event_count:,}" if rpt.status.event_count else "0"],
                ["Recordings (30d)", f"{rpt.status.recording_count:,}" if rpt.status.recording_count else "0"],
            ])
            console.print()

        if rpt.views:
            format_table(f"Top Pages ({last})", ["Page", "Views", "Unique Visitors"],
                         [[v.page, v.views, v.unique_visitors] for v in rpt.views])
            console.print()

        if rpt.sources:
            format_table(f"Traffic Sources ({last})", ["Source", "Count", "Percentage"],
                         [[s.source, s.count, f"{s.percentage}%"] for s in rpt.sources])
            console.print()

        if rpt.visitors:
            format_table(f"Daily Visitors ({last})", ["Date", "Visitors"],
                         [[v.date, v.visitors] for v in rpt.visitors])
            console.print()

        if rpt.funnel:
            format_table(f"Funnel ({last})", ["Step", "Event", "Count", "Rate", "Drop-off"],
                         [[f.step, f.event, f.count, f"{f.conversion_rate}%", f.drop_off] for f in rpt.funnel])
            console.print()

        if rpt.event_counts:
            format_table(f"Event Counts ({last})", ["Event", "Count"],
                         [[e.event, e.count] for e in rpt.event_counts])


@app.command()
def compare(
    metric: str = typer.Argument(..., help="Metric to compare: views, sources, visitors, event-counts"),
    project_names: str = typer.Option(..., "--projects", help="Comma-separated project names"),
    last: str = LastOpt,
    json_out: bool = JsonOpt,
) -> None:
    """Compare a metric across multiple projects side-by-side."""
    names = [n.strip() for n in project_names.split(",")]
    config = load_config()

    all_results = {}
    for name in names:
        _, proj = get_project(config, name)
        client = PostHogClient(config.api_key, proj.project_id, proj.host)
        with client:
            if metric == "views":
                data = client.get_views(last=last, limit=10)
                all_results[name] = {d.page: d.views for d in data}
            elif metric == "sources":
                data = client.get_sources(last=last, limit=10)
                all_results[name] = {d.source: d.count for d in data}
            elif metric == "visitors":
                data = client.get_visitors(last=last)
                all_results[name] = {d.date: d.visitors for d in data}
            elif metric == "event-counts":
                data = client.get_event_counts(last=last, limit=10)
                all_results[name] = {d.event: d.count for d in data}
            else:
                print_error(f"Unknown metric '{metric}'. Use: views, sources, visitors, event-counts")
                raise typer.Exit(1)

    if json_out:
        import json
        console.print(json.dumps(all_results, indent=2, default=str))
        return

    # Build comparison table
    all_keys = []
    for results in all_results.values():
        for key in results:
            if key not in all_keys:
                all_keys.append(key)

    columns = ["Key"] + names
    rows = []
    for key in all_keys:
        row = [key]
        for name in names:
            row.append(all_results.get(name, {}).get(key, 0))
        rows.append(row)

    format_table(f"Compare: {metric} ({last})", columns, rows)


# -- Export subcommands --

@export_app.command("events")
def export_events(
    project: Optional[str] = ProjectOpt,
    last: str = LastOpt,
    event_name: Optional[str] = typer.Option(None, "--event", help="Filter by event name"),
    limit: int = typer.Option(1000, "--count", "-n", help="Number of results"),
    json_out: bool = JsonOpt,
    csv_out: bool = CsvOpt,
) -> None:
    """Export raw events data."""
    try:
        client, name = _make_client(project)
        with client:
            data = client.get_events(last=last, event_name=event_name, limit=limit)
    except PostHogError as e:
        print_error(str(e))
        raise typer.Exit(1)

    columns = ["Timestamp", "Event", "Person", "URL"]
    rows = [[d.timestamp, d.event, d.person, d.url] for d in data]
    # Default to JSON for export
    if not csv_out:
        json_out = True
    format_table(f"Events Export ({last})", columns, rows, as_json=json_out, as_csv=csv_out)


@export_app.command("funnel")
def export_funnel(
    project: Optional[str] = ProjectOpt,
    last: str = typer.Option("30d", "--last", "-l", callback=validate_range, help="Time range"),
    events_flag: Optional[str] = typer.Option(None, "--events", "-e", help="Comma-separated funnel steps"),
    json_out: bool = JsonOpt,
    csv_out: bool = CsvOpt,
) -> None:
    """Export funnel data."""
    steps = _get_funnel_steps(project, events_flag)
    try:
        client, name = _make_client(project)
        with client:
            data = client.get_funnel(last=last, steps=steps)
    except PostHogError as e:
        print_error(str(e))
        raise typer.Exit(1)

    columns = ["Step", "Event", "Count", "Rate", "Drop-off", "Drop-off Rate"]
    rows = [
        [d.step, d.event, d.count, f"{d.conversion_rate}%", d.drop_off, f"{d.drop_off_rate}%"]
        for d in data
    ]
    if not csv_out:
        json_out = True
    format_table(f"Funnel Export ({last})", columns, rows, as_json=json_out, as_csv=csv_out)


# -- Main callback for global flags --

@app.callback(invoke_without_command=True)
def main(
    version: bool = typer.Option(False, "--version", "-V", callback=version_callback, is_eager=True,
                                 help="Show version"),
    verbose: bool = typer.Option(False, "--verbose", "-v", help="Verbose output"),
) -> None:
    """PostHog analytics CLI for querying page views, funnels, and events."""
    pass


def tool_main() -> None:
    """The console-script entry point. The tool runs through the shared failure reporter (issue #3642): a
    failure is reported to the Gateway, and the exit code and the printed error stay exactly as they were."""
    from cc_shared.tool_errors import run_tool

    run_tool(app, "cc-posthog", app=app)
