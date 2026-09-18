"""Fix round 1, finding 3: return requires the lease. Without it the tool cannot know the holder let go."""

from __future__ import annotations

EXIT_USAGE = 2


def test_return_without_a_lease_is_a_usage_error_and_changes_nothing(local_world):
    w = local_world
    got = w.get(holder="first")
    before = w.slot(got["slot"])

    res = w.run("return", got["path"], "--json")

    assert res.code == EXIT_USAGE, res.out + res.err
    assert res.data["code"] == "usage"
    assert w.slot(got["slot"]) == before
    again = w.get(holder="second")
    assert again["slot"] != got["slot"]
