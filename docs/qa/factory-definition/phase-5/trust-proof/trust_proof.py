"""Factory Definition, phase 5, P7: where Claude Code records folder trust, and what a trusted parent covers.

Runs the REAL Claude Code on this machine, interactively, in a pseudo-terminal, in scratch folders under the
temporary directory, and reads its first screen: either the folder-trust question ("Quick safety check") or the
ready prompt. No prompt is ever sent to the agent, so no model is called.

CLEAN-UP DELETES ONLY WHAT THIS RUN MADE, BY EXACT PATH - NEVER BY A NAME PATTERN (the Ledger below). It records the
scratch root it made, every folder it made or had git make inside it, and every folder it started Claude Code in, and
it snapshots the ~/.claude.json project keys and the ~/.claude/projects folders that existed before it started. It then
deletes: a project key only when it is exactly the key of a folder in the ledger AND was absent before the run; a
~/.claude/projects folder only when it is exactly the one Claude Code names for a folder the agent ran in AND was absent
before the run; the scratch root only by its recorded path, directly under the temporary directory; and the lock only
when this run made it. Anything else is left alone and reported.

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
PROJECTS = Path.home() / ".claude" / "projects"
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


def take_lock(timeout: float = 30.0) -> None:
    """Make the lock, or wait for its holder to remove it. A lock this run did not make is NEVER removed here, stale or
    not: a running Claude Code clears a stale one on its own next write."""
    deadline = time.monotonic() + timeout
    while True:
        try:
            os.mkdir(LOCK)
            return
        except FileExistsError:
            if time.monotonic() > deadline:
                raise SystemExit(f"{LOCK} stayed held for {timeout}s and is not this run's to remove; nothing was written.")
            time.sleep(0.05)


def edit_config(change) -> None:
    take_lock()
    # From here the lock is this run's own: it is removed below, and by this function only.
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


def project_keys() -> set[str]:
    return set(json.loads(CONFIG.read_text(encoding="utf-8")).get("projects", {}))


def claude_project_folder(folder: Path) -> Path:
    """The folder Claude Code keeps a session's transcript in: the working folder's full path with every character that
    is not a letter or digit replaced by '-' (as ~/.claude/projects shows, e.g. D--ReposFred--wt-fd-p5-p7)."""
    return PROJECTS / "".join(c if c.isascii() and c.isalnum() else "-" for c in str(folder.resolve()))


class Ledger:
    """Everything this run made, by exact path. Clean-up deletes what is in here and nothing else."""

    def __init__(self, prefix: str):
        self.temp = Path(tempfile.gettempdir()).resolve()
        self.keys_before = project_keys()
        self.project_folders_before = {d.name for d in PROJECTS.iterdir()} if PROJECTS.exists() else set()
        self.root = self.temp / f"{prefix}-{uuid.uuid4().hex[:8]}"
        self.root.mkdir()  # fails if it exists: the root is always new
        self.folders: list[Path] = [self.root]
        self.agent_folders: list[Path] = []

    def make(self, folder: Path) -> Path:
        """Make a folder inside the root, recording it and every parent it had to make."""
        folder = folder.resolve()
        self._inside(folder)
        missing = []
        p = folder
        while not p.exists():
            missing.append(p)
            p = p.parent
        folder.mkdir(parents=True)
        self.folders.extend(reversed(missing))
        return folder

    def made_by_git(self, folder: Path) -> Path:
        """Record a folder git made at a path this run named."""
        folder = folder.resolve()
        self._inside(folder)
        self.folders.append(folder)
        return folder

    def agent_ran_in(self, folder: Path) -> None:
        self.agent_folders.append(folder.resolve())

    def _inside(self, folder: Path) -> None:
        if self.root not in folder.parents and folder != self.root:
            raise SystemExit(f"{folder} is not inside this run's scratch root {self.root}; refused.")

    def clean_up(self) -> None:
        say("")
        say("CLEAN-UP (only what the ledger holds, by exact path)")
        # 1. Project keys: exactly a ledger folder's key, and absent before the run.
        ours = {key(f) for f in self.folders} - self.keys_before
        removed: list[str] = []

        def drop(projects):
            for k in sorted(ours & set(projects)):
                removed.append(k)
                del projects[k]

        edit_config(drop)
        for k in removed:
            say(f"  removed projects['{k}']")
        say(f"  project keys of ledger folders left: {len(ours & project_keys())}")
        # 2. Claude Code's transcript folders: exactly the one named for a folder the agent ran in, absent before.
        for folder in dict.fromkeys(self.agent_folders):
            named = claude_project_folder(folder)
            if named.name in self.project_folders_before:
                say(f"  KEPT {named.name}: it existed before this run")
                continue
            if named.parent.resolve() != PROJECTS.resolve():
                say(f"  KEPT {named}: it is not directly under {PROJECTS}")
                continue
            if named.is_dir():
                shutil.rmtree(named)
                say(f"  removed Claude Code's folder for {key(folder)}: {not named.exists()}")
        # 3. The scratch root, by its recorded path, only directly under the temporary directory.
        if self.root.parent != self.temp:
            raise SystemExit(f"{self.root} is not directly under {self.temp}; refused.")

        def writable_then_delete(func, path, _exc):  # git writes its objects read-only
            os.chmod(path, 0o666)
            func(path)

        for _ in range(20):  # a killed agent's child processes can hold a folder open for a few seconds
            try:
                shutil.rmtree(self.root, onexc=writable_then_delete)
            except OSError:
                pass
            if not self.root.exists():
                break
            time.sleep(1.0)
        say(f"  scratch root exists afterwards: {self.root.exists()}")
        say(f"  {LOCK.name} exists afterwards: {LOCK.exists()}")


def entries_under(prefix: str) -> dict:
    """READ ONLY, for the report: the project entries under the scratch root. Never used to choose a delete."""
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
        LEDGER.agent_ran_in(folder)
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
    LEDGER.make(path)
    git("init", "-q", cwd=path)
    (path / "readme.txt").write_text("scratch\n", encoding="ascii")
    git("add", ".", cwd=path)
    git("-c", "user.name=proof", "-c", "user.email=proof@example.invalid", "commit", "-q", "-m", "scratch", cwd=path)


def add_worktree(repo: Path, slot: Path) -> None:
    git("worktree", "add", "-q", "--detach", str(slot), cwd=repo)
    LEDGER.made_by_git(slot)


# ---------------------------------------------------------------- the cases


LEDGER: Ledger  # set by the script that runs: main() here, or prompt_file_proof.py


def main() -> int:
    global LEDGER
    v = subprocess.run([CLAUDE, "--version"], capture_output=True, text=True).stdout.strip()
    LEDGER = Ledger("fd-p7-trust")
    scratch = LEDGER.root
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
        a = LEDGER.make(scratch / "plain-a")
        first_screen(a, accept=True, recorded=lambda: any(v.get("hasTrustDialogAccepted") for v in entries_under(sk).values()))
        for k2, v2 in entries_under(sk).items():
            say(f"  recorded: projects['{k2}'].hasTrustDialogAccepted = {v2.get('hasTrustDialogAccepted')}")
        say("  the same folder again:")
        first_screen(a)

        say("")
        say("CASE B - a trusted PARENT (not a git repository): a new folder two levels beneath it.")
        parent = scratch / "factory-run"
        child = LEDGER.make(parent / "11111111-2222-3333-4444-555555555555" / "work")
        trust(parent)
        first_screen(child)
        say("  a sibling of the trusted parent, for contrast:")
        sib = LEDGER.make(scratch / "factory-run-sibling" / "work")
        first_screen(sib)

        say("")
        say("CASE C - a git worktree (a pooled slot) of an untrusted repository. Accept, then read what was recorded.")
        repo = scratch / "repo"
        make_repo(repo)
        pool = LEDGER.make(scratch / "repo.worktrees")
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
        pool3 = LEDGER.make(scratch / "repo3.worktrees")
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
        pool4 = LEDGER.make(scratch / "repo4.worktrees")
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
        LEDGER.make(r2wt.parent)
        add_worktree(repo2, r2wt)
        first_screen(r2wt)
    finally:
        LEDGER.clean_up()
        OUT.write_text("\n".join(lines) + "\n", encoding="ascii", errors="replace")
    return 0


if __name__ == "__main__":
    sys.exit(main())
