"""The store's only protection at rest is its permissions, so each check here is first shown to FAIL on a
loosened folder or file before it is trusted to pass on a private one."""

import os
import stat
import subprocess
import sys

import pytest

from conftest import add_entry
from src import paths, permissions

WINDOWS = sys.platform == "win32"
BUILTIN_USERS_SID = "S-1-5-32-545"


def _icacls(*args):
    subprocess.run([os.path.join(os.environ["SystemRoot"], "System32", "icacls.exe"), *args],
                   check=True, capture_output=True)


def test_Put_LeavesFolderAndFilePrivate(store):
    add_entry(store)

    assert permissions.folder_problem(paths.secrets_home()) is None
    assert permissions.file_problem(paths.store_path()) is None


def test_Put_LeavesNoTempFileBehind(store):
    add_entry(store)
    add_entry(store)

    assert sorted(p.name for p in paths.secrets_home().iterdir() if p.is_file()) == ["secrets.json"]


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_FolderGrantsOnlyTheCurrentUser_AndInheritsNothing(store):
    add_entry(store)

    protected, entries = permissions.windows_access_list(paths.secrets_home())
    _, file_entries = permissions.windows_access_list(paths.store_path())

    assert protected is True
    assert {sid for _, sid in entries} == {permissions.current_user_sid()}
    assert {sid for _, sid in file_entries} == {permissions.current_user_sid()}


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_LoosenedFolder_IsDetected_ThenTightened(store):
    add_entry(store)
    folder = paths.secrets_home()
    _icacls(str(folder), "/grant", f"*{BUILTIN_USERS_SID}:(OI)(CI)R")

    assert "S-1-5-32-545" in (permissions.folder_problem(folder) or "")
    note = permissions.ensure_private_folder(folder)

    assert note is not None
    assert permissions.folder_problem(folder) is None


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_InheritingFolder_IsDetected(tmp_path):
    folder = tmp_path / "inherits"
    folder.mkdir()

    assert "inherits permissions" in (permissions.folder_problem(folder) or "")


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_LoosenedStoreFile_IsTightenedOnRead(store):
    add_entry(store)
    _icacls(str(paths.store_path()), "/grant", f"*{BUILTIN_USERS_SID}:R")
    assert permissions.file_problem(paths.store_path()) is not None

    store.entries()

    assert permissions.file_problem(paths.store_path()) is None


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_TempFile_IsPrivateBeforeAnyByteIsWritten(home):
    folder = paths.ensure_home()
    temp = folder / "probe.tmp"

    descriptor = permissions.create_private_file(temp)
    try:
        assert permissions.file_problem(temp) is None
        assert {sid for _, sid in permissions.windows_access_list(temp)[1]} == {permissions.current_user_sid()}
    finally:
        os.close(descriptor)


@pytest.mark.skipif(WINDOWS, reason="POSIX mode check")
def test_Posix_FolderIs0700_AndFileIs0600(store):
    add_entry(store)

    assert stat.S_IMODE(os.stat(paths.secrets_home()).st_mode) == 0o700
    assert stat.S_IMODE(os.stat(paths.store_path()).st_mode) == 0o600


@pytest.mark.skipif(WINDOWS, reason="POSIX mode check")
def test_Posix_LoosenedFile_IsDetected_ThenTightenedOnRead(store):
    add_entry(store)
    os.chmod(paths.store_path(), 0o644)
    assert permissions.file_problem(paths.store_path()) is not None

    store.entries()

    assert stat.S_IMODE(os.stat(paths.store_path()).st_mode) == 0o600
