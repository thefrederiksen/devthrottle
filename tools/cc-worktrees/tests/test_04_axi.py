"""AXI output: lists parse back, empty states are definitive, usage errors exit 2, help exists."""

from __future__ import annotations

from cc_shared import axi_output


def test_list_output_parses_back(local_world):
    w = local_world
    a = w.get(holder="holder, with a comma")
    w.get(holder="plain")

    res = w.run("list", "--repo", str(w.repo))

    assert res.code == 0, res.out + res.err
    assert res.out.isascii()
    assert res.out.startswith("count: 2 (free 0, in-use 2, held 0)")
    fields, records = axi_output.parse_list(res.out, "slots")
    assert fields == ["slot", "state", "holder", "reason"]
    by_slot = {r["slot"]: r for r in records}
    assert by_slot[a["slot"]]["holder"] == "holder, with a comma"
    assert by_slot[a["slot"]]["state"] == "in-use"
    assert by_slot[a["slot"]]["reason"] is None
    assert "help[" in res.out


def test_empty_pool_says_count_zero(local_world):
    res = local_world.run("list", "--repo", str(local_world.repo))
    assert res.code == 0, res.out + res.err
    assert res.out.splitlines()[0] == "count: 0"
    fields, records = axi_output.parse_list(res.out, "slots")
    assert records == []

    # The list of every pool is a different question: with no registry it cannot say "none", so it
    # fails instead (test_11). Once a pool exists and is emptied, it can say count 0.
    got = local_world.get()
    assert local_world.run("return", got["path"], "--lease", got["lease"]).code == 0
    assert local_world.run("destroy", got["path"], "--yes").code == 0
    everything = local_world.run("list", "--json")
    assert everything.code == 0, everything.out
    assert everything.data["count"] == 0


def test_unknown_flag_exits_2(local_world):
    res = local_world.run("list", "--no-such-flag")
    assert res.code == 2
    assert "error:" in res.out

    res = local_world.run("get", "--repo", str(local_world.repo), "--holder", "x", "--bogus", "--json")
    assert res.code == 2
    assert res.data["code"] == "usage"


def test_unknown_field_exits_2_and_lists_valid_fields(local_world):
    res = local_world.run("list", "--repo", str(local_world.repo), "--fields", "slot,nope")
    assert res.code == 2
    assert "Valid fields" in res.out


def test_every_command_has_help(local_world):
    for command in ["get", "return", "list", "lease", "destroy"]:
        res = local_world.run(command, "--help")
        assert res.code == 0, command
        assert res.out.isascii()
    top = local_world.run("--help")
    assert top.code == 0
    for code in ["0 ", "1 ", "2 ", "3 ", "4 "]:
        assert f"  {code}" in top.out


def test_destroy_is_a_dry_run_by_default_and_refuses_in_use(local_world):
    w = local_world
    got = w.get()
    refused = w.run("destroy", got["path"], "--json")
    assert refused.code == 1
    assert refused.data["code"] == "in-use"

    assert w.run("return", got["path"], "--lease", got["lease"]).code == 0
    dry = w.run("destroy", got["path"], "--json")
    assert dry.code == 0
    assert dry.data["dry_run"] is True and dry.data["removed"] is False
    assert (w.repo.parent / f"{w.repo.name}.worktrees" / got["slot"]).is_dir()

    real = w.run("destroy", got["path"], "--yes", "--json")
    assert real.code == 0, real.out
    assert real.data["removed"] is True
    assert not (w.repo.parent / f"{w.repo.name}.worktrees" / got["slot"]).exists()
    assert w.list() == []
