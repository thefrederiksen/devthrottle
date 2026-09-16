"""The store's only protection at rest is its permissions, so each check here is first shown to FAIL on a
loosened folder or file before it is trusted to pass on a private one."""

import os
import stat
import subprocess
import sys

import pytest

from conftest import add_entry, run_entry_point
from src import paths, permissions

WINDOWS = sys.platform == "win32"
BUILTIN_USERS_SID = "S-1-5-32-545"


def _icacls(*args):
    subprocess.run([os.path.join(os.environ["SystemRoot"], "System32", "icacls.exe"), *args],
                   check=True, capture_output=True)


def _permissions_snapshot(path):
    if WINDOWS:
        return permissions.windows_access_list(path)
    info = os.stat(path)
    return info.st_uid, stat.S_IMODE(info.st_mode)


def test_Put_LeavesFolderAndFilePrivate(store):
    add_entry(store)

    assert permissions.folder_problem(paths.secrets_home()) is None
    assert permissions.file_problem(paths.store_path()) is None


def test_Put_LeavesNoTempFileBehind(store):
    add_entry(store)
    add_entry(store)

    assert sorted(p.name for p in paths.secrets_home().iterdir() if p.is_file()) == [".cc-secrets-folder", "secrets.json"]


@pytest.mark.needs_store
def test_ExistingFolderThatIsNotPrivate_IsRefused_AndLeftExactlyAsItWas(tmp_path, monkeypatch):
    folder = tmp_path / "someone-elses-folder"
    folder.mkdir()
    (folder / "their-file.txt").write_text("theirs", encoding="utf-8")
    before = _permissions_snapshot(folder)
    assert permissions.folder_problem(folder) is not None
    monkeypatch.setenv("CC_SECRETS_HOME", str(folder))
    monkeypatch.setattr(paths, "_checked_home", None)

    with pytest.raises(permissions.StorePermissionError, match="created itself"):
        paths.ensure_home()

    assert _permissions_snapshot(folder) == before
    assert sorted(p.name for p in folder.iterdir()) == ["their-file.txt"]


@pytest.mark.needs_store
def test_Command_PointedAtAnExistingFolder_RefusesWithAClearMessage(tmp_path):
    folder = tmp_path / "someone-elses-folder"
    folder.mkdir()
    before = _permissions_snapshot(folder)

    result = run_entry_point(["run", "anything", "--", sys.executable, "-c", "print(1)"],
                             {"CC_SECRETS_HOME": str(folder), "CC_SESSION_ID": "permissions-test"})

    assert result.returncode != 0
    assert b"only changes the permissions of a folder it created itself" in result.stderr
    assert _permissions_snapshot(folder) == before
    assert list(folder.iterdir()) == []


@pytest.mark.needs_store
def test_ExistingFolderThatIsAlreadyPrivate_IsAdopted(tmp_path, monkeypatch):
    folder = tmp_path / "already-private"
    folder.mkdir()
    if WINDOWS:
        user = permissions.current_user_sid()
        _icacls(str(folder), "/inheritance:r", "/grant:r", f"*{user}:(OI)(CI)F")
        # /inheritance:r drops inherited entries only. A folder can also carry explicit ones - the Windows
        # continuous integration runner's temp folder grants SYSTEM explicitly - so remove every other identity.
        for sid in {sid for _, sid in permissions.windows_access_list(folder)[1] if sid and sid != user}:
            _icacls(str(folder), "/remove", f"*{sid}")
    else:
        os.chmod(folder, 0o700)
    assert permissions.folder_problem(folder) is None, "test setup did not make the folder private"
    before = _permissions_snapshot(folder)
    monkeypatch.setenv("CC_SECRETS_HOME", str(folder))
    monkeypatch.setattr(paths, "_checked_home", None)

    paths.ensure_home()

    assert (folder / permissions.FOLDER_MARKER).exists()
    assert _permissions_snapshot(folder) == before


def _remove_every_direct_grant(path):
    # /inheritance:r drops inherited entries only, and /grant:r does not replace a direct grant whose inheritance
    # flags differ - it adds a second one beside it. On the Windows continuous integration runner a new file or
    # folder already carries direct grants (SYSTEM, and this user with inheritance), so every identity, this user
    # included, is removed before the test grants what it means to.
    _icacls(str(path), "/inheritance:r")
    for sid in {sid for _, sid in permissions.windows_access_list(path)[1] if sid}:
        _icacls(str(path), "/remove", f"*{sid}")


def _folder_granted_to_this_user_only(tmp_path, name, grant):
    folder = tmp_path / name
    folder.mkdir()
    user = permissions.current_user_sid()
    _remove_every_direct_grant(folder)
    _icacls(str(folder), "/grant", f"*{user}:{grant}")
    entries = permissions.windows_access_entries(folder)[1]
    assert len(entries) == 1 and entries[0][2] == user, f"test setup left other grants: {entries}"
    return folder


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_FolderWhoseGrantIsNotInheritedByNewFiles_IsNotPrivate(tmp_path):
    # The review's probe, steps 1-3: a "this folder only" grant (no (OI)(CI)).
    folder = _folder_granted_to_this_user_only(tmp_path, "private-no-inheritance", "F")

    assert "new files" in (permissions.folder_problem(folder) or "")


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_SaveIntoAFolderWithoutAnInheritableGrant_NeverSucceedsUnreadable(tmp_path, monkeypatch):
    # The review's probe, steps 5-6: the save used to succeed and leave a store nobody could read back.
    import json

    from src.storefile import UserOnlyFile

    folder = _folder_granted_to_this_user_only(tmp_path, "private-no-inheritance", "F")
    monkeypatch.setenv("CC_SECRETS_HOME", str(folder))
    monkeypatch.setattr(paths, "_checked_home", None)
    data = json.dumps({"version": 1, "entries": []}).encode("utf-8")
    store_file = UserOnlyFile(folder / "secrets.json")

    try:
        store_file.write(data)
        saved = True
    except permissions.StorePermissionError:
        saved = False

    if saved:
        assert store_file.read() == data
    assert saved is False
    assert not (folder / "secrets.json").exists()


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_AdoptedFolderWithAnInheritableGrant_SavesAndReadsBack(tmp_path, monkeypatch):
    import json

    from src.storefile import UserOnlyFile

    folder = _folder_granted_to_this_user_only(tmp_path, "private-inheriting", "(OI)(CI)F")
    monkeypatch.setenv("CC_SECRETS_HOME", str(folder))
    monkeypatch.setattr(paths, "_checked_home", None)
    data = json.dumps({"version": 1, "entries": []}).encode("utf-8")
    store_file = UserOnlyFile(folder / "secrets.json")

    store_file.write(data)

    assert store_file.read() == data
    assert permissions.file_problem(folder / "secrets.json") is None


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_FileWithAnEmptyAccessList_IsNotPrivate(tmp_path):
    locked_out = tmp_path / "locked-out.txt"
    locked_out.write_text("x", encoding="utf-8")
    _remove_every_direct_grant(locked_out)
    try:
        assert permissions.windows_access_list(locked_out)[1] == [], "test setup left grants on the file"
        assert "empty access list" in (permissions.file_problem(locked_out) or "")
    finally:
        _icacls(str(locked_out), "/reset")


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_FolderGrantsOnlyTheCurrentUser_AndInheritsNothing(store):
    add_entry(store)

    protected, entries = permissions.windows_access_list(paths.secrets_home())
    _, file_entries = permissions.windows_access_list(paths.store_path())

    assert protected is True
    assert {sid for _, sid in entries} == {permissions.current_user_sid()}
    assert {sid for _, sid in file_entries} == {permissions.current_user_sid()}


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_LoosenedFolderItCreated_IsDetected_ThenTightened(store):
    add_entry(store)
    folder = paths.secrets_home()
    _icacls(str(folder), "/grant", f"*{BUILTIN_USERS_SID}:(OI)(CI)R")

    assert BUILTIN_USERS_SID in (permissions.folder_problem(folder) or "")
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


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_NewFileThatWouldNotBePrivate_IsRefusedBeforeAnythingIsWritten(tmp_path):
    # The review's probe, step 4: in a folder whose grant new files do not inherit, a new file gets the
    # process's default permissions. Creating it must be refused, and nothing left behind.
    folder = _folder_granted_to_this_user_only(tmp_path, "private-no-inheritance", "F")
    probe = folder / "probe.tmp"

    with pytest.raises(permissions.StorePermissionError, match="would not be private"):
        permissions.create_private_file(probe)

    assert not probe.exists()


@pytest.mark.skipif(not WINDOWS, reason="Windows access list check")
def test_Windows_SaveThatCannotBeReadBack_IsReportedAsFailed(home, monkeypatch):
    # Defence behind the folder check: whatever leaves the saved file unreadable, the save must not report success.
    import json

    from src.storefile import UserOnlyFile

    def lock_everyone_out(path):
        _icacls(str(path), "/inheritance:r")
        return None

    monkeypatch.setattr(permissions, "ensure_private_file", lock_everyone_out)
    data = json.dumps({"version": 1, "entries": []}).encode("utf-8")
    store_file = UserOnlyFile(paths.store_path())

    with pytest.raises(permissions.StorePermissionError, match="cannot be read back"):
        store_file.write(data)
    _icacls(str(paths.store_path()), "/reset")


@pytest.mark.skipif(WINDOWS, reason="POSIX mode check")
def test_Posix_LoosenedFile_IsDetected_ThenTightenedOnRead(store):
    add_entry(store)
    os.chmod(paths.store_path(), 0o644)
    assert permissions.file_problem(paths.store_path()) is not None

    store.entries()

    assert stat.S_IMODE(os.stat(paths.store_path()).st_mode) == 0o600


def test_MacOS_IsRefused_BeforeAnythingIsCreated(tmp_path, monkeypatch):
    # Review of pull request 2891: mode bits alone do not make a file private on macOS, where an access control
    # list can grant another account, and those lists are not checked yet.
    folder = tmp_path / "never-created"
    monkeypatch.setenv("CC_SECRETS_HOME", str(folder))
    monkeypatch.setattr(paths, "_checked_home", None)
    monkeypatch.setattr(sys, "platform", "darwin")

    with pytest.raises(permissions.StorePermissionError, match="does not run on macOS"):
        paths.ensure_home()

    monkeypatch.undo()
    assert not folder.exists()
