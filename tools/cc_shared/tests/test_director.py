"""Unit tests for the shared Director-API helpers used by the fleet-messaging tools (issue #705)."""

import sys
from pathlib import Path

# Make cc_shared importable when tests run from the tools/ tree.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import director  # noqa: E402


def _session(sid, name=None, machine="machine-A"):
    return {"sessionId": sid, "name": name, "machineName": machine}


SESSIONS = [
    _session("4c810000-1111-2222-3333-444444444444", "feature-work"),
    _session("9b2f0000-aaaa-bbbb-cccc-dddddddddddd", "docs"),
    _session("9b2f9999-eeee-ffff-0000-111111111111", "docs-helper"),
]


def test_short_id_truncates_to_eight():
    assert director.short_id("4c810000-1111") == "4c810000"
    assert director.short_id("abc") == "abc"


def test_field_tolerates_camel_and_pascal_case():
    assert director.field({"sessionId": "x"}, "sessionId", "SessionId") == "x"
    assert director.field({"SessionId": "y"}, "sessionId", "SessionId") == "y"
    assert director.field({}, "name", "Name", default="-") == "-"


def test_resolve_target_exact_full_id_wins():
    full = "9b2f0000-aaaa-bbbb-cccc-dddddddddddd"
    matches = director.resolve_target(SESSIONS, full)
    assert len(matches) == 1
    assert director.field(matches[0], "sessionId", "SessionId") == full


def test_resolve_target_unique_prefix_matches_one():
    matches = director.resolve_target(SESSIONS, "4c81")
    assert len(matches) == 1
    assert director.field(matches[0], "name", "Name") == "feature-work"


def test_resolve_target_ambiguous_prefix_returns_all_candidates():
    matches = director.resolve_target(SESSIONS, "9b2f")
    assert len(matches) == 2  # caller must refuse and list these


def test_resolve_target_by_exact_name():
    matches = director.resolve_target(SESSIONS, "docs")
    assert len(matches) == 1
    assert director.field(matches[0], "name", "Name") == "docs"


def test_resolve_target_no_match_returns_empty():
    assert director.resolve_target(SESSIONS, "zzzz") == []
