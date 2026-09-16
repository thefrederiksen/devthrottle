"""Fix round 1, finding 6: a state file that parses but is wrong is as corrupt as one that does not parse."""

from __future__ import annotations

import json

import pytest

EXIT_POOL_FULL = 4


def _wrong_version(data, w):
    data["version"] = 999


def _wrong_repo(data, w):
    data["repo"] = str(w.tmp / "some-other-repo")


def _free_but_still_held_by_someone(data, w):
    data["slots"]["wt01"]["state"] = "free"


def _unknown_state(data, w):
    data["slots"]["wt01"]["state"] = "borrowed"


def _in_use_without_lease(data, w):
    data["slots"]["wt01"]["lease"] = None


def _held_without_reason(data, w):
    data["slots"]["wt01"]["state"] = "held"
    data["slots"]["wt01"]["reason"] = None


def _unknown_slot_name(data, w):
    data["slots"]["notaslot"] = dict(data["slots"]["wt01"])


def _the_inspector_case(data, w):
    _wrong_version(data, w)
    _wrong_repo(data, w)
    _free_but_still_held_by_someone(data, w)


CORRUPTIONS = [_wrong_version, _wrong_repo, _free_but_still_held_by_someone, _unknown_state,
               _in_use_without_lease, _held_without_reason, _unknown_slot_name, _the_inspector_case]


@pytest.mark.parametrize("corrupt", CORRUPTIONS, ids=[c.__name__.strip("_") for c in CORRUPTIONS])
def test_parseable_but_invalid_state_brings_every_slot_back_held(local_world, corrupt):
    w = local_world
    w.get(holder="owner")
    state = w.state_file()
    data = json.loads(state.read_text(encoding="utf-8"))
    corrupt(data, w)
    state.write_text(json.dumps(data), encoding="utf-8")

    slots = w.list()

    assert [s["slot"] for s in slots] == ["wt01"]
    assert slots[0]["state"] == "held"
    assert slots[0]["reason"] == "state lost, cannot verify"
    assert list(state.parent.glob(state.name + ".corrupt-*")), "the damaged file is kept for inspection"
    res = w.run("get", "--repo", str(w.repo), "--holder", "x", "--pool-size", "1")
    assert res.code == EXIT_POOL_FULL, res.out + res.err
