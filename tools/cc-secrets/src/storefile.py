"""The seam between the secret store and the disk.

Today there is one implementation: a plain JSON file private to this user (owner decision on issue
#2889). If encryption is added later it is a different StoreFile - the store and every command that
uses it stay as they are.
"""

from __future__ import annotations

import os
import secrets
from abc import ABC, abstractmethod
from pathlib import Path

from . import filelog, paths, permissions


class StoreFile(ABC):
    """Reads and writes the store's bytes."""

    @property
    @abstractmethod
    def location(self) -> Path:
        """Where the store is, for messages."""

    @abstractmethod
    def exists(self) -> bool:
        """True once the store has been written."""

    @abstractmethod
    def read(self) -> bytes:
        """The store's bytes."""

    @abstractmethod
    def write(self, data: bytes) -> None:
        """Replace the store's bytes atomically."""


class UserOnlyFile(StoreFile):
    """A file in the private secrets folder, replaced atomically through a temp file that is private
    from the moment it is created, so the plain text is never readable by anyone else, even briefly."""

    def __init__(self, path: Path) -> None:
        self._path = path

    @property
    def location(self) -> Path:
        return self._path

    def exists(self) -> bool:
        return self._path.exists()

    def read(self) -> bytes:
        paths.ensure_home()
        note = permissions.ensure_private_file(self._path)
        if note:
            filelog.write(f"[UserOnlyFile] read: {note}")
        return self._path.read_bytes()

    def write(self, data: bytes) -> None:
        folder = paths.ensure_home()
        if self._path.parent != folder:
            note = permissions.ensure_private_folder(self._path.parent)
            if note:
                filelog.write(f"[UserOnlyFile] write: {note}")
        temp = self._path.parent / f".{self._path.name}.{secrets.token_hex(8)}.tmp"
        descriptor = permissions.create_private_file(temp)
        try:
            with os.fdopen(descriptor, "wb") as fh:
                fh.write(data)
                fh.flush()
                os.fsync(fh.fileno())
            os.replace(temp, self._path)
        finally:
            if temp.exists():
                temp.unlink()
        note = permissions.ensure_private_file(self._path)
        if note:
            filelog.write(f"[UserOnlyFile] write: {note} after replace")
        # A save only counts when the store reads back as written: a file this user cannot open is a lost store.
        try:
            written = self._path.read_bytes()
        except OSError as exc:
            raise permissions.StorePermissionError(
                f"The store was saved to {self._path} but cannot be read back ({type(exc).__name__}). "
                "Check that folder's permissions.") from exc
        if written != data:
            raise permissions.StorePermissionError(f"The store saved to {self._path} does not read back as written.")
        filelog.write(f"[UserOnlyFile] write: {len(data)} bytes to {self._path}, read back")
