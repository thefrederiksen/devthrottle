"""What a skill or workflow writer checks BEFORE it touches the disk.

`skill pull`, `workflow pull` and both caches replace old files with the Gateway's. A value that
cannot become a file - text that is not valid UTF-8, a name the operating system refuses - must be
found while the whole answer is being checked, not half way through the writes: by then the old files
are already gone. So every checker turns each value into the exact bytes it will write, and each name
into the exact path it will write to, and refuses the answer if either cannot be done. The write step
that follows only writes prepared bytes to checked paths.
"""

from __future__ import annotations

import os
import re
from pathlib import Path
from typing import Callable, Iterable, Optional

#: Characters no name may hold on at least one of Windows, macOS and Linux. The same bundle is pulled
#: on all three, so a name only one of them refuses is refused everywhere. A slash is not here: the
#: callers split or refuse it themselves.
_REFUSED_CHARACTERS = frozenset('<>:"|?*\\')

#: The characters the Gateway itself allows in one file name (`SkillValidation.SegmentPattern` and
#: `WorkflowValidation.FileNamePattern`): ASCII letters, digits, dot, dash and underscore. A name
#: outside this set is one the Gateway would never have stored, so an answer holding one is malformed
#: and is refused - this also refuses every control character (C0, DEL and C1 such as U+0085), every
#: space and every non-ASCII letter, whatever the operating system would make of them.
_GATEWAY_NAME = re.compile(r"[A-Za-z0-9._-]+")

#: The longest single name Windows, macOS and Linux all accept: 255 UTF-16 units on Windows, 255
#: UTF-8 bytes on macOS and Linux. UTF-8 is never shorter than UTF-16 in units, so bytes decide.
_MAX_NAME_BYTES = 255


def text_bytes(text: str) -> Optional[bytes]:
    """`text` as the UTF-8 bytes a writer will put on disk, or None when it cannot be written as
    UTF-8 (a lone surrogate, which JSON can carry and UTF-8 cannot)."""
    try:
        return text.encode("utf-8")
    except UnicodeEncodeError:
        return None


def name_problem(name: str) -> Optional[str]:
    """Why `name` - ONE file or folder name, no slashes - cannot be created on every platform, or
    None when it can."""
    encoded = text_bytes(name)
    if encoded is None:
        return "it cannot be written as UTF-8"
    if any(ord(ch) < 0x20 or 0x7F <= ord(ch) <= 0x9F for ch in name):
        # NUL ends a name for the operating system; the others are refused by Windows or break
        # every line-based listing of the directory (U+0085 is a line break to Unicode).
        return "it holds a control character"
    if not _GATEWAY_NAME.fullmatch(name):
        return "it holds a character the Gateway never allows (only letters, digits, dot, dash and underscore)"
    if _REFUSED_CHARACTERS.intersection(name):
        return 'it holds one of < > : " | ? * \\'
    if len(encoded) > _MAX_NAME_BYTES:
        return f"it is longer than {_MAX_NAME_BYTES} bytes"
    return None


def _existing_ancestor(path: Path) -> Path:
    for candidate in [path, *path.parents]:
        if candidate.exists():
            return candidate
    return Path(path.anchor or ".")


def check_paths(paths: Iterable[Path], refuse: Callable[[str], Exception]) -> None:
    """Refuse the answer when any of `paths` is longer than this machine allows.

    On macOS and Linux the limit is asked of the file system the files will land on. Windows has no
    such question: its limit depends on a machine-wide long-path setting, so a path over it is still
    found only when the write is made (and reported as an error then)."""
    if os.name == "nt":
        return
    paths = [p.absolute() for p in paths]
    if not paths:
        return
    limit = os.pathconf(_existing_ancestor(paths[0]), "PC_PATH_MAX")
    for path in paths:
        # PATH_MAX counts the ending NUL.
        if len(os.fsencode(str(path))) >= limit:
            raise refuse(f"would write to a path longer than this machine allows ({limit} bytes)")
