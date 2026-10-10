"""Every failure exit in every cc-* tool reaches the shared failure reporter (issue #3642, mission #3675).

The reporter (`cc_shared.tool_errors.run_tool`) sits at each tool's entry point and sees how the process
ends, so an exit reaches it when - and only when - it happens INSIDE a run that went through that entry
point. This scan reads the code and checks exactly that. Nothing here is a hand-kept list of sites:

  1. Every non-zero exit in every tool's source is FOUND by reading the code: `raise typer.Exit(n)`,
     `raise SystemExit(n)`, `sys.exit(n)`, `exit(n)`, `os._exit(n)` - with n anything but 0 or None.
  2. Each one must be reachable through the hook: its tool's console script and `main.py` must go
     through a function that calls `run_tool`, and the exit must be inside a function (a module-level
     exit runs while the module is imported, before the entry point exists). `os._exit` ends the
     process without unwinding, so nothing can see it: it is never allowed.
  3. Every fail helper - a function named `fail` / `_fail...` that exits itself - must call
     `note_failure`, so the report carries the helper's sentence and not just an exit code.

Anything that cannot meet that carries a written reason in EXEMPT below. The scan prints how many tools,
exits and fail helpers it read, and fails when it read none - a scan that found nothing proves nothing.
`test_scan_catches_a_broken_site` proves it fails on a deliberately broken tool.

Run: python -m pytest tools/test_tool_error_reporting.py -s
"""

import ast
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, List, Optional, Set, Tuple

TOOLS_DIR = Path(__file__).resolve().parent

#: Tools, files or single sites that do not go through the hook, each with the reason. A key is a tool
#: ("cc-trisight"), a file ("cc-scrub/src/cli.py") or a site ("cc-scrub/src/cli.py:30").
EXEMPT: Dict[str, str] = {
    "cc-browser-archived": "Archived: kept for reference, never built or shipped, and has no entry point to wire.",
    "cc-trisight": "A .NET tool; its two Python files are standalone helper scripts it launches, not a cc-* entry point. "
                   "The hook is Python; .NET tools report through the Director's own reporter work, not this one.",
    "cc-computer": "A .NET tool; its Python file is a standalone helper script, not a cc-* entry point.",
    "cc-scrub/src/cli.py:30": "An import-time check that Pillow is installed. It runs while cli.py is imported, before "
                              "the entry point exists, and says the fix (pip install Pillow) itself.",
    "cc-ship/main.py": "The interpreter-version check (`sys.exit(\"needs Python 3.11\")`) runs before anything can be "
                       "imported - the reporter itself needs 3.11 - so it cannot go through the hook.",
    "cc-worktrees/main.py": "The interpreter-version check runs before anything can be imported, as for cc-ship.",
    "cc-dev-reports/install.py": "A developer's setup script (puts a launcher on PATH), run by hand from a checkout; "
                                 "not the tool and never shipped.",
    "cc-ship/install.py": "A developer's setup script, as for cc-dev-reports; not the tool and never shipped.",
    "cc-scrub/gen_samples.py": "Renders the test screenshots; a build helper run by hand, not the tool.",
    "cc-scrub/proof-build-windows.py": "Writes the proof document from a transcript; a build helper, not the tool.",
    "cc-secrets/src/askpass_helper.py": "Git's askpass program: git runs it with no terminal and reads only its exit "
                                        "code and standard output. It is its own process, not a cc-secrets command, and "
                                        "its refusal is already in the cc-secrets listener's audit log.",
}

_EXIT_CALLS = {("sys", "exit"), (None, "exit"), (None, "quit")}
_EXIT_CLASSES = {"Exit", "SystemExit"}
_FAIL_NAME = re.compile(r"^_?fail(_\w+)?$")


@dataclass
class Site:
    tool: str
    file: str  # relative to the tools folder, forward slashes
    line: int
    kind: str  # "exit", "os._exit"
    function: Optional[str]  # None at module level


@dataclass
class Helper:
    tool: str
    file: str
    line: int
    name: str
    notes: bool


@dataclass
class Scan:
    tools: List[str] = field(default_factory=list)
    sites: List[Site] = field(default_factory=list)
    helpers: List[Helper] = field(default_factory=list)
    wired: Set[str] = field(default_factory=set)
    wiring_problems: List[str] = field(default_factory=list)
    exempt_tools: List[str] = field(default_factory=list)


def _is_zero(node: Optional[ast.AST]) -> bool:
    return node is None or (isinstance(node, ast.Constant) and node.value in (0, None) and node.value is not False)


def _exit_code_arg(call: ast.Call) -> Optional[ast.AST]:
    if call.args:
        return call.args[0]
    for kw in call.keywords:
        if kw.arg == "code":
            return kw.value
    return None


def _name_of(func: ast.AST) -> Tuple[Optional[str], Optional[str]]:
    if isinstance(func, ast.Name):
        return None, func.id
    if isinstance(func, ast.Attribute):
        owner = func.value.id if isinstance(func.value, ast.Name) else None
        return owner, func.attr
    return None, None


def _exit_kind(node: ast.AST) -> Optional[str]:
    """'exit' for a non-zero exit, 'os._exit' for a process kill, None for anything else."""
    if isinstance(node, ast.Raise) and isinstance(node.exc, ast.Call):
        _, name = _name_of(node.exc.func)
        if name in _EXIT_CLASSES and not _is_zero(_exit_code_arg(node.exc)):
            return "exit"
    if isinstance(node, ast.Call):
        owner, name = _name_of(node.func)
        if (owner, name) in _EXIT_CALLS and not _is_zero(_exit_code_arg(node)):
            return "exit"
        if (owner, name) == ("os", "_exit") and not _is_zero(_exit_code_arg(node)):
            return "os._exit"
    return None


def _calls(tree: ast.AST, name: str) -> bool:
    return any(isinstance(n, ast.Call) and _name_of(n.func)[1] == name for n in ast.walk(tree))


def _source_files(tool_dir: Path) -> List[Path]:
    files = []
    for path in tool_dir.rglob("*.py"):
        parts = set(path.relative_to(tool_dir).parts)
        if parts & {"tests", "test", "build", "dist", ".venv", "venv", "node_modules"}:
            continue
        if path.name.startswith("test_") or path.name == "conftest.py":
            continue
        files.append(path)
    return sorted(files)


def _console_script(tool_dir: Path) -> Optional[Tuple[Path, str]]:
    """The file and function the tool's console script names, from its pyproject.toml."""
    pyproject = tool_dir / "pyproject.toml"
    if not pyproject.exists():
        return None
    text = pyproject.read_text(encoding="utf-8")
    match = re.search(r'(?m)^\s*' + re.escape(tool_dir.name) + r'\s*=\s*"([\w.]+):(\w+)"', text)
    if not match:
        return None
    module, function = match.groups()
    package, _, rest = module.partition(".")
    package_dir = package
    mapping = re.search(r'package-dir\s*=\s*\{(.*?)\}', text)
    if mapping:
        found = re.search(r'"' + re.escape(package) + r'"\s*=\s*"([^"]*)"', mapping.group(1))
        if found:
            package_dir = found.group(1)
    return (tool_dir / package_dir / (rest.replace(".", "/") + ".py")).resolve(), function


def _function_named(tree: ast.Module, name: str) -> Optional[ast.FunctionDef]:
    for node in tree.body:
        if isinstance(node, ast.FunctionDef) and node.name == name:
            return node
    return None


def _main_block_calls(tree: ast.Module, top_level: bool) -> Set[str]:
    """The names called in a module's `if __name__ == "__main__":` block - and, for a main.py, at its top
    level too, where cc-ship's and cc-worktrees' start the tool."""
    names: Set[str] = set()
    for node in tree.body:
        if isinstance(node, ast.If) and "__main__" in ast.unparse(node.test):
            names.update(_name_of(n.func)[1] for n in ast.walk(node) if isinstance(n, ast.Call))
        elif top_level and isinstance(node, ast.Expr) and isinstance(node.value, ast.Call):
            names.add(_name_of(node.value.func)[1])
    return names


def _check_wiring(tool_dir: Path, scan: Scan) -> None:
    tool = tool_dir.name
    target = _console_script(tool_dir)
    if target is None:
        scan.wiring_problems.append(f"{tool}: no console script in pyproject.toml, so there is no entry point to check")
        return
    module_file, function = target
    if not module_file.exists():
        scan.wiring_problems.append(f"{tool}: the console script names {module_file}, which does not exist")
        return
    tree = ast.parse(module_file.read_text(encoding="utf-8"))
    entry = _function_named(tree, function)
    if entry is None or not _calls(entry, "run_tool"):
        scan.wiring_problems.append(f"{tool}: the console script's {function}() does not run the tool through run_tool")
        return
    for name, path in (("its console-script module", module_file), ("main.py", tool_dir / "main.py")):
        if not path.exists():
            continue
        called = _main_block_calls(ast.parse(path.read_text(encoding="utf-8")), top_level=name == "main.py")
        main_function = _function_named(ast.parse(path.read_text(encoding="utf-8")), "main")
        if main_function is not None and "main" in called:
            called |= {_name_of(n.func)[1] for n in ast.walk(main_function) if isinstance(n, ast.Call)}
        if called and function not in called:
            scan.wiring_problems.append(f"{tool}: {name} starts the tool with {sorted(c for c in called if c)} "
                                        f"instead of {function}(), so it bypasses the reporter")
            return
    scan.wired.add(tool)


def _exempt(site_or_file: str, tool: str) -> bool:
    if tool in EXEMPT:
        return True
    file_key = site_or_file.rsplit(":", 1)[0]
    return site_or_file in EXEMPT or file_key in EXEMPT


def scan_tools(tools_dir: Path) -> Scan:
    """Read every cc-* tool under `tools_dir`: its exits, its fail helpers and its entry point."""
    scan = Scan()
    for tool_dir in sorted(p for p in tools_dir.glob("cc-*") if p.is_dir()):
        tool = tool_dir.name
        if tool in EXEMPT:
            scan.exempt_tools.append(tool)
            continue
        files = _source_files(tool_dir)
        if not files:
            continue
        found_any = False
        for path in files:
            rel = path.relative_to(tools_dir).as_posix()
            tree = ast.parse(path.read_text(encoding="utf-8-sig"), filename=str(path))
            parents: Dict[ast.AST, Optional[ast.AST]] = {}
            for parent in ast.walk(tree):
                for child in ast.iter_child_nodes(parent):
                    parents[child] = parent

            def enclosing(node: ast.AST) -> Optional[ast.AST]:
                current = parents.get(node)
                while current is not None and not isinstance(current, (ast.FunctionDef, ast.AsyncFunctionDef)):
                    current = parents.get(current)
                return current

            for node in ast.walk(tree):
                kind = _exit_kind(node)
                if kind is None:
                    continue
                found_any = True
                func = enclosing(node)
                scan.sites.append(Site(tool, rel, node.lineno, kind, func.name if func is not None else None))
            for node in ast.walk(tree):
                if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and _FAIL_NAME.match(node.name):
                    direct = [n for n in ast.walk(node) if _exit_kind(n) and enclosing(n) is node]
                    if direct:
                        scan.helpers.append(Helper(tool, rel, node.lineno, node.name, _calls(node, "note_failure")))
        scan.tools.append(tool)
        if found_any:
            _check_wiring(tool_dir, scan)
    return scan


def problems(scan: Scan) -> List[str]:
    """Every exit or helper that does not reach the reporter, and is not exempt with a reason."""
    out = list(scan.wiring_problems)
    for s in scan.sites:
        key = f"{s.file}:{s.line}"
        if _exempt(key, s.tool):
            continue
        if s.kind == "os._exit":
            out.append(f"{key}: os._exit ends the process without unwinding, so the reporter never sees it")
        elif s.function is None:
            out.append(f"{key}: exits at module level, while the module is imported - before the entry point runs")
        elif s.tool not in scan.wired:
            out.append(f"{key}: {s.tool} is not wired through run_tool, so this exit is never reported")
    for h in scan.helpers:
        if not h.notes and not _exempt(f"{h.file}:{h.line}", h.tool):
            out.append(f"{h.file}:{h.line}: fail helper {h.name}() exits without note_failure, so its report "
                       "loses the sentence it printed")
    return out


def _summary(scan: Scan) -> str:
    exits = [s for s in scan.sites if s.kind == "exit"]
    return (f"read {len(scan.tools)} tools, {len(scan.sites)} non-zero exits ({len(exits)} exits, "
            f"{len(scan.sites) - len(exits)} os._exit), {len(scan.helpers)} fail helpers; "
            f"{len(scan.wired)} tools wired through run_tool; {len(scan.exempt_tools)} tools exempt "
            f"({', '.join(scan.exempt_tools) or 'none'})")


# --- The tests -----------------------------------------------------------------------------------


def test_every_failure_exit_reaches_the_reporter():
    scan = scan_tools(TOOLS_DIR)
    print("\n" + _summary(scan))
    assert scan.tools, "the scan read no tools at all - it is looking in the wrong place"
    assert scan.sites, "the scan found no exits at all - a scan that reads nothing proves nothing"
    assert scan.helpers, "the scan found no fail helpers - the helper rule is reading nothing"
    found = problems(scan)
    assert not found, "Exits that never reach the failure reporter:\n  " + "\n  ".join(found)


def test_every_exemption_still_names_something_real():
    """An exemption for a tool, file or site that no longer exists is a stale hole: remove it."""
    for key in EXEMPT:
        path = key.rsplit(":", 1)[0] if re.search(r":\d+$", key) else key
        assert (TOOLS_DIR / path).exists(), f"EXEMPT names {key}, which does not exist"


def _write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def test_scan_catches_a_broken_site(tmp_path):
    """The proof the scan is not reading nothing: a tool that bypasses the reporter in every way it can
    is caught on every count, and a correctly wired tool beside it is not."""
    _write(tmp_path / "cc-good/pyproject.toml",
           '[project.scripts]\ncc-good = "cc_good.cli:tool_main"\n[tool.setuptools]\npackage-dir = {"cc_good" = "src"}\n')
    _write(tmp_path / "cc-good/src/cli.py",
           "import typer\nfrom cc_shared.tool_errors import note_failure, run_tool\napp = typer.Typer()\n"
           "def _fail(m):\n    note_failure(m)\n    raise typer.Exit(1)\n"
           "def tool_main():\n    run_tool(app, 'cc-good', app=app)\n"
           "if __name__ == '__main__':\n    tool_main()\n")
    _write(tmp_path / "cc-good/main.py", "from cli import tool_main\nif __name__ == '__main__':\n    tool_main()\n")

    _write(tmp_path / "cc-broken/pyproject.toml",
           '[project.scripts]\ncc-broken = "cc_broken.cli:app"\n[tool.setuptools]\npackage-dir = {"cc_broken" = "src"}\n')
    _write(tmp_path / "cc-broken/src/cli.py",
           "import os, sys, typer\napp = typer.Typer()\n"
           "def _fail(m):\n    print(m)\n    raise typer.Exit(1)\n"
           "def kill():\n    os._exit(3)\n"
           "sys.exit('needs something')\n")

    scan = scan_tools(tmp_path)
    found = problems(scan)
    print("\n" + _summary(scan) + "\n  " + "\n  ".join(found))
    assert scan.wired == {"cc-good"}
    assert any("cc-broken: the console script's app() does not run the tool" in p for p in found)
    assert any("cc-broken/src/cli.py:3: fail helper _fail()" in p for p in found)
    assert any("cc-broken/src/cli.py:7: os._exit" in p for p in found)
    assert any("cc-broken/src/cli.py:8: exits at module level" in p for p in found)
    assert not any("cc-good" in p for p in found)


def test_scan_catches_a_main_py_that_bypasses_the_entry(tmp_path):
    _write(tmp_path / "cc-sly/pyproject.toml",
           '[project.scripts]\ncc-sly = "cc_sly.cli:tool_main"\n[tool.setuptools]\npackage-dir = {"cc_sly" = "src"}\n')
    _write(tmp_path / "cc-sly/src/cli.py",
           "import typer\nfrom cc_shared.tool_errors import run_tool\napp = typer.Typer()\n"
           "def go():\n    raise typer.Exit(1)\ndef tool_main():\n    run_tool(app, 'cc-sly', app=app)\n")
    _write(tmp_path / "cc-sly/main.py", "from cli import app\nif __name__ == '__main__':\n    app()\n")
    found = problems(scan_tools(tmp_path))
    assert any("cc-sly: main.py starts the tool with ['app']" in p for p in found)
