"""Settings operations for cc-devthrottle."""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import typer
from rich.console import Console
from rich.table import Table

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared.config import CCDirectorConfig, get_config_path  # noqa: E402

console = Console()


def load_config() -> CCDirectorConfig:
    """Load a fresh config from disk."""
    return CCDirectorConfig().load()


def get_all_settings(config: CCDirectorConfig) -> Dict[str, Any]:
    """Get all settings as a flat dictionary with dotted keys."""
    result: Dict[str, Any] = {}
    _flatten(config.to_dict(), "", result)
    return result


def _flatten(data: Any, prefix: str, result: Dict[str, Any]) -> None:
    if isinstance(data, dict):
        for key, value in data.items():
            new_prefix = f"{prefix}.{key}" if prefix else key
            _flatten(value, new_prefix, result)
    elif isinstance(data, list):
        result[prefix] = data
    else:
        result[prefix] = data


def get_section(config: CCDirectorConfig, section: str) -> Optional[Dict[str, Any]]:
    """Get a top-level config section by name."""
    return config.to_dict().get(section)


def get_value(config: CCDirectorConfig, key: str) -> Tuple[bool, Any]:
    """Get a specific setting value by dotted key."""
    all_settings = get_all_settings(config)
    if key in all_settings:
        return True, all_settings[key]
    return False, None


def set_value(config: CCDirectorConfig, key: str, value: str) -> bool:
    """Set a specific setting value by dotted key."""
    parts = key.split(".")
    if len(parts) < 2:
        return False

    section_name = parts[0]
    section = getattr(config, section_name, None)
    if section is None:
        return False

    obj = section
    for part in parts[1:-1]:
        obj = getattr(obj, part, None)
        if obj is None:
            return False

    attr_name = parts[-1]
    if not hasattr(obj, attr_name):
        return False

    current = getattr(obj, attr_name)
    coerced: Any = value
    if isinstance(current, bool):
        coerced = value.lower() in ("true", "1", "yes")
    elif isinstance(current, int):
        coerced = int(value)
    elif isinstance(current, float):
        coerced = float(value)

    setattr(obj, attr_name, coerced)
    config.save()
    return True


def list_keys(config: CCDirectorConfig) -> List[str]:
    """List all available setting keys."""
    return sorted(get_all_settings(config).keys())


def get_section_names(config: CCDirectorConfig) -> List[str]:
    """Get sorted top-level section names."""
    return sorted(config.to_dict().keys())


def show(section: Optional[str], json_output: bool) -> None:
    """Display current settings."""
    config = load_config()

    if section:
        data = get_section(config, section)
        if data is None:
            sections = get_section_names(config)
            console.print(f"[red]Unknown section: {section}[/red]")
            console.print(f"Available sections: {', '.join(sections)}")
            raise typer.Exit(1)

        if json_output:
            console.print(json.dumps({section: data}, indent=2))
            return

        console.print(f"\n[bold]{section}[/bold]")
        _display_section(data, indent=2)
        console.print()
        return

    full = config.to_dict()
    if json_output:
        console.print(json.dumps(full, indent=2))
        return

    for name, data in full.items():
        console.print(f"\n[bold]{name}[/bold]")
        _display_section(data, indent=2)
    console.print()


def get(key: str, json_output: bool) -> None:
    """Get a specific setting value."""
    config = load_config()
    found, value = get_value(config, key)

    if not found:
        console.print(f"[red]Unknown key: {key}[/red]")
        console.print("Use 'cc-devthrottle settings list' to see available keys.")
        raise typer.Exit(1)

    if json_output:
        console.print(json.dumps({"key": key, "value": value}, indent=2))
    else:
        console.print(str(value))


def set_config_value(key: str, value: str, json_output: bool) -> None:
    """Set a configuration value."""
    config = load_config()
    success = set_value(config, key, value)

    if not success:
        console.print(f"[red]Cannot set key: {key}[/red]")
        console.print("Use 'cc-devthrottle settings list' to see available keys.")
        raise typer.Exit(1)

    if json_output:
        console.print(json.dumps({"key": key, "value": value, "status": "saved"}, indent=2))
    else:
        console.print(f"[green]Set {key} = {value}[/green]")


def list_settings(json_output: bool) -> None:
    """List all setting keys."""
    config = load_config()
    keys = list_keys(config)
    all_settings = get_all_settings(config)

    if json_output:
        console.print(json.dumps(keys, indent=2))
        return

    table = Table(show_header=True, header_style="bold")
    table.add_column("Key")
    table.add_column("Value")

    for key in keys:
        table.add_row(key, _format_value(all_settings[key]))

    console.print(table)


def path(json_output: bool) -> None:
    """Show the config file location."""
    config_path = str(get_config_path())

    if json_output:
        console.print(json.dumps({"config_path": config_path}, indent=2))
    else:
        console.print(config_path)


def _display_section(data: Any, indent: int = 0) -> None:
    prefix = " " * indent
    if isinstance(data, dict):
        for key, value in data.items():
            if isinstance(value, dict):
                console.print(f"{prefix}[dim]{key}:[/dim]")
                _display_section(value, indent + 2)
            elif isinstance(value, list):
                console.print(f"{prefix}[dim]{key}:[/dim] {_format_value(value)}")
            else:
                console.print(f"{prefix}[dim]{key}:[/dim] {value}")
    else:
        console.print(f"{prefix}{data}")


def _format_value(value: Any) -> str:
    if isinstance(value, list):
        if not value:
            return "[]"
        return json.dumps(value)
    if isinstance(value, bool):
        return str(value).lower()
    return str(value)
