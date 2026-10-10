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
    "cc-launcher": "A developer daemon for the Mac that launchd starts (launcher.py, standard library only by design): "
                   "it answers HTTP on loopback and is not a command an agent runs. Its main.py imports a src.cli that "
                   "does not exist, so there is no Typer entry point to wire.",
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
        # EVERY tool with an entry point is checked, with an explicit exit or without one (review of #3757). A
        # tool whose failures are all unhandled exceptions - exactly what the hook exists to catch - has no exit
        # site to find, and a scan that only looked where exits are would let it go unwired without a word.
        if found_any or (tool_dir / "pyproject.toml").exists() or (tool_dir / "main.py").exists():
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


def test_scan_checks_a_tool_with_no_explicit_exit(tmp_path):
    """A tool whose only failures are unhandled exceptions has no exit to find; it is still checked."""
    _write(tmp_path / "cc-quiet/pyproject.toml",
           '[project.scripts]\ncc-quiet = "cc_quiet.cli:app"\n[tool.setuptools]\npackage-dir = {"cc_quiet" = "src"}\n')
    _write(tmp_path / "cc-quiet/src/cli.py",
           "import typer\napp = typer.Typer()\n@app.command()\ndef read(path: str):\n    print(open(path).read())\n")
    scan = scan_tools(tmp_path)
    assert scan.sites == []
    assert any("cc-quiet: the console script's app() does not run the tool" in p for p in problems(scan))


def test_scan_catches_a_main_py_that_bypasses_the_entry(tmp_path):
    _write(tmp_path / "cc-sly/pyproject.toml",
           '[project.scripts]\ncc-sly = "cc_sly.cli:tool_main"\n[tool.setuptools]\npackage-dir = {"cc_sly" = "src"}\n')
    _write(tmp_path / "cc-sly/src/cli.py",
           "import typer\nfrom cc_shared.tool_errors import run_tool\napp = typer.Typer()\n"
           "def go():\n    raise typer.Exit(1)\ndef tool_main():\n    run_tool(app, 'cc-sly', app=app)\n")
    _write(tmp_path / "cc-sly/main.py", "from cli import app\nif __name__ == '__main__':\n    app()\n")
    found = problems(scan_tools(tmp_path))
    assert any("cc-sly: main.py starts the tool with ['app']" in p for p in found)


# --- Test suites keep their reports inside the test ----------------------------------------------
#
# A test suite that makes a tool fail on purpose, through the entry point, would send that failure to a
# real Gateway - and before the isolation fixture existed, 24 such reports did (review of #3757). The
# fixture is a per-suite convention, so this rule makes forgetting it a red test: every suite that starts a
# Python process, starts a cc-* tool by name, or calls an entry point in process must carry an autouse
# fixture named ISOLATION_FIXTURE in its conftest.py that points CC_DIRECTOR_ROOT at a throwaway folder and
# DEVTHROTTLE_HOSTED_GATEWAY_URL at an address that fails at once.

ISOLATION_FIXTURE = "tool_error_reports_stay_in_the_test"

#: Test files that start a Python process which is NOT a cc-* tool, each with the reason. Per file, never
#: per suite, so a new file in the same suite that does start the tool is still caught.
ISOLATION_EXEMPT: Dict[str, str] = {
    "cc-ship/tests/test_engine.py": "Starts Python only to stand in for gh, git and codex (`PY` stub programs); its "
                                    "tests call cli.main() directly, which does not go through the entry hook.",
}


_PROCESS_CALLS = {"run", "Popen", "call", "check_call", "check_output"}


@dataclass
class SuiteScan:
    suites: List[str] = field(default_factory=list)
    starting: Dict[str, List[str]] = field(default_factory=dict)  # suite -> files that start a tool
    isolated: Set[str] = field(default_factory=set)


def _entry_function_names(tools_dir: Path) -> Set[str]:
    """Every console-script function the tools name, plus the hook itself - read from the pyprojects."""
    names = {"run_tool"}
    for tool_dir in tools_dir.glob("cc-*"):
        target = _console_script(tool_dir) if tool_dir.is_dir() else None
        if target is not None:
            names.add(target[1])
    return names


def _starts_a_tool(tree: ast.Module, text: str, entry_names: Set[str], tool_names: Set[str]) -> bool:
    """A Python process (`sys.executable`), a cc-* tool started by name, or an entry point called here."""
    if "sys.executable" in text:
        return True
    for node in ast.walk(tree):
        if isinstance(node, ast.Call) and _name_of(node.func)[1] in entry_names:
            return True
    for node in ast.walk(tree):
        # subprocess.run(["cc-pdf", ...]) and the like: a process whose program is a cc-* tool by name.
        if (isinstance(node, ast.Call) and _name_of(node.func)[1] in _PROCESS_CALLS and node.args
                and isinstance(node.args[0], (ast.List, ast.Tuple)) and node.args[0].elts
                and isinstance(node.args[0].elts[0], ast.Constant) and node.args[0].elts[0].value in tool_names):
            return True
    return False


def _has_isolation_fixture(conftest: Path) -> bool:
    if not conftest.exists():
        return False
    text = conftest.read_text(encoding="utf-8")
    for node in ast.walk(ast.parse(text)):
        if isinstance(node, ast.FunctionDef) and node.name == ISOLATION_FIXTURE:
            autouse = any(isinstance(d, ast.Call) and any(k.arg == "autouse" and isinstance(k.value, ast.Constant)
                                                          and k.value.value is True for k in d.keywords)
                          for d in node.decorator_list)
            body = ast.get_source_segment(text, node) or ""
            return autouse and "CC_DIRECTOR_ROOT" in body and "DEVTHROTTLE_HOSTED_GATEWAY_URL" in body
    return False


def scan_suites(tools_dir: Path) -> SuiteScan:
    """Read every test suite under `tools_dir` (a `tests` folder of a tool or of cc_shared)."""
    scan = SuiteScan()
    entry_names = _entry_function_names(tools_dir)
    tool_names = {p.name for p in tools_dir.glob("cc-*") if p.is_dir()}
    for tests_dir in sorted(tools_dir.glob("*/tests")):
        suite = tests_dir.relative_to(tools_dir).as_posix()
        scan.suites.append(suite)
        for path in sorted(tests_dir.rglob("*.py")):
            rel = path.relative_to(tools_dir).as_posix()
            if rel in ISOLATION_EXEMPT:
                continue
            text = path.read_text(encoding="utf-8-sig")
            if _starts_a_tool(ast.parse(text), text, entry_names, tool_names):
                scan.starting.setdefault(suite, []).append(rel)
        if _has_isolation_fixture(tests_dir / "conftest.py"):
            scan.isolated.add(suite)
    return scan


def isolation_problems(scan: SuiteScan) -> List[str]:
    return [f"{suite}: {', '.join(files)} start(s) a tool or call an entry point, but {suite}/conftest.py has no "
            f"autouse {ISOLATION_FIXTURE} fixture - the tool's deliberate failures would reach a real Gateway"
            for suite, files in sorted(scan.starting.items()) if suite not in scan.isolated]


def test_every_suite_that_starts_a_tool_keeps_its_reports():
    scan = scan_suites(TOOLS_DIR)
    files = sum(len(f) for f in scan.starting.values())
    print(f"\nread {len(scan.suites)} test suites; {len(scan.starting)} start a tool or call an entry point "
          f"({files} files: {', '.join(sorted(scan.starting))}); {len(scan.isolated)} carry the isolation fixture")
    assert scan.suites, "the scan read no test suites - it is looking in the wrong place"
    assert scan.starting, "no suite starts a tool - the rule is reading nothing"
    found = isolation_problems(scan)
    assert not found, "Suites that would send their deliberate failures to a real Gateway:\n  " + "\n  ".join(found)


def test_every_isolation_exemption_still_names_a_real_file():
    for key in ISOLATION_EXEMPT:
        assert (TOOLS_DIR / key).exists(), f"ISOLATION_EXEMPT names {key}, which does not exist"


def test_suite_scan_catches_a_suite_without_the_fixture(tmp_path):
    """The proof: a suite that starts its tool with no fixture is caught, one with a fixture that does not set
    the storage root is caught, and a properly isolated suite beside them is not."""
    _write(tmp_path / "cc-loud/tests/test_run.py",
           "import subprocess, sys\ndef test_it():\n    subprocess.run([sys.executable, 'main.py', 'fail'])\n")
    _write(tmp_path / "cc-half/tests/conftest.py",
           "import pytest\n@pytest.fixture(autouse=True)\ndef tool_error_reports_stay_in_the_test(monkeypatch):\n"
           "    monkeypatch.delenv('CC_GATEWAY_URL', raising=False)\n")
    _write(tmp_path / "cc-half/pyproject.toml", '[project.scripts]\ncc-half = "cc_half.cli:tool_main"\n')
    _write(tmp_path / "cc-half/tests/test_run.py", "from src.cli import tool_main\ndef test_it():\n    tool_main()\n")
    _write(tmp_path / "cc-calm/tests/conftest.py",
           "import pytest\n@pytest.fixture(autouse=True)\ndef tool_error_reports_stay_in_the_test(monkeypatch, tmp_path):\n"
           "    monkeypatch.setenv('CC_DIRECTOR_ROOT', str(tmp_path))\n"
           "    monkeypatch.setenv('DEVTHROTTLE_HOSTED_GATEWAY_URL', 'http://127.0.0.1:0')\n")
    _write(tmp_path / "cc-calm/tests/test_run.py", "import subprocess\ndef test_it():\n    subprocess.run(['cc-calm', 'x'])\n")
    _write(tmp_path / "cc-calm/pyproject.toml", "")
    found = isolation_problems(scan_suites(tmp_path))
    print("\n  " + "\n  ".join(found))
    assert any(p.startswith("cc-loud/tests:") for p in found)
    assert any(p.startswith("cc-half/tests:") for p in found)
    assert not any(p.startswith("cc-calm/tests:") for p in found)
