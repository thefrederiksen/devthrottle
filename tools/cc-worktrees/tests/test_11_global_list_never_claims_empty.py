"""Fix round 1, finding 7: the list of every pool never says count 0 when it cannot back it."""

from __future__ import annotations

from pathlib import Path


def test_global_list_shows_slots_of_a_pool_whose_state_file_is_gone(local_world):
    w = local_world
    got = w.get()
    w.state_file().unlink()

    res = w.run("list", "--json")

    assert res.code == 0, res.out + res.err
    assert res.data["count"] == 1
    slot = res.data["slots"][0]
    assert slot["slot"] == got["slot"]
    assert slot["state"] == "held"
    assert slot["reason"].startswith("state missing")
    assert Path(got["path"]).is_dir()


def test_global_list_fails_when_the_pool_registry_is_missing(local_world):
    w = local_world
    w.get()
    registry = w.home / "registry.json"
    assert registry.is_file()
    registry.unlink()

    res = w.run("list", "--json")

    assert res.code != 0, res.out + res.err
    assert "count" not in res.data
    assert res.data["code"] == "no-inventory"


def test_global_list_fails_when_the_pool_registry_is_unreadable(local_world):
    w = local_world
    w.get()
    (w.home / "registry.json").write_text("{ not json", encoding="utf-8")

    res = w.run("list", "--json")

    assert res.code != 0, res.out + res.err
    assert "count" not in res.data


def test_global_list_on_a_machine_with_no_registry_fails_rather_than_saying_empty(local_world):
    res = local_world.run("list", "--json")

    assert res.code != 0, res.out + res.err
    assert res.data["code"] == "no-inventory"
