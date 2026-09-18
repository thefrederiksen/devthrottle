"""Step 1c, item 2: the command a refusal suggests must be one a user can copy and paste.

On the error path every help line went through the AXI value escape, the escaping used INSIDE a
quoted value. A help line is not a value: it is a command, and the escape turned every backslash in
it into two, so a Windows path came out as `D:\\\\ReposFred\\\\...` and could not be pasted. The
success path never did this, so the same tool printed the same path two different ways depending on
whether it had just refused.

Every refusal in the tool prints through one seam, `cli._emit_error`, so the first two tests hold for
all of them, and the rest drive the refusals a user actually meets end to end.

The rest of the AXI shape is unchanged: `error:` and `code:` are VALUES and are rendered by the
value renderer, which leaves a plain value - a Windows path among them - exactly as it is and quotes
and escapes anything that needs it, the same as every other value the tool prints.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

import cli                                  # noqa: E402
from errors import EXIT_HELD, ToolError     # noqa: E402

WINDOWS_PATH = r"D:\ReposFred\devthrottle\repo"
DOUBLED = r"D:\\ReposFred\\devthrottle\\repo"


def _refusals(w):
    """Every refusal a user meets from the command line, as (name, argv)."""
    repo = str(w.repo)
    got = w.get()
    slot = got["slot"]
    return got, [
        ("usage-missing-confirm", ["release", slot, "--repo", repo]),
        ("usage-unknown-flag", ["list", "--repo", repo, "--nonsense"]),
        ("unknown-slot", ["return", "wt99", "--repo", repo, "--lease", got["lease"]]),
        ("lease-mismatch", ["return", got["path"], "--lease", "not-the-lease"]),
        ("in-use", ["destroy", got["path"]]),
        ("in-use-lease", ["lease", slot, "--repo", repo, "--holder", "someone"]),
        ("not-held", ["release", slot, "--repo", repo, "--confirm-abandon"]),
        ("pool-full", ["get", "--repo", repo, "--holder", "another", "--pool-size", "1"]),
        ("not-a-pooled-worktree", ["return", str(w.tmp), "--lease", got["lease"]]),
        ("not-a-repository", ["get", "--repo", str(w.tmp / "nothing-here"), "--holder", "someone"]),
    ]


def _help_lines(text: str) -> list[str]:
    lines = text.splitlines()
    for index, line in enumerate(lines):
        if line.startswith("help[") and line.endswith("]:"):
            count = int(line[len("help["):-len("]:")])
            block = lines[index + 1: index + 1 + count]
            assert len(block) == count, f"help block says {count} lines, got {block}"
            assert all(line.startswith("  ") for line in block), block
            return [line[2:] for line in block]
    return []


# ---------------------------------------------------------------------------------------------------
# The one seam every refusal prints through
# ---------------------------------------------------------------------------------------------------


def test_a_refusals_help_line_holds_a_windows_path_exactly_once(capsys):
    err = ToolError("held", "wt01 is held", [f"cc-worktrees list --repo {WINDOWS_PATH}"], exit_code=EXIT_HELD)

    cli._emit_error(False, err)

    out = capsys.readouterr().out
    assert DOUBLED not in out, f"the help line was escaped and cannot be pasted:\n{out}"
    assert _help_lines(out) == [f"cc-worktrees list --repo {WINDOWS_PATH}"], out
    assert out.count(WINDOWS_PATH) == 1, out


def test_a_refusals_help_line_is_the_same_in_the_json(capsys):
    err = ToolError("held", "wt01 is held", [f"cc-worktrees list --repo {WINDOWS_PATH}"], exit_code=EXIT_HELD)

    cli._emit_error(True, err)

    payload = json.loads(capsys.readouterr().out)
    assert payload["help"] == [f"cc-worktrees list --repo {WINDOWS_PATH}"], payload


def test_a_refusals_message_names_a_windows_path_as_typed(capsys):
    err = ToolError("not-a-repository", f"{WINDOWS_PATH} is not a directory",
                    ["Pass --repo <path to a git repository>"])

    cli._emit_error(False, err)

    out = capsys.readouterr().out
    assert DOUBLED not in out, f"the message was escaped and no longer names the path:\n{out}"
    assert out.splitlines()[0] == f"error: {WINDOWS_PATH} is not a directory", out


def test_a_message_that_needs_quoting_is_still_rendered_as_a_value(capsys):
    """The AXI shape of the rest of the answer is unchanged: a value that cannot be written plainly
    is quoted and escaped, exactly as the value renderer does everywhere else in the tool."""
    err = ToolError("held", "wt01 is held: a.txt, b.txt are uncommitted", ["cc-worktrees list"])

    cli._emit_error(False, err)

    first = capsys.readouterr().out.splitlines()[0]
    assert first == 'error: "wt01 is held: a.txt, b.txt are uncommitted"', first


def test_the_error_block_keeps_its_axi_shape(capsys):
    err = ToolError("lease-mismatch", "wt01 is no longer held under that lease",
                    [f"cc-worktrees list --repo {WINDOWS_PATH}", "cc-worktrees get --repo <path> --holder <who>"])

    cli._emit_error(False, err)

    lines = capsys.readouterr().out.splitlines()
    assert lines[0] == "error: wt01 is no longer held under that lease", lines
    assert lines[1] == "code: lease-mismatch", lines
    assert lines[2] == "help[2]:", lines
    assert len(lines) == 5, lines


# ---------------------------------------------------------------------------------------------------
# Every refusal a user meets, end to end
# ---------------------------------------------------------------------------------------------------


def test_no_refusal_doubles_a_backslash_in_what_it_suggests(local_world):
    w = local_world
    got, cases = _refusals(w)
    checked = []
    for name, argv in cases:
        res = w.run(*argv)
        assert res.code != 0, f"{name} did not refuse: {res.out}{res.err}"
        assert "\\\\" not in res.out, f"{name} printed a doubled backslash:\n{res.out}"
        for line in _help_lines(res.out):
            assert "\\\\" not in line, f"{name} suggested a command nobody can paste: {line}"
        checked.append(name)
    assert checked == [name for name, _ in cases]
    assert w.slot(got["slot"])["state"] == "in-use", "a refusal changed the slot"


def test_every_refusal_that_names_the_repository_names_it_as_typed(local_world):
    w = local_world
    repo = str(w.repo)
    got, cases = _refusals(w)
    named = []
    for name, argv in cases:
        res = w.run(*argv)
        for line in _help_lines(res.out):
            words = line.split()
            if "--repo" not in words:
                continue
            value = line.split("--repo", 1)[1].strip()
            if value.startswith("<"):
                continue    # a placeholder for the user to fill in, not a path
            assert value.split(" --")[0].strip() == repo, f"{name} named the repository some other way: {line}"
            named.append(name)
        if got["path"] in res.out:
            assert res.out.count(got["path"]) >= 1
    assert named, "no refusal named the repository at all; the case is not staged"


def test_the_json_of_every_refusal_carries_the_same_help_lines(local_world):
    w = local_world
    _, cases = _refusals(w)
    for name, argv in cases:
        text = w.run(*argv)
        data = w.run(*argv, "--json")
        payload = json.loads(data.out)
        assert payload["help"] == _help_lines(text.out), f"{name}: {payload['help']} vs {_help_lines(text.out)}"
        for line in payload["help"]:
            assert "\\\\" not in line, f"{name} suggested a command nobody can paste in the json: {line}"


def test_a_held_slot_suggests_the_command_that_reads_it(local_world):
    """The refusal a user meets most often, and the one whose help lines carry a full Windows path."""
    w = local_world
    got = w.get()
    (Path(got["path"]) / "left-behind.txt").write_text("uncommitted\n", encoding="utf-8", newline="\n")

    res = w.run("return", got["path"], "--lease", got["lease"])

    assert res.code == EXIT_HELD, res.out + res.err
    lines = _help_lines(res.out)
    assert lines == [f"git -C {got['path']} status",
                     f"cc-worktrees return {got['path']} --lease {got['lease']}"], lines


@pytest.mark.parametrize("as_json", [False, True])
def test_a_usage_error_still_suggests_the_help(local_world, as_json):
    w = local_world
    res = w.run("release", "wt01", "--repo", str(w.repo), *(["--json"] if as_json else []))

    assert res.code == 2, res.out + res.err
    help_lines = json.loads(res.out)["help"] if as_json else _help_lines(res.out)
    assert help_lines == ["cc-worktrees --help"], res.out
