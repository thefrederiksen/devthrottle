"""The conformance command line BUILDS THE LIBRARY FROM SOURCE AND RUNS IT on every check (final inspection
finding F-03; the fix-round inspection's open item on this command).

The previous command kept a --library-json route that read a saved answer, never built, set the dll digest
to null, and still printed "Library provenance: built from source this run". These tests drive the REAL
main() with the process boundary recorded - subprocess.run is a recorder that answers the build and the run
- and assert on what the command actually did: the build command runs, then the dll it produced runs, the
report names that dll's digest, a saved-answer option no longer exists, and a failed build runs nothing.

Run from the product repository:  python -m pytest tools/throttle-conformance/tests
"""
import hashlib
import importlib.util
import json
import subprocess
import sys
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parent
TOOL = HERE.parent


def load_conformance():
    spec = importlib.util.spec_from_file_location("conformance_under_test", TOOL / "conformance.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


ANSWER = {
    "turns": 10, "voiceTurns": 8, "typedTurns": 2, "sessions": 3,
    "headline": {"voice": {"percent": 80}, "phone": {"percent": 80}},
    "buckets": [{"modality": "voice", "surface": "phone", "turns": 8}, {"modality": "typed", "surface": "desktop", "turns": 2}],
    "excluded": {"noInputOrigin": 0, "agentDriven": 0, "framework": 0, "unresolved": 0},
}
MENTOR = {"turns": 10, "voiceTurns": 8, "typedTurns": 2, "sessions": 3, "buckets": {}, "excluded": ANSWER["excluded"]}
RAW = {"agents": [], "repos": [], "hourlyTurns": []}
DLL_BYTES = b"MZ the dll this run built"


class Recorder:
    """Stands in for subprocess.run at the process boundary: answers `dotnet build` by writing the dll, and
    `dotnet <dll>` by writing the answer to the --out path. Records every command in order."""

    def __init__(self, dll, build_exit=0):
        self.dll = dll
        self.build_exit = build_exit
        self.calls = []

    def __call__(self, argv, **kwargs):
        self.calls.append(list(argv))
        if argv[:2] == ["dotnet", "build"]:
            if self.build_exit == 0:
                self.dll.parent.mkdir(parents=True, exist_ok=True)
                self.dll.write_bytes(DLL_BYTES)
            return subprocess.CompletedProcess(argv, self.build_exit, stdout="", stderr="build failed on purpose\n")
        if argv[:2] == ["dotnet", str(self.dll)]:
            out = Path(argv[argv.index("--out") + 1])
            out.write_text(json.dumps(ANSWER), encoding="utf-8")
            return subprocess.CompletedProcess(argv, 0, stdout="", stderr="10 turns\n")
        raise AssertionError("an unexpected command reached the process boundary: " + " ".join(argv))


@pytest.fixture
def world(tmp_path, monkeypatch):
    """A synthetic mentor directory (config and the two extracts the command requires), the library's
    project directory redirected to scratch so the recorder's build writes the dll there, and every mentor
    side reading replaced by a canned one - this module is about what the command does with the LIBRARY."""
    conformance = load_conformance()
    mentor = tmp_path / "mentor"
    mentor.mkdir()
    (mentor / "config.json").write_text(json.dumps({
        "accounts": [{"label": "soren", "tenant_id": "tenant-one", "time_zone": "America/Toronto"}],
        "data_root": str(tmp_path / "data"),
    }), encoding="utf-8")
    db = tmp_path / "data" / "accounts" / "soren" / "raw" / "db"
    db.mkdir(parents=True)
    (db / "activity_events.jsonl").write_text("", encoding="utf-8")
    (db / "session_history.jsonl").write_text("", encoding="utf-8")
    monkeypatch.setenv("DEVTHROTTLE_GATEWAY_DB_CONNECTION", "Host=invented.invalid")

    tool_dir = tmp_path / "tool"
    tool_dir.mkdir()
    (tool_dir / "ThrottleConformance.csproj").write_text("<Project />", encoding="utf-8")
    monkeypatch.setattr(conformance, "HERE", tool_dir)
    dll = tool_dir / "bin" / "Debug" / "net10.0" / "throttle-conformance.dll"

    import datetime as dt

    class FakeMetrics:
        @staticmethod
        def week_bounds(week, tz):
            from zoneinfo import ZoneInfo
            start = dt.datetime(2026, 8, 24, tzinfo=ZoneInfo(tz))
            return start, start + dt.timedelta(days=7)

    monkeypatch.setattr(conformance, "load_mentor", lambda mentor_dir: (FakeMetrics, None))
    monkeypatch.setattr(conformance, "mentor_side", lambda *a, **k: dict(MENTOR))
    monkeypatch.setattr(conformance, "headline_side", lambda mentor: {"phone": {"turns": 8}})
    monkeypatch.setattr(conformance, "raw_side", lambda *a, **k: dict(RAW))
    monkeypatch.setattr(conformance, "predicate_from_source", lambda: "the predicate")
    monkeypatch.setattr(conformance, "compare", lambda *a, **k: [])
    monkeypatch.setattr(conformance, "library_provenance", lambda: {"commit": "c" * 40, "dirty": False})
    monkeypatch.setenv("TEMP", str(tmp_path / "temp"))
    (tmp_path / "temp").mkdir()
    recorder = Recorder(dll)
    monkeypatch.setattr(conformance.subprocess, "run", recorder)
    return conformance, mentor, dll, recorder


def run_main(conformance, argv, capsys):
    sys.argv = ["conformance.py"] + argv
    capsys.readouterr()
    with pytest.raises(SystemExit) as stop:
        conformance.main()
    return stop.value.code, capsys.readouterr()


def test_every_check_builds_the_library_from_source_then_runs_the_dll_it_built(world, capsys, tmp_path):
    conformance, mentor, dll, recorder = world
    report = tmp_path / "report.md"
    code, out = run_main(conformance, ["--account", "soren", "--week", "2026-W35", "--mentor-dir", str(mentor),
                                      "--report", str(report)], capsys)
    assert code == 0, out.err
    # The build ran, then the dll it produced ran, and nothing else reached the process boundary.
    assert [c[:2] for c in recorder.calls] == [["dotnet", "build"], ["dotnet", str(dll)]]
    assert recorder.calls[0][2] == str(conformance.HERE / "ThrottleConformance.csproj")
    # The report's provenance sentence names the digest of the dll THIS run built - not null, not a guess.
    text = report.read_text(encoding="utf-8")
    assert "Library provenance: built from source this run; product checkout commit " + "c" * 40 in text
    assert "dll sha256 " + hashlib.sha256(DLL_BYTES).hexdigest() + "." in text
    assert "## PASS" in text


def test_the_dll_is_rebuilt_even_when_one_is_already_there(world, capsys):
    """The original finding: a dll on disk was taken as the library. Now a dll from an earlier run is
    rebuilt before it is trusted, and the digest reported is the rebuilt file's."""
    conformance, mentor, dll, recorder = world
    dll.parent.mkdir(parents=True)
    dll.write_bytes(b"MZ an older build")
    code, _ = run_main(conformance, ["--account", "soren", "--week", "2026-W35", "--mentor-dir", str(mentor)], capsys)
    assert code == 0
    assert recorder.calls[0][:2] == ["dotnet", "build"]
    assert dll.read_bytes() == DLL_BYTES


def test_there_is_no_route_that_reads_a_saved_answer_instead_of_running_the_library(world, capsys, tmp_path):
    conformance, mentor, dll, recorder = world
    saved = tmp_path / "saved.json"
    saved.write_text(json.dumps(ANSWER), encoding="utf-8")
    code, out = run_main(conformance, ["--account", "soren", "--week", "2026-W35", "--mentor-dir", str(mentor),
                                      "--library-json", str(saved)], capsys)
    assert code == 2
    assert "unrecognized arguments: --library-json" in out.err
    assert recorder.calls == []
    assert "library-json" not in (conformance.__doc__ or "")


def test_a_failed_build_stops_the_check_and_never_runs_an_old_dll(world, capsys):
    conformance, mentor, dll, recorder = world
    dll.parent.mkdir(parents=True)
    dll.write_bytes(b"MZ an older build")
    recorder.build_exit = 1
    code, out = run_main(conformance, ["--account", "soren", "--week", "2026-W35", "--mentor-dir", str(mentor)], capsys)
    assert code == 2
    assert "building the library tool failed" in out.err and "build failed on purpose" in out.err
    assert [c[:2] for c in recorder.calls] == [["dotnet", "build"]]


def test_without_the_connection_variable_the_check_stops_and_says_to_run_it_through_cc_secrets(world, capsys, monkeypatch):
    conformance, mentor, dll, recorder = world
    monkeypatch.delenv("DEVTHROTTLE_GATEWAY_DB_CONNECTION", raising=False)
    code, out = run_main(conformance, ["--account", "soren", "--week", "2026-W35", "--mentor-dir", str(mentor)], capsys)
    assert code == 2
    assert "DEVTHROTTLE_GATEWAY_DB_CONNECTION is not set" in out.err
    assert "cc-secrets run devthrottle-gateway-db-connection" in out.err
    assert not any(c[:2] == ["dotnet", str(dll)] for c in recorder.calls)
