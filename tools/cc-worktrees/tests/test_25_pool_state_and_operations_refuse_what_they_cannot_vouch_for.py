"""Guard inventory, fix round 2: the pool-state, registry and operation guards that no earlier test could
tell from their absence. State that is wrong in a way the earlier corruptions did not cover, a registry
that is incomplete, a state that changes while the remote is fetched, and each command's own refusals."""

from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

import pytest

from conftest import TOOL_DIR, commit_file, git, make_world

EXIT_HELD = 3


def _key(path) -> str:
    return os.path.normcase(os.path.realpath(path))


def _slots_dir(w) -> Path:
    return w.repo.parent / f"{w.repo.name}.worktrees"


def _error(call):
    """The ToolError a call ends with, or None when it succeeded."""
    try:
        call()
    except Exception as ex:  # the test asserts which error it was, so nothing is swallowed
        return ex
    return None


def _code(ex) -> str:
    return getattr(ex, "code", f"escaped {type(ex).__name__}")


def _rewrite_state(w, change) -> None:
    state = w.state_file()
    data = json.loads(state.read_text(encoding="utf-8"))
    change(data)
    state.write_text(json.dumps(data, indent=2) + "\n", encoding="ascii")


def _two_repositories(tmp_path):
    (tmp_path / "a").mkdir()
    (tmp_path / "b").mkdir()
    a = make_world(tmp_path / "a", "local")
    b = make_world(tmp_path / "b", "local")
    b.home = a.home
    return a, b


def _unreachable(w) -> None:
    git(w.repo, "remote", "set-url", "origin", str(w.tmp / "no-such-remote.git"))


# --- state that parses but cannot be vouched for ------------------------------------------------------


def _slots_is_not_an_object(data, w):
    data["slots"] = [data["slots"]["wt01"]]


def _a_second_slot_whose_name_is_not_a_slot_name(data, w):
    data["slots"]["wt1"] = dict(data["slots"]["wt01"], path=str(_slots_dir(w) / "wt1"))


def _the_path_is_another_directory(data, w):
    data["slots"]["wt01"]["path"] = str(w.repo)


def _updated_is_not_text(data, w):
    data["slots"]["wt01"]["updated"] = 5


def _the_holder_is_empty_text(data, w):
    data["slots"]["wt01"]["holder"] = ""


CORRUPTIONS = [_slots_is_not_an_object, _a_second_slot_whose_name_is_not_a_slot_name,
               _the_path_is_another_directory, _updated_is_not_text, _the_holder_is_empty_text]


@pytest.mark.parametrize("corrupt", CORRUPTIONS, ids=[c.__name__.strip("_") for c in CORRUPTIONS])
def test_more_parseable_but_invalid_state_brings_every_slot_back_held(local_world, corrupt):
    w = local_world
    w.get(holder="owner")
    _rewrite_state(w, lambda data: corrupt(data, w))

    slots = w.list()

    assert [s["slot"] for s in slots] == ["wt01"]
    assert slots[0]["state"] == "held"
    assert slots[0]["reason"] == "state lost, cannot verify"


# --- the registry -------------------------------------------------------------------------------------


def test_a_registry_of_the_wrong_shape_is_an_error_not_a_list(local_world):
    w = local_world
    w.get()
    (w.home / "registry.json").write_text(json.dumps({"version": 1, "repos": "not a list"}), encoding="ascii")

    res = w.run("list", "--json")

    assert res.code == 1, res.out + res.err
    assert res.data["code"] == "unreadable-registry"


def test_a_pool_state_file_that_cannot_be_read_stops_the_registry_being_rebuilt_without_it(tmp_path):
    a, b = _two_repositories(tmp_path)
    a.get()
    b.get()
    (a.home / "registry.json").unlink()
    for file in (a.home / "pools").glob("*.json"):
        if _key(json.loads(file.read_text(encoding="utf-8"))["repo"]) == _key(b.repo):
            file.write_text("not json", encoding="ascii")

    res = a.run("get", "--repo", str(a.repo), "--holder", "x", "--json")

    assert res.code == 1, res.out + res.err
    assert res.data["code"] == "unreadable-state"


def test_a_global_list_with_a_pool_missing_from_the_registry_is_an_error(tmp_path):
    a, b = _two_repositories(tmp_path)
    a.get()
    b.get()
    registry = a.home / "registry.json"
    data = json.loads(registry.read_text(encoding="utf-8"))
    data["repos"] = [r for r in data["repos"] if _key(r) != _key(b.repo)]
    registry.write_text(json.dumps(data), encoding="ascii")

    res = a.run("list", "--json")

    assert res.code == 1, res.out + res.err
    assert res.data["code"] == "no-inventory"


def test_a_clone_whose_git_directory_is_not_dot_git_is_refused(local_world):
    w = local_world
    clone = w.tmp / "separate-clone"
    git(w.tmp, "clone", "-q", "--separate-git-dir", str(w.tmp / "separate-git"), w.remote_url, str(clone))

    res = w.run("get", "--repo", str(clone), "--holder", "x", "--json")

    assert res.code == 1, res.out + res.err
    assert res.data["code"] == "unsupported-repository"


# --- return, get ----------------------------------------------------------------------------------------


def test_returning_a_free_slot_is_refused_as_not_in_use(local_world):
    w = local_world
    got = w.get()
    assert w.run("return", got["path"], "--lease", got["lease"]).code == 0

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == 1, res.out + res.err
    assert res.data["code"] == "not-in-use"


def test_a_lease_that_changes_while_the_remote_is_fetched_refuses_the_return(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    original = landed.fetch_default

    def fetch_while_someone_else_takes_the_slot(cwd):
        answer = original(cwd)
        _rewrite_state(w, lambda data: data["slots"][got["slot"]].update(holder="someone else", lease="f" * 32))
        return answer

    monkeypatch.setattr(landed, "fetch_default", fetch_while_someone_else_takes_the_slot)

    error = _error(lambda: pool.return_slot(got["path"], got["lease"], None))

    monkeypatch.undo()
    assert _code(error) == "lease-mismatch"
    slot = w.slot(got["slot"])
    assert slot["state"] == "in-use" and slot["holder"] == "someone else"


def test_get_with_an_unreachable_remote_hands_nothing_out(local_world):
    w = local_world
    _unreachable(w)

    res = w.run("get", "--repo", str(w.repo), "--holder", "x", "--json")

    assert res.code == 1, res.out + res.err
    assert res.data["code"] == "cannot-fetch"
    assert not _slots_dir(w).exists()


def test_get_holds_a_free_slot_that_gained_unlanded_work_and_hands_out_another(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    assert w.run("return", got["path"], "--lease", got["lease"]).code == 0
    sha = commit_file(path, "work.txt", "unpushed\n", "unpushed work in a free slot")

    second = w.get(holder="next")

    assert second["slot"] != got["slot"]
    slot = w.slot(got["slot"])
    assert slot["state"] == "held" and sha[:12] in slot["reason"]
    assert git(path, "rev-parse", "HEAD") == sha


def test_get_never_creates_a_slot_over_a_file_already_in_the_slots_directory(local_world):
    w = local_world
    _slots_dir(w).mkdir()
    (_slots_dir(w) / "wt01").write_text("not a slot\n", encoding="utf-8")

    got = w.get()

    assert got["slot"] == "wt02"
    assert (_slots_dir(w) / "wt01").read_text(encoding="utf-8") == "not a slot\n"


# --- lease ---------------------------------------------------------------------------------------------


def test_leasing_an_in_use_slot_is_refused_as_in_use(local_world):
    w = local_world
    got = w.get()

    res = w.run("lease", got["slot"], "--repo", str(w.repo), "--holder", "x", "--json")

    assert res.code == 1, res.out + res.err
    assert res.data["code"] == "in-use"


def test_a_held_slot_is_not_leased_without_reclaim_held(local_world):
    w = local_world
    got = w.get()
    (Path(got["path"]) / "notes.txt").write_text("keep\n", encoding="utf-8")
    assert w.run("return", got["path"], "--lease", got["lease"]).code == EXIT_HELD

    res = w.run("lease", got["slot"], "--repo", str(w.repo), "--holder", "x", "--json")

    assert res.code == 1, res.out + res.err
    assert res.data["code"] == "held"
    assert w.slot(got["slot"])["state"] == "held"


def test_a_slot_taken_while_the_remote_is_fetched_is_not_leased_again(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    pool.return_slot(got["path"], got["lease"], None)
    original = landed.fetch_default

    def fetch_while_someone_else_takes_the_slot(cwd):
        answer = original(cwd)
        _rewrite_state(w, lambda data: data["slots"][got["slot"]].update(
            state="in-use", holder="someone else", lease="f" * 32))
        return answer

    monkeypatch.setattr(landed, "fetch_default", fetch_while_someone_else_takes_the_slot)

    error = _error(lambda: pool.lease_slot(got["slot"], "x", False, str(w.repo)))

    monkeypatch.undo()
    assert _code(error) == "changed"
    slot = w.slot(got["slot"])
    assert slot["state"] == "in-use" and slot["holder"] == "someone else"


def test_leasing_a_free_slot_with_an_unreachable_remote_holds_it(local_world):
    w = local_world
    got = w.get()
    assert w.run("return", got["path"], "--lease", got["lease"]).code == 0
    _unreachable(w)

    res = w.run("lease", got["slot"], "--repo", str(w.repo), "--holder", "x", "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    slot = w.slot(got["slot"])
    assert slot["state"] == "held" and slot["reason"].startswith("cannot verify")


def test_leasing_a_free_slot_that_gained_unlanded_work_holds_it(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    assert w.run("return", got["path"], "--lease", got["lease"]).code == 0
    sha = commit_file(path, "work.txt", "unpushed\n", "unpushed work in a free slot")

    res = w.run("lease", got["slot"], "--repo", str(w.repo), "--holder", "x", "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    slot = w.slot(got["slot"])
    assert slot["state"] == "held" and sha[:12] in slot["reason"]
    assert git(path, "rev-parse", "HEAD") == sha


# --- destroy -------------------------------------------------------------------------------------------


def test_destroy_refuses_a_held_slot_without_allow_held(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    (path / "notes.txt").write_text("keep\n", encoding="utf-8")
    assert w.run("return", got["path"], "--lease", got["lease"]).code == EXIT_HELD
    (path / "notes.txt").unlink()

    res = w.run("destroy", got["slot"], "--repo", str(w.repo), "--yes", "--json")

    assert res.code == 1, res.out + res.err
    assert res.data["code"] == "held"
    assert path.is_dir()


def test_a_slot_taken_while_the_remote_is_fetched_is_not_destroyed(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    pool.return_slot(got["path"], got["lease"], None)
    original = landed.fetch_default

    def fetch_while_someone_else_takes_the_slot(cwd):
        answer = original(cwd)
        _rewrite_state(w, lambda data: data["slots"][got["slot"]].update(
            state="in-use", holder="someone else", lease="f" * 32))
        return answer

    monkeypatch.setattr(landed, "fetch_default", fetch_while_someone_else_takes_the_slot)

    error = _error(lambda: pool.destroy_slot(got["path"], True, False, False, None))

    monkeypatch.undo()
    assert _code(error) == "in-use"
    assert Path(got["path"]).is_dir()


def test_destroy_with_an_unreachable_remote_is_held_and_removes_nothing(local_world):
    w = local_world
    got = w.get()
    assert w.run("return", got["path"], "--lease", got["lease"]).code == 0
    _unreachable(w)

    res = w.run("destroy", got["path"], "--yes", "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert Path(got["path"]).is_dir()
    assert w.slot(got["slot"])["state"] == "held"


# --- the machine-wide lock and the entry point ----------------------------------------------------------


def test_the_pool_lock_is_held_while_a_new_slot_is_decided(in_process, monkeypatch):
    w, pool, landed = in_process
    original = landed.mark_new_slot
    probes: list[str] = []

    def mark_while_another_process_tries_the_pool_lock(*args, **kwargs):
        script = (
            "import sys; sys.path.insert(0, sys.argv[1])\n"
            "from pathlib import Path\n"
            "from errors import ToolError\n"
            "from statelock import machine_lock\n"
            "try:\n"
            "    with machine_lock(Path(sys.argv[2]), timeout=0.5):\n"
            "        print('taken')\n"
            "except ToolError:\n"
            "    print('busy')\n")
        out = subprocess.run([sys.executable, "-c", script, str(TOOL_DIR / "src"), str(w.home)],
                             capture_output=True, text=True, check=True).stdout.strip()
        probes.append(out)
        return original(*args, **kwargs)

    monkeypatch.setattr(landed, "mark_new_slot", mark_while_another_process_tries_the_pool_lock)

    pool.get(str(w.repo), "owner", 4)

    assert probes == ["busy"]


def test_an_unexpected_failure_exits_one_and_says_so(in_process, monkeypatch, capsys):
    w, pool, landed = in_process
    import cli

    def fails(*args, **kwargs):
        raise RuntimeError("simulated")

    monkeypatch.setattr(pool, "list_slots", fails)

    code = cli.main(["list", "--repo", str(w.repo), "--json"])

    assert code == 1
    assert json.loads(capsys.readouterr().out)["code"] == "internal"
