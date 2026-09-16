"""Replace a directory's contents all at once, so a failure leaves the old bytes where they were.

A pull or a cache refresh mirrors what the Gateway holds, which means it deletes local files. Doing
that in place - delete first, then write - loses the old files the moment anything goes wrong half
way: a partial answer, content that does not decode, a full disk, a killed process. So a replace
works like this, all inside one hidden work directory in the target (WORK_DIR):

1. Take the work directory's lock, so two commands never replace the same directory at once.
2. Finish off whatever an earlier, interrupted replace left behind (see "Recovery" below).
3. Write the new entries into `incoming/`. Nothing live has changed yet.
4. Write a journal naming every entry that is about to move out and in, then move the old entries
   into `outgoing/` and the new ones into place, one rename each.
5. Mark the journal committed, then delete `outgoing/`, `incoming/` and the journal.

Recovery. Every replace, and every read by these commands of a directory they replace (`recover`),
first puts right what an interrupted replace left:
- no journal: the new entries were never moving, so `incoming/` is deleted and the live directory
  is already the old one;
- a journal that is not committed: the replace is ROLLED BACK - each new entry that reached the
  live directory goes back to `incoming/`, each old entry comes back from `outgoing/`, and the
  directory holds the old contents again;
- a committed journal: every move finished, so only the work files are deleted.
Recovery never deletes an old entry it cannot account for: an `outgoing/` that still holds files
with no journal to explain them is reported as an error and left for a person.

WHAT IS GUARANTEED. If a replace raises, or its process is terminated at any point, then once the
next replace or `recover` call on that directory returns, the directory holds either exactly its
old owned entries (the replace had not committed) or exactly the new ones (it had) - never a mix.
A replace that raises has already rolled back before it returns.

WHAT IS NOT GUARANTEED.
- Between the interruption and the next call, the directory can be missing entries. Anything that
  reads it without calling `recover` first can see that.
- Nothing is flushed to disk (no fsync), so a power cut or an operating system crash can lose
  renames or file contents that had not been written out yet.
- Another program editing the directory while a replace runs is not detected.
- The lock is an operating system file lock; on a network filesystem that does not honour file
  locks, two concurrent replaces of one directory are not kept apart.

The caller validates the Gateway's whole answer BEFORE calling this; this module is the second line,
for failures only the filesystem or the process can produce.
"""

from __future__ import annotations

import contextlib
import json
import os
import shutil
import time
from pathlib import Path
from typing import Callable, Iterable, Iterator, List, Optional

#: The hidden directory inside every replaced directory that holds the lock and a replace in flight.
#: It is never replaced itself, and a writer must never be handed a file of its own at this path.
WORK_DIR = ".bundle-swap"
_LOCK = "lock"
_JOURNAL = "journal.json"
_INCOMING = "incoming"
_OUTGOING = "outgoing"
_MOVING = "moving"
_COMMITTED = "committed"
LOCK_TIMEOUT_SECONDS = 120.0


def replace_directory(
    target: Path,
    build: Callable[[Path], None],
    managed: Optional[Iterable[str]] = None,
    before_commit: Optional[Callable[[], None]] = None,
) -> None:
    """Replace the entries of `target` with what `build` writes into an empty staging directory.

    `managed` names the top-level entries of `target` this replace owns; every other entry is left
    alone. None means the replace owns the whole directory (except WORK_DIR), so every existing
    entry is replaced. `build` may only write entries it owns.

    `before_commit` runs after every entry has moved and before the replace is marked committed,
    so a failure in it rolls the replace back. It is for a file OUTSIDE `target` that vouches for
    the old contents and must stop vouching before the new contents become final."""
    target.mkdir(parents=True, exist_ok=True)
    absolute = target.resolve()
    owned = None if managed is None else set(managed)
    if owned is not None and WORK_DIR in owned:
        raise ValueError(f"'{WORK_DIR}' is this module's own work directory and cannot be managed")
    with _locked(absolute) as work:
        _recover(work, absolute)
        staging = work / _INCOMING
        aside = work / _OUTGOING
        staging.mkdir()
        try:
            build(staging)
            incoming = sorted(p.name for p in staging.iterdir())
            stray = [n for n in incoming if n == WORK_DIR or (owned is not None and n not in owned)]
            if stray:
                raise ValueError(f"refusing to write entries this replace does not own: {stray}")
            outgoing = sorted(
                p.name for p in absolute.iterdir()
                if p.name != WORK_DIR and (owned is None or p.name in owned)
            )
            aside.mkdir()
            _write_journal(work, _MOVING, outgoing, incoming)
        except BaseException:
            # Nothing live has moved: the new entries and the empty aside are simply discarded.
            _discard(work)
            raise
        try:
            for name in outgoing:
                os.replace(absolute / name, aside / name)
            for name in incoming:
                os.replace(staging / name, absolute / name)
            if before_commit is not None:
                before_commit()
        except BaseException:
            # If this rollback itself fails, the journal is still there and the next call retries it.
            _roll_back(work, absolute, outgoing, incoming)
            raise
        _write_journal(work, _COMMITTED, outgoing, incoming)
        _discard(work)


def recover(target: Path) -> None:
    """Put right whatever an interrupted replace left in `target`, so what a caller reads next is
    whole. Waits for a replace another process is running. Does nothing to a directory no replace
    has touched, and never creates `target`."""
    if not (target / WORK_DIR).is_dir():
        return
    absolute = target.resolve()
    with _locked(absolute) as work:
        _recover(work, absolute)


def _recover(work: Path, absolute: Path) -> None:
    journal = _read_journal(work)
    if journal is None:
        aside = work / _OUTGOING
        if aside.is_dir() and any(aside.iterdir()):
            raise OSError(
                f"{aside} holds files but there is no journal saying where they came from, so they "
                f"were left alone. Check them, move any you need back into {absolute}, then delete "
                f"{work}."
            )
        _discard(work)
        return
    phase, outgoing, incoming = journal
    if phase == _MOVING:
        _roll_back(work, absolute, outgoing, incoming)
    else:
        _discard(work)


def _roll_back(work: Path, absolute: Path, outgoing: List[str], incoming: List[str]) -> None:
    """Undo a replace that did not commit. Every step is a rename that a later retry can see was
    done, so this is safe to run again from any point it was interrupted at.

    Old entries all move out before any new entry moves in, so for an entry that is both old and
    new: while its old copy is not yet in `outgoing/`, the live one is still the old one."""
    staging = work / _INCOMING
    aside = work / _OUTGOING
    staging.mkdir(exist_ok=True)
    was_old = set(outgoing)
    for name in incoming:
        live = absolute / name
        if not os.path.lexists(live):
            continue
        if name in was_old and not os.path.lexists(aside / name):
            continue
        if os.path.lexists(staging / name):
            raise OSError(
                f"cannot roll back {absolute}: '{name}' is both live and still staged. "
                f"Check {absolute} and {work} by hand."
            )
        os.replace(live, staging / name)
    for name in outgoing:
        kept = aside / name
        if not os.path.lexists(kept):
            continue
        if os.path.lexists(absolute / name):
            raise OSError(
                f"cannot roll back {absolute}: '{name}' is in the way of its own old copy in "
                f"{aside}. Check both by hand."
            )
        os.replace(kept, absolute / name)
    _discard(work)


def _discard(work: Path) -> None:
    """Delete the work files of a replace that is finished either way. The journal goes last, so an
    interruption here leaves a journal that a retry acts on again."""
    for name in (_OUTGOING, _INCOMING):
        if (work / name).is_dir():
            shutil.rmtree(work / name)
    for name in (_JOURNAL + ".tmp", _JOURNAL):
        with contextlib.suppress(FileNotFoundError):
            (work / name).unlink()


def _write_journal(work: Path, phase: str, outgoing: List[str], incoming: List[str]) -> None:
    temporary = work / (_JOURNAL + ".tmp")
    temporary.write_text(
        json.dumps({"phase": phase, "outgoing": outgoing, "incoming": incoming}), encoding="utf-8"
    )
    os.replace(temporary, work / _JOURNAL)


def _read_journal(work: Path):
    path = work / _JOURNAL
    if not path.is_file():
        return None
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        phase = data["phase"]
        outgoing = data["outgoing"]
        incoming = data["incoming"]
        valid = (
            phase in (_MOVING, _COMMITTED)
            and all(isinstance(n, str) for n in outgoing + incoming)
        )
    except (ValueError, KeyError, TypeError) as exc:
        raise OSError(f"the replace journal {path} could not be read ({exc}); check {work} by hand.") from exc
    if not valid:
        raise OSError(f"the replace journal {path} is not one this command wrote; check {work} by hand.")
    return phase, outgoing, incoming


@contextlib.contextmanager
def _locked(absolute: Path) -> Iterator[Path]:
    """Hold the work directory's lock. The lock file is removed on the way out so a finished
    directory carries no work directory; a waiter that locked the removed file notices and retries."""
    work = absolute / WORK_DIR
    deadline = time.monotonic() + LOCK_TIMEOUT_SECONDS
    while True:
        work.mkdir(exist_ok=True)
        try:
            fd = os.open(work / _LOCK, os.O_RDWR | os.O_CREAT, 0o600)
        except FileNotFoundError:
            # The holder removed the work directory between the two calls.
            _wait_or_give_up(absolute, deadline)
            continue
        try:
            _acquire(fd, absolute, deadline)
        except BaseException:
            os.close(fd)
            raise
        if os.name == "nt":
            break  # Windows cannot remove a file someone has open, so this one is still current.
        try:
            same = os.path.samestat(os.fstat(fd), os.stat(work / _LOCK))
        except FileNotFoundError:
            same = False
        if same:
            break
        _release(fd)
        os.close(fd)
    try:
        yield work
    finally:
        if os.name != "nt":
            # Removed while still held, so no one can lock this file and believe it current.
            with contextlib.suppress(OSError):
                (work / _LOCK).unlink()
        _release(fd)
        os.close(fd)
        if os.name == "nt":
            # Windows refuses to remove a file another process has open, which is what keeps this safe.
            with contextlib.suppress(OSError):
                (work / _LOCK).unlink()
        with contextlib.suppress(OSError):
            work.rmdir()


if os.name == "nt":
    import msvcrt

    def _acquire(fd: int, absolute: Path, deadline: float) -> None:
        while True:
            os.lseek(fd, 0, os.SEEK_SET)
            try:
                msvcrt.locking(fd, msvcrt.LK_NBLCK, 1)
                return
            except OSError:
                _wait_or_give_up(absolute, deadline)

    def _release(fd: int) -> None:
        os.lseek(fd, 0, os.SEEK_SET)
        with contextlib.suppress(OSError):
            msvcrt.locking(fd, msvcrt.LK_UNLCK, 1)

else:
    import fcntl

    def _acquire(fd: int, absolute: Path, deadline: float) -> None:
        while True:
            try:
                fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
                return
            except BlockingIOError:
                _wait_or_give_up(absolute, deadline)

    def _release(fd: int) -> None:
        fcntl.flock(fd, fcntl.LOCK_UN)


def _wait_or_give_up(absolute: Path, deadline: float) -> None:
    if time.monotonic() >= deadline:
        raise OSError(
            f"another command has been replacing {absolute} for over {int(LOCK_TIMEOUT_SECONDS)} "
            "seconds; wait for it to finish and run this again."
        )
    time.sleep(0.05)
