"""Replace a directory's contents all at once, so a failure leaves the old bytes where they were.

A pull or a cache refresh mirrors what the Gateway holds, which means it deletes local files. Doing
that in place - delete first, then write - loses the old files the moment anything goes wrong half
way: a partial answer, content that does not decode, a full disk. So the new contents are written
into a temporary sibling directory first, and only when every byte is on disk are the old entries
moved aside, the new ones moved in, and the old ones deleted. A failure while moving puts the old
entries back.

The caller validates the Gateway's whole answer BEFORE calling this; this module is the second line,
for failures only the filesystem can produce.
"""

from __future__ import annotations

import os
import shutil
import tempfile
from pathlib import Path
from typing import Callable, Iterable, Optional


def replace_directory(
    target: Path,
    build: Callable[[Path], None],
    managed: Optional[Iterable[str]] = None,
) -> None:
    """Replace the entries of `target` with what `build` writes into an empty staging directory.

    `managed` names the top-level entries of `target` this replace owns; every other entry is left
    alone. None means the replace owns the whole directory, so every existing entry is replaced.
    `build` may only write entries it owns."""
    target.mkdir(parents=True, exist_ok=True)
    absolute = target.resolve()
    owned = None if managed is None else set(managed)
    staging = Path(tempfile.mkdtemp(prefix=f".{absolute.name}.incoming-", dir=absolute.parent))
    aside: Optional[Path] = None
    try:
        build(staging)
        incoming = sorted(staging.iterdir())
        if owned is not None:
            stray = [p.name for p in incoming if p.name not in owned]
            if stray:
                raise ValueError(f"refusing to write entries this replace does not own: {stray}")

        aside = Path(tempfile.mkdtemp(prefix=f".{absolute.name}.outgoing-", dir=absolute.parent))
        outgoing = [p for p in sorted(absolute.iterdir()) if owned is None or p.name in owned]
        moved_out = []
        moved_in = []
        try:
            for path in outgoing:
                os.replace(path, aside / path.name)
                moved_out.append(path.name)
            for path in incoming:
                os.replace(path, absolute / path.name)
                moved_in.append(path.name)
        except BaseException:
            for name in moved_in:
                _remove(absolute / name)
            for name in moved_out:
                os.replace(aside / name, absolute / name)
            raise
        shutil.rmtree(aside)
        aside = None
    finally:
        shutil.rmtree(staging, ignore_errors=True)
        # An `aside` still holding entries means putting the old files back failed; it is the only
        # copy of them, so it is kept rather than deleted.
        if aside is not None and aside.is_dir() and not any(aside.iterdir()):
            aside.rmdir()


def _remove(path: Path) -> None:
    if path.is_dir() and not path.is_symlink():
        shutil.rmtree(path)
    else:
        path.unlink()
