"""A full pool refuses at once, before the fetch. The Director is given 30 seconds to answer the Gateway;
a pool-full refusal that waited on the remote first answered in 30 to 550 seconds on a real machine, so
the Gateway recorded "the Director did not answer" and the reason - which slots, held by whom - was lost."""

from __future__ import annotations

import time

from test_14_network_timeout import _hang_remote

EXIT_POOL_FULL = 4


def test_a_full_pool_refuses_without_waiting_on_the_remote(local_world):
    w = local_world
    a = w.get(holder="alpha", pool_size=2)
    b = w.get(holder="beta", pool_size=2)
    _hang_remote(w, 60)

    started = time.monotonic()
    res = w.run("get", "--repo", str(w.repo), "--holder", "gamma", "--pool-size", "2",
                env_extra={"CC_WORKTREES_NETWORK_TIMEOUT": "20"})
    elapsed = time.monotonic() - started

    assert res.code == EXIT_POOL_FULL, res.out + res.err
    assert elapsed < 15, f"a full pool waited {elapsed:.1f}s on the remote before refusing"
    assert a["slot"] in res.out and b["slot"] in res.out
    assert "alpha" in res.out and "beta" in res.out


def test_a_pool_with_a_free_slot_still_fetches_before_handing_it_out(local_world):
    w = local_world
    a = w.get(holder="alpha", pool_size=1)
    assert w.run("return", a["path"], "--lease", a["lease"], "--json").code == 0
    _hang_remote(w, 60)

    res = w.run("get", "--repo", str(w.repo), "--holder", "gamma", "--pool-size", "1",
                env_extra={"CC_WORKTREES_NETWORK_TIMEOUT": "3"})

    assert res.code == 1, res.out + res.err
    assert "cannot-fetch" in res.out + res.err, res.out + res.err
    assert w.slot(a["slot"])["state"] == "free"
