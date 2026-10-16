"""Factory Definition, phase 5, P7: where Claude Code records folder trust, and what a trusted parent covers.

Runs the REAL Claude Code on this machine, interactively, in a pseudo-terminal, in scratch folders under the
temporary directory, and reads its first screen: either the folder-trust question ("Quick safety check") or the
ready prompt. No prompt is ever sent to the agent, so no model is called. Every scratch folder is deleted afterwards
and every entry this run put in ~/.claude.json - or Claude Code put there for a scratch folder - is removed.

Every write to ~/.claude.json takes Claude Code's own lock first (the directory ~/.claude.json.lock, the
proper-lockfile protocol Claude Code uses), re-reads the file under it, changes only the one entry, and replaces the
file atomically (a temporary file in the same folder, then a rename) - the same way the Director's writer does.

Usage (from the repository root):
    uv run --no-project --with pywinpty --with pyte python docs/qa/factory-definition/phase-5/trust-proof/trust_proof.py
Output: trust-proof-output.txt beside this script.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import uuid
from pathlib import Path

import pyte
from winpty import PtyProcess

CLAUDE = shutil.which("claude") or str(Path.home() / ".local" / "bin" / "claude.exe")
CONFIG = Path.home() / ".claude.json"
LOCK = Path(str(CONFIG) + ".lock")
STALE_SECONDS = 10.0  # proper-lockfile's default stale age, which Claude Code keeps
OUT = Path(__file__).with_name("trust-proof-output.txt")
HOME_MARK = str(Path.home())
CLAUDE_ARGS: list[str] = []  # the trust cases launch the bare command; the prompt-file proof sets its own

lines: list[str] = []


def say(text: str = "") -> None:
    text = text.replace(HOME_MARK, "<home>").replace(HOME_MARK.replace("\\", "/"), "<home>")
    lines.append(text)
    print(text.encode("ascii", "replace").decode("ascii"), flush=True)


def key(folder: Path) -> str:
    """Claude Code keys a project on the full path with forward slashes."""
    return str(folder.resolve()).replace("\\", "/")


# ---------------------------------------------------------------- the lock-respecting writer


def take_lock(timeout: float = 10.0) -> None:
    deadline = time.monotonic() + timeout
    while True:
        try:
            os.mkdir(LOCK)
            return
        except FileExistsError:
            try:
                age = time.time() - LOCK.stat().st_mtime
            except FileNotFoundError:
                continue
            if age > STALE_SECONDS:
                say(f"  (a lock {age:.0f}s old is stale by Claude Code's own rule; removing it)")
                try:
                    os.rmdir(LOCK)
                except OSError:
                    pass
                continue
            if time.monotonic() > deadline:
                raise SystemExit(f"{LOCK} stayed held for {timeout}s; nothing was written.")
            time.sleep(0.05)


def edit_config(change) -> None:
    take_lock()
    try:
        data = json.loads(CONFIG.read_text(encoding="utf-8"))
        change(data.setdefault("projects", {}))
        tmp = CONFIG.with_name(f"{CONFIG.name}.tmp.{os.getpid()}.{uuid.uuid4().hex[:12]}")
        tmp.write_text(json.dumps(data, indent=2), encoding="utf-8")
        os.replace(tmp, CONFIG)
    finally:
        os.rmdir(LOCK)


def trust(folder: Path) -> None:
    k = key(folder)

    def change(projects):
        projects.setdefault(k, {})["hasTrustDialogAccepted"] = True

    edit_config(change)
    say(f"  wrote projects['{k}'].hasTrustDialogAccepted = true")


def entries_under(prefix: str) -> dict:
    projects = json.loads(CONFIG.read_text(encoding="utf-8")).get("projects", {})
    p = prefix.lower().replace("\\", "/")
    return {k: v for k, v in projects.items() if k.lower().replace("\\", "/").startswith(p)}


# ---------------------------------------------------------------- driving the real Claude Code


class Seat:
    def __init__(self, folder: Path):
        self.screen = pyte.Screen(120, 32)
        self.stream = pyte.Stream(self.screen)
        self.lock = threading.Lock()
        # The agent starts as a person's would: none of the variables of the session running this proof (a Claude Code
        # child-session marker, this session's fleet identity and pointer file) reach it.
        env = {k: v for k, v in os.environ.items() if not k.upper().startswith(("CLAUDE", "CC_"))}
        self.proc = PtyProcess.spawn([CLAUDE, *CLAUDE_ARGS], cwd=str(folder), dimensions=(32, 120), env=env)
        threading.Thread(target=self._read, daemon=True).start()

    def _read(self):
        while True:
            try:
                data = self.proc.read(4096)
            except Exception:
                return
            with self.lock:
                self.stream.feed(data)

    def text(self) -> str:
        with self.lock:
            return "\n".join(line.rstrip() for line in self.screen.display if line.strip())

    def wait_first_screen(self, timeout: float = 60.0) -> str:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            t = self.text()
            if "Quick safety check" in t and "Enter to confirm" in t:
                return "TRUST QUESTION"
            if "for shortcuts" in t or "bypass permissions" in t.lower() or "/effort" in t or "Opus" in t or "Sonnet" in t:
                time.sleep(1.0)
                if "Quick safety check" not in self.text():
                    return "READY PROMPT"
            time.sleep(0.25)
        return "NEITHER WITHIN 60s"

    def send(self, data: str):
        self.proc.write(data)

    def stop(self):
        try:
            self.proc.terminate(force=True)
        except Exception:
            pass


def first_screen(folder: Path, accept: bool = False, recorded=None) -> str:
    seat = Seat(folder)
    try:
        verdict = seat.wait_first_screen()
        say(f"  first screen in {key(folder)}: {verdict}")
        if verdict == "TRUST QUESTION":
            for line in seat.text().splitlines():
                if "trust" in line.lower() or "exit" in line.lower():
                    say(f"    | {line.strip()}")
        elif verdict != "READY PROMPT":
            for line in seat.text().splitlines():
                say(f"    | {line.strip()}")
        if accept and verdict == "TRUST QUESTION":
            time.sleep(1.5)  # let the question finish drawing before a key reaches it
            seat.send("\x1b[B")  # down arrow: the default choice is "No, exit"
            time.sleep(1.0)
            seat.send("\r")
            deadline = time.monotonic() + 60.0
            while time.monotonic() < deadline and "Quick safety check" in seat.text():
                time.sleep(0.25)
            after = "the question is gone" if "Quick safety check" not in seat.text() else "the question is STILL shown"
            say(f"  after choosing 'Yes, I trust this folder': {after}")
            # Claude Code writes the answer to its configuration file on its own schedule; stop the agent only once
            # the answer is on disk (or 60 seconds have passed, which the output then shows as nothing recorded).
            deadline = time.monotonic() + 60.0
            while recorded is not None and time.monotonic() < deadline and not recorded():
                time.sleep(0.5)
            time.sleep(1.0)
        return verdict
    finally:
        seat.stop()
        time.sleep(1.0)


def git(*args, cwd: Path):
    subprocess.run(["git", *args], cwd=str(cwd), check=True, capture_output=True, text=True)


def make_repo(path: Path) -> None:
    path.mkdir(parents=True)
    git("init", "-q", cwd=path)
    (path / "readme.txt").write_text("scratch\n", encoding="ascii")
    git("add", ".", cwd=path)
    git("-c", "user.name=proof", "-c", "user.email=proof@example.invalid", "commit", "-q", "-m", "scratch", cwd=path)


def add_worktree(repo: Path, slot: Path) -> None:
    git("worktree", "add", "-q", "--detach", str(slot), cwd=repo)


# ---------------------------------------------------------------- the cases


def main() -> int:
    v = subprocess.run([CLAUDE, "--version"], capture_output=True, text=True).stdout.strip()
    scratch = Path(tempfile.gettempdir()) / f"fd-p7-trust-{uuid.uuid4().hex[:8]}"
    scratch.mkdir()
    sk = key(scratch)
    say(f"Claude Code: {v}")
    say(f"Configuration file: {CONFIG}")
    say(f"Scratch root: {sk}")
    projects = json.loads(CONFIG.read_text(encoding="utf-8")).get("projects", {})
    p = scratch.resolve()
    while p.parent != p:
        p = p.parent
        say(f"  ancestor {key(p)}: hasTrustDialogAccepted={projects.get(key(p), {}).get('hasTrustDialogAccepted')}")

    try:
        say("")
        say("CASE A - a new plain folder, nothing trusted. Accept the question, then read what was recorded.")
        a = scratch / "plain-a"
        a.mkdir()
        first_screen(a, accept=True, recorded=lambda: any(v.get("hasTrustDialogAccepted") for v in entries_under(sk).values()))
        for k2, v2 in entries_under(sk).items():
            say(f"  recorded: projects['{k2}'].hasTrustDialogAccepted = {v2.get('hasTrustDialogAccepted')}")
        say("  the same folder again:")
        first_screen(a)

        say("")
        say("CASE B - a trusted PARENT (not a git repository): a new folder two levels beneath it.")
        parent = scratch / "factory-run"
        child = parent / "11111111-2222-3333-4444-555555555555" / "work"
        child.mkdir(parents=True)
        trust(parent)
        first_screen(child)
        say("  a sibling of the trusted parent, for contrast:")
        sib = scratch / "factory-run-sibling" / "work"
        sib.mkdir(parents=True)
        first_screen(sib)

        say("")
        say("CASE C - a git worktree (a pooled slot) of an untrusted repository. Accept, then read what was recorded.")
        repo = scratch / "repo"
        make_repo(repo)
        pool = scratch / "repo.worktrees"
        pool.mkdir()
        wt01 = pool / "wt01"
        add_worktree(repo, wt01)
        before = set(entries_under(sk))
        first_screen(wt01, accept=True, recorded=lambda: any(k not in before and v.get("hasTrustDialogAccepted") for k, v in entries_under(sk).items()))
        for k2, v2 in entries_under(sk).items():
            if k2 not in before:
                say(f"  recorded: projects['{k2}'].hasTrustDialogAccepted = {v2.get('hasTrustDialogAccepted')}")

        say("  (case C's answer trusted that repository, so cases D and E each use a repository of their own)")

        say("")
        say("CASE D - the POOL ROOT (repo3.worktrees, not itself a repository) trusted: a new slot beneath it.")
        repo3 = scratch / "repo3"
        make_repo(repo3)
        pool3 = scratch / "repo3.worktrees"
        pool3.mkdir()
        add_worktree(repo3, pool3 / "wt01")
        say("  control, before the pool root is trusted:")
        first_screen(pool3 / "wt01")
        trust(pool3)
        add_worktree(repo3, pool3 / "wt02")
        first_screen(pool3 / "wt02")

        say("")
        say("CASE E - the slot's OWN folder trusted, written before Claude Code ever opened it (repository untrusted).")
        repo4 = scratch / "repo4"
        make_repo(repo4)
        pool4 = scratch / "repo4.worktrees"
        pool4.mkdir()
        add_worktree(repo4, pool4 / "wt01")
        add_worktree(repo4, pool4 / "wt02")
        trust(pool4 / "wt01")
        first_screen(pool4 / "wt01")
        say("  the slot beside it, not trusted, for contrast:")
        first_screen(pool4 / "wt02")

        say("")
        say("CASE F - a different repository's main checkout trusted: a new worktree of it (for the record).")
        repo2 = scratch / "repo2"
        make_repo(repo2)
        trust(repo2)
        r2wt = scratch / "repo2.worktrees" / "wt01"
        r2wt.parent.mkdir()
        add_worktree(repo2, r2wt)
        first_screen(r2wt)
    finally:
        say("")
        say("CLEAN-UP")
        removed: list[str] = []

        def drop(projects):
            p2 = sk.lower()
            for k2 in [k3 for k3 in projects if k3.lower().replace("\\", "/").startswith(p2)]:
                removed.append(k2)
                del projects[k2]

        edit_config(drop)
        for k2 in sorted(removed):
            say(f"  removed projects['{k2}']")
        say(f"  entries left under the scratch root: {len(entries_under(sk))}")
        # The folders Claude Code made for the scratch sessions in its own projects folder go too.
        projects_dir = Path.home() / ".claude" / "projects"
        for folder in [d for d in projects_dir.iterdir() if scratch.name in d.name] if projects_dir.exists() else []:
            shutil.rmtree(folder, ignore_errors=True)
            say(f"  removed Claude Code's folder for a scratch session: {not folder.exists()}")
        # A killed agent's child processes can hold a folder open for a few seconds.
        # Git writes its objects read-only, which a plain delete on Windows refuses.
        def writable_then_delete(func, path, _exc):
            os.chmod(path, 0o666)
            func(path)

        for _ in range(20):
            try:
                shutil.rmtree(scratch, onexc=writable_then_delete)
            except OSError:
                pass
            if not scratch.exists():
                break
            time.sleep(1.0)
        say(f"  scratch root exists afterwards: {scratch.exists()}")
        say(f"  {LOCK.name} exists afterwards: {LOCK.exists()}")
        OUT.write_text("\n".join(lines) + "\n", encoding="ascii", errors="replace")
    return 0


if __name__ == "__main__":
    sys.exit(main())
