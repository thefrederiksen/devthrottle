"""CLI interface for cc-hardware."""

import json
from typing import Any

import typer
from rich.console import Console

from . import __version__
from . import hardware

app = typer.Typer(
    name="cc-hardware",
    help="Query system hardware information.",
    no_args_is_help=False,
)
console = Console()


def version_callback(value: bool) -> None:
    if value:
        console.print(f"cc-hardware v{__version__}")
        raise typer.Exit()


def _display_os(info: dict[str, Any]) -> None:
    """Display OS information."""
    console.print("\n[bold]Operating System[/bold]")
    console.print(f"  System:       {info['system']} {info['release']}")
    console.print(f"  Version:      {info['version']}")
    console.print(f"  Architecture: {info['architecture']}")
    console.print(f"  Hostname:     {info['hostname']}")


def _display_cpu(info: dict[str, Any]) -> None:
    """Display CPU information."""
    console.print("\n[bold]CPU[/bold]")
    console.print(f"  Model:        {info['model']}")
    console.print(f"  Cores:        {info['physical_cores']} physical, {info['logical_cores']} logical")
    console.print(f"  Usage:        {info['usage_percent']}%")


def _display_ram(info: dict[str, Any]) -> None:
    """Display RAM information."""
    console.print("\n[bold]RAM[/bold]")
    console.print(f"  Total:        {info['total_gb']} GB")
    console.print(f"  Used:         {info['used_gb']} GB ({info['percent']}%)")
    console.print(f"  Available:    {info['available_gb']} GB")


def _display_gpu(info: list[dict[str, Any]]) -> None:
    """Display GPU information."""
    console.print("\n[bold]GPU[/bold]")
    if info:
        for gpu in info:
            console.print(f"  [{gpu['id']}] {gpu['name']}")
            console.print(f"      Memory:   {gpu['used_mb']:.0f} / {gpu['total_mb']:.0f} MB ({gpu['free_mb']:.0f} MB free)")
            console.print(f"      Load:     {gpu['load_percent']}%")
            if gpu.get('temperature_c'):
                console.print(f"      Temp:     {gpu['temperature_c']}C")
    else:
        console.print("  No NVIDIA GPU detected")


def _display_disk(info: list[dict[str, Any]]) -> None:
    """Display disk information."""
    console.print("\n[bold]Disk[/bold]")
    for disk in info:
        console.print(f"  {disk['device']} ({disk['fstype']})")
        console.print(f"      {disk['used_gb']} / {disk['total_gb']} GB ({disk['percent']}% used, {disk['free_gb']} GB free)")


def _display_battery(info: dict[str, Any] | None) -> None:
    """Display battery information if present."""
    if info:
        console.print("\n[bold]Battery[/bold]")
        status = "Plugged in" if info['plugged_in'] else "On battery"
        console.print(f"  Status:       {status}")
        console.print(f"  Charge:       {info['percent']}%")
        if info['time_remaining']:
            console.print(f"  Time left:    {info['time_remaining']}")


@app.callback(invoke_without_command=True)
def main(
    ctx: typer.Context,
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON"),
    version: bool = typer.Option(None, "--version", "-v", callback=version_callback, help="Show version"),
) -> None:
    """Show all hardware information (default command)."""
    if ctx.invoked_subcommand is None:
        all_info = hardware.get_all_info()

        if json_output:
            console.print(json.dumps(all_info, indent=2))
            return

        _display_os(all_info["os"])
        _display_cpu(all_info["cpu"])
        _display_ram(all_info["ram"])
        _display_gpu(all_info["gpu"])
        _display_disk(all_info["disk"])
        _display_battery(all_info["battery"])
        console.print()


@app.command()
def ram(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON"),
) -> None:
    """Show RAM information."""
    info = hardware.get_ram_info()

    if json_output:
        console.print(json.dumps(info, indent=2))
        return

    _display_ram(info)
    console.print()


@app.command()
def cpu(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON"),
) -> None:
    """Show CPU information."""
    info = hardware.get_cpu_info()

    if json_output:
        console.print(json.dumps(info, indent=2))
        return

    _display_cpu(info)
    console.print()


@app.command()
def gpu(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON"),
) -> None:
    """Show GPU information (NVIDIA only)."""
    info = hardware.get_gpu_info()

    if json_output:
        console.print(json.dumps(info, indent=2))
        return

    _display_gpu(info)
    console.print()


@app.command()
def disk(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON"),
) -> None:
    """Show disk information."""
    info = hardware.get_disk_info()

    if json_output:
        console.print(json.dumps(info, indent=2))
        return

    from rich.table import Table

    console.print("\n[bold]Disk[/bold]")
    table = Table(show_header=True, header_style="bold")
    table.add_column("Device")
    table.add_column("Mount")
    table.add_column("Type")
    table.add_column("Total", justify="right")
    table.add_column("Used", justify="right")
    table.add_column("Free", justify="right")
    table.add_column("%", justify="right")

    for d in info:
        table.add_row(
            d['device'],
            d['mountpoint'],
            d['fstype'],
            f"{d['total_gb']} GB",
            f"{d['used_gb']} GB",
            f"{d['free_gb']} GB",
            f"{d['percent']}%",
        )

    console.print(table)
    console.print()


@app.command(name="os")
def os_info(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON"),
) -> None:
    """Show operating system information."""
    info = hardware.get_os_info()

    if json_output:
        console.print(json.dumps(info, indent=2))
        return

    _display_os(info)
    console.print()


@app.command()
def network(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON"),
) -> None:
    """Show network interface information."""
    info = hardware.get_network_info()

    if json_output:
        console.print(json.dumps(info, indent=2))
        return

    console.print("\n[bold]Network Interfaces[/bold]")
    for iface in info:
        console.print(f"  {iface['name']}")
        for addr in iface['addresses']:
            console.print(f"      {addr['type']}: {addr['address']}")
    console.print()


@app.command()
def battery(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON"),
) -> None:
    """Show battery information."""
    info = hardware.get_battery_info()

    if json_output:
        console.print(json.dumps(info, indent=2))
        return

    console.print("\n[bold]Battery[/bold]")
    if info:
        status = "Plugged in" if info['plugged_in'] else "On battery"
        console.print(f"  Status:       {status}")
        console.print(f"  Charge:       {info['percent']}%")
        if info['time_remaining']:
            console.print(f"  Time left:    {info['time_remaining']}")
    else:
        console.print("  No battery detected")
    console.print()


def tool_main() -> None:
    """The console-script entry point. The tool runs through the shared failure reporter (issue #3642): a
    failure is reported to the Gateway, and the exit code and the printed error stay exactly as they were."""
    from cc_shared.tool_errors import run_tool

    run_tool(app, "cc-hardware", app=app)


if __name__ == "__main__":
    tool_main()
