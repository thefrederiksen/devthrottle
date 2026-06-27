"""Shipped-tool contract guard (review item #27).

Reads tools/registry.json and asserts, for every shipped (ship:true) Python tool, the things that
silently broke a clean install in the past:
- the pyproject distribution name EXACTLY equals the registry name (the bundle dependency collector
  matches by exact name; a mismatch drops the tool's unique deps from the wheelhouse - issue that hit
  cc-comm-queue);
- the tool declares a console-script entry point under [project.scripts];
- the tool's shipped source is ASCII-only (house rule - no emoji/unicode).

Run: python -m pytest tools/test_shipped_tools_contract.py
"""

import json
import re
from pathlib import Path

import pytest

TOOLS_DIR = Path(__file__).resolve().parent
REGISTRY = json.loads((TOOLS_DIR / "registry.json").read_text(encoding="utf-8"))
SHIPPED_PYTHON = [
    t["name"]
    for t in REGISTRY["tools"]
    if t.get("type") == "python" and t.get("ship") is True
]


def _pyproject_text(name: str) -> str:
    path = TOOLS_DIR / name / "pyproject.toml"
    assert path.exists(), f"{name}: missing pyproject.toml"
    return path.read_text(encoding="utf-8")


@pytest.mark.parametrize("name", SHIPPED_PYTHON)
def test_pyproject_name_matches_registry(name):
    text = _pyproject_text(name)
    match = re.search(r'(?m)^\s*name\s*=\s*"([^"]+)"', text)
    assert match, f"{name}: no project name in pyproject.toml"
    assert match.group(1) == name, (
        f"{name}: pyproject name '{match.group(1)}' != registry name '{name}'. "
        "The bundle dependency collector matches by exact name; a mismatch drops this tool's "
        "unique dependencies from the shipped wheelhouse."
    )


@pytest.mark.parametrize("name", SHIPPED_PYTHON)
def test_declares_console_script(name):
    text = _pyproject_text(name)
    assert "[project.scripts]" in text, f"{name}: no [project.scripts] section in pyproject.toml"
    assert re.search(rf'(?m)^\s*{re.escape(name)}\s*=', text), (
        f"{name}: no '{name} = ...' console-script entry in [project.scripts]"
    )


@pytest.mark.parametrize("name", SHIPPED_PYTHON)
def test_shipped_source_is_ascii(name):
    src = TOOLS_DIR / name / "src"
    if not src.exists():
        return
    offenders = []
    for py in sorted(src.rglob("*.py")):
        try:
            py.read_bytes().decode("ascii")
        except UnicodeDecodeError:
            offenders.append(str(py.relative_to(TOOLS_DIR)))
    assert not offenders, f"{name}: non-ASCII bytes in shipped source: {offenders}"
